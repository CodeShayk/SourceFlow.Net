// STU-E2E-14 (exports: SVG, PNG, Mermaid, BPMN) and STU-E2E-15 (the static site renders read-only) (plan 10 §3.3).
import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const golden = (name: string) => readFileSync(fileURLToPath(new URL(`../../../tests/workflow-golden/cases/01-order-fulfilment/${name}`, import.meta.url)), 'utf8');
const site = 'http://127.0.0.1:5195/docs/studio/';

async function openWorkflow(page: Page, url = '/') {
  await page.goto(url);
  await page.getByRole('navigation', { name: 'Workspace' }).getByRole('link', { name: 'Workflow order-fulfilment' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'order-fulfilment.sfflow.yaml' })).toBeVisible();
  await expect(page.locator('.flow[data-ready="true"]')).toBeVisible({ timeout: 30_000 });
}

async function exported(page: Page, label: string): Promise<{ name: string; bytes: Buffer }> {
  const download = page.waitForEvent('download');
  await page.getByRole('group', { name: 'Export' }).getByRole('button', { name: `Export ${label}` }).click();
  const file = await download;
  return { name: file.suggestedFilename(), bytes: readFileSync((await file.path())!) };
}

test('STU-E2E-14 exports the statechart as SVG and PNG, and the workflow as Mermaid and BPMN', async ({ page }) => {
  await openWorkflow(page);

  const svg = await exported(page, 'SVG');
  expect(svg.name).toBe('order-fulfilment.svg');
  const text = svg.bytes.toString('utf8');
  expect(text).toMatch(/^<svg xmlns="http:\/\/www\.w3\.org\/2000\/svg" width="(\d+)" height="(\d+)"/);
  expect(text).toContain('<title id="sf-title">Statechart of order-fulfilment.sfflow.yaml</title>');
  expect(text.match(/<g class="state /g)).toHaveLength(12);
  expect(text).toContain('◆ create-shipment');
  expect(text).toContain('Retry: charge-payment is retried 3× every PT10M on payments.declined');
  // The SVG is a document of its own: the browser draws it as an image.
  const drawn = await page.evaluate(async (source) => {
    const image = new Image();
    image.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(source);
    await image.decode();
    return image.naturalWidth;
  }, text);
  const width = Number(/width="(\d+)"/.exec(text)![1]);
  expect(drawn).toBe(width);

  const png = await exported(page, 'PNG');
  expect(png.name).toBe('order-fulfilment.png');
  expect([...png.bytes.subarray(0, 8)]).toEqual([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  expect(png.bytes.readUInt32BE(16)).toBe(width * 2); // IHDR: drawn at twice the size
  expect(png.bytes.length).toBeGreaterThan(20_000);

  const mermaid = await exported(page, 'Mermaid');
  expect(mermaid.name).toBe('order-fulfilment.mmd');
  expect(mermaid.bytes.toString('utf8')).toBe(golden('expected.mmd'));
  const bpmn = await exported(page, 'BPMN');
  expect(bpmn.name).toBe('order-fulfilment.bpmn');
  expect(bpmn.bytes.toString('utf8')).toBe(golden('expected.bpmn'));

  // The picture follows the theme.
  await page.getByRole('radio', { name: 'Dark' }).check();
  expect((await exported(page, 'SVG')).bytes.toString('utf8')).toContain('fill="#15181c"');
});

test('STU-E2E-15 the static site renders the workspace read-only from a sub-path', async ({ page }) => {
  // Everything the site loads comes from its own folder (other origins, such as a local security product's injected
  // scripts, aren't the site's).
  const outside: string[] = [];
  page.on('request', (request) => {
    const url = request.url();
    if (url.startsWith(new URL(site).origin) && !url.startsWith(site)) {
      outside.push(url);
    }
  });

  await page.goto(site);
  await expect(page.getByRole('heading', { level: 1, name: 'commerce' })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('note')).toHaveText('Read-only');
  await expect(page.locator('.flow-map[data-ready="true"]')).toBeVisible({ timeout: 30_000 });

  await openWorkflow(page, site);
  expect(page.url()).toBe(`${site}#/workflows/orders/workflows/order-fulfilment.sfflow.yaml`);
  await expect(page.getByRole('region', { name: 'Diagnostics' }).getByRole('status')).toHaveText('No problems.', { timeout: 30_000 });
  await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled();
  await expect(page.getByRole('toolbar', { name: 'Edit' }).getByRole('status')).toHaveText('Read-only');

  // The core runs from the site's own folder: simulate, and list the paths (specs need the local host).
  await page.getByRole('tab', { name: 'Simulate' }).click();
  await page.getByRole('region', { name: 'Simulate' }).getByRole('button', { name: 'Start' }).click();
  await expect(page.getByTestId('sim-status')).toHaveText('Running', { timeout: 30_000 });
  await page.getByRole('tab', { name: 'Paths' }).click();
  await expect(page.getByText(/This static site can't write specs/)).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('button', { name: 'Preview specs' })).toBeDisabled();
  await page.getByRole('tab', { name: 'YAML' }).click();
  await expect(page.getByTestId('monaco').locator('.view-lines')).toContainText('sourceflow.workflow', { timeout: 30_000 });
  expect((await exported(page, 'Mermaid')).bytes.toString('utf8')).toBe(golden('expected.mmd'));

  // A hash route survives a reload on a host with no fallback to index.html.
  await page.reload();
  await expect(page.getByRole('heading', { level: 1, name: 'order-fulfilment.sfflow.yaml' })).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.flow[data-ready="true"]')).toBeVisible({ timeout: 30_000 });
  const write = await page.request.put(`${site}api/files?path=x.sfflow.yaml`, { data: { content: 'x' } });
  expect(write.status()).toBe(404); // nothing to write to
  expect(outside).toEqual([]);

  const scan = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
  expect(scan.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => v.id)).toEqual([]);
});
