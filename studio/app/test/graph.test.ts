// STU-UT-11..25: edge-kind encodings, the statechart mapping, the ELK adapter, the domain-map aggregation, choreography
// chains and the swimlane geometry.
import type { GraphNode } from '@sourceflow/core-client';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import type { WorkspaceDto } from '../src/api.ts';
import { chainFrom, domainMap, mapLayoutInput } from '../src/graph/domainMap.ts';
import { EDGE_ENCODINGS, EDGE_KINDS, encodingsAreDistinct, NODE_ENCODINGS } from '../src/graph/encodings.ts';
import { positionsOf, sizeFor, toElk } from '../src/graph/layout.ts';
import { arrow, describe as describeMessage, MESSAGE_KINDS, pathChoice } from '../src/graph/sequence.ts';
import { edgeCounts, EXIT_STATES, layoutInput, nodeLabel, nodeLines, statechartPositions, toFlow } from '../src/graph/statechart.ts';
import { layout, orderGraph, sequenceOf } from './fixtures.tsx';

const golden = join(process.cwd(), '..', '..', 'tests', 'workflow-golden', 'domain');
const commerce: WorkspaceDto = {
  domain: 'commerce',
  file: 'sourceflow.domain.yaml',
  readOnly: false,
  services: ['orders', 'inventory', 'payments', 'shipping'].map((name) => ({
    name,
    manifest: `${name}.manifest.json`,
    manifestJson: readFileSync(join(golden, `${name}.manifest.json`), 'utf8'),
    workflowFiles: [],
  })),
};

const node = (overrides: Partial<GraphNode>): GraphNode => ({ id: 'x', kind: 'step', label: 'x', compensable: false, ...overrides });

describe('STU-UT-11 edge encodings', () => {
  it('give every edge kind its own line style, symbol and name: never colour alone', () => {
    expect(encodingsAreDistinct()).toBe(true);
    for (const kind of EDGE_KINDS) {
      expect(EDGE_ENCODINGS[kind].kind).toBe(kind);
      expect(EDGE_ENCODINGS[kind].name.length).toBeGreaterThan(3);
    }

    expect(new Set(orderGraph.edges.map((e) => e.kind))).toEqual(new Set(EDGE_KINDS));
    for (const edge of orderGraph.edges) {
      expect(edge.label.startsWith(EDGE_ENCODINGS[edge.kind].symbol)).toBe(true);
    }

    expect(NODE_ENCODINGS.pivot.shape).toBe('diamond');
    expect(NODE_ENCODINGS.manual.symbol).toBe('✋');
  });
});

describe('STU-UT-12 node lines', () => {
  it('show what a step sends and awaits, its guard and timeout; a pivot is marked', () => {
    expect(nodeLines(node({ label: 'charge', sends: 'payments.charge.v1', awaits: 'payments.payment-captured.v1', when: '$data.x > 1', timeout: 'PT2M' })))
      .toEqual(['charge', 'send payments.charge.v1', 'await payments.payment-captured.v1', 'when $data.x > 1', '⏱ PT2M']);
    expect(nodeLines(node({ kind: 'pivot', label: 'ship' }))[0]).toBe('◆ ship');
    expect(nodeLines(node({ kind: 'start', label: 'orders.order-placed.v1' }))).toEqual(['● start', 'orders.order-placed.v1']);
  });
});

describe('STU-UT-13 accessible node names', () => {
  it('name the kind, the step and its details', () => {
    expect(nodeLabel(node({ kind: 'pivot', label: 'create-shipment', sends: 'shipping.create-shipment.v1' })))
      .toBe('Pivot (point of no return): create-shipment, sends shipping.create-shipment.v1');
    expect(nodeLabel(node({ kind: 'compensation', label: '⟲ 1 charge-payment', compensable: false }))).toBe('Compensation: 1 charge-payment');
    expect(nodeLabel(node({ kind: 'step', label: 'reserve', compensable: true, timeout: 'PT5M' }))).toBe('Step: reserve, times out after PT5M, compensated on failure');
  });
});

describe('STU-UT-14 layout input', () => {
  it('sizes nodes from their text and nests the compensations', () => {
    const input = layoutInput(orderGraph);
    expect(input.nodes).toHaveLength(orderGraph.nodes.length);
    expect(input.nodes.filter((n) => n.parent === 'Compensating').map((n) => n.id)).toEqual(['undo:charge-payment', 'undo:reserve-stock']);
    expect(input.nodes.every((n) => n.width >= 120 && n.height > 0)).toBe(true);
    expect(sizeFor(['a'.repeat(40)]).width).toBe(332);
    expect(sizeFor(['a', 'b', 'c']).height).toBe(86);
  });
});

