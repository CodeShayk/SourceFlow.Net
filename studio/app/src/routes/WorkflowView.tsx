import type { Diagnostic } from '@sourceflow/core-client';
import { forwardRef, useEffect, useRef } from 'react';
import type { ServiceDto } from '../api.ts';

interface Props {
  path: string;
  owner: ServiceDto | undefined;
  text: string | undefined;
  /** The selected diagnostic: its line is highlighted and scrolled to. */
  selected: Diagnostic | undefined;
  error?: string;
}

/**
 * An open workflow: its file with line numbers and the selected diagnostic's line marked. The statechart (WP-S.9) and
 * the editor (WP-S.10) replace this view's body.
 */
export const WorkflowView = forwardRef<HTMLElement, Props>(function WorkflowView({ path, owner, text, selected, error }, ref) {
  const marked = useRef<HTMLLIElement | null>(null);
  useEffect(() => marked.current?.scrollIntoView?.({ block: 'nearest' }), [selected]);
  const name = path.slice(path.lastIndexOf('/') + 1);
  return (
    <article ref={ref} className="view workflow" aria-labelledby="workflow-title" tabIndex={-1}>
      <h1 id="workflow-title">{name}</h1>
      <p className="lead">
        {owner !== undefined ? <>Owned by <strong>{owner.name}</strong> · </> : null}
        <code>{path}</code>
      </p>
      {error !== undefined ? (
        <p role="alert">{error}</p>
      ) : text === undefined ? (
        <p role="status">Loading…</p>
      ) : (
        <ol className="source" aria-label={`Source of ${name}`}>
          {text.split('\n').map((line, index) => {
            const number = index + 1;
            const isMarked = selected?.line === number;
            return (
              <li key={number} ref={isMarked ? marked : undefined} className={isMarked ? `marked ${selected!.severity}` : undefined}
                aria-current={isMarked ? 'location' : undefined}>
                <code>{line.length === 0 ? ' ' : line}</code>
              </li>
            );
          })}
        </ol>
      )}
    </article>
  );
});
