// The structural difference between two statecharts (doc 15 §6.1 "Diff", WFD-36): states and transitions added,
// removed or changed, by id, with the marks each side is drawn with and a sentence per change for a PR comment.
import type { GraphEdge, GraphNode, GraphResult } from '@sourceflow/core-client';
import { EDGE_ENCODINGS, NODE_ENCODINGS } from './encodings.ts';
import type { Mark } from './svg.ts';

export interface GraphDiff {
  added: { nodes: GraphNode[]; edges: GraphEdge[] };
  removed: { nodes: GraphNode[]; edges: GraphEdge[] };
  changed: { nodes: Array<{ before: GraphNode; after: GraphNode }>; edges: Array<{ before: GraphEdge; after: GraphEdge }> };
  /** Marks for the before picture (removed, changed) and the after picture (added, changed). */
  before: Map<string, Mark>;
  after: Map<string, Mark>;
  unchanged: boolean;
}

const NODE_FIELDS = ['kind', 'label', 'sends', 'awaits', 'when', 'timeout', 'compensable', 'order', 'parent'] as const;
const EDGE_FIELDS = ['source', 'target', 'kind', 'label'] as const;

function same<T extends object>(a: T, b: T, fields: readonly (keyof T)[]): boolean {
  return fields.every((f) => a[f] === b[f]);
}

/** Compares two statecharts; a missing side (a new or deleted workflow) counts as empty. */
export function diffGraphs(before: GraphResult | undefined, after: GraphResult | undefined): GraphDiff {
  const empty: GraphResult = { nodes: [], edges: [] };
  const a = before ?? empty;
  const b = after ?? empty;
  const nodesA = new Map(a.nodes.map((n) => [n.id, n]));
  const nodesB = new Map(b.nodes.map((n) => [n.id, n]));
  const edgesA = new Map(a.edges.map((e) => [e.id, e]));
  const edgesB = new Map(b.edges.map((e) => [e.id, e]));
  const diff: GraphDiff = {
    added: { nodes: b.nodes.filter((n) => !nodesA.has(n.id)), edges: b.edges.filter((e) => !edgesA.has(e.id)) },
    removed: { nodes: a.nodes.filter((n) => !nodesB.has(n.id)), edges: a.edges.filter((e) => !edgesB.has(e.id)) },
    changed: {
      nodes: a.nodes.flatMap((n) => {
        const other = nodesB.get(n.id);
        return other !== undefined && !same(n, other, NODE_FIELDS) ? [{ before: n, after: other }] : [];
      }),
      edges: a.edges.flatMap((e) => {
        const other = edgesB.get(e.id);
        return other !== undefined && !same(e, other, EDGE_FIELDS) ? [{ before: e, after: other }] : [];
      }),
    },
    before: new Map(),
    after: new Map(),
    unchanged: false,
  };
  for (const n of diff.removed.nodes) diff.before.set(n.id, 'removed');
  for (const e of diff.removed.edges) diff.before.set(e.id, 'removed');
  for (const n of diff.added.nodes) diff.after.set(n.id, 'added');
  for (const e of diff.added.edges) diff.after.set(e.id, 'added');
  for (const { before: n } of diff.changed.nodes) {
    diff.before.set(n.id, 'changed');
    diff.after.set(n.id, 'changed');
  }

  for (const { before: e } of diff.changed.edges) {
    diff.before.set(e.id, 'changed');
    diff.after.set(e.id, 'changed');
  }

  diff.unchanged = diff.before.size === 0 && diff.after.size === 0;
  return diff;
}

function stateName(node: GraphNode): string {
  return `${NODE_ENCODINGS[node.kind].name.toLowerCase()} ${node.label.replace(/^[✋⟲◆] /u, '')}`;
}

function transitionName(edge: GraphEdge): string {
  return `${EDGE_ENCODINGS[edge.kind].name.toLowerCase()} ${edge.source} → ${edge.target} (${edge.label})`;
}

function changes(before: object, after: object, fields: readonly string[]): string {
  const b = before as Record<string, unknown>;
  const a = after as Record<string, unknown>;
  return fields.filter((f) => b[f] !== a[f]).map((f) => `${f} ${b[f] === undefined ? '(none)' : String(b[f])} → ${a[f] === undefined ? '(none)' : String(a[f])}`).join(', ');
}

/** One sentence per change, additions first. */
export function describeDiff(diff: GraphDiff): string[] {
  return [
    ...diff.added.nodes.map((n) => `＋ Added ${stateName(n)}`),
    ...diff.added.edges.map((e) => `＋ Added ${transitionName(e)}`),
    ...diff.removed.nodes.map((n) => `− Removed ${stateName(n)}`),
    ...diff.removed.edges.map((e) => `− Removed ${transitionName(e)}`),
    ...diff.changed.nodes.map(({ before, after }) => `△ Changed ${stateName(after)}: ${changes(before, after, NODE_FIELDS)}`),
    ...diff.changed.edges.map(({ before, after }) => `△ Changed ${transitionName(after)}: ${changes(before, after, EDGE_FIELDS)}`),
  ];
}
