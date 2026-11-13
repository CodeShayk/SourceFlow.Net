// STU-E2E-07..10 and STU-A11Y-05 (plan 10 §3.3): editing in the browser against a writable host on a fresh copy of the golden workspace
// (e2e/generate.mjs editable-workspace): an error typed in Monaco shows everywhere, a visual fix clears it, visual edits
// save with the file's comments, and an add → save → undo → save round trip gives back the file byte for byte.
import { expect, test, type Page } from '@playwright/test';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const base = 'http://127.0.0.1:5196';
const golden = fileURLToPath(new URL('../../../tests/workflow-golden/cases', import.meta.url));
const original = (name: string) => readFileSync(join(golden, name, 'workflow.sfflow.yaml'), 'utf8');
const file = (name: string) => `orders/workflows/${name}.sfflow.yaml`;

async function openWorkflow(page: Page, name: string) {
  await page.goto(base + '/');
  await page.getByRole('navigation', { name: 'Workspace' }).getByRole('link', { name: `Workflow ${name}` }).click();
  await expect(page.getByRole('heading', { level: 1, name: `${name}.sfflow.yaml` })).toBeVisible();
  // The statechart, or why it can't be drawn: the text stays editable either way.
  await expect(page.locator('.flow[data-ready="true"], [role="tabpanel"] [role="alert"]').first()).toBeVisible({ timeout: 30_000 });
}

async function stored(page: Page, name: string): Promise<string> {
  const response = await page.request.get(`${base}/api/files?path=${encodeURIComponent(file(name))}`);
  expect(response.ok()).toBe(true);
  return response.text();
}

async function selectStep(page: Page, id: string) {
  await page.getByRole('tab', { name: 'Statechart' }).click();
  await page.locator(`.react-flow__node[data-id="${id}"]`).click();
  const form = page.getByRole('form', { name: `Properties of ${id}` });
  await expect(form).toBeVisible();
  return form;
}

const status = (page: Page) => page.getByRole('toolbar', { name: 'Edit' }).getByRole('status');
const diagnostics = (page: Page) => page.getByRole('region', { name: 'Diagnostics' });

