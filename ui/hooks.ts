import type { Api } from './control-center';

interface Hook {
  key: string; eventName: string; matcher: string | null; timeoutSec: number;
  statusMessage: string | null; additionalContextLimit: number | null;
  sourcePath: string; source: string; pluginId: string | null; displayOrder: number;
  enabled: boolean; isManaged: boolean; currentHash: string; trustStatus: string;
  handlerType: string; command: string | null; async: boolean | null; server: string | null; tool: string | null;
}
interface HooksEntry { cwd: string; hooks: Hook[]; warnings: string[]; errors: { path: string; message: string }[] }
interface HooksSnapshot { data: HooksEntry[] }
const el = <T extends keyof HTMLElementTagNameMap>(tag: T, className = '', text?: string) => {
  const node = document.createElement(tag); node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
};
const trustNames: Record<string, string> = {
  managed: 'Verwaltet', trusted: 'Vertraut', untrusted: 'Nicht vertraut', modified: 'Seit der Freigabe geändert'
};

export function createHooksView(api: Api, center: HTMLElement, isActive: () => boolean) {
  let workspacePath = '';
  let generation = 0;

  return async function renderHooks() {
    const request = ++generation;
    const form = el('form', 'control-toolbar hooks-toolbar');
    const field = el('label', 'hooks-path', 'Projektpfad (optional)');
    const path = el('input'); path.type = 'text'; path.value = workspacePath;
    path.placeholder = '/absoluter/Projektpfad'; path.setAttribute('aria-label', 'Projektpfad für Hooks');
    field.append(path);
    const refresh = el('button', 'button secondary', 'Aktualisieren'); refresh.type = 'submit';
    form.onsubmit = event => { event.preventDefault(); workspacePath = path.value.trim(); void renderHooks(); };
    form.append(field, refresh);
    const note = el('p', 'muted compact', 'Ohne Projektpfad verwendet Codex sein aktuelles Arbeitsverzeichnis. Vertrauen und Freigaben verwaltest du in Codex.');
    const content = el('div', 'hooks-content');
    const loading = el('p', 'muted', 'Hooks werden geladen …'); loading.setAttribute('role', 'status');
    content.append(loading); center.replaceChildren(form, note, content);
    let snapshot: HooksSnapshot;
    try {
      snapshot = await api<HooksSnapshot>('list_codex_hooks', workspacePath ? { workspacePath } : {});
    } catch (error) {
      if (!isActive() || request !== generation) return;
      const failure = el('p', 'question-error', `Hooks konnten nicht geladen werden. ${error instanceof Error ? error.message : String(error)}`);
      failure.setAttribute('role', 'alert'); content.replaceChildren(failure);
      refresh.textContent = 'Erneut versuchen';
      return;
    }
    if (!isActive() || request !== generation) return;
    content.replaceChildren();
    if (!snapshot.data.length) content.append(el('p', 'empty', 'Codex hat keine Hook-Konfiguration gemeldet.'));
    for (const entry of snapshot.data) {
      const project = el('section', 'hooks-project');
      project.append(el('h2', 'thread-path', entry.cwd));
      for (const warning of entry.warnings) {
        const diagnostic = el('p', 'hook-warning', `Warnung: ${warning}`);
        diagnostic.setAttribute('role', 'status'); project.append(diagnostic);
      }
      for (const error of entry.errors) {
        const diagnostic = el('p', 'question-error', `Fehler in ${error.path}: ${error.message}`);
        diagnostic.setAttribute('role', 'alert'); project.append(diagnostic);
      }
      if (!entry.hooks.length) project.append(el('p', 'empty', 'Für dieses Arbeitsverzeichnis sind keine Hooks gemeldet.'));
      for (const hook of entry.hooks) {
        const card = el('article', 'work-card hook-card');
        card.append(el('h3', '', hook.eventName));
        const trust = el('span', 'status-badge hook-trust', `Vertrauen: ${trustNames[hook.trustStatus] ?? hook.trustStatus}`);
        trust.dataset.trust = hook.trustStatus;
        trust.title = `Codex trustStatus: ${hook.trustStatus}`;
        card.append(trust, el('span', 'status-badge', hook.enabled ? 'Aktiviert' : 'Deaktiviert'));
        if (hook.isManaged) card.append(el('span', 'status-badge', 'Zentral verwaltet'));
        if (hook.trustStatus === 'untrusted' || hook.trustStatus === 'modified')
          card.append(el('p', 'hook-warning', 'Vertrauensstatus in Codex prüfen. Die Aktivierung allein bestätigt keine Freigabe.'));
        card.append(el('p', 'muted compact', `Handler: ${hook.handlerType} · Quelle: ${hook.source}`));
        const source = el('p', 'thread-path', hook.sourcePath); source.title = hook.sourcePath; card.append(source);
        if (hook.pluginId) card.append(el('p', 'muted compact', `Plugin: ${hook.pluginId}`));
        if (hook.matcher !== null) card.append(el('p', '', `Matcher: ${hook.matcher}`));
        if (hook.statusMessage) card.append(el('p', '', hook.statusMessage));
        if (hook.handlerType === 'command') card.append(el('pre', 'reply-preview', hook.command ?? 'Befehl nicht verfügbar.'));
        else if (hook.handlerType === 'mcpTool') card.append(el('p', '', `MCP: ${hook.server ?? '?'} / ${hook.tool ?? '?'}`));
        const details = el('details'); details.append(el('summary', '', 'Konfiguration & Herkunft'));
        const facts = el('dl', 'provenance');
        for (const [label, value] of [
          ['Hook-Schlüssel', hook.key], ['Zeitlimit', `${hook.timeoutSec} Sekunden`],
          ['Ausführung', hook.handlerType === 'command' ? hook.async ? 'Asynchron' : 'Synchron' : hook.handlerType],
          ['Reihenfolge', String(hook.displayOrder)], ['Aktueller Hash', hook.currentHash],
          ['Kontext-Dateischwelle', hook.additionalContextLimit === null ? 'Codex-Standard' : hook.additionalContextLimit === 0 ? 'Deaktiviert' : `${hook.additionalContextLimit} Tokens`]
        ]) facts.append(el('dt', '', label), el('dd', '', value));
        details.append(facts); card.append(details); project.append(card);
      }
      content.append(project);
    }
  };
}
