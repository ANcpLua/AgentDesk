import type { Api } from './control-center';

interface TokenSummary {
  lifetimeTokens: string | null;
  peakDailyTokens: string | null;
  longestRunningTurnSec: string | null;
  currentStreakDays: string | null;
  longestStreakDays: string | null;
}
interface TokenActivity {
  summary: TokenSummary;
  dailyUsageBuckets: { startDate: string; tokens: string }[] | null;
}

const el = <T extends keyof HTMLElementTagNameMap>(tag: T, className = '', text?: string) => {
  const node = document.createElement(tag); node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
};
const counter = (value: string | null): string => value === null ? 'Nicht verfügbar' : BigInt(value).toLocaleString('de-AT');
const reportedDate = (value: string): string => /^\d{4}-\d{2}-\d{2}$/.test(value)
  ? `${value.slice(8, 10)}.${value.slice(5, 7)}.${value.slice(0, 4)}` : value;

// Own loading/error state: a failed allowance read must not hide token measurements.
export async function renderTokenActivity(api: Api, container: HTMLElement, isCurrent: () => boolean): Promise<void> {
  const heading = el('h2', '', 'Tägliche Token-Aktivität');
  const explanation = el('p', 'muted compact', 'Kontoweite Aktivität aus Codex. Verbleibende Kontingente werden separat angezeigt.');
  const content = el('div');
  const loading = el('p', 'muted', 'Token-Aktivität wird geladen …'); loading.setAttribute('role', 'status');
  content.append(loading); container.replaceChildren(heading, explanation, content);
  try {
    const activity = await api<TokenActivity>('read_codex_token_activity');
    if (!isCurrent()) return;
    const summary = el('dl', 'token-summary');
    for (const [label, value, unit] of [
      ['Token insgesamt', activity.summary.lifetimeTokens, ''],
      ['Höchster Tageswert', activity.summary.peakDailyTokens, ''],
      ['Längster Turn', activity.summary.longestRunningTurnSec, ' Sekunden'],
      ['Aktuelle Serie', activity.summary.currentStreakDays, ' Tage'],
      ['Längste Serie', activity.summary.longestStreakDays, ' Tage']
    ] as const) {
      summary.append(el('dt', '', label), el('dd', '', counter(value) + (value === null ? '' : unit)));
    }
    content.replaceChildren(summary);
    if (activity.dailyUsageBuckets === null) {
      content.append(el('p', 'muted', 'Tägliche Token-Daten sind nicht verfügbar.'));
      return;
    }
    if (!activity.dailyUsageBuckets.length) {
      content.append(el('p', 'muted', 'Keine Tage mit Token-Aktivität gemeldet.'));
      return;
    }
    const days = [...activity.dailyUsageBuckets].sort((a, b) => b.startDate.localeCompare(a.startDate));
    const max = days.reduce((peak, day) => BigInt(day.tokens) > peak ? BigInt(day.tokens) : peak, 0n);
    const table = el('table', 'token-days');
    table.append(el('caption', 'muted compact', `${days.length.toLocaleString('de-AT')} von Codex gemeldete Tage · neueste zuerst`));
    const head = el('thead'); const labels = el('tr');
    for (const label of ['Tag', 'Token']) { const cell = el('th', '', label); cell.scope = 'col'; labels.append(cell); }
    head.append(labels); table.append(head);
    const body = el('tbody');
    for (const day of days) {
      const row = el('tr'); const date = el('th'); date.scope = 'row';
      const time = el('time', '', reportedDate(day.startDate)); time.dateTime = day.startDate; date.append(time);
      const amount = el('td'); amount.append(el('span', 'token-count', counter(day.tokens)));
      const bar = el('span', 'token-bar'); bar.setAttribute('aria-hidden', 'true');
      bar.style.width = `${max === 0n ? 0 : Number(BigInt(day.tokens) * 10000n / max) / 100}%`;
      amount.append(bar); row.append(date, amount); body.append(row);
    }
    table.append(body); content.append(table);
    content.append(el('p', 'muted compact', 'Datumsangaben wie von Codex gemeldet. Fehlende Tage werden nicht als Nullwerte ergänzt.'));
  } catch (error) {
    if (!isCurrent()) return;
    const failure = el('p', 'question-error', `Token-Aktivität konnte nicht geladen werden. ${error instanceof Error ? error.message : String(error)}`);
    failure.setAttribute('role', 'alert');
    const retry = el('button', 'button secondary', 'Token-Aktivität erneut versuchen');
    retry.onclick = () => void renderTokenActivity(api, container, isCurrent);
    content.replaceChildren(failure, retry);
  }
}
