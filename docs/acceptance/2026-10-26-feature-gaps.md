# Concierge feature-gap implementation — 26 October 2026

This is an implementation checkpoint, not a claim of complete Gemini parity.
The twelve items below are the current working backlog; the comparison has not
established that they are an exhaustive inventory of Gemini's features.

## Reproducible checks

Run `dotnet test tests/Concierge.FeatureAcceptance/Concierge.FeatureAcceptance.csproj`.
The focused harness avoids the full test project's Web/WASM dependency graph.
Latest observed result: **37 passed, zero failed, zero skipped**.
It covers notebook persistence/context isolation, schedule execution/receipts,
approval isolation, stop, recurrence, corrupt-state preservation, UI rendering,
library preview, recorded file outcomes, schedule tools, malformed tool calls,
and the shipped icon symbols required by the phone composer/navigation.

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
  elevated Services window. Composer focus is not yet proven; a subsequent
  render-order correction still needs native verification.
- PixieDust: updated successfully with `adb install -r`, preserving data.
  Existing conversation and reply remained visible. The new Notebooks screen
  opened through the sidebar. Created and saved a phone-local notebook named
  “Acceptance - PixieDust” with a “Meeting notes” source stating the Juniper
  meeting is Friday at 10am and Nandi is the organiser; started a conversation
  from that notebook. The first source question received a fabricated answer
  (“next week”; “Juniper Networks leadership team”) and is a **failed grounding
  acceptance**. Do not count mobile notebook answering as working. A second
  attempt was interrupted by Android's microphone permission dialog; it was
  left on screen for the user and not acted on.
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

## Remaining work, individually tracked

- [ ] Global Desktop summon: verify typing lands in composer after restoration;
  document elevated-window limitations.
- [ ] Unified Library: text-event preview and source navigation exist; rich-media
  preview and full output coverage remain incomplete.
- [ ] Notebooks: text sources persist and link to conversations, but the live
  PixieDust answer ignored the source. Fix and pass this before claiming grounded
  notebook answers. Richer source ingestion and cross-device sync remain incomplete.
- [ ] Scheduling: while-open scheduling, approval, stop, repeat and saved results
  work; app-closed execution and completion notifications remain incomplete.
- [ ] Connected-app actions: friendly connection setup and live action acceptance.
- [ ] File provenance: recorded file tools work; shell and external-service
  changes are not captured and must not be presented as complete provenance.
- [ ] Browser operation: visible progress, takeover and live acceptance.
- [ ] Phone continuous voice: interruption and reliable conversation continuity.
- [ ] Live camera video assistance.
- [ ] Screen-sharing assistance.
- [ ] Watch wake phrase / hands-free start.
- [ ] Watch connected-app actions.

## Boundaries

The Android build after the icon/context changes passed with zero errors and 101
warnings and was installed on PixieDust. The focused test harness passed 38
checks. A Windows build in the normal output folder hit a file lock from the
running Desktop app. I then compiled the Windows app into an isolated output
folder against the already-built dependency assemblies: it passed with zero
errors and 40 warnings. The running Desktop was not replaced, so native shortcut
focus remains unverified. Existing dependency vulnerability warnings are not
resolved by this work. The full repository test suite has not passed here. No
Circle AI service source was changed.
