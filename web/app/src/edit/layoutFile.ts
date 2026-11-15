// The layout sidecar (doc 15 §6.2): `<workflow>.sfflow.layout.json` keeps the positions of nodes a user moved, so they
// stay where they were put. It is optional and the generator ignores it.
import type { Box, Positions } from '../graph/layout.ts';

export interface LayoutFile {
  version: 1;
  /** Positions by node id (top-level nodes; compensations relative to their group). */
  nodes: Record<string, { x: number; y: number }>;
}

export function layoutPath(workflowPath: string): string {
  return workflowPath.replace(/\.sfflow\.yaml$/, '') + '.sfflow.layout.json';
}

/** Reads a layout file; anything unreadable is an empty layout. */
export function readLayout(text: string | undefined): LayoutFile {
  if (text === undefined) {
    return { version: 1, nodes: {} };
  }

  try {
    const parsed = JSON.parse(text) as { nodes?: Record<string, { x?: unknown; y?: unknown }> };
    const nodes: LayoutFile['nodes'] = {};
    for (const [id, p] of Object.entries(parsed.nodes ?? {})) {
      if (typeof p?.x === 'number' && typeof p.y === 'number' && Number.isFinite(p.x) && Number.isFinite(p.y)) {
        nodes[id] = { x: p.x, y: p.y };
      }
    }

    return { version: 1, nodes };
  } catch {
    return { version: 1, nodes: {} };
  }
}

/** The file text: ids sorted and whole pixels, so moving a node changes one line. */
export function writeLayout(layout: LayoutFile): string {
  const sorted = Object.fromEntries(Object.keys(layout.nodes).sort().map((id) => [id, { x: Math.round(layout.nodes[id]!.x), y: Math.round(layout.nodes[id]!.y) }]));
  return JSON.stringify({ version: 1, nodes: sorted }, null, 2) + '\n';
}

export function moveNode(layout: LayoutFile, id: string, x: number, y: number): LayoutFile {
  return { version: 1, nodes: { ...layout.nodes, [id]: { x, y } } };
}

/** The laid-out positions with the saved ones on top (only for nodes that still exist). */
export function applyLayout(positions: Positions, layout: LayoutFile): Map<string, Box> {
  const result = new Map(positions);
  for (const [id, p] of Object.entries(layout.nodes)) {
    const box = result.get(id);
    if (box !== undefined) {
      result.set(id, { ...box, x: p.x, y: p.y });
    }
  }

  return result;
}

/** Drops positions of nodes that no longer exist (a renamed or removed step). */
export function pruneLayout(layout: LayoutFile, ids: Iterable<string>): LayoutFile {
  const keep = new Set(ids);
  return { version: 1, nodes: Object.fromEntries(Object.entries(layout.nodes).filter(([id]) => keep.has(id))) };
}
