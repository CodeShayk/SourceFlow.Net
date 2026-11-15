// UI state (Zustand): theme, the keyboard-map dialog, and the selected diagnostic of the open workflow.
import { create } from 'zustand';
import { loadThemePreference, saveThemePreference, type ThemePreference } from './theme.ts';

export interface StudioState {
  theme: ThemePreference;
  keyboardMapOpen: boolean;
  /** The index of the selected diagnostic in the panel's order, or -1. */
  selectedDiagnostic: number;
  diagnosticCount: number;
  setTheme(theme: ThemePreference): void;
  toggleTheme(resolved: 'light' | 'dark'): void;
  setKeyboardMapOpen(open: boolean): void;
  setDiagnostics(count: number): void;
  selectDiagnostic(index: number): void;
  moveDiagnostic(step: 1 | -1): void;
}

export function createStudioStore(initialTheme: ThemePreference = loadThemePreference()) {
  return create<StudioState>()((set, get) => ({
    theme: initialTheme,
    keyboardMapOpen: false,
    selectedDiagnostic: -1,
    diagnosticCount: 0,
    setTheme(theme) {
      saveThemePreference(theme);
      set({ theme });
    },
    toggleTheme(resolved) {
      get().setTheme(resolved === 'dark' ? 'light' : 'dark');
    },
    setKeyboardMapOpen(open) {
      set({ keyboardMapOpen: open });
    },
    setDiagnostics(count) {
      const selected = get().selectedDiagnostic;
      set({ diagnosticCount: count, selectedDiagnostic: selected < count ? selected : -1 });
    },
    selectDiagnostic(index) {
      const count = get().diagnosticCount;
      set({ selectedDiagnostic: index >= 0 && index < count ? index : -1 });
    },
    moveDiagnostic(step) {
      const { diagnosticCount: count, selectedDiagnostic: selected } = get();
      if (count === 0) {
        return;
      }

      set({ selectedDiagnostic: selected < 0 ? (step === 1 ? 0 : count - 1) : (selected + step + count) % count });
    },
  }));
}

export type StudioStore = ReturnType<typeof createStudioStore>;

export const useStudio = createStudioStore();
