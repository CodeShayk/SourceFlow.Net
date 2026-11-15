// The ELK layout adapter (doc 15 §6.2: the layered algorithm): statechart or domain-map nodes and edges in, positions
// out. In the browser ELK runs in its own worker; tests use the bundled build on the same thread.
import type { ElkExtendedEdge, ElkNode, LayoutOptions } from 'elkjs/lib/elk-api.js';

/** What the adapter needs of an ELK instance. */
export interface ElkLike {
  layout(graph: ElkNode): Promise<ElkNode>;
}

export interface LayoutNode {
  id: string;
  width: number;
  height: number;
  /** The containing node (its position is relative to the parent's). */
  parent?: string;
}

export interface LayoutEdge {
  id: string;
  source: string;
  target: string;
}

export interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** Positions by node id; a child's position is relative to its parent (as React Flow takes it). */
export type Positions = ReadonlyMap<string, Box>;

export type LayoutEngine = (nodes: readonly LayoutNode[], edges: readonly LayoutEdge[], direction?: 'DOWN' | 'RIGHT') => Promise<Positions>;

export const LAYOUT_OPTIONS: LayoutOptions = {
  'elk.algorithm': 'layered',
  'elk.hierarchyHandling': 'INCLUDE_CHILDREN',
  'elk.layered.spacing.nodeNodeBetweenLayers': '64',
  'elk.spacing.nodeNode': '40',
  'elk.spacing.edgeNode': '24',
  'elk.layered.nodePlacement.strategy': 'BRANDES_KOEPF',
  'elk.padding': '[top=40,left=20,bottom=20,right=20]',
};

/** The ELK graph: children nested under their parents, every edge on the root (INCLUDE_CHILDREN routes across groups). */
export function toElk(nodes: readonly LayoutNode[], edges: readonly LayoutEdge[], direction: 'DOWN' | 'RIGHT' = 'DOWN'): ElkNode {
  const byId = new Map<string, ElkNode>();
  for (const node of nodes) {
    byId.set(node.id, { id: node.id, width: node.width, height: node.height });
  }

  const roots: ElkNode[] = [];
  for (const node of nodes) {
    const elk = byId.get(node.id)!;
    const parent = node.parent === undefined ? undefined : byId.get(node.parent);
    if (parent === undefined) {
      roots.push(elk);
    } else {
      (parent.children ??= []).push(elk);
      delete parent.width;
      delete parent.height;
    }
  }

  const known = new Set(byId.keys());
  const elkEdges: ElkExtendedEdge[] = edges
    .filter((e) => known.has(e.source) && known.has(e.target) && e.source !== e.target) // self loops are drawn, not laid out
    .map((e) => ({ id: e.id, sources: [e.source], targets: [e.target] }));
  return { id: 'root', layoutOptions: { ...LAYOUT_OPTIONS, 'elk.direction': direction }, children: roots, edges: elkEdges };
}

/** Reads the positions ELK computed (relative to each node's parent). */
export function positionsOf(laidOut: ElkNode): Map<string, Box> {
  const positions = new Map<string, Box>();
  const visit = (node: ElkNode) => {
    for (const child of node.children ?? []) {
      positions.set(child.id, { x: child.x ?? 0, y: child.y ?? 0, width: child.width ?? 0, height: child.height ?? 0 });
      visit(child);
    }
  };
  visit(laidOut);
  return positions;
}

/** A layout engine over an ELK instance. */
export function elkEngine(elk: ElkLike): LayoutEngine {
  return async (nodes, edges, direction) => positionsOf(await elk.layout(toElk(nodes, edges, direction)));
}

/**
 * The handles of a laid-out node: edges enter at the top and leave at the bottom (or left and right for a left-to-right
 * map). Giving React Flow the sizes and handles up front means it draws edges without measuring the DOM first (its
 * server-rendering mode), which the static build and jsdom tests rely on.
 */
export function handlesFor(width: number, height: number, direction: 'DOWN' | 'RIGHT' = 'DOWN') {
  return direction === 'DOWN'
    ? [
        { type: 'target' as const, position: 'top' as const, x: width / 2 - 4, y: -4, width: 8, height: 8 },
        { type: 'source' as const, position: 'bottom' as const, x: width / 2 - 4, y: height - 4, width: 8, height: 8 },
      ]
    : [
        { type: 'target' as const, position: 'left' as const, x: -4, y: height / 2 - 4, width: 8, height: 8 },
        { type: 'source' as const, position: 'right' as const, x: width - 4, y: height / 2 - 4, width: 8, height: 8 },
      ];
}

/** A node's size from its text: wide enough for the longest line at ~7.5 px per character. */
export function sizeFor(lines: readonly string[], minWidth = 140, lineHeight = 18, padding = 16): { width: number; height: number } {
  const longest = lines.reduce((max, line) => Math.max(max, line.length), 0);
  return { width: Math.max(minWidth, Math.ceil(longest * 7.5) + padding * 2), height: lines.length * lineHeight + padding * 2 };
}
