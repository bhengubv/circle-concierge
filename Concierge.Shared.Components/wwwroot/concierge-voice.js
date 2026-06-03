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
};

async function start() {
    if (state.recorder) {
        return;
    }
    state.chunks = [];
    state.stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    state.recorder = new MediaRecorder(state.stream);
    state.recorder.addEventListener('dataavailable', (event) => {
        if (event.data && event.data.size > 0) {
            state.chunks.push(event.data);
        }
    });
    state.recorder.start();
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
        recorder.addEventListener('stop', async () => {
            try {
                const blob = new Blob(state.chunks, { type: recorder.mimeType || 'audio/webm' });
                state.chunks = [];
                state.recorder = null;
                if (state.stream) {
                    state.stream.getTracks().forEach((track) => track.stop());
                    state.stream = null;
                }
                resolve(await blobToBase64(blob));
            } catch (err) {
                reject(err);
            }
        }, { once: true });
        try {
            recorder.stop();
        } catch (err) {
            reject(err);
        }
    });
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

window.conciergeVoice = { start, stop, play };
