# SleptON! media handoff v1

## What SleptON! can integrate now

Concierge prepares a **local draft ZIP** for a book trailer or a Nota audio draft. The person confirms rights and presses **Prepare for SleptON!**. Concierge writes the ZIP below `Concierge/content/Exports/SleptON` and offers a Save package link when it is small enough for an inline download. Nothing is uploaded, submitted, listed, or published by this action.

SleptON! has built a local Video and Audio importer against this package in its `codex/slepton-concierge-import` branch. Its current CLI 1.1.0 integration in Concierge is read-only and is not the import/publish route. The two apps use an explicit Files handoff; Concierge must not add a hidden server transfer.

On MAUI devices, **Save to Files** opens the operating system's file saver. The creator chooses an accessible location, opens SleptON! and uses **Import from Concierge** under Publish → Drafts. This copies the ZIP out of Concierge's private app folder only after the creator acts. The package writer refuses a ZIP if its compressed size or extracted entries exceed SleptON!'s current 50 MiB import limit. Web's private book import remains disabled.

## Package contract

ZIP root contains `manifest.json` with `schemaVersion: 1`, `kind` (`book-trailer` or `audio-draft`), `status: "draft"`, `published: false`, `rightsConfirmed: true`, `title`, `createdUtc`, and `files`. Every `files` entry has a relative `path`, `mediaType`, `bytes`, and lower-case SHA-256. The importer must verify all entries and hashes, reject missing/extra paths and unsupported versions, and treat ZIP text as untrusted content. No absolute device path, auth token, source book, or raw Nota project is included.

Book packages contain `trailer.mp4`, `captions.srt`, `script.json`, `credits.md`, and `source-manifest.json`; the package manifest also has `sourceSha256` and `targetSeconds: 30`. The MP4 is a draft, not a published listing. Audio packages contain `audio.wav`, `audio.flac`, or `audio.mp3`; silent drafts cannot be packaged when Concierge detects silence. SleptON! must listen/check the file before publication.

SleptON! can export a separate `slepton-import-result.json` receipt through its native share sheet. Concierge's **Read SleptON! result** opens the device file picker and accepts a bounded v1 JSON receipt: `{ "schemaVersion": 1, "status": "accepted", "draftId": "<guid>", "revisionId": "<64-character lowercase SHA-256>" }`, or a rejected result with `errors: [{ "code": "...", "message": "...", "path": "..." }]`. `path` is optional. Concierge displays the reported outcome, but v1 has no package identity in the receipt, so Concierge cannot prove which ZIP it refers to. The creator must check the draft in SleptON! before treating the import as complete. A future receipt should bind to a stable package identity.

## Verified Concierge evidence

- Tests exercise permission refusal, package contents, SHA-256 integrity, no absolute path in the manifest, approved page actions, and silent-audio refusal.
- A local original TXT source rendered to an H.264 1280×720, 30 fps MP4 of 30.02 seconds. The initial live frame was clipped; the card renderer was corrected to wrap text, and the rerendered frame was visually inspected.
- A real Nota 0.60.1 scratch song produced an eight-second audible stereo WAV through Concierge, measured at −15.5 dB peak. The same WAV was copied into Concierge's consolidated content tree with an identical SHA-256; its original remains.
- The Concierge writer created a real Nota audio ZIP from that WAV. It contains only `audio.wav` and `manifest.json`; the WAV's 3,072,044 bytes and SHA-256 match both the archive manifest and the original. SleptON!'s local importer test accepted this ZIP and the real trailer ZIP, verified each attachment hash, reopened both from SQLite, and deduplicated identical retries. A physical device-to-device Files transfer and playback are still unverified.

## Not release acceptance yet

The book first cut uses source excerpts and moving title cards, without narration or generated imagery. A live Circle AI request to change the Nota song did not call Nota tools because service agentic work was not connected. Mobile-to-Desktop handoff is not implemented. Large-file Files export, Mac Catalyst production, and the physical phone import journey remain unverified. SleptON!'s local importer exists, but publish submission is blocked by its content API not persisting the linked media URL. Neither app should call the complete creator journey ready for release.
