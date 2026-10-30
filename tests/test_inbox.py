"""Black-box acceptance tests. No model calls, credentials or personal database are used."""
import concurrent.futures
from contextlib import closing
import json
import os
from pathlib import Path
import select
import socket
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.parse
import urllib.request
import uuid
import sqlite3
from fake_codex import FakeCodex, THREAD_A, THREAD_B, WORKSPACE

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / '.local/runtime/AgentDesk.dll'


class InboxAcceptanceTests(unittest.TestCase):
    maxDiff = None

    def setUp(self):
        if not DLL.exists():
            raise RuntimeError('Run scripts/build.sh before the acceptance tests.')
        self.temp = tempfile.TemporaryDirectory(prefix='question-inbox-acceptance-')
        self.addCleanup(self.temp.cleanup)
        self.codex = FakeCodex()
        self.addCleanup(self.codex.close)
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            self.port = sock.getsockname()[1]
        self.base = f'http://127.0.0.1:{self.port}'
        self.env = dict(os.environ, QUESTION_INBOX_DB=str(Path(self.temp.name) / 'questions.db'),
                       QUESTION_INBOX_URL=self.base, QUESTION_INBOX_CONTENT_ROOT=str(DLL.parent),
                       ASPNETCORE_ENVIRONMENT='Development')
        self.env.update(self.codex.environment)
        self.log = open(Path(self.temp.name) / 'server.log', 'w+')
        self.addCleanup(self.log.close)
        self.start_server()

    def start_server(self):
        self.server = subprocess.Popen(['dotnet', str(DLL), '--urls', self.base], env=self.env,
                                      stdout=self.log, stderr=self.log)
        self.addCleanup(self.stop_process, self.server)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            try:
                with urllib.request.urlopen(self.base + '/health', timeout=.5) as response:
                    if json.load(response)['service'] == 'question-inbox':
                        return
            except (OSError, ValueError):
                if self.server.poll() is not None:
                    break
                time.sleep(.05)
        self.log.seek(0)
        raise RuntimeError(self.log.read())

    @staticmethod
    def stop_process(process):
        if process.poll() is not None:
            return
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)

    def stop_server(self):
        self.stop_process(self.server)

    def request(self, method, path, body=None, headers=None):
        request = urllib.request.Request(self.base + path, method=method,
            data=None if body is None else json.dumps(body).encode(),
            headers={'Content-Type': 'application/json', 'X-Question-Inbox': '1', **(headers or {})})
        try:
            with urllib.request.urlopen(request, timeout=6) as response:
                raw = response.read()
                return response.status, json.loads(raw) if raw else None
        except urllib.error.HTTPError as error:
            with error:
                raw = error.read()
                return error.code, json.loads(raw) if raw and 'json' in error.headers.get('Content-Type', '') else raw

    def codex_usage_tool(self, tool_name='read_codex_usage', arguments=None):
        process = subprocess.Popen(['dotnet', str(DLL), '--mcp-stdio'], env=self.env,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.log, text=True, bufsize=1)
        self.addCleanup(self.stop_process, process)

        def rpc(request_id, method, params):
            process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': request_id, 'method': method, 'params': params}) + '\n')
            process.stdin.flush()
            deadline = time.monotonic() + 8
            while time.monotonic() < deadline:
                if select.select([process.stdout], [], [], .2)[0]:
                    line = process.stdout.readline()
                    self.assertTrue(line, 'MCP server closed stdout')
                    response = json.loads(line)
                    if response.get('id') == request_id:
                        self.assertNotIn('error', response, response)
                        return response['result']
            self.fail('MCP usage request timed out: ' + method)

        try:
            rpc(1, 'initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                'clientInfo': {'name': 'usage-acceptance', 'version': '1.0'}})
            process.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}) + '\n')
            process.stdin.flush()
            return rpc(2, 'tools/call', {'name': tool_name, 'arguments': arguments or {}})
        finally:
            self.stop_process(process)
            process.stdin.close()
            process.stdout.close()

    def codex_tool(self, name, arguments=None):
        return self.codex_usage_tool(name, arguments)

    def create(self, **overrides):
        body = {'prompt': 'Welchen Entwurf soll ich verwenden?', 'choices': [{'label': 'A'}, {'label': 'B'}],
                'project': 'acceptance', 'conversationId': uuid.uuid4().hex, **overrides}
        status, question = self.request('POST', '/api/questions/', body)
        self.assertEqual(status, 201, question)
        return question

    def test_create_returns_pending_question_and_task_scope(self):
        q = self.create(context='Beide Entwürfe sind fertig.', blocking=True)
        self.assertEqual(q['state']['kind'], 'pending')
        self.assertEqual(q['version'], 1)
        self.assertNotIn('text', q['state'])
        self.assertTrue(q['blocking'])
        status, snapshot = self.request('GET', '/api/questions/?conversationId=' + q['conversationId'])
        self.assertEqual(status, 200)
        self.assertEqual([x['id'] for x in snapshot['questions']], [q['id']])
        self.assertGreater(snapshot['revision'], 0)

    def test_retry_is_idempotent_but_changed_payload_conflicts(self):
        key = uuid.uuid4().hex
        payload = {'prompt': 'Unverändert?', 'choices': [{'label': 'Ja'}], 'requestKey': key}
        status, first = self.request('POST', '/api/questions/', payload)
        self.assertEqual(status, 201, first)
        status, repeated = self.request('POST', '/api/questions/', payload)
        self.assertEqual(status, 201)
        self.assertEqual(first, repeated)
        status, _ = self.request('POST', '/api/questions/', {**payload, 'prompt': 'Anders?'})
        self.assertEqual(status, 409)

    def test_choice_answer_changes_state_and_cannot_be_overwritten(self):
        q = self.create(allowFreeText=False)
        status, answer = self.request('POST', f"/api/questions/{q['id']}/answer", {'decision': {'kind': 'answer', 'text': 'B'}, 'expectedVersion': 1})
        self.assertEqual(status, 200)
        self.assertEqual((answer['state']['kind'], answer['state']['text'], answer['version']), ('answered', 'B', 2))
        self.assertIsNotNone(answer['state']['at'])
        for version in (1, 2):
            status, _ = self.request('POST', f"/api/questions/{q['id']}/answer", {'decision': {'kind': 'answer', 'text': 'A'}, 'expectedVersion': version})
            self.assertEqual(status, 409)
        self.assertEqual(self.request('GET', f"/api/questions/{q['id']}")[1]['state']['text'], 'B')

    def test_free_text_is_saved_and_invalid_choice_is_rejected(self):
        q = self.create(allowFreeText=False)
        status, _ = self.request('POST', f"/api/questions/{q['id']}/answer", {'decision': {'kind': 'answer', 'text': 'Unbekannt'}, 'expectedVersion': 1})
        self.assertEqual(status, 400)
        self.assertEqual(self.request('GET', f"/api/questions/{q['id']}")[1]['state']['kind'], 'pending')
        free = self.create(choices=[])
        status, saved = self.request('POST', f"/api/questions/{free['id']}/answer", {'decision': {'kind': 'answer', 'text': '  Eine eigene Idee.  '}, 'expectedVersion': 1})
        self.assertEqual(status, 200)
        self.assertEqual(saved['state']['text'], 'Eine eigene Idee.')

    def test_invalid_questions_and_empty_answers_are_rejected(self):
        for changes in ({'prompt': ''}, {'choices': [{'label': 'A'}, {'label': 'A'}]}, {'choices': [], 'allowFreeText': False},
                        {'choices': [None]}, {'project': ''}, {'requestKey': ' '}):
            status, _ = self.request('POST', '/api/questions/', {'prompt': 'Frage?', **changes})
            self.assertEqual(status, 400, changes)
        q = self.create()
        status, _ = self.request('POST', f"/api/questions/{q['id']}/answer", {'decision': {'kind': 'answer', 'text': '  '}, 'expectedVersion': 1})
        self.assertEqual(status, 400)

    def test_dismissal_is_distinct_from_answer_and_preserves_history(self):
        q = self.create()
        status, saved = self.request('POST', f"/api/questions/{q['id']}/answer", {'expectedVersion': 1, 'decision': {'kind': 'dismiss'}})
        self.assertEqual(status, 200)
        self.assertEqual(saved['state']['kind'], 'dismissed')
        self.assertNotIn('text', saved['state'])
        status, snapshot = self.request('GET', '/api/questions/?status=dismissed&conversationId=' + q['conversationId'])
        self.assertEqual(status, 200)
        self.assertEqual([x['id'] for x in snapshot['questions']], [q['id']])

    def test_two_simultaneous_answers_have_one_winner(self):
        q = self.create()

        def answer(value):
            status, body = self.request('POST', f"/api/questions/{q['id']}/answer",
                                        {'decision': {'kind': 'answer', 'text': value}, 'expectedVersion': 1})
            return value, status, body

        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(answer, ['A', 'B']))
        self.assertEqual(sorted(status for _, status, _ in results), [200, 409])
        winner, _, response = next(result for result in results if result[1] == 200)
        self.assertEqual((response['state']['kind'], response['state']['text'], response['version']),
                         ('answered', winner, 2))
        status, saved = self.request('GET', f"/api/questions/{q['id']}")
        self.assertEqual(status, 200, saved)
        self.assertEqual(saved, response)

    def test_sse_sends_initial_snapshot_and_durable_update(self):
        with urllib.request.urlopen(self.base + '/api/questions/stream', timeout=6) as stream:
            def next_snapshot():
                while True:
                    line = stream.readline().decode()
                    if line.startswith('data:'):
                        return json.loads(line[5:])
                    if not line:
                        self.fail('SSE ended before a snapshot')
            initial = next_snapshot()
            q = self.create()
            updated = next_snapshot()
            self.assertGreater(updated['revision'], initial['revision'])
            self.assertIn(q['id'], [item['id'] for item in updated['questions']])

    def test_missing_question_and_unknown_status_return_problem_details(self):
        status, problem = self.request('GET', '/api/questions/missing')
        self.assertEqual((status, problem['status']), (404, 404))
        self.assertEqual(self.request('GET', '/api/questions/?status=unknown')[0], 400)

    def test_cross_origin_and_missing_mutation_header_are_rejected(self):
        status, _ = self.request('POST', '/api/questions/', {'prompt': 'Rejected?'}, {'Origin': 'https://other.example'})
        self.assertEqual(status, 403)
        status, _ = self.request('POST', '/api/questions/', {'prompt': 'Rejected?'}, {'X-Question-Inbox': ''})
        self.assertEqual(status, 403)

    def test_questions_and_answers_survive_server_restart(self):
        pending = self.create()
        answered = self.create()
        self.request('POST', f"/api/questions/{answered['id']}/answer", {'decision': {'kind': 'answer', 'text': 'A'}, 'expectedVersion': 1})
        self.stop_server()
        self.start_server()
        self.assertEqual(self.request('GET', f"/api/questions/{pending['id']}")[1]['state']['kind'], 'pending')
        saved = self.request('GET', f"/api/questions/{answered['id']}")[1]
        self.assertEqual((saved['state']['kind'], saved['state']['text'], saved['version']), ('answered', 'A', 2))

    def test_mcp_roundtrip_returns_immediately_and_reads_later_human_answer(self):
        process = subprocess.Popen(['dotnet', str(DLL), '--mcp-stdio'], env=self.env,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.log, text=True, bufsize=1)
        next_id = 0
        def rpc(method, params):
            nonlocal next_id
            next_id += 1
            process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': next_id, 'method': method, 'params': params}) + '\n')
            process.stdin.flush()
            deadline = time.monotonic() + 8
            while time.monotonic() < deadline:
                if select.select([process.stdout], [], [], .2)[0]:
                    line = process.stdout.readline()
                    self.assertTrue(line, 'MCP server closed stdout')
                    response = json.loads(line)
                    if response.get('id') == next_id:
                        self.assertNotIn('error', response, response)
                        return response['result']
            self.fail('MCP request timed out: ' + method)
        try:
            rpc('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'acceptance', 'version': '1.0'}})
            process.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}) + '\n')
            process.stdin.flush()
            tools = {t['name']: t for t in rpc('tools/list', {})['tools']}
            self.assertIn('ask_question', tools)
            self.assertEqual(tools['open_question_inbox']['_meta']['ui']['resourceUri'], 'ui://question-inbox/view.html')
            result = rpc('tools/call', {'name': 'ask_question', 'arguments': {'prompt': 'Bleibt diese Frage offen?', 'project': 'mcp-test'}})
            self.assertFalse(result.get('isError', False))
            q = result['structuredContent']
            self.assertEqual(q['state']['kind'], 'pending')
            process.stdin.close()
            process.wait(timeout=8)
            # Ending the entire MCP process must not remove the question.
            status, q = self.request('GET', f"/api/questions/{q['id']}")
            self.assertEqual((status, q['state']['kind']), (200, 'pending'))
            status, saved = self.request('POST', f"/api/questions/{q['id']}/answer", {'decision': {'kind': 'answer', 'text': 'Ja, später beantwortet.'}, 'expectedVersion': 1})
            self.assertEqual((status, saved['state']['text']), (200, 'Ja, später beantwortet.'))
            process.stdout.close()
            process = subprocess.Popen(['dotnet', str(DLL), '--mcp-stdio'], env=self.env,
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.log, text=True, bufsize=1)
            rpc('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'acceptance-resumed', 'version': '1.0'}})
            process.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}) + '\n')
            process.stdin.flush()
            result = rpc('tools/call', {'name': 'get_question', 'arguments': {'id': q['id']}})
            self.assertEqual(result['structuredContent']['state']['text'], 'Ja, später beantwortet.')
            resource = rpc('resources/read', {'uri': 'ui://question-inbox/view.html'})['contents'][0]
            self.assertEqual(resource['mimeType'], 'text/html;profile=mcp-app')
            self.assertIn('window.__MCP_INBOX__ = true', resource['text'])
            self.assertNotIn('src="/inbox.js"', resource['text'])
            self.assertNotIn('href="/inbox.css"', resource['text'])
            self.assertEqual(resource['_meta']['ui']['csp']['connectDomains'], [])
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=8)
            process.stdout.close()
            if not process.stdin.closed:
                process.stdin.close()

    def test_openapi_and_http_mcp_are_available(self):
        status, document = self.request('GET', '/openapi/v1.json')
        self.assertEqual(status, 200)
        self.assertIn('/api/questions/{id}/answer', document['paths'])
        request = urllib.request.Request(self.base + '/mcp', method='POST',
            headers={'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'},
            data=json.dumps({'jsonrpc': '2.0', 'id': 1, 'method': 'initialize',
                'params': {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'http-test', 'version': '1'}}}).encode())
        with urllib.request.urlopen(request, timeout=6) as response:
            self.assertEqual(response.status, 200)
            self.assertIn(b'question-inbox', response.read())

    def test_token_activity_reads_account_wide_counters_exactly_through_http_and_mcp(self):
        native = self.codex.response('account/usage/read', {})
        native['summary']['lifetimeTokens'] = 9223372036854775807
        native['dailyUsageBuckets'][0]['tokens'] = 9007199254740993
        native['threadUsage'] = {'ignored': 'account reads omit threadId'}
        expected = {'summary': {key: str(value) for key, value in native['summary'].items()},
            'dailyUsageBuckets': [{**day, 'tokens': str(day['tokens'])} for day in native['dailyUsageBuckets']]}
        self.codex.token_activity_replies = [{'result': native}, {'result': native}]
        self.assertEqual(self.request('GET', '/api/codex/token-activity'), (200, expected))
        result = self.codex_usage_tool('read_codex_token_activity')
        self.assertFalse(result.get('isError', False), result)
        self.assertEqual(result['structuredContent'], expected)
        self.assertEqual(json.loads(result['content'][0]['text']), expected)
        self.assertEqual([call['params'] for call in self.codex.token_activity_requests], [{}, {}])
        self.assertEqual(self.codex.usage_requests, [])
        self.assertEqual(self.codex.queued(), [])

    def test_token_activity_preserves_unavailable_and_empty_daily_measurements(self):
        for case in ('unavailable', 'empty', 'omitted'):
            with self.subTest(case=case):
                expected = {'summary': dict.fromkeys(('lifetimeTokens', 'peakDailyTokens',
                    'longestRunningTurnSec', 'currentStreakDays', 'longestStreakDays')),
                    'dailyUsageBuckets': [] if case == 'empty' else None}
                native = {'summary': {}} if case == 'omitted' else expected
                self.codex.token_activity_replies = [{'result': native}, {'result': native}]
                self.assertEqual(self.request('GET', '/api/codex/token-activity'), (200, expected))
                result = self.codex_usage_tool('read_codex_token_activity')
                self.assertFalse(result.get('isError', False), result)
                self.assertEqual(result['structuredContent'], expected)
        self.assertEqual(self.codex.queued(), [])

    def test_token_activity_error_retries_read_without_allowance_or_agent_calls(self):
        for transport in ('http', 'mcp'):
            with self.subTest(transport=transport):
                self.codex.token_activity_replies = [{'error': {'code': -32601,
                    'message': 'Token activity is unavailable in this server'}}]
                start = len(self.codex.token_activity_requests)
                if transport == 'http':
                    status, problem = self.request('GET', '/api/codex/token-activity')
                    self.assertEqual((status, problem['status'], problem['title']),
                        (503, 503, 'Token activity is unavailable in this server'))
                    self.assertEqual(self.request('GET', '/api/codex/token-activity')[0], 200)
                else:
                    result = self.codex_usage_tool('read_codex_token_activity')
                    self.assertTrue(result['isError'], result)
                    self.assertEqual(result['content'][0]['text'], 'Token activity is unavailable in this server')
                    self.assertIsNone(result.get('structuredContent'))
                    retried = self.codex_usage_tool('read_codex_token_activity')
                    self.assertFalse(retried.get('isError', False), retried)
                    self.assertEqual(retried['structuredContent']['summary']['lifetimeTokens'], '123456')
                calls = self.codex.token_activity_requests[start:]
                self.assertEqual(len(calls), 2)
                self.assertNotEqual(calls[0]['connection'], calls[1]['connection'])
                self.assertEqual([call['params'] for call in calls], [{}, {}])
        self.assertEqual(self.codex.usage_requests, [])
        self.assertEqual(self.codex.queued(), [])

    def test_token_activity_rejects_malformed_native_measurements(self):
        valid = self.codex.response('account/usage/read', {})
        cases = (
            ('missing_summary', {'dailyUsageBuckets': []}),
            ('null_summary', {'summary': None, 'dailyUsageBuckets': None}),
            ('fractional_counter', {**valid, 'summary': {**valid['summary'], 'peakDailyTokens': 1.5}}),
            ('negative_counter', {**valid, 'summary': {**valid['summary'], 'lifetimeTokens': -1}}),
            ('overflow_counter', {**valid, 'summary': {**valid['summary'], 'lifetimeTokens': 9223372036854775808}}),
            ('null_day', {**valid, 'dailyUsageBuckets': [None]}),
            ('missing_day_count', {**valid, 'dailyUsageBuckets': [{'startDate': '2026-10-28'}]}),
            ('null_day_count', {**valid, 'dailyUsageBuckets': [{'startDate': '2026-10-28', 'tokens': None}]}),
            ('negative_day_count', {**valid, 'dailyUsageBuckets': [{'startDate': '2026-10-28', 'tokens': -1}]}),
            ('blank_date', {**valid, 'dailyUsageBuckets': [{'startDate': ' ', 'tokens': 0}]}))
        for name, native in cases:
            with self.subTest(case=name):
                self.codex.token_activity_replies = [{'result': native}, {'result': native}]
                status, problem = self.request('GET', '/api/codex/token-activity')
                self.assertEqual((status, problem['status']), (503, 503), problem)
                result = self.codex_usage_tool('read_codex_token_activity')
                self.assertTrue(result['isError'], result)
                self.assertEqual(result['content'][0]['text'], problem['title'])
                self.assertIsNone(result.get('structuredContent'))
        self.assertEqual(self.codex.queued(), [])

    def test_codex_mcp_health_projects_runtime_auth_and_tool_errors(self):
        states = [None, 'notStarted', 'starting', 'connected', 'authenticationRequired', 'failed',
                  'cancelled', 'disabled', 'futureState']
        native = {'data': [{'name': f'server-{i}', 'authStatus': 'oAuth', 'runtimeStatus': state,
            'tools': {}, 'toolsError': 'Catalog denied' if state == 'failed' else None,
            'httpOrigin': 'https://tools.example', 'pluginId': 'tools@example',
            'resources': [], 'resourceTemplates': []} for i, state in enumerate(states)], 'nextCursor': None}
        self.codex.mcp_replies = [{'result': native}, {'result': native}]
        status, page = self.request('GET', '/api/codex/mcp-servers?threadId=' + THREAD_A)
        self.assertEqual(status, 200, page)
        self.assertEqual([s['runtimeStatus'] for s in page['servers']], states)
        self.assertEqual([s['authStatus'] for s in page['servers']], ['oAuth'] * len(states))
        self.assertEqual([s['toolCount'] for s in page['servers']], [None if s == 'failed' else 0 for s in states])
        self.assertEqual(page['servers'][5]['toolsError'], 'Catalog denied')
        self.assertEqual(page['servers'][0]['httpOrigin'], 'https://tools.example')
        self.assertEqual(page['servers'][0]['pluginId'], 'tools@example')
        result = self.codex_tool('list_codex_mcp_servers', {'threadId': THREAD_A})
        self.assertFalse(result.get('isError', False), result)
        self.assertEqual(result['structuredContent'], page)
        self.assertEqual(self.codex.queued(), [])

    def test_codex_mcp_health_preserves_scope_pagination_and_optional_status(self):
        opaque = 'next &=?/ü'
        native = {'data': [{'name': 'first', 'authStatus': 'futureAuth', 'tools': {'read': {}},
            'resources': [], 'resourceTemplates': []}], 'nextCursor': opaque}
        self.codex.mcp_replies = [{'result': native}, {'result': {'data': [], 'nextCursor': None}}]
        status, first = self.request('GET', '/api/codex/mcp-servers')
        self.assertEqual(status, 200, first)
        self.assertEqual(first['nextCursor'], opaque)
        self.assertIsNone(first['servers'][0]['runtimeStatus'])
        self.assertEqual(first['servers'][0]['authStatus'], 'futureAuth')
        self.assertEqual(first['servers'][0]['toolCount'], 1)
        result = self.codex_tool('list_codex_mcp_servers', {'threadId': THREAD_B, 'serverName': 'name &/?ü', 'cursor': opaque})
        self.assertFalse(result.get('isError', False), result)
        self.assertEqual(result['structuredContent'], {'servers': [], 'nextCursor': None})
        self.assertEqual([r['params'] for r in self.codex.mcp_requests], [
            {'threadId': None, 'serverName': None, 'cursor': None, 'limit': 50, 'detail': 'toolsAndAuthOnly'},
            {'threadId': THREAD_B, 'serverName': 'name &/?ü', 'cursor': opaque, 'limit': 50, 'detail': 'toolsAndAuthOnly'}])
        self.assertNotEqual(self.codex.mcp_requests[0]['connection'], self.codex.mcp_requests[1]['connection'])

    def test_codex_mcp_health_only_method_not_found_means_unsupported(self):
        for code, expected in [(-32601, 501), (-32602, 503), (-32000, 503)]:
            with self.subTest(code=code):
                self.codex.mcp_replies = [{'error': {'code': code, 'message': 'Native request rejected'}},
                                         {'error': {'code': code, 'message': 'Native request rejected'}}]
                status, problem = self.request('GET', '/api/codex/mcp-servers')
                self.assertEqual(status, expected, problem)
                self.assertEqual(problem['status'], expected)
                result = self.codex_tool('list_codex_mcp_servers')
                self.assertTrue(result['isError'], result)
                message = result['content'][0]['text']
                self.assertIn('nicht bereit' if code == -32601 else 'Native request rejected', message)
        self.assertEqual(self.request('GET', '/api/codex/mcp-servers')[0], 200)

    def test_codex_mcp_health_rejects_invalid_thread_before_native_request(self):
        status, problem = self.request('GET', '/api/codex/mcp-servers?threadId=not-a-thread')
        self.assertEqual((status, problem['status']), (400, 400))
        self.assertEqual(self.codex.mcp_requests, [])

    def test_codex_mcp_health_rejects_malformed_or_missing_required_data(self):
        for data in ({}, {'data': None}, {'data': [None]}, {'data': [{}]},
                     {'data': [{'name': 'x', 'authStatus': 'unknown', 'tools': None}]},
                     {'data': [{'name': '', 'authStatus': 'unknown', 'tools': {}}]},
                     {'data': [{'name': 'x', 'authStatus': None, 'tools': {}}]},
                     {'data': 'not-an-array'}):
            with self.subTest(data=data):
                self.codex.mcp_replies = [{'result': data}, {'result': data}]
                status, problem = self.request('GET', '/api/codex/mcp-servers')
                self.assertEqual((status, problem['status']), (503, 503))
                result = self.codex_tool('list_codex_mcp_servers')
                self.assertTrue(result['isError'], result)
                self.assertIn('lesbar', result['content'][0]['text'])

    def test_codex_usage_reads_current_snapshot_from_native_server(self):
        status, usage = self.request('GET', '/api/codex/usage')
        self.assertEqual(status, 200, usage)
        self.assertTrue(usage['ordinaryUsageAllowed'])
        self.assertEqual(usage['rateLimits']['primary']['usedPercent'], 25)
        self.assertEqual(usage['rateLimits']['primary']['resetsAt'], 1793779482)
        self.assertIsNone(usage['rateLimitsByLimitId'])
        self.assertIsNone(usage['rateLimits']['secondary'])
        self.assertEqual(usage['rateLimits']['credits']['balance'], '12.5')

    def test_codex_hooks_preserve_handlers_trust_and_diagnostics_through_http_and_mcp(self):
        workspace = '/projects/demo space & ä'
        expected = self.codex.response('hooks/list', {'cwds': [workspace]})
        hook = expected['data'][0]['hooks'][0]
        variants = [hook,
            {**hook, 'key': 'mcp', 'handlerType': 'mcpTool', 'command': None, 'async': None,
             'server': 'policy', 'tool': 'review', 'trustStatus': 'managed', 'isManaged': True, 'pluginId': 'policy@company'},
            {**hook, 'key': 'prompt', 'handlerType': 'prompt', 'command': None, 'async': None,
             'trustStatus': 'trusted', 'enabled': False, 'matcher': None, 'additionalContextLimit': 0},
            {**hook, 'key': 'agent', 'handlerType': 'agent', 'command': None, 'async': None, 'trustStatus': 'modified'},
            {**hook, 'key': 'future', 'handlerType': 'futureHandler', 'eventName': 'futureEvent',
             'command': None, 'async': None, 'source': 'futureSource', 'trustStatus': 'futureTrust'}]
        expected['data'][0].update(hooks=variants, warnings=['Duplicate matcher'],
            errors=[{'path': '/project/broken-hooks.json', 'message': 'Invalid hook'}])
        self.codex.hooks_replies = [{'result': expected}, {'result': expected}]
        self.assertEqual(self.request('GET', '/api/codex/hooks?workspacePath=' + urllib.parse.quote(workspace)), (200, expected))
        result = self.codex_usage_tool('list_codex_hooks', {'workspacePath': workspace})
        self.assertFalse(result.get('isError', False), result)
        self.assertEqual(result['structuredContent'], expected)
        self.assertEqual(json.loads(result['content'][0]['text']), expected)
        self.assertEqual([request['params'] for request in self.codex.hooks_requests], [{'cwds': [workspace]}] * 2)
        self.assertEqual(set(self.codex.native_requests), {'initialize', 'hooks/list'})
        self.assertEqual(self.codex.queued(), [])

    def test_codex_hooks_default_cwd_and_empty_snapshots_are_explicit(self):
        expected = self.codex.response('hooks/list', {})
        self.assertEqual(self.request('GET', '/api/codex/hooks'), (200, expected))
        self.assertEqual(self.codex.hooks_requests[-1]['params'], {'cwds': []})
        for empty in ({'data': []}, {'data': [{'cwd': WORKSPACE, 'hooks': [], 'warnings': [], 'errors': []}]}):
            with self.subTest(snapshot=empty):
                self.codex.hooks_replies = [{'result': empty}, {'result': empty}]
                self.assertEqual(self.request('GET', '/api/codex/hooks'), (200, empty))
                self.assertEqual(self.codex_usage_tool('list_codex_hooks')['structuredContent'], empty)

    def test_codex_hooks_reject_relative_paths_without_native_calls(self):
        for invalid in ('relative/project', '   '):
            with self.subTest(path=invalid):
                status, problem = self.request('GET', '/api/codex/hooks?workspacePath=' + urllib.parse.quote(invalid))
                self.assertEqual(status, 400, problem)
                result = self.codex_usage_tool('list_codex_hooks', {'workspacePath': invalid})
                self.assertTrue(result['isError'], result)
                self.assertIn('absoluter Projektpfad', result['content'][0]['text'])
        self.assertEqual(self.codex.hooks_requests, [])

    def test_codex_hooks_native_error_is_not_an_empty_inventory_and_retry_reconnects(self):
        self.codex.hooks_replies = [{'error': {'code': -32601, 'message': 'hooks/list unavailable'}}]
        status, problem = self.request('GET', '/api/codex/hooks')
        self.assertEqual((status, problem['title']), (503, 'hooks/list unavailable'))
        self.assertNotIn('data', problem)
        self.assertEqual(self.request('GET', '/api/codex/hooks')[0], 200)
        self.assertNotEqual(self.codex.hooks_requests[0]['connection'], self.codex.hooks_requests[1]['connection'])
        self.codex.hooks_replies = [{'error': {'code': -32601, 'message': 'hooks/list unavailable'}}]
        result = self.codex_usage_tool('list_codex_hooks')
        self.assertTrue(result['isError'], result)
        self.assertEqual(result['content'][0]['text'], 'hooks/list unavailable')
        self.assertIsNone(result.get('structuredContent'))

    def test_codex_hooks_malformed_or_missing_trust_is_unavailable(self):
        cases = [{'result': {}}, {'result': {'data': None}}, {'result': {'data': [None]}},
            {'result': {'data': [{'cwd': WORKSPACE, 'hooks': [], 'warnings': None, 'errors': []}]}}]
        for field in ('trustStatus', 'enabled', 'isManaged', 'handlerType', 'currentHash'):
            snapshot = self.codex.response('hooks/list', {})
            del snapshot['data'][0]['hooks'][0][field]
            cases.append({'result': snapshot})
        snapshot = self.codex.response('hooks/list', {})
        snapshot['data'][0]['hooks'][0]['trustStatus'] = None
        cases.append({'result': snapshot})
        for reply in cases:
            with self.subTest(reply=reply):
                self.codex.hooks_replies = [reply, reply]
                status, problem = self.request('GET', '/api/codex/hooks')
                self.assertEqual(status, 503, problem)
                self.assertIn('lesbaren', problem['title'])
                result = self.codex_usage_tool('list_codex_hooks')
                self.assertTrue(result['isError'], result)
                self.assertIsNone(result.get('structuredContent'))
        self.assertEqual(self.codex.queued(), [])

    def test_codex_usage_preserves_multiple_buckets_through_http_and_mcp(self):
        main = {'limitId': 'codex', 'limitName': 'General',
            'primary': {'usedPercent': 100, 'windowDurationMins': 300, 'resetsAt': 1793779482},
            'secondary': None, 'credits': None}
        premium = {'limitId': 'codex_premium', 'limitName': None, 'primary': None,
            'secondary': {'usedPercent': 8, 'windowDurationMins': None, 'resetsAt': None},
            'credits': {'hasCredits': False, 'unlimited': False, 'balance': None}}
        expected = {'ordinaryUsageAllowed': False, 'rateLimits': main,
            'rateLimitsByLimitId': {'codex': main, 'codex_premium': premium}}
        self.codex.usage_replies = [{'result': expected}, {'result': expected}]

        self.assertEqual(self.request('GET', '/api/codex/usage'), (200, expected))
        result = self.codex_usage_tool()
        self.assertFalse(result.get('isError', False), result)
        self.assertEqual(result['structuredContent'], expected)
        self.assertEqual(json.loads(result['content'][0]['text']), expected)
        self.assertEqual([request['params'] for request in self.codex.usage_requests], [{}, {}])
        self.assertEqual(self.codex.queued(), [])

    def test_codex_usage_preserves_unavailable_values_through_http_and_mcp(self):
        native = {'ordinaryUsageAllowed': None, 'rateLimits': {}, 'rateLimitsByLimitId': None}
        expected = {'ordinaryUsageAllowed': None,
            'rateLimits': {'limitId': None, 'limitName': None, 'primary': None, 'secondary': None, 'credits': None},
            'rateLimitsByLimitId': None}
        self.codex.usage_replies = [{'result': native}, {'result': native}]

        self.assertEqual(self.request('GET', '/api/codex/usage'), (200, expected))
        result = self.codex_usage_tool()
        self.assertFalse(result.get('isError', False), result)
        self.assertEqual(result['structuredContent'], expected)
        self.assertEqual(self.codex.queued(), [])

    def test_codex_usage_native_error_is_unavailable_and_retry_uses_new_connection(self):
        expected = self.codex.response('account/rateLimits/read', {})
        for transport in ('http', 'mcp'):
            with self.subTest(transport=transport):
                self.codex.usage_replies = [
                    {'error': {'code': -32000, 'message': 'Usage temporarily unavailable'}},
                    {'result': expected}]
                start = len(self.codex.usage_requests)
                if transport == 'http':
                    status, problem = self.request('GET', '/api/codex/usage')
                    self.assertEqual((status, problem['status'], problem['title']),
                        (503, 503, 'Usage temporarily unavailable'))
                    self.assertEqual(self.request('GET', '/api/codex/usage'), (200, expected))
                else:
                    error = self.codex_usage_tool()
                    self.assertTrue(error['isError'], error)
                    self.assertEqual(error['content'][0]['text'], 'Usage temporarily unavailable')
                    self.assertIsNone(error.get('structuredContent'))
                    result = self.codex_usage_tool()
                    self.assertFalse(result.get('isError', False), result)
                    self.assertEqual(result['structuredContent'], expected)
                calls = self.codex.usage_requests[start:]
                self.assertEqual(len(calls), 2)
                self.assertNotEqual(calls[0]['connection'], calls[1]['connection'])
                self.assertEqual([call['params'] for call in calls], [{}, {}])
        self.assertEqual(self.codex.queued(), [])

    def test_codex_usage_rejects_missing_required_fields_through_http_and_mcp(self):
        for container, field in (('primary', 'usedPercent'), ('credits', 'hasCredits'), ('credits', 'unlimited')):
            with self.subTest(case=field):
                native = self.codex.response('account/rateLimits/read', {})
                del native['rateLimits'][container][field]
                self.codex.usage_replies = [{'result': native}, {'result': native}]
                status, problem = self.request('GET', '/api/codex/usage')
                self.assertEqual(status, 503, problem)
                self.assertEqual((problem['status'], problem['title']),
                    (503, 'Codex hat keine lesbaren Antwortdaten geliefert.'))
                result = self.codex_usage_tool()
                self.assertTrue(result['isError'], result)
                self.assertEqual(result['content'][0]['text'], problem['title'])
                self.assertIsNone(result.get('structuredContent'))
        self.assertEqual(self.codex.queued(), [])

    def test_codex_usage_malformed_payload_is_unavailable_through_http_and_mcp(self):
        cases = (
            ('invalid_json', b'not json'),
            ('non_object_envelope', b'[]'),
            ('missing_result', {}),
            ('wrong_value_type', {'result': {'rateLimits': {'primary': {'usedPercent': 'invalid'}}}}))
        for case, reply in cases:
            with self.subTest(case=case):
                self.codex.usage_replies = [reply, reply]
                status, problem = self.request('GET', '/api/codex/usage')
                self.assertEqual(status, 503, problem)
                self.assertEqual((problem['status'], problem['title']),
                    (503, 'Codex hat keine lesbaren Antwortdaten geliefert.'))
                result = self.codex_usage_tool()
                self.assertTrue(result['isError'], result)
                self.assertEqual(result['content'][0]['text'], problem['title'])
                self.assertIsNone(result.get('structuredContent'))
        self.assertEqual(self.codex.queued(), [])

    def test_codex_list_and_detail_preserve_identity_without_full_history(self):
        status, page = self.request('GET', '/api/codex/threads')
        self.assertEqual(status, 200, page)
        self.assertEqual([t['id'] for t in page['threads']], [THREAD_A, THREAD_B])
        self.assertEqual([t['status'] for t in page['threads']], ['idle', 'active'])
        self.assertEqual(page['threads'][0]['workspacePath'], WORKSPACE)
        status, detail = self.request('GET', '/api/codex/threads/' + THREAD_A)
        self.assertEqual(status, 200, detail)
        self.assertEqual(detail['thread']['sessionId'], THREAD_A)
        self.assertEqual(detail['thread']['transcriptPath'], '/sessions/' + THREAD_A + '.jsonl')
        self.assertEqual(detail['latestReply'], 'A bounded summary from the original thread.')
        self.assertEqual(detail['goal'], 'Preserve the shared task state')

    def test_suggestions_and_origin_are_verified_and_persisted(self):
        q = self.create(originThreadId=THREAD_A, goal='Keep existing data.', choices=[
            {'label': 'Small change', 'impact': 'Lower effort, fewer features.', 'followUpPrompt': 'Implement the small change.'},
            {'label': 'Larger change', 'impact': 'More effort, broader functionality.'}])
        self.assertEqual([c['label'] for c in q['choices']], ['Small change', 'Larger change'])
        self.assertEqual(q['origin']['threadId'], THREAD_A)
        self.assertEqual(q['project'], WORKSPACE)
        self.assertEqual(q['choices'][0]['impact'], 'Lower effort, fewer features.')
        status, loaded = self.request('GET', '/api/questions/' + q['id'])
        self.assertEqual(status, 200)
        self.assertEqual(loaded['origin']['workspacePath'], WORKSPACE)
        self.assertEqual(loaded['goal'], 'Keep existing data.')

    def test_suggestions_reject_invalid_or_ambiguous_choices(self):
        for payload in [
            {'choices': [None]},
            {'choices': [{'label': ''}]},
            {'choices': [{'label': str(i), 'impact': 'estimate'} for i in range(4)]},
            {'choices': [{'label': 'A'}, {'label': 'A'}]},
            {'originThreadId': 'not-a-thread'}]:
            status, _ = self.request('POST', '/api/questions/', {'prompt': 'Choose?', **payload})
            self.assertEqual(status, 400, payload)

    def test_fanout_queues_each_target_once_and_records_partial_failure(self):
        key = uuid.uuid4().hex
        body = {'requestKey': key, 'threadIds': [THREAD_A, THREAD_B], 'content': {'kind': 'message', 'text': '[fail-second] ' + key}}
        status, batch = self.request('POST', '/api/codex/dispatch', body)
        self.assertEqual(status, 200, batch)
        self.assertEqual([d['state']['kind'] for d in batch['deliveries']], ['queued', 'failed'])
        self.assertIsNotNone(batch['deliveries'][0]['state']['messageId'])
        again_status, again = self.request('POST', '/api/codex/dispatch', body)
        self.assertEqual(again_status, 200)
        self.assertEqual(again, batch)
        self.assertEqual([d['threadId'] for d in batch['deliveries']], [THREAD_A, THREAD_B])
        self.assertCountEqual(self.codex.queued(), [
            {'thread': delivery['threadId'],
             'message': body['content']['text'] + f"\n\n[Postfach-Zustellung {delivery['id']}]"}
            for delivery in batch['deliveries']])
        self.assertEqual(self.request('POST', '/api/codex/dispatch', {**body, 'content': {'kind': 'message', 'text': 'Different'}})[0], 409)

    def test_answer_dispatch_includes_exact_answer_and_selected_followup(self):
        q = self.create(originThreadId=THREAD_A, choices=[
            {'label': 'A', 'impact': 'Small.', 'followUpPrompt': 'Only implement A.'},
            {'label': 'B', 'impact': 'Large.', 'followUpPrompt': 'Implement everything.'}])
        self.request('POST', '/api/questions/' + q['id'] + '/answer', {'decision': {'kind': 'answer', 'text': 'A'}, 'expectedVersion': 1})
        status, batch = self.request('POST', '/api/codex/dispatch', {
            'requestKey': uuid.uuid4().hex, 'threadIds': [THREAD_A], 'content': {'kind': 'answer', 'questionId': q['id']}})
        self.assertEqual(status, 200, batch)
        message = batch['deliveries'][0]['message']
        self.assertIn('Antwort: A', message)
        self.assertIn('Only implement A.', message)
        self.assertNotIn('Implement everything.', message)
        self.assertIn('Ursprungs-Thread: ' + THREAD_A, message)

    def test_unsafe_dispatch_inputs_and_pending_answers_are_rejected(self):
        for targets in [[], [THREAD_A, THREAD_A], ['Agent A'], [THREAD_A] * 9]:
            status, _ = self.request('POST', '/api/codex/dispatch', {'requestKey': uuid.uuid4().hex, 'threadIds': targets, 'content': {'kind': 'message', 'text': 'test'}})
            self.assertEqual(status, 400)
        q = self.create()
        self.assertEqual(self.request('POST', '/api/codex/dispatch', {
            'requestKey': uuid.uuid4().hex, 'threadIds': [THREAD_A], 'content': {'kind': 'answer', 'questionId': q['id']}})[0], 409)

    def test_unknown_receipt_stays_uncertain_and_is_not_retried(self):
        key = uuid.uuid4().hex
        body = {'requestKey': key, 'threadIds': [THREAD_A], 'content': {'kind': 'message', 'text': '[unreadable-receipt] ' + key}}
        status, batch = self.request('POST', '/api/codex/dispatch', body)
        self.assertEqual(status, 200)
        self.assertEqual(batch['deliveries'][0]['state']['kind'], 'uncertain')
        self.request('POST', '/api/codex/dispatch', body)
        self.assertEqual(len([m for m in self.codex.queued() if key in m['message']]), 1)

    def test_timeout_is_uncertain_and_does_not_block_later_dispatch(self):
        key = uuid.uuid4().hex
        body = {'requestKey': key, 'threadIds': [THREAD_A], 'content': {'kind': 'message', 'text': '[timeout] ' + key}}
        status, batch = self.request('POST', '/api/codex/dispatch', body)
        self.assertEqual(status, 200)
        self.assertEqual(batch['deliveries'][0]['state']['kind'], 'uncertain')
        later, delivered = self.request('POST', '/api/codex/dispatch', {
            'requestKey': uuid.uuid4().hex, 'threadIds': [THREAD_A], 'content': {'kind': 'message', 'text': 'A later normal message'}})
        self.assertEqual(later, 200)
        self.assertEqual(delivered['deliveries'][0]['state']['kind'], 'queued')
        self.request('POST', '/api/codex/dispatch', body)
        self.assertEqual(len([m for m in self.codex.queued() if key in m['message']]), 1)

    def test_native_queue_receives_literal_shell_characters(self):
        key = uuid.uuid4().hex
        marker = Path(self.temp.name) / 'must-not-exist'
        message = f'$(touch {marker}) `touch {marker}`; {key}'
        status, batch = self.request('POST', '/api/codex/dispatch', {'requestKey': key, 'threadIds': [THREAD_A], 'content': {'kind': 'message', 'text': message}})
        self.assertEqual(status, 200, batch)
        self.assertEqual(batch['deliveries'][0]['state']['kind'], 'queued')
        self.assertFalse(marker.exists())
        delivery = batch['deliveries'][0]
        self.assertEqual(delivery['message'], message)
        self.assertEqual(self.codex.queued(), [
            {'thread': THREAD_A, 'message': message + f"\n\n[Postfach-Zustellung {delivery['id']}]"}])

    def test_shared_tasks_require_correct_owner_and_concrete_completion(self):
        status, item = self.request('POST', '/api/work-items/', {
            'title': 'Inspect the result', 'workspacePath': WORKSPACE, 'owner': {'kind': 'agent', 'threadId': THREAD_A},
            'objective': 'Verify the persisted answer.'})
        self.assertEqual(status, 201, item)
        self.assertEqual(item['owner']['threadId'], THREAD_A)
        self.assertEqual(item['state']['kind'], 'todo')
        self.assertEqual(self.request('PUT', '/api/work-items/' + item['id'], {'expectedVersion': 1, 'state': {'kind': 'done', 'evidence': ''}})[0], 400)
        status, done = self.request('PUT', '/api/work-items/' + item['id'], {
            'expectedVersion': 1, 'state': {'kind': 'done', 'evidence': 'Verified the persisted value.'}})
        self.assertEqual(status, 200)
        self.assertEqual(done['state'], {'kind': 'done', 'evidence': 'Verified the persisted value.'})
        self.assertEqual(done['version'], 2)
        self.assertEqual(self.request('PUT', '/api/work-items/' + item['id'], {'expectedVersion': 1, 'state': {'kind': 'todo'}})[0], 409)
        status, stored = self.request('GET', '/api/work-items/')
        self.assertEqual(status, 200, stored)
        self.assertEqual(stored['items'], [done])
        invalid, _ = self.request('POST', '/api/work-items/', {'title': 'Task', 'workspacePath': WORKSPACE, 'owner': {'kind': 'agent'}})
        self.assertEqual(invalid, 400)

    def test_concurrent_dispatch_retry_has_only_one_native_send(self):
        key = uuid.uuid4().hex
        body = {'requestKey': key, 'threadIds': [THREAD_A], 'content': {'kind': 'message', 'text': 'Concurrent ' + key}}
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(lambda _: self.request('POST', '/api/codex/dispatch', body), range(2)))
        self.assertEqual([r[0] for r in results], [200, 200])
        self.assertEqual(results[0][1]['id'], results[1][1]['id'])
        self.assertEqual(len([m for m in self.codex.queued() if key in m['message']]), 1)

    def test_interrupted_dispatch_is_not_retried_after_restart(self):
        key = uuid.uuid4().hex
        body = {'requestKey': key, 'threadIds': [THREAD_A], 'content': {'kind': 'message', 'text': 'Crash boundary ' + key}}
        _, batch = self.request('POST', '/api/codex/dispatch', body)
        _, task = self.request('POST', '/api/work-items/', {'title': 'Persistent operator task', 'workspacePath': WORKSPACE, 'owner': {'kind': 'operator'}})
        self.stop_server()
        with closing(sqlite3.connect(self.env['QUESTION_INBOX_DB'])) as connection, connection:
            document = batch['deliveries'][0]
            document['state'] = {'kind': 'sending'}
            connection.execute('UPDATE dispatch_deliveries SET document=? WHERE id=?', (json.dumps(document), document['id']))
        self.start_server()
        status, repeated = self.request('POST', '/api/codex/dispatch', body)
        self.assertEqual(status, 200)
        self.assertEqual(repeated['deliveries'][0]['state']['kind'], 'uncertain')
        self.assertEqual(len([m for m in self.codex.queued() if key in m['message']]), 1)
        _, tasks = self.request('GET', '/api/work-items/')
        self.assertIn(task['id'], [t['id'] for t in tasks['items']])

    def test_answer_origin_cannot_be_reassigned(self):
        q = self.create(originThreadId=THREAD_A)
        self.request('POST', '/api/questions/' + q['id'] + '/answer', {'decision': {'kind': 'answer', 'text': 'A'}, 'expectedVersion': 1})
        status, _ = self.request('POST', '/api/codex/dispatch', {
            'requestKey': uuid.uuid4().hex, 'threadIds': [THREAD_A], 'content': {'kind': 'answer', 'questionId': q['id'], 'sourceThreadId': THREAD_B}})
        self.assertEqual(status, 400)

    def test_union_endpoints_reject_missing_unknown_and_mixed_cases(self):
        q = self.create()
        self.assertEqual(self.request('POST', '/api/questions/', {'prompt': 'stale client', 'options': ['legacy']})[0], 400)
        for decision in [{}, {'kind': 'unknown'}, {'kind': 'answer', 'text': None}, {'kind': 'dismiss', 'text': 'hidden answer'}]:
            status, _ = self.request('POST', '/api/questions/' + q['id'] + '/answer', {'expectedVersion': 1, 'decision': decision})
            self.assertEqual(status, 400, decision)
        for content in [{}, {'kind': 'unknown'}, {'kind': 'answer', 'questionId': None},
                        {'kind': 'message', 'text': 'hello', 'questionId': q['id']}]:
            status, _ = self.request('POST', '/api/codex/dispatch', {'requestKey': uuid.uuid4().hex, 'threadIds': [THREAD_A], 'content': content})
            self.assertEqual(status, 400, content)
        for owner in [{}, {'kind': 'robot'}, {'kind': 'agent', 'threadId': None}]:
            status, _ = self.request('POST', '/api/work-items/', {'title': 'Task', 'workspacePath': WORKSPACE, 'owner': owner})
            self.assertEqual(status, 400, owner)


if __name__ == '__main__':
    unittest.main()
