// STU-E2E-04..06 (the domain map, the statechart, the swimlane) and STU-A11Y-01..03 (plan 10 §3.3).
import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

async function openWorkflow(page: Page, name: string) {
  await page.goto('/');
  await page.getByRole('navigation', { name: 'Workspace' }).getByRole('link', { name: `Workflow ${name}` }).click();
  await expect(page.getByRole('heading', { level: 1, name: `${name}.sfflow.yaml` })).toBeVisible();
}

async function statechartReady(page: Page) {
  await expect(page.locator('.flow[data-ready="true"]')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.react-flow__edge').first()).toBeAttached();
}

test('STU-E2E-04 the domain map shows who owns what, and highlights a choreography chain', async ({ page }) => {
  await page.goto('/');
  const map = page.getByRole('region', { name: 'Domain map of commerce' });
  await expect(map.locator('.flow[data-ready="true"]')).toBeVisible({ timeout: 30_000 });
  await expect(map.getByLabel('Aggregate stock: inventory.release-stock.v1, inventory.reserve-stock.v1')).toBeVisible();
  await expect(map.locator('[aria-label="orders commands stock: inventory.release-stock.v1, inventory.reserve-stock.v1"]')).toBeAttached();
  await expect(map.locator('[aria-label^="Topic orders.events"]')).toBeVisible();

  // Keyboard: focus a service and press Enter to select it; its chain stays highlighted.
  await map.locator('.react-flow__node[data-id="service:payments"]').focus();
  await page.keyboard.press('Enter');
  await expect(map.locator('.flow-map')).not.toHaveAttribute('data-highlighted', '0');
  await expect(map.locator('path.map-edge.highlighted').first()).toBeAttached();
});

test('STU-E2E-05 the statechart draws every state and transition kind with its encoding', async ({ page }) => {
  await openWorkflow(page, 'order-fulfilment');
  await statechartReady(page);
  const chart = page.getByRole('region', { name: 'Statechart of order-fulfilment.sfflow.yaml' });
  await expect(chart.locator('[data-kind="pivot"]')).toContainText('◆ create-shipment');
  await expect(chart.locator('[aria-label="Retry: charge-payment is retried 3× every PT10M on payments.declined"]')).toBeAttached();
  await expect(chart.locator('[aria-label="Failure: shipping.address_invalid at create-shipment leads to await-address-fix"]')).toBeAttached();
  await expect(chart.locator('.edge-label.kind-timeout').filter({ hasText: '⏱ P1D' })).toBeVisible();
  await expect(chart.getByRole('figure', { name: 'Legend' })).toContainText('⟲ Compensation');
  const nodes = await chart.locator('.react-flow__node').count();
  expect(nodes).toBe(12);
});

test('STU-E2E-06 the swimlane replays a compensated path between the services', async ({ page }) => {
  await openWorkflow(page, 'order-fulfilment');
  await page.getByRole('tab', { name: 'Swimlane' }).click();
  const picker = page.getByRole('combobox', { name: 'Path' });
  const compensated = await picker.locator('option', { hasText: 'Compensated' }).first().getAttribute('value');
  await picker.selectOption(compensated!);
  const messages = page.getByRole('list', { name: `Messages of ${compensated}` });
  await expect(messages).toBeVisible({ timeout: 30_000 });
  await expect(messages.getByRole('listitem').filter({ hasText: '⟲' }).first()).toBeVisible();
  await expect(messages.locator('[aria-label^="command from order-fulfilment@1 (orders) to inventory"]').first()).toBeAttached();
  await expect(page.locator('.lane-head')).toContainText(['order-fulfilment@1 (orders)']);
});

for (const theme of ['Light', 'Dark']) {
  test(`STU-A11Y-01 no serious accessibility violations in the views (${theme})`, async ({ page }) => {
    await page.goto('/');
    await page.getByRole('radio', { name: theme }).check();
    await expect(page.locator('.flow[data-ready="true"]')).toBeVisible({ timeout: 30_000 });
    const scans = [await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze()];
    await openWorkflow(page, 'order-fulfilment');
    await statechartReady(page);
    scans.push(await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze());
    await page.getByRole('tab', { name: 'Swimlane' }).click();
    await expect(page.locator('.lanes')).toBeVisible({ timeout: 30_000 });
    scans.push(await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze());
    const serious = scans.flatMap((s) => s.violations).filter((v) => v.impact === 'serious' || v.impact === 'critical');
    expect(serious.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).slice(0, 3).join(', ')}`)).toEqual([]);
  });
}

test('STU-A11Y-02 keyboard users reach states and transitions by name', async ({ page }) => {
  await openWorkflow(page, 'order-fulfilment');
  await statechartReady(page);
  await page.locator('.react-flow__node').first().focus();
  await expect(page.locator('.react-flow__node:focus')).toHaveAttribute('aria-label', /^Start: /);
  let names: string[] = [];
  for (let i = 0; i < 40; i++) {
    await page.keyboard.press('Tab');
    names.push((await page.evaluate(() => document.activeElement?.getAttribute('aria-label'))) ?? '');
  }

  names = names.filter((n) => n.length > 0);
  expect(names.some((n) => n.startsWith('Pivot (point of no return): create-shipment'))).toBe(true);
  expect(names.some((n) => /^(Success|Failure|Retry|Timeout|Compensation|Manual): /.test(n))).toBe(true);
  await page.keyboard.press('Alt+3');
  await expect(page.getByRole('region', { name: 'Diagnostics' })).toBeFocused();
});

test('STU-A11Y-03 reduced motion turns animation off, and labels never rely on colour', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await openWorkflow(page, 'order-fulfilment');
  await statechartReady(page);
  const animated = await page.evaluate(() => [...document.querySelectorAll('*')].filter((e) => {
    const style = getComputedStyle(e);
    return style.animationName !== 'none' || parseFloat(style.transitionDuration) > 0;
  }).length);
  expect(animated).toBe(0);
  const labels = await page.locator('.edge-label').allTextContents();
  expect(labels.length).toBeGreaterThan(10);
  expect(labels.every((l) => /^[▶✓✕↻⏱⟲✋⤼]/u.test(l))).toBe(true);
});
