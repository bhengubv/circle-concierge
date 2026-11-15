import { spawn } from 'node:child_process';
import { mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const gameRoot = resolve(process.argv[2] || '');
const chromePath = process.argv[3] || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
if (!gameRoot || !process.argv[2]) throw new Error('Pass the extracted game folder.');
const profile = await mkdtemp(join(tmpdir(), 'concierge-game-smoke-'));
const server = spawn(process.execPath, ['preview.mjs'], { cwd: gameRoot, windowsHide: true });
const chrome = spawn(chromePath, ['--headless=new', '--disable-gpu', '--no-sandbox', '--no-proxy-server',
  '--remote-allow-origins=*', '--remote-debugging-port=9227', `--user-data-dir=${profile}`, '--window-size=1280,800',
  'about:blank'], { windowsHide: true });
try {
  async function waitFor(url) {
    for (let i = 0; i < 80; i++) {
      try { const response = await fetch(url); if (response.ok) return response; } catch { /* starting */ }
      await new Promise(done => setTimeout(done, 100));
    }
    throw new Error(`Timed out waiting for ${url}`);
  }
  await waitFor('http://127.0.0.1:8765/scene.json');
  await waitFor('http://127.0.0.1:9227/json/version');
  const target = await fetch('http://127.0.0.1:9227/json/new?http://127.0.0.1:8765', { method: 'PUT' }).then(r => r.json());
  const socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((done, fail) => { socket.addEventListener('open', done, { once: true }); socket.addEventListener('error', fail, { once: true }); });
  let next = 1;
  const pending = new Map();
  socket.addEventListener('message', event => {
    const message = JSON.parse(event.data);
    if (!message.id || !pending.has(message.id)) return;
    const { done, fail } = pending.get(message.id);
    pending.delete(message.id);
    message.error ? fail(new Error(message.error.message)) : done(message.result);
  });
  const send = (method, params = {}) => new Promise((done, fail) => {
    const id = next++; pending.set(id, { done, fail }); socket.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async expression => (await send('Runtime.evaluate', { expression, returnByValue: true })).result.value;
  await send('Runtime.enable');
  await send('Page.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 800, deviceScaleFactor: 1, mobile: false });
  for (let i = 0; i < 80; i++) {
    if (await evaluate("document.querySelector('#progress')?.textContent === '0 / 3' && !!document.querySelector('canvas')")) break;
    await new Promise(done => setTimeout(done, 100));
  }
  if (await evaluate("document.querySelector('#progress')?.textContent") !== '0 / 3')
    throw new Error('Game did not load its scene and progress indicator.');
  if (process.argv[5]) {
    await new Promise(done => setTimeout(done, 250));
    const { data } = await send('Page.captureScreenshot', { format: 'png' });
    await writeFile(process.argv[5], Buffer.from(data, 'base64'));
  }
  // Exercise the real browser pointer path against the rendered room.
  for (let y = 280; y <= 620; y += 25) {
    for (let x = 340; x <= 950; x += 25) {
      await send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 });
      await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 });
      if (await evaluate("document.querySelector('#progress').textContent") === '3 / 3') break;
    }
    if (await evaluate("document.querySelector('#progress').textContent") === '3 / 3') break;
  }
  const reached = await evaluate("document.querySelector('#progress').textContent");
  if (reached !== '3 / 3') throw new Error(`Game stopped at ${reached}.`);
  await send('Runtime.evaluate', { expression: "document.querySelector('#reset').click()" });
  const reset = await evaluate("document.querySelector('#progress').textContent");
  if (reset !== '0 / 3') throw new Error(`Reset stopped at ${reset}.`);
  await send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
  await send('Page.reload', { ignoreCache: true });
  await new Promise(done => setTimeout(done, 700));
  for (let i = 0; i < 80; i++) {
    if (await evaluate("document.readyState === 'complete' && document.querySelector('#progress')?.textContent === '0 / 3' && document.title === 'Circle Signal Hunt'")) break;
    await new Promise(done => setTimeout(done, 100));
  }
  if (!await evaluate("document.readyState === 'complete' && document.querySelector('#progress')?.textContent === '0 / 3'"))
    throw new Error('Mobile game did not reload.');
  if (await evaluate('innerWidth') !== 390) throw new Error('Mobile viewport meta tag did not apply.');
  if (await evaluate('document.documentElement.scrollWidth > innerWidth'))
    throw new Error('Mobile layout overflows horizontally.');
  if (process.argv[4]) {
    const { data } = await send('Page.captureScreenshot', { format: 'png' });
    await writeFile(process.argv[4], Buffer.from(data, 'base64'));
  }
  console.log('Browser smoke passed: loaded room, collected all three signals, reset to 0 / 3; mobile layout fits.');
  socket.close();
} finally {
  server.kill();
  chrome.kill();
}
