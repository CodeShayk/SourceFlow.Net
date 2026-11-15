import { useState } from 'react';
import { choicesFor, type Palette } from './palette.ts';
import { addStep, newStepId, type StepForm } from './steps.ts';

/**
 * The palette (doc 15 §6.2): the commands the domain's services own, grouped by service; adding one creates a step
 * after the selected one that sends it and waits for an event it emits (WF-V02, WF-V03).
 */
export function PalettePanel({ palette, steps, selected, edit, readOnly, onAdded }: {
  palette: Palette;
  steps: readonly StepForm[];
  selected: number | undefined;
  edit: (change: (text: string) => string) => boolean;
  readOnly: boolean;
  onAdded: (id: string) => void;
}) {
  const [command, setCommand] = useState('');
  const [event, setEvent] = useState('');
  const events = choicesFor(palette, command === '' ? undefined : command).successEvents;
  const services = palette.services;
  const after = selected ?? steps.length - 1;
  const success = event !== '' && events.includes(event) ? event : events[0] ?? '';

  return (
    <section className="palette" aria-label="Palette">
      <h2>Add a step</h2>
      <p className="hint">{selected === undefined ? 'At the end of the workflow.' : `After ${steps[selected]?.id ?? 'the selected step'}.`}</p>
      <label className="field">
        <span>Command</span>
        <select value={command} disabled={readOnly} onChange={(e) => { setCommand(e.target.value); setEvent(''); }}>
          <option value="">(waits only: sends nothing)</option>
          {services.map((service) => (
            <optgroup key={service} label={service}>
              {palette.commands.filter((c) => c.service === service).map((c) => <option key={c.ref} value={c.ref}>{c.ref}</option>)}
            </optgroup>
          ))}
        </select>
      </label>
      <label className="field">
        <span>Waits for</span>
        <select value={success} disabled={readOnly || events.length === 0} onChange={(e) => setEvent(e.target.value)}>
          {events.map((e) => <option key={e} value={e}>{e}</option>)}
        </select>
      </label>
      <button type="button" className="button" disabled={readOnly || success === ''}
        onClick={(e) => {
          const button = e.currentTarget;
          let added = '';
          if (edit((t) => {
            added = newStepId(t, command === '' ? undefined : command);
            const map = Object.fromEntries((palette.commands.find((c) => c.ref === command)?.fields ?? []).slice(0, 1).map((f) => [f, '$data.' + f]));
            return addStep(t, after, { id: added, ...(command === '' ? {} : { command, map }), success });
          })) {
            onAdded(added);
            button.blur();
          }
        }}>
        Add step
      </button>
    </section>
  );
}
