# SleptON! media handoff v1

## What SleptON! can integrate now

Concierge prepares a **local draft ZIP** for a book trailer or a Nota audio draft. The person confirms rights and presses **Prepare for SleptON!**. Concierge writes the ZIP below `Concierge/content/Exports/SleptON` and offers a Save package link when it is small enough for an inline download. Nothing is uploaded, submitted, listed, or published by this action.

SleptON! can implement its Video and Audio **import, preview, revision-request, and separate publish-decision** surfaces against this package. Its current CLI 1.1.0 integration is read-only and is not the import/publish route. The SleptON! team should design a local file/URI handoff or explicit user upload; Concierge must not add a hidden server transfer.

## Package contract

ZIP root contains `manifest.json` with `schemaVersion: 1`, `kind` (`book-trailer` or `audio-draft`), `status: "draft"`, `published: false`, `rightsConfirmed: true`, `title`, `createdUtc`, and `files`. Every `files` entry has a relative `path`, `mediaType`, `bytes`, and lower-case SHA-256. The importer must verify all entries and hashes, reject missing/extra paths and unsupported versions, and treat ZIP text as untrusted content. No absolute device path, auth token, source book, or raw Nota project is included.

Book packages contain `trailer.mp4`, `captions.srt`, `script.json`, `credits.md`, and `source-manifest.json`; the package manifest also has `sourceSha256` and `targetSeconds: 30`. The MP4 is a draft, not a published listing. Audio packages contain `audio.wav`, `audio.flac`, or `audio.mp3`; silent drafts cannot be packaged when Concierge detects silence. SleptON! must listen/check the file before publication.

## Verified Concierge evidence

- Tests exercise permission refusal, package contents, SHA-256 integrity, no absolute path in the manifest, approved page actions, and silent-audio refusal.
- A local original TXT source rendered to an H.264 1280×720, 30 fps MP4 of 30.02 seconds. The initial live frame was clipped; the card renderer was corrected to wrap text, and the rerendered frame was visually inspected.
- A real Nota 0.60.1 scratch song produced an eight-second audible stereo WAV through Concierge, measured at −15.5 dB peak. The same WAV was copied into Concierge's consolidated content tree with an identical SHA-256; its original remains.

## Not release acceptance yet

The book first cut uses source excerpts and moving title cards, without narration or generated imagery. A live Circle AI request to change the Nota song did not call Nota tools because service agentic work was not connected. Mobile-to-Desktop handoff is not implemented. Large-file inline downloads, Mac Catalyst production, and a real SleptON! importer remain unverified. SleptON! can start against the package contract now; neither app should call the complete creator journey ready for release.
