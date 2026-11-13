import type { Diagnostic } from '@sourceflow/core-client';
import { forwardRef, useEffect, useRef, useState } from 'react';
import type { ServiceDto, WorkspaceDto } from '../api.ts';
import { Tabs } from '../components/Tabs.tsx';
import { StatechartView } from '../views/StatechartView.tsx';
import { SwimlaneView } from '../views/SwimlaneView.tsx';

interface Props {
  path: string;
  owner: ServiceDto | undefined;
  workspace: WorkspaceDto;
  text: string | undefined;
  /** The selected diagnostic: the source tab opens on its line. */
  selected: Diagnostic | undefined;
  error?: string;
}

/** An open workflow: its statechart, the swimlane of a path, and its source (the editor arrives with WP-S.10). */
export const WorkflowView = forwardRef<HTMLElement, Props>(function WorkflowView({ path, owner, workspace, text, selected, error }, ref) {
  const [tab, setTab] = useState('statechart');
  useEffect(() => {
    if (selected !== undefined) {
      setTab('source');
    }
  }, [selected]);
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
        <Tabs
          label={`Views of ${name}`}
          selected={tab}
          onSelect={setTab}
          tabs={[
            { id: 'statechart', label: 'Statechart', content: () => <StatechartView text={text} name={name} /> },
            { id: 'swimlane', label: 'Swimlane', content: () => <SwimlaneView text={text} workspace={workspace} name={name} /> },
            { id: 'source', label: 'Source', content: () => <Source text={text} name={name} selected={selected} /> },
          ]}
        />
      )}
    </article>
  );
});

function Source({ text, name, selected }: { text: string; name: string; selected: Diagnostic | undefined }) {
  const marked = useRef<HTMLLIElement | null>(null);
  useEffect(() => marked.current?.scrollIntoView?.({ block: 'nearest' }), [selected]);
  return (
    <ol className="source" aria-label={`Source of ${name}`}>
      {text.split('\n').map((line, index) => {
        const number = index + 1;
        const isMarked = selected?.line === number;
        return (
          <li key={number} ref={isMarked ? marked : undefined} className={isMarked ? `marked ${selected!.severity}` : undefined} aria-current={isMarked ? 'location' : undefined}>
            <code>{line.length === 0 ? ' ' : line}</code>
          </li>
        );
      })}
    </ol>
  );
}
