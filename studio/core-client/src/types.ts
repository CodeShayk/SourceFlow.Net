// The JSON contract of SourceFlow.Studio.Dsl.Api.StudioApi (WP-S.7). The exports are string → string; these are the
// shapes inside the strings. Keep them in step with StudioApi.cs: the WASM tests check real responses against them.

/** A diagnostic at a YAML position (WF-S*, WF-V*). */
export interface Diagnostic {
  id: string;
  severity: 'error' | 'warning';
  line: number;
  column: number;
  message: string;
}

/** The domain's service manifests, as the JSON text `manifest export` writes. */
export type Manifests = readonly string[];

export interface VersionResult {
  api: number;
  dsl: string;
}

export interface ParseRequest {
  yaml: string;
}

export interface ParseResult {
  /** The workflow model (the generator's JSON form), or null when the text isn't a workflow. */
  workflow: WorkflowModel | null;
  diagnostics: Diagnostic[];
}

/** The workflow model; its full shape is the generator's (WorkflowJson.cs). */
export interface WorkflowModel {
  name: string;
  version: number;
  owner: string;
  start: { on: string; correlate: string; when?: string };
  steps: Array<{ id: string; [key: string]: unknown }>;
  [key: string]: unknown;
}

export interface ValidateRequest {
  yaml: string;
  manifests: Manifests;
}

export interface ValidateResult {
  valid: boolean;
  /** `sha256:` and 16 hex digits (WF-V15); absent when the text isn't a workflow. */
  fingerprint?: string;
  diagnostics: Diagnostic[];
}

export type SimulationInput =
  | { kind: 'start'; data: Record<string, unknown> }
  | { kind: 'succeed'; data?: Record<string, unknown> }
  | { kind: 'fail'; event: string; data?: Record<string, unknown> }
  | { kind: 'reject'; code: string; message?: string }
  | { kind: 'rejected'; command: string; code: string; message?: string }
  | { kind: 'receive'; type: string; data?: Record<string, unknown> }
  | { kind: 'timer'; timer?: 'timeout' | 'retry'; step?: string; attempt?: number }
  | { kind: 'resume'; step?: string }
  | { kind: 'retry-step' }
  | { kind: 'abort'; reason?: string; compensate?: boolean };

export interface SimulateRequest {
  yaml: string;
  /** The opaque state a previous step returned; absent for `start`. */
  state?: string;
  input: SimulationInput;
  /** Forced guard decisions by step id (`$start`, `$success:{step}`). */
  guards?: Record<string, boolean>;
  /** ISO-8601 simulated time (start time, or a later time for the move). */
  now?: string;
}

export type WorkflowStatus = 'Running' | 'Completed' | 'Compensating' | 'Compensated' | 'ManualIntervention' | 'Aborted';

export type TransitionKind = 'success' | 'failure' | 'retry' | 'timeout' | 'compensate' | 'manual' | 'complete';

export interface SimulationTimer {
  kind: 'timeout' | 'retry';
  step: string;
  attempt: number;
  due: string;
}

export interface SimulationEffect {
  kind: 'send' | 'schedule' | 'cancel' | 'status' | 'capture' | 'skip' | 'alert' | 'ignored';
  text: string;
  command?: { type: string; messageId: string; fields: Record<string, unknown> };
  timer?: SimulationTimer;
}

export interface SimulateResult {
  /** Pass back as `state` with the next move. */
  state: string;
  instance: string;
  status: WorkflowStatus;
  step?: string;
  phase: 'Awaiting' | 'Retrying';
  attempt: number;
  now: string;
  terminal: boolean;
  timer?: SimulationTimer;
  failure?: { code: string; message: string; step: string };
  compensationStack: string[];
  data: Record<string, unknown>;
  effects: SimulationEffect[];
  transitions: Array<{ kind: TransitionKind; step?: string; attempt: number }>;
}

export interface PathsRequest {
  yaml: string;
  maxStepEntries?: number;
  maxPaths?: number;
  maxMoves?: number;
}

/** One move of a path: the statechart edge it covers, and the input that takes it (none for a skipped step). */
export interface PathMove {
  step: string;
  outcome: string;
  edge: string;
  retryEdge?: string;
  /** Pass to `simulateStep` (with the path's guards) to replay the path. */
  input?: Exclude<SimulationInput, { kind: 'start' }>;
}

export interface WorkflowPath {
  id: string;
  number: number;
  text: string;
  end: 'Completed' | 'Compensated' | 'ManualIntervention';
  moves: PathMove[];
  guards: Record<string, boolean>;
}

export interface PathsResult {
  paths: WorkflowPath[];
  edges: string[];
  uncovered: string[];
  truncated: boolean;
  /** Path ids of the smallest set that covers every covered edge (the default for specs). */
  minimalCover: string[];
}

export interface ExportRequest {
  yaml: string;
}

export interface TextResult {
  text: string;
}

export interface SpecsRequest {
  yaml: string;
  manifests: Manifests;
  namespace?: string;
  moduleType?: string;
  workflowType?: string;
  allPaths?: boolean;
  pathIds?: readonly string[];
}

export type NodeKind = 'start' | 'step' | 'pivot' | 'compensation' | 'compensating' | 'terminal' | 'manual';

export type EdgeKind = 'start' | 'success' | 'failure' | 'retry' | 'timeout' | 'compensation' | 'manual' | 'skip';

/** A statechart node (doc 15 §6.1). */
export interface GraphNode {
  id: string;
  kind: NodeKind;
  label: string;
  sends?: string;
  awaits?: string;
  when?: string;
  timeout?: string;
  compensable: boolean;
  /** A compensation's place in the undo order (1 runs first). */
  order?: number;
  parent?: string;
}

/** A statechart edge: its kind, a label starting with the kind's symbol, and a sentence for assistive technology. */
export interface GraphEdge {
  id: string;
  source: string;
  target: string;
  kind: EdgeKind;
  label: string;
  description: string;
}

export interface GraphResult {
  nodes: GraphNode[];
  edges: GraphEdge[];
}

export interface SequenceRequest extends PathsRequest {
  manifests: Manifests;
  /** The path id, for example `P03`. */
  path: string;
}

export type MessageKind = 'start' | 'command' | 'event' | 'rejection' | 'failure' | 'timer' | 'compensation' | 'status' | 'manual';

export interface SequenceLane {
  id: string;
  kind: 'workflow' | 'service' | 'external';
  label: string;
}

export interface SequenceMessage {
  from: string;
  to: string;
  kind: MessageKind;
  label: string;
  step?: string;
  type?: string;
  attempt?: number;
}

export interface SequenceResult {
  path: { id: string; text: string; end: string };
  lanes: SequenceLane[];
  messages: SequenceMessage[];
}

/** The requests and results of each operation. */
export interface Operations {
  version: [Record<string, never>, VersionResult];
  parse: [ParseRequest, ParseResult];
  validate: [ValidateRequest, ValidateResult];
  simulateStep: [SimulateRequest, SimulateResult];
  enumeratePaths: [PathsRequest, PathsResult];
  toMermaid: [ExportRequest, TextResult];
  toBpmn: [ExportRequest, TextResult];
  generateSpecs: [SpecsRequest, TextResult];
  graph: [ExportRequest, GraphResult];
  sequence: [SequenceRequest, SequenceResult];
}

export type Operation = keyof Operations;

/** The envelope every export returns. */
export type Envelope<T> = { ok: true; result: T } | { ok: false; error: string; diagnostics?: Diagnostic[] };
