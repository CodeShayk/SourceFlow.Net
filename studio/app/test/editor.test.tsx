// STU-CT-19..28: editing the golden order-fulfilment workflow: selecting a step, the properties panel, the palette,
// undo and redo, the YAML editor, saving, read-only mode, and the layout sidecar.
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import type { WorkspaceDto } from '../src/api.ts';
import { textOf, useEditor } from '../src/edit/editorStore.ts';
import { stepsOf } from '../src/edit/steps.ts';
import { valueAt } from '../src/edit/yamlEdit.ts';
import { fakeApi, fakeCore, renderApp } from './fixtures.tsx';

const root = join(process.cwd(), '..', '..', 'tests', 'workflow-golden');
const path = 'orders/workflows/order-fulfilment.sfflow.yaml';
const yaml = readFileSync(join(root, 'cases', '01-order-fulfilment', 'workflow.sfflow.yaml'), 'utf8').replaceAll('\r\n', '\n');

function commerce(readOnly = false): WorkspaceDto {
  return {
    domain: 'commerce',
    file: 'sourceflow.domain.yaml',
    readOnly,
    services: ['orders', 'inventory', 'payments', 'shipping'].map((name) => ({
      name,
      manifest: `${name}.manifest.json`,
      manifestJson: readFileSync(join(root, 'domain', `${name}.manifest.json`), 'utf8'),
      ...(name === 'orders' ? { workflows: 'orders/workflows' } : {}),
      workflowFiles: name === 'orders' ? [path] : [],
    })),
  };
}

async function open(options: { readOnly?: boolean; layout?: string } = {}) {
  const files: Record<string, string> = { [path]: yaml };
  if (options.layout !== undefined) {
    files['orders/workflows/order-fulfilment.sfflow.layout.json'] = options.layout;
  }

  const api = fakeApi(commerce(options.readOnly), files);
  const core = fakeCore([]);
  const view = renderApp('/workflows/' + path, api, core);
  await waitFor(() => expect(useEditor.getState().path).toBe(path));
  await waitFor(() => expect(view.container.querySelector('[data-ready="true"]')).not.toBeNull(), { timeout: 10_000 });
  return { ...view, api, core };
}

const text = () => textOf(useEditor.getState());

async function selectStep(container: HTMLElement, id: string) {
  fireEvent.click(container.querySelector(`.react-flow__node[data-id="${id}"]`) as HTMLElement);
  return screen.findByRole('form', { name: `Properties of ${id}` });
}

describe('STU-CT-19 selecting a step', () => {
  it('opens its properties from the statechart, and clears on the pane', async () => {
    const { container } = await open();
    expect(screen.getByText('Select a step in the statechart to edit it.')).toBeTruthy();
    const form = await selectStep(container, 'charge-payment');
    expect((within(form).getByRole('textbox', { name: 'Id' }) as HTMLInputElement).value).toBe('charge-payment');
    expect((within(form).getByRole('combobox', { name: 'Command' }) as HTMLSelectElement).value).toBe('payments.charge.v1');
    expect((within(form).getByRole('combobox', { name: 'Success event' }) as HTMLSelectElement).value).toBe('payments.payment-captured.v1');
    expect(within(form).getByRole('list', { name: 'Failures' }).textContent).toContain('✕ rejection payments.declined');
    expect(useEditor.getState().selected).toBe('charge-payment');
    expect(container.querySelector('.react-flow__node[data-id="charge-payment"]')?.getAttribute('aria-label')).toMatch(/press Enter to edit$/);
  });
});

describe('STU-CT-20 the command and the map', () => {
  it('edits the text with each change, minimally', async () => {
    const { container } = await open();
    const form = await selectStep(container, 'reserve-stock');
    await userEvent.selectOptions(within(form).getByRole('combobox', { name: 'Command' }), 'inventory.release-stock.v1');
    expect(valueAt(text(), ['steps', 0, 'send', 'command'])).toBe('inventory.release-stock.v1');
    expect(text().replace('inventory.release-stock.v1, map: { orderId', 'inventory.reserve-stock.v1, map: { orderId')).toBe(yaml);

    const lines = within(form).getByRole('textbox', { name: 'Map lines' });
    await userEvent.clear(lines);
    await userEvent.type(lines, '$data.items{Enter}');
    expect(valueAt(text(), ['steps', 0, 'send', 'map', 'lines'])).toBe('$data.items');
    await userEvent.click(within(form).getByRole('button', { name: 'Remove lines from the map' }));
    expect(valueAt(text(), ['steps', 0, 'send', 'map'])).toEqual({ orderId: '$data.orderId' });
  });
});

describe('STU-CT-21 renaming', () => {
  it('renames a step and its references, and explains a bad id', async () => {
    const { container } = await open();
    const form = await selectStep(container, 'await-address-fix');
    const id = within(form).getByRole('textbox', { name: 'Id' });
    await userEvent.clear(id);
    await userEvent.type(id, 'fix-address{Enter}');
    expect(stepsOf(text()).map((s) => s.id)).toContain('fix-address');
    expect(valueAt(text(), ['steps', 3, 'await', 'failure', 0, 'then'])).toEqual({ goto: 'fix-address' });

    const renamed = await screen.findByRole('form', { name: 'Properties of fix-address' });
    const again = within(renamed).getByRole('textbox', { name: 'Id' });
    await userEvent.clear(again);
    await userEvent.type(again, 'Bad Id{Enter}');
    expect((await screen.findByRole('alert')).textContent).toContain('is not a step id');
  });
});

