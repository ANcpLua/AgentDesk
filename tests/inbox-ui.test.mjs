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
const usage = (overrides = {}) => ({ ordinaryUsageAllowed: true, rateLimits: { limitId: 'codex', limitName: null, primary: { usedPercent: 25, windowDurationMins: 10080, resetsAt: 1793779482 }, secondary: null, credits: { hasCredits: true, unlimited: false, balance: '12.5' } }, rateLimitsByLimitId: null, ...overrides });
const usageResponse = (data = usage()) => ({ ok: true, json: async () => data });
const tokenActivity = (overrides = {}) => ({ summary: { lifetimeTokens: '9007199254740993', peakDailyTokens: '12345',
  longestRunningTurnSec: '3600', currentStreakDays: '0', longestStreakDays: '5' },
  dailyUsageBuckets: [{ startDate: '2026-10-28', tokens: '12345' }, { startDate: '2026-10-29', tokens: '0' }], ...overrides });
const tokenResponse = (data = tokenActivity()) => ({ ok: true, json: async () => data });
const hook = (overrides = {}) => ({ key: 'guard', eventName: 'preToolUse', matcher: 'exec.*', timeoutSec: 30,
  statusMessage: 'Inspecting command', additionalContextLimit: null, sourcePath: '/projects/demo/.codex/hooks.json',
  source: 'project', pluginId: null, displayOrder: 0, enabled: true, isManaged: false, currentHash: 'sha256:test',
  trustStatus: 'untrusted', handlerType: 'command', command: 'echo <hook-preview>', async: false, server: null, tool: null, ...overrides });
const hooksResponse = (hooks = [hook()], overrides = {}) => ({ ok: true, json: async () => ({ data: [{ cwd: '/projects/demo', hooks, warnings: [], errors: [], ...overrides }] }) });

test('hooks view shows native trust independently from enabled and managed state', async t => {
  const app = await setup(t, undefined, { readHooks: () => hooksResponse([
    hook(), hook({ key: 'managed', handlerType: 'mcpTool', command: null, async: null, server: 'policy', tool: 'review', trustStatus: 'managed', isManaged: true, pluginId: 'policy@company' }),
    hook({ key: 'trusted', handlerType: 'prompt', trustStatus: 'trusted', enabled: false }),
    hook({ key: 'modified', handlerType: 'agent', trustStatus: 'modified' }),
    hook({ key: 'future', handlerType: 'futureHandler', trustStatus: 'futureTrust', source: 'futureSource' })
  ]) });
  app.document.querySelector('[data-view=hooks]').click(); await flush();
  const center = app.document.querySelector('#control-center');
  assert.equal(app.document.querySelector('#heading').textContent, 'Codex-Hooks & Vertrauen');
  const cards = [...center.querySelectorAll('.hook-card')];
  assert.equal(cards.length, 5);
  assert.match(cards[0].textContent, /Vertrauen: Nicht vertraut/);
  assert.match(cards[0].textContent, /Aktiviert/);
  assert.match(cards[0].textContent, /Aktivierung allein bestätigt keine Freigabe/);
  assert.match(cards[0].textContent, /\/projects\/demo\/\.codex\/hooks.json/);
  assert.equal(cards[0].querySelector('pre').textContent, 'echo <hook-preview>');
  assert.equal(cards[0].querySelector('hook-preview'), null);
  assert.match(cards[0].textContent, /sha256:test/);
  assert.match(cards[1].textContent, /Vertrauen: Verwaltet/);
  assert.match(cards[1].textContent, /Zentral verwaltet/);
  assert.match(cards[1].textContent, /MCP: policy \/ review/);
  assert.match(cards[1].textContent, /policy@company/);
  assert.match(cards[2].textContent, /Vertrauen: Vertraut/);
  assert.match(cards[2].textContent, /Deaktiviert/);
  assert.match(cards[3].textContent, /Seit der Freigabe geändert/);
  assert.match(cards[4].textContent, /futureTrust|futureSource/);
  assert.equal(center.querySelectorAll('button').length, 1);
  assert.deepEqual(app.requests.filter(r => r.path.startsWith('/api/codex/hooks')).map(r => [r.method, r.path]), [['GET', '/api/codex/hooks']]);
  assert.equal(app.requests.filter(r => r.method !== 'GET').length, 0);
});

