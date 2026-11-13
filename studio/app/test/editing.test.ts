// STU-UT-32..40: step forms, renames, adding steps and failures, the palette's validity filter, undo/redo and the layout
// sidecar.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import type { WorkspaceDto } from '../src/api.ts';
import { canRedo, canUndo, current, LIMIT, record, redo, start, undo } from '../src/edit/history.ts';
import { applyLayout, layoutPath, moveNode, pruneLayout, readLayout, writeLayout } from '../src/edit/layoutFile.ts';
import { choicesFor, isOwned, palette } from '../src/edit/palette.ts';
import { addFailure, addStep, newStepId, renameStep, setOrRemove, stepAtLine, stepsOf } from '../src/edit/steps.ts';
import { parse, valueAt } from '../src/edit/yamlEdit.ts';

const root = join(process.cwd(), '..', '..', 'tests', 'workflow-golden');
const fulfilment = readFileSync(join(root, 'cases', '01-order-fulfilment', 'workflow.sfflow.yaml'), 'utf8').replaceAll('\r\n', '\n');
const commerce: WorkspaceDto = {
  domain: 'commerce',
  file: 'sourceflow.domain.yaml',
  readOnly: false,
  services: ['orders', 'inventory', 'payments', 'shipping'].map((name) => ({
    name, manifest: `${name}.manifest.json`, manifestJson: readFileSync(join(root, 'domain', `${name}.manifest.json`), 'utf8'), workflowFiles: [],
  })),
};

describe('STU-UT-32 step forms', () => {
  it('read every field the properties panel shows', () => {
    const steps = stepsOf(fulfilment);
    expect(steps.map((s) => s.id)).toEqual(['reserve-stock', 'charge-payment', 'review-large-order', 'create-shipment', 'await-address-fix']);
    expect(steps[0]).toMatchObject({
      sends: 'inventory.reserve-stock.v1',
      map: [['orderId', '$data.orderId'], ['lines', '$data.lines']],
      awaits: 'inventory.stock-reserved.v1',
      failures: [{ rejected: 'inventory.out_of_stock', then: 'fail' }],
      timeout: { after: 'PT5M', then: 'fail', retry: { max: 2 } },
      compensate: { sends: 'inventory.release-stock.v1', awaits: 'inventory.stock-released.v1' },
      pivot: false,
    });
    expect(steps[1]?.awaits).toBe('payments.payment-captured.v1');
    expect(steps[1]?.failures[0]).toMatchObject({ rejected: 'payments.declined', retry: { max: 3, backoff: 'fixed', initial: 'PT10M' } });
    expect(steps[2]).toMatchObject({ when: '$data.amount > 5000', failures: [{ on: 'orders.order-rejected-by-review.v1' }], timeout: { then: 'manual' } });
    expect(steps[3]).toMatchObject({ pivot: true, next: 'complete', failures: [{ then: { goto: 'await-address-fix' } }] });
    expect(steps[4]?.sends).toBeUndefined();
    expect(stepsOf('not: [a workflow')).toEqual([]);
    expect(stepsOf('name: x\n')).toEqual([]);
  });
});

describe('STU-UT-33 renaming a step', () => {
  it('renames its next and goto references with it, and refuses bad or taken ids', () => {
    const renamed = renameStep(fulfilment, 4, 'fix-address');
    expect(stepsOf(renamed)[4]?.id).toBe('fix-address');
    expect(valueAt(renamed, ['steps', 3, 'await', 'failure', 0, 'then'])).toEqual({ goto: 'fix-address' });
    expect(renameStep(renameStep(fulfilment, 3, 'ship'), 3, 'create-shipment')).toBe(fulfilment);
    const looped = renameStep(fulfilment, 3, 'ship');
    expect(valueAt(looped, ['steps', 4, 'next'])).toBe('ship');
    expect(() => renameStep(fulfilment, 0, 'Bad Id')).toThrow(/not a step id/);
    expect(() => renameStep(fulfilment, 0, 'charge-payment')).toThrow(/already exists/);
    expect(() => renameStep(fulfilment, 9, 'x')).toThrow(/No step 9/);
  });
});

