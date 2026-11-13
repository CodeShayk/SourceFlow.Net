#!/usr/bin/env node
// npx @sourceflow/studio build: a read-only static site of a domain workspace (doc 15 §6.3), for docs portals and
// static hosts. It writes the built Studio app, the WebAssembly core and a snapshot of the workspace (its manifests,
// workflows and layouts); the app opens it read-only, with hash routes, from any folder of any static host.
import { copyFileSync, cpSync, existsSync, mkdirSync, readdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadWorkspace, WorkspaceError } from './workspace.mjs';

const MARKER = '.sourceflow-site';
const here = dirname(fileURLToPath(import.meta.url));
const pkg = resolve(here, '..');

const USAGE = `Usage: npx @sourceflow/studio build [options]

Builds a read-only static site of a domain workspace.

Options:
  --workspace <path>   sourceflow.domain.yaml, or the folder holding it (default: .)
  --out <dir>          where to write the site (default: site)
  --assets <dir>       the built Studio app (default: the package's dist)
  --framework <dir>    the WebAssembly core's _framework folder (default: the package's framework)
  --help               show this help`;

export function parseArgs(argv) {
  const [command, ...rest] = argv[0]?.startsWith('--') ? [undefined, ...argv] : argv;
  const options = { command, workspace: '.', out: 'site', assets: undefined, framework: undefined };
  for (let i = 0; i < rest.length; i++) {
    const arg = rest[i];
    const value = () => {
      const next = rest[++i];
      if (next === undefined || next.startsWith('--')) {
        throw new WorkspaceError(`${arg} needs a value.`);
      }

      return next;
    };
    switch (arg) {
      case '--workspace': options.workspace = value(); break;
      case '--out': options.out = value(); break;
      case '--assets': options.assets = value(); break;
      case '--framework': options.framework = value(); break;
      case '--help': options.command = 'help'; break;
      default: throw new WorkspaceError(`Unknown option '${arg}'.\n\n${USAGE}`);
    }
  }

  return options;
}

function firstExisting(...candidates) {
  return candidates.find((c) => c !== undefined && existsSync(c));
}

/** The core files the site needs: dotnet.js and every file it names (a publish folder can hold stale fingerprinted copies). */
export function frameworkFiles(framework) {
  const loader = readFileSync(join(framework, 'dotnet.js'), 'utf8');
  return readdirSync(framework).filter((name) => name === 'dotnet.js' || (!/\.(br|gz)$/.test(name) && loader.includes(name)));
}

/** index.html for a static site: assets from its own folder, and the mark that switches the app to static mode. */
export function staticIndex(html) {
  const mark = '<meta name="sourceflow-mode" content="static" />';
  const marked = html
    .replace(/\s*<base href="\/"\s*\/?>/, '') // the local host's root; a static site is wherever it is served
    .replace(/(src|href)="\/(assets\/)/g, '$1="./$2');
  return /<meta charset="[^"]*"\s*\/?>/i.test(marked)
    ? marked.replace(/(<meta charset="[^"]*"\s*\/?>)/i, `$1\n    ${mark}`)
    : marked.replace('<head>', `<head>\n    ${mark}`);
}

/** Builds the site; returns what it wrote. */
export function build(options, log = () => {}) {
  const assets = resolve(options.assets ?? firstExisting(join(pkg, 'dist')) ?? join(pkg, 'dist'));
  const framework = resolve(options.framework ?? firstExisting(join(pkg, 'framework'), join(pkg, '..', '..', 'artifacts', 'studio-wasm', 'wwwroot', '_framework')) ?? join(pkg, 'framework'));
  if (!existsSync(join(assets, 'index.html'))) {
    throw new WorkspaceError(`No built Studio app in ${assets}: run 'npm run build' in studio/, or pass --assets.`);
  }

  if (!existsSync(join(framework, 'dotnet.js'))) {
    throw new WorkspaceError(`No WebAssembly core in ${framework}: run 'npm run build:wasm' in studio/, or pass --framework.`);
  }

  const { workspace, files } = loadWorkspace(options.workspace);
  const out = resolve(options.out);
  if (existsSync(out) && readdirSync(out).length > 0 && !existsSync(join(out, MARKER))) {
    throw new WorkspaceError(`${out} isn't empty and isn't a Studio site: choose another --out, or empty it first.`);
  }

  rmSync(out, { recursive: true, force: true });
  mkdirSync(out, { recursive: true });
  cpSync(assets, out, { recursive: true });
  writeFileSync(join(out, 'index.html'), staticIndex(readFileSync(join(assets, 'index.html'), 'utf8')));
  mkdirSync(join(out, '_framework'));
  const core = frameworkFiles(framework);
  for (const name of core) {
    copyFileSync(join(framework, name), join(out, '_framework', name));
  }

  mkdirSync(join(out, 'data'));
  writeFileSync(join(out, 'data', 'workspace.json'), JSON.stringify(workspace, null, 2) + '\n');
  writeFileSync(join(out, 'data', 'files.json'), JSON.stringify(files, null, 2) + '\n');
  writeFileSync(join(out, '.nojekyll'), ''); // GitHub Pages would drop _framework otherwise
  writeFileSync(join(out, MARKER), 'Written by npx @sourceflow/studio build; the next build replaces this folder.\n');
  const workflows = workspace.services.reduce((n, s) => n + s.workflowFiles.length, 0);
  log(`SourceFlow Studio site for '${workspace.domain}': ${workspace.services.length} services, ${workflows} workflows -> ${out}`);
  return { out, workspace, files, core };
}

export function main(argv, log = console.log, error = console.error) {
  try {
    const options = parseArgs(argv);
    if (options.command === 'help' || options.command === undefined) {
      log(USAGE);
      return options.command === 'help' ? 0 : 2;
    }

    if (options.command !== 'build') {
      error(`Unknown command '${options.command}'.\n\n${USAGE}`);
      return 2;
    }

    build(options, log);
    return 0;
  } catch (failure) {
    if (failure instanceof WorkspaceError) {
      error(failure.message);
      return 1;
    }

    throw failure;
  }
}

// Run when started as a program (npx links the package, so compare real paths), not when imported by the tests.
function isEntry() {
  if (process.argv[1] === undefined) {
    return false;
  }

  try {
    const same = (a, b) => (process.platform === 'win32' ? a.toLowerCase() === b.toLowerCase() : a === b);
    return same(realpathSync(resolve(process.argv[1])), realpathSync(fileURLToPath(import.meta.url)));
  } catch {
    return false;
  }
}

if (isEntry()) {
  process.exitCode = main(process.argv.slice(2));
}