test('hooks view preserves load diagnostics and distinguishes empty configuration', async t => {
  const app = await setup(t, undefined, { readHooks: () => hooksResponse([], { warnings: ['Duplicate matcher'], errors: [{ path: '/bad/hooks.json', message: 'Invalid hook' }] }) });
  app.document.querySelector('[data-view=hooks]').click(); await flush();
  const center = app.document.querySelector('#control-center');
  assert.match(center.textContent, /Warnung: Duplicate matcher/);
  assert.match(center.querySelector('[role=alert]').textContent, /Fehler in \/bad\/hooks.json: Invalid hook/);
  assert.match(center.textContent, /keine Hooks gemeldet/);
  assert.equal(center.querySelectorAll('.hook-card').length, 0);
});

test('hooks view shows an empty native snapshot without inventing a project', async t => {
  const app = await setup(t, undefined, { readHooks: () => ({ ok: true, json: async () => ({ data: [] }) }) });
  app.document.querySelector('[data-view=hooks]').click(); await flush();
  const center = app.document.querySelector('#control-center');
  assert.match(center.textContent, /keine Hook-Konfiguration gemeldet/);
  assert.equal(center.querySelector('.hooks-project'), null);
  assert.equal(center.querySelector('[role=alert]'), null);
});

test('hooks view sends the explicit project path and retains it on refresh', async t => {
  const app = await setup(t); app.document.querySelector('[data-view=hooks]').click(); await flush();
  const center = app.document.querySelector('#control-center');
  center.querySelector('input').value = '/projects/space & unicode ä';
  center.querySelector('button').click(); await flush();
  assert.equal(center.querySelector('input').value, '/projects/space & unicode ä');
  center.querySelector('button').click(); await flush();
  assert.deepEqual(app.requests.filter(r => r.path.startsWith('/api/codex/hooks')).map(r => r.path),
    ['/api/codex/hooks', '/api/codex/hooks?workspacePath=%2Fprojects%2Fspace%20%26%20unicode%20%C3%A4', '/api/codex/hooks?workspacePath=%2Fprojects%2Fspace%20%26%20unicode%20%C3%A4']);
});

test('hooks failure exposes an error with a working read-only retry', async t => {
  let attempts = 0;
  const app = await setup(t, undefined, { readHooks: () => ++attempts === 1
    ? { ok: false, json: async () => ({ title: 'hooks/list unavailable' }) } : hooksResponse() });
  app.document.querySelector('[data-view=hooks]').click(); await flush();
  const center = app.document.querySelector('#control-center');
  assert.match(center.querySelector('[role=alert]').textContent, /hooks\/list unavailable/);
  assert.doesNotMatch(center.textContent, /keine Hooks gemeldet|Hooks werden geladen/);
  assert.equal(center.querySelector('button').textContent, 'Erneut versuchen');
  center.querySelector('button').click(); await flush();
  assert.match(center.textContent, /Vertrauen: Nicht vertraut/);
  assert.equal(center.querySelector('[role=alert]'), null);
});

for (const outcome of ['success', 'failure']) {
  test(`hooks view ignores older ${outcome} after refresh`, async t => {
    const older = Promise.withResolvers(); let attempts = 0;
    const app = await setup(t, undefined, { readHooks: () => ++attempts === 1 ? older.promise : hooksResponse() });
    app.document.querySelector('[data-view=hooks]').click();
    const center = app.document.querySelector('#control-center');
    center.querySelector('button').click(); await flush();
    const current = center.innerHTML;
    if (outcome === 'success') older.resolve(hooksResponse([hook({ trustStatus: 'trusted' })]));
    else older.reject(new Error('Old hooks failure'));
    await flush(); assert.equal(center.innerHTML, current);
  });
}

