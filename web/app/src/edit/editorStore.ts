// The open workflow's editing state: the text history (the text is the model), what was last saved, the layout
// sidecar, the selected step, and the last edit error.
import { create } from 'zustand';
import * as history from './history.ts';
import { moveNode, readLayout, writeLayout, type LayoutFile } from './layoutFile.ts';
import { EditError } from './yamlEdit.ts';

export interface EditorState {
  path: string | undefined;
  saved: string;
  history: history.History;
  layout: LayoutFile;
  savedLayout: string;
  selected: string | undefined;
  error: string | undefined;
  /** A request to show a line in the code editor (the nonce makes the same line show again). */
  reveal: { line: number; nonce: number } | undefined;
  open(path: string, text: string, layout?: string): void;
  type(text: string, at?: number): void;
  edit(change: (text: string) => string): boolean;
  undo(): void;
  redo(): void;
  select(step: string | undefined): void;
  move(node: string, x: number, y: number): void;
  revealLine(line: number): void;
  saved_(text: string, layout: string): void;
  clearError(): void;
}

export function createEditorStore(now: () => number = () => Date.now()) {
  return create<EditorState>()((set, get) => ({
    path: undefined,
    saved: '',
    history: history.start(''),
    layout: readLayout(undefined),
    savedLayout: writeLayout(readLayout(undefined)),
    selected: undefined,
    error: undefined,
    reveal: undefined,
    open(path, text, layout) {
      const parsed = readLayout(layout);
      set({ path, saved: text, history: history.start(text), layout: parsed, savedLayout: writeLayout(parsed), selected: undefined, error: undefined, reveal: undefined });
    },
    type(text, at = now()) {
      set({ history: history.record(get().history, text, 'typing', at), error: undefined });
    },
    edit(change) {
      try {
        const next = change(history.current(get().history));
        set({ history: history.record(get().history, next, 'edit', now()), error: undefined });
        return true;
      } catch (error) {
        if (error instanceof EditError) {
          set({ error: error.message });
          return false;
        }

        throw error;
      }
    },
    undo() {
      set({ history: history.undo(get().history) });
    },
    redo() {
      set({ history: history.redo(get().history) });
    },
    select(step) {
      set({ selected: step });
    },
    move(node, x, y) {
      set({ layout: moveNode(get().layout, node, x, y) });
    },
    revealLine(line) {
      set({ reveal: { line, nonce: (get().reveal?.nonce ?? 0) + 1 } });
    },
    saved_(text, layout) {
      set({ saved: text, savedLayout: layout });
    },
    clearError() {
      set({ error: undefined });
    },
  }));
}

export type EditorStore = ReturnType<typeof createEditorStore>;

export const useEditor = createEditorStore();

export const textOf = (state: EditorState) => history.current(state.history);

export function isDirty(state: EditorState): boolean {
  return textOf(state) !== state.saved || writeLayout(state.layout) !== state.savedLayout;
}
