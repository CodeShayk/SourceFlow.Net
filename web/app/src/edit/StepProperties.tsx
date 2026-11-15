import { useEffect, useState, type ReactNode } from 'react';
import { choicesFor, isOwned, type Palette } from './palette.ts';
import { addFailure, renameStep, retryValue, setOrRemove, stepPath, thenValue, type RetryForm, type StepForm, type Then } from './steps.ts';
import { remove, setValue, valueAt } from './yamlEdit.ts';

type Edit = (change: (text: string) => string) => boolean;

interface Props {
  step: StepForm;
  steps: readonly StepForm[];
  palette: Palette;
  edit: Edit;
  readOnly: boolean;
  /** A rename succeeded: the selection follows the new id. */
  onRenamed?: (id: string) => void;
}

const THENS: Array<{ value: string; label: string }> = [
  { value: 'fail', label: 'fail (compensate)' },
  { value: 'manual', label: 'manual intervention' },
  { value: 'complete', label: 'complete' },
];

function thenKey(then: Then): string {
  return typeof then === 'string' ? then : 'goto:' + then.goto;
}

function parseThen(key: string): Then {
  return key.startsWith('goto:') ? { goto: key.slice(5) } : (key as Then);
}

/** A text input that edits on blur or Enter (one edit, not one per keystroke). */
function TextField({ label, value, onCommit, readOnly, placeholder }: { label: string; value: string; onCommit: (value: string) => void; readOnly: boolean; placeholder?: string }) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  const commit = () => {
    if (draft !== value) {
      onCommit(draft);
    }
  };
  return (
    <label className="field">
      <span>{label}</span>
      <input value={draft} readOnly={readOnly} placeholder={placeholder} onChange={(e) => setDraft(e.target.value)} onBlur={commit}
        onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); commit(); } }} />
    </label>
  );
}

