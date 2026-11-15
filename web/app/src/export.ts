// Exports from the workflow view (doc 15 §6.3): the statechart as SVG or PNG (drawn in the browser), and the core's
// Mermaid and BPMN 2.0. Everything is made on the page and saved as a download; nothing leaves the machine.
import type { GraphResult } from '@sourceflow/core-client';
import type { LayoutFile } from './edit/layoutFile.ts';
import { applyLayout } from './edit/layoutFile.ts';
import type { LayoutEngine } from './graph/layout.ts';
import { statechartPositions } from './graph/statechart.ts';
import { statechartSvg, type SvgTheme } from './graph/svg.ts';
import type { StudioCore } from './services.tsx';

export type ExportFormat = 'svg' | 'png' | 'mermaid' | 'bpmn';

export const EXPORTS: ReadonlyArray<{ format: ExportFormat; label: string; extension: string; type: string }> = [
  { format: 'svg', label: 'SVG', extension: '.svg', type: 'image/svg+xml' },
  { format: 'png', label: 'PNG', extension: '.png', type: 'image/png' },
  { format: 'mermaid', label: 'Mermaid', extension: '.mmd', type: 'text/plain' },
  { format: 'bpmn', label: 'BPMN', extension: '.bpmn', type: 'application/xml' },
];

/** The statechart as the canvas shows it: the core's graph, ELK's layout and the positions saved beside the workflow. */
export async function statechartDocument(graph: GraphResult, engine: LayoutEngine, layout: LayoutFile, theme: SvgTheme, title: string): Promise<string> {
  return statechartSvg(graph, applyLayout(await statechartPositions(graph, engine), layout), { theme, title });
}

/** Draws an SVG document on a canvas at `scale` and returns the PNG. */
export async function svgToPng(svg: string, scale = 2): Promise<Blob> {
  const width = Number(/width="(\d+)"/.exec(svg)?.[1] ?? 800);
  const height = Number(/height="(\d+)"/.exec(svg)?.[1] ?? 600);
  const url = URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml' }));
  try {
    const image = new Image();
    image.decoding = 'async';
    await new Promise<void>((resolve, reject) => {
      image.onload = () => resolve();
      image.onerror = () => reject(new Error('The statechart could not be drawn as an image.'));
      image.src = url;
    });
    const canvas = document.createElement('canvas');
    canvas.width = Math.ceil(width * scale);
    canvas.height = Math.ceil(height * scale);
    const context = canvas.getContext('2d');
    if (context === null) {
      throw new Error('This browser has no 2D canvas.');
    }

    context.scale(scale, scale);
    context.drawImage(image, 0, 0, width, height);
    return await new Promise<Blob>((resolve, reject) => canvas.toBlob((blob) => (blob === null ? reject(new Error('The PNG could not be made.')) : resolve(blob)), 'image/png'));
  } finally {
    URL.revokeObjectURL(url);
  }
}

/** Saves `content` as a download called `name`. */
export function download(name: string, content: Blob): void {
  const url = URL.createObjectURL(content);
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  link.hidden = true;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

/** Makes one export of the workflow `text`: its file name and content. */
export async function exportWorkflow(format: ExportFormat, text: string, name: string, services: { core: Pick<StudioCore, 'graph' | 'toMermaid' | 'toBpmn'>; layout: LayoutEngine },
  layout: LayoutFile, theme: SvgTheme): Promise<{ name: string; content: Blob }> {
  const spec = EXPORTS.find((e) => e.format === format)!;
  const base = name.replace(/\.sfflow\.yaml$/, '');
  const file = base + spec.extension;
  switch (format) {
    case 'svg':
    case 'png': {
      const svg = await statechartDocument(await services.core.graph({ yaml: text }), services.layout, layout, theme, `Statechart of ${name}`);
      return { name: file, content: format === 'svg' ? new Blob([svg], { type: spec.type }) : await svgToPng(svg) };
    }
    case 'mermaid':
      return { name: file, content: new Blob([(await services.core.toMermaid({ yaml: text })).text], { type: spec.type }) };
    case 'bpmn':
      return { name: file, content: new Blob([(await services.core.toBpmn({ yaml: text })).text], { type: spec.type }) };
  }
}
