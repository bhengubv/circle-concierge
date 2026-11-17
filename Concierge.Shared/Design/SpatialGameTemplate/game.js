import * as THREE from './vendor/three.module.js';

const canvas = document.querySelector('#scene');
const message = document.querySelector('#message');
const progress = document.querySelector('#progress');
const vrButton = document.querySelector('#vr');
const arButton = document.querySelector('#ar');
const xrOverlay = document.querySelector('#xr-overlay');
const xrProgress = document.querySelector('#xr-progress');
const xrMessage = document.querySelector('#xr-message');
const xrExit = document.querySelector('#xr-exit');
const data = await fetch('./scene.json', { cache: 'no-store' }).then(response => {
  if (!response.ok) throw new Error('The room file is missing.');
  return response.json();
});
document.title = data.title;
document.querySelector('#game-title').textContent = data.title;

const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true });
renderer.setPixelRatio(Math.min(devicePixelRatio || 1, 2));
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.xr.enabled = true;
renderer.xr.setReferenceSpaceType('local');
const scene = new THREE.Scene();
scene.background = new THREE.Color('#07111f');
scene.add(new THREE.HemisphereLight('#c8f2ff', '#174167', 2.2));
const sun = new THREE.DirectionalLight('#ffffff', 2.3);
sun.position.set(-3, 7, 4);
scene.add(sun);
const room = new THREE.Group();
scene.add(room);
const width = Math.max(1, data.widthM);
const depth = Math.max(1, data.depthM);

const floor = new THREE.Mesh(new THREE.PlaneGeometry(width + .5, depth + .5),
  new THREE.MeshStandardMaterial({ color: '#173b57', roughness: .85, metalness: .1, side: THREE.DoubleSide }));
floor.rotation.x = -Math.PI / 2;
room.add(floor);
const grid = new THREE.GridHelper(Math.max(width, depth) + .5, Math.max(2, Math.ceil(Math.max(width, depth) * 2)), '#69cfff', '#316687');
grid.position.y = .004;
room.add(grid);

const wallMaterial = new THREE.MeshStandardMaterial({ color: '#4b93b9', roughness: .72, metalness: .08, transparent: true, opacity: .77 });
function wallBox(wall, from, to, bottom, top) {
  if (to - from < .005 || top - bottom < .005) return;
  const dx = wall.b[0] - wall.a[0];
  const dz = wall.b[1] - wall.a[1];
  const length = Math.hypot(dx, dz);
  const solid = new THREE.Mesh(new THREE.BoxGeometry(to - from, top - bottom, .07), wallMaterial);
  const at = (from + to) / 2 / length;
  solid.position.set(wall.a[0] + dx * at, (bottom + top) / 2, wall.a[1] + dz * at);
  solid.rotation.y = -Math.atan2(dz, dx);
  room.add(solid);
}
for (const wall of data.walls) {
  const length = Math.hypot(wall.b[0] - wall.a[0], wall.b[1] - wall.a[1]);
  let at = 0;
  for (const opening of [...wall.openings].sort((a, b) => a.at - b.at)) {
    const from = Math.max(at, Math.min(length, opening.at));
    const to = Math.max(from, Math.min(length, opening.at + opening.width));
    wallBox(wall, at, from, 0, wall.height);
    wallBox(wall, from, to, 0, Math.min(wall.height, opening.sill));
    wallBox(wall, from, to, Math.min(wall.height, opening.sill + opening.height), wall.height);
    at = to;
  }
  wallBox(wall, at, length, 0, wall.height);
}

const targetObjects = [];
const targets = new Set(data.targets);
for (const item of data.objects) {
  const target = targets.has(item.id);
  const material = new THREE.MeshStandardMaterial({
    color: target ? '#19b6f4' : '#697e9b', roughness: .42, metalness: .18,
    emissive: target ? '#00517d' : '#000000', emissiveIntensity: target ? .7 : 0,
  });
  const mesh = new THREE.Mesh(new THREE.BoxGeometry(item.width, item.height, item.depth), material);
  mesh.position.set(item.x, item.height / 2, item.z);
  mesh.rotation.y = item.rotation || 0;
  mesh.userData = { id: item.id, name: item.name, target };
  room.add(mesh);
  if (target) {
    const halo = new THREE.Mesh(new THREE.TorusGeometry(Math.max(.12, Math.min(item.width, item.depth) * .32), .024, 8, 36),
      new THREE.MeshBasicMaterial({ color: '#42e5ff' }));
    halo.rotation.x = Math.PI / 2;
    halo.position.set(item.x, item.height + .08, item.z);
    room.add(halo);
    mesh.userData.halo = halo;
    targetObjects.push(mesh);
  }
}

