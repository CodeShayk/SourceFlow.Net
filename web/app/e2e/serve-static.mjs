// A plain static file server for STU-E2E-15: it serves a folder under a sub-path, with no fallback to index.html (like
// GitHub Pages or a docs portal), so the static site must work with relative URLs and hash routes.
//   node e2e/serve-static.mjs <dir> <port> [/prefix/]
import { createReadStream, existsSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize, resolve, sep } from 'node:path';

const [, , dir, port = '5195', prefix = '/'] = process.argv;
const root = resolve(dir);
const TYPES = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.json': 'application/json',
  '.wasm': 'application/wasm', '.svg': 'image/svg+xml', '.ttf': 'font/ttf', '.dat': 'application/octet-stream', '.png': 'image/png',
};

createServer((request, response) => {
  const path = decodeURIComponent(new URL(request.url ?? '/', 'http://localhost').pathname);
  if (!path.startsWith(prefix)) {
    response.writeHead(404).end('Not found');
    return;
  }

  const relative = normalize(path.slice(prefix.length) || 'index.html');
  const file = join(root, relative.endsWith(sep) || relative === '.' ? join(relative, 'index.html') : relative);
  if (!file.startsWith(root) || !existsSync(file) || !statSync(file).isFile()) {
    response.writeHead(404).end('Not found');
    return;
  }

  response.writeHead(200, { 'Content-Type': TYPES[extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-cache' });
  createReadStream(file).pipe(response);
}).listen(Number(port), '127.0.0.1');
