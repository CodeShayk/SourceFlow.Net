// The keyboard map: every shortcut in one table, shown by the keyboard-map dialog (`?`).

export type Command = 'keyboardMap' | 'focusExplorer' | 'focusEditor' | 'focusDiagnostics' | 'nextDiagnostic' | 'previousDiagnostic' | 'toggleTheme' | 'overview'
  | 'save' | 'undo' | 'redo';

export interface Shortcut {
  command: Command;
  /** For example `Alt+1`, `Shift+?`, `F8`. */
  keys: string;
  description: string;
}

export const SHORTCUTS: readonly Shortcut[] = [
  { command: 'keyboardMap', keys: 'Shift+?', description: 'Show the keyboard map' },
  { command: 'focusExplorer', keys: 'Alt+1', description: 'Go to the workspace explorer' },
  { command: 'focusEditor', keys: 'Alt+2', description: 'Go to the open workflow' },
  { command: 'focusDiagnostics', keys: 'Alt+3', description: 'Go to the diagnostics' },
  { command: 'nextDiagnostic', keys: 'F8', description: 'Next diagnostic' },
  { command: 'previousDiagnostic', keys: 'Shift+F8', description: 'Previous diagnostic' },
  { command: 'toggleTheme', keys: 'Alt+T', description: 'Switch between light and dark' },
  { command: 'overview', keys: 'Alt+H', description: 'Go to the domain overview' },
  { command: 'save', keys: 'Ctrl+S', description: 'Save the workflow (and its layout)' },
  { command: 'undo', keys: 'Ctrl+Z', description: 'Undo the last edit (in the YAML editor: its own undo)' },
  { command: 'redo', keys: 'Ctrl+Y', description: 'Redo' },
];

interface KeyLike {
  key: string;
  altKey: boolean;
  ctrlKey: boolean;
  metaKey: boolean;
  shiftKey: boolean;
}

/** Whether `event` is `keys` (modifiers must match exactly; letters ignore case). */
export function matches(event: KeyLike, keys: string): boolean {
  const parts = keys.split('+');
  const key = parts[parts.length - 1] ?? '';
  const modifiers = new Set(parts.slice(0, -1));
  return event.altKey === modifiers.has('Alt')
    && event.ctrlKey === modifiers.has('Ctrl')
    && event.metaKey === modifiers.has('Meta')
    && event.shiftKey === modifiers.has('Shift')
    && (event.key.toLowerCase() === key.toLowerCase() || (event.altKey && altCode(event) === key));
}

/** The command for a key press, or undefined. Typing in a text field never triggers a plain-key shortcut. */
export function commandFor(event: KeyLike & { target?: EventTarget | null }): Command | undefined {
  const editing = isEditable(event.target ?? null);
  for (const shortcut of SHORTCUTS) {
    const plain = !shortcut.keys.includes('Alt+') && !shortcut.keys.includes('Ctrl+') && !/^F\d+$/.test(shortcut.keys.replace('Shift+', ''));
    // In a text field, plain keys type, and undo/redo belong to the field (Monaco and inputs have their own).
    if (editing && (plain || shortcut.command === 'undo' || shortcut.command === 'redo')) {
      continue;
    }

    if (matches(event, shortcut.keys)) {
      return shortcut.command;
    }
  }

  return undefined;
}

function isEditable(target: EventTarget | null): boolean {
  if (typeof HTMLElement === 'undefined' || !(target instanceof HTMLElement)) {
    return false;
  }

  return target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
}

// macOS turns Alt+1 into '¡' and so on: fall back to the physical key.
function altCode(event: KeyLike): string | undefined {
  const code = (event as KeyLike & { code?: string }).code;
  if (code === undefined) {
    return undefined;
  }

  return code.startsWith('Digit') ? code.slice(5) : code.startsWith('Key') ? code.slice(3) : undefined;
}
