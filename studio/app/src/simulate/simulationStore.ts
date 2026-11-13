// The simulation of the open workflow, kept while its tabs come and go: the results so far (each the core's answer to
// one move), the start data, guard modes and the plan a run follows, and the paths chosen in the path explorer.
import type { SimulateResult, WorkflowPath } from '@sourceflow/core-client';
import { create } from 'zustand';
import type { StepForm } from '../edit/steps.ts';
import type { StudioCore } from '../services.tsx';
import { guardsFor, MAX_MOVES, nextInPlan, pathGuards, SIMULATION_EPOCH, startData, type Choice, type GuardMode } from './simulation.ts';

type Core = Pick<StudioCore, 'simulateStep'>;

export interface Move {
  label: string;
  result: SimulateResult;
}

export interface SimulationState {
  file: string | undefined;
  /** The text the moves were simulated on: a changed workflow needs a new simulation. */
  yaml: string | undefined;
  startText: string;
  guardModes: Record<string, GuardMode>;
  plan: Record<string, string>;
  guards: Record<string, boolean>;
  moves: Move[];
  busy: boolean;
  error: string | undefined;
  note: string | undefined;
  /** The path the moves replay, when they came from the path explorer. */
  replayed: string | undefined;
  /** The paths chosen in the path explorer; undefined is the minimal cover. */
  selectedPaths: string[] | undefined;

  open(file: string, text: string): void;
  setStartText(text: string): void;
  setGuardMode(step: string, mode: GuardMode): void;
  setPlan(step: string, choice: string): void;
  setSelectedPaths(paths: string[] | undefined): void;
  start(core: Core, yaml: string, stepIds: readonly string[]): Promise<boolean>;
  apply(core: Core, yaml: string, choice: Choice): Promise<boolean>;
  run(core: Core, yaml: string, steps: readonly StepForm[]): Promise<void>;
  replay(core: Core, yaml: string, path: WorkflowPath, stepIds: readonly string[]): Promise<void>;
  back(): void;
  reset(): void;
}

const fresh = { yaml: undefined, guards: {}, moves: [], busy: false, error: undefined, note: undefined, replayed: undefined };

export function createSimulationStore() {
  return create<SimulationState>()((set, get) => {
    const current = () => get().moves.at(-1)?.result;

    async function call(core: Core, request: Parameters<Core['simulateStep']>[0], label: string): Promise<boolean> {
      try {
        const result = await core.simulateStep(request);
        set((s) => ({ moves: [...s.moves, { label, result }], error: undefined }));
        return true;
      } catch (error) {
        set({ error: (error as Error).message });
        return false;
      }
    }

    return {
      file: undefined,
      startText: '{}',
      guardModes: {},
      plan: {},
      selectedPaths: undefined,
      ...fresh,

      open(file, text) {
        if (get().file !== file) {
          set({ ...fresh, file, startText: JSON.stringify(startData(text), null, 2), guardModes: {}, plan: {}, selectedPaths: undefined });
        }
      },
      setStartText(startText) {
        set({ startText });
      },
      setGuardMode(step, mode) {
        set((s) => ({ guardModes: { ...s.guardModes, [step]: mode } }));
      },
      setPlan(step, choice) {
        set((s) => ({ plan: { ...s.plan, [step]: choice } }));
      },
      setSelectedPaths(selectedPaths) {
        set({ selectedPaths });
      },

      async start(core, yaml, stepIds) {
        let data: unknown;
        try {
          data = JSON.parse(get().startText);
        } catch (error) {
          set({ error: `The start data isn't JSON: ${(error as Error).message}` });
          return false;
        }

        if (data === null || typeof data !== 'object' || Array.isArray(data)) {
          set({ error: 'The start data is a JSON object, for example { "orderId": "o-1" }.' });
          return false;
        }

        const guards = guardsFor(stepIds, get().guardModes);
        set({ ...fresh, yaml, guards, busy: true });
        const started = await call(core, { yaml, input: { kind: 'start', data: data as Record<string, unknown> }, guards, now: SIMULATION_EPOCH }, '▶ Start');
        set({ busy: false });
        return started;
      },

      async apply(core, yaml, choice) {
        const result = current();
        if (result === undefined) {
          return false;
        }

        set({ busy: true });
        const applied = await call(core, { yaml, state: result.state, input: choice.input, guards: get().guards }, (result.step === undefined ? '' : `${result.step}: `) + choice.label);
        set({ busy: false });
        return applied;
      },

      async run(core, yaml, steps) {
        set({ busy: true, note: undefined });
        let moves = 0;
        for (let next = nextInPlan(current(), steps, get().plan); next !== undefined; next = nextInPlan(current(), steps, get().plan)) {
          if (moves++ === MAX_MOVES) {
            set({ note: `Stopped after ${MAX_MOVES} moves: the plan goes round a loop. Change an outcome in the plan, or step on.` });
            break;
          }

          if (!(await get().apply(core, yaml, next))) {
            break;
          }

          set({ busy: true });
        }

        const end = current();
        if (end?.status === 'ManualIntervention') {
          set({ note: 'Stopped at manual intervention: choose what the operator does.' });
        }

        set({ busy: false });
      },

      async replay(core, yaml, path, stepIds) {
        const guards = pathGuards(stepIds, path);
        set({ ...fresh, yaml, guards, busy: true, replayed: path.id, startText: JSON.stringify(startData(yaml), null, 2) });
        if (await call(core, { yaml, input: { kind: 'start', data: startData(yaml) }, guards, now: SIMULATION_EPOCH }, '▶ Start')) {
          for (const move of path.moves) {
            const result = current()!;
            const label = `${move.step}: ${move.outcome}`;
            if (move.input === undefined) {
              continue; // a skipped step: the start or the previous move already skipped it
            }

            if (!(await call(core, { yaml, state: result.state, input: move.input, guards }, label))) {
              break;
            }
          }
        }

        set({ busy: false });
      },

      back() {
        set((s) => ({ moves: s.moves.slice(0, -1), error: undefined, note: undefined }));
      },
      reset() {
        set({ ...fresh });
      },
    };
  });
}

export const useSimulation = createSimulationStore();
