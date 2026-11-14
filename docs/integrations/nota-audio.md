# Nota audio drafts in Concierge

## What works

On Windows or Mac Catalyst, open a song in Nota, enable **AI control (MCP)** in Nota Settings, then open Concierge **Projects → Make music with Nota**. The connection is opt-in and uses Nota's loopback Streamable HTTP MCP endpoint at `127.0.0.1:3900`. Concierge reads the current song summary, can export WAV, FLAC or MP3 into the local `Concierge/content/Music/Nota drafts` folder, and can preview and save small drafts. Older local drafts are copied forward without deleting their source. A silent export is marked when the local media probe is available. The connected agent sees a curated subset of Nota's tools; each call requires Concierge's normal tool approval.

The current screen exports the song open in Nota. It does not by itself compose a song from a prompt, synthesize vocals, or publish audio. On Android and iOS the screen explains that production is on Desktop; Mobile audio generation and handoff are still open.

After confirming rights, a person can prepare a local versioned draft ZIP for SleptON! import. It contains the verified audio file and a manifest with its hash; nothing is uploaded. See [SleptON! media handoff v1](SLEPTON-MEDIA-HANDOFF-V1.md).

Nota is AGPL-3.0-only. Concierge does not copy or link Nota source. The integration communicates with a separately installed Nota process through its public MCP interface. Review distribution obligations before shipping an installer that bundles Nota.

## Verification

- Fake MCP HTTP acceptance tests cover connect, curated tool discovery, an approved note call, denied export, true file creation, false export claims, audio preview, reopened drafts, and Mobile's unavailable state.
- On 2026-11-14, the official Nota 0.60.1 Windows installer ran locally. Concierge connected to its live scratch project. A first export was 8 seconds but silent. After placing and lengthening a MIDI note in Nota, Concierge exported an eight-second 48 kHz stereo WAV; `ffmpeg volumedetect` measured mean −40.0 dB and peak −15.5 dB. The built-in audio preview loaded.
- A live Ask first chat request to inspect the open Nota song and add a melody **did not call any Nota tool**. The Windows Circle AI runtime reported that service agentic work was not connected, and the answer instead asked for a MIDI file version that was never part of the request. The studio's chat link is therefore labeled for music ideas, not automated song editing.
- The initial Windows incremental build omitted new MCP transitive DLLs from the app output; a full rebuild included them and the live page worked. Use a full rebuild for release packaging and verify the output contains `ModelContextProtocol.dll` and `ModelContextProtocol.Core.dll`.

## Remaining acceptance

- Verify a spoken prompt causes an approved Nota tool call and a useful musical revision, with the real Circle AI service. The fake MCP test proves the route and approval seam, not model judgment.
- Verify FLAC and MP3 exports with the installed Nota version and long files over the inline preview limit.
- Verify Mac Catalyst with Nota running on a Mac. No Mac test has been run here.
- Decide the Mobile-to-Desktop audio handoff and persistence contract before claiming Mobile can produce or manage these drafts.