test('hooks completion after navigation cannot replace the active view', async t => {
  const pending = Promise.withResolvers();
  const app = await setup(t, undefined, { readHooks: () => pending.promise });
  app.document.querySelector('[data-view=hooks]').click();
  app.document.querySelector('[data-view=work]').click(); await flush();
  pending.resolve(hooksResponse()); await flush();
  assert.ok(app.document.querySelector('#control-center .work-board'));
  assert.equal(app.document.querySelector('#control-center .hook-card'), null);
});
const mcpServer = (overrides = {}) => ({ name: 'local-tools', runtimeStatus: null, authStatus: 'unsupported', toolCount: 2, toolsError: null, httpOrigin: null, pluginId: null, ...overrides });
const mcpResponse = (servers = [mcpServer()], nextCursor = null) => ({ ok: true, json: async () => ({ servers, nextCursor }) });
const buttonText = (element, text) => [...element.querySelectorAll('button')].find(button => button.textContent === text);


test('MCP view keeps unknown runtime separate from auth, tool availability, and failures', async t => {
  const app = await setup(t, undefined, { readMcp: () => mcpResponse([
    mcpServer(), mcpServer({ name: 'connected', runtimeStatus: 'connected', toolCount: 0 }),
    mcpServer({ name: 'broken', runtimeStatus: 'failed', toolCount: null, toolsError: 'Discovery rejected' }),
    mcpServer({ name: 'login', runtimeStatus: 'authenticationRequired', authStatus: 'notLoggedIn' }),
    mcpServer({ name: 'future', runtimeStatus: 'newState', authStatus: 'newAuth' })
  ]) });
  app.document.querySelector('[data-view=mcp]').click();
  await flush();
  const cards = [...app.document.querySelectorAll('.mcp-card')];
  assert.equal(cards.length, 5);
  assert.match(cards[0].textContent, /Unbekannt · kein Laufzeitstatus gemeldet/);
  assert.equal(cards[0].querySelector('.queued'), null);
  assert.match(cards[0].textContent, /Authentifizierung: Nicht unterstützt/);
  assert.match(cards[0].textContent, /2 Werkzeuge im gemeldeten Katalog/);
  assert.equal(cards[1].querySelector('.queued').textContent, 'Verbunden');
  assert.match(cards[1].textContent, /0 Werkzeuge/);
  assert.match(cards[2].textContent, /Fehlgeschlagen/);
  assert.match(cards[2].textContent, /Werkzeugliste nicht verfügbar: Discovery rejected/);
  assert.doesNotMatch(cards[2].textContent, /0 Werkzeuge/);
  assert.match(cards[3].textContent, /Anmeldung erforderlich/);
  assert.match(cards[3].textContent, /Nicht angemeldet/);
  assert.match(cards[4].textContent, /Unbekannter Status: newState/);
  assert.match(cards[4].textContent, /Authentifizierung: newAuth/);
});

test('MCP view scopes the read to the selected native thread and refresh preserves that scope', async t => {
  const app = await setup(t);
  app.document.querySelector('[data-view=mcp]').click();
  await flush();
  const scope = app.document.querySelector('#mcp-thread');
  assert.equal(scope.options.length, 3);
  scope.value = threadB.id;
  scope.dispatchEvent(new app.window.Event('change'));
  await flush();
  buttonText(app.document.querySelector('#control-center'), 'Aktualisieren').click();
  await flush();
  assert.deepEqual(app.requests.filter(r => r.path.startsWith('/api/codex/mcp-servers?')).map(r => r.path), [
    '/api/codex/mcp-servers?', '/api/codex/mcp-servers?threadId=' + threadB.id, '/api/codex/mcp-servers?threadId=' + threadB.id
  ]);
  assert.equal(app.requests.filter(r => r.method && r.method !== 'GET').length, 0);
});

test('MCP pagination preserves opaque cursors and existing cards across a failed next page', async t => {
  const cursor = 'next &=?/ü';
  let reads = 0;
  const app = await setup(t, undefined, { readMcp: () => ++reads === 1 ? mcpResponse([mcpServer()], cursor)
    : reads === 2 ? { ok: false, json: async () => ({ title: 'Next page failed' }) }
    : mcpResponse([mcpServer({ name: 'second', runtimeStatus: 'disabled' })]) });
  app.document.querySelector('[data-view=mcp]').click();
  await flush();
  const center = app.document.querySelector('#control-center');
  buttonText(center, 'Weitere Server laden').click();
  await flush();
  assert.equal(center.querySelectorAll('.mcp-card').length, 1);
  assert.match(center.querySelector('[role=alert]:not([hidden])').textContent, /Next page failed/);
  buttonText(center, 'Weitere Server laden').click();
  await flush();
  assert.deepEqual([...center.querySelectorAll('.mcp-card h2')].map(n => n.textContent), ['local-tools', 'second']);
  assert.equal(buttonText(center, 'Weitere Server laden').hidden, true);
  assert.equal(center.querySelector('[role=alert]:not([hidden])'), null);
  const paths = app.requests.filter(r => r.path.startsWith('/api/codex/mcp-servers?')).map(r => r.path);
  assert.equal(paths.length, 3);
  assert.equal(new URL(paths[1], 'http://localhost').searchParams.get('cursor'), cursor);
  assert.equal(paths[1], paths[2]);
});