const found = new Set();
function collect(hit) {
  if (!hit?.userData.target || found.has(hit.userData.id)) return;
  found.add(hit.userData.id);
  hit.material.color.set('#b5f6ff');
  hit.material.emissive.set('#1b8bb7');
  hit.userData.halo.visible = false;
  progress.textContent = `${found.size} / ${targets.size}`;
  message.textContent = found.size === targets.size
    ? 'All signals found. You completed the room!'
    : `Found ${hit.userData.name}. ${targets.size - found.size} signals remain.`;
  syncXrHud();
}
document.querySelector('#reset').addEventListener('click', () => {
  found.clear();
  for (const item of targetObjects) {
    item.material.color.set('#19b6f4');
    item.material.emissive.set('#00517d');
    item.userData.halo.visible = true;
  }
  progress.textContent = `0 / ${targets.size}`;
  message.textContent = 'Drag to look around. Tap a glowing object to collect its signal.';
  syncXrHud();
});
progress.textContent = `0 / ${targets.size}`;

const camera = new THREE.PerspectiveCamera(52, 1, .02, 100);
let azimuth = -.65;
let elevation = .66;
let distance = Math.max(width, depth) * 1.6 + 1.5;
function placeCamera() {
  const aspect = (canvas.clientWidth || innerWidth) / (canvas.clientHeight || innerHeight);
  const framedDistance = distance * Math.max(1, .9 / aspect);
  camera.position.set(Math.sin(azimuth) * Math.cos(elevation) * framedDistance,
    Math.sin(elevation) * framedDistance, Math.cos(azimuth) * Math.cos(elevation) * framedDistance);
  camera.lookAt(0, .7, 0);
}
function resize() {
  const w = canvas.clientWidth || innerWidth;
  const h = canvas.clientHeight || innerHeight;
  renderer.setSize(w, h, false);
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
  placeCamera();
}
addEventListener('resize', resize);
resize();
const ray = new THREE.Raycaster();
const pointer = new THREE.Vector2();
function selectAt(x, y) {
  const bounds = canvas.getBoundingClientRect();
  pointer.set(((x - bounds.left) / bounds.width) * 2 - 1, -((y - bounds.top) / bounds.height) * 2 + 1);
  ray.setFromCamera(pointer, camera);
  collect(ray.intersectObjects(targetObjects, false)[0]?.object);
}
let drag;
canvas.addEventListener('pointerdown', event => {
  drag = { id: event.pointerId, x: event.clientX, y: event.clientY, moved: 0 };
  canvas.setPointerCapture(event.pointerId);
});
canvas.addEventListener('pointermove', event => {
  if (!drag || drag.id !== event.pointerId) return;
  const dx = event.clientX - drag.x;
  const dy = event.clientY - drag.y;
  drag.moved += Math.abs(dx) + Math.abs(dy);
  azimuth -= dx * .006;
  elevation = THREE.MathUtils.clamp(elevation + dy * .006, .14, 1.45);
  drag.x = event.clientX;
  drag.y = event.clientY;
  placeCamera();
});
canvas.addEventListener('pointerup', event => {
  if (drag?.id === event.pointerId && drag.moved < 10) selectAt(event.clientX, event.clientY);
  drag = undefined;
});
canvas.addEventListener('wheel', event => {
  distance = THREE.MathUtils.clamp(distance + event.deltaY * .008, 2, 30);
  placeCamera();
}, { passive: true });
addEventListener('keydown', event => {
  if (event.key === 'ArrowLeft') azimuth -= .14;
  else if (event.key === 'ArrowRight') azimuth += .14;
  else if (event.key === 'ArrowUp') elevation = Math.min(1.45, elevation + .1);
  else if (event.key === 'ArrowDown') elevation = Math.max(.14, elevation - .1);
  else return;
  event.preventDefault();
  placeCamera();
});