describe('STU-UT-34 adding steps and failures', () => {
  it('inserts after the selection with a unique id, and adds failures to block or flow awaits', () => {
    expect(newStepId(fulfilment, 'payments.refund.v1')).toBe('refund');
    expect(newStepId(fulfilment, 'inventory.reserve-stock.v1')).toBe('reserve-stock-2');
    expect(newStepId(fulfilment, undefined)).toBe('wait');
    const added = addStep(fulfilment, 1, { id: 'refund', command: 'payments.refund.v1', success: 'payments.refunded.v1', map: { orderId: '$data.orderId' } });
    expect(stepsOf(added).map((s) => s.id).slice(1, 4)).toEqual(['charge-payment', 'refund', 'review-large-order']);
    expect(addStep(fulfilment, -1, { id: 'first', success: 'orders.order-approved.v1' }).includes('  - id: first\n    await: { success: orders.order-approved.v1 }\n  - id: reserve-stock')).toBe(true);

    const blockList = addFailure(fulfilment, 1, { rejected: 'payments.card_expired', then: 'manual' });
    expect(stepsOf(blockList)[1]?.failures.map((f) => f.rejected)).toEqual(['payments.declined', 'payments.fraud_suspected', 'payments.card_expired']);
    const flowAwait = addFailure(fulfilment, 4, { on: 'orders.order-cancelled.v1', then: 'complete', retry: { max: 1 } });
    expect(stepsOf(flowAwait)[4]?.failures).toEqual([{ on: 'orders.order-cancelled.v1', then: 'complete', retry: { max: 1 } }]);
    expect(parse(flowAwait).errors).toEqual([]);

    // A file that doesn't parse is edited as text only.
    const broken = readFileSync(join(root, 'cases', '03-s01-invalid-yaml', 'workflow.sfflow.yaml'), 'utf8');
    expect(() => addStep(broken, -1, { id: 'x', success: 'orders.order-approved.v1' })).toThrow(/doesn't parse \(line \d+\): fix it in the YAML tab first/);
    expect(() => setOrRemove(broken, ['steps', 0, 'pivot'], true)).toThrow(/doesn't parse/);
    expect(() => setOrRemove(broken, ['steps', 0, 'pivot'], undefined)).toThrow(/doesn't parse/);
  });
});

describe('STU-UT-35 toggles', () => {
  it('set or remove optional keys, restoring the file when turned back', () => {
    const pivot = setOrRemove(fulfilment, ['steps', 0, 'pivot'], true);
    expect(stepsOf(pivot)[0]?.pivot).toBe(true);
    expect(setOrRemove(pivot, ['steps', 0, 'pivot'], undefined)).toBe(fulfilment);
    expect(setOrRemove(fulfilment, ['steps', 0, 'when'], undefined)).toBe(fulfilment);
    const guarded = setOrRemove(fulfilment, ['steps', 1, 'when'], '$data.amount > 0');
    expect(stepsOf(guarded)[1]?.when).toBe('$data.amount > 0');
  });
});

describe('STU-UT-36 diagnostics to steps', () => {
  it('map a line to the step it belongs to', () => {
    const lines = fulfilment.split('\n');
    const at = (text: string) => lines.findIndex((l) => l.includes(text)) + 1;
    expect(stepAtLine(fulfilment, at('id: reserve-stock'))).toBe(0);
    expect(stepAtLine(fulfilment, at('inventory.release-stock.v1'))).toBe(0);
    expect(stepAtLine(fulfilment, at('payments.fraud_suspected'))).toBe(1);
    expect(stepAtLine(fulfilment, at('next: create-shipment'))).toBe(4);
    expect(stepAtLine(fulfilment, 1)).toBeUndefined();
  });
});

describe('STU-UT-37 the palette', () => {
  it('lists the commands the domain owns, with what they emit, reject and carry', () => {
    const p = palette(commerce);
    const reserve = p.commands.find((c) => c.ref === 'inventory.reserve-stock.v1')!;
    expect(reserve).toMatchObject({ service: 'inventory', aggregate: 'stock' });
    expect(reserve.emits).toContain('inventory.stock-reserved.v1');
    expect(reserve.rejects).toContain('inventory.out_of_stock');
    expect(reserve.fields).toContain('orderId');
    expect(p.commands.map((c) => c.ref)).toEqual([...p.commands.map((c) => c.ref)].sort());
    expect(p.events).toContain('payments.payment-captured.v1');
    expect(palette({ ...commerce, services: [{ name: 'x', manifest: '', manifestJson: 'broken', workflowFiles: [] }] })).toEqual({ commands: [], services: [], events: [] });
  });
});

describe('STU-UT-38 choices at a step', () => {
  it('filter events, rejections and fields to what the step can use', () => {
    const p = palette(commerce);
    const charge = choicesFor(p, 'payments.charge.v1', ['payments.declined'], ['orderId'], 'payments.payment-captured.v1');
    expect(charge.successEvents).toContain('payments.payment-captured.v1');
    expect(charge.rejections).not.toContain('payments.declined');
    expect(charge.rejections).toContain('payments.fraud_suspected');
    expect(charge.failureEvents).not.toContain('payments.payment-captured.v1');
    expect(charge.unmappedFields).not.toContain('orderId');
    const waitOnly = choicesFor(p, undefined);
    expect(waitOnly.successEvents).toEqual(p.events);
    expect(waitOnly.rejections).toEqual([]);
    expect(isOwned(p, 'payments.charge.v1')).toBe(true);
    expect(isOwned(p, 'billing.nowhere.v1')).toBe(false);
    expect(isOwned(p, undefined)).toBe(true);
  });
});

describe('STU-UT-39 undo and redo', () => {
  it('records edits, merges typing bursts, and drops the redo branch on a new edit', () => {
    let h = start('a');
    expect(canUndo(h)).toBe(false);
    h = record(h, 'ab', 'typing', 1000);
    h = record(h, 'abc', 'typing', 1300);
    expect(h.states).toEqual(['a', 'abc']);
    h = record(h, 'abcd', 'typing', 3000);
    h = record(h, 'abcd!', 'edit', 3100);
    h = record(h, 'abcd!', 'edit', 3200);
    expect(h.states).toEqual(['a', 'abc', 'abcd', 'abcd!']);
    h = undo(undo(h));
    expect(current(h)).toBe('abc');
    expect(canRedo(h)).toBe(true);
    h = redo(h);
    expect(current(h)).toBe('abcd');
    h = record(h, 'x', 'edit', 4000);
    expect(h.states).toEqual(['a', 'abc', 'abcd', 'x']);
    expect(canRedo(h)).toBe(false);
    expect(redo(h)).toBe(h);
    expect(undo(start('z'))).toEqual(start('z'));
    let long = start('0');
    for (let i = 1; i <= LIMIT + 10; i++) {
      long = record(long, String(i), 'edit', i * 10_000);
    }

    expect(long.states).toHaveLength(LIMIT);
    expect(current(long)).toBe(String(LIMIT + 10));
  });
});

describe('STU-UT-40 the layout sidecar', () => {
  it('reads, writes, applies and prunes saved positions', () => {
    expect(layoutPath('orders/workflows/order-fulfilment.sfflow.yaml')).toBe('orders/workflows/order-fulfilment.sfflow.layout.json');
    expect(readLayout(undefined)).toEqual({ version: 1, nodes: {} });
    expect(readLayout('not json')).toEqual({ version: 1, nodes: {} });
    expect(readLayout('{"nodes":{"a":{"x":1,"y":"2"},"b":{"x":3,"y":4}}}')).toEqual({ version: 1, nodes: { b: { x: 3, y: 4 } } });
    const moved = moveNode(moveNode(readLayout(undefined), 'z', 10.4, 20.6), 'a', 1, 2);
    expect(writeLayout(moved)).toBe('{\n  "version": 1,\n  "nodes": {\n    "a": {\n      "x": 1,\n      "y": 2\n    },\n    "z": {\n      "x": 10,\n      "y": 21\n    }\n  }\n}\n');
    const positions = new Map([['a', { x: 0, y: 0, width: 10, height: 10 }], ['b', { x: 5, y: 5, width: 10, height: 10 }]]);
    const applied = applyLayout(positions, moved);
    expect(applied.get('a')).toEqual({ x: 1, y: 2, width: 10, height: 10 });
    expect(applied.get('b')).toEqual({ x: 5, y: 5, width: 10, height: 10 });
    expect(applied.has('z')).toBe(false);
    expect(Object.keys(pruneLayout(moved, ['a']).nodes)).toEqual(['a']);
  });
});