describe('STU-CT-22 failures and retries', () => {
  it('adds a failure from the codes the command can reject, and sets its retry and next step', async () => {
    const { container } = await open();
    const form = await selectStep(container, 'charge-payment');
    expect(within(form).queryByRole('combobox', { name: 'Add a failure' })).toBeNull(); // every code the command rejects is handled
    await userEvent.click(within(form).getByRole('button', { name: 'Remove the failure on rejection payments.fraud_suspected' }));
    expect(stepsOf(text())[1]!.failures).toHaveLength(1);
    const add = within(await screen.findByRole('form', { name: 'Properties of charge-payment' })).getByRole('combobox', { name: 'Add a failure' }) as HTMLSelectElement;
    expect([...add.options].map((o) => o.value)).toEqual(['', 'rejected:payments.fraud_suspected']);
    const code = 'rejected:payments.fraud_suspected';
    await userEvent.selectOptions(add, code);
    const failures = stepsOf(text())[1]!.failures;
    expect(failures).toHaveLength(2);
    expect(failures[1]).toEqual({ rejected: code.slice(9), then: 'fail' });

    const fresh = await screen.findByRole('form', { name: 'Properties of charge-payment' });
    const name = `rejection ${code.slice(9)}`;
    const retries = within(fresh).getByRole('textbox', { name: `Retry (${name}): retries` });
    await userEvent.clear(retries);
    await userEvent.type(retries, '2{Enter}');
    expect(stepsOf(text())[1]!.failures[1]!.retry).toEqual({ max: 2 });
    await userEvent.selectOptions(within(fresh).getByRole('combobox', { name: `Then (${name})` }), 'goto:review-large-order');
    expect(stepsOf(text())[1]!.failures[1]!.then).toEqual({ goto: 'review-large-order' });
  });
});

describe('STU-CT-23 pivot, guard, timeout and compensation', () => {
  it('toggle their keys', async () => {
    const { container } = await open();
    const form = await selectStep(container, 'review-large-order');
    await userEvent.click(within(form).getByRole('checkbox', { name: /Pivot/ }));
    expect(stepsOf(text())[2]!.pivot).toBe(true);
    const guard = within(form).getByRole('textbox', { name: 'Guard (when)' });
    await userEvent.clear(guard);
    await userEvent.type(guard, '{Enter}');
    expect(stepsOf(text())[2]!.when).toBeUndefined();
    const timeout = within(form).getByRole('textbox', { name: 'Timeout after' });
    await userEvent.clear(timeout);
    await userEvent.type(timeout, 'PT2H{Enter}');
    expect(stepsOf(text())[2]!.timeout?.after).toBe('PT2H');
    await userEvent.selectOptions(within(form).getByRole('combobox', { name: 'Undo command' }), 'orders.cancel-order.v1');
    expect(stepsOf(text())[2]!.compensate?.sends).toBe('orders.cancel-order.v1');
    expect(text()).not.toContain('# CESQL guard'); // the guard's own end-of-line comment goes with it
    for (const comment of ['# point of no return', '# business retry', '# forward recovery after pivot']) {
      expect(text()).toContain(comment);
    }
  });
});

describe('STU-CT-24 the palette', () => {
  it('offers the domain commands by service and adds a step after the selection', async () => {
    const { container } = await open();
    await selectStep(container, 'charge-payment');
    const palette = screen.getByRole('region', { name: 'Palette' });
    expect(palette.textContent).toContain('After charge-payment.');
    const command = within(palette).getByRole('combobox', { name: 'Command' }) as HTMLSelectElement;
    expect([...command.querySelectorAll('optgroup')].map((g) => g.getAttribute('label'))).toEqual(['orders', 'inventory', 'payments', 'shipping']);
    await userEvent.selectOptions(command, 'payments.refund.v1');
    const waits = within(palette).getByRole('combobox', { name: 'Waits for' }) as HTMLSelectElement;
    expect([...waits.options].map((o) => o.value)).toEqual(['payments.refunded.v1']);
    await userEvent.click(within(palette).getByRole('button', { name: 'Add step' }));
    expect(stepsOf(text()).map((s) => s.id).slice(1, 3)).toEqual(['charge-payment', 'refund']);
    expect(useEditor.getState().selected).toBe('refund');
  });
});

