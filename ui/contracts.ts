export type QuestionState =
  | { kind: 'pending' }
  | { kind: 'answered'; text: string; at: string }
  | { kind: 'dismissed'; at: string };

export type QuestionDecision = { kind: 'answer'; text: string } | { kind: 'dismiss' };
export interface QuestionChoice { label: string; impact: string | null; followUpPrompt: string | null }
export interface QuestionOrigin {
  threadId: string; sessionId: string; workspacePath: string; threadName: string;
  transcriptPath: string | null; parentThreadId: string | null; forkedFromId: string | null;
}
export interface Question {
  id: string; prompt: string; choices: QuestionChoice[]; allowFreeText: boolean; context: string | null;
  project: string; conversationId: string | null; source: string; blocking: boolean;
  state: QuestionState; createdAt: string; version: number;
  goal: string | null; followUpPrompt: string | null; origin: QuestionOrigin | null;
}

export type DeliveryState =
  | { kind: 'sending' }
  | { kind: 'queued'; messageId: string }
  | { kind: 'failed'; reason: string }
  | { kind: 'uncertain'; reason: string };

export type DispatchContent =
  | { kind: 'message'; text: string; sourceThreadId?: string | null }
  | { kind: 'answer'; questionId: string; note?: string | null };

export type WorkOwner = { kind: 'operator' } | { kind: 'agent'; threadId: string };
export type WorkState =
  | { kind: 'todo'; note: string | null }
  | { kind: 'in_progress'; note: string | null }
  | { kind: 'blocked'; reason: string }
  | { kind: 'done'; evidence: string };

export function workNote(state: WorkState): string {
  switch (state.kind) {
    case 'todo': case 'in_progress': return state.note ?? '';
    case 'blocked': return state.reason;
    case 'done': return state.evidence;
    default: return assertNever(state);
  }
}

export function workState(kind: string, note: string): WorkState {
  switch (kind) {
    case 'todo': return { kind, note };
    case 'in_progress': return { kind, note };
    case 'blocked': return { kind, reason: note };
    case 'done': return { kind, evidence: note };
    default: throw new Error('Unbekannter Aufgabenstatus.');
  }
}

export function assertNever(value: never): never { throw new Error('Unbekannte Variante: ' + JSON.stringify(value)); }
