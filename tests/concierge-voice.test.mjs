import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

const script = await readFile(new URL('../Concierge.Shared.Components/wwwroot/concierge-voice.js', import.meta.url), 'utf8');

class Track {
    stopped = false;
    stop() { this.stopped = true; }
}

class Stream {
    tracks = [new Track()];
    getTracks() { return this.tracks; }
}

class Recorder extends EventTarget {
    static latest;
    static created = 0;
    static failOnStop = false;
    state = 'inactive';
    mimeType = 'audio/webm';

    constructor(stream) {
        super();
        this.stream = stream;
        Recorder.latest = this;
        Recorder.created++;
    }

    start() { this.state = 'recording'; }

    stop() {
        this.state = 'inactive';
        if (Recorder.failOnStop) {
            this.dispatchEvent(Object.assign(new Event('error'), { error: new Error('recorder failed') }));
            return;
        }
        this.dispatchEvent(Object.assign(new Event('dataavailable'), {
            data: new Blob(['spoken words'], { type: this.mimeType }),
        }));
        this.dispatchEvent(new Event('stop'));
    }
}

class Reader {
    result = null;
    onloadend = null;
    onerror = null;
    async readAsDataURL(blob) {
        this.result = `data:${blob.type};base64,${Buffer.from(await blob.arrayBuffer()).toString('base64')}`;
        this.onloadend?.();
    }
}

function harness(getUserMedia = async () => new Stream()) {
    Recorder.latest = undefined;
    Recorder.created = 0;
    Recorder.failOnStop = false;
    const window = { addEventListener() {} };
    const context = {
        window,
        navigator: { mediaDevices: { getUserMedia } },
        MediaRecorder: Recorder,
        FileReader: Reader,
        Blob,
        Promise,
        Error,
    };
    vm.runInNewContext(script, context, { filename: 'concierge-voice.js' });
    return window.conciergeVoice;
}

test('stop returns the recording and releases the microphone tracks', async () => {
    const stream = new Stream();
    const voice = harness(async () => stream);
    await voice.start();
    assert.equal(await voice.stop(), Buffer.from('spoken words').toString('base64'));
    assert.equal(stream.tracks[0].stopped, true);
    assert.equal(await voice.stop(), '');
});

test('navigation cancellation stops recording and discards the draft audio', async () => {
    const stream = new Stream();
    const voice = harness(async () => stream);
    await voice.start();
    voice.cancel();
    assert.equal(stream.tracks[0].stopped, true);
    assert.equal(await voice.stop(), '');
});

test('permission resolving after navigation does not start an orphan recorder', async () => {
    let grant;
    const voice = harness(() => new Promise(resolve => { grant = resolve; }));
    const pendingStart = voice.start();
    await Promise.resolve();
    voice.cancel();
    const lateStream = new Stream();
    grant(lateStream);
    await pendingStart;
    assert.equal(lateStream.tracks[0].stopped, true);
    assert.equal(Recorder.created, 0);
});

test('a new screen can start recording after a cancelled permission request settles', async () => {
    const grants = [];
    const voice = harness(() => new Promise(resolve => { grants.push(resolve); }));
    const oldScreenStart = voice.start();
    await Promise.resolve();
    voice.cancel();

    const newScreenStart = voice.start();
    const lateStream = new Stream();
    grants[0](lateStream);
    await oldScreenStart;
    await Promise.resolve();
    assert.equal(lateStream.tracks[0].stopped, true);

    const activeStream = new Stream();
    grants[1](activeStream);
    await newScreenStart;
    assert.equal(Recorder.created, 1);
    assert.equal(Recorder.latest.stream, activeStream);
    voice.cancel();
    assert.equal(activeStream.tracks[0].stopped, true);
});

test('recorder errors reject stop and still release the microphone', async () => {
    const stream = new Stream();
    const voice = harness(async () => stream);
    await voice.start();
    Recorder.failOnStop = true;
    await assert.rejects(voice.stop(), /recorder failed/);
    assert.equal(stream.tracks[0].stopped, true);
});