test('MCP method unavailable is an error with retry, while an empty result is a distinct state', async t => {
  let reads = 0;
  const app = await setup(t, undefined, { readMcp: () => ++reads === 1
    ? { ok: false, json: async () => ({ title: 'Diese Codex-Version stellt MCP-Verbindungsstatus nicht bereit.' }) }
    : mcpResponse([]) });
  app.document.querySelector('[data-view=mcp]').click();
  await flush();
  const center = app.document.querySelector('#control-center');
  assert.match(center.querySelector('[role=alert]:not([hidden])').textContent, /nicht bereit/);
  assert.doesNotMatch(center.textContent, /werden geladen|keine MCP-Server/);
  buttonText(center, 'Erneut versuchen').click();
  await flush();
  assert.match(center.textContent, /Codex meldet in dieser Auswahl keine MCP-Server/);
  assert.equal(center.querySelector('[role=alert]:not([hidden])'), null);
  assert.equal(reads, 2);
});

for (const outcome of ['success', 'failure']) {
  test(`MCP view ignores stale ${outcome} after refresh`, async t => {
    const older = Promise.withResolvers();
    let reads = 0;
    const app = await setup(t, undefined, { readMcp: () => ++reads === 1 ? older.promise : mcpResponse([mcpServer({ name: 'fresh' })]) });
    app.document.querySelector('[data-view=mcp]').click();
    const center = app.document.querySelector('#control-center');
    buttonText(center, 'Aktualisieren').click();
    await flush();
    assert.equal(center.querySelector('.mcp-card h2').textContent, 'fresh');
    const fresh = center.innerHTML;
    if (outcome === 'success') older.resolve(mcpResponse([mcpServer({ name: 'obsolete' })]));
    else older.reject(new Error('Obsolete error'));
    await flush();
    assert.equal(center.innerHTML, fresh);
  });
}

test('MCP completion after leaving and reentering the view cannot replace the newer view', async t => {
  const older = Promise.withResolvers();
  let reads = 0;
  const app = await setup(t, undefined, { readMcp: () => ++reads === 1 ? older.promise : mcpResponse([mcpServer({ name: 'new mount' })]) });
  app.document.querySelector('[data-view=mcp]').click();
  app.document.querySelector('[data-view=work]').click();
  await flush();
  const center = app.document.querySelector('#control-center');
  assert.ok(center.querySelector('.work-board'));
  app.document.querySelector('[data-view=mcp]').click();
  await flush();
  older.resolve(mcpResponse([mcpServer({ name: 'obsolete mount' })]));
  await flush();
  assert.equal(center.querySelector('.mcp-card h2').textContent, 'new mount');
});

test('MCP server fields and discovery errors are rendered as text', async t => {
  const attack = '<img src=x onerror=alert(1)>';
  const app = await setup(t, undefined, { readMcp: () => mcpResponse([mcpServer({ name: attack, runtimeStatus: attack,
    authStatus: attack, toolsError: attack, toolCount: null, httpOrigin: attack, pluginId: attack })]) });
  app.document.querySelector('[data-view=mcp]').click();
  await flush();
  const card = app.document.querySelector('.mcp-card');
  assert.equal(card.querySelector('h2').textContent, attack);
  assert.equal(card.querySelectorAll('img').length, 0);
  for (const prefix of ['Unbekannter Status: ', 'Authentifizierung: ', 'Werkzeugliste nicht verfügbar: ', 'HTTP-Ursprung: ', 'Plugin: '])
    assert.ok(card.textContent.includes(prefix + attack));
});

