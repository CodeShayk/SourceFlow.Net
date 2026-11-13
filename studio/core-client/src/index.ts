// @sourceflow/core-client: the Studio core (the C# workflow DSL engine compiled to WebAssembly) behind a typed API.
import { StudioCoreClient, type WorkerLike } from './client.ts';

export { StudioCoreClient, StudioCoreError, type CallOptions, type StudioCoreOptions, type WorkerLike } from './client.ts';
export { API_VERSION, createHost, exportName, loadExports, type HostInit, type HostReply, type HostRequest } from './host.ts';
export type * from './types.ts';
export type { StudioExports, ExportName } from './generated/exports.d.ts';

/**
 * A client running the core in a module Web Worker (browsers). The worker and the runtime load on the first call.
 * @param frameworkUrl Where the published `_framework` folder of SourceFlow.Studio.Wasm is served.
 * @param cacheSize How many results to keep (default 64; 0 disables the cache).
 */
export function createBrowserClient(frameworkUrl: string, cacheSize?: number): StudioCoreClient {
  return new StudioCoreClient({
    frameworkUrl,
    ...(cacheSize === undefined ? {} : { cacheSize }),
    createWorker: () => new Worker(new URL('./worker.ts', import.meta.url), { type: 'module' }) as unknown as WorkerLike,
  });
}