// XR is an optional mode. The browser game above remains playable without it.
// AR places a miniature room at a real-world hit-test point; it does not claim
// to align the scan to the original building or provide occlusion.
let xrSession;
let xrMode;
let hitSource;
let placed = false;
const hudCanvas = document.createElement('canvas');
hudCanvas.width = 768;
hudCanvas.height = 256;
const hudTexture = new THREE.CanvasTexture(hudCanvas);
hudTexture.colorSpace = THREE.SRGBColorSpace;
const xrHud = new THREE.Group();
const hudCard = new THREE.Mesh(new THREE.PlaneGeometry(.72, .24),
  new THREE.MeshBasicMaterial({ map: hudTexture, transparent: true, depthTest: false, side: THREE.DoubleSide }));
xrHud.add(hudCard);
const exitCanvas = document.createElement('canvas');
exitCanvas.width = 256;
exitCanvas.height = 128;
const exitContext = exitCanvas.getContext('2d');
exitContext.fillStyle = '#8f2746';
exitContext.fillRect(0, 0, 256, 128);
exitContext.fillStyle = '#ffffff';
exitContext.font = 'bold 60px system-ui';
exitContext.textAlign = 'center';
exitContext.fillText('EXIT', 128, 84);
const xrExitTarget = new THREE.Mesh(new THREE.PlaneGeometry(.15, .075),
  new THREE.MeshBasicMaterial({ map: new THREE.CanvasTexture(exitCanvas), depthTest: false, side: THREE.DoubleSide }));
xrExitTarget.position.set(.245, -.047, .008);
xrHud.add(xrExitTarget);
xrHud.visible = false;
scene.add(xrHud);
function syncXrHud() {
  xrProgress.textContent = `${found.size} / ${targets.size} signals`;
  xrMessage.textContent = message.textContent;
  const ctx = hudCanvas.getContext('2d');
  ctx.fillStyle = '#071a2fea';
  ctx.fillRect(0, 0, 768, 256);
  ctx.strokeStyle = '#6bd8ff';
  ctx.lineWidth = 8;
  ctx.strokeRect(4, 4, 760, 248);
  ctx.fillStyle = '#f4f9ff';
  ctx.font = 'bold 62px system-ui';
  ctx.fillText(`${found.size} / ${targets.size} signals`, 38, 92);
  ctx.font = '36px system-ui';
  ctx.fillText(message.textContent.slice(0, 37), 38, 168);
  hudTexture.needsUpdate = true;
}
syncXrHud();
xrExit.addEventListener('beforexrselect', event => event.preventDefault());
xrExit.addEventListener('click', () => xrSession?.end().catch(error => { message.textContent = error.message; }));
const reticle = new THREE.Mesh(new THREE.RingGeometry(.08, .11, 32),
  new THREE.MeshBasicMaterial({ color: '#40dfff', side: THREE.DoubleSide }));
