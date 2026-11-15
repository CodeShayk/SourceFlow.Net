// The browser Web Worker that hosts the Studio core (ADR-025). Start it as a module worker:
//   new Worker(new URL('@sourceflow/core-client/worker', import.meta.url), { type: 'module' })
//
// It listens with addEventListener, never `onmessage`: dotnet.js decides how to run from whether `globalThis.onmessage`
// is set when it loads. Unset, it runs as the "sidecar", the runtime's main thread inside a worker, which is what this is.
// Set, it takes the worker for one of its own pthread workers and waits for a parent that never speaks.
import { createHost, type HostInit, type HostRequest } from './host.ts';

const host = createHost();
const scope = self as unknown as {
  addEventListener(type: 'message', listener: (event: MessageEvent<HostRequest | HostInit>) => void): void;
  postMessage(message: unknown): void;
};

scope.addEventListener('message', (event) => {
  void host.handle(event.data).then((reply) => {
    if (reply !== undefined) {
      scope.postMessage(reply);
    }
  });
});
