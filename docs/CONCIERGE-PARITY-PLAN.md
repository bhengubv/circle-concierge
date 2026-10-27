# Concierge parity delivery plan

**Source of evidence:** [28 October product parity review](acceptance/2026-10-28-product-parity-review.md).

**Product direction:** [Concierge vision](CONCIERGE-PRODUCT-VISION.md).

**Status:** Work queue. Check a task only after the named live acceptance is recorded. The 12-item review backlog is not an exhaustive list of competitor features.

## What parity means here

Concierge does not need the same interface as Codex, Claude Code, or Gemini. It must let a person state a goal, follow the work, receive a correct and useful result, inspect what happened, correct it, and continue on another device. Compare the same user outcome on each product; do not count a visible button, registered tool, passing mock, or connected model as completion.

The first reference journey is an original appointment-booking MVP for a one-person salon: customer phone booking; recurring lunch and holiday blocks; low cost; five hours of owner time each week; a two-week plan; a reviewable working deliverable; a correction; and a follow-up from a second device. The [five response-quality tasks](acceptance/2026-10-26-response-quality-tasks.md) remain the shorter regression set. Preserve their exact prompts and expected results so improvements can be compared.

## Ownership and constraints

| Owner | Responsible for | Boundary |
|---|---|---|
| Concierge | Chat/Workspace/Project experience, tool routing, approvals, records, files, rendering, scheduling, cross-device continuity and client-side acceptance | Work in this repository; do not implement model selection or inference in the client |
| Circle AI service team | Service capability contract, model/resource selection, inference quality and latency, service skills/memory, speech and visual engine support | Raise measured contract failures; do not edit Circle AI source here |
| Butler/integration owner | Externally executed operations and attributable receipts | Do not imply an action ran from a chat answer |

Every task records app build, device, service build, selected model when reported, elapsed times, full input/output, tool calls, skill/memory/source use, approvals, files, and screenshots or an event trace. A result that cannot be observed on the target device stays **unverified**.

## Ordered delivery queue

### Gate 0 — Make claims and measurements trustworthy

- [ ] **P00 — Re-run the baseline on installed builds.** Run the five response-quality conversations from fresh threads on Desktop and PixieDust. Capture cold and warm response intervals, time to first useful progress, answer accuracy, and rendered format. Record failures turn by turn; do not average away a failed task. **Done:** a dated transcript and scorecard for each attempted task on each device.
- [ ] **P01 — Correct the Skills claim on PixieDust.** The installed screen says Circle AI library matches are passed with the request, while current source describes library search as names/IDs for discovery. Trace one ordinary request through service-skill search, selection, and actual use. If there is no such path, make the UI describe discovery honestly until the path exists. **Done:** live request trace or corrected live screen; no claim based only on the count of 1,378 entries.
- [ ] **P02 — Make run diagnostics visible to the person.** For each answer show the running device, preparation versus inference, meaningful progress, used tools/sources/skills, and an honest failure or retry state. Keep model choice automatic in Circle AI. **Done:** the salon journey exposes these facts without developer tooling on Desktop and PixieDust.
- [x] **P03 — Desktop summon.** The ordinary-window shortcut restored and focused the minimized app; the elevated-window limit is recorded in the [feature checkpoint](acceptance/2026-10-26-feature-gaps.md).

### Gate 1 — Deliver dependable answers before expanding the surface

- [ ] **P10 — Carry the right context.** Verify conversation history, selected notebook text, relevant long-term memory, and approved skill instructions at the actual service boundary on both Desktop and PixieDust. Avoid duplicating or flooding the prompt. **Done:** the model answers source questions and corrected follow-ups from the intended context; the trace identifies each context source.
- [ ] **P11 — Use service skills, not just list them.** Discover the service's supported skill invocation contract and let Circle AI choose relevant skills for each request. Keep the person's ability to pin a skill. If the published contract is names/IDs only, file that exact service-team dependency and keep the client copy honest. **Done:** a live task records the selected service skill and a result that follows it on both devices.
- [ ] **P12 — Close answer-quality failures.** Fix client prompt composition, tool-result handling, and response truncation where measurement identifies Concierge as the cause. Send exact traces for model/service-caused failures to the Circle AI team. **Done:** all five response-quality tasks pass completely on Desktop and PixieDust, including the $14 allowance calculation and the original-game recovery.
- [ ] **P13 — Render useful results.** Headings, lists, tables, code, math, citations, images, and attachments must appear as readable content, not raw syntax or clipped text. **Done:** the same generated two-week plan and artifact can be read and copied on Desktop and PixieDust; long answers remain navigable.
- [ ] **P14 — Reduce waiting without hiding it.** Measure client queueing, context construction, service preparation, and inference separately. Keep UI responsive while the service works; show real preparation progress. Optimize only the measured client bottleneck, and give the Circle AI team the measured service segment. **Done:** repeatable cold/warm traces show the improvement and no silent multi-minute wait.

