// The browser Web Worker that hosts the Studio core (ADR-025). Start it as a module worker:
//   new Worker(new URL('@sourceflow/core-client/worker', import.meta.url), { type: 'module' })
import { createHost, type HostInit, type HostRequest } from './host.ts';

const host = createHost();
const scope = self as unknown as { onmessage: ((event: MessageEvent<HostRequest | HostInit>) => void) | null; postMessage(message: unknown): void };

scope.onmessage = (event) => {
  void host.handle(event.data).then((reply) => {
    if (reply !== undefined) {
      scope.postMessage(reply);
    }
  });
};
