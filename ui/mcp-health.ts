import type { Api } from './control-center';

interface McpServer { name: string; runtimeStatus: string | null; authStatus: string; toolCount: number | null; toolsError: string | null; httpOrigin: string | null; pluginId: string | null }
interface McpPage { servers: McpServer[]; nextCursor: string | null }
interface ThreadPage { threads: { id: string; name: string; workspacePath: string }[]; nextCursor: string | null }
const el = <T extends keyof HTMLElementTagNameMap>(tag: T, className = '', text?: string) => {
  const node = document.createElement(tag); node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
};
const runtimeNames: Record<string, string> = {
  notStarted: 'Nicht gestartet', starting: 'Verbindet …', connected: 'Verbunden',
  authenticationRequired: 'Anmeldung erforderlich', failed: 'Fehlgeschlagen', cancelled: 'Abgebrochen', disabled: 'Deaktiviert'
};
const authNames: Record<string, string> = {
  unknown: 'Unbekannt', unsupported: 'Nicht unterstützt', notLoggedIn: 'Nicht angemeldet', bearerToken: 'Bearer-Token', oAuth: 'OAuth'
};

export function mountMcpHealth(center: HTMLElement, api: Api, isActive: () => boolean) {
  let request = 0;
  let cursor: string | null = null;
  let threadCursor: string | null = null;
  let servers: McpServer[] = [];
  const toolbar = el('div', 'control-toolbar');
  const label = el('label', '', 'Verbindungen für'); label.htmlFor = 'mcp-thread';
  const scope = el('select'); scope.id = 'mcp-thread'; scope.setAttribute('aria-label', 'MCP-Verbindungen für Thread');
  scope.add(new Option('Konfiguration · ohne Thread', ''));
  const refresh = el('button', 'button secondary', 'Aktualisieren');
  const moreThreads = el('button', 'button secondary', 'Weitere Threads laden'); moreThreads.hidden = true;
  toolbar.append(label, scope, refresh, moreThreads);
  const scopeError = el('p', 'question-error'); scopeError.hidden = true; scopeError.setAttribute('role', 'alert');
  const hint = el('p', 'muted', 'Die Verbindungen eines Threads sind nur verfügbar, wenn Codex ihn geladen hat. Ohne gemeldeten Laufzeitstatus bleibt die Verbindung unbekannt; Anmeldung und Werkzeugliste belegen keine aktive Verbindung.');
  const content = el('div', 'mcp-list');
  const more = el('button', 'button secondary', 'Weitere Server laden'); more.hidden = true;
  const failure = el('p', 'question-error'); failure.hidden = true; failure.setAttribute('role', 'alert');
  const observed = el('p', 'muted compact');
  center.replaceChildren(toolbar, scopeError, hint, content, failure, more, observed);

  function fill() {
    content.replaceChildren();
    for (const server of servers) {
      const card = el('article', 'work-card mcp-card');
      card.append(el('h2', '', server.name));
      const state = server.runtimeStatus == null ? 'Unbekannt · kein Laufzeitstatus gemeldet'
        : runtimeNames[server.runtimeStatus] ?? `Unbekannter Status: ${server.runtimeStatus}`;
      const badgeClass = server.runtimeStatus === 'connected' ? 'queued'
        : server.runtimeStatus === 'failed' || server.runtimeStatus === 'authenticationRequired' ? 'failed' : '';
      card.append(el('p', 'status-badge ' + badgeClass, state),
        el('p', 'muted compact', 'Authentifizierung: ' + (authNames[server.authStatus] ?? server.authStatus)));
      if (server.toolsError !== null) card.append(el('p', 'question-error', `Werkzeugliste nicht verfügbar: ${server.toolsError}`));
      else card.append(el('p', 'muted compact', server.toolCount == null ? 'Werkzeuganzahl nicht verfügbar.' : `${server.toolCount} Werkzeuge im gemeldeten Katalog`));
      if (server.httpOrigin) card.append(el('p', 'muted compact', 'HTTP-Ursprung: ' + server.httpOrigin));
      if (server.pluginId) card.append(el('p', 'muted compact', 'Plugin: ' + server.pluginId));
      content.append(card);
    }
    if (!servers.length) content.append(el('p', 'empty', 'Codex meldet in dieser Auswahl keine MCP-Server.'));
  }

  async function load(append = false) {
    const current = ++request;
    const args: Record<string, unknown> = {};
    if (scope.value) args.threadId = scope.value;
    if (append && cursor) args.cursor = cursor;
    failure.hidden = true; more.disabled = true;
    if (!append) {
      more.hidden = true; observed.textContent = '';
      const loading = el('p', 'muted', 'MCP-Verbindungen werden geladen …'); loading.setAttribute('role', 'status');
      content.replaceChildren(loading);
    }
    try {
      const page = await api<McpPage>('list_codex_mcp_servers', args);
      if (!isActive() || current !== request) return;
      if (!Array.isArray(page.servers)) throw new Error('Codex hat keine lesbare MCP-Liste geliefert.');
      servers = append ? [...servers, ...page.servers.filter(s => !servers.some(old => old.name === s.name))] : page.servers;
      cursor = page.nextCursor;
      fill(); more.hidden = !cursor; more.disabled = false;
      refresh.textContent = 'Aktualisieren';
      observed.textContent = 'Abgerufen: ' + new Date().toLocaleString('de-AT');
    } catch (error) {
      if (!isActive() || current !== request) return;
      if (!append) content.replaceChildren();
      failure.textContent = `MCP-Verbindungen konnten nicht geladen werden. ${error instanceof Error ? error.message : String(error)}`;
      failure.hidden = false; more.disabled = false;
      refresh.textContent = 'Erneut versuchen';
    }
  }

  async function loadThreads() {
    moreThreads.disabled = true;
    try {
      const page = await api<ThreadPage>('list_codex_threads', threadCursor ? { cursor: threadCursor } : {});
      if (!isActive()) return;
      if (!Array.isArray(page.threads)) throw new Error('Keine lesbare Thread-Liste.');
      const existing = new Set(Array.from(scope.options, option => option.value));
      for (const thread of page.threads) if (!existing.has(thread.id)) {
        scope.add(new Option(`${thread.name || 'Unbenannter Thread'} · ${thread.workspacePath} · ${thread.id}`, thread.id));
        existing.add(thread.id);
      }
      threadCursor = page.nextCursor;
      moreThreads.hidden = !threadCursor; moreThreads.disabled = false;
      scopeError.hidden = true;
    } catch (error) {
      if (!isActive()) return;
      scopeError.textContent = `Thread-Auswahl nicht verfügbar. ${error instanceof Error ? error.message : String(error)}`;
      scopeError.hidden = false; moreThreads.hidden = false; moreThreads.disabled = false;
    }
  }
  scope.onchange = () => void load();
  refresh.onclick = () => void load();
  more.onclick = () => void load(true);
  moreThreads.onclick = () => void loadThreads();
  void load();
  void loadThreads();
}
