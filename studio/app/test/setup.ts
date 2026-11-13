import { cleanup } from '@testing-library/react';
import { afterEach, beforeEach } from 'vitest';
import { useStudio } from '../src/store.ts';

const initial = useStudio.getState();

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
