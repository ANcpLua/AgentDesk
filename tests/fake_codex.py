"""Deterministic local Codex protocol double; never reaches a real agent."""
import base64
import hashlib
import json
import os
from pathlib import Path
import socketserver
import struct
import tempfile
import threading

THREAD_A = '11111111-1111-4111-8111-111111111111'
THREAD_B = '22222222-2222-4222-8222-222222222222'
WORKSPACE = '/projects/inbox-demo'


def thread_info(thread_id):
    if thread_id not in (THREAD_A, THREAD_B):
        raise KeyError(thread_id)
    return {'id': thread_id, 'sessionId': thread_id, 'name': 'Agent A' if thread_id == THREAD_A else 'Agent B',
            'cwd': WORKSPACE, 'status': {'type': 'idle' if thread_id == THREAD_A else 'active', 'activeFlags': []},
            'model': 'test-model', 'reasoningEffort': 'high', 'originator': 'codex-tui',
            'path': '/sessions/' + thread_id + '.jsonl', 'updatedAt': 1793065389}


class FakeCodex:
    def __init__(self):
        self.temp = tempfile.TemporaryDirectory(prefix='inbox-native-', dir='/tmp')
        self.root = Path(self.temp.name)
        self.socket = self.root / 'c.sock'
        self.log = self.root / 'queued.jsonl'
        owner = self

        class Handler(socketserver.StreamRequestHandler):
            def handle(self):
                try:
                    headers = {}
                    self.rfile.readline()
                    while (line := self.rfile.readline()) not in (b'\r\n', b'\n', b''):
                        key, value = line.decode().split(':', 1)
                        headers[key.lower()] = value.strip()
                    accept = base64.b64encode(hashlib.sha1((headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode()).digest()).decode()
                    self.wfile.write(('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ' + accept + '\r\n\r\n').encode())
                    self.wfile.flush()
                    while True:
                        head = self.rfile.read(2)
                        if len(head) < 2 or head[0] & 15 == 8:
                            return
                        size = head[1] & 127
                        if size == 126:
                            size = struct.unpack('!H', self.rfile.read(2))[0]
                        elif size == 127:
                            size = struct.unpack('!Q', self.rfile.read(8))[0]
                        mask = self.rfile.read(4) if head[1] & 128 else None
                        data = self.rfile.read(size)
                        if mask:
                            data = bytes(x ^ mask[i % 4] for i, x in enumerate(data))
                        request = json.loads(data)
                        if 'id' not in request:
                            continue
                        try:
                            result = owner.response(request['method'], request.get('params', {}))
                            response = {'id': request['id'], 'result': result}
                        except KeyError:
                            response = {'id': request['id'], 'error': {'code': -32000, 'message': 'Unknown test thread'}}
                        output = json.dumps(response).encode()
                        header = bytes([0x81, len(output)]) if len(output) < 126 else bytes([0x81, 126]) + struct.pack('!H', len(output))
                        self.wfile.write(header + output)
                        self.wfile.flush()
                except (BrokenPipeError, ConnectionResetError):
                    pass

        class Server(socketserver.ThreadingUnixStreamServer):
            daemon_threads = True

        self.server = Server(str(self.socket), Handler)
        self.worker = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.worker.start()
        self.executable = self.root / 'codex'
        self.executable.write_text('''#!/usr/bin/env python3
import json,os,sys,time,uuid
args=sys.argv[1:]
assert args[0]=='queue'
thread=args[args.index('--thread')+1]
message=args[args.index('--message')+1]
fd=os.open(os.environ['FAKE_CODEX_LOG'],os.O_WRONLY|os.O_CREAT|os.O_APPEND,0o600)
os.write(fd,(json.dumps({'thread':thread,'message':message})+'\\n').encode());os.close(fd)
if '[timeout]' in message: time.sleep(3)
if '[unreadable-receipt]' in message:
 print('unknown receipt format');sys.exit(0)
if '[fail-second]' in message and thread.startswith('2222'):
 print('test queue rejection',file=sys.stderr);sys.exit(1)
print(f'Queued message {uuid.uuid4()} for thread {thread}.')
''')
        self.executable.chmod(0o755)

    @property
    def environment(self):
        return {'QUESTION_INBOX_CODEX': str(self.executable), 'QUESTION_INBOX_CODEX_SOCKET': str(self.socket),
                'QUESTION_INBOX_QUEUE_TIMEOUT_SECONDS': '1', 'FAKE_CODEX_LOG': str(self.log)}

    def response(self, method, args):
        if method == 'initialize':
            return {'userAgent': 'fake-codex'}
        if method == 'thread/list':
            items = [thread_info(THREAD_A), thread_info(THREAD_B)]
            if args.get('cwd') and args['cwd'] != WORKSPACE:
                items = []
            return {'data': items, 'nextCursor': None}
        if method == 'thread/read':
            return {'thread': thread_info(args['threadId'])}
        if method == 'thread/goal/get':
            thread_info(args['threadId'])
            return {'goal': {'objective': 'Preserve the shared task state', 'status': 'active'}}
        if method == 'thread/turns/list':
            thread_info(args['threadId'])
            return {'data': [{'id': 'test-turn', 'status': 'completed', 'items': [
                {'type': 'agentMessage', 'text': 'A bounded summary from the original thread.'}]}]}
        raise KeyError(method)

    def queued(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.worker.join(timeout=2)
        self.temp.cleanup()