describe('STU-UT-15 the ELK graph', () => {
  it('nests children, drops self loops and dangling edges, and sets the direction', () => {
    const elk = toElk(
      [{ id: 'a', width: 10, height: 10 }, { id: 'g', width: 50, height: 50 }, { id: 'c', width: 10, height: 10, parent: 'g' }],
      [{ id: 'e1', source: 'a', target: 'c' }, { id: 'loop', source: 'a', target: 'a' }, { id: 'dangling', source: 'a', target: 'zzz' }],
      'RIGHT',
    );
    expect(elk.children?.map((c) => c.id)).toEqual(['a', 'g']);
    expect(elk.children?.[1]?.children?.map((c) => c.id)).toEqual(['c']);
    expect(elk.children?.[1]?.width).toBeUndefined();
    expect(elk.edges?.map((e) => e.id)).toEqual(['e1']);
    expect(elk.layoutOptions?.['elk.direction']).toBe('RIGHT');
  });
});

describe('STU-UT-16 positions', () => {
  it('reads every node, children relative to their parent', () => {
    const positions = positionsOf({ id: 'root', children: [{ id: 'g', x: 10, y: 20, width: 100, height: 80, children: [{ id: 'c', x: 5, y: 6, width: 10, height: 10 }] }] });
    expect(positions.get('g')).toEqual({ x: 10, y: 20, width: 100, height: 80 });
    expect(positions.get('c')).toEqual({ x: 5, y: 6, width: 10, height: 10 });
  });
});

describe('STU-UT-17 layered layout with ELK', () => {
  it('positions every state, layered from the start, the exits beside the flow, compensations inside their group', async () => {
    const positions = await statechartPositions(orderGraph, layout);
    for (const n of orderGraph.nodes) {
      expect(positions.has(n.id)).toBe(true);
    }

    expect(positions.get('start')!.y).toBeLessThan(positions.get('reserve-stock')!.y);
    expect(positions.get('reserve-stock')!.y).toBeLessThan(positions.get('charge-payment')!.y);
    const group = positions.get('Compensating')!;
    const flowRight = Math.max(...orderGraph.nodes.filter((n) => n.parent === undefined && !EXIT_STATES.has(n.id)).map((n) => positions.get(n.id)!.x + positions.get(n.id)!.width));
    expect(group.x).toBeGreaterThan(flowRight);
    expect(positions.get('Compensated')!.y).toBeGreaterThan(group.y + group.height);
    expect(positions.get('ManualIntervention')!.y).toBeGreaterThan(positions.get('Compensated')!.y);
    for (const id of ['undo:charge-payment', 'undo:reserve-stock']) {
      const child = positions.get(id)!;
      expect(child.x + child.width).toBeLessThanOrEqual(group.width);
      expect(child.y + child.height).toBeLessThanOrEqual(group.height);
    }
  });
});

describe('STU-UT-18 React Flow mapping', () => {
  it('maps nodes and edges with their kinds, parents, accessible names and loop indexes', async () => {
    const input = layoutInput(orderGraph);
    const flow = toFlow(orderGraph, await layout(input.nodes, input.edges));
    expect(flow.nodes.find((n) => n.id === 'Compensating')?.type).toBe('group');
    expect(flow.nodes.find((n) => n.id === 'undo:reserve-stock')).toMatchObject({ parentId: 'Compensating', extent: 'parent' });
    expect(flow.nodes.find((n) => n.id === 'create-shipment')?.ariaLabel).toMatch(/^Pivot \(point of no return\): create-shipment/);
    expect(flow.edges.every((e) => e.type === 'kind' && e.ariaLabel === e.data?.edge.description)).toBe(true);
    const loops = flow.edges.filter((e) => e.source === 'charge-payment' && e.target === 'charge-payment');
    expect(loops.map((e) => e.data?.loop)).toEqual([0, 1]);
    expect(toFlow(orderGraph, new Map()).nodes.every((n) => n.position.x === 0)).toBe(true);
  });
});

describe('STU-UT-19 edge counts', () => {
  it('count the edges of each kind for the legend', () => {
    const counts = edgeCounts(orderGraph);
    expect([...counts.values()].reduce((a, b) => a + b, 0)).toBe(orderGraph.edges.length);
    expect(counts.get('start')).toBe(1);
  });
});

