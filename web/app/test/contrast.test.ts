// STU-A11Y-04 (plan 10 §3.3): contrast. The theme tokens of both themes and the export palettes meet WCAG 2.1 AA: text
// 4.5:1 (1.4.3) and the boundaries of controls and graphics 3:1 (1.4.11), on every surface they are drawn on. The
// browser check of the rendered views is in e2e/sample.spec.ts.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { contrast, GRAPHIC, over, parseColor, TEXT, type Rgba } from '../src/a11y/contrast';
import { PALETTES } from '../src/graph/svg';

const css = readFileSync(join(process.cwd(), 'src', 'styles.css'), 'utf8').replaceAll('\r\n', '\n');

function tokens(selector: string): Record<string, string> {
  const start = css.indexOf(selector + ' {');
  const block = css.slice(start, css.indexOf('}', start));
  return Object.fromEntries([...block.matchAll(/--([\w-]+):\s*([^;]+);/g)].map((m) => [m[1] ?? '', (m[2] ?? '').trim()]));
}

const themes = {
  light: tokens(':root'),
  dark: { ...tokens(':root'), ...tokens(":root[data-theme='dark']") },
};

function color(text: string | undefined): Rgba {
  const parsed = parseColor(text ?? '');
  if (parsed === undefined) {
    throw new Error(`Not a colour: ${text}`);
  }

  return parsed;
}

function ratios(theme: Record<string, string>, names: string[], surfaces: string[]) {
  return names.flatMap((name) => surfaces.map((surface) => ({ pair: `${name} on ${surface}`, ratio: contrast(color(theme[name]), color(theme[surface])) })));
}

const EDGES = ['edge-start', 'edge-success', 'edge-failure', 'edge-retry', 'edge-timeout', 'edge-compensation', 'edge-manual', 'edge-skip'];

describe('STU-A11Y-04 contrast', () => {
  it('parses the colour forms browsers report', () => {
    expect(parseColor('#fff')).toEqual({ r: 255, g: 255, b: 255, a: 1 });
    expect(parseColor('rgb(11, 92, 173)')).toEqual({ r: 11, g: 92, b: 173, a: 1 });
    expect(parseColor('rgba(0, 0, 0, 0)')?.a).toBe(0);
    expect(parseColor('color(srgb 1 1 1 / 0.5)')).toEqual({ r: 255, g: 255, b: 255, a: 0.5 });
    expect(parseColor('transparent')).toBeUndefined();
    expect(contrast(color('#000000'), color('#ffffff'))).toBeCloseTo(21, 5);
    expect(over(color('rgba(0, 0, 0, 0.5)'), color('#ffffff'))).toEqual({ r: 127.5, g: 127.5, b: 127.5, a: 1 });
  });

  for (const [name, theme] of Object.entries(themes)) {
    it(`${name} theme: text is 4.5:1 on every surface`, () => {
      const failing = ratios(theme, ['text', 'muted', 'accent', 'error', 'warning'], ['bg', 'surface', 'surface-strong']).filter((r) => r.ratio < TEXT);
      expect(failing).toEqual([]);
    });

    it(`${name} theme: edges, focus and control boundaries are 3:1 on every surface`, () => {
      const failing = ratios(theme, [...EDGES, 'focus', 'control-border', 'text'], ['bg', 'surface', 'surface-strong']).filter((r) => r.ratio < GRAPHIC);
      expect(failing).toEqual([]);
    });

    it(`${name} theme: edge labels (the edge colour on the canvas) are readable as text`, () => {
      const failing = ratios(theme, EDGES, ['bg']).filter((r) => r.ratio < TEXT);
      expect(failing).toEqual([]);
    });
  }

  for (const [name, palette] of Object.entries(PALETTES)) {
    it(`the ${name} export palette: text 4.5:1 and every stroke 3:1`, () => {
      const strokes = ['start', 'success', 'failure', 'retry', 'timeout', 'compensation', 'manual', 'skip', 'added', 'removed', 'changed', 'text'];
      const failing = [
        ...ratios(palette, ['text', 'muted'], ['bg', 'surface']).filter((r) => r.ratio < TEXT),
        ...ratios(palette, strokes, ['bg', 'surface']).filter((r) => r.ratio < GRAPHIC),
      ];
      expect(failing).toEqual([]);
    });
  }
});
