// The visual encoding of each statechart edge and node kind (doc 15 §6.1): a line style, an icon and a word for each,
// never colour alone (WFD-NFR-4). The legend, the edges and the swimlane all read this table.
import type { EdgeKind, NodeKind } from '@sourceflow/core-client';

export interface EdgeEncoding {
  kind: EdgeKind;
  /** The word shown in the legend and read by assistive technology. */
  name: string;
  symbol: string;
  /** SVG stroke-dasharray; empty for a solid line. */
  dash: string;
  width: number;
  /** A CSS custom property holding the colour (a redundant cue). */
  color: string;
}

export const EDGE_ENCODINGS: Readonly<Record<EdgeKind, EdgeEncoding>> = {
  start: { kind: 'start', name: 'Start', symbol: '▶', dash: '', width: 1.5, color: 'var(--edge-start)' },
  success: { kind: 'success', name: 'Success', symbol: '✓', dash: '', width: 2, color: 'var(--edge-success)' },
  failure: { kind: 'failure', name: 'Failure', symbol: '✕', dash: '7 4', width: 2, color: 'var(--edge-failure)' },
  retry: { kind: 'retry', name: 'Retry', symbol: '↻', dash: '2 3', width: 2, color: 'var(--edge-retry)' },
  timeout: { kind: 'timeout', name: 'Timeout', symbol: '⏱', dash: '9 3 2 3', width: 2, color: 'var(--edge-timeout)' },
  compensation: { kind: 'compensation', name: 'Compensation', symbol: '⟲', dash: '1 4', width: 2.5, color: 'var(--edge-compensation)' },
  manual: { kind: 'manual', name: 'Manual intervention', symbol: '✋', dash: '12 3 2 3 2 3', width: 2.5, color: 'var(--edge-manual)' },
  skip: { kind: 'skip', name: 'Skipped by a guard', symbol: '⤼', dash: '14 6', width: 1.5, color: 'var(--edge-skip)' },
};

/** Every edge kind, in legend order. */
export const EDGE_KINDS: readonly EdgeKind[] = ['start', 'success', 'failure', 'retry', 'timeout', 'compensation', 'manual', 'skip'];

export interface NodeEncoding {
  name: string;
  symbol: string;
  shape: 'circle' | 'box' | 'diamond' | 'pill' | 'group' | 'octagon';
}

export const NODE_ENCODINGS: Readonly<Record<NodeKind, NodeEncoding>> = {
  start: { name: 'Start', symbol: '●', shape: 'circle' },
  step: { name: 'Step', symbol: '▭', shape: 'box' },
  pivot: { name: 'Pivot (point of no return)', symbol: '◆', shape: 'diamond' },
  compensation: { name: 'Compensation', symbol: '⟲', shape: 'box' },
  compensating: { name: 'Compensating', symbol: '⟲', shape: 'group' },
  terminal: { name: 'End', symbol: '◉', shape: 'pill' },
  manual: { name: 'Manual intervention', symbol: '✋', shape: 'octagon' },
};

/** Whether all edge kinds have distinct line styles and symbols (the encoding is unambiguous without colour). */
export function encodingsAreDistinct(): boolean {
  const all = EDGE_KINDS.map((k) => EDGE_ENCODINGS[k]);
  return new Set(all.map((e) => e.symbol)).size === all.length && new Set(all.map((e) => `${e.dash}|${e.width}`)).size === all.length;
}
