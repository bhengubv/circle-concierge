// camera.js — Concierge in-line camera capture.
//
// Flow:
//   1. Caller calls conciergeCamera.open() → opens a full-screen view with
//      live preview, a single shutter button, a switch-camera button, and
//      a "cancel" button.
//   2. User taps shutter → snap a still from the current MediaStream,
//      compress to JPEG ~80% quality, base64-encode.
//   3. Promise resolves with the base64 string (no "data:image/jpeg;base64,"
//      prefix — caller adds it). Cancel resolves with null.
//
// Permissions: caller is responsible for explaining WHY before opening. We
// surface the OS prompt the first time the stream is requested.

(function () {
    'use strict';

    let active = null;     // { stream, video, root, resolve }
    let facing = 'environment';

    function buildUi() {
        const root = document.createElement('div');
        root.className = 'cu-camera-root';
        root.innerHTML = `
            <video class="cu-camera-video" playsinline autoplay muted></video>
            <div class="cu-camera-controls">
                <button class="cu-camera-cancel" type="button" aria-label="Cancel">
                    <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="white" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><line x1="6" y1="6" x2="18" y2="18"/><line x1="18" y1="6" x2="6" y2="18"/></svg>
                </button>
                <button class="cu-camera-shutter" type="button" aria-label="Take picture"></button>
                <button class="cu-camera-flip" type="button" aria-label="Switch camera">
                    <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="white" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M4 6h12l-3 -3 m3 3 l-3 3"/><path d="M20 18H8l3 3 m-3 -3 l3 -3"/></svg>
                </button>
            </div>
        `;
        const style = document.createElement('style');
        style.textContent = `
            .cu-camera-root { position: fixed; inset: 0; background: #000; z-index: 9999; display: flex; flex-direction: column; }
            .cu-camera-video { flex: 1; width: 100%; object-fit: cover; }
            .cu-camera-controls { padding: 16px calc(20px + env(safe-area-inset-right)) calc(28px + env(safe-area-inset-bottom)) calc(20px + env(safe-area-inset-left)); display: grid; grid-template-columns: 1fr auto 1fr; align-items: center; gap: 24px; background: linear-gradient(0deg, rgba(0,0,0,0.7), rgba(0,0,0,0)); }
            .cu-camera-cancel, .cu-camera-flip { width: 48px; height: 48px; border-radius: 999px; background: rgba(255,255,255,0.18); border: 0; display: grid; place-items: center; cursor: pointer; }
            .cu-camera-cancel { justify-self: start; }
            .cu-camera-flip { justify-self: end; }
            .cu-camera-shutter { width: 76px; height: 76px; border-radius: 999px; background: white; border: 4px solid rgba(255,255,255,0.5); justify-self: center; cursor: pointer; transition: transform 80ms; }
            .cu-camera-shutter:active { transform: scale(0.92); }
        `;
        root.appendChild(style);
        return root;
    }

    async function startStream(video) {
        try {
            if (active && active.stream) active.stream.getTracks().forEach(t => t.stop());
            const stream = await navigator.mediaDevices.getUserMedia({
                video: { facingMode: facing, width: { ideal: 1280 }, height: { ideal: 1280 } },
                audio: false,
            });
            video.srcObject = stream;
            if (active) active.stream = stream;
            return stream;
        } catch (err) {
            return null;
        }
    }

    function snap(video) {
        const canvas = document.createElement('canvas');
        const w = video.videoWidth, h = video.videoHeight;
        // Cap at 1280 longest side to keep payloads sensible for upload.
        const scale = Math.min(1, 1280 / Math.max(w, h));
        canvas.width  = Math.round(w * scale);
        canvas.height = Math.round(h * scale);
        const ctx = canvas.getContext('2d');
        ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
        const dataUrl = canvas.toDataURL('image/jpeg', 0.82);
        const idx = dataUrl.indexOf(',');
        return idx >= 0 ? dataUrl.substring(idx + 1) : dataUrl;
    }

    function close(value) {
        if (!active) return;
        if (active.stream) active.stream.getTracks().forEach(t => t.stop());
        if (active.root && active.root.parentNode) active.root.parentNode.removeChild(active.root);
        const r = active.resolve;
        active = null;
        r(value);
    }

    async function open() {
        if (active) return null;
        return await new Promise(async (resolve) => {
            const root = buildUi();
            document.body.appendChild(root);
            const video = root.querySelector('.cu-camera-video');
            active = { stream: null, video, root, resolve };
            const stream = await startStream(video);
            if (!stream) { close(null); return; }
            root.querySelector('.cu-camera-cancel').addEventListener('click', () => close(null));
            root.querySelector('.cu-camera-flip').addEventListener('click', async () => {
                facing = facing === 'environment' ? 'user' : 'environment';
                await startStream(video);
            });
            root.querySelector('.cu-camera-shutter').addEventListener('click', () => {
                try { if (window.conciergeSense) { conciergeSense.playWhoosh(); conciergeSense.hapticSuccess(); } } catch {}
                const b64 = snap(video);
                close(b64);
            });
        });
    }

    window.conciergeCamera = { open };
})();
