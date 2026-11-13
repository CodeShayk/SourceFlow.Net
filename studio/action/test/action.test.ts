// The workflow-diff action against a fixture repository (plan 10 WP-S.12): a git repository whose pull request adds,
// removes, changes and breaks workflows; the real core (WebAssembly in Node) and ELK; and a fake GitHub API that keeps
// the comments, so the action's comment is created once and then updated.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { createServer, type Server } from 'node:http';
import type { AddressInfo } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { comment, COMMENT_LIMIT, MARKER, type FileDiff } from '../src/diff.ts';
import { run } from '../src/main.ts';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const golden = (name: string) => readFileSync(join(repository, 'tests', 'workflow-golden', 'cases', name, 'workflow.sfflow.yaml'), 'utf8');
const frameworkDir = process.env.SOURCEFLOW_STUDIO_WASM ?? join(repository, 'artifacts', 'studio-wasm', 'wwwroot', '_framework');
const frameworkUrl = pathToFileURL(frameworkDir).href + '/';

interface Stored {
  id: number;
  body: string;
}

let root: string;
let repo: string;
let server: Server;
let api: string;
const comments: Stored[] = [];
const requests: Array<{ method: string; url: string; auth: string | undefined }> = [];

function git(...args: string[]): string {
  return execFileSync('git', ['-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', '-c', 'commit.gpgsign=false', ...args], { cwd: repo, encoding: 'utf8' }).trim();
}

function write(path: string, text: string) {
  mkdirSync(dirname(join(repo, path)), { recursive: true });
  writeFileSync(join(repo, path), text);
}

function event(base: string, head: string, number = 7): string {
  const file = join(root, `event-${number}-${head}.json`);
  writeFileSync(file, JSON.stringify({ number, pull_request: { number, base: { sha: base }, head: { sha: head } } }));
  return file;
}

function env(eventPath: string, extra: Record<string, string> = {}) {
  return {
    GITHUB_EVENT_PATH: eventPath,
    GITHUB_WORKSPACE: repo,
    GITHUB_REPOSITORY: 'acme/shop',
    GITHUB_API_URL: api,
    GITHUB_SERVER_URL: 'https://github.example',
    GITHUB_RUN_ID: '42',
    GITHUB_OUTPUT: join(root, 'output.txt'),
    INPUT_TOKEN: 'secret-token',
    INPUT_OUT: 'diff',
    ...extra,
  };
}

let base: string;
let head: string;

beforeAll(async () => {
  root = mkdtempSync(join(tmpdir(), 'sourceflow-diff-'));
  repo = join(root, 'repo');
  mkdirSync(repo);
  git('init', '-q', '-b', 'main');
  write('README.md', '# shop\n');
  write('orders/workflows/order-fulfilment.sfflow.yaml', golden('01-order-fulfilment'));
  write('orders/workflows/minimal.sfflow.yaml', golden('02-minimal'));
  write('orders/workflows/retired.sfflow.yaml', golden('28-retries-and-backoff'));
  git('add', '.');
  git('commit', '-q', '-m', 'base');
  base = git('rev-parse', 'HEAD');

  // The pull request: a changed timeout and a timeout that now compensates, a comment-only edit, a new workflow, a
  // deleted one, and one that no longer parses.
  write('orders/workflows/order-fulfilment.sfflow.yaml', golden('01-order-fulfilment')
    .replace('timeout: { after: PT5M, retry: { max: 2 }, then: fail }', 'timeout: { after: PT10M, retry: { max: 2 }, then: fail }')
    .replace('timeout: { after: P1D, then: manual }', 'timeout: { after: P1D, then: fail }'));
  write('orders/workflows/minimal.sfflow.yaml', '# Approves orders.\n' + golden('02-minimal'));
  write('orders/workflows/returns.sfflow.yaml', golden('27-local-steps'));
  rmSync(join(repo, 'orders/workflows/retired.sfflow.yaml'));
  write('orders/workflows/broken.sfflow.yaml', golden('03-s01-invalid-yaml'));
  write('docs/notes.md', 'not a workflow\n');
  git('add', '-A');
  git('commit', '-q', '-m', 'change workflows');
  head = git('rev-parse', 'HEAD');

  server = createServer((request, response) => {
    let body = '';
    request.on('data', (chunk: Buffer) => (body += chunk.toString('utf8')));
    request.on('end', () => {
      requests.push({ method: request.method!, url: request.url!, auth: request.headers.authorization });
      const url = new URL(request.url!, 'http://localhost');
      if (request.method === 'GET' && url.pathname === '/repos/acme/shop/issues/7/comments') {
        response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify([{ id: 1, body: 'LGTM' }, ...comments]));
      } else if (request.method === 'GET' && url.pathname.startsWith('/repos/acme/shop/issues/')) {
        response.writeHead(200, { 'Content-Type': 'application/json' }).end('[]');
      } else if (request.method === 'POST' && url.pathname === '/repos/acme/shop/issues/7/comments') {
        comments.push({ id: 100 + comments.length, body: (JSON.parse(body) as { body: string }).body });
        response.writeHead(201, { 'Content-Type': 'application/json' }).end('{}');
      } else if (request.method === 'PATCH' && url.pathname.startsWith('/repos/acme/shop/issues/comments/')) {
        const stored = comments.find((c) => c.id === Number(url.pathname.split('/').at(-1)))!;
        stored.body = (JSON.parse(body) as { body: string }).body;
        response.writeHead(200, { 'Content-Type': 'application/json' }).end('{}');
      } else {
        response.writeHead(404).end('{}');
      }
    });
  });
  await new Promise<void>((done) => server.listen(0, '127.0.0.1', done));
  api = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});

