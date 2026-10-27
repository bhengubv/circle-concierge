# Concierge feature-gap implementation — 26 October 2026

This is an implementation checkpoint, not a claim of complete Gemini parity.
The twelve items below are the current working backlog; the comparison has not
established that they are an exhaustive inventory of Gemini's features.

## Reproducible checks

Run `dotnet test tests/Concierge.FeatureAcceptance/Concierge.FeatureAcceptance.csproj`.
The focused harness avoids the full test project's Web/WASM dependency graph.
Latest observed result: **40 passed, zero failed, zero skipped**.
It covers notebook persistence/context isolation, schedule execution/receipts,
approval isolation, stop, recurrence, corrupt-state preservation, UI rendering,
library preview, recorded file outcomes, schedule tools, malformed tool calls,
and the shipped icon symbols required by the phone composer/navigation.

Phone recording cleanup has a separate JavaScript contract suite. Run
`node --test tests/concierge-voice.test.mjs`. Latest observed result:
**5 passed, zero failed**. It covers microphone release after stop, cancellation
on navigation, a microphone permission result arriving after cancellation,
retrying from a new screen after that prompt settles, and recorder failure.
These mocked tests do not replace a live handset interruption test.

The runtime-selection regression was observed failing before correction:
the scheduler selected the last registered, unconfigured provider. It now uses
the same Circle AI connection preference as interactive chat. This does not
select a model; Circle AI retains that responsibility.

## Observed live outcomes

- Desktop notebook: created “Acceptance — notebook sources”, added the
  “Launch agreement” text, and started its conversation. Circle AI correctly
  answered that Juniper's advertising budget was 730 rand and its launch owner
  Nandi, citing that source. Conversation:
  `bd6f2104-5e04-41c6-b2ce-0784d9296930`.
- Desktop scheduled work: executed a one-sentence small-business advice request
  through Circle AI and displayed the saved result. Conversation:
  `59ad4358-783f-49db-bfc2-52390d9b79ff`.
  Evidence: [scheduled result](2026-10-26-scheduled-result.png).
- Desktop shortcut: Ctrl+Alt+C restored Concierge from a minimized window while
  File Explorer was active and navigated to chat. It did not restore from the
  elevated Services window. Retested live after rebuilding: from Explorer, the
  shortcut restored Concierge, focused the composer, and accepted the typed
  probe text. The probe was cleared without sending. Elevation-boundary behavior
  remains a Windows limitation.
- PixieDust: updated successfully with `adb install -r`, preserving data.
  Existing conversation and reply remained visible. The new Notebooks screen
  opened through the sidebar. Created and saved a phone-local notebook named
  “Acceptance - PixieDust” with a “Meeting notes” source stating the Juniper
  meeting is Friday at 10am and Nandi is the organiser; started a conversation
  from that notebook. The first source question received a fabricated answer
  (“next week”; “Juniper Networks leadership team”) and is a **failed grounding
  acceptance**. Do not count mobile notebook answering as working. A later
  microphone tap surfaced Android's permission dialog; the user granted it during
  the live microphone follow-up below.
- PixieDust revealed invisible external SVG icons for menu, microphone, camera
  and send. The source now embeds the existing design-system sprite in the
  document. The updated APK was installed with `adb install -r`, and a new
  screenshot confirms those icons are visible:
  [PixieDust composer](2026-10-26-pixiedust-icons.png).
- On that updated APK, asking a notebook question about the meeting date caused
  Circle AI's own `answered by clock` shortcut to return the current date, rather
  than use the note. The notebook reference is now demonstrably included in the
  service request by an automated contract test, but **source-grounded mobile
  answers still have not passed live acceptance**. The service's clock shortcut
  is outside Concierge's source-code scope and should be reviewed by the Circle
  AI team if it continues to intercept source questions.
- The recording-cleanup APK built with zero errors and installed with `adb
  install -r` on PixieDust and AngelDust, preserving app data. PixieDust launched
  and restored its saved conversation. Tapping the microphone surfaced Android's
  “Allow Concierge to record audio?” prompt; the user granted it. The live screen
  showed Android's green microphone indicator while recording. Stopping returned
  Concierge to idle. A second recording was interrupted by navigating to
  Notebooks: the green indicator disappeared, and AudioFlinger showed the
  Concierge record track idle. This verifies live capture start/stop and
  navigation cleanup on PixieDust. **Transcription is still unverified:** the
  phone's Concierge model folder has chat models but no local voice model, so the
  local Whisper path is unavailable. The test did not establish a usable
  transcript or conversation continuity.

## Remaining work, individually tracked

- [x] Global Desktop summon: live test from Explorer restored the minimized app,
  focused the composer, and accepted typed text. Elevated-window limitation is
  documented above.
- [ ] Unified Library: text-event preview and source navigation exist. Small,
  inline PNG/JPEG/WebP/GIF, audio and video data URIs can now preview when their
  declared type matches. Other output storage and full output coverage remain
  incomplete; rich-media previews have automated UI coverage but no device
  smoke test yet.
- [ ] Notebooks: text sources persist and link to conversations, but the live
  PixieDust answer ignored the source. Fix and pass this before claiming grounded
  notebook answers. Richer source ingestion and cross-device sync remain incomplete.
- [ ] Scheduling: while-open scheduling, approval, stop, repeat and saved results
  work; app-closed execution and completion notifications remain incomplete.
- [ ] Connected-app actions: friendly connection setup and live action acceptance.
- [ ] File provenance: recorded file tools work; shell and external-service
  file side effects are not attributable to individual paths. The activity page
  now records each `run_command` request and its outcome, but still does not claim
  complete provenance; external-service changes remain unreported unless the
  service publishes file activity.
- [ ] Browser operation: visible progress, takeover and live acceptance.
- [ ] Phone continuous voice: screen teardown now cancels capture, stops the
  microphone tracks, discards a late permission grant, and releases resources
  after recorder errors. Five JavaScript contract tests pass. On PixieDust, the
  user granted Android microphone access; live capture started, and navigating
  away stopped the microphone (privacy indicator disappeared; AudioFlinger track
  idle). Transcription and reliable conversation continuity remain unverified
  because no on-device voice model is installed.
- [ ] Live camera video assistance.
- [ ] Screen-sharing assistance.
- [ ] Watch wake phrase / hands-free start.
- [ ] Watch connected-app actions.

## Boundaries

The latest Android build, after adding Android's microphone permission path,
passed with zero errors and 104 warnings and was installed on PixieDust and
AngelDust. Warnings include
dependency vulnerability notices and Android 16 16 KB page-size warnings for
bundled native libraries. The focused .NET harness passes 40 checks, and the
separate phone-recording JavaScript suite passes 5 checks. The live PixieDust
capture and navigation-interruption checks also passed. Neither transcript
delivery nor end-to-end voice-to-conversation acceptance passed; the device has
no local voice model, and those remain open.
A Windows build passed with zero errors and 40 warnings. The rebuilt Desktop was
launched with WebView2 remote debugging enabled for the live shortcut/composer
check above. Existing dependency vulnerability warnings are not resolved by this
work. The full repository test suite has not passed here. No Circle AI service
source was changed.
