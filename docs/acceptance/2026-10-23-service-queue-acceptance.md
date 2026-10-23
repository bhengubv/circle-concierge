# Local Circle AI request queue — 23 October 2026

## Defects corrected

The 3.9.1 service supports protocol-2 AskSubmit/AskStatus records, but Concierge
still used the older blocking turn call and rejected a question while live text
readiness was Unknown/starting. A second defect hid IChatRuntimePreparation at
the safety decorator, so even an eligible runtime could not admit work from the
actual UI. Direct runtime tests alone had not exercised that wrapper.

Concierge now negotiates queue support from Hello. An accepting service can
retain the question while it prepares; this does not label text inference Ready.
The client submits once with a request key derived from the durable run, session
and message. It follows that ask's progress and final answer, using sequence-based
polling without a client-side preparation deadline. Stop withdraws the accepted
ask; it does not cancel service downloads. Legacy services retain their older
readiness-gated path. Native agentic actions remain disabled until an approval
bridge exists; this change does not claim tool execution parity.

Request status/cancellation use Query for the ask ID, matching the published
Android SDK's wire contract. The safety wrapper forwards preparation admission
dynamically while preserving the inner readiness and safety behavior.

## Physical-device results

Final APK SHA-256:
`8318109E4A776606102FD01408B1CF69C665996F77716DD9B60CEA18EE767CC3`.
Installed in place on both phones, preserving models, settings and history.

| Device | Fresh question | Actual answer | Service ask ID |
| --- | --- | --- | --- |
| AngelDust / CircleOS | 16:00 appointment, 30-minute travel | Leave at 15:30 to arrive exactly at 16:00. | `1f22f7ede89a426c8d90da5479c760e4` |
| PixieDust / AndroidOS | 15:00 meeting, 35-minute travel | To arrive by 15:00, you must leave at 14:25 (2:25 PM). | `b8360fe875184e209216853f8a0c1d9c` |

Both local build-60 services progressed Queued → WaitingForModel → Running →
Succeeded using Qwen3.5-2B-MNN. Each client log contains exactly one AskSubmit for
its test. While loading, Concierge displayed the service's preparation sentence.
Both Settings panels subsequently reported text conversations Ready. Voice,
speech recognition, wake word and live fetch readiness remained Not reported;
they were not relabelled ready because text worked.

Each answer appeared on Desktop under the original conversation ID:
`73c631c0-eddd-40b7-9806-1232962bd246` and
`ee17e8b7-a3cb-4f99-91c9-b1e57ba9be50`. The Desktop was used to read the synced
results; the requests were submitted through each phone's normal composer.

Measured client-log intervals from queued receipt to succeeded receipt were
26,992 ms on AngelDust and 17,435 ms on PixieDust. These include preparation and
polling; they are not pure inference times or exact UI rendering latency.
The adjacent JSON preserves the prompts, replies, observation timestamps,
receipt lines, Settings reports and Desktop transcripts.

PixieDust initially crashed in libmonodroid's assembly loader after incremental
installation. Reinstalling the same APK without incremental delivery launched
successfully. The final APK was delivered without incremental installation to
both phones; both remained open through the answer and Settings checks. The
underlying cause of that native loader crash has not been established.

## Verification and limits

- 64 focused tests passed, including queue negotiation, preparation/partial/final
  transitions, one submission, stable request arguments, failure, cancellation,
  wrong-record rejection and safety-wrapper capability forwarding.
- Deliberately changing status routing from Query to Subject made its test fail.
  The unfixed safety decorator separately failed the new forwarding regression.
  Both were restored/fixed before the final green run and deployment.
- Android build: 0 errors, 101 warnings. Windows build: 0 errors, 49 warnings,
  built to `work/queue-windows-build` without replacing the running Desktop.
- The full test graph still has the separate browser-wasm framework-reference
  blocker. Existing dependency security/native-library warnings remain.
- Reboot/process-death recovery of an accepted service ask, device cancellation,
  fresh large downloads, foreground-service endurance and general answer/tool
  accuracy were not established by these two questions.
- Windows standalone service remains 3.8.1. StarDust was disconnected during the
  phone acceptance; this does not add WearOS local inference or prove physical
  hand recognition.

Reproduce the focused tests using the adjacent readiness harness template. Local
logs: `work/2026-10-23-service-queue-tests.log`,
`work/2026-10-23-service-queue-red.log`,
`work/2026-10-23-preparation-forwarding-red.log`,
`work/2026-10-23-service-queue-android-build.log`,
`work/2026-10-23-service-queue-windows-build.log`, and the two `*-ask-trace.log` files.
