// Theming: light, dark or the system's choice, on <html data-theme>; the choice is remembered per browser.

export type ThemePreference = 'light' | 'dark' | 'system';
export type Theme = 'light' | 'dark';

export const THEME_KEY = 'sourceflow.studio.theme';

export function resolveTheme(preference: ThemePreference, prefersDark: boolean): Theme {
  return preference === 'system' ? (prefersDark ? 'dark' : 'light') : preference;
}

export function isThemePreference(value: unknown): value is ThemePreference {
  return value === 'light' || value === 'dark' || value === 'system';
}

/** The remembered preference; storage can be unavailable (private windows, blocked site data). */
export function loadThemePreference(storage: Pick<Storage, 'getItem'> | undefined = safeStorage()): ThemePreference {
  try {
    const value = storage?.getItem(THEME_KEY);
    return isThemePreference(value) ? value : 'system';
  } catch {
    return 'system';
  }
}

export function saveThemePreference(preference: ThemePreference, storage: Pick<Storage, 'setItem'> | undefined = safeStorage()): void {
  try {
    storage?.setItem(THEME_KEY, preference);
  } catch {
    // not remembered: the page still works
  }
}

export function prefersDark(): boolean {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function' && window.matchMedia('(prefers-color-scheme: dark)').matches;
}

export function applyTheme(theme: Theme, root: HTMLElement = document.documentElement): void {
  root.dataset.theme = theme;
  root.style.colorScheme = theme;
}

function safeStorage(): Storage | undefined {
  try {
    return typeof window === 'undefined' ? undefined : window.localStorage;
  } catch {
    return undefined;
  }
}
