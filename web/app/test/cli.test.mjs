// npx @sourceflow/studio build (WP-S.12): the workspace snapshot, the static index, the core's files, the site folder's
// safety, and the static API the site's app reads it with.
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { build, frameworkFiles, main, parseArgs, staticIndex } from '../bin/studio.mjs';
import { loadWorkspace, WorkspaceError } from '../bin/workspace.mjs';
import { staticApi } from '../src/api.ts';

const workspaceDir = join(process.cwd(), 'e2e', 'workspace');
const temps = [];
const temp = () => {
  const dir = mkdtempSync(join(tmpdir(), 'sourceflow-site-'));
  temps.push(dir);
  return dir;
};

afterEach(() => {
  for (const dir of temps.splice(0)) {
    rmSync(dir, { recursive: true, force: true });
  }
});

function fixture() {
  const root = temp();
  const assets = join(root, 'dist');
  const framework = join(root, 'framework');
  mkdirSync(join(assets, 'assets'), { recursive: true });
  mkdirSync(framework);
  writeFileSync(join(assets, 'index.html'), '<!doctype html>\n<html>\n  <head>\n    <meta charset="UTF-8" />\n    <base href="/" />\n    <script type="module" src="./assets/app.js"></script>\n  </head>\n</html>\n');
  writeFileSync(join(assets, 'assets', 'app.js'), 'console.log(1)');
  writeFileSync(join(framework, 'dotnet.js'), 'load("dotnet.native.abc.wasm"); load("App.new.wasm");');
  for (const name of ['dotnet.native.abc.wasm', 'App.new.wasm', 'App.old.wasm', 'App.new.wasm.br', 'App.new.wasm.gz']) {
    writeFileSync(join(framework, name), name);
  }

  return { root, assets, framework };
}

describe('the workspace snapshot', () => {
  it('reads the services, their manifests and workflows like the .NET loader', () => {
    const { workspace, files } = loadWorkspace(workspaceDir);
    expect(workspace).toMatchObject({ domain: 'commerce', description: 'Order to delivery', file: 'sourceflow.domain.yaml', readOnly: true, staticSite: true });
    expect(workspace.services.map((s) => s.name)).toEqual(['orders', 'inventory', 'payments', 'shipping']);
    expect(workspace.services[0].workflowFiles).toEqual(['orders/workflows/large.sfflow.yaml', 'orders/workflows/order-fulfilment.sfflow.yaml', 'orders/workflows/unknown-type.sfflow.yaml']);
    expect(JSON.parse(workspace.services[1].manifestJson).service).toBe('inventory');
    expect(workspace.services[1].workflows).toBeUndefined();
    expect(Object.keys(files)).toEqual(workspace.services[0].workflowFiles);
    expect(files['orders/workflows/order-fulfilment.sfflow.yaml']).toContain('name: order-fulfilment');
  });

  it('reports what is wrong, with the line', () => {
    const root = temp();
    writeFileSync(join(root, 'p.json'), '{"service":"payments"}');
    const fails = (yaml) => {
      writeFileSync(join(root, 'sourceflow.domain.yaml'), yaml);
      return () => loadWorkspace(root);
    };
    expect(fails('domain: d\nservices:\n  - name: payments\n    manifest: p.json\n    colour: red\n')).toThrow(/\(5,5\): unknown key 'colour'/);
    expect(fails('domain: d\nowner: me\nservices: []\n')).toThrow(/\(2,1\): unknown key 'owner'/);
    expect(fails('domain: d\nservices: []\n')).toThrow("'services' lists no service");
    expect(fails('services:\n  - name: payments\n    manifest: p.json\n')).toThrow("'domain' is required");
    expect(fails('domain: d\nservices:\n  - name: payments\n    manifest: p.json\n  - name: payments\n    manifest: p.json\n')).toThrow("listed twice");
    expect(fails('domain: d\nservices:\n  - name: payments\n    manifest: https://example.invalid/p.json\n')).toThrow('is remote');
    expect(fails('domain: d\nservices:\n  - name: payments\n    manifest: missing.json\n')).toThrow("manifest 'missing.json' not found");
    expect(fails('domain: d\nservices:\n  - name: billing\n    manifest: p.json\n')).toThrow("is the manifest of service 'payments'");
    expect(fails('domain: [d\n')).toThrow(/sourceflow\.domain\.yaml\(\d+,\d+\)/);
    expect(() => loadWorkspace(join(root, 'nowhere'))).toThrow(WorkspaceError);
  });
});

