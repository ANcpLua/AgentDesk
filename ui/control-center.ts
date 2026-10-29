import { workNote, workState, type Question, type DeliveryState, type WorkOwner, type WorkState, type DispatchContent } from './contracts';

export type Api = <T>(name: string, args?: Record<string, unknown>) => Promise<T>;
interface Thread {
  id: string; sessionId: string; name: string; workspacePath: string; status: string;
  activeFlags: string[]; model: string | null; reasoningEffort: string | null;
  parentThreadId: string | null; forkedFromId: string | null; transcriptPath: string | null;
  originator: string | null; agentRole: string | null; updatedAt: string;
}
interface Delivery { id: string; batchId: string; threadId: string; threadName: string; workspacePath: string; message: string; state: DeliveryState; createdAt: string }
interface Work { id: string; title: string; objective: string | null; workspacePath: string; owner: WorkOwner; questionId: string | null; state: WorkState; version: number }
interface Detail { thread: Thread; goal: string | null; goalStatus: string | null; latestTurnId: string | null; latestTurnStatus: string | null; latestReply: string | null }
interface UsageWindow { usedPercent: number; windowDurationMins: number | null; resetsAt: number | null }
interface UsageBucket { limitId: string | null; limitName: string | null; primary: UsageWindow | null; secondary: UsageWindow | null; credits: { hasCredits: boolean; unlimited: boolean; balance: string | null } | null }
interface Usage { ordinaryUsageAllowed: boolean | null; rateLimits: UsageBucket | null; rateLimitsByLimitId: Record<string, UsageBucket> | null }
type DispatchQuestion = Question;
const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const el = <T extends keyof HTMLElementTagNameMap>(tag: T, className = '', text?: string) => { const n = document.createElement(tag); n.className = className; if (text !== undefined) n.textContent = text; return n; };
const shortPath = (path: string) => path.replace(/^\/Users\/[^/]+/, '~');
const stateName = (status: string) => ({ active: 'Arbeitet', idle: 'Bereit', notLoaded: 'Inaktiv', systemError: 'Fehler' }[status] ?? status);
const deliveryNames: Record<DeliveryState['kind'], string> = { sending: 'Wird gesendet', queued: 'Von Codex angenommen', failed: 'Fehlgeschlagen', uncertain: 'Zustellung unklar' };
const deliveryName = (state: DeliveryState) => deliveryNames[state.kind];
const deliveryReason = (state: DeliveryState) => state.kind === 'failed' || state.kind === 'uncertain' ? state.reason : null;
function usageDuration(minutes: number): string {
  if (!Number.isSafeInteger(minutes) || minutes <= 0) return `${minutes.toLocaleString('de-AT')} Minuten`;
  const parts: string[] = [];
  for (const [unit, singular, plural] of [[1440, 'Tag', 'Tage'], [60, 'Stunde', 'Stunden'], [1, 'Minute', 'Minuten']] as const) {
    const amount = Math.floor(minutes / unit);
    if (amount) parts.push(`${amount.toLocaleString('de-AT')} ${amount === 1 ? singular : plural}`);
    minutes %= unit;
  }
  return parts.join(' ');
}
function usageCredits(balance: string): string {
  const value = Number(balance);
  if (!balance.trim() || !Number.isFinite(value)) return balance;
  const rounded = Number(value.toFixed(2)) !== value;
  return `${rounded ? '≈ ' : ''}${value.toLocaleString('de-AT', { maximumFractionDigits: 2 })}`;
}

