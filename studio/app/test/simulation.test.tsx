// STU-CT-29 (the simulation panel) and STU-CT-30 (the path explorer and spec generation), on the golden order-fulfilment
// workflow. The core is scripted here; the WASM suite (WASM-06) and the browser tests (STU-E2E-11..13) run the real one.
import type { PathsResult, SimulateResult } from '@sourceflow/core-client';
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import type { WorkspaceDto } from '../src/api.ts';
import { useEditor } from '../src/edit/editorStore.ts';
import { stepsOf } from '../src/edit/steps.ts';
import { coverage, edgesOf } from '../src/simulate/PathExplorer.tsx';
import { activeNode, choicesAt, guardsFor, MAX_MOVES, nextInPlan, outbox, pathGuards, planChoices, startData } from '../src/simulate/simulation.ts';
import { fakeApi, renderApp, scriptedCore, simulated } from './fixtures.tsx';

const root = join(process.cwd(), '..', '..', 'tests', 'workflow-golden');
const path = 'orders/workflows/order-fulfilment.sfflow.yaml';
const yaml = readFileSync(join(root, 'cases', '01-order-fulfilment', 'workflow.sfflow.yaml'), 'utf8').replaceAll('\r\n', '\n');
const steps = stepsOf(yaml);

function commerce(options: { readOnly?: boolean; tests?: boolean } = {}): WorkspaceDto {
  return {
    domain: 'commerce',
    file: 'sourceflow.domain.yaml',
    readOnly: options.readOnly ?? false,
    services: ['orders', 'inventory', 'payments', 'shipping'].map((name) => ({
      name,
      manifest: `${name}.manifest.json`,
      manifestJson: readFileSync(join(root, 'domain', `${name}.manifest.json`), 'utf8'),
      ...(name === 'orders' ? { workflows: 'orders/workflows', ...(options.tests === false ? {} : { tests: 'orders/tests', module: 'Orders.OrdersModule' }) } : {}),
      workflowFiles: name === 'orders' ? [path] : [],
    })),
  };
}

const send = (type: string) => ({ kind: 'send' as const, text: `send ${type}`, command: { type, messageId: `${type}-0000-id`, fields: { orderId: 'instance-1' } } });

const paths: PathsResult = {
  paths: [
    {
      id: 'P01', number: 1, end: 'Completed', guards: {},
      text: 'P01 reserve-stock: success → charge-payment: success ⇒ Completed',
      moves: [
        { step: 'reserve-stock', outcome: 'success', edge: 'reserve-stock:success', input: { kind: 'succeed' } },
        { step: 'review-large-order', outcome: 'skipped', edge: 'review-large-order:skip' },
        { step: 'charge-payment', outcome: 'success', edge: 'charge-payment:success', input: { kind: 'succeed' } },
      ],
    },
    {
      id: 'P02', number: 2, end: 'Compensated', guards: { 'review-large-order': false },
      text: 'P02 reserve-stock: success → charge-payment: rejected payments.declined → retry ⇒ Compensated',
      moves: [
        { step: 'reserve-stock', outcome: 'success', edge: 'reserve-stock:success', input: { kind: 'succeed' } },
        { step: 'charge-payment', outcome: 'rejected payments.declined', edge: 'charge-payment:rejected:payments.declined', retryEdge: 'charge-payment:retry', input: { kind: 'reject', code: 'payments.declined' } },
        { step: 'charge-payment', outcome: 'retry', edge: 'charge-payment:retry', input: { kind: 'timer' } },
        { step: 'reserve-stock', outcome: 'compensated', edge: 'compensate:reserve-stock:success', input: { kind: 'succeed' } },
      ],
    },
    {
      id: 'P03', number: 3, end: 'ManualIntervention', guards: {},
      text: 'P03 reserve-stock: timeout ⇒ ManualIntervention',
      moves: [{ step: 'reserve-stock', outcome: 'timeout', edge: 'reserve-stock:timeout', input: { kind: 'timer' } }],
    },
  ],
  edges: ['reserve-stock:success', 'review-large-order:skip', 'charge-payment:success', 'charge-payment:rejected:payments.declined', 'charge-payment:retry', 'compensate:reserve-stock:success', 'reserve-stock:timeout', 'on:manual'],
  uncovered: ['on:manual'],
  truncated: false,
  minimalCover: ['P01', 'P03'],
};