describe('the site', () => {
  it('marks the index static and drops the host root, and copies only the core files dotnet.js loads', () => {
    const index = staticIndex('<head>\n    <meta charset="UTF-8" />\n    <base href="/" />\n    <link href="/assets/a.css">\n</head>');
    expect(index).toBe('<head>\n    <meta charset="UTF-8" />\n    <meta name="sourceflow-mode" content="static" />\n    <link href="./assets/a.css">\n</head>');
    const { framework } = fixture();
    expect(frameworkFiles(framework).sort()).toEqual(['App.new.wasm', 'dotnet.js', 'dotnet.native.abc.wasm']);
  });

  it('writes the app, the core and the snapshot, and only ever replaces a site', () => {
    const { root, assets, framework } = fixture();
    const out = join(root, 'site');
    const logs = [];
    build({ workspace: workspaceDir, out, assets, framework }, (line) => logs.push(line));
    expect(readdirSync(out).sort()).toEqual(['.nojekyll', '.sourceflow-site', '_framework', 'assets', 'data', 'index.html']);
    expect(readFileSync(join(out, 'index.html'), 'utf8')).toContain('<meta name="sourceflow-mode" content="static" />');
    expect(readFileSync(join(out, 'index.html'), 'utf8')).not.toContain('<base');
    expect(readdirSync(join(out, '_framework')).sort()).toEqual(['App.new.wasm', 'dotnet.js', 'dotnet.native.abc.wasm']);
    expect(JSON.parse(readFileSync(join(out, 'data', 'workspace.json'), 'utf8')).services).toHaveLength(4);
    expect(Object.keys(JSON.parse(readFileSync(join(out, 'data', 'files.json'), 'utf8')))).toHaveLength(3);
    expect(logs[0]).toMatch(/^SourceFlow Studio site for 'commerce': 4 services, 3 workflows -> /);

    writeFileSync(join(out, 'stale.txt'), 'old');
    build({ workspace: workspaceDir, out, assets, framework });
    expect(existsSync(join(out, 'stale.txt'))).toBe(false);

    const other = join(root, 'documents');
    mkdirSync(other);
    writeFileSync(join(other, 'keep.txt'), 'mine');
    expect(() => build({ workspace: workspaceDir, out: other, assets, framework })).toThrow("isn't empty and isn't a Studio site");
    expect(readFileSync(join(other, 'keep.txt'), 'utf8')).toBe('mine');
    expect(() => build({ workspace: workspaceDir, out, assets: join(root, 'none'), framework })).toThrow('No built Studio app');
    expect(() => build({ workspace: workspaceDir, out, assets, framework: join(root, 'none') })).toThrow('No WebAssembly core');
  });

  it('parses its command line', () => {
    expect(parseArgs(['build', '--workspace', 'w', '--out', 'o'])).toMatchObject({ command: 'build', workspace: 'w', out: 'o' });
    expect(parseArgs(['--help']).command).toBe('help');
    expect(() => parseArgs(['build', '--out'])).toThrow('--out needs a value.');
    const out = [];
    const err = [];
    expect(main(['--help'], (l) => out.push(l), (l) => err.push(l))).toBe(0);
    expect(out[0]).toMatch(/^Usage: npx @sourceflow\/studio build/);
    expect(main(['publish'], (l) => out.push(l), (l) => err.push(l))).toBe(2);
    expect(main(['build', '--workspace', join(temp(), 'none')], (l) => out.push(l), (l) => err.push(l))).toBe(1);
    expect(err.at(-1)).toMatch(/not found|No built Studio app/);
  });
});

describe('the static API', () => {
  it('reads the snapshot beside the page, read-only', async () => {
    const requests = [];
    const data = {
      'https://docs.example/studio/data/workspace.json': { domain: 'commerce', file: 'sourceflow.domain.yaml', readOnly: false, services: [] },
      'https://docs.example/studio/data/files.json': { 'orders/workflows/a.sfflow.yaml': 'name: a\n' },
    };
    const api = staticApi('https://docs.example/studio/', async (url) => {
      requests.push(String(url));
      const body = data[String(url)];
      return new Response(body === undefined ? 'missing' : JSON.stringify(body), { status: body === undefined ? 404 : 200 });
    });
    expect(await api.workspace()).toMatchObject({ domain: 'commerce', readOnly: true, staticSite: true });
    expect(await api.readFile('orders/workflows/a.sfflow.yaml')).toBe('name: a\n');
    await expect(api.readFile('orders/workflows/b.sfflow.yaml')).rejects.toThrow("No file 'orders/workflows/b.sfflow.yaml' in this static site.");
    expect(requests.filter((r) => r.endsWith('files.json'))).toHaveLength(1);
    await expect(api.writeFile('x', 'y')).rejects.toMatchObject({ status: 403 });
    await expect(api.specs('orders', { workflow: 'x' })).rejects.toThrow('read-only static site');
    await expect(staticApi('https://docs.example/elsewhere/', async () => new Response('', { status: 404 })).workspace()).rejects.toThrow('build it again');
  });
});
