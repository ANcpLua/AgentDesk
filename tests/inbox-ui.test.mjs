import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { JSDOM } from 'jsdom';

const html = await readFile(new URL('../AgentDesk/wwwroot/index.html', import.meta.url), 'utf8');
const script = await readFile(new URL('../AgentDesk/wwwroot/inbox.js', import.meta.url), 'utf8');
const initialQuestion = (overrides = {}) => ({
  id: 'q1', prompt: 'Welchen Entwurf soll ich verwenden?', choices: [{ label: 'A' }, { label: 'B' }], allowFreeText: true,
  context: 'Beide Entwürfe sind fertig.', project: 'AgentDesk', conversationId: 'task-1',
  source: 'Agent', blocking: false, state: { kind: 'pending' },
  createdAt: '2026-10-27T10:00:00Z', version: 1, ...overrides
});
const threadA = { id: '11111111-1111-4111-8111-111111111111', name: 'Agent A', sessionId: 'session-a', workspacePath: '/projects/demo', status: 'idle', activeFlags: [], model: 'test-model', reasoningEffort: 'high', parentThreadId: null, forkedFromId: null, transcriptPath: '/sessions/a.jsonl', originator: 'codex-tui', agentRole: null };
const threadB = { ...threadA, id: '22222222-2222-4222-8222-222222222222', name: 'Agent B', status: 'active' };
const flush = async () => { for (let i = 0; i < 5; i++) await new Promise(resolve => setImmediate(resolve)); };

async function setup(t, questions = [initialQuestion()]) {
  let snapshot = { revision: 1, questions };
  let stream;
  const requests = [];
  const workItems = [];
  const unexpectedRequests = [];
  const dom = new JSDOM(html, { url: 'http://127.0.0.1:5137', runScripts: 'outside-only', pretendToBeVisual: true });
  const { window } = dom;
  t.after(() => {
    window.close();
    assert.deepEqual(unexpectedRequests, [], 'The UI made an unexpected HTTP request');
  });
  window.EventSource = class {
    constructor() { stream = this; this.handlers = {}; }
    addEventListener(type, handler) { this.handlers[type] = handler; }
    close() {}
  };
  window.fetch = async (path, options = {}) => {
    const method = options.method ?? 'GET';
    const supported = method === 'GET' && (
      path.startsWith('/api/questions/?') || path === '/api/questions/' ||
      (path.startsWith('/api/codex/threads?') || /^\/api\/codex\/threads\/[^/]+$/.test(path)) || path === '/api/codex/dispatch' ||
      path.startsWith('/api/work-items/')) ||
      method === 'POST' && (path === '/api/questions/' ||
        /^\/api\/questions\/[^/]+\/answer$/.test(path) ||
        path === '/api/codex/dispatch' || path === '/api/work-items/');
    if (!supported) {
      unexpectedRequests.push({ method, path });
      throw new Error(`Unexpected request: ${method} ${path}`);
    }
    requests.push({ path, ...options });
    if (path.startsWith('/api/codex/threads?')) return { ok: true, json: async () => ({ threads: [threadA, threadB], nextCursor: null }) };
    if (path.startsWith('/api/codex/threads/')) return { ok: true, json: async () => ({ thread: threadA, goal: 'Keep data', goalStatus: 'active', latestReply: 'The original reply.', latestTurnStatus: 'completed' }) };
    if (path === '/api/codex/dispatch') {
      if (options.method === 'POST') {
        const body = JSON.parse(options.body);
        return { ok: true, json: async () => ({ id: 'batch-1', deliveries: body.threadIds.map(threadId => ({ threadId, state: { kind: 'queued', messageId: 'm1' } })) }) };
      }
      return { ok: true, json: async () => ({ deliveries: [] }) };
    }
    if (path.startsWith('/api/work-items')) {
      if (options.method === 'POST') workItems.push({ ...JSON.parse(options.body), id: 'work-1', state: { kind: 'todo' }, version: 1 });
      return { ok: true, json: async () => options.method === 'POST' ? workItems.at(-1) : { items: workItems } };
    }
    if (options.method === 'POST') {
      const body = JSON.parse(options.body);
      if (path.endsWith('/answer')) {
        const id = path.split('/')[3];
        snapshot = { revision: snapshot.revision + 1, questions: snapshot.questions.map(q => q.id === id
          ? { ...q, state: body.decision.kind === 'dismiss' ? { kind: 'dismissed', at: '2026-10-27T11:00:00Z' } : { kind: 'answered', text: body.decision.text, at: '2026-10-27T11:00:00Z' }, version: 2 } : q) };
        return { ok: true, json: async () => snapshot.questions.find(q => q.id === id) };
      }
      const created = initialQuestion({ ...body, id: 'q-new' });
      snapshot = { revision: snapshot.revision + 1, questions: [created, ...snapshot.questions] };
      return { ok: true, json: async () => created };
    }
    return { ok: true, json: async () => snapshot };
  };
  window.confirm = () => true;
  window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  window.HTMLDialogElement.prototype.close = function () { this.open = false; };
  window.eval(script);
  await flush();
  return { window, document: window.document, requests, emit(questions) {
    snapshot = { revision: snapshot.revision + 1, questions };
    stream.handlers.inbox({ data: JSON.stringify(snapshot) });
  }};
}

