// WASM-07: the core in a real worker thread; cancellation restarts it and re-sends the other calls.
import { afterEach, describe, expect, it } from 'vitest';
import { StudioCoreClient, StudioCoreError } from '../src/client.ts';
import { frameworkUrl, ThreadWorker, workflow } from './support.ts';

let client: StudioCoreClient | undefined;
afterEach(() => client?.dispose());

function threaded() {
  const workers: ThreadWorker[] = [];
  const created = new StudioCoreClient({
    frameworkUrl: frameworkUrl(),
    createWorker: () => {
      const worker = new ThreadWorker();
      workers.push(worker);
      return worker;
    },
  });
  client = created;
  return { client: created, workers };
}

describe('WASM-07 worker', () => {
  it('runs the core in a worker thread', async () => {
    const { client } = threaded();
    expect((await client.toMermaid({ yaml: workflow('02-minimal') })).text).toContain('stateDiagram-v2');
  });

  it('cancels a call: it rejects, the worker restarts, and the other calls still answer', async () => {
    const { client, workers } = threaded();
    const yaml = workflow('01-order-fulfilment');
    const cancel = new AbortController();
    const cancelled = client.enumeratePaths({ yaml }, { signal: cancel.signal });
    const other = client.toMermaid({ yaml });
    cancel.abort();

    await expect(cancelled).rejects.toMatchObject({ name: 'AbortError' });
    expect((await other).text).toContain('stateDiagram-v2');
    expect(workers).toHaveLength(2);

    const already = new AbortController();
    already.abort();
    await expect(client.toBpmn({ yaml }, { signal: already.signal })).rejects.toMatchObject({ name: 'AbortError' });
  });

  it('reports a runtime that cannot load', async () => {
    client = new StudioCoreClient({ frameworkUrl: 'file:///nowhere/_framework/', createWorker: () => new ThreadWorker() });
    await expect(client.version()).rejects.toBeInstanceOf(StudioCoreError);
  });
});