async function open(script: SimulateResult[] = [], options: { readOnly?: boolean; tests?: boolean } = {}) {
  const api = fakeApi(commerce(options), { [path]: yaml });
  const core = scriptedCore(script);
  core.enumeratePaths.mockResolvedValue(paths);
  const view = renderApp('/workflows/' + path, api, core);
  await waitFor(() => expect(useEditor.getState().path).toBe(path));
  await waitFor(() => expect(view.container.querySelector('[data-ready="true"]')).not.toBeNull(), { timeout: 10_000 });
  return { ...view, api, core };
}

const panel = () => screen.getByRole('region', { name: 'Simulate' });
const part = (name: string) => within(panel()).getByRole('region', { name });
const status = () => within(panel()).getByTestId('sim-status').textContent;

describe('STU-CT-29 the simulation panel', () => {
  it('chooses what happens next from the state the core returns', () => {
    const at = (overrides: Partial<SimulateResult>) => choicesAt(simulated(overrides), steps).map((c) => c.id);
    expect(at({ step: 'charge-payment' })).toEqual(['success', 'rejected:payments.declined', 'rejected:payments.fraud_suspected', 'timeout']);
    expect(at({ step: 'review-large-order' })).toEqual(['success', 'event:orders.order-rejected-by-review.v1', 'timeout']);
    expect(at({ step: 'charge-payment', phase: 'Retrying' })).toEqual(['retry']);
    expect(at({ status: 'Compensating', step: 'reserve-stock' })).toEqual(['compensated', 'rejected', 'timeout']);
    expect(at({ status: 'ManualIntervention', step: 'reserve-stock' })).toEqual(['resume', 'retry-step', 'abort-compensate', 'abort']);
    expect(at({ status: 'Completed', terminal: true })).toEqual([]);
    expect(planChoices(steps[0]!).map((c) => c.label)).toEqual(['✓ inventory.stock-reserved.v1', '✕ Rejected inventory.out_of_stock', '⏱ Timeout after PT5M']);

    expect(nextInPlan(simulated({ step: 'charge-payment' }), steps, { 'charge-payment': 'rejected:payments.declined' })?.input).toEqual({ kind: 'reject', code: 'payments.declined' });
    expect(nextInPlan(simulated({ step: 'charge-payment' }), steps, {})?.id).toBe('success');
    expect(nextInPlan(simulated({ step: 'charge-payment', phase: 'Retrying' }), steps, {})?.id).toBe('retry');
    expect(nextInPlan(simulated({ status: 'ManualIntervention' }), steps, {})).toBeUndefined();

    expect(activeNode(simulated({ step: 'charge-payment' }))).toBe('charge-payment');
    expect(activeNode(simulated({ status: 'Compensating', step: 'reserve-stock' }))).toBe('undo:reserve-stock');
    expect(activeNode(simulated({ status: 'Compensated', terminal: true }))).toBe('Compensated');
    expect(activeNode(simulated({ status: 'Aborted', terminal: true }))).toBeUndefined();

    expect(startData(yaml)).toEqual({ orderId: 'instance-1' });
    expect(guardsFor(['a', 'b', 'c'], { a: 'run', b: 'skip', c: 'auto' })).toEqual({ $start: true, '$success:a': true, '$success:b': true, '$success:c': true, a: true, b: false });
    expect(pathGuards(['a'], paths.paths[1]!)).toEqual({ $start: true, '$success:a': true, 'review-large-order': false });
    expect(outbox([simulated({ effects: [send('x.v1'), { kind: 'status', text: 'Running' }] }), simulated({ effects: [send('y.v1')] })]).map((c) => [c.move, c.type])).toEqual([[0, 'x.v1'], [1, 'y.v1']]);
  });

  it('starts, steps, goes back and runs to the end, showing state, timer, outbox, stack and data', async () => {
    const start = simulated({ step: 'reserve-stock', timer: { kind: 'timeout', step: 'reserve-stock', attempt: 1, due: '2026-01-01T00:05:00Z' }, effects: [send('inventory.reserve-stock.v1')] });
    const charging = simulated({ step: 'charge-payment', compensationStack: ['reserve-stock'], effects: [send('payments.charge.v1')], data: { orderId: 'instance-1', paymentId: 'p-1' } });
    const compensating = simulated({
      status: 'Compensating', step: 'reserve-stock', compensationStack: ['reserve-stock'],
      failure: { code: 'payments.fraud_suspected', message: 'Rejected', step: 'charge-payment' }, effects: [send('inventory.release-stock.v1')],
    });
    const review = simulated({ step: 'create-shipment', compensationStack: ['charge-payment', 'reserve-stock'] });
    const done = simulated({ status: 'Completed', terminal: true, compensationStack: ['charge-payment', 'reserve-stock'], effects: [send('orders.mark-fulfilled.v1')] });
    const { container, core } = await open([start, charging, compensating, review, done]);

    await userEvent.click(screen.getByRole('tab', { name: 'Simulate' }));
    expect(within(panel()).getByText('Start an instance to simulate it.')).toBeTruthy();
    await userEvent.selectOptions(within(panel()).getByRole('combobox', { name: 'Guard of review-large-order' }), 'skip');
    await userEvent.click(within(panel()).getByRole('button', { name: 'Start' }));
    await waitFor(() => expect(status()).toBe('Running'));
    expect(core.simulateStep.mock.calls[0]![0]).toMatchObject({ yaml, input: { kind: 'start', data: { orderId: 'instance-1' } }, guards: { $start: true, 'review-large-order': false }, now: '2026-01-01T00:00:00Z' });
    expect(part('Timers').textContent).toContain('⏱ timeout for reserve-stock, attempt 1, due 2026-01-01T00:05:00Z');
    expect(part('Outbox').textContent).toContain('→ inventory.reserve-stock.v1');
    expect(part('Compensation stack').textContent).toContain('Nothing to undo.');
    await waitFor(() => expect(container.querySelector('.react-flow__node[data-id="reserve-stock"]')?.getAttribute('aria-label')).toMatch(/simulated here/));
    expect(container.querySelector('.react-flow__node[data-id="reserve-stock"] [data-active="true"]')).not.toBeNull();

    const next = within(panel()).getByRole('combobox', { name: 'Next outcome' }) as HTMLSelectElement;
    expect([...next.options].map((o) => o.textContent)).toEqual(['✓ inventory.stock-reserved.v1', '✕ Rejected inventory.out_of_stock', '⏱ Timeout after PT5M']);
    await userEvent.click(within(panel()).getByRole('button', { name: 'Step' }));
    await waitFor(() => expect(within(panel()).getByText('charge-payment', { selector: 'dd' })).toBeTruthy());
    expect(core.simulateStep.mock.calls[1]![0]).toMatchObject({ state: start.state, input: { kind: 'succeed' }, guards: { 'review-large-order': false } });
    expect(part('Outbox').querySelectorAll('li')).toHaveLength(2);
    expect(part('Data').textContent).toContain('"paymentId": "p-1"');

    await userEvent.selectOptions(within(panel()).getByRole('combobox', { name: 'Next outcome' }), 'rejected:payments.fraud_suspected');
    await userEvent.click(within(panel()).getByRole('button', { name: 'Step' }));
    await waitFor(() => expect(status()).toBe('Compensating'));
    expect(core.simulateStep.mock.calls[2]![0]).toMatchObject({ state: charging.state, input: { kind: 'reject', code: 'payments.fraud_suspected' } });
    expect(part('Compensation stack').textContent).toBe('Compensation stack⟲ undo reserve-stock');
    expect(within(panel()).getByText(/payments.fraud_suspected at charge-payment/)).toBeTruthy();
    await waitFor(() => expect(container.querySelector('.react-flow__node[data-id="undo:reserve-stock"] [data-active="true"]')).not.toBeNull());

    // Back to the payment, then run with the plan's default (success) to the end.
    await userEvent.click(within(panel()).getByRole('button', { name: 'Back' }));
    expect(status()).toBe('Running');
    await userEvent.click(within(panel()).getByRole('button', { name: 'Run to the end' }));
    await waitFor(() => expect(status()).toBe('Completed'));
    expect(core.simulateStep.mock.calls[3]![0]).toMatchObject({ state: charging.state, input: { kind: 'succeed' } });
    expect(core.simulateStep).toHaveBeenCalledTimes(5);
    expect(within(panel()).getByText('The instance has ended: Completed.')).toBeTruthy();
    expect(part('Compensation stack').textContent).toBe('Compensation stack⟲ undo reserve-stock⟲ undo charge-payment');
    expect([...part('Moves').querySelectorAll('li')].map((li) => li.textContent)).toEqual([
      '▶ Start → Running at reserve-stock',
      'reserve-stock: ✓ inventory.stock-reserved.v1 → Running at charge-payment',
      'charge-payment: ✓ payments.payment-captured.v1 → Running at create-shipment',
      'create-shipment: ✓ shipping.shipment-created.v1 → Completed',
    ]);
    expect(container.querySelector('.react-flow__node[data-id="Completed"] [data-active="true"]')).not.toBeNull();

    // A changed workflow needs a new simulation.
    act(() => useEditor.getState().type(yaml + '# changed\n'));
    expect(within(panel()).getByText(/The workflow changed since this simulation started/)).toBeTruthy();
    expect(container.querySelector('[data-active="true"]')).toBeNull();
  });

  it('follows the plan, stops at an operator or a loop, and explains bad start data', async () => {
    const loop = { ...scriptedCore([]) };
    let calls = 0;
    loop.simulateStep = vi.fn(async () => simulated({ state: `s${calls++}`, step: 'create-shipment' }));
    const api = fakeApi(commerce(), { [path]: yaml });
    loop.enumeratePaths.mockResolvedValue(paths);
    renderApp('/workflows/' + path, api, loop);
    await userEvent.click(await screen.findByRole('tab', { name: 'Simulate' }));

    fireEvent.change(within(panel()).getByRole('textbox', { name: 'Start event data (JSON)' }), { target: { value: '{ not json' } });
    await userEvent.click(within(panel()).getByRole('button', { name: 'Start' }));
    expect((await within(panel()).findByRole('alert')).textContent).toMatch(/The start data isn't JSON/);
    fireEvent.change(within(panel()).getByRole('textbox', { name: 'Start event data (JSON)' }), { target: { value: '[1]' } });
    await userEvent.click(within(panel()).getByRole('button', { name: 'Start' }));
    expect(within(panel()).getByRole('alert').textContent).toMatch(/is a JSON object/);
    fireEvent.change(within(panel()).getByRole('textbox', { name: 'Start event data (JSON)' }), { target: { value: '{ "orderId": "o-9" }' } });
    await userEvent.click(within(panel()).getByRole('button', { name: 'Start' }));
    await waitFor(() => expect(status()).toBe('Running'));
    expect(loop.simulateStep.mock.calls[0]![0]).toMatchObject({ input: { data: { orderId: 'o-9' } } });

    await userEvent.selectOptions(within(panel()).getByRole('combobox', { name: 'Outcome of create-shipment' }), 'rejected:shipping.address_invalid');
    await userEvent.click(within(panel()).getByRole('button', { name: 'Run to the end' }));
    expect(await within(panel()).findByText(`Stopped after ${MAX_MOVES} moves: the plan goes round a loop. Change an outcome in the plan, or step on.`, {}, { timeout: 10_000 })).toBeTruthy();
    expect(loop.simulateStep).toHaveBeenCalledTimes(MAX_MOVES + 1);
    expect(loop.simulateStep.mock.calls[1]![0]).toMatchObject({ input: { kind: 'reject', code: 'shipping.address_invalid' } });
  }, 30_000);
});

describe('STU-CT-30 the path explorer and specs', () => {
  it('measures what paths and selections cover', () => {
    expect([...edgesOf(paths.paths[1]!)]).toEqual(['reserve-stock:success', 'charge-payment:rejected:payments.declined', 'charge-payment:retry', 'compensate:reserve-stock:success']);
    expect(coverage(paths, ['P01', 'P03'])).toMatchObject({ covered: ['reserve-stock:success', 'review-large-order:skip', 'charge-payment:success', 'reserve-stock:timeout'] });
    expect(coverage(paths, []).missed).toHaveLength(7);
    expect(coverage(paths, ['P01', 'P02', 'P03']).missed).toEqual([]);
  });

  it('lists the paths with their coverage, filters and selects them, and previews and writes specs', async () => {
    const { api } = await open();
    await userEvent.click(screen.getByRole('tab', { name: 'Paths' }));
    const explorer = await screen.findByRole('region', { name: 'Paths' });
    expect(within(explorer).getByText('3 paths · 7 of 8 edges covered · a minimal cover of 2')).toBeTruthy();
    expect(within(explorer).getByText('1 edge no path covers')).toBeTruthy();
    const list = within(explorer).getByRole('list', { name: 'Paths of order-fulfilment.sfflow.yaml' });
    expect(within(list).getAllByRole('listitem')).toHaveLength(3);
    expect((within(list).getByRole('checkbox', { name: 'Select P01' }) as HTMLInputElement).checked).toBe(true);
    expect((within(list).getByRole('checkbox', { name: 'Select P02' }) as HTMLInputElement).checked).toBe(false);
    expect(within(list).getAllByText('★ cover')).toHaveLength(2);
    expect(within(explorer).getByText('2 paths selected, covering 4 of 7 edges.')).toBeTruthy();

    await userEvent.selectOptions(within(explorer).getByRole('combobox', { name: 'Show' }), 'Compensated');
    expect(within(list).getAllByRole('listitem')).toHaveLength(1);
    await userEvent.click(within(explorer).getByRole('button', { name: 'Select the shown paths' }));
    expect(within(explorer).getByText('3 paths selected, covering 7 of 7 edges.')).toBeTruthy();
    await userEvent.click(within(explorer).getByRole('button', { name: 'Clear the selection' }));
    expect(within(explorer).getByText('0 paths selected, covering 0 of 7 edges.')).toBeTruthy();
    expect(within(explorer).getByRole('button', { name: 'Preview specs' }).hasAttribute('disabled')).toBe(true);
    await userEvent.click(within(explorer).getByRole('button', { name: 'Select the minimal cover' }));
    await userEvent.selectOptions(within(explorer).getByRole('combobox', { name: 'Show' }), 'all');
    await userEvent.click(within(list).getByRole('checkbox', { name: 'Select P02' }));

    // The selection outlives the tab.
    await userEvent.click(screen.getByRole('tab', { name: 'Statechart' }));
    await userEvent.click(screen.getByRole('tab', { name: 'Paths' }));
    const again = await screen.findByRole('region', { name: 'Paths' });
    await userEvent.click(within(again).getByRole('button', { name: 'Preview specs' }));
    expect(api.specs).toHaveBeenCalledWith('orders', { workflow: path, paths: ['P01', 'P03', 'P02'], dryRun: true });
    expect((await within(again).findByLabelText('Specs preview')).textContent).toContain('OrderFulfilmentV1PathTests');
    expect(within(again).getByText('3 tests for orders/tests/Workflows/OrderFulfilmentV1PathTests.cs.')).toBeTruthy();
    await userEvent.click(within(again).getByRole('button', { name: 'Write specs' }));
    expect(api.specs).toHaveBeenLastCalledWith('orders', { workflow: path, paths: ['P01', 'P03', 'P02'], dryRun: false });
    expect(await within(again).findByText('Wrote 3 tests to orders/tests/Workflows/OrderFulfilmentV1PathTests.cs.')).toBeTruthy();

    // The host's refusal is shown as it is; unsaved text can't be specified.
    api.specs.mockRejectedValueOnce(new Error("'orders/tests/Workflows/OrderFulfilmentV1PathTests.cs' isn't a file Studio generated"));
    await userEvent.click(within(again).getByRole('button', { name: 'Write specs' }));
    expect((await within(again).findByRole('alert')).textContent).toContain("isn't a file Studio generated");
    act(() => useEditor.getState().type(yaml + '# changed\n'));
    expect(await within(screen.getByRole('region', { name: 'Paths' })).findByText(/Save the workflow first/)).toBeTruthy();
    expect(within(screen.getByRole('region', { name: 'Paths' })).getByRole('button', { name: 'Preview specs' }).hasAttribute('disabled')).toBe(true);
  });

  it('explains a service without a test project, and previews but does not write read-only', async () => {
    await open([], { tests: false });
    await userEvent.click(screen.getByRole('tab', { name: 'Paths' }));
    const explorer = await screen.findByRole('region', { name: 'Paths' });
    expect(explorer.textContent).toContain('To write specs, give orders a tests folder (its test project) and its module type in the workspace file.');
    expect(within(explorer).getByRole('button', { name: 'Preview specs' }).hasAttribute('disabled')).toBe(true);
    cleanup();
    useEditor.setState(useEditor.getInitialState(), true);

    await open([], { readOnly: true });
    await userEvent.click(screen.getByRole('tab', { name: 'Paths' }));
    const readOnly = await screen.findByRole('region', { name: 'Paths' });
    expect(within(readOnly).getByRole('button', { name: 'Preview specs' }).hasAttribute('disabled')).toBe(false);
    expect(within(readOnly).getByRole('button', { name: 'Write specs' }).hasAttribute('disabled')).toBe(true);
  });

  it('replays a path in the simulator', async () => {
    const script = [
      simulated({ step: 'reserve-stock' }),
      simulated({ step: 'charge-payment', compensationStack: ['reserve-stock'] }),
      simulated({ step: 'charge-payment', phase: 'Retrying', compensationStack: ['reserve-stock'] }),
      simulated({ status: 'Compensating', step: 'reserve-stock', compensationStack: ['reserve-stock'] }),
      simulated({ status: 'Compensated', terminal: true }),
    ];
    const { core } = await open(script);
    await userEvent.click(screen.getByRole('tab', { name: 'Paths' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Simulate P02' }));
    await waitFor(() => expect(status()).toBe('Compensated'));
    expect(screen.getByRole('tab', { name: 'Simulate' }).getAttribute('aria-selected')).toBe('true');
    expect(core.simulateStep.mock.calls.map((c) => c[0].input)).toEqual([
      { kind: 'start', data: { orderId: 'instance-1' } },
      { kind: 'succeed' },
      { kind: 'reject', code: 'payments.declined' },
      { kind: 'timer' },
      { kind: 'succeed' },
    ]);
    expect(core.simulateStep.mock.calls.every((c) => c[0].guards?.['review-large-order'] === false && c[0].guards.$start === true)).toBe(true);
    expect(within(panel()).getByText('P02', { selector: 'dd' })).toBeTruthy();
    expect([...part('Moves').querySelectorAll('li')].map((li) => li.textContent)).toEqual([
      '▶ Start → Running at reserve-stock',
      'reserve-stock: success → Running at charge-payment',
      'charge-payment: rejected payments.declined → Running at charge-payment',
      'charge-payment: retry → Compensating at reserve-stock',
      'reserve-stock: compensated → Compensated',
    ]);
  });
});
