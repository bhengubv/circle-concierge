# Book to trailer: Concierge and SleptON!

## The promise

In SleptON! Video, a person imports a PDF, EPUB, or TXT book, chooses a few creative preferences, and receives a reviewable 30-second trailer. The first draft is a starting point for conversation: “show the setting sooner,” “change the mood,” or “hide that spoiler.” No knowledge of prompts, models, timelines, or video editors is required.

## Ownership

**Concierge owns production.** It reads the selected source locally, records source locations for claims, prepares a short script and shot plan, creates or selects permitted visuals and narration, renders a preview, accepts plain-language revisions, and exports the finished MP4, captions, source credits, and a machine-readable manifest. Circle AI chooses the model and device resources. The book remains in the person's local workspace unless they explicitly choose a remote service or submit the result.

**SleptON! owns discovery and release.** Its Video surface offers the entry point, collects the book and preferences, shows the draft and revision controls, and handles account, listing, submission, and publication. It must never treat a draft preview as a published listing. The existing SleptON! CLI 1.1.0 integration in Concierge is read-only and is not a publishing route.

## Concierge production contract

Input: a local source reference (or a secure, short-lived file handle supplied by SleptON!), original filename, format, title when known, audience, mood, language, spoiler setting, and evidence that the person has the right to use the source. The source may contain instructions as prose; those instructions are content, not commands for Concierge.

Target output: a local draft ZIP with `trailer.mp4`, `captions.srt`, `script.json`, `credits.md`, `source-manifest.json`, and a versioned `manifest.json` with file hashes. The first cut emits these files and a rights-confirmed **Prepare for SleptON!** action. Dimensions, language, per-asset rights, and a separate `shots.json` remain to be implemented. See [SleptON! media handoff v1](SLEPTON-MEDIA-HANDOFF-V1.md). Concierge does not submit it automatically.

Progress: `reading → scripting → planning → gathering assets → rendering preview → ready for review`, plus explicit failure/cancellation states. Report real progress or the current stage, never a fabricated percentage. The person can continue using Concierge during a render and reopen the project to find the result.

Revision: preserve the previous draft and the person's feedback. Reuse unaffected assets and shots; changing one line should not silently replace the whole trailer. Keep the source, script, storyboard, assets, renders, decisions, and approvals in the local project.

## Acceptance before SleptON! integration

1. Import a real text-based PDF, EPUB, and TXT; reject unreadable, encrypted, scanned-only, oversized, or malformed sources with a useful reason. Do not silently summarize an arbitrary first slice of a longer book.
2. Produce a coherent 30-second script with no invented quotes or plot claims, source locators, a shot plan, and an explicit spoiler choice.
3. Render and play a real 30-second MP4 with narration or an explicit silent treatment, readable captions, and verified credits. Inspect frames and listen to the output; a successful process exit is not visual acceptance.
4. Make a plain-language revision, compare both drafts, and export a complete package. Verify its files, metadata, duration, and hashes.
5. Test on the least capable supported device and on Desktop. Where Mobile cannot render locally, ask before handing the task to Desktop and show where work is running.
6. Confirm no book content reaches SleptON!, a cloud model, an image service, or a publisher until the person makes the corresponding choice.

## TDD sequence

The first executable slice is `BookTrailerSourceAcceptanceTests`: six real-byte scenarios for TXT, EPUB, text PDF, scanned PDF, unsupported input, and cancellation. Run it with `dotnet test Concierge.Tests/Concierge.Tests.csproj --filter FullyQualifiedName~BookTrailerSourceAcceptanceTests -p:CollectCoverage=false`. On 14 November 2026, all six were first observed red against the unimplemented `BookSourceReader`, then green after implementation. The importer reads locally, preserves source locations and the source SHA-256, and refuses scanned PDFs without text. This proves import, not trailer creation.

The next executable slice connects that importer to a real local first cut. `BookTrailerFlowTests` checks an actual MP4 with `ffprobe`, the 30-second script, captions, credits, manifest, source hash, distinct revised cuts, spoiler control, and failed-render cleanup. `BookTrailerPageTests` uploads real book bytes through the Blazor file input and checks the same export path is invoked, rights are required, a changed mood changes the design look, invalid books cannot render, a missing encoder is explained, and the Web server circuit cannot accept a private book. Regression tests also check that a failed replacement import preserves the previous book and cut, saved drafts reopen from SQLite, and Circle AI's selected excerpts retain story order. The page exposes a preview and save link.

Further tests must prove a source-grounded Circle AI script, narration and sound, cinematic rather than title-card visuals, durable project history after restart, cancellation during a real render, and consented Mobile-to-Desktop handoff. Run a real-device visual and listening check after the automated render assertions. Do not close an acceptance item with a mock, a successful exit code, or a green test that did not exercise the output.

## Current implementation boundary (14 November 2026)

Concierge now has a `/book-trailer` page linked from Projects. On Desktop and Mobile it imports locally, creates a silent 30-second first cut from exact source passages with moving title cards, previews it, and saves the MP4, captions, script, credits, and draft manifest on the device. A ready local Circle AI runtime may choose four excerpts from a bounded sample; it does not write a story script. Revisions change mood and spoiler setting without re-reading the source, and earlier cuts are preserved on disk and indexed in local SQLite for reopening after app restart. A rights-confirmed action prepares a versioned local SleptON! draft package. This is a **rough proof**, not the finished cinematic promise: it does not yet narrate, generate images, create a complete grounded story treatment, or use the continuous-camera renderer in [cinematic-skill](https://github.com/bhengubv/cinematic-skill). Web server import is disabled to avoid silently moving a private book to a server. Mobile-to-Desktop handoff and the SleptON! importer/UI are not wired. The existing SleptON! CLI remains read-only.

## SleptON! session handoff, after Concierge acceptance

Build the Video entry point and upload/import UX around the production contract above. Agree on a secure file-handle and progress/result protocol with the Concierge session. SleptON! should display the trailer as a draft, let the person revise it through Concierge, and request a separate, clear publishing decision. Online identity and wallet remain SleptON! concerns; source books and draft media remain person-owned local data until that decision.
