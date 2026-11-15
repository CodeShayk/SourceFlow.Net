// The statechart as a standalone SVG document (doc 15 §6.3: SVG export, the static site and the workflow-diff action).
// A pure function of the core's graph and the laid-out positions, so the browser, Node and the PR action draw the same
// picture. Every kind keeps its line style, symbol and word (WFD-NFR-4); colour is a redundant cue.
import type { GraphEdge, GraphNode, GraphResult } from '@sourceflow/core-client';
import { EDGE_ENCODINGS, NODE_ENCODINGS } from './encodings.ts';
import { sizeFor, type Box, type Positions } from './layout.ts';
import { nodeLabel, nodeLines } from './statechart.ts';

export type SvgTheme = 'light' | 'dark';

/** How a diff marks a state or a transition: added, removed or changed (WFD-36). */
export type Mark = 'added' | 'removed' | 'changed';

export interface SvgOptions {
  theme?: SvgTheme;
  /** The document's title (its accessible name). */
  title?: string;
  /** Diff marks by node or edge id. */
  marks?: ReadonlyMap<string, Mark>;
}

/** The export palettes (STU-A11Y-04 checks their contrast). */
export const PALETTES: Record<SvgTheme, Record<string, string>> = {
  light: {
    bg: '#ffffff', surface: '#f5f6f8', text: '#1b1f24', muted: '#59616c', border: '#d0d5dc',
    start: '#59616c', success: '#1a7f37', failure: '#b42318', retry: '#8250df', timeout: '#9a6700', compensation: '#0b5cad', manual: '#bc4c00', skip: '#6e7781',
    added: '#1a7f37', removed: '#b42318', changed: '#9a6700',
  },
  dark: {
    bg: '#15181c', surface: '#1d2126', text: '#e6e9ed', muted: '#a3acb8', border: '#3a414a',
    start: '#a3acb8', success: '#56d364', failure: '#ff8a80', retry: '#d2a8ff', timeout: '#f5c26b', compensation: '#6cb4ff', manual: '#ffa657', skip: '#8b949e',
    added: '#56d364', removed: '#ff8a80', changed: '#f5c26b',
  },
};

const MARK_WORDS: Record<Mark, string> = { added: '＋ added', removed: '− removed', changed: '△ changed' };
const MARGIN = 24;
const FONT = "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif";
const MONO = "ui-monospace, 'Cascadia Code', Menlo, Consolas, monospace";

export function escapeXml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/** Absolute boxes: a child's position is relative to its parent's. */
export function absoluteBoxes(graph: GraphResult, positions: Positions): Map<string, Box> {
  const parents = new Map(graph.nodes.map((n) => [n.id, n.parent]));
  const boxes = new Map<string, Box>();
  const resolve = (id: string): Box => {
    const known = boxes.get(id);
    if (known !== undefined) {
      return known;
    }

    const node = graph.nodes.find((n) => n.id === id);
    const own = positions.get(id) ?? { x: 0, y: 0, ...sizeFor(node === undefined ? [id] : nodeLines(node)) };
    const parent = parents.get(id);
    const origin = parent === undefined ? { x: 0, y: 0 } : resolve(parent);
    const box = { ...own, x: own.x + origin.x, y: own.y + origin.y };
    boxes.set(id, box);
    return box;
  };

  for (const node of graph.nodes) {
    resolve(node.id);
  }

  return boxes;
}

/** An orthogonal path from the bottom of `from` to the top of `to`, with rounded corners, and where its label goes. */
export function stepPath(from: Box, to: Box): { path: string; labelX: number; labelY: number } {
  const sx = from.x + from.width / 2;
  const sy = from.y + from.height;
  const tx = to.x + to.width / 2;
  const ty = to.y;
  if (Math.abs(sx - tx) < 1) {
    return { path: `M ${r(sx)} ${r(sy)} L ${r(tx)} ${r(ty)}`, labelX: sx, labelY: (sy + ty) / 2 };
  }

  // Down, across at the midpoint (or below both when the target is above), then down into the target.
  const mid = ty > sy + 20 ? (sy + ty) / 2 : Math.max(sy, ty) + 20;
  const radius = Math.min(10, Math.abs(tx - sx) / 2, Math.abs(mid - sy), Math.abs(mid - ty) || 10);
  const dir = tx > sx ? 1 : -1;
  const down = ty > mid ? 1 : -1;
  const path = [
    `M ${r(sx)} ${r(sy)}`,
    `L ${r(sx)} ${r(mid - radius)}`,
    `Q ${r(sx)} ${r(mid)} ${r(sx + dir * radius)} ${r(mid)}`,
    `L ${r(tx - dir * radius)} ${r(mid)}`,
    `Q ${r(tx)} ${r(mid)} ${r(tx)} ${r(mid + down * radius)}`,
    `L ${r(tx)} ${r(ty)}`,
  ].join(' ');
  return { path, labelX: (sx + tx) / 2, labelY: mid };
}