export function startControlCenter(api: Api, report: (error: unknown) => void) {
  let threads: Thread[] = [];
  let nextCursor: string | null = null;
  let view = 'questions';
  let filter = '';
  let selected = new Set<string>();
  let question: DispatchQuestion | null = null;
  let dispatchKey = '';
  let lastSendNote = '';
  let usageRequest = 0;
  const center = $('control-center');

  function setView(next: string) {
    view = next; document.body.dataset.view = view;
    $('project').hidden = view !== 'questions';
    document.querySelector<HTMLLabelElement>('label[for=project]')!.hidden = view !== 'questions';
    $('questions').hidden = view !== 'questions'; center.hidden = view === 'questions';
    document.querySelectorAll('[data-view]').forEach(n => n.classList.toggle('active', (n as HTMLElement).dataset.view === view));
    $('new-question').hidden = view !== 'questions';
    if (view === 'questions') return;
    document.querySelectorAll('[data-filter]').forEach(n => n.classList.remove('active'));
    $('heading').textContent = view === 'agents' ? 'Deine Codex-Threads' : view === 'usage' ? 'Codex-Nutzung' : 'Gemeinsam weiterkommen';
    document.querySelector('.intro')!.textContent = view === 'agents'
      ? 'Bestehende Threads gezielt erreichen. Eine Nachricht, mehrere Empfänger.'
      : view === 'usage' ? 'Aktuelle Kontingente deines Codex-Kontos.' : 'Nächste Schritte, Zuständigkeiten und Ergebnisse bleiben nachvollziehbar.';
    if (view === 'agents') void loadThreads().catch(report); else if (view === 'usage') void renderUsage().catch(report); else void renderWork().catch(report);
  }

  async function renderUsage() {
    const request = ++usageRequest;
    const refresh = el('button', 'button secondary', 'Aktualisieren');
    refresh.onclick = () => void renderUsage().catch(report);
    const content = el('div');
    const loading = el('p', 'muted', 'Nutzung wird geladen …'); loading.setAttribute('role', 'status');
    content.append(loading);
    center.replaceChildren(refresh, content);
    let usage: Usage;
    try {
      usage = await api<Usage>('read_codex_usage');
    } catch (error) {
      if (view !== 'usage' || request !== usageRequest) return;
      const failure = el('p', 'question-error', `Nutzung konnte nicht geladen werden. ${error instanceof Error ? error.message : String(error)}`);
      failure.setAttribute('role', 'alert');
      content.replaceChildren(failure);
      refresh.textContent = 'Erneut versuchen';
      return;
    }
    if (view !== 'usage' || request !== usageRequest) return;
    content.replaceChildren();
    const buckets = Object.entries(usage.rateLimitsByLimitId ?? {});
    if (!buckets.length && usage.rateLimits) buckets.push([usage.rateLimits.limitId ?? 'Codex', usage.rateLimits]);
    if (!buckets.length) content.append(el('p', 'muted', 'Keine Nutzungsdaten verfügbar.'));
    for (const [key, bucket] of buckets) {
      const card = el('article', 'work-card');
      card.append(el('h2', '', bucket.limitName ?? bucket.limitId ?? key));
      for (const [label, window] of [['Primäres Kontingent', bucket.primary], ['Sekundäres Kontingent', bucket.secondary]] as const) {
        if (!window) continue;
        const remaining = Math.max(0, Math.min(100, 100 - window.usedPercent));
        const period = window.windowDurationMins == null ? '' : ` · ${usageDuration(window.windowDurationMins)}`;
        card.append(el('h3', '', label + period), el('p', '', `${remaining.toLocaleString('de-AT')} % verbleibend`));
        if (window.resetsAt != null) card.append(el('p', 'muted compact', `Zurücksetzung: ${new Date(window.resetsAt * 1000).toLocaleString('de-AT')}`));
      }
      if (!bucket.primary && !bucket.secondary) card.append(el('p', 'muted', 'Kein Kontingent verfügbar.'));
      if (bucket.credits?.unlimited) card.append(el('p', 'muted compact', 'Credits: unbegrenzt'));
      else if (bucket.credits?.balance != null) {
        const credits = el('p', 'muted compact', `Credits: ${usageCredits(bucket.credits.balance)}`);
        credits.title = `Gemeldeter Wert: ${bucket.credits.balance}`;
        card.append(credits);
      }
      content.append(card);
    }
    if (usage.ordinaryUsageAllowed === false) content.append(el('p', 'muted', 'Die enthaltene Nutzung ist derzeit nicht verfügbar.'));
  }

  async function loadThreads(append = false) {
    const page = await api<{ threads: Thread[]; nextCursor: string | null }>('list_codex_threads', append && nextCursor ? { cursor: nextCursor } : {});
    if (!Array.isArray(page.threads)) throw new Error('Codex hat keine lesbare Thread-Liste geliefert.');
    threads = append ? [...threads, ...page.threads.filter(t => !threads.some(old => old.id === t.id))] : page.threads;
    nextCursor = page.nextCursor;
    if (view === 'agents') renderThreads();
    return threads;
  }

  function renderThreads() {
    center.replaceChildren();
    if (lastSendNote) center.append(el('p', 'send-success', lastSendNote));
    const toolbar = el('div', 'control-toolbar');
    const search = el('input'); search.type = 'search'; search.placeholder = 'Name, Projektpfad oder Thread-ID suchen'; search.value = filter;
    search.setAttribute('aria-label', 'Threads durchsuchen');
    const refresh = el('button', 'button secondary', 'Aktualisieren'); refresh.onclick = () => void loadThreads().catch(report);
    const send = el('button', 'button primary', `Nachricht an ${selected.size} Threads`); send.disabled = !selected.size;
    send.onclick = () => void openDispatch().catch(report);
    toolbar.append(search, refresh, send); center.append(toolbar);
    const list = el('div', 'thread-list'); center.append(list);
    const fillList = () => {
      list.replaceChildren();
      const matches = threads.filter(t => `${t.name} ${t.workspacePath} ${t.id}`.toLowerCase().includes(filter.toLowerCase()));
      for (const t of matches) {
        const card = el('article', 'thread-card');
        const choice = el('label', 'thread-select');
        const check = el('input'); check.type = 'checkbox'; check.checked = selected.has(t.id); check.setAttribute('aria-label', `Thread auswählen: ${t.name}`);
        check.onchange = () => {
          if (check.checked) { if (selected.size >= 8) { check.checked = false; report(new Error('Maximal 8 Empfänger pro Nachricht.')); return; } selected.add(t.id); }
          else selected.delete(t.id);
          send.textContent = `Nachricht an ${selected.size} Threads`; send.disabled = !selected.size;
        };
        choice.append(check, el('strong', '', t.name || 'Unbenannter Thread'), el('span', 'status-badge ' + t.status, stateName(t.status)));
        const path = el('p', 'thread-path', shortPath(t.workspacePath)); path.title = t.workspacePath;
        const meta = el('p', 'muted compact', `${t.model ?? 'Modell unbekannt'}${t.reasoningEffort ? ' · ' + t.reasoningEffort : ''} · ${t.id}`);
        const details = el('details'); const summary = el('summary', '', 'Herkunft & letzte Aktivität'); const body = el('div', 'thread-detail');
        details.append(summary, body);
        details.ontoggle = () => { if (details.open && !body.childNodes.length) void fillDetail(t, body).catch(report); };
        card.append(choice, path, meta, details); list.append(card);
      }
      if (!matches.length) list.append(el('p', 'empty', 'Keine passenden Threads auf dieser Seite.'));
    };
    search.oninput = () => { filter = search.value; fillList(); };
    fillList();
    if (nextCursor) { const more = el('button', 'button secondary', 'Weitere Threads laden'); more.onclick = () => void loadThreads(true).catch(report); center.append(more); }
    const receipts = el('details', 'receipts'); receipts.append(el('summary', '', 'Gesendete Nachrichten & Zustellstatus'));
    receipts.ontoggle = () => { if (receipts.open) void renderReceipts(receipts).catch(report); };
    center.append(receipts);
  }

  async function fillDetail(t: Thread, body: HTMLElement) {
    body.append(el('p', 'muted', 'Lade gezielte Zusammenfassung …'));
    const detail = await api<Detail>('read_codex_thread', { threadId: t.id });
    body.replaceChildren();
    const facts = [
      ['Thread', t.id], ['Session', t.sessionId], ['Projekt', t.workspacePath],
      ['Ursprung', t.originator], ['Parent', t.parentThreadId], ['Fork von', t.forkedFromId], ['Verlauf (Referenz)', t.transcriptPath]
    ];
    const dl = el('dl', 'provenance');
    for (const [label, value] of facts) if (value) dl.append(el('dt', '', label!), el('dd', '', value));
    body.append(dl);
    if (detail.goal) body.append(el('p', 'goal', `Codex-Ziel (${detail.goalStatus ?? 'unbekannt'}): ${detail.goal}`));
    body.append(el('h3', '', 'Letzte Antwort im Thread'), el('pre', 'reply-preview', detail.latestReply ?? 'Noch keine Antwort verfügbar.'));
    body.append(el('p', 'muted compact', 'Diese Aktivität ist keine Empfangsbestätigung für eine bestimmte Postfach-Nachricht.'));
    const copy = el('button', 'link-button', 'Resume-Befehl kopieren');
    copy.onclick = async () => { try { await navigator.clipboard.writeText('codex resume ' + t.id); copy.textContent = 'Kopiert'; } catch (error) { report(error); } };
    body.append(copy);
  }

  async function renderReceipts(container: HTMLElement) {
    container.querySelector('.receipt-list')?.remove();
    const list = el('div', 'receipt-list'); container.append(list);
    const result = await api<{ deliveries: Delivery[] }>('list_dispatches');
    for (const d of result.deliveries) {
      const row = el('article', 'receipt');
      row.append(el('strong', '', d.threadName), el('span', 'status-badge ' + d.state.kind, deliveryName(d.state)), el('p', 'muted compact', d.threadId));
      const reason = deliveryReason(d.state);
      if (reason) row.append(el('p', 'question-error', reason));
      const more = el('details'); more.append(el('summary', '', 'Gesendeten Inhalt ansehen'), el('pre', 'reply-preview', d.message));
      row.append(more); list.append(row);
    }
    if (!result.deliveries.length) list.append(el('p', 'muted', 'Noch keine Nachrichten aus diesem Postfach gesendet.'));
  }

  async function openDispatch(q: DispatchQuestion | null = null) {
    question = q;
    dispatchKey = crypto.randomUUID();
    $('dispatch-error').textContent = '';
    $<HTMLTextAreaElement>('dispatch-message').value = '';
    const preview = $('dispatch-preview'); preview.hidden = !q;
    const answer = q?.state.kind === 'answered' ? q.state.text : null;
    const suggestion = q?.choices.find(s => s.label === answer);
    const followUp = suggestion?.followUpPrompt ?? q?.followUpPrompt;
    preview.textContent = q ? `${q.prompt}\n\nDeine Antwort: ${answer}${q.goal ? '\nZiel: ' + q.goal : ''}${followUp ? '\nFolgeprompt: ' + followUp : ''}` : '';
    const dialog = $<HTMLDialogElement>('dispatch-dialog'); dialog.showModal();
    const targets = $('dispatch-targets'); targets.textContent = 'Lade native Threads …';
    const available = await loadThreads(); targets.replaceChildren();
    const chosen = q?.origin ? new Set([q.origin.threadId]) : new Set(selected);
    for (const t of available) {
      const row = el('label', 'recipient'); const check = el('input'); check.type = 'checkbox'; check.value = t.id; check.checked = chosen.has(t.id);
      const text = el('span'); text.append(el('strong', '', t.name), el('small', '', `${shortPath(t.workspacePath)} · ${t.id}`));
      row.append(check, text, el('span', 'status-badge', stateName(t.status))); targets.append(row);
    }
  }
  $('close-dispatch').onclick = () => $<HTMLDialogElement>('dispatch-dialog').close();
  $<HTMLFormElement>('dispatch-form').onsubmit = async event => {
    event.preventDefault(); const button = $<HTMLButtonElement>('dispatch-submit'); button.disabled = true;
    try {
      const threadIds = [...$('dispatch-targets').querySelectorAll<HTMLInputElement>('input:checked')].map(n => n.value);
      if (!threadIds.length || threadIds.length > 8) throw new Error('Bitte 1 bis 8 Empfänger auswählen.');
      const text = $<HTMLTextAreaElement>('dispatch-message').value;
      const content: DispatchContent = question ? { kind: 'answer', questionId: question.id, note: text } : { kind: 'message', text };
      const result = await api<{ deliveries: Delivery[] }>('dispatch_to_codex', { requestKey: dispatchKey, threadIds, content });
      const failed = result.deliveries.filter(d => d.state.kind !== 'queued');
      if (failed.length) { $('dispatch-error').textContent = result.deliveries.map(d => `${d.threadName}: ${deliveryName(d.state)}${deliveryReason(d.state) ? ' — ' + deliveryReason(d.state) : ''}`).join('\n'); }
      else {
        $<HTMLDialogElement>('dispatch-dialog').close();
        selected.clear();
        lastSendNote = `Von Codex angenommen: ${result.deliveries.length} Nachricht(en). Die Agentenantworten findest du in den jeweiligen Threads.`;
        setView('agents');
      }
    } catch (error) { $('dispatch-error').textContent = error instanceof Error ? error.message : String(error); }
    finally { button.disabled = false; }
  };

  async function prepareOriginSelect() {
    const values = await loadThreads();
    for (const id of ['origin-thread', 'work-thread']) {
      const node = $<HTMLSelectElement>(id); const old = node.value;
      node.replaceChildren(new Option('Ohne Thread-Zuordnung', ''));
      for (const t of values) node.add(new Option(`${t.name} · ${shortPath(t.workspacePath)} · ${t.id}`, t.id));
      node.value = old;
    }
  }

  async function renderWork() {
    const { items } = await api<{ items: Work[] }>('list_work_items'); center.replaceChildren();
    const toolbar = el('div', 'control-toolbar'); const add = el('button', 'button primary', '+ Aufgabe festhalten');
    const refresh = el('button', 'button secondary', 'Aktualisieren'); refresh.onclick = () => void renderWork().catch(report);
    add.onclick = async () => {
      $<HTMLDialogElement>('work-dialog').showModal();
      try {
        await prepareOriginSelect();
        if (!$<HTMLInputElement>('work-path').value && threads[0]) $<HTMLInputElement>('work-path').value = threads[0].workspacePath;
      } catch (error) { report(error); }
    };
    toolbar.append(add, refresh); center.append(toolbar);
    const board = el('div', 'work-board');
    for (const owner of ['operator', 'agent']) {
      const column = el('section', 'work-column'); column.append(el('h2', '', owner === 'operator' ? 'Du · Operator' : 'Codex · Agent'));
      for (const item of items.filter(x => x.owner.kind === owner)) {
        const card = el('article', 'work-card'); card.append(el('h3', '', item.title), el('p', 'thread-path', shortPath(item.workspacePath)));
        if (item.objective) card.append(el('p', 'context', item.objective));
        if (item.owner.kind === 'agent') card.append(el('p', 'muted compact', item.owner.threadId));
        const select = el('select'); select.setAttribute('aria-label', `Status: ${item.title}`);
        for (const [value, label] of [['todo', 'Offen'], ['in_progress', 'In Arbeit'], ['blocked', 'Wartet'], ['done', 'Erledigt']]) select.add(new Option(label, value));
        select.value = item.state.kind;
        const evidence = el('textarea'); evidence.placeholder = 'Ergebnis oder nächstes Hindernis festhalten …'; evidence.value = workNote(item.state); evidence.maxLength = 8000;
        const save = el('button', 'button secondary', 'Fortschritt speichern');
        save.onclick = async () => {
          save.disabled = true;
          try { await api('update_work_item', { id: item.id, expectedVersion: item.version, state: workState(select.value, evidence.value) }); await renderWork(); }
          catch (error) { report(error); save.disabled = false; }
        };
        card.append(select, evidence, save); column.append(card);
      }
      if (!items.some(x => x.owner.kind === owner)) column.append(el('p', 'muted', 'Noch keine Aufgaben.'));
      board.append(column);
    }
    center.append(board);
  }
  $('close-work').onclick = () => $<HTMLDialogElement>('work-dialog').close();
  $<HTMLSelectElement>('work-thread').onchange = () => {
    const t = threads.find(t => t.id === $<HTMLSelectElement>('work-thread').value);
    if (t) $<HTMLInputElement>('work-path').value = t.workspacePath;
  };
  $<HTMLFormElement>('work-form').onsubmit = async event => {
    event.preventDefault(); const button = $('work-form').querySelector<HTMLButtonElement>('[type=submit]')!; button.disabled = true;
    try {
      const owner: WorkOwner = $<HTMLSelectElement>('work-owner').value === 'agent'
        ? { kind: 'agent', threadId: $<HTMLSelectElement>('work-thread').value } : { kind: 'operator' };
      await api('create_work_item', { title: $<HTMLInputElement>('work-title').value,
        objective: $<HTMLTextAreaElement>('work-objective').value, workspacePath: $<HTMLInputElement>('work-path').value,
        owner });
      $<HTMLDialogElement>('work-dialog').close(); $<HTMLFormElement>('work-form').reset(); await renderWork();
    } catch (error) { $('work-error').textContent = error instanceof Error ? error.message : String(error); }
    finally { button.disabled = false; }
  };
  document.querySelectorAll<HTMLButtonElement>('[data-view]').forEach(button => button.onclick = () => setView(button.dataset.view!));
  document.querySelectorAll<HTMLButtonElement>('[data-filter]').forEach(button => button.addEventListener('click', () => setView('questions')));
  return { openDispatch, prepareOriginSelect, showQuestions: () => setView('questions') };
}
