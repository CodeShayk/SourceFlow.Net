// The simulation panel (doc 15 §6.1, WFD-33): start an instance, choose each outcome (or plan them and run), and watch
// its state, data, outbox, timer and compensation stack. Every move is the core's simulator; nothing here decides what
// a workflow does.
import { useEffect, useState, type ReactNode } from 'react';
import type { StepForm } from '../edit/steps.ts';
import { useServices } from '../services.tsx';
import { choicesAt, outbox, planChoices, type GuardMode } from './simulation.ts';
import { useSimulation } from './simulationStore.ts';

interface Props {
  path: string;
  text: string;
  steps: readonly StepForm[];
}

const GUARD_MODES: Array<{ value: GuardMode; label: string }> = [
  { value: 'auto', label: 'Evaluate the guard' },
  { value: 'run', label: 'Run the step' },
  { value: 'skip', label: 'Skip the step' },
];

function Picker({ label, value, options, onChange, disabled }: { label: string; value: string; options: Array<{ value: string; label: string }>; onChange: (value: string) => void; disabled?: boolean }) {
  return (
    <label className="field">
      <span>{label}</span>
      <select value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
        {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </label>
  );
}

function Part({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="section sim-part" aria-label={title}>
      <h3>{title}</h3>
      {children}
    </section>
  );
}

export function SimulationPanel({ path, text, steps }: Props) {
  const { core } = useServices();
  const sim = useSimulation();
  useEffect(() => sim.open(path, text), [path]); // eslint-disable-line react-hooks/exhaustive-deps

  const ids = steps.map((s) => s.id);
  const moves = sim.file === path ? sim.moves : [];
  const result = moves.at(-1)?.result;
  const choices = choicesAt(result, steps);
  const stale = result !== undefined && sim.yaml !== text;
  // The next outcome starts on the first choice after every move.
  const [picked, setPicked] = useState<string | undefined>();
  useEffect(() => setPicked(undefined), [moves.length]);
  const next = choices.find((c) => c.id === picked) ?? choices[0];
  const step = async () => {
    if (next !== undefined) {
      await sim.apply(core, sim.yaml ?? text, next);
    }
  };

  const sent = outbox(moves.map((m) => m.result));
  const timer = result?.timer;
  return (
    <section className="simulation" aria-labelledby="simulation-title">
      <h2 id="simulation-title">Simulate</h2>
      <p className="hint">The core's simulator runs the workflow: start and success guards are assumed to hold, and time moves to each timer as it fires.</p>

      <fieldset className="section">
        <legend>Start</legend>
        <label className="field stacked">
          <span>Start event data (JSON)</span>
          <textarea rows={3} value={sim.startText} spellCheck={false} onChange={(e) => sim.setStartText(e.target.value)} />
        </label>
        {steps.filter((s) => s.when !== undefined).map((s) => (
          <Picker key={s.id} label={`Guard of ${s.id}`} value={sim.guardModes[s.id] ?? 'auto'} options={GUARD_MODES} onChange={(mode) => sim.setGuardMode(s.id, mode as GuardMode)} />
        ))}
        <div className="toolbar">
          <button type="button" className="button" disabled={sim.busy} onClick={() => void sim.start(core, text, ids)}>{result === undefined ? 'Start' : 'Restart'}</button>
          <button type="button" className="button" disabled={sim.busy || moves.length < 2} onClick={() => sim.back()}>Back</button>
          <button type="button" className="button" disabled={sim.busy || moves.length === 0} onClick={() => sim.reset()}>Reset</button>
        </div>
      </fieldset>

      <fieldset className="section">
        <legend>Plan</legend>
        <p className="hint">What each step's wait ends with when you run. Retries fire and compensations succeed on their own.</p>
        {steps.map((s) => (
          <Picker key={s.id} label={`Outcome of ${s.id}`} value={sim.plan[s.id] ?? 'success'} options={planChoices(s).map((c) => ({ value: c.id, label: c.label }))} onChange={(choice) => sim.setPlan(s.id, choice)} />
        ))}
        <button type="button" className="button" disabled={sim.busy || choices.length === 0 || stale} onClick={() => void sim.run(core, text, steps)}>Run to the end</button>
      </fieldset>

      {sim.error !== undefined ? <p role="alert" className="edit-error">{sim.error}</p> : null}
      {stale ? <p role="status" className="warning">The workflow changed since this simulation started: restart it to simulate the new text.</p> : null}
      {sim.note !== undefined ? <p role="status">{sim.note}</p> : null}

      {result === undefined ? (
        <p className="hint">Start an instance to simulate it.</p>
      ) : (
        <>
          <dl className="sim-state" aria-label="Instance">
            <dt>Status</dt><dd data-testid="sim-status">{result.status}</dd>
            {result.step !== undefined ? <><dt>Step</dt><dd>{result.step}</dd></> : null}
            <dt>Phase</dt><dd>{result.phase}</dd>
            <dt>Attempt</dt><dd>{result.attempt}</dd>
            <dt>Time</dt><dd>{result.now}</dd>
            {result.failure !== undefined ? <><dt>Failure</dt><dd>{result.failure.code} at {result.failure.step}: {result.failure.message}</dd></> : null}
            {sim.replayed !== undefined ? <><dt>Path</dt><dd>{sim.replayed}</dd></> : null}
          </dl>

          {choices.length > 0 ? (
            <div className="sim-next">
              <label className="field">
                <span>Next outcome</span>
                <select value={next?.id ?? ''} disabled={sim.busy || stale} onChange={(e) => setPicked(e.target.value)}>
                  {choices.map((c) => <option key={c.id} value={c.id}>{c.label}</option>)}
                </select>
              </label>
              <button type="button" className="button" disabled={sim.busy || stale} onClick={() => void step()}>Step</button>
            </div>
          ) : (
            <p role="status">The instance has ended: {result.status}.</p>
          )}

          <Part title="Compensation stack">
            {result.compensationStack.length === 0 ? <p className="hint">Nothing to undo.</p> : (
              <ol className="sim-list">
                {[...result.compensationStack].reverse().map((s) => <li key={s}>⟲ undo {s}</li>)}
              </ol>
            )}
          </Part>
          <Part title="Timers">
            {timer === undefined ? <p className="hint">No timer is pending.</p> : <p>⏱ {timer.kind} for {timer.step}, attempt {timer.attempt}, due {timer.due}</p>}
          </Part>
          <Part title="Outbox">
            {sent.length === 0 ? <p className="hint">No command sent yet.</p> : (
              <ol className="sim-list">
                {sent.map((c) => (
                  <li key={c.messageId + c.move}>→ {c.type} <code title={c.messageId}>{c.messageId.slice(0, 8)}</code></li>
                ))}
              </ol>
            )}
          </Part>
          <Part title="Data">
            <pre className="sim-data">{JSON.stringify(result.data, null, 2)}</pre>
          </Part>
          <Part title="Moves">
            <ol className="sim-list">
              {moves.map((m, i) => <li key={i}>{m.label} → {m.result.status}{m.result.step === undefined || m.result.terminal ? '' : ` at ${m.result.step}`}</li>)}
            </ol>
          </Part>
        </>
      )}
    </section>
  );
}
