import { cleanup } from '@testing-library/react';
import { afterEach, beforeEach } from 'vitest';
import { useStudio } from '../src/store.ts';

const initial = useStudio.getState();

// What React Flow needs from a browser that jsdom lacks (its documented test setup).
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}

globalThis.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver;
globalThis.DOMMatrixReadOnly ??= class {
  m22: number;
  constructor(transform?: string) {
    const scale = transform?.match(/scale\(([1-9.])\)/)?.[1];
    this.m22 = scale !== undefined ? Number(scale) : 1;
  }
} as unknown as typeof DOMMatrixReadOnly;
Object.defineProperties(globalThis.HTMLElement.prototype, {
  offsetHeight: { configurable: true, get() { return Number.parseFloat((this as HTMLElement).style.height) || 600; } },
  offsetWidth: { configurable: true, get() { return Number.parseFloat((this as HTMLElement).style.width) || 800; } },
});
(globalThis.SVGElement.prototype as unknown as { getBBox: () => DOMRect }).getBBox = () => ({ x: 0, y: 0, width: 0, height: 0 }) as DOMRect;

beforeEach(() => {
  // jsdom has no matchMedia: the system theme is light.
  window.matchMedia ??= ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
});

afterEach(() => {
  cleanup();
  useStudio.setState({ ...initial, theme: 'system', keyboardMapOpen: false, selectedDiagnostic: -1, diagnosticCount: 0 });
  window.localStorage.clear();
  delete document.documentElement.dataset.theme;
});