/** A self loop (a retry), out of the right side and back, further out for each further loop on the same node. */
export function selfLoop(box: Box, loop: number): { path: string; labelX: number; labelY: number } {
  const x = box.x + box.width;
  const y1 = box.y + box.height * 0.3;
  const y2 = box.y + box.height * 0.7;
  const reach = 60 + loop * 36;
  return { path: `M ${r(x)} ${r(y1)} C ${r(x + reach)} ${r(y1 - 10)}, ${r(x + reach)} ${r(y2 + 10)}, ${r(x)} ${r(y2)}`, labelX: x + reach * 0.75, labelY: (y1 + y2) / 2 };
}

function r(n: number): string {
  return String(Math.round(n * 10) / 10);
}

function labelWidth(text: string): number {
  return Math.ceil([...text].length * 6.4) + 10;
}

/** The statechart as an SVG document. */
export function statechartSvg(graph: GraphResult, positions: Positions, options: SvgOptions = {}): string {
  const colors = PALETTES[options.theme ?? 'light'];
  const marks = options.marks ?? new Map<string, Mark>();
  const boxes = absoluteBoxes(graph, positions);
  const loops = new Map<string, number>();
  const edges = graph.edges.map((edge) => {
    const from = boxes.get(edge.source);
    const to = boxes.get(edge.target);
    if (from === undefined || to === undefined) {
      return undefined;
    }

    if (edge.source === edge.target) {
      const loop = loops.get(edge.source) ?? 0;
      loops.set(edge.source, loop + 1);
      return { edge, ...selfLoop(from, loop) };
    }

    return { edge, ...stepPath(from, to) };
  }).filter((e): e is { edge: GraphEdge; path: string; labelX: number; labelY: number } => e !== undefined);

  // Bounds: the boxes, the loops and the labels.
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  const grow = (x1: number, y1: number, x2: number, y2: number) => {
    minX = Math.min(minX, x1);
    minY = Math.min(minY, y1);
    maxX = Math.max(maxX, x2);
    maxY = Math.max(maxY, y2);
  };
  for (const box of boxes.values()) {
    grow(box.x, box.y, box.x + box.width, box.y + box.height + (marks.size > 0 ? 18 : 0));
  }

  for (const e of edges) {
    const half = labelWidth(e.edge.label) / 2;
    grow(e.labelX - half, e.labelY - 10, e.labelX + half, e.labelY + 10);
    if (e.edge.source === e.edge.target) {
      const box = boxes.get(e.edge.source)!;
      grow(box.x, box.y, box.x + box.width + 60 + ((loops.get(e.edge.source) ?? 1) - 1) * 36 + 10, box.y + box.height);
    }
  }

  if (!Number.isFinite(minX)) {
    minX = minY = 0;
    maxX = maxY = 1;
  }

  const width = Math.ceil(maxX - minX + 2 * MARGIN);
  const height = Math.ceil(maxY - minY + 2 * MARGIN);
  const title = options.title ?? 'Workflow statechart';
  const out: string[] = [];
  out.push(`<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="${r(minX - MARGIN)} ${r(minY - MARGIN)} ${width} ${height}" role="img" aria-labelledby="sf-title sf-desc" font-family="${escapeXml(FONT)}" font-size="12">`);
  out.push(`<title id="sf-title">${escapeXml(title)}</title>`);
  out.push(`<desc id="sf-desc">${graph.nodes.length} states and ${graph.edges.length} transitions. Line style, symbol and label give each transition's kind.</desc>`);
  out.push('<defs>');
  for (const kind of Object.keys(EDGE_ENCODINGS)) {
    out.push(`<marker id="arrow-${kind}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="${colors[kind]}"/></marker>`);
  }

  out.push('</defs>');
  out.push(`<rect x="${r(minX - MARGIN)}" y="${r(minY - MARGIN)}" width="${width}" height="${height}" fill="${colors.bg}"/>`);

  // Groups first (behind), then edges, then states, then edge labels on top.
  const ordered = [...graph.nodes].sort((a, b) => Number(b.kind === 'compensating') - Number(a.kind === 'compensating'));
  for (const node of ordered.filter((n) => n.kind === 'compensating')) {
    out.push(groupSvg(node, boxes.get(node.id)!, colors, marks.get(node.id)));
  }

  for (const e of edges) {
    const encoding = EDGE_ENCODINGS[e.edge.kind];
    const mark = marks.get(e.edge.id);
    out.push(`<g class="edge kind-${e.edge.kind}${mark === undefined ? '' : ' ' + mark}"><title>${escapeXml(e.edge.description + (mark === undefined ? '' : ` (${mark})`))}</title>`);
    if (mark !== undefined) {
      out.push(`<path d="${e.path}" fill="none" stroke="${colors[mark]}" stroke-opacity="0.35" stroke-width="${encoding.width + 6}"/>`);
    }

    out.push(`<path d="${e.path}" fill="none" stroke="${colors[e.edge.kind]}" stroke-width="${encoding.width}"${encoding.dash === '' ? '' : ` stroke-dasharray="${encoding.dash}"`} marker-end="url(#arrow-${e.edge.kind})"/></g>`);
  }

  for (const node of ordered.filter((n) => n.kind !== 'compensating')) {
    out.push(nodeSvg(node, boxes.get(node.id)!, colors, marks.get(node.id)));
  }

  for (const e of edges) {
    const mark = marks.get(e.edge.id);
    const text = e.edge.label + (mark === undefined ? '' : ` ${MARK_WORDS[mark]}`);
    const w = labelWidth(text);
    out.push(`<g class="edge-label" aria-hidden="true"><rect x="${r(e.labelX - w / 2)}" y="${r(e.labelY - 9)}" width="${w}" height="18" rx="4" fill="${colors.bg}" stroke="${mark === undefined ? colors.border : colors[mark]}"/>`
      + `<text x="${r(e.labelX)}" y="${r(e.labelY + 4)}" text-anchor="middle" font-size="11" fill="${colors.text}">${escapeXml(text)}</text></g>`);
  }

  out.push('</svg>');
  return out.join('\n') + '\n';
}

