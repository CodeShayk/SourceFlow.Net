// WASM-02..05: the WebAssembly core gives exactly the WF-GOLD outputs the .NET suite checks (plan 10 §3).
import { beforeAll, describe, expect, it } from 'vitest';
import type { StudioCoreClient } from '../src/client.ts';
import { cases, expected, inProcessClient, manifests, workflow } from './support.ts';

let client: StudioCoreClient;
const domain = manifests();
const valid = new Set<string>();

beforeAll(async () => {
  client = inProcessClient().client;
  for (const name of cases()) {
    const result = await client.validate({ yaml: workflow(name), manifests: domain });
    if (result.valid) {
      valid.add(name);
    }
  }
});

describe('WASM-02 diagnostics', () => {
  it.each(cases())('%s', async (name) => {
    const result = await client.validate({ yaml: workflow(name), manifests: domain });
    expect(JSON.stringify(result.diagnostics, null, 2) + '\n').toBe(expected(name, 'expected.diagnostics.json'));
  });
});

describe('WASM-03 paths', () => {
  it.each(cases())('%s', async (name) => {
    if (!valid.has(name)) {
      return; // paths and diagrams are for valid workflows
    }

    const paths = await client.enumeratePaths({ yaml: workflow(name) });
    const text = paths.paths.map((p) => p.text).join('\n')
      + '\n\nedges: ' + paths.edges.length + ', uncovered: ' + (paths.uncovered.length === 0 ? 'none' : paths.uncovered.join(', '))
      + (paths.truncated ? '\ntruncated' : '') + '\n';
    expect(text).toBe(expected(name, 'expected.paths.txt'));
  });
});

describe('WASM-04 exports', () => {
  it.each(cases())('%s', async (name) => {
    if (!valid.has(name)) {
      return;
    }

    expect((await client.toMermaid({ yaml: workflow(name) })).text).toBe(expected(name, 'expected.mmd'));
    expect((await client.toBpmn({ yaml: workflow(name) })).text).toBe(expected(name, 'expected.bpmn'));
  });
});

describe('WASM-04 statechart graph', () => {
  it.each(cases())('%s', async (name) => {
    if (!valid.has(name)) {
      return;
    }

    const graph = await client.graph({ yaml: workflow(name) });
    expect(JSON.stringify(graph, null, 2) + '\n').toBe(expected(name, 'expected.graph.json'));
  });
});

describe('WASM-06 swimlane sequence', () => {
  it('places every message of a path between the workflow and its services', async () => {
    const yaml = workflow('01-order-fulfilment');
    const paths = await client.enumeratePaths({ yaml });
    const happy = paths.paths.find((p) => p.end === 'Completed' && p.moves.every((m) => m.outcome === 'success'))!;
    const sequence = await client.sequence({ yaml, manifests: domain, path: happy.id });
    const lanes = sequence.lanes.map((l) => l.id);
    expect(lanes[0]).toBe('workflow');
    expect(sequence.messages.every((m) => lanes.includes(m.from) && lanes.includes(m.to))).toBe(true);
    expect(sequence.messages.find((m) => m.kind === 'command')).toMatchObject({ from: 'workflow', to: 'inventory', type: 'inventory.reserve-stock.v1' });
    await expect(client.sequence({ yaml, manifests: domain, path: 'P999' })).rejects.toThrow("No path 'P999'");
  });
});

describe('WASM-05 specs', () => {
  it('generates the golden WorkflowSpec tests for the minimal path cover', async () => {
    const specs = await client.generateSpecs({
      yaml: workflow('01-order-fulfilment'),
      manifests: domain,
      namespace: 'Orders.Tests',
      moduleType: 'Orders.OrdersModule',
      workflowType: 'Orders.OrderFulfilmentWorkflowV1',
    });
    expect(specs.text).toBe(expected('01-order-fulfilment', 'expected.specs.cs'));
  });
});
