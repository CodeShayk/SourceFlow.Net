// Undo and redo over the workflow text (the DSL text is the model, so a snapshot of it is the whole state). Bursts of
// typing within `coalesceMs` become one step; a visual edit is always its own step.

export interface History {
  readonly states: readonly string[];
  readonly index: number;
  /** When the current state was recorded, and how (for coalescing typing). */
  readonly last: { at: number; kind: 'typing' | 'edit' } | undefined;
}

export const LIMIT = 200;

export function start(text: string): History {
  return { states: [text], index: 0, last: undefined };
}

export function current(history: History): string {
  return history.states[history.index]!;
}

/** Records a new text: drops the redo branch; typing right after typing replaces the last state instead of adding one. */
export function record(history: History, text: string, kind: 'typing' | 'edit', at: number, coalesceMs = 600): History {
  if (text === current(history)) {
    return history;
  }

  const kept = history.states.slice(0, history.index + 1);
  const merge = kind === 'typing' && history.last?.kind === 'typing' && at - history.last.at <= coalesceMs && history.index > 0;
  const states = merge ? [...kept.slice(0, -1), text] : [...kept, text];
  const trimmed = states.length > LIMIT ? states.slice(states.length - LIMIT) : states;
  return { states: trimmed, index: trimmed.length - 1, last: { at, kind } };
}

export const canUndo = (history: History) => history.index > 0;
export const canRedo = (history: History) => history.index < history.states.length - 1;

export function undo(history: History): History {
  return canUndo(history) ? { ...history, index: history.index - 1, last: undefined } : history;
}

export function redo(history: History): History {
  return canRedo(history) ? { ...history, index: history.index + 1, last: undefined } : history;
}
