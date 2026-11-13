// WASM-01 (parse, validate, versions), WASM-06 (simulation) and the client's lazy loading and cache.
import { describe, expect, it } from 'vitest';
import { StudioCoreError } from '../src/client.ts';
import { API_VERSION, exportName } from '../src/host.ts';
import type { Operation, SimulateResult, SimulationInput } from '../src/types.ts';
import { inProcessClient, manifests, sharedExports, workflow } from './support.ts';

const order = { orderId: 'o-1', total: 100, lines: ['sku'], customerEmail: 'a@b.c', priority: false };

describe('WASM-01 parse and validate', () => {
  it('reads a workflow and reports structural diagnostics', async () => {
    const { client } = inProcessClient();
    expect(await client.version()).toEqual({ api: API_VERSION, dsl: 'sourceflow.workflow/1' });

    const parsed = await client.parse({ yaml: workflow('01-order-fulfilment') });
    expect(parsed.workflow?.name).toBe('order-fulfilment');
    expect(parsed.workflow?.steps.map((s) => s.id)).toContain('create-shipment');
    expect(parsed.diagnostics).toEqual([]);

    const broken = await client.parse({ yaml: workflow('03-s01-invalid-yaml') });
    expect(broken.workflow).toBeNull();
    expect(broken.diagnostics[0]).toMatchObject({ id: 'WF-S01', severity: 'error' });
  });

  it('validates against the manifests, with the fingerprint', async () => {
    const { client } = inProcessClient();
    const ok = await client.validate({ yaml: workflow('01-order-fulfilment'), manifests: manifests() });
    expect(ok.valid).toBe(true);
    expect(ok.fingerprint).toMatch(/^sha256:[0-9a-f]{16}$/);

    const unknown = await client.validate({ yaml: workflow('09-v01-unknown-type'), manifests: manifests() });
    expect(unknown.valid).toBe(false);
    expect(unknown.diagnostics.some((d) => d.id === 'WF-V01')).toBe(true);
  });

  it('rejects with the core message and diagnostics', async () => {
    const { client } = inProcessClient();
    const failure = await client.toBpmn({ yaml: workflow('03-s01-invalid-yaml') }).catch((e: unknown) => e);
    expect(failure).toBeInstanceOf(StudioCoreError);
    expect((failure as StudioCoreError).diagnostics.length).toBeGreaterThan(0);
    await expect(client.simulateStep({ yaml: workflow('02-minimal'), input: { kind: 'succeed' } })).rejects.toThrow("'state' is required after the start.");
  });

  it('maps every operation to its export', async () => {
    const exports = await sharedExports();
    const operations: Operation[] = ['version', 'parse', 'validate', 'simulateStep', 'enumeratePaths', 'toMermaid', 'toBpmn', 'generateSpecs', 'graph', 'sequence'];
    for (const op of operations) {
      expect(typeof exports[exportName(op)]).toBe('function');
    }
  });
});

describe('WASM-06 simulation', () => {
  it('runs the happy path through the opaque state, with deterministic send ids', async () => {
    const { client } = inProcessClient(0);
    const yaml = workflow('01-order-fulfilment');
    let step: SimulateResult = await client.simulateStep({ yaml, input: { kind: 'start', data: order }, now: '2026-01-01T00:00:00Z' });
    const first = step.effects.find((e) => e.kind === 'send');
    expect(first?.command).toMatchObject({ type: 'inventory.reserve-stock.v1', fields: { orderId: 'o-1' } });

    const again = await client.simulateStep({ yaml, input: { kind: 'start', data: order }, now: '2026-01-01T00:00:00Z' });
    expect(again.effects.find((e) => e.kind === 'send')?.command?.messageId).toBe(first?.command?.messageId);

    const inputs: SimulationInput[] = [{ kind: 'succeed' }, { kind: 'succeed', data: { paymentId: 'p-1' } }, { kind: 'succeed' }];
    for (const input of inputs) {
      step = await client.simulateStep({ yaml, state: step.state, input });
    }

    expect(step.status).toBe('Completed');
    expect(step.terminal).toBe(true);
    expect(step.transitions.map((t) => t.kind)).toEqual(['success', 'complete']);
    expect(step.data).toMatchObject({ orderId: 'o-1', paymentId: 'p-1' });
    expect(step.effects.find((e) => e.kind === 'send')?.command?.type).toBe('orders.mark-fulfilled.v1');
  });

  it('compensates in reverse order and honours forced guards', async () => {
    const { client } = inProcessClient(0);
    const yaml = workflow('01-order-fulfilment');
    let step = await client.simulateStep({ yaml, input: { kind: 'start', data: order } });
    step = await client.simulateStep({ yaml, state: step.state, input: { kind: 'succeed' } });
    step = await client.simulateStep({ yaml, state: step.state, input: { kind: 'reject', code: 'payments.fraud_suspected' } });
    expect(step.status).toBe('Compensating');
    expect(step.failure?.code).toBe('payments.fraud_suspected');
    expect(step.effects.find((e) => e.kind === 'send')?.command?.type).toBe('inventory.release-stock.v1');

    const guards = { 'review-large-order': true };
    let next = await client.simulateStep({ yaml, input: { kind: 'start', data: order }, guards });
    next = await client.simulateStep({ yaml, state: next.state, input: { kind: 'succeed' }, guards });
    next = await client.simulateStep({ yaml, state: next.state, input: { kind: 'succeed', data: { paymentId: 'p' } }, guards });
    expect(next.step).toBe('review-large-order');
  });
});

describe('client loading and cache', () => {
  it('starts the worker on the first call only, and answers repeated calls from the cache', async () => {
    const { client, workers } = inProcessClient();
    expect(client.started).toBe(false);
    expect(workers).toHaveLength(0);

    const yaml = workflow('02-minimal');
    const first = await client.toMermaid({ yaml });
    const second = await client.toMermaid({ yaml });
    expect(second).toBe(first);
    expect(client.started).toBe(true);
    expect(workers).toHaveLength(1);
    expect(workers[0]?.received.filter((m) => 'op' in m)).toHaveLength(1);
    expect(workers[0]?.received[0]).toMatchObject({ id: 0 });

    const { client: small, workers: smallWorkers } = inProcessClient(1);
    await small.toMermaid({ yaml });
    await small.toBpmn({ yaml });
    await small.toMermaid({ yaml });
    expect(smallWorkers[0]?.received.filter((m) => 'op' in m)).toHaveLength(3);

    client.dispose();
    await expect(client.version()).rejects.toThrow('disposed');
  });
});
