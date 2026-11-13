// STU-CT-01..05: the explorer, the diagnostics panel, the shell (routing, theme, keyboard map) and the service view.
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';
import { DiagnosticsPanel } from '../src/components/DiagnosticsPanel.tsx';
import { Explorer } from '../src/components/Explorer.tsx';
import { useStudio } from '../src/store.ts';
import { diagnostics, fakeApi, fakeCore, renderApp, workspace } from './fixtures.tsx';

describe('STU-CT-01 explorer', () => {
  it('shows the services and their workflows as named links, the open one marked', () => {
    render(
      <MemoryRouter initialEntries={['/workflows/orders/workflows/order-fulfilment.sfflow.yaml']}>
        <Explorer workspace={workspace()} />
      </MemoryRouter>,
    );

    const nav = screen.getByRole('navigation', { name: 'Workspace' });
    expect(within(nav).getByRole('link', { name: 'Service orders' }).getAttribute('href')).toBe('/services/orders');
    expect(within(nav).getByRole('link', { name: 'Service inventory' })).toBeTruthy();
    const workflows = within(nav).getByRole('list', { name: 'Workflows of orders' });
    expect(within(workflows).getAllByRole('link').map((a) => a.getAttribute('aria-label'))).toEqual(['Workflow order-fulfilment', 'Workflow returns']);
    expect(within(nav).getByRole('link', { name: 'Workflow order-fulfilment' }).getAttribute('aria-current')).toBe('page');
    expect(within(nav).queryByRole('list', { name: 'Workflows of inventory' })).toBeNull();
  });
});

describe('STU-CT-02 diagnostics panel', () => {
  it('lists errors first with the severity in words, and selects one on click', async () => {
    render(<DiagnosticsPanel diagnostics={diagnostics} />);
    const panel = screen.getByRole('region', { name: 'Diagnostics' });
    expect(panel.textContent).toContain('1 error, 1 warning');
    const items = within(panel).getAllByRole('button');
    expect(items[0]?.textContent).toMatch(/^✕ Error WF-V01 line 4, column 8: 'orders' is not a known service\.$/);
    expect(items[1]?.textContent).toContain('Warning WF-V07');

    await userEvent.click(items[1]!);
    expect(items[1]?.getAttribute('aria-current')).toBe('true');
    expect(useStudio.getState().selectedDiagnostic).toBe(1);
  });

  it('says so when there is nothing, or nothing yet', () => {
    const { rerender } = render(<DiagnosticsPanel diagnostics={[]} />);
    expect(screen.getByRole('status').textContent).toBe('No problems.');
    rerender(<DiagnosticsPanel diagnostics={undefined} status="Validating…" />);
    expect(screen.getByRole('status').textContent).toBe('Validating…');
  });
});

describe('STU-CT-03 shell and routing', () => {
  it('opens on the domain overview and opens a workflow with its diagnostics from the core', async () => {
    const { core } = renderApp('/');
    expect(await screen.findByRole('heading', { level: 1, name: 'commerce' })).toBeTruthy();
    expect(screen.getByRole('table', { name: 'Services in sourceflow.domain.yaml' })).toBeTruthy();
    expect(screen.getByRole('status').textContent).toBe('Open a workflow to see its diagnostics.');

    await userEvent.click(screen.getByRole('link', { name: 'Workflow order-fulfilment' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'order-fulfilment.sfflow.yaml' })).toBeTruthy();
    const source = await screen.findByRole('list', { name: 'Source of order-fulfilment.sfflow.yaml' });
    expect(within(source).getAllByRole('listitem')).toHaveLength(7);
    await screen.findByText(/1 error, 1 warning/);
    expect(core.validate).toHaveBeenCalledWith(expect.objectContaining({ yaml: expect.stringContaining('order-fulfilment'), manifests: expect.any(Array) }));

    await userEvent.click(screen.getAllByRole('button', { name: /Error WF-V01/ })[0]!);
    expect(within(source).getAllByRole('listitem')[3]?.getAttribute('aria-current')).toBe('location');
  });

  it('shows not-found pages and a workspace that cannot load', async () => {
    renderApp('/workflows/orders/workflows/missing.sfflow.yaml');
    expect(await screen.findByText(/There is no workflow in this workspace/)).toBeTruthy();

    const broken = fakeApi();
    broken.workspace.mockRejectedValue(new Error('Workspace file not found.'));
    renderApp('/', broken);
    expect((await screen.findByRole('alert')).textContent).toContain('Workspace file not found.');
  });
});

describe('STU-CT-04 theme and keyboard map', () => {
  it('switches the theme, and opens the keyboard map with Shift+?', async () => {
    renderApp('/');
    await screen.findByRole('heading', { level: 1, name: 'commerce' });
    expect(document.documentElement.dataset.theme).toBe('light');

    await userEvent.click(screen.getByRole('radio', { name: 'Dark' }));
    expect(document.documentElement.dataset.theme).toBe('dark');
    fireEvent.keyDown(window, { key: 't', altKey: true });
    await waitFor(() => expect(document.documentElement.dataset.theme).toBe('light'));

    fireEvent.keyDown(window, { key: '?', shiftKey: true });
    const dialog = await screen.findByRole('dialog', { name: 'Keyboard map' });
    expect(within(dialog).getByText('Show the keyboard map')).toBeTruthy();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('moves through the diagnostics with F8', async () => {
    renderApp('/workflows/orders/workflows/order-fulfilment.sfflow.yaml');
    await screen.findByText(/1 error, 1 warning/);
    fireEvent.keyDown(window, { key: 'F8' });
    await waitFor(() => expect(screen.getAllByRole('button', { name: /Error WF-V01/ })[0]?.getAttribute('aria-current')).toBe('true'));
    fireEvent.keyDown(window, { key: 'F8', shiftKey: true });
    await waitFor(() => expect(screen.getByRole('button', { name: /Warning WF-V07/ }).getAttribute('aria-current')).toBe('true'));
  });
});

describe('STU-CT-05 service view and read-only mode', () => {
  it("exports a service's manifest when it has a project", async () => {
    const { api } = renderApp('/services/orders');
    const button = await screen.findByRole('button', { name: 'Export the manifest again' });
    expect(screen.getByText(/orders\.place-order, orders\.cancel-order/)).toBeTruthy();
    await userEvent.click(button);
    expect(await screen.findByText('The manifest was exported.')).toBeTruthy();
    expect(api.exportManifest).toHaveBeenCalledWith('orders');
  });

  it('marks the Studio read-only and disables writes', async () => {
    const api = fakeApi(workspace({ readOnly: true }));
    renderApp('/services/orders', api, fakeCore([]));
    expect(await screen.findByRole('note')).toBeTruthy();
    expect(screen.getByRole('note').textContent).toBe('Read-only');
    expect((screen.getByRole('button', { name: 'Export the manifest again' }) as HTMLButtonElement).disabled).toBe(true);

    renderApp('/services/inventory');
    expect(((await screen.findAllByRole('button', { name: 'Export the manifest again' })).at(-1) as HTMLButtonElement).disabled).toBe(true);
    renderApp('/services/nobody');
    expect(await screen.findByText(/no service 'nobody'/)).toBeTruthy();
  });
});
