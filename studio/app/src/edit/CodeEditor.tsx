import type { Diagnostic } from '@sourceflow/core-client';
import { useEffect, useRef, type ComponentType } from 'react';

/** The code editor beside the canvas: Monaco in the app (MonacoEditor.tsx), a plain textarea in tests and as a fallback. */
export interface CodeEditorProps {
  path: string;
  value: string;
  onChange: (value: string) => void;
  diagnostics: readonly Diagnostic[];
  readOnly: boolean;
  label: string;
  reveal: { line: number; nonce: number } | undefined;
}

export type CodeEditorComponent = ComponentType<CodeEditorProps>;

/** A textarea editor: the same contract as Monaco, for tests and when Monaco can't load. */
export function TextareaEditor({ value, onChange, readOnly, label, reveal, diagnostics }: CodeEditorProps) {
  const area = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    const element = area.current;
    if (element === null || reveal === undefined) {
      return;
    }

    const lines = element.value.split('\n');
    const start = lines.slice(0, reveal.line - 1).reduce((sum, line) => sum + line.length + 1, 0);
    element.focus();
    element.setSelectionRange(start, start + (lines[reveal.line - 1]?.length ?? 0));
    element.dataset.cursorLine = String(reveal.line);
  }, [reveal]);
  return (
    <div className="code-editor">
      <textarea ref={area} aria-label={label} value={value} readOnly={readOnly} spellCheck={false} onChange={(event) => onChange(event.target.value)}
        aria-invalid={diagnostics.some((d) => d.severity === 'error') ? 'true' : undefined} />
    </div>
  );
}
