// Copies the WebAssembly core into dist/framework for the bundled action: dotnet.js and the files it names (a publish
// folder can hold stale fingerprinted copies). The source is SOURCEFLOW_STUDIO_WASM or the repository's artifacts.
import { copyFileSync, existsSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { frameworkFiles } from '../../app/bin/studio.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const source = resolve(process.env.SOURCEFLOW_STUDIO_WASM ?? join(here, '..', '..', '..', 'artifacts', 'studio-wasm', 'wwwroot', '_framework'));
if (!existsSync(join(source, 'dotnet.js'))) {
  console.error(`No WebAssembly core in ${source}: run 'npm run build:wasm' in studio/ first.`);
  process.exit(1);
}

const target = join(here, '..', 'dist', 'framework');
mkdirSync(target, { recursive: true });
const files = frameworkFiles(source);
for (const name of files) {
  copyFileSync(join(source, name), join(target, name));
}

console.log(`Bundled the core (${files.length} files) into ${target}`);
