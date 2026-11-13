// STU-UT-01..10: workspace mapping, routes, diagnostics order, the store, theming, the keyboard map and the API client.
import { describe, expect, it, vi } from 'vitest';
import { ApiError, hostApi } from '../src/api.ts';
import { commandFor, matches, SHORTCUTS } from '../src/keys.ts';
import { createStudioStore } from '../src/store.ts';
import { applyTheme, loadThemePreference, resolveTheme, saveThemePreference, THEME_KEY } from '../src/theme.ts';
import { countDiagnostics, explorerTree, manifestOf, manifests, ownerOf, sortDiagnostics, workflowPath, workflowRoute } from '../src/workspace.ts';
import { diagnostics, workspace } from './fixtures.tsx';

const key = (k: string, mods: Partial<Record<'altKey' | 'ctrlKey' | 'metaKey' | 'shiftKey', boolean>> = {}, extra: Record<string, unknown> = {}) =>
  ({ key: k, altKey: false, ctrlKey: false, metaKey: false, shiftKey: false, ...mods, ...extra });

describe('STU-UT-01 explorer tree', () => {
  it('lists services in workspace order with their workflows sorted and their command counts', () => {
    const tree = explorerTree(workspace());
    expect(tree.map((s) => s.name)).toEqual(['orders', 'inventory']);
    expect(tree[0]?.workflows.map((w) => w.name)).toEqual(['order-fulfilment', 'returns']);
    expect(tree[0]?.commands).toBe(2);
    expect(tree[0]?.route).toBe('/services/orders');
    expect(tree[1]?.workflows).toEqual([]);
  });
});

describe('STU-UT-02 workflow routes', () => {
  it('round-trips a workflow path and refuses anything else', () => {
    const route = workflowRoute('orders/workflows/order fulfilment.sfflow.yaml');
    expect(route).toBe('/workflows/orders/workflows/order%20fulfilment.sfflow.yaml');
    expect(workflowPath(route.slice('/workflows/'.length))).toBe('orders/workflows/order fulfilment.sfflow.yaml');
    expect(workflowPath('orders/notes.txt')).toBeUndefined();
    expect(workflowPath('../x.sfflow.yaml')).toBeUndefined();
    expect(workflowPath('')).toBeUndefined();
    expect(workflowPath(undefined)).toBeUndefined();
  });
});

describe('STU-UT-03 manifests and owners', () => {
  it('gives the core every manifest in order, finds a workflow owner, and tolerates bad manifest text', () => {
    const data = workspace();
    expect(manifests(data)).toHaveLength(2);
    expect(ownerOf(data, 'orders/workflows/returns.sfflow.yaml')?.name).toBe('orders');
    expect(ownerOf(data, 'nowhere.sfflow.yaml')).toBeUndefined();
    expect(manifestOf({ ...data.services[0]!, manifestJson: 'not json' })).toEqual({});
    expect(manifestOf({ ...data.services[0]!, manifestJson: '42' })).toEqual({});
  });
});

describe('STU-UT-04 diagnostics order', () => {
  it('puts errors first, then sorts by position, and counts both kinds', () => {
    const sorted = sortDiagnostics([...diagnostics, { id: 'WF-V02', severity: 'error', line: 2, column: 1, message: 'x' }]);
    expect(sorted.map((d) => d.id)).toEqual(['WF-V02', 'WF-V01', 'WF-V07']);
    expect(countDiagnostics(sorted)).toEqual({ errors: 2, warnings: 1 });
    expect(countDiagnostics([])).toEqual({ errors: 0, warnings: 0 });
  });
});

describe('STU-UT-05 store: theme', () => {
  it('sets and toggles the theme and remembers it', () => {
    const store = createStudioStore('system');
    store.getState().setTheme('dark');
    expect(store.getState().theme).toBe('dark');
    expect(window.localStorage.getItem(THEME_KEY)).toBe('dark');
    store.getState().toggleTheme('dark');
    expect(store.getState().theme).toBe('light');
    store.getState().toggleTheme('light');
    expect(store.getState().theme).toBe('dark');
  });
});

describe('STU-UT-06 store: diagnostic selection', () => {
  it('moves through the diagnostics with wrap-around and clears a selection that no longer exists', () => {
    const store = createStudioStore('system');
    store.getState().moveDiagnostic(1);
    expect(store.getState().selectedDiagnostic).toBe(-1);
    store.getState().setDiagnostics(3);
    store.getState().moveDiagnostic(-1);
    expect(store.getState().selectedDiagnostic).toBe(2);
    store.getState().moveDiagnostic(1);
    expect(store.getState().selectedDiagnostic).toBe(0);
    store.getState().selectDiagnostic(2);
    store.getState().setDiagnostics(2);
    expect(store.getState().selectedDiagnostic).toBe(-1);
    store.getState().selectDiagnostic(9);
    expect(store.getState().selectedDiagnostic).toBe(-1);
    store.getState().setKeyboardMapOpen(true);
    expect(store.getState().keyboardMapOpen).toBe(true);
  });
});