function Select({ label, value, options, onChange, disabled }: { label: string; value: string; options: Array<{ value: string; label: string }>; onChange: (value: string) => void; disabled: boolean }) {
  const all = options.some((o) => o.value === value) ? options : [{ value, label: `${value} (not in the workspace)` }, ...options];
  return (
    <label className="field">
      <span>{label}</span>
      <select value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
        {all.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </label>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <fieldset className="section">
      <legend>{title}</legend>
      {children}
    </fieldset>
  );
}

function thenOptions(steps: readonly StepForm[], self: string) {
  return [...THENS, ...steps.filter((s) => s.id !== self).map((s) => ({ value: 'goto:' + s.id, label: `go to ${s.id}` }))];
}

function RetryFields({ label, retry, onChange, readOnly }: { label: string; retry: RetryForm | undefined; onChange: (retry: RetryForm | undefined) => void; readOnly: boolean }) {
  return (
    <div className="retry">
      <TextField label={`${label}: retries`} value={retry === undefined ? '0' : String(retry.max)} readOnly={readOnly}
        onCommit={(v) => { const max = Number.parseInt(v, 10); onChange(Number.isFinite(max) && max > 0 ? { ...retry, max } : undefined); }} />
      {retry !== undefined ? (
        <>
          <Select label={`${label}: backoff`} value={retry.backoff ?? 'fixed'} disabled={readOnly}
            options={[{ value: 'fixed', label: 'fixed' }, { value: 'exponential', label: 'exponential' }]} onChange={(v) => onChange({ ...retry, backoff: v as RetryForm['backoff'] })} />
          <TextField label={`${label}: first delay`} value={retry.initial ?? ''} placeholder="PT1M" readOnly={readOnly}
            onCommit={(v) => onChange(v === '' ? (({ initial: _, ...rest }) => rest)(retry) : { ...retry, initial: v })} />
        </>
      ) : null}
    </div>
  );
}

/**
 * The properties of a step (doc 15 §6.2): what it sends and how, what it waits for, its failures, retries, timeout,
 * compensation, pivot and guard. Each change is one minimal edit of the text.
 */
export function StepProperties({ step, steps, palette, edit, readOnly, onRenamed }: Props) {
  const i = step.index;
  const choices = choicesFor(palette, step.sends, step.failures.map((f) => f.rejected ?? ''), step.map.map(([f]) => f), step.awaits);
  const commandOptions = palette.commands.map((c) => ({ value: c.ref, label: `${c.ref} (${c.service})` }));
  const successPath = (text: string) => (typeof valueAt(text, stepPath(i, 'await', 'success')) === 'object' ? stepPath(i, 'await', 'success', 'on') : stepPath(i, 'await', 'success'));

  return (
    <form className="properties" aria-label={`Properties of ${step.id}`} onSubmit={(e) => e.preventDefault()}>
      <h2>Step {step.id}</h2>
      <TextField label="Id" value={step.id} readOnly={readOnly} onCommit={(id) => { if (edit((t) => renameStep(t, i, id))) { onRenamed?.(id); } }} />

      <Section title="Send">
        <Select label="Command" value={step.sends ?? ''} disabled={readOnly}
          options={[{ value: '', label: '(waits only: sends nothing)' }, ...commandOptions]}
          onChange={(v) => edit((t) => (v === '' ? setOrRemove(t, stepPath(i, 'send'), undefined) : step.sends === undefined ? setValue(t, stepPath(i, 'send'), { command: v, map: {} }) : setValue(t, stepPath(i, 'send', 'command'), v)))} />
        {!isOwned(palette, step.sends) ? <p role="note" className="warning">No service of the workspace handles {step.sends} (WF-V02).</p> : null}
        {step.sends !== undefined ? (
          <>
            {step.map.map(([field, value]) => (
              <div className="map-row" key={field}>
                <TextField label={`Map ${field}`} value={value} readOnly={readOnly} onCommit={(v) => edit((t) => setValue(t, stepPath(i, 'send', 'map', field), v))} />
                <button type="button" className="button" disabled={readOnly} aria-label={`Remove ${field} from the map`}
                  onClick={() => edit((t) => (step.map.length === 1 ? setValue(t, stepPath(i, 'send', 'map'), {}) : remove(t, stepPath(i, 'send', 'map', field))))}>✕</button>
              </div>
            ))}
            {choices.unmappedFields.length > 0 && !readOnly ? (
              <Select label="Map a field" value="" disabled={readOnly}
                options={[{ value: '', label: '(choose a field)' }, ...choices.unmappedFields.map((f) => ({ value: f, label: f }))]}
                onChange={(f) => { if (f !== '') { edit((t) => setValue(t, stepPath(i, 'send', 'map', f), '$data.' + f)); } }} />
            ) : null}
          </>
        ) : null}
      </Section>

      <Section title="Await">
        <Select label="Success event" value={step.awaits ?? ''} disabled={readOnly}
          options={choices.successEvents.map((e) => ({ value: e, label: e }))}
          onChange={(v) => edit((t) => setValue(t, successPath(t), v))} />
        <ul className="failures" aria-label="Failures">
          {step.failures.map((failure, f) => {
            const name = failure.rejected !== undefined ? `rejection ${failure.rejected}` : `event ${failure.on ?? ''}`;
            return (
              <li key={f}>
                <strong>✕ {name}</strong>
                <Select label={`Then (${name})`} value={thenKey(failure.then)} disabled={readOnly} options={thenOptions(steps, step.id)}
                  onChange={(v) => edit((t) => setValue(t, stepPath(i, 'await', 'failure', f, 'then'), thenValue(parseThen(v))))} />
                <RetryFields label={`Retry (${name})`} retry={failure.retry} readOnly={readOnly}
                  onChange={(r) => edit((t) => setOrRemove(t, stepPath(i, 'await', 'failure', f, 'retry'), r === undefined ? undefined : retryValue(r)))} />
                <button type="button" className="button" disabled={readOnly} aria-label={`Remove the failure on ${name}`}
                  onClick={() => edit((t) => (step.failures.length === 1 ? remove(t, stepPath(i, 'await', 'failure')) : remove(t, stepPath(i, 'await', 'failure', f))))}>Remove</button>
              </li>
            );
          })}
        </ul>
        {!readOnly && (choices.rejections.length > 0 || choices.failureEvents.length > 0) ? (
          <Select label="Add a failure" value="" disabled={readOnly}
            options={[{ value: '', label: '(choose a rejection or event)' }, ...choices.rejections.map((r) => ({ value: 'rejected:' + r, label: `rejected ${r}` })), ...choices.failureEvents.map((e) => ({ value: 'on:' + e, label: `event ${e}` }))]}
            onChange={(v) => {
              if (v !== '') {
                edit((t) => addFailure(t, i, v.startsWith('rejected:') ? { rejected: v.slice(9), then: 'fail' } : { on: v.slice(3), then: 'fail' }));
              }
            }} />
        ) : null}
      </Section>

      <Section title="Timeout">
        <TextField label="Timeout after" value={step.timeout?.after ?? ''} placeholder="PT5M (empty: no timeout)" readOnly={readOnly}
          onCommit={(v) => edit((t) => (v === '' ? setOrRemove(t, stepPath(i, 'timeout'), undefined) : step.timeout === undefined ? setValue(t, stepPath(i, 'timeout'), { after: v, then: 'fail' }) : setValue(t, stepPath(i, 'timeout', 'after'), v)))} />
        {step.timeout !== undefined ? (
          <>
            <Select label="Then (timeout)" value={thenKey(step.timeout.then)} disabled={readOnly} options={thenOptions(steps, step.id)}
              onChange={(v) => edit((t) => setValue(t, stepPath(i, 'timeout', 'then'), thenValue(parseThen(v))))} />
            <RetryFields label="Retry (timeout)" retry={step.timeout.retry} readOnly={readOnly}
              onChange={(r) => edit((t) => setOrRemove(t, stepPath(i, 'timeout', 'retry'), r === undefined ? undefined : retryValue(r)))} />
          </>
        ) : null}
      </Section>

      <Section title="Compensation">
        <Select label="Undo command" value={step.compensate?.sends ?? ''} disabled={readOnly}
          options={[{ value: '', label: '(not compensated)' }, ...commandOptions]}
          onChange={(v) => edit((t) => {
            if (v === '') {
              return setOrRemove(t, stepPath(i, 'compensate'), undefined);
            }

            const undone = palette.commands.find((c) => c.ref === v)?.emits[0];
            return step.compensate === undefined
              ? setValue(t, stepPath(i, 'compensate'), { send: { command: v, map: {} }, ...(undone !== undefined ? { await: { success: undone } } : {}) })
              : setValue(t, stepPath(i, 'compensate', 'send', 'command'), v);
          })} />
        {step.compensate !== undefined ? (
          <Select label="Undone when" value={step.compensate.awaits ?? ''} disabled={readOnly}
            options={[{ value: '', label: '(when sent)' }, ...choicesFor(palette, step.compensate.sends).successEvents.map((e) => ({ value: e, label: e }))]}
            onChange={(v) => edit((t) => setOrRemove(t, stepPath(i, 'compensate', 'await'), v === '' ? undefined : { success: v }))} />
        ) : null}
      </Section>

      <Section title="Flow">
        <label className="field checkbox">
          <input type="checkbox" checked={step.pivot} disabled={readOnly} onChange={(e) => edit((t) => setOrRemove(t, stepPath(i, 'pivot'), e.target.checked ? true : undefined))} />
          <span>◆ Pivot: the point of no return (later failures recover forward)</span>
        </label>
        <TextField label="Guard (when)" value={step.when ?? ''} placeholder="$data.amount > 5000 (empty: always runs)" readOnly={readOnly}
          onCommit={(v) => edit((t) => setOrRemove(t, stepPath(i, 'when'), v === '' ? undefined : v))} />
      </Section>

      <button type="button" className="button danger" disabled={readOnly || steps.length === 1} onClick={() => edit((t) => remove(t, ['steps', i]))}>
        Remove step {step.id}
      </button>
    </form>
  );
}