reticle.rotation.x = -Math.PI / 2;
reticle.visible = false;
scene.add(reticle);
function controllerSelect(controller) {
  controller.updateMatrixWorld(true);
  const origin = new THREE.Vector3().setFromMatrixPosition(controller.matrixWorld);
  const direction = new THREE.Vector3(0, 0, -1).transformDirection(controller.matrixWorld);
  ray.set(origin, direction);
  if (xrHud.visible && ray.intersectObject(xrExitTarget, false).length) {
    xrSession?.end();
    return;
  }
  if (xrMode === 'immersive-ar' && !placed) {
    if (!reticle.visible) return;
    room.position.copy(reticle.position);
    room.scale.setScalar(.2);
    room.visible = true;
    placed = true;
    reticle.visible = false;
    message.textContent = 'Room placed. Tap glowing objects to find the signals.';
    syncXrHud();
    return;
  }
  collect(ray.intersectObjects(targetObjects, false)[0]?.object);
}
for (let index = 0; index < 2; index++) {
  const controller = renderer.xr.getController(index);
  const beam = new THREE.Line(new THREE.BufferGeometry().setFromPoints([
    new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, 0, -1)]),
  new THREE.LineBasicMaterial({ color: '#56dfff' }));
  beam.scale.z = 3;
  beam.visible = false;
  controller.add(beam);
  controller.userData.beam = beam;
  controller.addEventListener('select', () => controllerSelect(controller));
  scene.add(controller);
}
function finishXr() {
  try { hitSource?.cancel(); } catch { /* A browser may already have ended it. */ }
  hitSource = undefined;
  xrSession = undefined;
  xrMode = undefined;
  room.visible = true;
  room.position.set(0, 0, 0);
  room.scale.setScalar(1);
  reticle.visible = false;
  xrHud.visible = false;
  xrOverlay.classList.remove('active');
  for (let index = 0; index < 2; index++) renderer.xr.getController(index).userData.beam.visible = false;
  vrButton.disabled = false;
  arButton.disabled = false;
  scene.background = new THREE.Color('#07111f');
  message.textContent = 'Drag to look around. Tap a glowing object to collect its signal.';
  syncXrHud();
}
async function startXr(mode) {
  if (xrSession) return;
  let session;
  vrButton.disabled = true;
  arButton.disabled = true;
  try {
    session = await navigator.xr.requestSession(mode,
      mode === 'immersive-ar'
        ? { requiredFeatures: ['hit-test'], optionalFeatures: ['dom-overlay'], domOverlay: { root: xrOverlay } }
        : { optionalFeatures: ['local-floor', 'dom-overlay'], domOverlay: { root: xrOverlay } });
    xrSession = session;
    xrMode = mode;
    session.addEventListener('end', finishXr, { once: true });
    await renderer.xr.setSession(session);
    scene.background = mode === 'immersive-ar' ? null : new THREE.Color('#07111f');
    const hasOverlay = !!session.domOverlayState;
    xrOverlay.classList.toggle('active', hasOverlay);
    xrHud.visible = !hasOverlay;
    for (let index = 0; index < 2; index++)
      renderer.xr.getController(index).userData.beam.visible = mode === 'immersive-vr';
    if (mode === 'immersive-ar') {
      room.visible = false;
      placed = false;
      const viewer = await session.requestReferenceSpace('viewer');
      hitSource = await session.requestHitTestSource({ space: viewer });
      message.textContent = 'Move your phone to find a surface, then tap to place the miniature room.';
    } else {
      message.textContent = 'Point at glowing objects. Find three signals.';
    }
    syncXrHud();
  } catch (error) {
    if (session) await session.end().catch(() => {});
    finishXr();
    message.textContent = `XR could not start: ${error.message}`;
  }
}
if (navigator.xr && globalThis.isSecureContext) {
  navigator.xr.isSessionSupported('immersive-vr').then(supported => { vrButton.hidden = !supported; }).catch(() => {});
  navigator.xr.isSessionSupported('immersive-ar').then(supported => { arButton.hidden = !supported; }).catch(() => {});
}
vrButton.addEventListener('click', () => startXr('immersive-vr'));
arButton.addEventListener('click', () => startXr('immersive-ar'));

renderer.setAnimationLoop((_time, frame) => {
  if (xrMode && xrHud.visible) {
    const xrCamera = renderer.xr.getCamera(camera);
    xrCamera.getWorldPosition(xrHud.position);
    xrCamera.getWorldQuaternion(xrHud.quaternion);
    xrHud.position.add(new THREE.Vector3(0, -.24, -.85).applyQuaternion(xrHud.quaternion));
    xrHud.updateMatrixWorld(true);
  }
  if (xrMode === 'immersive-ar' && frame && hitSource && !placed) {
    const reference = renderer.xr.getReferenceSpace();
    const hit = frame.getHitTestResults(hitSource)[0];
    const pose = reference && hit?.getPose(reference);
    reticle.visible = !!pose;
    if (pose) reticle.position.set(pose.transform.position.x, pose.transform.position.y, pose.transform.position.z);
  }
  renderer.render(scene, camera);
});
