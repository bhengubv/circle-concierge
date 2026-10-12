# Desktop inference investigation — 11–12 October 2026

## Scope and result

This advances runtime diagnostics, worker budgeting, and reply identity. Desktop
Chat acceptance still fails: no correct, timely answer was observed. No vision
coverage row is closed by this investigation.

The running engine selected Qwen3-14B-MNN and reported runtime version 3.6.0.
Production package references were not upgraded during this investigation.

## Measured running-app evidence

All requests used `What is 6 plus 7? Answer briefly.` Each had a distinct request
and conversation identity; none was retried automatically.

| Path | Conversation | Model input | Outcome |
|---|---|---|---|
| Desktop composer, original budget | `99cc62c6-14a0-4a46-944c-b0b733f6b23c` | 15,456 UTF-16 units, of which 15,423 were system content | No output at the recorded 98.6-second live sample; later explicitly stopped. |
| Away diagnostic, catalogue omitted, original budget | `fdbcb4b8-872e-4911-8e27-6c91ac35eb53` | 1,384 units, of which 1,351 were system content | HTTP observation timed out at 180 seconds. The accepted work continued; explicit cancellation later produced a terminal receipt. TurnStart-to-TurnEnd was 297.946 seconds, with no assistant message. |
| Desktop composer, corrected budget | `f1e86ee6-e7c7-4dc9-b004-a17e258871c8` | 15,456 units, of which 15,423 were system content | No output at 321.4 seconds. Explicit Stop produced a cancelled receipt at 411.386 seconds. |

Navigating from Chat to Engineering kept the originating run alive. Returning to
its conversation exposed Stop. The confirmed stop replaced the stale “Stopping
this run…” hint with “Run stopped. Your work is saved.” The saved conversation
retained the request and cancelled receipt.

The original live worker sample showed an 8.3 GB resident limit. A corrected
sample showed a 16.7 GB resident limit, 17 GB commit limit, and 9.2 GB resident
usage. These are observations of different instants, not a controlled latency
improvement claim. The remaining delay is not explained solely by catalogue size
or by the erroneous cap calculation.

## Defects corrected

- A stream completion defaults `Ready` to false. The parent previously treated it
  as runtime readiness and briefly advertised a loaded model as unavailable.
  Readiness now changes only with runtime/setup state, not ordinary reply completion.
- Worker budgeting previously used system-free RAM as an absolute worker cap.
  Since free RAM excludes the worker's own allocation, model loading tightened its
  own cap. Live budgeting now includes the owned worker's resident allocation;
  external application pressure still tightens the ceiling. Startup and physical
  ceilings remain enforced.
- The parent previously delivered every chunk/error/completion to its current
  reply regardless of request ID. Setup/download completion or another request's
  output can no longer enter or prematurely close that reply.
- Host Manager now shows latest-request input sizes, elapsed time, time to first
  output, output size, and terminal state. This stores counts and timings, not
  prompt or answer content. “First output” measures the first content chunk seen
  by the parent, not a native-token event or internal reasoning activity.

Regression checks for readiness, owned-memory accounting, and reply correlation
were deliberately made red, then restored. Automated coverage does not establish
native correctness, timely answers, or the full Desktop journey.

The final exact-source full suite passed 2,494/2,494 with coverage collection
disabled. Windows Debug built with zero warnings/errors and was relaunched from
its normal output. Android Debug packaged with 17 warnings and zero errors before
the final Windows-parent reply-correlation change; that APK was not deployed in
this investigation. Build servers were shut down. This does not constitute a new
Mobile or Wearable acceptance trace.

## New SDK compatibility probe

A temporary project compiled the production `Concierge.Ai` source against the
merged CircleAI 3.8.1 package and the built shared assembly: zero compile errors,
13 warnings. This is a compile probe, not a restored production dependency graph
or a running 3.8.1 Concierge engine. Warnings include dependency advisories and
the obsolete `WhisperTranscriber` API marked unsafe by the SDK. Voice migration
and dependency remediation must be verified before treating an upgrade as complete.

## Evidence location

Local evidence lives under
`C:\Users\user\AppData\Local\Temp\ConciergeDeviceProof`:

- `inference-measurement-live.txt` and `.png`: original composer measurements.
- `compact-context-request.json`, `compact-context-events.json`, and
  `compact-context-measurement.txt`: reduced-context experiment and cancellation.
- `budget-after-live.txt`, `budget-after-events.json`, and
  `budget-after-capabilities.json`: corrected budget and terminal request state.
- `inference-budget-final-ui.json` and `.png`: confirmed-stop UI.
- `readiness-red.log`, `worker-budget-red.log`, `reply-correlation-red.log`: checks
  failing when their respective defects are present.
- `sdk381-compatibility/full-adapter-build.log`: isolated SDK compile result.

Next acceptance work: establish a suitable engine execution path and observe a
correct answer in ordinary Desktop Chat; then repeat action controls, recovery,
and route → execute → sync on the physical clients. Preserve these failed traces
and do not replace them with successful unit-test counts.
