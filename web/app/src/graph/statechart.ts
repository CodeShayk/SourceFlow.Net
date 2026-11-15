// The statechart as React Flow nodes and edges (doc 15 §6.1): the core's graph, sized, laid out by ELK, with each
// edge's encoding and accessible description.
import type { GraphEdge, GraphNode, GraphResult } from '@sourceflow/core-client';
import type { Edge, Node, Position } from '@xyflow/react';
import { EDGE_ENCODINGS, NODE_ENCODINGS } from './encodings.ts';
import { handlesFor, sizeFor, type Box, type LayoutEdge, type LayoutEngine, type LayoutNode, type Positions } from './layout.ts';

export interface StateNodeData extends Record<string, unknown> {
  node: GraphNode;
  /** The lines shown in the node. */
  lines: string[];
  /** Diagnostics on the step's lines (WP-S.10): shown as a badge. */
  problems?: number;
  /** The simulated instance is here (WP-S.11). */
  active?: boolean;
}

export interface KindEdgeData extends Record<string, unknown> {
  edge: GraphEdge;
  /** For a self loop: how many loops come before this one on the same node (they are drawn apart). */
  loop: number;
}

export type StateFlowNode = Node<StateNodeData>;
export type KindFlowEdge = Edge<KindEdgeData>;

/** The text lines of a node: its name, then what it sends and awaits, its guard and timeout. */
export function nodeLines(node: GraphNode): string[] {
  const lines = [node.kind === 'pivot' ? '◆ ' + node.label : node.label];
  if (node.kind === 'start') {
    return ['● start', node.label];
  }

  if (node.sends !== undefined) {
    lines.push('send ' + node.sends);
  }

  if (node.awaits !== undefined) {
    lines.push('await ' + node.awaits);
  }

  if (node.when !== undefined) {
    lines.push('when ' + node.when);
  }

  if (node.timeout !== undefined) {
    lines.push('⏱ ' + node.timeout);
  }

  return lines;
}

/** What ELK lays out. The Compensating group's size comes from its children. */
export function layoutInput(graph: GraphResult): { nodes: LayoutNode[]; edges: LayoutEdge[] } {
  return {
    nodes: graph.nodes.map((node) => ({
      id: node.id,
      ...sizeFor(nodeLines(node), node.kind === 'start' ? 120 : 150),
      ...(node.parent === undefined ? {} : { parent: node.parent }),
    })),
    edges: graph.edges.map((edge) => ({ id: edge.id, source: edge.source, target: edge.target })),
  };
}

/** The states every step can reach: laid out beside the flow, not in it. */
export const EXIT_STATES: ReadonlySet<string> = new Set(['Compensating', 'Compensated', 'ManualIntervention']);

const GROUP_PADDING = { top: 40, side: 20, bottom: 20 };
const COLUMN_GAP = 120;
const ROW_GAP = 56;

/**
 * Lays out a statechart. ELK (layered) lays out the main flow (the start, the steps and Completed) and, on its own, the
 * compensations inside their group; the exit states (Compensating, Compensated, Manual intervention) form a column to
 * the right of the flow. Edges into the exits are not given to ELK: every failing step points at them, and as long edges
 * through every layer they would make the layout super-linear (a 200-step workflow took seconds instead of ~100 ms).
 */
