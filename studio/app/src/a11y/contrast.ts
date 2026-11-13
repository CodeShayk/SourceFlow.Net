// WCAG 2.1 contrast (STU-A11Y-04): relative luminance and contrast ratios of sRGB colours, shared by the tests that check
// the theme tokens and the export palettes, and by the browser check of the rendered views.

/** An sRGB colour with 0–255 channels and an alpha of 0–1. */
export interface Rgba {
  r: number;
  g: number;
  b: number;
  a: number;
}

/** Parses #rgb, #rrggbb, rgb(…), rgba(…) and color(srgb …); undefined for anything else. */
export function parseColor(text: string): Rgba | undefined {
  const value = text.trim().toLowerCase();
  let match = /^#([0-9a-f]{3}|[0-9a-f]{6})$/.exec(value);
  if (match?.[1] !== undefined) {
    const digits = match[1];
    const hex = digits.length === 3 ? [...digits].map((c) => c + c).join('') : digits;
    return { r: parseInt(hex.slice(0, 2), 16), g: parseInt(hex.slice(2, 4), 16), b: parseInt(hex.slice(4, 6), 16), a: 1 };
  }

  match = /^rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)(?:\s*[,/]\s*([\d.]+%?))?\s*\)$/.exec(value);
  if (match) {
    return { r: Number(match[1]), g: Number(match[2]), b: Number(match[3]), a: alpha(match[4]) };
  }

  match = /^color\(srgb\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)(?:\s*\/\s*([\d.]+%?))?\s*\)$/.exec(value);
  if (match) {
    return { r: Number(match[1]) * 255, g: Number(match[2]) * 255, b: Number(match[3]) * 255, a: alpha(match[4]) };
  }

  return undefined;
}

function alpha(text: string | undefined): number {
  if (text === undefined) {
    return 1;
  }

  return text.endsWith('%') ? Number(text.slice(0, -1)) / 100 : Number(text);
}

/** <paramref name="top"/> drawn over an opaque <paramref name="bottom"/>. */
export function over(top: Rgba, bottom: Rgba): Rgba {
  const a = top.a;
  return { r: top.r * a + bottom.r * (1 - a), g: top.g * a + bottom.g * (1 - a), b: top.b * a + bottom.b * (1 - a), a: 1 };
}

/** The WCAG relative luminance. */
export function luminance({ r, g, b }: Rgba): number {
  const channel = (c: number) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

/** The WCAG contrast ratio of two opaque colours, 1 to 21. */
export function contrast(a: Rgba, b: Rgba): number {
  const [la, lb] = [luminance(a), luminance(b)];
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

/** WCAG 2.1 AA: 4.5:1 for text (1.4.3), 3:1 for the parts of controls and graphics people must see (1.4.11). */
export const TEXT = 4.5;
export const GRAPHIC = 3;
