// STU-E2E-11..13 (plan 10 §3.3): the simulation panel, the path explorer and spec generation in the browser, on the
// real WebAssembly core and the writable host over a fresh copy of the golden workspace (e2e/generate.mjs
// editable-workspace), whose orders service has a test project.
import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const base = 'http://127.0.0.1:5196';
const specsFile = fileURLToPath(new URL('../../../artifacts/studio-e2e/editable/orders/tests/Workflows/OrderFulfilmentV1PathTests.cs', import.meta.url));
const order = '{ "orderId": "o-1", "total": 100, "lines": ["sku"] }';

async function openWorkflow(page: Page, name: string, host = base) {
  await page.goto(host + '/');
  await page.getByRole('navigation', { name: 'Workspace' }).getByRole('link', { name: `Workflow ${name}` }).click();
  await expect(page.getByRole('heading', { level: 1, name: `${name}.sfflow.yaml` })).toBeVisible();
  await expect(page.locator('.flow[data-ready="true"]')).toBeVisible({ timeout: 30_000 });
}

const panel = (page: Page) => page.getByRole('region', { name: 'Simulate' });
const state = (page: Page, term: string) => panel(page).getByRole('definition').filter({ has: page.locator(`xpath=preceding-sibling::dt[1][text()="${term}"]`) });
const status = (page: Page) => panel(page).getByTestId('sim-status');
const active = (page: Page, node: string) => page.locator(`.react-flow__node[data-id="${node}"] [data-active="true"]`);

async function start(page: Page) {
  await page.getByRole('tab', { name: 'Simulate' }).click();
  await panel(page).getByRole('textbox', { name: 'Start event data (JSON)' }).fill(order);
  await panel(page).getByRole('button', { name: /^(Start|Restart)$/ }).click();
  await expect(status(page)).toHaveText('Running', { timeout: 30_000 });
}

