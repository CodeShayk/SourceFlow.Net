import type { SequenceResult, WorkflowPath } from '@sourceflow/core-client';
import { useEffect, useState } from 'react';
import type { WorkspaceDto } from '../api.ts';
import { arrow, describe, MESSAGE_KINDS, pathChoice } from '../graph/sequence.ts';
import { usePaths, useSequence } from '../services.tsx';

/**
 * The swimlane sequence (doc 15 §6.1, WFD-31): one lane per service, and the ordered commands and events of the path
 * selected, including retries and compensations.
 */
export function SwimlaneView({ text, workspace, name }: { text: string; workspace: WorkspaceDto; name: string }) {
  const paths = usePaths(text);
  const [selected, setSelected] = useState<string | undefined>();
  const first = paths.data?.minimalCover[0] ?? paths.data?.paths[0]?.id;
  useEffect(() => setSelected(undefined), [text]);
  const path = selected ?? first;
  const sequence = useSequence(text, workspace, path);

  if (paths.isError) {
    return <div role="alert" className="view-message">The swimlane needs a readable workflow: {(paths.error as Error).message}</div>;
  }

  if (paths.data === undefined) {
    return <p role="status">Enumerating the paths…</p>;
  }

  return (
    <section className="swimlane" aria-label={`Swimlane of ${name}`}>
      <label className="path-picker">
        Path{' '}
        <select value={path ?? ''} onChange={(event) => setSelected(event.target.value)}>
          {paths.data.paths.map((p: WorkflowPath) => (
            <option key={p.id} value={p.id}>
              {pathChoice(p)}
              {paths.data.minimalCover.includes(p.id) ? ' · in the minimal cover' : ''}
            </option>
          ))}
        </select>
      </label>
      {paths.data.truncated ? <p role="note">Only the first {paths.data.paths.length} paths are listed.</p> : null}
      {sequence.isError ? <p role="alert">{(sequence.error as Error).message}</p> : sequence.data === undefined ? <p role="status">Replaying the path…</p> : <Lanes sequence={sequence.data} />}
    </section>
  );
}

function Lanes({ sequence }: { sequence: SequenceResult }) {
  const columns = `repeat(${sequence.lanes.length}, minmax(9rem, 1fr))`;
  return (
    <div className="lanes" data-lanes={sequence.lanes.length} data-messages={sequence.messages.length}>
      <p className="path-text">{sequence.path.text}</p>
      <div className="lane-heads" style={{ gridTemplateColumns: columns }} role="presentation">
        {sequence.lanes.map((lane) => (
          <div key={lane.id} className={`lane-head lane-${lane.kind}`}>
            {lane.label}
          </div>
        ))}
      </div>
      <ol className="messages" aria-label={`Messages of ${sequence.path.id}`}>
        {sequence.messages.map((message, index) => {
          const a = arrow(sequence.lanes, message);
          return (
            <li key={index} className={`message message-${message.kind}`} style={{ gridTemplateColumns: columns }} aria-label={describe(sequence, message)}>
              <div className={`arrow arrow-${a.direction}`} style={{ gridColumn: `${a.columnStart} / ${a.columnEnd}` }} aria-hidden="true">
                <svg className="arrow-line" preserveAspectRatio="none" viewBox="0 0 100 10" aria-hidden="true">
                  <line x1="2" y1="5" x2="98" y2="5" vectorEffect="non-scaling-stroke" style={{ strokeDasharray: MESSAGE_KINDS[message.kind].dash || undefined }} />
                </svg>
                <span className="arrow-label">
                  {a.direction === 'left' ? '◀ ' : ''}
                  {message.label}
                  {message.attempt !== undefined && message.attempt > 1 ? ` (attempt ${message.attempt})` : ''}
                  {a.direction === 'right' ? ' ▶' : ''}
                </span>
              </div>
            </li>
          );
        })}
      </ol>
    </div>
  );
}
