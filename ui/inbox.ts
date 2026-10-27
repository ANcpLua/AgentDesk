import { App, applyDocumentTheme, applyHostStyleVariables } from '@modelcontextprotocol/ext-apps';
import { startControlCenter } from './control-center';
import type { Question, QuestionDecision } from './contracts';
interface Snapshot { revision: number; questions: Question[] }
type Filter = 'pending' | 'answered' | 'all';
const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const embedded = (window as Window & { __MCP_INBOX__?: boolean }).__MCP_INBOX__ === true;
let host: App | undefined;
let snapshot: Snapshot = { revision: -1, questions: [] };
let filter: Filter = 'pending';
let opened = new Set<string>();
const drafts = new Map<string, { selected: string; text: string }>();
const submitting = new Set<string>();
let controls: ReturnType<typeof startControlCenter>;
const date = (value: string) => new Intl.DateTimeFormat('de-AT', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' }).format(new Date(value));
const element = <T extends keyof HTMLElementTagNameMap>(tag: T, className = '', text?: string) => {
  const node = document.createElement(tag); node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
};
function report(error: unknown) { const node = $('error'); node.textContent = error instanceof Error ? error.message : String(error); node.hidden = false; }
function clearError() { $('error').hidden = true; }

async function call<T>(name: string, args: Record<string, unknown> = {}): Promise<T> {
  if (host) {
    const result = await host.callServerTool({ name, arguments: args });
    const data = result.structuredContent ?? JSON.parse((result.content?.find(c => c.type === 'text') as { text: string })?.text ?? '{}');
    if (result.isError) throw new Error((data as { error?: string }).error ?? 'Die Anfrage ist fehlgeschlagen.');
    return data as T;
  }
  let path = '/api/questions/'; let method = 'GET'; let body: unknown;
  if (name === 'ask_question') { method = 'POST'; body = args; }
  if (name === 'get_question') path += encodeURIComponent(String(args.id));
  if (name === 'submit_question_answer') {
    path += encodeURIComponent(String(args.id)) + '/answer'; method = 'POST';
    body = { decision: args.decision, expectedVersion: args.expectedVersion };
  }
  if (name === 'list_codex_threads') {
    const params = new URLSearchParams();
    if (args.workspacePath) params.set('workspacePath', String(args.workspacePath));
    if (args.cursor) params.set('cursor', String(args.cursor));
    path = '/api/codex/threads?' + params;
  }
  if (name === 'read_codex_thread') path = '/api/codex/threads/' + encodeURIComponent(String(args.threadId));
  if (name === 'dispatch_to_codex') { path = '/api/codex/dispatch'; method = 'POST'; body = args; }
  if (name === 'list_dispatches') path = '/api/codex/dispatch';
  if (name === 'list_work_items') path = '/api/work-items/' + (args.workspacePath ? '?workspacePath=' + encodeURIComponent(String(args.workspacePath)) : '');
  if (name === 'create_work_item') { path = '/api/work-items/'; method = 'POST'; body = args; }
  if (name === 'update_work_item') { path = '/api/work-items/' + encodeURIComponent(String(args.id)); method = 'PUT'; body = args; }
  const response = await fetch(path, {
    method, headers: { 'Content-Type': 'application/json', 'X-Question-Inbox': '1' },
    ...(body !== undefined ? { body: JSON.stringify(body) } : {})
  });
  const data = await response.json();
  if (!response.ok) throw new Error(data.title ?? 'Die Anfrage ist fehlgeschlagen.');
  return data as T;
}

function receive(next: Snapshot) {
  if (next.revision < snapshot.revision) return;
  if (next.revision === snapshot.revision) return;
  snapshot = next;
  const selectedProject = $<HTMLSelectElement>('project').value;
  const select = $<HTMLSelectElement>('project');
  select.replaceChildren(new Option('Alle Projekte', ''));
  for (const project of [...new Set(next.questions.map(q => q.project))].sort()) select.add(new Option(project, project));
  select.value = selectedProject;
  if (!opened.size) { const first = next.questions.find(q => q.state.kind === 'pending'); if (first) opened.add(first.id); }
  render();
}

function render() {
  const active = document.activeElement;
  const editing = active instanceof HTMLTextAreaElement && active.id.startsWith('reply-')
    ? { id: active.id, start: active.selectionStart, end: active.selectionEnd } : null;
  const project = $<HTMLSelectElement>('project').value;
  const scoped = snapshot.questions.filter(q => !project || q.project === project);
  $('pending-count').textContent = String(scoped.filter(q => q.state.kind === 'pending').length);
  $('answered-count').textContent = String(scoped.filter(q => q.state.kind === 'answered').length);
  $('all-count').textContent = String(scoped.length);
  if (!document.body.dataset.view || document.body.dataset.view === 'questions') {
    $('heading').textContent = { pending: 'Offene Fragen', answered: 'Deine Antworten', all: 'Fragenverlauf' }[filter];
    document.querySelector('.intro')!.textContent = 'Deine Antworten warten auf dich. Nicht umgekehrt.';
  }
  document.querySelectorAll<HTMLButtonElement>('[data-filter]').forEach(b => b.classList.toggle('active', b.dataset.filter === filter));
  const container = $('questions'); container.replaceChildren();
  const visible = scoped.filter(q => filter === 'all' || q.state.kind === filter);
  if (!visible.length) {
    const empty = element('div', 'empty');
    empty.append(element('div', 'empty-icon', '✓'), element('h2', '', filter === 'pending' ? 'Alles beantwortet. Raum für Neues.' : 'Hier ist noch nichts.'));
    empty.append(element('p', '', filter === 'pending' ? 'Sobald dein Agent eine Frage stellt, erscheint sie hier. Du kannst den Ablauf auch mit einer Testfrage ausprobieren.' : 'Beantwortete und zurückgezogene Fragen bleiben im Verlauf erhalten.'));
    container.append(empty); return;
  }
  for (const q of visible) {
    const card = element('article', 'question'); card.dataset.id = q.id;
    const head = element('button', 'question-head'); head.type = 'button'; head.setAttribute('aria-expanded', String(opened.has(q.id)));
    const meta = element('div', 'meta');
    const projectLabel = element('span', 'project-tag', q.project.replace(/^\/Users\/[^/]+/, '~')); projectLabel.title = q.project;
    meta.append(projectLabel, element('span', '', q.source), element('span', '', '· ' + date(q.createdAt)));
    if (q.blocking && q.state.kind === 'pending') meta.append(element('span', 'blocking', '· Antwort benötigt'));
    if (q.state.kind !== 'pending') meta.append(element('span', '', q.state.kind === 'answered' ? '· Beantwortet' : '· Zurückgezogen'));
    const title = element('div', 'question-title'); title.append(element('span', '', q.prompt), element('span', 'chevron', opened.has(q.id) ? '−' : '+'));
    head.append(meta, title); head.onclick = () => { if (opened.has(q.id)) opened.delete(q.id); else opened.add(q.id); render(); };
    const body = element('div', 'question-body'); body.hidden = !opened.has(q.id);
    if (q.context) body.append(element('p', 'context', q.context));
    if (q.goal) body.append(element('p', 'goal', 'Ziel: ' + q.goal));
    if (q.origin) {
      const origin = element('details', 'question-origin'); origin.append(element('summary', '', 'Verifizierte Herkunft · ' + q.origin.threadName));
      const dl = element('dl', 'provenance');
      for (const [label, value] of [['Projektpfad', q.origin.workspacePath], ['Thread', q.origin.threadId], ['Session', q.origin.sessionId], ['Parent', q.origin.parentThreadId], ['Fork von', q.origin.forkedFromId], ['Verlauf', q.origin.transcriptPath]])
        if (value) dl.append(element('dt', '', label!), element('dd', '', value));
      origin.append(dl); body.append(origin);
    }
    if (q.state.kind === 'pending') renderAnswerForm(q, body);
    else {
      const answer = q.state.kind === 'answered' ? q.state.text : null;
      body.append(element('div', 'reply-label', date(q.state.at)));
      body.append(element('div', 'answer', answer ?? 'Diese Frage wurde zurückgezogen.'));
      if (answer) {
        const send = element('button', 'button primary send-answer', 'Antwort an Codex senden');
        send.onclick = () => void controls.openDispatch(q).catch(report); body.append(send);
        const forward = element('button', 'link-button', embedded ? 'Antwort an den Chat übergeben ↗' : 'Antwort für den Chat kopieren ↗');
        forward.onclick = async () => {
          const message = `Meine Antwort auf die Frage „${q.prompt}“ (${q.id}, Projekt ${q.project}${q.conversationId ? ', Auftrag ' + q.conversationId : ''}): ${answer}. Bitte berücksichtige sie bei der Fortsetzung.`;
          try {
            if (host) {
              const result = await host.sendMessage({ role: 'user', content: [{ type: 'text', text: message }] });
              if (result.isError) throw new Error('Der Host konnte die Nachricht nicht übernehmen.');
              forward.textContent = 'An den Chat übergeben';
            } else { await navigator.clipboard.writeText(message); forward.textContent = 'Kopiert — im passenden Chat einfügen'; }
          } catch (error) { report(error); }
        };
        body.append(forward);
      }
    }
    card.append(head, body); container.append(card);
  }
  if (editing) {
    const input = document.getElementById(editing.id) as HTMLTextAreaElement | null;
    input?.focus({ preventScroll: true }); input?.setSelectionRange(editing.start, editing.end);
  }
}

function renderAnswerForm(q: Question, body: HTMLElement) {
  const form = element('form'); const draft = drafts.get(q.id) ?? { selected: '', text: '' }; drafts.set(q.id, draft);
  const choices = element('fieldset', 'choices'); const legend = element('legend', 'visually-hidden', 'Antwort auswählen'); choices.append(legend);
  for (const suggestion of q.choices) {
    const option = suggestion.label;
    const label = element('label', 'choice'); const radio = element('input'); radio.type = 'radio'; radio.name = 'answer-' + q.id; radio.value = option; radio.checked = draft.selected === option;
    radio.onchange = () => { draft.selected = option; draft.text = ''; if (textarea) textarea.value = ''; };
    const content = element('span'); content.append(element('span', '', option));
    if (suggestion.impact) {
      content.append(element('small', 'impact', 'Einschätzung: ' + suggestion.impact));
      if (suggestion.followUpPrompt) {
        const details = element('details', 'suggestion-prompt'); details.append(element('summary', '', 'Vorgeschlagenen Folgeprompt ansehen'), element('p', '', suggestion.followUpPrompt));
        content.append(details);
      }
    }
    label.append(radio, content); choices.append(label);
  }
  form.append(choices);
  let textarea: HTMLTextAreaElement | undefined;
  if (q.allowFreeText) {
    const label = element('label', 'free-label', q.choices.length ? 'Oder deine eigene Antwort' : 'Deine Antwort'); label.htmlFor = 'reply-' + q.id;
    textarea = element('textarea'); textarea.id = label.htmlFor; textarea.maxLength = 8000; textarea.value = draft.text; textarea.placeholder = 'Nimm dir Zeit für deine Antwort …';
    textarea.oninput = () => { draft.text = textarea!.value; if (draft.text) { draft.selected = ''; choices.querySelectorAll<HTMLInputElement>('input').forEach(r => r.checked = false); } };
    form.append(label, textarea);
  }
  const error = element('p', 'question-error'); error.setAttribute('role', 'alert');
  const actions = element('div', 'actions'); const dismiss = element('button', 'dismiss', 'Frage zurückziehen'); dismiss.type = 'button';
  const submit = element('button', 'button primary', 'Antwort speichern'); submit.type = 'submit';
  dismiss.disabled = submit.disabled = submitting.has(q.id);
  async function save(isDismiss: boolean) {
    const answer = draft.text.trim() || draft.selected;
    if (!isDismiss && !answer) { error.textContent = 'Bitte wähle eine Option oder schreibe eine Antwort.'; return; }
    if (submitting.has(q.id)) return;
    submitting.add(q.id); dismiss.disabled = submit.disabled = true; error.textContent = '';
    try {
      const decision: QuestionDecision = isDismiss ? { kind: 'dismiss' } : { kind: 'answer', text: answer };
      await call('submit_question_answer', { id: q.id, decision, expectedVersion: q.version });
      drafts.delete(q.id); clearError(); await refresh();
    } catch (e) { error.textContent = e instanceof Error ? e.message : String(e); }
    finally { submitting.delete(q.id); dismiss.disabled = submit.disabled = false; }
  }
  form.onsubmit = event => { event.preventDefault(); void save(false); };
  dismiss.onclick = () => { if (confirm('Diese Frage zurückziehen? Sie bleibt im Verlauf erhalten.')) void save(true); };
  actions.append(dismiss, submit); form.append(error, actions); body.append(form);
}

async function refresh() { receive(await call<Snapshot>('list_questions')); }

async function start() {
  controls = startControlCenter(call, report);
  document.querySelectorAll<HTMLButtonElement>('[data-filter]').forEach(button => button.onclick = () => { controls.showQuestions(); filter = button.dataset.filter as Filter; render(); });
  $('project').onchange = render;
  const dialog = $<HTMLDialogElement>('create-dialog');
  $('new-question').onclick = () => { dialog.showModal(); void controls.prepareOriginSelect().catch(report); }; $('close-dialog').onclick = () => dialog.close();
  $<HTMLFormElement>('create-form').onsubmit = async event => {
    event.preventDefault(); const button = dialog.querySelector<HTMLButtonElement>('[type=submit]')!; button.disabled = true;
    try {
      const q = await call<Question>('ask_question', {
        prompt: $<HTMLTextAreaElement>('prompt').value, project: $<HTMLInputElement>('new-project').value,
        choices: $<HTMLTextAreaElement>('options').value.split('\n').map(x => x.trim()).filter(Boolean).map(label => ({ label })),
        allowFreeText: true, blocking: $<HTMLInputElement>('blocking').checked, source: 'Du · Testfrage', requestKey: crypto.randomUUID(),
        originThreadId: $<HTMLSelectElement>('origin-thread').value || null
      });
      opened.add(q.id); filter = 'pending'; $<HTMLSelectElement>('project').value = ''; dialog.close();
      $<HTMLFormElement>('create-form').reset(); await refresh();
    } catch (error) { $('create-error').textContent = error instanceof Error ? error.message : String(error); }
    finally { button.disabled = false; }
  };
  if (embedded) {
    host = new App({ name: 'AgentDesk', version: '0.3.0' });
    host.ontoolresult = result => { const data = result.structuredContent as unknown as Snapshot; if (data?.questions) receive(data); };
    host.onhostcontextchanged = context => {
      if (context.theme) applyDocumentTheme(context.theme);
      if (context.styles?.variables) applyHostStyleVariables(context.styles.variables);
    };
    await host.connect(); $('connection').textContent = 'Mit ChatGPT verbunden';
    await refresh();
    // The app surface, not the model, refreshes its own view. No agent turn is held open.
    const interval = window.setInterval(() => { if (!document.hidden) void refresh().catch(report); }, 5000);
    host.onteardown = async () => { window.clearInterval(interval); return {}; };
  } else {
    const stream = new EventSource('/api/questions/stream');
    stream.onopen = () => { $('connection').textContent = 'Live verbunden'; clearError(); };
    stream.onerror = () => { $('connection').textContent = 'Verbinde erneut …'; };
    stream.addEventListener('inbox', event => { receive(JSON.parse((event as MessageEvent).data)); });
    await refresh();
    window.addEventListener('pagehide', () => stream.close(), { once: true });
  }
}
// Bundled inline in MCP resources and deferred as an external script in the browser.
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', () => void start().catch(report), { once: true });
else void start().catch(report);
