// STU-PERF-01..02 (plan 10 §3.3): the Playwright project 'perf' runs these after every other test, alone, so they
// measure the app rather than the test load.
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

test('STU-PERF-01 a 200-state workflow lays out and renders in under a second', async ({ page }) => {
  await openWorkflow(page, 'order-fulfilment'); // the core and ELK workers are warm
  await statechartReady(page);
  const started = Date.now();
  await page.getByRole('navigation', { name: 'Workspace' }).getByRole('link', { name: 'Workflow large' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'large.sfflow.yaml' })).toBeVisible();
  const flow = page.locator('.flow[data-ready="true"]');
  await expect(flow).toBeVisible({ timeout: 30_000 });
  const states = Number(await flow.getAttribute('data-nodes'));
  expect(states).toBeGreaterThan(200); // 200 steps, their compensations, the start and the end states
  await expect(page.locator('.react-flow__node')).toHaveCount(states);
  const elapsed = Date.now() - started;
  console.info(`STU-PERF-01: 200 steps laid out and rendered in ${elapsed} ms`);
  expect(elapsed).toBeLessThan(1_000);
});

test('STU-PERF-02 a workspace of 20 services and 50 workflows opens in under three seconds', async ({ page }) => {
  const started = Date.now();
  await page.goto('http://127.0.0.1:5197/');
  await expect(page.getByRole('heading', { level: 1, name: 'large' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Workspace' }).getByRole('link', { name: /^Workflow / })).toHaveCount(50);
  await expect(page.locator('.flow-map[data-ready="true"]')).toBeVisible({ timeout: 30_000 });
  const elapsed = Date.now() - started;
  console.info(`STU-PERF-02: 20 services and 50 workflows opened in ${elapsed} ms`);
  expect(elapsed).toBeLessThan(3_000);
});