function postedBody(app, path) {
  const matches = app.requests.filter(request => request.path === path && request.method === 'POST');
  assert.equal(matches.length, 1, `Expected exactly one POST ${path}`);
  return JSON.parse(matches[0].body);
}

test('selected_answer_is_saved_and_remains_in_history', async t => {
  const app = await setup(t);
  app.document.querySelector('input[type=radio][value=B]').click();
  app.document.querySelector('.question form').dispatchEvent(new app.window.Event('submit', { bubbles: true, cancelable: true }));
  await flush();
  const sent = postedBody(app, '/api/questions/q1/answer');
  assert.deepEqual(sent, { expectedVersion: 1, decision: { kind: 'answer', text: 'B' } });
  assert.equal(app.document.getElementById('pending-count').textContent, '0');
  app.document.querySelector('[data-filter=answered]').click();
  assert.equal(app.document.querySelector('.answer').textContent, 'B');
});

test('incoming_question_preserves_draft_focus_and_caret', async t => {
  const app = await setup(t);
  const input = app.document.getElementById('reply-q1');
  input.value = 'Ich muss darüber nachdenken.';
  input.dispatchEvent(new app.window.Event('input', { bubbles: true }));
  input.focus(); input.setSelectionRange(5, 8);
  app.emit([initialQuestion({ id: 'q2' }), initialQuestion()]);
  const restored = app.document.getElementById('reply-q1');
  assert.equal(restored.value, 'Ich muss darüber nachdenken.');
  assert.equal(app.document.activeElement, restored);
  assert.equal(restored.selectionStart, 5);
  assert.equal(restored.selectionEnd, 8);
  assert.equal(app.document.getElementById('pending-count').textContent, '2');
});

test('free_text_replaces_previously_selected_option', async t => {
  const app = await setup(t);
  app.document.querySelector('input[type=radio][value=A]').click();
  const input = app.document.getElementById('reply-q1');
  input.value = 'Meine eigene Antwort'; input.dispatchEvent(new app.window.Event('input', { bubbles: true }));
  assert.equal(app.document.querySelector('input[type=radio][value=A]').checked, false);
  app.document.querySelector('.question form').dispatchEvent(new app.window.Event('submit', { bubbles: true, cancelable: true }));
  await flush();
  assert.deepEqual(postedBody(app, '/api/questions/q1/answer'), {
    expectedVersion: 1, decision: { kind: 'answer', text: 'Meine eigene Antwort' }
  });
});

test('question_content_is_rendered_as_text_not_markup', async t => {
  const attack = '<img src=x onerror=alert(1)>';
  const app = await setup(t, [initialQuestion({ prompt: attack, context: attack, choices: [{ label: attack }] })]);
  assert.equal(app.document.querySelector('.question-title span').textContent, attack);
  assert.equal(app.document.querySelector('.question img'), null);
  assert.equal(app.document.querySelector('.context').textContent, attack);
  assert.equal(app.document.querySelector('.choice').textContent.trim(), attack);
});

