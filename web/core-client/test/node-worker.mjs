// A Node worker thread hosting the Studio core, as worker.ts does in a browser (WASM-07).
import { parentPort } from 'node:worker_threads';
import { createHost } from '../src/host.ts';

const host = createHost();
parentPort.on('message', async (message) => {
  const reply = await host.handle(message);
  if (reply !== undefined) {
    parentPort.postMessage(reply);
  }
});
