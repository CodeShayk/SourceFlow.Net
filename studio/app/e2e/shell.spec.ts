// STU-E2E-01..03: open the workspace, open workflows with diagnostics from the WebAssembly core, and the read-only host.
import { expect, test } from '@playwright/test';

test('STU-E2E-01 opens the workspace: the domain, its services and their workflows', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1, name: 'commerce' })).toBeVisible();
  const explorer = page.getByRole('navigation', { name: 'Workspace' });
  for (const service of ['orders', 'inventory', 'payments', 'shipping']) {
    await expect(explorer.getByRole('link', { name: `Service ${service}` })).toBeVisible();
  }

  await expect(explorer.getByRole('link', { name: 'Workflow order-fulfilment' })).toBeVisible();
  await expect(explorer.getByRole('link', { name: 'Workflow unknown-type' })).toBeVisible();
  await explorer.getByRole('link', { name: 'Service payments' }).click();
  await expect(page).toHaveURL(/\/services\/payments$/);
  await expect(page.getByRole('heading', { level: 1, name: 'payments' })).toBeVisible();

  // A client route reloads into the SPA.
  await page.reload();
  await expect(page.getByRole('heading', { level: 1, name: 'payments' })).toBeVisible();
});

test('STU-E2E-02 opens workflows: the WebAssembly core validates them in a worker', async ({ page }) => {
  await page.goto('/');
  const explorer = page.getByRole('navigation', { name: 'Workspace' });
  await explorer.getByRole('link', { name: 'Workflow order-fulfilment' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'order-fulfilment.sfflow.yaml' })).toBeVisible();
  const diagnostics = page.getByRole('region', { name: 'Diagnostics' });
  await expect(diagnostics.getByRole('status')).toHaveText('No problems.', { timeout: 30_000 });

  await explorer.getByRole('link', { name: 'Workflow unknown-type' }).click();
  const error = diagnostics.getByRole('button', { name: /Error WF-V01/ }).first();
  await expect(error).toBeVisible({ timeout: 30_000 });
  await error.click();
  await expect(page.getByRole('list', { name: 'Source of unknown-type.sfflow.yaml' }).locator('li[aria-current="location"]')).toHaveCount(1);

  await page.keyboard.press('Shift+?');
  await expect(page.getByRole('dialog', { name: 'Keyboard map' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('STU-E2E-03 a read-only host shows it and refuses writes', async ({ page }) => {
  await page.goto('http://127.0.0.1:5198/services/orders');
  await expect(page.getByRole('note')).toHaveText('Read-only');
  await expect(page.getByRole('button', { name: 'Export the manifest again' })).toBeDisabled();

  const write = await page.request.put('http://127.0.0.1:5198/api/files?path=orders%2Fworkflows%2Forder-fulfilment.sfflow.yaml', {
    data: { content: 'name: changed' },
  });
  expect(write.status()).toBe(403);
  expect(await write.text()).toContain('--read-only');

  const rebinding = await page.request.get('http://127.0.0.1:5198/api/workspace', { headers: { Host: 'evil.example' } });
  expect(rebinding.status()).toBe(403);
});