test('test_question_dialog_creates_and_displays_a_question', async t => {
  const app = await setup(t, []);
  app.document.getElementById('new-question').click();
  assert.equal(app.document.getElementById('create-dialog').open, true);
  app.document.getElementById('prompt').value = 'Neue Frage';
  app.document.getElementById('options').value = 'Ja\nNein';
  app.document.getElementById('create-form').dispatchEvent(new app.window.Event('submit', { bubbles: true, cancelable: true }));
  await flush();
  assert.equal(app.document.getElementById('create-dialog').open, false);
  assert.equal(app.document.querySelector('.question-title span').textContent, 'Neue Frage');
  assert.deepEqual(Array.from(app.document.querySelectorAll('.choice'), choice => choice.textContent.trim()), ['Ja', 'Nein']);
  const sent = postedBody(app, '/api/questions/');
  assert.match(sent.requestKey, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
  assert.equal(sent.prompt, 'Neue Frage');
  assert.deepEqual(sent.choices, [{ label: 'Ja' }, { label: 'Nein' }]);
  assert.equal(sent.source, 'Du · Testfrage');
});

test('native_threads_are_selectable_and_fanout_uses_exact_ids', async t => {
  const app = await setup(t);
  app.document.querySelector('[data-view=agents]').click(); await flush();
  assert.equal(app.document.querySelectorAll('.thread-card').length, 2);
  assert.match(app.document.querySelector('.thread-card').textContent, /Agent A/);
  const choices = app.document.querySelectorAll('.thread-select input');
  choices[0].click(); choices[1].click();
  app.document.querySelector('.control-toolbar .primary').click(); await flush();
  assert.equal(app.document.querySelectorAll('#dispatch-targets input:checked').length, 2);
  app.document.getElementById('dispatch-message').value = 'Prüft bitte beide den nächsten Schritt.';
  app.document.getElementById('dispatch-form').dispatchEvent(new app.window.Event('submit', { bubbles: true, cancelable: true }));
  await flush();
  const sent = postedBody(app, '/api/codex/dispatch');
  assert.deepEqual(sent.threadIds, [threadA.id, threadB.id]);
  assert.deepEqual(sent.content, { kind: 'message', text: 'Prüft bitte beide den nächsten Schritt.' });
  assert.match(app.document.querySelector('.send-success').textContent, /angenommen: 2/);
});

test('question_shows_estimated_impacts_and_verified_origin', async t => {
  const app = await setup(t, [initialQuestion({ choices: [{ label: 'Small', impact: 'Weniger Aufwand', followUpPrompt: 'Nur klein ändern.' }], goal: 'Daten erhalten', origin: { threadId: threadA.id, sessionId: 'session-a', workspacePath: '/projects/demo', threadName: 'Agent A', transcriptPath: '/sessions/a.jsonl' } })]);
  assert.equal(app.document.querySelector('.impact').textContent, 'Einschätzung: Weniger Aufwand');
  assert.match(app.document.querySelector('.suggestion-prompt').textContent, /Nur klein ändern/);
  assert.match(app.document.querySelector('.question-origin').textContent, /11111111-1111-4111-8111-111111111111/);
  assert.match(app.document.querySelector('.goal').textContent, /Daten erhalten/);
});

test('saved_answer_dispatch_preselects_its_origin_thread', async t => {
  const app = await setup(t, [initialQuestion({ state: { kind: 'answered', text: 'A', at: '2026-10-27T11:00:00Z' }, origin: { threadId: threadB.id, sessionId: 'session-b', workspacePath: '/projects/demo', threadName: 'Agent B' } })]);
  app.document.querySelector('[data-filter=answered]').click();
  app.document.querySelector('.send-answer').click(); await flush();
  const selected = app.document.querySelectorAll('#dispatch-targets input:checked');
  assert.equal(selected.length, 1); assert.equal(selected[0].value, threadB.id);
  app.document.getElementById('dispatch-form').dispatchEvent(new app.window.Event('submit', { bubbles: true, cancelable: true })); await flush();
  const request = postedBody(app, '/api/codex/dispatch');
  assert.deepEqual(request.threadIds, [threadB.id]);
  assert.deepEqual(request.content, { kind: 'answer', questionId: 'q1', note: '' });
});

test('operator_can_record_an_agent_owned_task', async t => {
  const app = await setup(t);
  app.document.querySelector('[data-view=work]').click(); await flush();
  app.document.querySelector('.control-toolbar .primary').click(); await flush();
  app.document.getElementById('work-title').value = 'Ergebnis prüfen';
  app.document.getElementById('work-owner').value = 'agent';
  app.document.getElementById('work-thread').value = threadA.id;
  app.document.getElementById('work-thread').dispatchEvent(new app.window.Event('change'));
  app.document.getElementById('work-form').dispatchEvent(new app.window.Event('submit', { bubbles: true, cancelable: true })); await flush();
  const request = postedBody(app, '/api/work-items/');
  assert.equal(request.title, 'Ergebnis prüfen');
  assert.deepEqual(request.owner, { kind: 'agent', threadId: threadA.id });
  assert.equal(request.workspacePath, '/projects/demo');
  assert.match(app.document.querySelector('.work-board').textContent, /Ergebnis prüfen/);
});
