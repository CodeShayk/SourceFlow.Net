// STU-PERF-01..03 (plan 10 §3.3): the Playwright project 'perf' runs these after every other test, alone, so they
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

// From a key press in the YAML editor to the diagnostics panel showing the core's answer, measured in the page (a
// keydown listener and a MutationObserver on the panel), on a copy of the commerce workflow in the editable workspace.
test('STU-PERF-03 a validation round trip (edit to diagnostics) takes under 100 ms', async ({ page }) => {
  const base = 'http://127.0.0.1:5196';
  await page.goto(base + '/');
  await page.getByRole('navigation', { name: 'Workspace' }).getByRole('link', { name: 'Workflow perf' }).click();
  const panel = page.getByRole('region', { name: 'Diagnostics' });
  await expect(panel.getByRole('status')).toHaveText('No problems.', { timeout: 30_000 });
  await page.getByRole('tab', { name: 'YAML' }).click();
  const monaco = page.getByTestId('monaco');
  await expect(monaco.locator('.view-lines')).toContainText('sourceflow.workflow', { timeout: 30_000 });
  await monaco.locator('.view-lines').click();
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+F');
  await page.keyboard.type('inventory.reserve-stock.v1');
  await page.keyboard.press('Enter');
  await page.keyboard.press('Escape');
  await page.keyboard.press('ArrowRight');   // the cursor right after the command: typing breaks it, Backspace mends it

  await page.evaluate(() => {
    const region = document.querySelector('[aria-label="Diagnostics"]')!;
    const state = { armed: false, start: 0, expect: '', times: [] as number[] };
    (window as unknown as { roundTrip: typeof state }).roundTrip = state;
    window.addEventListener('keydown', () => {
      if (state.armed) {
        state.start = performance.now();
        state.armed = false;
      }
    }, true);
    new MutationObserver(() => {
      if (state.start > 0 && new RegExp(state.expect).test(region.textContent ?? '')) {
        state.times.push(performance.now() - state.start);
        state.start = 0;
      }
    }).observe(region, { subtree: true, childList: true, characterData: true });
  });

  const rounds: Array<{ key: string; expect: string }> = [];
  for (let i = 0; i < 5; i++) {
    rounds.push({ key: 'x', expect: 'Error WF-' }, { key: 'Backspace', expect: 'No problems' });
  }

  for (const [n, round] of rounds.entries()) {
    await page.evaluate((r) => {
      const state = (window as unknown as { roundTrip: { armed: boolean; expect: string } }).roundTrip;
      state.expect = r.expect;
      state.armed = true;
    }, round);
    await page.keyboard.press(round.key);
    await expect.poll(() => page.evaluate(() => (window as unknown as { roundTrip: { times: number[] } }).roundTrip.times.length), { timeout: 10_000 }).toBe(n + 1);
  }

  const times = await page.evaluate(() => (window as unknown as { roundTrip: { times: number[] } }).roundTrip.times);
  const warm = times.slice(2).sort((a, b) => a - b);   // the first error and fix warm the worker and the panel
  const median = warm[Math.floor(warm.length / 2)] ?? Number.POSITIVE_INFINITY;
  console.info(`STU-PERF-03: validation round trips ${times.map((t) => t.toFixed(0)).join(', ')} ms (median after warm-up ${median.toFixed(0)} ms)`);
  expect(median).toBeLessThan(100);
});
