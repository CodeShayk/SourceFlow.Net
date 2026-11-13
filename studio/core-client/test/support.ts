// Shared test support: the built bundle, the golden fixtures, and worker adapters.
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { Worker } from 'node:worker_threads';
import { StudioCoreClient, type WorkerLike } from '../src/client.ts';
import { createHost, loadExports, type HostInit, type HostReply, type HostRequest } from '../src/host.ts';
import type { StudioExports } from '../src/generated/exports.d.ts';

export const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
export const golden = join(repository, 'tests', 'workflow-golden');

/** The published `_framework` folder (`npm run build:wasm`), or SOURCEFLOW_STUDIO_WASM. */
export const frameworkDir = process.env.SOURCEFLOW_STUDIO_WASM ?? join(repository, 'artifacts', 'studio-wasm', 'wwwroot', '_framework');

export function frameworkUrl(): string {
  if (!existsSync(join(frameworkDir, 'dotnet.js'))) {
    throw new Error(`No Studio core bundle at ${frameworkDir}: run 'npm run build:wasm' first (or set SOURCEFLOW_STUDIO_WASM).`);
  }

  return pathToFileURL(frameworkDir).href + '/';
}

let shared: Promise<StudioExports> | undefined;

/** The runtime, loaded once per test file (it can't be unloaded). */
export function sharedExports(): Promise<StudioExports> {
  shared ??= loadExports(frameworkUrl());
  return shared;
}

/** A worker that runs the host on this thread: what the golden tests use. */
export class InProcessWorker implements WorkerLike {
  onmessage: ((event: { data: HostReply }) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;
  readonly received: Array<HostRequest | HostInit> = [];
  terminated = false;
  private readonly host = createHost(() => sharedExports());

  postMessage(message: HostRequest | HostInit): void {
    this.received.push(message);
    void this.host.handle(message).then((reply) => {
      if (reply !== undefined && !this.terminated) {
        this.onmessage?.({ data: reply });
      }
    });
  }

  terminate(): void {
    this.terminated = true;
  }
}

/** A Node worker thread running node-worker.mjs: a real separate thread, as in the browser. */
export class ThreadWorker implements WorkerLike {
  onmessage: ((event: { data: HostReply }) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;
  private readonly worker = new Worker(new URL('./node-worker.mjs', import.meta.url));

  constructor() {
    this.worker.on('message', (data: HostReply) => this.onmessage?.({ data }));
    this.worker.on('error', (error) => this.onerror?.(error));
  }

  postMessage(message: HostRequest | HostInit): void {
    this.worker.postMessage(message);
  }

  terminate(): void {
    void this.worker.terminate();
  }
}

export function inProcessClient(cacheSize?: number) {
  const workers: InProcessWorker[] = [];
  const client = new StudioCoreClient({
    frameworkUrl: frameworkUrl(),
    ...(cacheSize === undefined ? {} : { cacheSize }),
    createWorker: () => {
      const worker = new InProcessWorker();
      workers.push(worker);
      return worker;
    },
  });
  return { client, workers };
}

export function cases(): string[] {
  return readdirSync(join(golden, 'cases')).sort();
}

export function workflow(name: string): string {
  return readFileSync(join(golden, 'cases', name, 'workflow.sfflow.yaml'), 'utf8');
}

export function expected(name: string, file: string): string | undefined {
  const path = join(golden, 'cases', name, file);
  return existsSync(path) ? readFileSync(path, 'utf8').replaceAll('\r\n', '\n') : undefined;
}

/** The golden domain's manifests, in sourceflow.domain.yaml order (as DomainWorkspace loads them). */
export function manifests(): string[] {
  const domain = join(golden, 'domain');
  return readFileSync(join(domain, 'sourceflow.domain.yaml'), 'utf8')
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.startsWith('manifest:'))
    .map((line) => readFileSync(join(domain, line.slice('manifest:'.length).trim()), 'utf8'));
}