**Gate 1 passes only when a complete answer is correct, grounded, readable, and recoverable.** A simple `12 + 1` answer does not close it.

### Gate 2 — Complete work, not just conversation

- [ ] **P20 — Project execution loop.** Turn the salon request into a durable Project plan, actionable steps, visible tool activity, an approval decision, and reviewable output. A correction must revise the existing run instead of starting an unrelated copy. **Done:** one live Desktop salon MVP journey reaches a usable deliverable with a provenance record and a follow-up.
- [ ] **P21 — Notebook grounding and sync.** Finish source ingestion beyond pasted text, source attribution, and cross-device notebook continuity. Reproduce and fix the PixieDust fabricated answer. **Done:** the same source question is answered correctly with cited source on both devices after restart and sync.
- [ ] **P22 — Unified Library.** Cover generated files and all supported media/storage paths, not only small inline data URIs. **Done:** the salon's plan, code/assets and previewable output are discoverable, openable, and attributable from Library on both devices.
- [ ] **P23 — File provenance.** Record the paths and resulting changes from file tools, shell commands, browser downloads, and external operations where the provider supplies them. Mark unknown side effects as unknown. **Done:** a person can inspect what the salon run created or changed, with no invented file list.
- [ ] **P24 — Browser work.** Provide a visible browser action path with takeover, permissions, progress and a receipt. **Done:** a live read-only research step completes with its source and a person can interrupt or take over.
- [ ] **P25 — Connected apps.** Make connection setup understandable, scope actions, request approvals as configured, and show receipts. **Done:** one read action and one reversible write action succeed against a configured integration, with a denied-action case that changes nothing.
- [ ] **P26 — App-closed schedules.** Run due work after Concierge's window closes and after restart; deliver result and notification once. **Done:** a scheduled salon follow-up fires while the UI is closed and reappears with its outcome when reopened.

**Gate 2 passes when the Desktop salon journey completes end to end.** The user must be able to inspect the work and correct it.

### Gate 3 — Make PixieDust a capable local worker

- [ ] **P30 — Local-first routing and one execution.** On PixieDust, use local Circle AI when it can handle the request. Transfer to Desktop only for a measured resource/capability limit, failure, or the person's manual choice. Sync after execution. **Done:** the salon follow-up is completed locally when suitable; a heavier step transfers once and both devices show the same result/run identity.
- [ ] **P31 — Continuous voice.** Extend the proven microphone start/stop cleanup to transcription, interruptible spoken turns, and conversation continuity. **Done:** a full spoken follow-up is captured, transcribed, answered, read aloud when chosen, and survives navigation; privacy indicator turns off on stop.
- [ ] **P32 — Live camera.** Establish the service's visual-input contract, then support a live camera assistance session with clear capture state. Do not treat still-photo attachment as video. **Done:** a real camera session answers a question about what the camera sees and stops capture on exit.
- [ ] **P33 — Screen sharing.** Request the platform permission in context, show what is shared, and stop reliably. **Done:** a live screen-help task works on PixieDust and sharing stops on user action or app interruption.

**Gate 3 passes only with a phone-local completed task and a separate, proven Desktop handoff.** It does not require the phone to mirror Desktop's layout.

### Gate 4 — Give StarDust useful, bounded control

- [ ] **P40 — Watch wake and quick input.** Verify a supported wake phrase or hands-free entry, voice/text submission and a visible receipt. **Done:** StarDust starts a request without opening the phone, and the watch shows that it was accepted.
- [ ] **P41 — Watch result and decisions.** Show a readable result, vibration/tone when appropriate, scrolling for long answers, and approve/deny/stop actions tied to the correct run. **Done:** a meeting-length task initiated elsewhere reaches StarDust with progress and one real decision; the action is reflected on Desktop/PixieDust without duplicate execution.
- [ ] **P42 — Watch connected actions.** Expose only actions that the linked device/service can perform and report their outcomes. **Done:** one safe action and one denied action are tested on StarDust, with service-side receipts.

## Completion rule

The plan is complete when all 12 review items are closed with live evidence, all five response-quality tasks pass on Desktop and PixieDust, the salon MVP reaches a reviewable deliverable and survives correction/handoff, and the Watch quick-input/result/decision journey passes on StarDust. Record remaining product differences against current competitor versions separately; do not claim universal feature equivalence from these gates. If a Circle AI or Butler contract blocks a task, keep it open with the exact request, evidence, owner and retest condition.
