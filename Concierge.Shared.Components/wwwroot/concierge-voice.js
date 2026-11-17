// concierge-voice.js
//
// Minimal wrapper around the browser's MediaRecorder + HTMLAudioElement so the chat
// composer can do mic capture and the speaker button can play back synthesised speech
// without touching the host project's JS pipeline.
//
// Exposed globals (window.conciergeVoice):
//   start()        : request the mic and begin recording. Throws if permission denied.
//   stop()         : stop recording and resolve to a base64 (no data: prefix) string of
//                    the captured audio/webm body. Resolves to '' if nothing recorded.
//   play(base64, mime) : play the supplied base64 payload via a transient <audio> tag.

const state = {
    recorder: null,
    chunks: [],
    stream: null,
    audio: null,
    generation: 0,
    startPromise: null,
    startGeneration: null,
    owner: null,
    visualContext: null,
    visualFrame: null,
};

let lastTextField = null;
let lastTextPath = null;
document.addEventListener('focusin', (event) => {
    const field = event.target;
    if (field instanceof HTMLTextAreaElement ||
        (field instanceof HTMLInputElement && /^(text|search|url|email)$/i.test(field.type))) {
        if (field.id === 'cm-voice-transcript') return;
        lastTextField = field;
        lastTextPath = location.pathname;
    }
});

function textTarget() {
    const note = location.pathname === '/notes' ? document.querySelector('#cm-note-body') : null;
    const field = note || (lastTextPath === location.pathname ? lastTextField : null);
    return field?.isConnected && !field.disabled && !field.readOnly ? field : null;
}

function hasTextTarget() { return Boolean(textTarget()); }

function insertText(value) {
    const field = textTarget();
    if (!field || !value) return false;
    field.value = field.value ? field.value.trimEnd() + ' ' + value : value;
    field.dispatchEvent(new Event('input', { bubbles: true }));
    field.dispatchEvent(new Event('change', { bubbles: true }));
    field.focus();
    return true;
}

function stopVisual() {
    if (state.visualFrame != null) cancelAnimationFrame(state.visualFrame);
    state.visualFrame = null;
    document.querySelector('.cm-bottom-b')?.style.removeProperty('--cm-voice-level');
    const context = state.visualContext;
    state.visualContext = null;
    if (context) void context.close().catch(() => { });
}

function startVisual(stream) {
    const Context = window.AudioContext || window.webkitAudioContext;
    if (!Context) return;
    try {
        const context = new Context();
        state.visualContext = context;
        const source = context.createMediaStreamSource(stream);
        const analyser = context.createAnalyser();
        analyser.fftSize = 256;
        source.connect(analyser);
        const samples = new Uint8Array(analyser.frequencyBinCount);
        const update = () => {
            if (state.stream !== stream) return;
            analyser.getByteTimeDomainData(samples);
            let energy = 0;
            for (const sample of samples) energy += Math.abs(sample - 128);
            const level = Math.min(1, energy / samples.length / 28);
            document.querySelector('.cm-bottom-b')?.style.setProperty('--cm-voice-level', level.toFixed(3));
            state.visualFrame = requestAnimationFrame(update);
        };
        void context.resume().catch(() => { });
        update();
    } catch (_) { stopVisual(); } // Visual feedback must never block recording.
}

function stopTracks(stream) {
    if (stream) {
        stream.getTracks().forEach((track) => {
            try { track.stop(); } catch (_) { /* A track may already have ended. */ }
        });
    }
}

function release(recorder, stream) {
    if (state.recorder === recorder) { state.recorder = null; state.owner = null; }
    if (state.stream === stream) state.stream = null;
    state.chunks = [];
    stopVisual();
    stopTracks(stream);
}