function nodeSvg(node: GraphNode, box: Box, colors: Record<string, string>, mark: Mark | undefined): string {
  const shape = NODE_ENCODINGS[node.kind].shape;
  const stroke = node.kind === 'compensation' ? colors.compensation : colors.text;
  const dash = node.kind === 'compensation' ? ' stroke-dasharray="1.5 3"' : shape === 'octagon' ? ' stroke-dasharray="6 3"' : '';
  const parts = [`<g class="state state-${node.kind}${mark === undefined ? '' : ' ' + mark}" data-id="${escapeXml(node.id)}"><title>${escapeXml(nodeLabel(node) + (mark === undefined ? '' : ` (${mark})`))}</title>`];
  if (mark !== undefined) {
    parts.push(`<rect x="${r(box.x - 5)}" y="${r(box.y - 5)}" width="${r(box.width + 10)}" height="${r(box.height + 10)}" rx="10" fill="none" stroke="${colors[mark]}" stroke-width="3"/>`);
  }

  if (shape === 'octagon') {
    const c = 10;
    const { x, y, width: w, height: h } = box;
    parts.push(`<polygon points="${[[x + c, y], [x + w - c, y], [x + w, y + c], [x + w, y + h - c], [x + w - c, y + h], [x + c, y + h], [x, y + h - c], [x, y + c]].map(([px, py]) => `${r(px!)},${r(py!)}`).join(' ')}" fill="${colors.surface}" stroke="${stroke}" stroke-width="1.5"${dash}/>`);
  } else {
    const rx = shape === 'pill' || shape === 'circle' ? box.height / 2 : 6;
    parts.push(`<rect x="${r(box.x)}" y="${r(box.y)}" width="${r(box.width)}" height="${r(box.height)}" rx="${r(rx)}" fill="${colors.surface}" stroke="${stroke}" stroke-width="1.5"${dash}/>`);
    if (shape === 'pill') {
      parts.push(`<rect x="${r(box.x + 3)}" y="${r(box.y + 3)}" width="${r(box.width - 6)}" height="${r(box.height - 6)}" rx="${r(rx - 3)}" fill="none" stroke="${stroke}" stroke-width="1"/>`);
    }
  }

  const lines = nodeLines(node);
  lines.forEach((line, index) => {
    const y = box.y + 16 + 12 + index * 18;
    parts.push(index === 0
      ? `<text x="${r(box.x + 16)}" y="${r(y)}" font-weight="600" fill="${colors.text}">${escapeXml(line)}</text>`
      : `<text x="${r(box.x + 16)}" y="${r(y)}" font-family="${escapeXml(MONO)}" font-size="11" fill="${colors.muted}">${escapeXml(line)}</text>`);
  });
  if (mark !== undefined) {
    parts.push(`<text x="${r(box.x)}" y="${r(box.y + box.height + 16)}" font-size="11" font-weight="600" fill="${colors[mark]}">${MARK_WORDS[mark]}</text>`);
  }

  parts.push('</g>');
  return parts.join('');
}

function groupSvg(node: GraphNode, box: Box, colors: Record<string, string>, mark: Mark | undefined): string {
  return `<g class="state state-compensating${mark === undefined ? '' : ' ' + mark}"><title>${escapeXml(nodeLabel(node))}</title>`
    + `<rect x="${r(box.x)}" y="${r(box.y)}" width="${r(box.width)}" height="${r(box.height)}" rx="8" fill="none" stroke="${colors.compensation}" stroke-width="1.5" stroke-dasharray="1.5 3"/>`
    + `<text x="${r(box.x + 12)}" y="${r(box.y + 22)}" font-weight="600" fill="${colors.text}">⟲ ${escapeXml(node.label)}</text></g>`;
}