export async function statechartPositions(graph: GraphResult, engine: LayoutEngine): Promise<Positions> {
  const input = layoutInput(graph);
  const main = input.nodes.filter((n) => n.parent === undefined && !EXIT_STATES.has(n.id));
  const mainIds = new Set(main.map((n) => n.id));
  const children = input.nodes.filter((n) => n.parent === 'Compensating');
  const childIds = new Set(children.map((n) => n.id));
  const [flow, compensations] = await Promise.all([
    engine(main, input.edges.filter((e) => mainIds.has(e.source) && mainIds.has(e.target)), 'DOWN'),
    children.length === 0 ? Promise.resolve(new Map<string, Box>()) : engine(children, input.edges.filter((e) => childIds.has(e.source) && childIds.has(e.target)), 'DOWN'),
  ]);

  const positions = new Map<string, Box>(flow);
  const right = Math.max(0, ...[...flow.values()].map((b) => b.x + b.width));
  const top = Math.min(0, ...[...flow.values()].map((b) => b.y));
  const inner = [...compensations.values()];
  const minX = Math.min(...inner.map((b) => b.x), Infinity);
  const minY = Math.min(...inner.map((b) => b.y), Infinity);
  const innerWidth = inner.length === 0 ? 0 : Math.max(...inner.map((b) => b.x + b.width)) - minX;
  const innerHeight = inner.length === 0 ? 0 : Math.max(...inner.map((b) => b.y + b.height)) - minY;
  const group: Box = { x: right + COLUMN_GAP, y: top, width: Math.max(220, innerWidth + 2 * GROUP_PADDING.side), height: Math.max(80, innerHeight + GROUP_PADDING.top + GROUP_PADDING.bottom) };
  positions.set('Compensating', group);
  for (const [id, box] of compensations) {
    positions.set(id, { ...box, x: box.x - minX + GROUP_PADDING.side, y: box.y - minY + GROUP_PADDING.top });
  }

  let y = group.y + group.height + ROW_GAP;
  for (const id of ['Compensated', 'ManualIntervention']) {
    const size = input.nodes.find((n) => n.id === id);
    if (size !== undefined) {
      positions.set(id, { x: group.x + (group.width - size.width) / 2, y, width: size.width, height: size.height });
      y += size.height + ROW_GAP;
    }
  }

  return positions;
}

/** A node's accessible name. */
export function nodeLabel(node: GraphNode): string {
  const kind = NODE_ENCODINGS[node.kind].name;
  const details = [node.sends === undefined ? undefined : `sends ${node.sends}`, node.awaits === undefined ? undefined : `awaits ${node.awaits}`,
    node.when === undefined ? undefined : `runs when ${node.when}`, node.timeout === undefined ? undefined : `times out after ${node.timeout}`,
    node.compensable ? 'compensated on failure' : undefined].filter((d) => d !== undefined);
  return `${kind}: ${node.label.replace(/^[✋⟲◆] /u, '')}${details.length === 0 ? '' : ', ' + details.join(', ')}`;
}

/** The React Flow nodes and edges, positioned. */
export function toFlow(graph: GraphResult, positions: Positions): { nodes: StateFlowNode[]; edges: KindFlowEdge[] } {
  const nodes: StateFlowNode[] = graph.nodes.map((node) => {
    const box = positions.get(node.id) ?? { x: 0, y: 0, ...sizeFor(nodeLines(node)) };
    return {
      id: node.id,
      type: node.kind === 'compensating' ? 'group' : 'state',
      position: { x: box.x, y: box.y },
      width: box.width,
      height: box.height,
      handles: handlesFor(box.width, box.height).map((h) => ({ ...h, position: h.position as Position })),
      data: { node, lines: nodeLines(node) },
      ariaLabel: nodeLabel(node),
      draggable: false,
      connectable: false,
      ...(node.parent === undefined ? {} : { parentId: node.parent, extent: 'parent' as const }),
      ...(node.kind === 'compensating' ? { className: 'state-group', zIndex: -1 } : {}),
    };
  });
  const loops = new Map<string, number>();
  const edges: KindFlowEdge[] = graph.edges.map((edge) => {
    const loop = edge.source === edge.target ? (loops.get(edge.source) ?? 0) : 0;
    if (edge.source === edge.target) {
      loops.set(edge.source, loop + 1);
    }

    return {
      id: edge.id,
      source: edge.source,
      target: edge.target,
      type: 'kind',
      data: { edge, loop },
      ariaLabel: edge.description,
      focusable: true,
      className: `kind-${edge.kind}`,
      zIndex: 1,
    };
  });
  return { nodes, edges };
}

/** Counts by edge kind (for the legend). */
export function edgeCounts(graph: GraphResult): Map<string, number> {
  const counts = new Map<string, number>();
  for (const edge of graph.edges) {
    counts.set(edge.kind, (counts.get(edge.kind) ?? 0) + 1);
  }

  return counts;
}

export function encodingOf(edge: GraphEdge) {
  return EDGE_ENCODINGS[edge.kind];
}