test('MCP thread picker loads later native thread pages', async t => {
  let reads = 0;
  const app = await setup(t, undefined, { readThreads: () => ({ ok: true, json: async () => ++reads === 1
    ? { threads: [threadA], nextCursor: 'older threads' } : { threads: [threadB], nextCursor: null } }) });
  app.document.querySelector('[data-view=mcp]').click();
  await flush();
  const center = app.document.querySelector('#control-center');
  assert.equal(center.querySelector('#mcp-thread').options.length, 2);
  buttonText(center, 'Weitere Threads laden').click();
  await flush();
  assert.equal(center.querySelector('#mcp-thread').options.length, 3);
  assert.equal(buttonText(center, 'Weitere Threads laden').hidden, true);
  assert.equal(app.requests.filter(r => r.path.startsWith('/api/codex/threads?')).at(-1).path, '/api/codex/threads?cursor=older+threads');
});

test('usage view reads and refreshes the remaining allowance', async t => {
  const app = await setup(t);
  app.document.querySelector('[data-view=usage]').click();
  await flush();
  const center = app.document.querySelector('#control-center');
  assert.match(center.textContent, /75 % verbleibend/);
  assert.match(center.textContent, /Primäres Kontingent · 7 Tage/);
  assert.match(center.textContent, /Credits: 12,5/);
  assert.match(center.textContent, /Zurücksetzung:/);
  assert.doesNotMatch(center.textContent, /10080 Minuten/);
  center.querySelector('button').click();
  await flush();
  assert.equal(app.requests.filter(r => r.path === '/api/codex/usage').length, 2);
});

test('usage failure replaces loading with an error and a working retry', async t => {
  let attempts = 0;
  const app = await setup(t, undefined, { readUsage: () => ++attempts === 1
    ? { ok: false, json: async () => ({ title: 'Codex ist vorübergehend nicht erreichbar.' }) }
    : usageResponse() });
  app.document.querySelector('[data-view=usage]').click();
  const center = app.document.querySelector('#control-center');
  assert.match(center.textContent, /Nutzung wird geladen/);
  assert.equal(center.querySelector('button').disabled, false);
  await flush();
  assert.match(center.querySelector('[role=alert]').textContent, /Codex ist vorübergehend nicht erreichbar/);
  assert.doesNotMatch(center.textContent, /Nutzung wird geladen/);
  const retry = center.querySelector('button');
  assert.equal(retry.textContent, 'Erneut versuchen');
  retry.click();
  await flush();
  assert.match(center.textContent, /75 % verbleibend/);
  assert.equal(center.querySelector('[role=alert]'), null);
  assert.equal(center.querySelector('button').textContent, 'Aktualisieren');
  assert.equal(app.document.querySelector('#error').hidden, true);
  assert.equal(app.requests.filter(r => r.path === '/api/codex/usage').length, 2);
});

for (const outcome of ['success', 'failure']) {
  test(`usage view ignores an older ${outcome} after a newer request succeeds`, async t => {
    const older = Promise.withResolvers();
    const newer = Promise.withResolvers();
    let attempts = 0;
    const app = await setup(t, undefined, { readUsage: () => ++attempts === 1 ? older.promise : newer.promise });
    app.document.querySelector('[data-view=usage]').click();
    const center = app.document.querySelector('#control-center');
    center.querySelector('button').click();
    newer.resolve(usageResponse());
    await flush();
    assert.match(center.textContent, /75 % verbleibend/);
    const freshContent = center.innerHTML;
    if (outcome === 'success') older.resolve(usageResponse(usage({ rateLimits: { ...usage().rateLimits, primary: { usedPercent: 95, windowDurationMins: 60, resetsAt: null } } })));
    else older.reject(new Error('Obsolete failure'));
    await flush();
    assert.equal(center.innerHTML, freshContent);
    assert.equal(center.querySelector('[role=alert]'), null);
    assert.equal(app.document.querySelector('#error').hidden, true);
    assert.equal(app.requests.filter(r => r.path === '/api/codex/usage').length, 2);
  });
}