test('STU-E2E-07 an error typed in Monaco shows in the panel, on the step and as a marker', async ({ page }) => {
  await openWorkflow(page, '01-order-fulfilment');
  await expect(diagnostics(page).getByRole('status')).toHaveText('No problems.', { timeout: 30_000 });
  await page.getByRole('tab', { name: 'YAML' }).click();
  const monaco = page.getByTestId('monaco');
  await expect(monaco.locator('.view-lines')).toContainText('sourceflow.workflow', { timeout: 30_000 });

  // Type the error as a person would: find the command, then type over it.
  await monaco.locator('.view-lines').click();
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+F');
  await page.keyboard.type('reserve-stock.v1');
  await page.keyboard.press('Enter');
  await page.keyboard.press('Escape');
  await page.keyboard.type('reserve-stocks.v1');
  await expect(status(page)).toHaveText('Unsaved changes');

  await expect(diagnostics(page).getByRole('button', { name: /Error WF-V01/ }).first()).toBeVisible({ timeout: 30_000 });
  await expect(monaco.locator('.squiggly-error').first()).toBeAttached({ timeout: 10_000 });
  await page.getByRole('tab', { name: 'Statechart' }).click();
  await expect(page.locator('.react-flow__node[data-id="reserve-stock"] .state-problems')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.react-flow__node[data-id="reserve-stock"]')).toHaveAttribute('aria-label', /problem/);

  // STU-E2E-08: fixed visually, the text is the file again.
  const form = await selectStep(page, 'reserve-stock');
  await form.getByRole('combobox', { name: 'Command', exact: true }).selectOption('inventory.reserve-stock.v1');
  await expect(diagnostics(page).getByRole('status')).toHaveText('No problems.', { timeout: 30_000 });
  await expect(page.locator('.react-flow__node[data-id="reserve-stock"] .state-problems')).toHaveCount(0);
  await expect(status(page)).toHaveText('No changes');
  await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled();
});

test('STU-E2E-08 a visual fix of an invalid workflow clears its diagnostic', async ({ page }) => {
  await openWorkflow(page, '09-v01-unknown-type');
  // An unknown command, and a command where the success event belongs.
  const error = diagnostics(page).getByRole('button', { name: /Error WF-V01/ });
  await expect(error).toHaveCount(2, { timeout: 30_000 });
  await expect(page.locator('.react-flow__node[data-id="approve"] .state-problems')).toHaveText(/2/);
  let form = await selectStep(page, 'approve');
  await form.getByRole('combobox', { name: 'Command', exact: true }).selectOption('orders.approve-order.v1');
  await expect(error).toHaveCount(1, { timeout: 30_000 });
  form = page.getByRole('form', { name: 'Properties of approve' });
  await form.getByRole('combobox', { name: 'Success event' }).selectOption('orders.order-approved.v1');
  await expect(diagnostics(page).getByRole('status')).toHaveText('No problems.', { timeout: 30_000 });
  await expect(page.locator('.react-flow__node[data-id="approve"] .state-problems')).toHaveCount(0);
  await expect(status(page)).toHaveText('Unsaved changes');
});

test('STU-E2E-09 a retry and a compensation added visually save with the file and its comments', async ({ page }) => {
  await openWorkflow(page, 'visual-edits');
  const form = await selectStep(page, 'review-large-order');
  const retries = form.getByRole('textbox', { name: 'Retry (timeout): retries' });
  await retries.fill('2');
  await retries.press('Enter');
  await form.getByRole('combobox', { name: 'Undo command' }).selectOption('orders.cancel-order.v1');
  await expect(status(page)).toHaveText('Unsaved changes');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(status(page)).toHaveText('Saved.');

  const text = await stored(page, 'visual-edits');
  const review = text.slice(text.indexOf('- id: review-large-order'), text.indexOf('- id: create-shipment'));
  expect(review).toContain('timeout: { after: P1D, then: manual, retry: { max: 2 } }');
  expect(review).toMatch(/compensate:[\s\S]*orders\.cancel-order\.v1/);
  for (const comment of ['# generated into the orders service', '# CESQL guard; skipped when false', '# point of no return', '# business retry']) {
    expect(text).toContain(comment);
  }

  // Everything outside the edited step is the file as it was.
  const before = original('01-order-fulfilment');
  const cut = (t: string) => t.slice(0, t.indexOf('- id: review-large-order')) + t.slice(t.indexOf('- id: create-shipment'));
  expect(cut(text)).toBe(cut(before));

  // The core checks the new compensation like any other: it still needs its map and a timeout.
  await expect(diagnostics(page).getByRole('button', { name: /Error WF-V12 .*orders\.cancel-order\.v1 needs 'orderId'/ })).toBeVisible({ timeout: 30_000 });
  await expect(diagnostics(page).getByRole('button', { name: /Error WF-V06 .*compensation of 'review-large-order'/ })).toBeVisible();
});

test('STU-A11Y-05 keyboard only: add a step, connect it, give it a failure, save', async ({ page }) => {
  // Focus moves between controls (as Tab or a screen reader would take it); every action is a key press.
  await page.goto(base + '/');
  await expect(page.getByRole('navigation', { name: 'Workspace' }).getByRole('link', { name: 'Workflow keyboard-edits' })).toBeVisible();
  await page.keyboard.press('Alt+1');
  await expect(page.getByRole('navigation', { name: 'Workspace' })).toBeFocused();
  await page.getByRole('navigation', { name: 'Workspace' }).getByRole('link', { name: 'Workflow keyboard-edits' }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { level: 1, name: 'keyboard-edits.sfflow.yaml' })).toBeVisible();
  await expect(page.locator('.flow[data-ready="true"]')).toBeVisible({ timeout: 30_000 });

  // Add a step: choose its command by typing, Tab past the event it waits for, Enter on Add step.
  const palette = page.getByRole('region', { name: 'Palette' });
  const command = palette.getByRole('combobox', { name: 'Command', exact: true });
  await command.focus();
  await page.keyboard.type('payments.charge.v1');
  await expect(command).toHaveValue('payments.charge.v1');
  await page.keyboard.press('Tab');
  await expect(palette.getByRole('combobox', { name: 'Waits for' })).toBeFocused();
  await expect(palette.getByRole('combobox', { name: 'Waits for' })).toHaveValue('payments.payment-captured.v1');
  await page.keyboard.press('Tab');
  await expect(palette.getByRole('button', { name: 'Add step' })).toBeFocused();
  await page.keyboard.press('Enter');
  const form = page.getByRole('form', { name: 'Properties of charge' });
  await expect(form).toBeVisible();

  // Give it a failure, and connect the failure to another step.
  await form.getByRole('combobox', { name: 'Add a failure' }).focus();
  await page.keyboard.type('rejected payments.declined');
  const then = form.getByRole('combobox', { name: 'Then (rejection payments.declined)' });
  await expect(then).toBeVisible();
  await then.focus();
  await page.keyboard.type('go to create-shipment');
  await expect(then).toHaveValue('goto:create-shipment');

  await page.keyboard.press('ControlOrMeta+S');
  await expect(status(page)).toHaveText('Saved.');
  const text = await stored(page, 'keyboard-edits');
  const step = text.slice(text.indexOf('  - id: charge\n'));
  expect(step).toContain('command: payments.charge.v1');
  expect(step).toContain('success: payments.payment-captured.v1');
  expect(step).toMatch(/rejected: payments\.declined[\s\S]*then: \{ goto: create-shipment \}/);
  await expect(page.locator('.react-flow__node[data-id="charge"]')).toBeAttached({ timeout: 30_000 });
});

// STU-E2E-10 on every golden workflow.
const cases = readdirSync(golden);
for (const name of cases) {
  test(`STU-E2E-10 add, save, undo and save gives back ${name} byte for byte`, async ({ page }) => {
    await openWorkflow(page, name);
    const palette = page.getByRole('region', { name: 'Palette' });
    await expect(palette).toContainText('At the end of the workflow.');
    await palette.getByRole('combobox', { name: 'Command', exact: true }).selectOption('payments.refund.v1');
    await palette.getByRole('button', { name: 'Add step' }).click();
    if (readFileSync(join(golden, name, 'expected.diagnostics.json'), 'utf8').includes('Invalid YAML')) {
      // Nothing is spliced into a file that doesn't parse: the edit is refused, with the way out.
      await expect(page.getByRole('alert').filter({ hasText: "The YAML doesn't parse" })).toContainText('fix it in the YAML tab first');
      await expect(status(page)).toHaveText('No changes');
      await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled();
      expect(await stored(page, name)).toBe(original(name));
      return;
    }

    await expect(page.getByRole('form', { name: 'Properties of refund' })).toBeVisible();
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(status(page)).toHaveText('Saved.');
    const added = await stored(page, name);
    expect(added).toContain('  - id: refund\n');
    expect(added.length).toBeGreaterThan(original(name).length);

    await page.getByRole('button', { name: 'Undo' }).click();
    await expect(status(page)).toHaveText('Unsaved changes');
    await page.keyboard.press('ControlOrMeta+S');
    await expect(status(page)).toHaveText('Saved.');
    expect(await stored(page, name)).toBe(original(name));
  });
}