describe('STU-UT-07 theming', () => {
  it('resolves the system theme, survives unavailable storage, and applies to <html>', () => {
    expect(resolveTheme('system', true)).toBe('dark');
    expect(resolveTheme('system', false)).toBe('light');
    expect(resolveTheme('light', true)).toBe('light');
    expect(loadThemePreference({ getItem: () => 'purple' })).toBe('system');
    expect(loadThemePreference({ getItem: () => { throw new Error('blocked'); } })).toBe('system');
    expect(loadThemePreference({ getItem: () => 'dark' })).toBe('dark');
    expect(() => saveThemePreference('dark', { setItem: () => { throw new Error('quota'); } })).not.toThrow();
    applyTheme('dark');
    expect(document.documentElement.dataset.theme).toBe('dark');
  });
});

describe('STU-UT-08 shortcut matching', () => {
  it('needs the exact modifiers, ignores letter case, and reads the physical key with Alt', () => {
    expect(matches(key('1', { altKey: true }), 'Alt+1')).toBe(true);
    expect(matches(key('1', { altKey: true, ctrlKey: true }), 'Alt+1')).toBe(false);
    expect(matches(key('T', { altKey: true }), 'Alt+T')).toBe(true);
    expect(matches(key('t', { altKey: true }), 'Alt+T')).toBe(true);
    expect(matches(key('¡', { altKey: true }, { code: 'Digit1' }), 'Alt+1')).toBe(true);
    expect(matches(key('†', { altKey: true }, { code: 'KeyT' }), 'Alt+T')).toBe(true);
    expect(matches(key('F8'), 'F8')).toBe(true);
    expect(matches(key('F8', { shiftKey: true }), 'F8')).toBe(false);
  });
});

describe('STU-UT-09 commands', () => {
  it('maps keys to commands, but not plain keys while typing', () => {
    expect(commandFor(key('?', { shiftKey: true }))).toBe('keyboardMap');
    expect(commandFor(key('F8', { shiftKey: true }))).toBe('previousDiagnostic');
    expect(commandFor(key('3', { altKey: true }))).toBe('focusDiagnostics');
    expect(commandFor(key('x'))).toBeUndefined();
    const input = document.createElement('input');
    expect(commandFor({ ...key('?', { shiftKey: true }), target: input })).toBeUndefined();
    expect(commandFor({ ...key('F8'), target: input })).toBe('nextDiagnostic');
    expect(new Set(SHORTCUTS.map((s) => s.keys)).size).toBe(SHORTCUTS.length);
  });
});

describe('STU-UT-10 host API client', () => {
  it('calls the host routes and reports its errors', async () => {
    const calls: Array<{ url: string; init: RequestInit | undefined }> = [];
    const fetcher = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      calls.push({ url: String(input), init });
      if (String(input).includes('refused')) {
        return new Response(JSON.stringify({ error: 'The Studio host runs with --read-only.' }), { status: 403 });
      }

      if (String(input).includes('broken')) {
        return new Response('<html>', { status: 500, statusText: 'Server Error' });
      }

      return new Response(String(input).endsWith('/api/workspace') ? JSON.stringify(workspace()) : 'text');
    }) as unknown as typeof fetch;
    const api = hostApi('http://127.0.0.1:5180', fetcher);

    expect((await api.workspace()).domain).toBe('commerce');
    expect(await api.readFile('orders/workflows/a b.sfflow.yaml')).toBe('text');
    await api.writeFile('orders/workflows/a.sfflow.yaml', 'name: a');
    await api.exportManifest('orders');
    expect(calls.map((c) => [c.init?.method ?? 'GET', c.url])).toEqual([
      ['GET', 'http://127.0.0.1:5180/api/workspace'],
      ['GET', 'http://127.0.0.1:5180/api/files?path=orders%2Fworkflows%2Fa%20b.sfflow.yaml'],
      ['PUT', 'http://127.0.0.1:5180/api/files?path=orders%2Fworkflows%2Fa.sfflow.yaml'],
      ['POST', 'http://127.0.0.1:5180/api/services/orders/manifest/export'],
    ]);
    expect(calls[2]?.init?.body).toBe('{"content":"name: a"}');
    expect((calls[2]?.init?.headers as Record<string, string>)['Content-Type']).toBe('application/json');

    await expect(api.readFile('refused.sfflow.yaml')).rejects.toEqual(new ApiError(403, 'The Studio host runs with --read-only.'));
    await expect(api.readFile('broken.sfflow.yaml')).rejects.toMatchObject({ status: 500, message: '500 Server Error' });
  });
});