test('usage completion after navigation cannot replace the active view', async t => {
  const pending = Promise.withResolvers();
  const app = await setup(t, undefined, { readUsage: () => pending.promise });
  app.document.querySelector('[data-view=usage]').click();
  app.document.querySelector('[data-view=work]').click();
  await flush();
  const center = app.document.querySelector('#control-center');
  assert.ok(center.querySelector('.work-board'));
  pending.resolve(usageResponse());
  await flush();
  assert.ok(center.querySelector('.work-board'));
  assert.doesNotMatch(center.textContent, /75 % verbleibend/);
});

test('usage display explains periods and rounds credits while preserving the reported value', async t => {
  const balance = '1234.5678901234';
  const app = await setup(t, undefined, { readUsage: () => usageResponse(usage({ rateLimits: {
    ...usage().rateLimits,
    secondary: { usedPercent: 50, windowDurationMins: 90, resetsAt: null },
    credits: { hasCredits: true, unlimited: false, balance }
  } })) });
  app.document.querySelector('[data-view=usage]').click();
  await flush();
  const center = app.document.querySelector('#control-center');
  assert.match(center.textContent, /Sekundäres Kontingent · 1 Stunde 30 Minuten/);
  assert.match(center.textContent, /50 % verbleibend/);
  const credits = [...center.querySelectorAll('p')].find(p => p.textContent.startsWith('Credits:'));
  assert.equal(credits.textContent, 'Credits: ≈ 1\u00a0234,57');
  assert.equal(credits.title, `Gemeldeter Wert: ${balance}`);
  assert.doesNotMatch(credits.textContent, /1234\.5678901234|€|\$|Token/);
});

test('daily token activity preserves exact counters and reported dates separately from allowance', async t => {
  const app = await setup(t);
  app.document.querySelector('[data-view=usage]').click();
  await flush();
  const section = app.document.querySelector('.token-activity');
  const facts = [...section.querySelectorAll('.token-summary dd')].map(n => n.textContent);
  assert.deepEqual(facts, [BigInt('9007199254740993').toLocaleString('de-AT'), '12\u00a0345', '3\u00a0600 Sekunden', '0 Tage', '5 Tage']);
  const rows = [...section.querySelectorAll('tbody tr')];
  assert.equal(rows.length, 2);
  assert.deepEqual(rows.map(row => row.querySelector('time').dateTime), ['2026-10-29', '2026-10-28']);
  assert.deepEqual(rows.map(row => row.querySelector('time').textContent), ['29.10.2026', '28.10.2026']);
  assert.deepEqual(rows.map(row => row.querySelector('.token-count').textContent), ['0', '12\u00a0345']);
  assert.deepEqual(rows.map(row => row.querySelector('.token-bar').style.width), ['0%', '100%']);
  assert.equal(section.querySelector('caption').textContent, '2 von Codex gemeldete Tage · neueste zuerst');
  assert.doesNotMatch(section.textContent, /75 % verbleibend|Credits:/);
  assert.equal(app.requests.filter(r => r.path === '/api/codex/token-activity').length, 1);
  app.document.querySelector('#control-center > button').click();
  await flush();
  assert.equal(app.requests.filter(r => r.path === '/api/codex/token-activity').length, 2);
});

for (const state of ['unavailable', 'empty']) {
  test(`daily token activity distinguishes ${state} measurements from reported zero`, async t => {
    const data = tokenActivity({ summary: { lifetimeTokens: null, peakDailyTokens: null,
      longestRunningTurnSec: null, currentStreakDays: '0', longestStreakDays: null },
      dailyUsageBuckets: state === 'unavailable' ? null : [] });
    const app = await setup(t, undefined, { readTokenActivity: () => tokenResponse(data) });
    app.document.querySelector('[data-view=usage]').click(); await flush();
    const section = app.document.querySelector('.token-activity');
    assert.deepEqual([...section.querySelectorAll('dd')].map(n => n.textContent),
      ['Nicht verfügbar', 'Nicht verfügbar', 'Nicht verfügbar', '0 Tage', 'Nicht verfügbar']);
    assert.match(section.textContent, state === 'unavailable' ? /Tägliche Token-Daten sind nicht verfügbar/ : /Keine Tage mit Token-Aktivität gemeldet/);
    assert.equal(section.querySelector('table'), null);
  });
}

