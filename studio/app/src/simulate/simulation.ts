// The simulation panel's logic (doc 15 §6.1, WFD-33): which outcomes the current state can take, the plan a run follows,
// and what the results show. The core simulates; this only chooses its inputs and reads its results.
import type { SimulateResult, SimulationInput, WorkflowPath } from '@sourceflow/core-client';
import type { StepForm } from '../edit/steps.ts';
import { valueAt } from '../edit/yamlEdit.ts';

export type MoveInput = Exclude<SimulationInput, { kind: 'start' }>;

/** One thing that can happen next, with the input that makes it happen. */
export interface Choice {
  id: string;
  label: string;
  input: MoveInput;
}

/** How a guarded step is decided: by its guard on the instance data, or forced to run or to be skipped. */
export type GuardMode = 'auto' | 'run' | 'skip';

/** A run stops after this many moves: a plan that loops (a `goto` taken every time) never ends otherwise. */
export const MAX_MOVES = 200;

/** The simulated start time, so a simulation (and its timers) is the same every time. */
export const SIMULATION_EPOCH = '2026-01-01T00:00:00Z';

/** The outcomes the instance can take next; none when it has ended. */
export function choicesAt(result: SimulateResult | undefined, steps: readonly StepForm[]): Choice[] {
  if (result === undefined || result.terminal) {
    return [];
  }

  if (result.phase === 'Retrying') {
    return [{ id: 'retry', label: '↻ Fire the retry timer', input: { kind: 'timer' } }];
  }

  switch (result.status) {
    case 'ManualIntervention':
      return [
        { id: 'resume', label: '▶ Resume', input: { kind: 'resume' } },
        { id: 'retry-step', label: '↻ Retry the step', input: { kind: 'retry-step' } },
        { id: 'abort-compensate', label: '⟲ Abort and compensate', input: { kind: 'abort', reason: 'aborted in the simulator', compensate: true } },
        { id: 'abort', label: '■ Abort', input: { kind: 'abort', reason: 'aborted in the simulator' } },
      ];
    case 'Compensating':
      return [
        { id: 'compensated', label: '⟲ Compensated', input: { kind: 'succeed' } },
        { id: 'rejected', label: '✕ The compensation is rejected', input: { kind: 'reject', code: 'compensation.rejected' } },
        { id: 'timeout', label: '⏱ The compensation times out', input: { kind: 'timer' } },
      ];
    case 'Running': {
      const step = steps.find((s) => s.id === result.step);
      const choices: Choice[] = [{ id: 'success', label: `✓ ${step?.awaits ?? 'Success'}`, input: { kind: 'succeed' } }];
      for (const failure of step?.failures ?? []) {
        if (failure.rejected !== undefined) {
          choices.push({ id: `rejected:${failure.rejected}`, label: `✕ Rejected ${failure.rejected}`, input: { kind: 'reject', code: failure.rejected } });
        } else if (failure.on !== undefined) {
          choices.push({ id: `event:${failure.on}`, label: `✕ ${failure.on}`, input: { kind: 'fail', event: failure.on } });
        }
      }

      if (step?.timeout !== undefined || result.timer?.kind === 'timeout') {
        choices.push({ id: 'timeout', label: `⏱ Timeout${step?.timeout?.after === undefined ? '' : ` after ${step.timeout.after}`}`, input: { kind: 'timer' } });
      }

      return choices;
    }
    default:
      return [];
  }
}

/** The choices a step offers in the plan a run follows (its outcomes while it waits). */
export function planChoices(step: StepForm): Choice[] {
  return choicesAt({ terminal: false, phase: 'Awaiting', status: 'Running', step: step.id } as SimulateResult, [step]);
}

/** What a run does next: the planned outcome of the waiting step (success when none), or undefined to stop. */
export function nextInPlan(result: SimulateResult | undefined, steps: readonly StepForm[], plan: Readonly<Record<string, string>>): Choice | undefined {
  const choices = choicesAt(result, steps);
  if (result === undefined || choices.length === 0 || result.status === 'ManualIntervention') {
    return undefined;
  }

  const wanted = result.phase === 'Retrying' ? 'retry' : result.status === 'Compensating' ? 'compensated' : plan[result.step ?? ''] ?? 'success';
  return choices.find((c) => c.id === wanted) ?? choices[0];
}

/** The statechart node the instance is at. */
export function activeNode(result: SimulateResult | undefined): string | undefined {
  if (result === undefined) {
    return undefined;
  }

  switch (result.status) {
    case 'Running':
      return result.step;
    case 'Compensating':
      return result.step === undefined ? 'Compensating' : `undo:${result.step}`;
    case 'Aborted':
      return undefined;
    default:
      return result.status;
  }
}

/** Start data to begin with: the correlation field, which every instance needs. */
export function startData(text: string): Record<string, unknown> {
  const correlate = valueAt(text, ['start', 'correlate']);
  const field = typeof correlate === 'string' && correlate.startsWith('data.') ? correlate.slice(5) : 'id';
  return { [field]: 'instance-1' };
}

/**
 * The guard decisions to simulate with: the start and success guards hold (as for the path explorer), and guarded steps
 * follow their mode.
 */
export function guardsFor(stepIds: readonly string[], modes: Readonly<Record<string, GuardMode>>): Record<string, boolean> {
  const guards: Record<string, boolean> = { $start: true };
  for (const id of stepIds) {
    guards[`$success:${id}`] = true;
    if (modes[id] === 'run' || modes[id] === 'skip') {
      guards[id] = modes[id] === 'run';
    }
  }

  return guards;
}

/** The guards that replay a path: its own decisions. */
export function pathGuards(stepIds: readonly string[], path: WorkflowPath): Record<string, boolean> {
  return { ...guardsFor(stepIds, {}), ...path.guards };
}

export interface SentCommand {
  move: number;
  type: string;
  messageId: string;
  fields: Record<string, unknown>;
}

/** Every command the instance sent, in order: its outbox. */
export function outbox(results: readonly SimulateResult[]): SentCommand[] {
  return results.flatMap((result, move) =>
    result.effects.flatMap((e) => (e.kind === 'send' && e.command !== undefined ? [{ move, type: e.command.type, messageId: e.command.messageId, fields: e.command.fields }] : [])),
  );
}
