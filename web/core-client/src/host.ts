// The worker side of the Studio core (WP-S.7): loads the .NET WebAssembly runtime once, then dispatches requests to
// the [JSExport] methods. It runs in a browser Web Worker (worker.ts) and, in tests, in a Node worker thread.
// Only erasable TypeScript here: Node runs this file directly.
import type { StudioExports } from './generated/exports.d.ts';
import type { Operation } from './types.ts';

/** The API version this client speaks (StudioApi.ApiVersion). */
export const API_VERSION = 1;

/** A request from the client: an operation and its JSON request. */
export interface HostRequest {
  id: number;
  op: Operation;
  request: string;
}

/** The first message: where the runtime's `_framework` folder is (a URL, or a file URL in Node). */
export interface HostInit {
  id: 0;
  init: string;
}

/** A reply: the export's JSON response, or why the host couldn't run it. */
export type HostReply = { id: number; response: string } | { id: number; failure: string };

/** The export name of an operation: `simulateStep` → `SimulateStep`. */
export function exportName(op: Operation): Capitalize<Operation> {
  return (op.charAt(0).toUpperCase() + op.slice(1)) as Capitalize<Operation>;
}

interface DotnetModule {
  dotnet: {
    create(): Promise<{ getAssemblyExports(assembly: string): Promise<{ SourceFlow: { Studio: { Wasm: { Exports: StudioExports } } } }> }>;
  };
}

/** Loads the runtime from `frameworkUrl` and checks the API version. */
export async function loadExports(frameworkUrl: string): Promise<StudioExports> {
  const base = frameworkUrl.endsWith('/') ? frameworkUrl : frameworkUrl + '/';
  const module = (await import(/* @vite-ignore */ base + 'dotnet.js')) as DotnetModule;
  const runtime = await module.dotnet.create();
  const exports = (await runtime.getAssemblyExports('SourceFlow.Studio.Wasm.dll')).SourceFlow.Studio.Wasm.Exports;
  const version = JSON.parse(exports.Version('{}')) as { ok: boolean; result?: { api: number } };
  if (!version.ok || version.result?.api !== API_VERSION) {
    throw new Error(`The Studio core speaks API ${version.result?.api ?? '?'}; this client needs ${API_VERSION}.`);
  }

  return exports;
}

/** A host: initialise it with the framework URL, then hand it requests. */
export function createHost(load: (frameworkUrl: string) => Promise<StudioExports> = loadExports) {
  let exports: Promise<StudioExports> | undefined;
  return {
    async handle(message: HostRequest | HostInit): Promise<HostReply | undefined> {
      if ('init' in message) {
        exports ??= load(message.init);
        exports.catch(() => undefined); // reported with the first request
        return undefined;
      }

      try {
        if (exports === undefined) {
          throw new Error('The host was not initialised with the runtime location.');
        }

        const api = await exports;
        return { id: message.id, response: api[exportName(message.op)](message.request) };
      } catch (error) {
        return { id: message.id, failure: error instanceof Error ? error.message : String(error) };
      }
    },
  };
}