test('STU-E2E-11 steps an instance through retries to completion, with its timer, outbox and data', async ({ page }) => {
  await openWorkflow(page, 'simulation');
  await start(page);
  await expect(state(page, 'Step')).toHaveText('reserve-stock');
  await expect(active(page, 'reserve-stock')).toBeVisible();
  await expect(panel(page).getByRole('region', { name: 'Timers' })).toContainText('⏱ timeout for reserve-stock, attempt 1, due 2026-01-01T00:05:00');
  await expect(panel(page).getByRole('region', { name: 'Outbox' })).toContainText('→ inventory.reserve-stock.v1');

  await panel(page).getByRole('button', { name: 'Step' }).click();
  await expect(state(page, 'Step')).toHaveText('charge-payment');
  await panel(page).getByRole('combobox', { name: 'Next outcome' }).selectOption('rejected:payments.declined');
  await panel(page).getByRole('button', { name: 'Step' }).click();
  await expect(state(page, 'Phase')).toHaveText('Retrying');
  await expect(panel(page).getByRole('region', { name: 'Timers' })).toContainText('⏱ retry for charge-payment');
  await expect(panel(page).getByRole('combobox', { name: 'Next outcome' })).toHaveValue('retry');
  await panel(page).getByRole('button', { name: 'Step' }).click();
  await expect(state(page, 'Attempt')).toHaveText('2');
  const charges = panel(page).getByRole('region', { name: 'Outbox' }).getByRole('listitem').filter({ hasText: 'payments.charge.v1' });
  await expect(charges).toHaveCount(2);
  const ids = await charges.locator('code').evaluateAll((codes) => codes.map((c) => c.getAttribute('title')));
  expect(new Set(ids).size).toBe(2); // a re-sent attempt has its own deterministic id

  await panel(page).getByRole('button', { name: 'Run to the end' }).click();
  await expect(status(page)).toHaveText('Completed', { timeout: 30_000 });
  await expect(panel(page).getByRole('status').filter({ hasText: 'The instance has ended: Completed.' })).toBeVisible();
  await expect(panel(page).getByRole('region', { name: 'Outbox' }).getByRole('listitem').last()).toContainText('orders.mark-fulfilled.v1');
  await expect(panel(page).getByRole('region', { name: 'Data' })).toContainText('"orderId": "o-1"');
  await expect(active(page, 'Completed')).toBeVisible();

  const scan = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
  const serious = scan.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(serious.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).slice(0, 3).join(', ')}`)).toEqual([]);
});

test('STU-E2E-12 simulates the compensated paths: from the path explorer, and from a plan', async ({ page }) => {
  await openWorkflow(page, 'simulation');
  await page.getByRole('tab', { name: 'Paths' }).click();
  const explorer = page.getByRole('region', { name: 'Paths' });
  await expect(explorer.getByRole('status').first()).toContainText('paths ·', { timeout: 30_000 });
  await explorer.getByRole('combobox', { name: 'Show' }).selectOption('Compensated');
  const first = explorer.getByRole('list', { name: 'Paths of simulation.sfflow.yaml' }).getByRole('listitem').first();
  const id = (await first.locator('strong').textContent())!;
  await first.getByRole('button', { name: `Simulate ${id}` }).click();

  await expect(page.getByRole('tab', { name: 'Simulate' })).toHaveAttribute('aria-selected', 'true');
  await expect(status(page)).toHaveText('Compensated', { timeout: 30_000 });
  await expect(state(page, 'Path')).toHaveText(id);
  await page.getByRole('tab', { name: 'Statechart' }).click();
  await expect(active(page, 'Compensated')).toBeVisible({ timeout: 30_000 });
  await expect(panel(page).getByRole('region', { name: 'Outbox' })).toContainText('orders.cancel-order.v1');

  // The same unwinding from a plan: fraud at the payment undoes the stock reservation.
  await panel(page).getByRole('combobox', { name: 'Outcome of charge-payment' }).selectOption('rejected:payments.fraud_suspected');
  await start(page);
  await panel(page).getByRole('button', { name: 'Step' }).click();
  await panel(page).getByRole('combobox', { name: 'Next outcome' }).selectOption('rejected:payments.fraud_suspected');
  await panel(page).getByRole('button', { name: 'Step' }).click();
  await expect(status(page)).toHaveText('Compensating');
  await expect(panel(page).getByRole('region', { name: 'Compensation stack' })).toContainText('⟲ undo reserve-stock');
  await expect(active(page, 'undo:reserve-stock')).toBeVisible();
  await expect(panel(page).getByRole('region', { name: 'Outbox' }).getByRole('listitem').last()).toContainText('inventory.release-stock.v1');
  await panel(page).getByRole('button', { name: 'Run to the end' }).click();
  await expect(status(page)).toHaveText('Compensated', { timeout: 30_000 });
  await expect(state(page, 'Failure')).toContainText('payments.fraud_suspected at charge-payment');
});

test('STU-E2E-13 generates specs for the selected paths into the owner test project', async ({ page }) => {
  await openWorkflow(page, 'simulation');
  await page.getByRole('tab', { name: 'Paths' }).click();
  const explorer = page.getByRole('region', { name: 'Paths' });
  await expect(explorer.getByText(/paths selected, covering/)).toBeVisible({ timeout: 30_000 });
  await explorer.getByRole('button', { name: 'Preview specs' }).click();
  const preview = explorer.getByLabel('Specs preview');
  await expect(preview).toContainText('namespace Orders.Tests.Workflows;', { timeout: 30_000 });
  await expect(preview).toContainText('WorkflowSpec.For<Orders.OrderFulfilmentWorkflowV1, Orders.OrdersModule>()');
  await expect(explorer.getByRole('status', { name: 'Specs' })).toHaveText(/^\d+ tests for orders\/tests\/Workflows\/OrderFulfilmentV1PathTests\.cs\.$/);

  await explorer.getByRole('button', { name: 'Write specs' }).click();
  await expect(explorer.getByRole('status', { name: 'Specs' })).toHaveText(/^Wrote \d+ tests to orders\/tests\/Workflows\/OrderFulfilmentV1PathTests\.cs\.$/);
  expect(readFileSync(specsFile, 'utf8')).toBe(await preview.textContent());

  // One path only: the file is regenerated with one test.
  await explorer.getByRole('button', { name: 'Clear the selection' }).click();
  await explorer.getByRole('checkbox', { name: 'Select P01' }).check();
  await explorer.getByRole('button', { name: 'Write specs' }).click();
  await expect(explorer.getByRole('status', { name: 'Specs' })).toHaveText('Wrote 1 test to orders/tests/Workflows/OrderFulfilmentV1PathTests.cs.');
  expect(readFileSync(specsFile, 'utf8').match(/\[Fact/g)).toHaveLength(1);

  // A workspace whose service has no test project says what to add.
  await openWorkflow(page, 'order-fulfilment', 'http://127.0.0.1:5198');
  await page.getByRole('tab', { name: 'Paths' }).click();
  const readOnly = page.getByRole('region', { name: 'Paths' });
  await expect(readOnly.getByText(/To write specs, give orders a tests folder/)).toBeVisible({ timeout: 30_000 });
  await expect(readOnly.getByRole('button', { name: 'Write specs' })).toBeDisabled();
});