function start(owner = 'legacy') {
    if (state.owner && state.owner !== owner)
        return Promise.reject(new Error('The microphone is already in use.'));
    if (state.recorder) return Promise.resolve();
    if (state.startPromise) {
        if (state.startGeneration === state.generation) return state.startPromise;
        // A screen was closed while the OS permission dialog was pending. Let
        // that request settle, then retry for the newly active screen.
        return state.startPromise.then(() => start(owner));
    }

    state.owner = owner;
    const generation = ++state.generation;
    const starting = (async () => {
        let stream = null;
        try {
            stream = await navigator.mediaDevices.getUserMedia({ audio: true });
            // Navigation can cancel while Android is showing its permission prompt.
            // Do not start an orphan recorder if that prompt resolves later.
            if (generation !== state.generation) {
                stopTracks(stream);
                return;
            }

            state.chunks = [];
            state.stream = stream;
            startVisual(stream);
            const recorder = new MediaRecorder(stream);
            state.recorder = recorder;
            recorder.addEventListener('dataavailable', (event) => {
                if (state.recorder === recorder && event.data && event.data.size > 0) {
                    state.chunks.push(event.data);
                }
            });
            recorder.addEventListener('error', () => release(recorder, stream), { once: true });
            recorder.start();
        } catch (error) {
            if (generation === state.generation) {
                if (state.recorder) {
                    const recorder = state.recorder;
                    state.recorder = null;
                    try { if (recorder.state !== 'inactive') recorder.stop(); } catch (_) { }
                }
                state.stream = null;
                state.owner = null;
                state.chunks = [];
            }
            stopVisual();
            stopTracks(stream);
            throw error;
        }
    })();

    state.startPromise = starting;
    state.startGeneration = generation;
    starting.finally(() => {
        if (state.startPromise === starting) {
            state.startPromise = null;
            state.startGeneration = null;
        }
    }).catch(() => { /* The caller receives the original rejection. */ });
    return starting;
}

function blobToBase64(blob) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onloadend = () => {
            const value = reader.result || '';
            // FileReader returns a data URL like "data:audio/webm;base64,XXXX" — strip the prefix.
            const i = value.indexOf(',');
            resolve(i >= 0 ? value.slice(i + 1) : value);
        };
        reader.onerror = () => reject(reader.error);
        reader.readAsDataURL(blob);
    });
}

async function stop(owner = 'legacy') {
    if (state.owner && state.owner !== owner)
        throw new Error('This recording belongs to another voice control.');
    const recorder = state.recorder;
    if (!recorder) {
        return '';
    }
    return new Promise((resolve, reject) => {
        let settled = false;
        const finish = async () => {
            if (settled) return;
            settled = true;
            if (state.recorder !== recorder) {
                resolve('');
                return;
            }
            try {
                const blob = new Blob(state.chunks, { type: recorder.mimeType || 'audio/webm' });
                release(recorder, state.stream);
                resolve(await blobToBase64(blob));
            } catch (err) {
                release(recorder, state.stream);
                reject(err);
            }
        };
        recorder.addEventListener('stop', finish, { once: true });
        recorder.addEventListener('error', (event) => {
            if (settled) return;
            settled = true;
            release(recorder, state.stream);
            reject(event.error || new Error('Audio recording failed.'));
        }, { once: true });
        try {
            if (recorder.state === 'inactive') void finish();
            else recorder.stop();
        } catch (err) {
            release(recorder, state.stream);
            reject(err);
        }
    });
}

function cancel(owner = 'legacy') {
    if (owner && state.owner && state.owner !== owner) return;
    ++state.generation;
    const recorder = state.recorder;
    const stream = state.stream;
    state.recorder = null;
    state.stream = null;
    state.owner = null;
    state.chunks = [];
    stopVisual();
    try {
        if (recorder && recorder.state !== 'inactive') recorder.stop();
    } catch (_) { /* The tracks still need releasing if stop itself fails. */ }
    stopTracks(stream);
}

function play(base64, mime) {
    if (!base64) {
        return;
    }
    if (!state.audio) {
        state.audio = document.createElement('audio');
        state.audio.style.display = 'none';
        document.body.appendChild(state.audio);
    }
    state.audio.src = 'data:' + (mime || 'audio/mpeg') + ';base64,' + base64;
    const promise = state.audio.play();
    if (promise && typeof promise.catch === 'function') {
        promise.catch(() => { /* autoplay blocked — user must interact first; no-op */ });
    }
}

window.addEventListener('pagehide', () => cancel(null));
window.conciergeVoice = { start, stop, cancel, play, hasTextTarget, insertText };