test('token activity loads when the independent allowance read fails', async t => {
  const app = await setup(t, undefined, { readUsage: () => ({ ok: false, json: async () => ({ title: 'Allowance offline' }) }) });
  app.document.querySelector('[data-view=usage]').click(); await flush();
  assert.match(app.document.querySelector('#control-center > div [role=alert]').textContent, /Allowance offline/);
  assert.ok(app.document.querySelector('.token-activity table'));
  assert.equal(app.document.querySelector('.token-activity [role=alert]'), null);
});

test('token activity failure has an independent retry while allowance remains visible', async t => {
  let attempts = 0;
  const app = await setup(t, undefined, { readTokenActivity: () => ++attempts === 1
    ? { ok: false, json: async () => ({ title: 'Token data offline' }) } : tokenResponse() });
  app.document.querySelector('[data-view=usage]').click(); await flush();
  const section = app.document.querySelector('.token-activity');
  assert.match(section.querySelector('[role=alert]').textContent, /Token data offline/);
  assert.doesNotMatch(section.textContent, /wird geladen/);
  assert.match(app.document.querySelector('#control-center').textContent, /75 % verbleibend/);
  section.querySelector('button').click(); await flush();
  assert.ok(section.querySelector('table'));
  assert.equal(section.querySelector('[role=alert]'), null);
  assert.equal(app.requests.filter(r => r.path === '/api/codex/token-activity').length, 2);
  assert.equal(app.requests.filter(r => r.path === '/api/codex/usage').length, 1);
});

for (const outcome of ['success', 'failure']) {
  test(`daily token activity ignores an older ${outcome} after refresh`, async t => {
    const old = Promise.withResolvers(); let attempts = 0;
    const app = await setup(t, undefined, { readTokenActivity: () => ++attempts === 1 ? old.promise : tokenResponse() });
    app.document.querySelector('[data-view=usage]').click();
    app.document.querySelector('#control-center > button').click(); await flush();
    const section = app.document.querySelector('.token-activity'); const current = section.innerHTML;
    if (outcome === 'success') old.resolve(tokenResponse(tokenActivity({ dailyUsageBuckets: null })));
    else old.reject(new Error('Obsolete activity failure'));
    await flush();
    assert.equal(app.document.querySelector('.token-activity'), section);
    assert.equal(section.innerHTML, current);
    assert.equal(app.document.querySelector('#error').hidden, true);
  });
}

test('daily token activity completion after navigation does not replace the current view', async t => {
  const old = Promise.withResolvers();
  const app = await setup(t, undefined, { readTokenActivity: () => old.promise });
  app.document.querySelector('[data-view=usage]').click();
  app.document.querySelector('[data-view=work]').click(); await flush();
  old.resolve(tokenResponse()); await flush();
  assert.ok(app.document.querySelector('.work-board'));
  assert.equal(app.document.querySelector('.token-activity'), null);
});

async function setup(t, questions = [initialQuestion()], { readUsage = () => usageResponse(), readTokenActivity = () => tokenResponse(), readHooks = () => hooksResponse(), readMcp = () => mcpResponse(), readThreads = () => ({ ok: true, json: async () => ({ threads: [threadA, threadB], nextCursor: null }) }) } = {}) {
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
      (path.startsWith('/api/codex/threads?') || /^\/api\/codex\/threads\/[^/]+$/.test(path)) || path === '/api/codex/dispatch' || path === '/api/codex/usage' || path === '/api/codex/token-activity' || path.startsWith('/api/codex/mcp-servers?') ||
      path.startsWith('/api/work-items/') || path.startsWith('/api/codex/hooks')) ||
      method === 'POST' && (path === '/api/questions/' ||
        /^\/api\/questions\/[^/]+\/answer$/.test(path) ||
        path === '/api/codex/dispatch' || path === '/api/work-items/');
    if (!supported) {
      unexpectedRequests.push({ method, path });
      throw new Error(`Unexpected request: ${method} ${path}`);
    }
    requests.push({ path, ...options });
    if (path === '/api/codex/usage') return readUsage();
    if (path.startsWith('/api/codex/hooks')) return readHooks();
    if (path === '/api/codex/token-activity') return readTokenActivity();
    if (path.startsWith('/api/codex/mcp-servers?')) return readMcp(path);
    if (path.startsWith('/api/codex/threads?')) return readThreads(path);
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
