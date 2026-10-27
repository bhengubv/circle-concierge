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
};

function stopTracks(stream) {
    if (stream) {
        stream.getTracks().forEach((track) => {
            try { track.stop(); } catch (_) { /* A track may already have ended. */ }
        });
    }
}

function release(recorder, stream) {
    if (state.recorder === recorder) state.recorder = null;
    if (state.stream === stream) state.stream = null;
    state.chunks = [];
    stopTracks(stream);
}

function start() {
    if (state.recorder) return Promise.resolve();
    if (state.startPromise) {
        if (state.startGeneration === state.generation) return state.startPromise;
        // A screen was closed while the OS permission dialog was pending. Let
        // that request settle, then retry for the newly active screen.
        return state.startPromise.then(() => start());
    }

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
                state.chunks = [];
            }
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

async function stop() {
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

function cancel() {
    ++state.generation;
    const recorder = state.recorder;
    const stream = state.stream;
    state.recorder = null;
    state.stream = null;
    state.chunks = [];
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

window.addEventListener('pagehide', cancel);
window.conciergeVoice = { start, stop, cancel, play };
