// The visual edits of a workflow (doc 15 §6.2): read a step's fields from the text, and turn each change of the
// properties panel or the palette into a minimal text edit (yamlEdit.ts). The text stays the model.
import { isMap, isSeq } from 'yaml';
import { editable, EditError, insertItem, itemLines, parse, remove, setValue, type Value } from './yamlEdit.ts';

export type Then = 'fail' | 'manual' | 'complete' | { goto: string };

export interface RetryForm {
  max: number;
  backoff?: 'fixed' | 'exponential';
  initial?: string;
  cap?: string;
}

export interface FailureForm {
  rejected?: string;
  on?: string;
  then: Then;
  retry?: RetryForm;
}

export interface StepForm {
  index: number;
  id: string;
  sends?: string;
  map: Array<[string, string]>;
  awaits?: string;
  failures: FailureForm[];
  timeout?: { after: string; then: Then; retry?: RetryForm };
  compensate?: { sends?: string; awaits?: string };
  pivot: boolean;
  when?: string;
  next?: string;
}

type Raw = Record<string, unknown>;

function then(value: unknown): Then {
  if (value === 'manual' || value === 'complete') {
    return value;
  }

  if (value !== null && typeof value === 'object' && typeof (value as Raw).goto === 'string') {
    return { goto: (value as Raw).goto as string };
  }

  return 'fail';
}

function retry(value: unknown): RetryForm | undefined {
  if (value === null || typeof value !== 'object') {
    return undefined;
  }

  const raw = value as Raw;
  return {
    max: Number(raw.max ?? 0),
    ...(raw.backoff === 'fixed' || raw.backoff === 'exponential' ? { backoff: raw.backoff } : {}),
    ...(typeof raw.initial === 'string' ? { initial: raw.initial } : {}),
    ...(typeof raw.cap === 'string' ? { cap: raw.cap } : {}),
  };
}

function successOf(value: unknown): string | undefined {
  if (typeof value === 'string') {
    return value;
  }

  return value !== null && typeof value === 'object' && typeof (value as Raw).on === 'string' ? ((value as Raw).on as string) : undefined;
}

/** The steps of a workflow as forms, or [] when the text isn't a readable workflow. */
export function stepsOf(text: string): StepForm[] {
  const doc = parse(text);
  if (doc.errors.length > 0 || !isMap(doc.contents)) {
    return [];
  }

  const steps = (doc.toJS() as Raw).steps;
  if (!Array.isArray(steps)) {
    return [];
  }

  return steps.map((raw: unknown, index): StepForm => {
    const step = (raw ?? {}) as Raw;
    const send = (step.send ?? undefined) as Raw | undefined;
    const wait = (step.await ?? undefined) as Raw | undefined;
    const timeout = (step.timeout ?? undefined) as Raw | undefined;
    const compensate = (step.compensate ?? undefined) as Raw | undefined;
    const failures = Array.isArray(wait?.failure) ? (wait!.failure as Raw[]) : [];
    return {
      index,
      id: String(step.id ?? ''),
      ...(typeof send?.command === 'string' ? { sends: send.command } : {}),
      map: Object.entries((send?.map ?? {}) as Raw).map(([field, value]) => [field, typeof value === 'object' ? JSON.stringify(value) : String(value)]),
      ...(successOf(wait?.success) !== undefined ? { awaits: successOf(wait?.success)! } : {}),
      failures: failures.map((f) => ({
        ...(typeof f.rejected === 'string' ? { rejected: f.rejected } : {}),
        ...(typeof f.on === 'string' ? { on: f.on } : {}),
        then: then(f.then),
        ...(retry(f.retry) !== undefined ? { retry: retry(f.retry)! } : {}),
      })),
      ...(timeout !== undefined && typeof timeout.after === 'string'
        ? { timeout: { after: timeout.after, then: then(timeout.then), ...(retry(timeout.retry) !== undefined ? { retry: retry(timeout.retry)! } : {}) } }
        : {}),
      ...(compensate !== undefined && compensate !== null && typeof compensate === 'object'
        ? { compensate: { ...(typeof (compensate.send as Raw | undefined)?.command === 'string' ? { sends: (compensate.send as Raw).command as string } : {}), ...(successOf((compensate.await as Raw | undefined)?.success) !== undefined ? { awaits: successOf((compensate.await as Raw).success)! } : {}) } }
        : {}),
      pivot: step.pivot === true,
      ...(typeof step.when === 'string' ? { when: step.when } : {}),
      ...(typeof step.next === 'string' ? { next: step.next } : {}),
    };
  });
}

