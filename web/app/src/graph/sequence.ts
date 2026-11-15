// Swimlane geometry (WFD-31): where each message's arrow starts and ends between the lanes, and how it is read aloud.
import type { MessageKind, SequenceLane, SequenceMessage, SequenceResult } from '@sourceflow/core-client';

export interface Arrow {
  /** The 0-based lane the arrow starts in. */
  from: number;
  to: number;
  /** 1-based CSS grid columns the arrow spans (end exclusive). */
  columnStart: number;
  columnEnd: number;
  direction: 'right' | 'left' | 'self';
}

export function arrow(lanes: readonly SequenceLane[], message: SequenceMessage): Arrow {
  const from = Math.max(0, lanes.findIndex((l) => l.id === message.from));
  const to = Math.max(0, lanes.findIndex((l) => l.id === message.to));
  return {
    from,
    to,
    columnStart: Math.min(from, to) + 1,
    columnEnd: Math.max(from, to) + 2,
    direction: from === to ? 'self' : to > from ? 'right' : 'left',
  };
}

export const MESSAGE_KINDS: Readonly<Record<MessageKind, { name: string; dash: string }>> = {
  start: { name: 'start', dash: '' },
  command: { name: 'command', dash: '' },
  event: { name: 'event', dash: '6 3' },
  rejection: { name: 'rejection', dash: '7 4' },
  failure: { name: 'failure event', dash: '7 4' },
  timer: { name: 'timer', dash: '9 3 2 3' },
  compensation: { name: 'compensation', dash: '1 4' },
  status: { name: 'status', dash: '' },
  manual: { name: 'manual intervention', dash: '12 3 2 3 2 3' },
};

/** The sentence a screen reader hears for a message. */
export function describe(sequence: SequenceResult, message: SequenceMessage): string {
  const lane = (id: string) => sequence.lanes.find((l) => l.id === id)?.label ?? id;
  const kind = MESSAGE_KINDS[message.kind].name;
  const attempt = message.attempt !== undefined && message.attempt > 1 ? `, attempt ${message.attempt}` : '';
  return message.from === message.to
    ? `${lane(message.from)}: ${kind} ${message.label}${attempt}`
    : `${kind} from ${lane(message.from)} to ${lane(message.to)}: ${message.label}${attempt}`;
}

/** Short choices for the path picker: the id, where it ends, and its length. */
export function pathChoice(path: { id: string; end: string; moves: readonly unknown[] }): string {
  return `${path.id} · ${path.end} · ${path.moves.length} move${path.moves.length === 1 ? '' : 's'}`;
}
