// The typed client of the Studio core (WP-S.7, ADR-025). The core runs in a worker, loaded on the first call; every
// operation is pure, so results are cached; a call can be cancelled with an AbortSignal (the worker is restarted, as
// WebAssembly can't be interrupted, and the other calls in flight are sent again).
import type { HostInit, HostReply, HostRequest } from './host.ts';
import type { Diagnostic, Envelope, Operation, Operations } from './types.ts';

/** What the client needs of a worker: a browser `Worker`, or an adapter over a Node worker thread in tests. */
export interface WorkerLike {
  postMessage(message: HostRequest | HostInit): void;
  terminate(): void;
  onmessage: ((event: { data: HostReply }) => void) | null;
  onerror?: ((event: unknown) => void) | null;
}

export interface StudioCoreOptions {
  /** Creates the worker (called lazily, again after a cancellation). */
  createWorker: () => WorkerLike;
  /** Where the runtime's `_framework` folder is served. */
  frameworkUrl: string;
  /** How many results to keep (least recently used first out); 0 disables the cache. Default 64. */
  cacheSize?: number;
}

export interface CallOptions {
  /** Cancels the call: it rejects with an `AbortError` and the worker restarts. */
  signal?: AbortSignal;
}

/** A failed operation: the core's message, with the workflow's diagnostics when it doesn't read. */
export class StudioCoreError extends Error {
  readonly diagnostics: readonly Diagnostic[];

  constructor(message: string, diagnostics: readonly Diagnostic[] = []) {
    super(message);
    this.name = 'StudioCoreError';
    this.diagnostics = diagnostics;
  }
}

interface Pending {
  op: Operation;
  request: string;
  resolve: (response: string) => void;
  reject: (error: unknown) => void;
}

export class StudioCoreClient {
  private readonly options: StudioCoreOptions;
  private readonly cache = new Map<string, unknown>();
  private readonly pending = new Map<number, Pending>();
  private worker: WorkerLike | undefined;
  private nextId = 1;
  private disposed = false;

  constructor(options: StudioCoreOptions) {
    this.options = options;
  }

  /** Whether the worker is running (it starts on the first call). */
  get started(): boolean {
    return this.worker !== undefined;
  }

  version(options?: CallOptions) {
    return this.call('version', {}, options);
  }

  /** Reads a workflow (structural diagnostics only). */
  parse(request: Operations['parse'][0], options?: CallOptions) {
    return this.call('parse', request, options);
  }

  /** Reads and validates a workflow against the domain's manifests (WF-S*, WF-V*). */
  validate(request: Operations['validate'][0], options?: CallOptions) {
    return this.call('validate', request, options);
  }

  /** One simulator move (doc 15 §5.3). */
  simulateStep(request: Operations['simulateStep'][0], options?: CallOptions) {
    return this.call('simulateStep', request, options);
  }

  /** Every distinct path, with edge coverage. */
  enumeratePaths(request: Operations['enumeratePaths'][0], options?: CallOptions) {
    return this.call('enumeratePaths', request, options);
  }

  /** The Mermaid `stateDiagram-v2`. */
  toMermaid(request: Operations['toMermaid'][0], options?: CallOptions) {
    return this.call('toMermaid', request, options);
  }

  /** The BPMN 2.0 XML. */
  toBpmn(request: Operations['toBpmn'][0], options?: CallOptions) {
    return this.call('toBpmn', request, options);
  }

  /** `WorkflowSpec` tests (C#) for the paths. */
  generateSpecs(request: Operations['generateSpecs'][0], options?: CallOptions) {
    return this.call('generateSpecs', request, options);
  }

  /** Stops the worker; pending calls reject. */
  dispose(): void {
    this.disposed = true;
    this.stop(new StudioCoreError('The Studio core client was disposed.'));
  }

  private async call<K extends Operation>(op: K, request: Operations[K][0], options?: CallOptions): Promise<Operations[K][1]> {
    if (this.disposed) {
      throw new StudioCoreError('The Studio core client was disposed.');
    }

    options?.signal?.throwIfAborted();
    const text = JSON.stringify(request);
    const key = op + '\u0000' + text;
    if (this.cache.has(key)) {
      const hit = this.cache.get(key) as Operations[K][1];
      this.cache.delete(key);
      this.cache.set(key, hit);
      return hit;
    }

    const response = await this.send(op, text, options?.signal);
    const envelope = JSON.parse(response) as Envelope<Operations[K][1]>;
    if (!envelope.ok) {
      throw new StudioCoreError(envelope.error, envelope.diagnostics ?? []);
    }

    this.remember(key, envelope.result);
    return envelope.result;
  }

  private send(op: Operation, request: string, signal: AbortSignal | undefined): Promise<string> {
    return new Promise<string>((resolve, reject) => {
      const id = this.nextId++;
      const entry: Pending = { op, request, resolve, reject };
      this.pending.set(id, entry);
      if (signal !== undefined) {
        const abort = () => this.cancel(id, signal.reason);
        signal.addEventListener('abort', abort, { once: true });
        const settle = (finish: () => void) => () => {
          signal.removeEventListener('abort', abort);
          finish();
        };
        entry.resolve = (value) => settle(() => resolve(value))();
        entry.reject = (error) => settle(() => reject(error))();
      }

      this.ensureWorker().postMessage({ id, op, request });
    });
  }

  // The worker can't be interrupted: stop it, fail this call, and send the others to a new one.
  private cancel(id: number, reason: unknown): void {
    const cancelled = this.pending.get(id);
    if (cancelled === undefined) {
      return;
    }

    this.pending.delete(id);
    this.worker?.terminate();
    this.worker = undefined;
    cancelled.reject(reason instanceof Error ? reason : new DOMException('The Studio core call was cancelled.', 'AbortError'));
    if (this.pending.size > 0) {
      const worker = this.ensureWorker();
      for (const [other, entry] of this.pending) {
        worker.postMessage({ id: other, op: entry.op, request: entry.request });
      }
    }
  }

  private ensureWorker(): WorkerLike {
    if (this.worker !== undefined) {
      return this.worker;
    }

    const worker = this.options.createWorker();
    worker.onmessage = (event) => this.receive(event.data);
    worker.onerror = (event) => this.stop(new StudioCoreError('The Studio core worker failed: ' + describe(event)));
    worker.postMessage({ id: 0, init: this.options.frameworkUrl });
    this.worker = worker;
    return worker;
  }

  private receive(reply: HostReply): void {
    const entry = this.pending.get(reply.id);
    if (entry === undefined) {
      return; // a reply for a call that was cancelled
    }

    this.pending.delete(reply.id);
    if ('failure' in reply) {
      entry.reject(new StudioCoreError(reply.failure));
    } else {
      entry.resolve(reply.response);
    }
  }

  private stop(error: Error): void {
    this.worker?.terminate();
    this.worker = undefined;
    const pending = [...this.pending.values()];
    this.pending.clear();
    for (const entry of pending) {
      entry.reject(error);
    }
  }

  private remember(key: string, result: unknown): void {
    const size = this.options.cacheSize ?? 64;
    if (size <= 0) {
      return;
    }

    this.cache.set(key, result);
    while (this.cache.size > size) {
      const oldest = this.cache.keys().next().value as string;
      this.cache.delete(oldest);
    }
  }
}

function describe(event: unknown): string {
  if (event instanceof Error) {
    return event.message;
  }

  const message = (event as { message?: unknown } | null)?.message;
  return typeof message === 'string' ? message : String(event);
}
