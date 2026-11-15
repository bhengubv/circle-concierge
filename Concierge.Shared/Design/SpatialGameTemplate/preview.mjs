import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve, sep } from 'node:path';

const root = resolve(import.meta.dirname);
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.json': 'application/json', '.png': 'image/png', '.txt': 'text/plain' };
const ext = path => path.slice(path.lastIndexOf('.'));
const server = createServer(async (request, response) => {
  const path = resolve(root, '.' + decodeURIComponent(new URL(request.url, 'http://localhost').pathname));
  if (path !== root && !path.startsWith(root + sep)) { response.writeHead(403).end(); return; }
  const filePath = path === root ? resolve(root, 'index.html') : path;
  try {
    const file = await readFile(filePath);
    response.writeHead(200, { 'Content-Type': mime[ext(filePath)] || 'application/octet-stream', 'Cache-Control': 'no-store' }).end(file);
  } catch { response.writeHead(404).end(); }
});
server.listen(8765, '127.0.0.1', () => console.log('Preview: http://127.0.0.1:8765'));