describe('STU-UT-20 domain map', () => {
  it('aggregates services, their aggregates, topics, and the edges between them', () => {
    const map = domainMap(commerce);
    expect(map.nodes.filter((n) => n.kind === 'service').map((n) => n.label)).toEqual(['orders', 'inventory', 'payments', 'shipping']);
    expect(map.nodes.find((n) => n.id === 'aggregate:inventory/stock')).toMatchObject({ parent: 'service:inventory', details: ['inventory.release-stock.v1', 'inventory.reserve-stock.v1'] });
    expect(map.nodes.filter((n) => n.kind === 'topic').map((n) => n.label)).toEqual(['inventory.events', 'orders.events', 'payments.events', 'shipping.events']);
    const command = map.edges.find((e) => e.id === 'command:service:orders->aggregate:inventory/stock')!;
    expect(command.messages).toEqual(['inventory.release-stock.v1', 'inventory.reserve-stock.v1']);
    expect(command.label).toBe('→ 2 commands');
    expect(map.edges.some((e) => e.kind === 'command' && e.source === 'service:inventory')).toBe(false);
    expect(map.edges.find((e) => e.id === 'subscribes:topic:inventory.events->service:orders')?.label).toBe('⇠ inventory.stock-reserved.v1, inventory.stock-released.v1');
    expect(map.edges.find((e) => e.id === 'subscribes:topic:orders.events->service:payments')?.label).toBe('⇠ all events');
  });
});

describe('STU-UT-21 domain map robustness', () => {
  it('ignores manifests that are not JSON and a service that handles its own commands', () => {
    const map = domainMap({ ...commerce, services: [{ name: 'broken', manifest: 'x', manifestJson: 'nope', workflowFiles: [] }, ...commerce.services.slice(0, 1)] });
    expect(map.nodes.find((n) => n.id === 'service:broken')).toBeDefined();
    expect(map.edges.every((e) => e.source !== 'service:broken')).toBe(true);
    expect(map.edges.filter((e) => e.kind === 'command')).toEqual([]); // orders alone: the owners aren't in this workspace
  });
});

describe('STU-UT-22 choreography chains', () => {
  it('follow commands, publications and subscriptions downstream', () => {
    const map = domainMap(commerce);
    const fromInventory = chainFrom(map, 'service:inventory');
    expect(fromInventory.has('publishes:service:inventory->topic:inventory.events')).toBe(true);
    expect(fromInventory.has('subscribes:topic:inventory.events->service:orders')).toBe(true);
    expect(fromInventory.has('command:service:orders->aggregate:payments/payment')).toBe(true);
    expect(chainFrom(map, 'aggregate:shipping/shipment').has('publishes:service:shipping->topic:shipping.events')).toBe(true);
    expect(chainFrom(map, 'topic:nowhere').size).toBe(0);
  });
});

describe('STU-UT-23 domain map layout input', () => {
  it('nests aggregates in their service', async () => {
    const map = domainMap(commerce);
    const input = mapLayoutInput(map);
    expect(input.nodes.find((n) => n.id === 'aggregate:payments/payment')?.parent).toBe('service:payments');
    const positions = await layout(input.nodes, input.edges, 'RIGHT');
    expect(positions.get('aggregate:payments/payment')!.x).toBeGreaterThanOrEqual(0);
    expect(positions.size).toBe(map.nodes.length);
  });
});

describe('STU-UT-24 swimlane arrows', () => {
  it('span the lanes between sender and receiver, in the right direction', () => {
    const sequence = sequenceOf('P01');
    expect(arrow(sequence.lanes, sequence.messages[0]!)).toEqual({ from: 1, to: 0, columnStart: 1, columnEnd: 3, direction: 'left' });
    expect(arrow(sequence.lanes, sequence.messages[1]!)).toMatchObject({ columnStart: 1, columnEnd: 4, direction: 'right' });
    expect(arrow(sequence.lanes, sequence.messages[3]!)).toMatchObject({ columnStart: 1, columnEnd: 2, direction: 'self' });
  });
});

describe('STU-UT-25 swimlane descriptions', () => {
  it('read each message as a sentence, with retries, and describe the path choices', () => {
    const sequence = sequenceOf('P02');
    expect(describeMessage(sequence, sequence.messages[1]!)).toBe('command from order-fulfilment@1 (orders) to inventory: → inventory.reserve-stock.v1');
    expect(describeMessage(sequence, sequence.messages[2]!)).toBe('rejection from inventory to order-fulfilment@1 (orders): ✕ rejected inventory.out_of_stock, attempt 2');
    expect(describeMessage(sequence, sequence.messages[3]!)).toBe('order-fulfilment@1 (orders): status Compensated');
    expect(pathChoice({ id: 'P01', end: 'Completed', moves: [1] })).toBe('P01 · Completed · 1 move');
    expect(MESSAGE_KINDS.event.dash).not.toBe(MESSAGE_KINDS.command.dash);
  });
});
