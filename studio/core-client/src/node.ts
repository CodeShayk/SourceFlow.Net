// The core on the calling thread, for Node: tools, scripts and the workflow-diff action (there is no Web Worker there).
// The .NET runtime loads once per process, the first time a client is used.
import { StudioCoreClient, type WorkerLike } from './client.ts';
import { createHost, loadExports, type HostInit, type HostReply, type HostRequest } from './host.ts';
import type { StudioExports } from './generated/exports.d.ts';

const loaded = new Map<string, Promise<StudioExports>>();

function sharedExports(frameworkUrl: string): Promise<StudioExports> {
  let exports = loaded.get(frameworkUrl);
  if (exports === undefined) {
    exports = loadExports(frameworkUrl);
    loaded.set(frameworkUrl, exports);
  }

  return exports;
}

/** A worker stand-in that runs the core's host on this thread. */
class InProcessWorker implements WorkerLike {
  onmessage: ((event: { data: HostReply }) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;
  private terminated = false;
  private readonly host = createHost(sharedExports);

  postMessage(message: HostRequest | HostInit): void {
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

/**
 * A client running the core on this thread.
 * @param frameworkUrl A `file:` (or `http:`) URL of the published `_framework` folder of SourceFlow.Studio.Wasm.
 */
export function createInProcessClient(frameworkUrl: string, cacheSize?: number): StudioCoreClient {
  return new StudioCoreClient({ frameworkUrl, ...(cacheSize === undefined ? {} : { cacheSize }), createWorker: () => new InProcessWorker() });
}