describe('STU-CT-25 undo and redo', () => {
  it('undo visual edits from the toolbar and the keyboard, and show unsaved changes', async () => {
    const { container } = await open();
    expect(screen.getByRole('button', { name: 'Undo' }).hasAttribute('disabled')).toBe(true);
    expect(within(screen.getByRole('toolbar', { name: 'Edit' })).getByRole('status').textContent).toBe('No changes');
    const form = await selectStep(container, 'reserve-stock');
    await userEvent.click(within(form).getByRole('checkbox', { name: /Pivot/ }));
    expect(within(screen.getByRole('toolbar', { name: 'Edit' })).getByRole('status').textContent).toBe('Unsaved changes');
    await userEvent.click(screen.getByRole('button', { name: 'Undo' }));
    expect(text()).toBe(yaml);
    await userEvent.click(screen.getByRole('button', { name: 'Redo' }));
    expect(stepsOf(text())[0]!.pivot).toBe(true);
    fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
    expect(text()).toBe(yaml);
    fireEvent.keyDown(window, { key: 'y', ctrlKey: true });
    expect(stepsOf(text())[0]!.pivot).toBe(true);
  });
});

describe('STU-CT-26 saving and read-only mode', () => {
  it('saves the text (Ctrl+S too), and a read-only Studio edits nothing', async () => {
    const { container, api } = await open();
    const form = await selectStep(container, 'reserve-stock');
    await userEvent.click(within(form).getByRole('checkbox', { name: /Pivot/ }));
    fireEvent.keyDown(window, { key: 's', ctrlKey: true });
    await waitFor(() => expect(api.writeFile).toHaveBeenCalledWith(path, text()));
    expect(await within(screen.getByRole('toolbar', { name: 'Edit' })).findByText('Saved.')).toBeTruthy();
    expect(api.writeFile).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: 'Save' }).hasAttribute('disabled')).toBe(true);
    // Undoing past the save is unsaved again.
    await userEvent.click(screen.getByRole('button', { name: 'Undo' }));
    expect(within(screen.getByRole('toolbar', { name: 'Edit' })).getByRole('status').textContent).toBe('Unsaved changes');
    expect(screen.getByRole('button', { name: 'Save' }).hasAttribute('disabled')).toBe(false);
  });

  it('disables editing when read-only', async () => {
    const { container } = await open({ readOnly: true });
    const form = await selectStep(container, 'reserve-stock');
    expect((within(form).getByRole('combobox', { name: 'Command' }) as HTMLSelectElement).disabled).toBe(true);
    expect((within(form).getByRole('textbox', { name: 'Id' }) as HTMLInputElement).readOnly).toBe(true);
    expect((within(screen.getByRole('region', { name: 'Palette' })).getByRole('button', { name: 'Add step' }) as HTMLButtonElement).disabled).toBe(true);
    await userEvent.click(screen.getByRole('tab', { name: 'YAML' }));
    expect((await screen.findByRole('textbox', { name: 'YAML of order-fulfilment.sfflow.yaml' }) as HTMLTextAreaElement).readOnly).toBe(true);
    expect(within(screen.getByRole('toolbar', { name: 'Edit' })).getByRole('status').textContent).toBe('Read-only');
  });
});

describe('STU-CT-27 the YAML editor', () => {
  it('is the same text: typing re-validates and joins the history', async () => {
    const { core } = await open();
    await userEvent.click(screen.getByRole('tab', { name: 'YAML' }));
    const editor = (await screen.findByRole('textbox', { name: 'YAML of order-fulfilment.sfflow.yaml' })) as HTMLTextAreaElement;
    expect(editor.value).toBe(yaml);
    fireEvent.change(editor, { target: { value: yaml.replace('version: 1', 'version: 2') } });
    expect(valueAt(text(), ['version'])).toBe(2);
    await waitFor(() => expect(core.validate).toHaveBeenLastCalledWith(expect.objectContaining({ yaml: text() })));
    await userEvent.click(screen.getByRole('button', { name: 'Undo' }));
    expect(editor.value).toBe(yaml);
  });
});

describe('STU-CT-28 the layout sidecar', () => {
  it('applies saved positions, and saves moved nodes beside the workflow', async () => {
    const { api, container } = await open({ layout: '{"version":1,"nodes":{"reserve-stock":{"x":777,"y":555}}}' });
    expect(useEditor.getState().layout.nodes['reserve-stock']).toEqual({ x: 777, y: 555 });
    await waitFor(() => expect((container.querySelector('.react-flow__node[data-id="reserve-stock"]') as HTMLElement).style.transform).toContain('777px'));
    act(() => {
      useEditor.getState().move('charge-payment', 10.6, 20.2);
      useEditor.getState().move('nowhere', 1, 1);
    });
    expect(within(screen.getByRole('toolbar', { name: 'Edit' })).getByRole('status').textContent).toBe('Unsaved changes');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(api.writeFile).toHaveBeenCalledWith('orders/workflows/order-fulfilment.sfflow.layout.json', expect.stringContaining('"charge-payment"')));
    const layout = JSON.parse((api.writeFile.mock.calls as unknown as Array<[string, string]>).find((c) => c[0].endsWith('.layout.json'))![1]) as { nodes: Record<string, unknown> };
    expect(layout.nodes).toEqual({ 'charge-payment': { x: 11, y: 20 }, 'reserve-stock': { x: 777, y: 555 } });
    expect(api.writeFile).not.toHaveBeenCalledWith(path, expect.anything());
  });
});
