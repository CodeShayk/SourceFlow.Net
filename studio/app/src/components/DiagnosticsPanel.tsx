import type { Diagnostic } from '@sourceflow/core-client';
import { forwardRef, useEffect } from 'react';
import { useStudio } from '../store.ts';
import { countDiagnostics, sortDiagnostics } from '../workspace.ts';

interface Props {
  diagnostics: readonly Diagnostic[] | undefined;
  /** Shown instead of the list (no workflow open, still validating, or the core failed). */
  status?: string;
}

/**
 * The diagnostics of the open workflow (WF-S*, WF-V*), errors first. Severity is a word and an icon, never colour
 * alone (WFD-NFR-4); F8 / Shift+F8 move the selection.
 */
export const DiagnosticsPanel = forwardRef<HTMLElement, Props>(function DiagnosticsPanel({ diagnostics, status }, ref) {
  const sorted = diagnostics === undefined ? [] : sortDiagnostics(diagnostics);
  const selected = useStudio((s) => s.selectedDiagnostic);
  const select = useStudio((s) => s.selectDiagnostic);
  const setCount = useStudio((s) => s.setDiagnostics);
  useEffect(() => setCount(sorted.length), [sorted.length, setCount]);
  const counts = countDiagnostics(sorted);

  return (
    <section ref={ref} className="diagnostics" aria-label="Diagnostics" tabIndex={-1}>
      <h2>
        Diagnostics
        {diagnostics !== undefined ? <span className="counts"> {counts.errors} error{counts.errors === 1 ? '' : 's'}, {counts.warnings} warning{counts.warnings === 1 ? '' : 's'}</span> : null}
      </h2>
      {status !== undefined ? (
        <p role="status">{status}</p>
      ) : sorted.length === 0 ? (
        <p role="status">No problems.</p>
      ) : (
        <ol>
          {sorted.map((d, index) => (
            <li key={`${d.id}:${d.line}:${d.column}:${index}`} className={`diagnostic ${d.severity}`}>
              <button type="button" aria-current={index === selected ? 'true' : undefined} onClick={() => select(index)}>
                <span className="severity">
                  <span aria-hidden="true">{d.severity === 'error' ? '✕' : '⚠'} </span>
                  {d.severity === 'error' ? 'Error' : 'Warning'}
                </span>{' '}
                <span className="rule">{d.id}</span> <span className="position">line {d.line}, column {d.column}</span>: {d.message}
              </button>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
});
