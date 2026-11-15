// STU-E2E-01 on the OrderFulfilment workspace (tests/Fixtures/OrderFulfilment, WP-S.13) and STU-A11Y-04 (plan 10 §3.3): the real
// four-service workspace opens, its workflow is valid against every manifest and has the doc 15 §5.1 path set; and what
// the views draw meets WCAG 2.1 AA contrast in both themes (text 4.5:1, the boundaries of states, edges and controls 3:1),
// measured on the rendered elements against what is behind them.
import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { contrast, GRAPHIC, over, parseColor, TEXT, type Rgba } from '../src/a11y/contrast';

const base = 'http://127.0.0.1:5194';

async function openWorkflow(page: Page) {
  await page.goto(base + '/');
  await page.getByRole('navigation', { name: 'Workspace' }).getByRole('link', { name: 'Workflow order-fulfilment' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'order-fulfilment.sfflow.yaml' })).toBeVisible();
  await expect(page.locator('.flow[data-ready="true"]')).toBeVisible({ timeout: 30_000 });
}

test('STU-E2E-01 the OrderFulfilment sample opens: four services, a valid workflow, the doc example paths', async ({ page }) => {
  await page.goto(base + '/');
  await expect(page.getByRole('heading', { level: 1, name: 'commerce' })).toBeVisible();
  const map = page.getByRole('region', { name: 'Domain map of commerce' });
  await expect(map.locator('.flow[data-ready="true"]')).toBeVisible({ timeout: 30_000 });
  for (const service of ['orders', 'inventory', 'payments', 'shipping']) {
    await expect(map.locator(`.react-flow__node[data-id="service:${service}"]`)).toBeVisible();
  }

  await expect(map.locator('[aria-label^="Topic inventory.events"]')).toBeVisible();
  await expect(map.locator('[aria-label="orders commands payment: payments.charge.v1, payments.refund.v1"]')).toBeAttached();

  await openWorkflow(page);
  await expect(page.getByRole('region', { name: 'Diagnostics' }).getByRole('status')).toHaveText('No problems.', { timeout: 30_000 });
  await expect(page.locator('.react-flow__node[data-id="create-shipment"]')).toHaveAttribute('aria-label', /^Pivot \(point of no return\): create-shipment/);
  await page.getByRole('tab', { name: 'Paths' }).click();
  await expect(page.getByRole('region', { name: 'Paths' }).getByRole('status').first())
    .toContainText('437 paths · ', { timeout: 30_000 });
  await expect(page.getByRole('region', { name: 'Paths' }).getByRole('status').first()).toContainText('a minimal cover of 6');
  const scan = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
  expect(scan.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => v.id)).toEqual([]);
});

interface Measured {
  what: string;
  color: string;
  layers: string[];
}

// Every element's colour (a stroke, a border, text) and the backgrounds behind it, from itself (or its parent, for a
// border, which sits on the outside) up to the page.
async function measure(page: Page, rules: { selector: string; property: 'stroke' | 'border-top-color' | 'color'; from: 'self' | 'parent' }[]): Promise<Measured[]> {
  return page.evaluate((list) => list.flatMap(({ selector, property, from }) =>
    [...document.querySelectorAll(selector)]
      .filter((element) => {
        const box = element.getBoundingClientRect();
        return box.width > 0 && box.height > 0 && !(element as HTMLButtonElement).disabled;
      })
      .slice(0, 40)
      .map((element) => {
        const layers: string[] = [];
        for (let e: Element | null = from === 'self' ? element : element.parentElement; e !== null; e = e.parentElement) {
          layers.push(getComputedStyle(e).backgroundColor);
        }

        const label = element.getAttribute('aria-label') ?? element.closest('[aria-label]')?.getAttribute('aria-label') ?? element.textContent?.slice(0, 30) ?? '';
        return { what: `${selector} (${label})`, color: getComputedStyle(element).getPropertyValue(property), layers };
      })), rules);
}

function behind(layers: string[]): Rgba {
  const parsed = layers.map((l) => parseColor(l) ?? { r: 0, g: 0, b: 0, a: 0 });
  const opaque = parsed.findIndex((c) => c.a >= 1);
  const above = opaque < 0 ? parsed : parsed.slice(0, opaque);
  return above.reduceRight((result, layer) => over(layer, result), parsed[opaque] ?? { r: 255, g: 255, b: 255, a: 1 });
}

function failing(measured: Measured[], minimum: number): string[] {
  return measured.flatMap((m) => {
    const color = parseColor(m.color);
    if (color === undefined || color.a === 0) {
      return [];
    }

    const back = behind(m.layers);
    const ratio = contrast(over(color, back), back);
    return ratio < minimum ? [`${m.what}: ${ratio.toFixed(2)}:1`] : [];
  });
}

for (const theme of ['Light', 'Dark']) {
  test(`STU-A11Y-04 states, edges, controls and labels meet WCAG contrast (${theme})`, async ({ page }) => {
    await page.goto(base + '/');
    await page.getByRole('radio', { name: theme }).check();
    await expect(page.locator('.flow[data-ready="true"]')).toBeVisible({ timeout: 30_000 });
    const map = await measure(page, [
      { selector: '.map-service', property: 'border-top-color', from: 'parent' },
      { selector: '.map-aggregate', property: 'border-top-color', from: 'parent' },
      { selector: '.map-topic', property: 'border-top-color', from: 'parent' },
      { selector: 'path.map-edge', property: 'stroke', from: 'parent' },
    ]);

    await openWorkflow(page);
    const chart = await measure(page, [
      { selector: '.state', property: 'border-top-color', from: 'parent' },
      { selector: '.react-flow__edge-path', property: 'stroke', from: 'parent' },
      { selector: '.button', property: 'border-top-color', from: 'parent' },
      { selector: 'select', property: 'border-top-color', from: 'parent' },
    ]);
    const labels = await measure(page, [
      { selector: '.edge-label', property: 'color', from: 'self' },
      { selector: '.state-detail', property: 'color', from: 'self' },
    ]);

    expect(map.length + chart.length).toBeGreaterThan(30);
    expect(labels.length).toBeGreaterThan(10);
    expect([...failing(map, GRAPHIC), ...failing(chart, GRAPHIC), ...failing(labels, TEXT)]).toEqual([]);
  });
}