afterAll(async () => {
  await new Promise<void>((done) => server.close(() => done()));
  rmSync(root, { recursive: true, force: true });
});

describe('the workflow-diff action', () => {
  it('draws before and after, marks the changes, and comments once on the pull request', async () => {
    const logs: string[] = [];
    const files = await run({ env: env(event(base, head)), log: (l) => logs.push(l), frameworkUrl });

    expect(files.map((f) => [f.path, f.status])).toEqual([
      ['orders/workflows/broken.sfflow.yaml', 'added'],
      ['orders/workflows/minimal.sfflow.yaml', 'modified'],
      ['orders/workflows/order-fulfilment.sfflow.yaml', 'modified'],
      ['orders/workflows/retired.sfflow.yaml', 'removed'],
      ['orders/workflows/returns.sfflow.yaml', 'added'],
    ]);
    const [broken, minimal, fulfilment, retired, returns] = files as [FileDiff, FileDiff, FileDiff, FileDiff, FileDiff];
    expect(broken.after?.error).toBeDefined();
    expect(minimal.unchanged).toBe(true);
    expect(fulfilment.changes).toContain('△ Changed step reserve-stock: timeout PT5M → PT10M');
    expect(fulfilment.changes.some((c) => c.startsWith('＋ Added timeout review-large-order → Compensating'))).toBe(true);
    expect(fulfilment.changes.some((c) => c.startsWith('− Removed timeout review-large-order → ManualIntervention'))).toBe(true);
    expect(retired.changes.every((c) => c.startsWith('− Removed'))).toBe(true);
    expect(returns.changes.every((c) => c.startsWith('＋ Added'))).toBe(true);

    // The SVGs, with the changes marked on each side.
    const out = join(repo, 'diff');
    expect(readdirSync(out).sort()).toEqual([
      'orders__workflows__minimal.after.svg', 'orders__workflows__minimal.before.svg',
      'orders__workflows__order-fulfilment.after.svg', 'orders__workflows__order-fulfilment.before.svg',
      'orders__workflows__retired.before.svg', 'orders__workflows__returns.after.svg',
    ]);
    const after = readFileSync(join(out, 'orders__workflows__order-fulfilment.after.svg'), 'utf8');
    expect(after).toContain('<title id="sf-title">orders/workflows/order-fulfilment.sfflow.yaml after</title>');
    expect(after).toContain('△ changed');
    expect(after).toContain('＋ added');
    expect(readFileSync(join(out, 'orders__workflows__order-fulfilment.before.svg'), 'utf8')).toContain('− removed');
    expect(readFileSync(join(out, 'orders__workflows__minimal.after.svg'), 'utf8')).not.toMatch(/added|removed|changed/);

    // One comment, with the summary, the Mermaid diagrams and the way to the SVGs.
    expect(comments).toHaveLength(1);
    const body = comments[0]!.body;
    expect(body.startsWith(MARKER)).toBe(true);
    expect(body).toContain('5 workflow files changed.');
    expect(body).toContain('### `orders/workflows/order-fulfilment.sfflow.yaml` · modified');
    expect(body).toContain('- △ Changed step reserve-stock: timeout PT5M → PT10M');
    expect(body).toContain('No change to the statechart (comments or formatting only).');
    expect(body).toMatch(/> ✕ The statechart can't be drawn after: /);
    expect(body).toContain('```mermaid\n---\ntitle: order-fulfilment@1 (orders)');
    expect(body).toContain('    classDef changed fill:#fff8c5');
    expect(body).toContain('    class reserve_stock changed');
    expect(body).toContain('The before and after SVGs are in the `workflow-diff` artifact of [this run](https://github.example/acme/shop/actions/runs/42).');
    expect(requests.every((r) => r.auth === 'Bearer secret-token')).toBe(true);
    expect(readFileSync(join(root, 'output.txt'), 'utf8')).toContain('changed=5\n');
    expect(logs[0]).toBe(`5 workflow file(s) changed between ${base.slice(0, 7)} and ${head.slice(0, 7)}.`);

    // A new push updates the same comment.
    write('orders/workflows/returns.sfflow.yaml', golden('27-local-steps').replace(/^description: .*$/m, 'description: Returns.'));
    git('commit', '-q', '-am', 'tweak');
    const next = git('rev-parse', 'HEAD');
    await run({ env: env(event(base, next)), log: () => undefined, frameworkUrl });
    expect(comments).toHaveLength(1);
    expect(requests.filter((r) => r.method === 'PATCH')).toHaveLength(1);

    // A pull request that no longer changes workflows says so; one that never did gets no comment.
    await run({ env: env(event(base, base)), log: () => undefined, frameworkUrl });
    expect(comments[0]!.body).toContain('This pull request no longer changes any workflow.');
    const before = requests.length;
    await run({ env: env(event(base, base, 8)), log: () => undefined, frameworkUrl });
    expect(requests.slice(before).map((r) => r.method)).toEqual(['GET']);
    expect(comments).toHaveLength(1);

    // comment: false only writes the SVGs.
    const quiet = requests.length;
    rmSync(join(repo, 'diff'), { recursive: true });
    await run({ env: env(event(base, head), { INPUT_COMMENT: 'false', INPUT_THEME: 'dark' }), log: () => undefined, frameworkUrl });
    expect(requests.length).toBe(quiet);
    expect(readFileSync(join(out, 'orders__workflows__returns.after.svg'), 'utf8')).toContain('fill="#15181c"');
  });

  it('needs a pull request or the base and head', async () => {
    await expect(run({ env: { GITHUB_WORKSPACE: repo }, log: () => undefined, frameworkUrl })).rejects.toThrow('No base and head');
    const files = await run({ env: { GITHUB_WORKSPACE: repo, INPUT_BASE: base, INPUT_HEAD: head, INPUT_OUT: 'by-input' }, log: () => undefined, frameworkUrl });
    expect(files).toHaveLength(5);
    expect(existsSync(join(repo, 'by-input', 'orders__workflows__returns.after.svg'))).toBe(true);
  });

  it('leaves the diagrams out of a comment that would be too long', () => {
    const big: FileDiff = { path: 'a.sfflow.yaml', status: 'modified', changes: ['△ x'], unchanged: false, files: [], after: { mermaid: 'stateDiagram-v2\n' + 'x\n'.repeat(COMMENT_LIMIT) } };
    const body = comment([big], { artifact: 'workflow-diff' });
    expect(body.length).toBeLessThan(COMMENT_LIMIT);
    expect(body).toContain('(The diagrams are left out: the comment would be too long. See the SVGs.)');
    expect(body).not.toContain('```mermaid');
  });
});