/** The step whose lines hold `line` (1-based), for mapping a diagnostic to the canvas. */
export function stepAtLine(text: string, line: number): number | undefined {
  return stepLocator(text)(line);
}

/** {@link stepAtLine} for many lines: the file is read once. */
export function stepLocator(text: string): (line: number) => number | undefined {
  const items = itemLines(text, ['steps']);
  return (line) => items.find((item) => line >= item.from && line <= item.to)?.index;
}

const stepPath = (index: number, ...rest: Array<string | number>) => ['steps', index, ...rest];

/** Sets a value, or removes the key when `value` is undefined (a missing key is left alone). */
export function setOrRemove(text: string, path: Array<string | number>, value: Value | undefined): string {
  if (value !== undefined) {
    return setValue(text, path, value);
  }

  try {
    return remove(text, path);
  } catch (error) {
    if (error instanceof EditError && error.message.startsWith('No ')) {
      return text;
    }

    throw error;
  }
}

/** Renames a step and every `next` and `goto` that points at it. */
export function renameStep(text: string, index: number, id: string): string {
  const steps = stepsOf(text);
  const old = steps[index]?.id;
  if (old === undefined) {
    throw new EditError(`No step ${index}.`);
  }

  if (!/^[a-z][a-z0-9-]*$/.test(id)) {
    throw new EditError(`'${id}' is not a step id (lowercase letters, digits and dashes, starting with a letter).`);
  }

  if (steps.some((s) => s.id === id && s.index !== index)) {
    throw new EditError(`A step '${id}' already exists.`);
  }

  let next = setValue(text, stepPath(index, 'id'), id);
  for (const step of steps) {
    if (step.next === old) {
      next = setValue(next, stepPath(step.index, 'next'), id);
    }

    step.failures.forEach((failure, f) => {
      if (typeof failure.then === 'object' && failure.then.goto === old) {
        next = setValue(next, stepPath(step.index, 'await', 'failure', f, 'then', 'goto'), id);
      }
    });
    if (step.timeout !== undefined && typeof step.timeout.then === 'object' && step.timeout.then.goto === old) {
      next = setValue(next, stepPath(step.index, 'timeout', 'then', 'goto'), id);
    }
  }

  return next;
}

export function thenValue(value: Then): Value {
  return typeof value === 'string' ? value : { goto: value.goto };
}

export function retryValue(value: RetryForm): Value {
  return {
    max: value.max,
    ...(value.backoff !== undefined ? { backoff: value.backoff } : {}),
    ...(value.initial !== undefined ? { initial: value.initial } : {}),
    ...(value.cap !== undefined ? { cap: value.cap } : {}),
  };
}

/** Adds a step after `after` (or first when -1) that sends `command` and awaits `success`. */
export function addStep(text: string, after: number, step: { id: string; command?: string; success: string; map?: Record<string, string> }): string {
  const value: Record<string, Value> = { id: step.id };
  if (step.command !== undefined) {
    value.send = { command: step.command, map: step.map ?? {} };
  }

  value.await = { success: step.success };
  const doc = editable(text);
  const steps = doc.getIn(['steps']);
  if (!isSeq(steps)) {
    throw new EditError("The workflow has no 'steps' list.");
  }

  return insertItem(text, ['steps'], Math.min(after + 1, steps.items.length), value);
}

/** Adds a failure to a step: a rejection code or a failure event, and what follows. */
export function addFailure(text: string, index: number, failure: FailureForm): string {
  const value: Record<string, Value> = {
    ...(failure.rejected !== undefined ? { rejected: failure.rejected } : {}),
    ...(failure.on !== undefined ? { on: failure.on } : {}),
    ...(failure.retry !== undefined ? { retry: retryValue(failure.retry) } : {}),
    then: thenValue(failure.then),
  };
  const existing = stepsOf(text)[index]?.failures.length ?? 0;
  return existing > 0 ? insertItem(text, stepPath(index, 'await', 'failure'), existing, value) : setValue(text, stepPath(index, 'await', 'failure'), [value]);
}

/** The step id a new step gets: the command's verb, unique in the workflow. */
export function newStepId(text: string, command: string | undefined): string {
  const base = (command ?? 'wait').replace(/\.v\d+$/, '').split('.').pop()!.replace(/[^a-z0-9-]/g, '-').replace(/^-+|-+$/g, '') || 'step';
  const ids = new Set(stepsOf(text).map((s) => s.id));
  let id = /^[a-z]/.test(base) ? base : 'step-' + base;
  for (let n = 2; ids.has(id); n++) {
    id = `${base}-${n}`;
  }

  return id;
}

export { stepPath };
