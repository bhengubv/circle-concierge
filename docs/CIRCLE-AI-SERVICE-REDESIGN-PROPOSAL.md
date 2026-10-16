# Circle AI service: redesign and development proposal

**Status:** Proposed architecture and delivery gates, October 16, 2026. No Circle AI source changes have been made. This document is for the service owner; it does not grant access to that repository. Existing device observations and official platform contracts inform the proposal. They do not prove that the proposed design is implemented.

## 1. Product contract

Circle AI is an independent assistant layer shared by authorized applications and OS integrations. Concierge, B!, other applications, and trusted remote devices are callers. Closing one caller must not cancel work that Circle AI has durably accepted, expose another caller's context, or shut down an assistant required by another caller.

Circle AI owns capability readiness, model/resource preparation, inference, assistant memory where authorized, and execution-device selection. Clients own their experiences and their domain work records: Concierge still owns Projects, workflows, artifacts and its approval policy; Butler owns its operational execution. Service request IDs link these records without replacing them.

Availability means the assistant can be reached, accept or explain requests, and recover work. It does not mean every model stays in RAM or every CPU stays awake continuously.

## 2. Defects to address, with evidence boundaries

| Observation | Required response |
| --- | --- |
| PixieDust's dataSync service exceeded its Android allowance, crashed, and was refused foreground status on restart. | Replace permanent dataSync hosting; handle timeout/shutdown and recovery. |
| Recovery then refused Qwen3.5-2B-MNN after the interrupted load, leaving no eligible installed alternative. | Separate service termination from verified model/runtime failures; use specific failure reasons and recoverable quarantine. |
| Both phones reported conversation Ready before a cold request failed while loading a model. | Make readiness precise; retain accepted questions during preparation. |
| AngelDust returned 7:55 PM instead of 09:40; Windows also failed the question, including a direct service request without Concierge system text. | Investigate inference correctness separately from transport and uptime. Larger models and different service types are not demonstrated fixes. |
| AngelDust retry took 30,862 ms from click to reply; native prefill was 29,166 ms for a logged 636-token prompt. | Instrument and improve prefill/runtime behavior, prompt construction and cache correctness. Faster UI metadata reads do not solve this delay. |
| Android logged that prefix-cache use requires kvcache_mmap=true. | Advertise/use caching only when the active backend/configuration supports it; verify isolation and correctness. |
| The final AngelDust reply was visible locally but its Desktop sync was not verified; only the first failed run was present at the last Desktop read. | Verify the client sync path separately; service delivery receipts and resumable events should let clients reconcile without another inference. This does not establish a service-caused sync defect. |
| Published build 58 cannot update AngelDust build 56 because their signing certificates differ. | Standardize supported update identities and provide a verified data migration path. |
| A synthetic direct-SDK test loses a partial download after cancellation; another selector expectation differs from the SDK result. | Reproduce against the installed service before attributing these SDK-test failures to it; add the resulting cases to service acceptance. |

See [live client/service evidence](acceptance/2026-10-16-live-client-cache-verification.md), [AngelDust comparison](acceptance/2026-10-16-angeldust-service-comparison.md), and [quota/signature comparison](acceptance/2026-10-16-service-quota-comparison.md). The same-build OS comparison is still blocked, not complete.

## 3. Recommended shape

One versioned service contract, with platform-specific hosting and bounded workers:

```text
Concierge / B! / other authorized apps / OS assistant entry points
                         |
                 authenticated request API
                         |
         small coordinator + durable request/event store
                         |
        scheduler, permissions, model/device selection
             /               |                \
     inference workers   voice/audio work   resource preparation
                         |
             progress/results to the owning callers
```

These are responsibilities, not a requirement to create a process for every box. Start with a small coordinator and a separately supervised inference process where supported. Keep one bounded shared model pool, with isolated sessions, rather than one loaded model per application.

### Android: a real assistant integration

- For the Alexa/JARVIS-style voice entry point, implement the user-selected **VoiceInteractionService** path. Android documents that it keeps this selected service running and recommends keeping it lightweight, with heavier interaction work in a separate process. This is a suitable foundation for the assistant role, not a guarantee of unlimited compute or immunity from all OS restrictions. The adapter may be provided by Circle AI or an authorized assistant client such as B!; either way it calls the same independent service contract.
- Use Binder/AIDL or the existing supported IPC mechanism for app requests. Binding is a connection mechanism, not the whole execution-lifetime policy. Keep Binder/main-thread handlers short: persist/acknowledge, then do heavy work off-thread and deliver events asynchronously.
- Use microphone access only through an eligible voice/foreground path with consent; verify lock-screen, boot, role and permission transitions. Do not promise background hotword capability on unsupported devices.
- In Circle OS, integrate the assistant with OS startup, permissions and supervision deliberately, using the least privileges required. Measure that integration separately from an ordinary installed APK. Merely being installed on Circle OS does not confer system privileges.
- On stock Android, support the selected assistant role where granted. Without that role, offer the supported app-invoked voice/text experience and declare its limits. Do not imitate assistant privileges with restart loops or unrelated foreground-service types.
- Map genuine ongoing work to a qualifying Android API/service type. If continued execution is not currently permitted, retain it as paused with a recovery reason. Durable acceptance guarantees retention and honest state, not continuous execution despite an OS prohibition. Evaluate specialUse only for an eligible declared use case and distribution requirements, not as a blanket timeout bypass.
- Move model transfers to the appropriate platform transfer mechanism. User-initiated data-transfer jobs are a candidate for qualifying user-requested large transfers; use DownloadManager or scheduled work where appropriate to the transfer and Android version. WorkManager is not an unlimited alternative: its long-running foreground workers can exhaust job quotas on Android 16.

Sources: [VoiceInteractionService](https://developer.android.com/reference/android/service/voice/VoiceInteractionService), [bound services](https://developer.android.com/develop/background-work/services/bound-services), [foreground types](https://developer.android.com/develop/background-work/services/fgs/service-types), [background/microphone restrictions](https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start), [transfer jobs](https://developer.android.com/develop/background-work/background-tasks/uidt), [long-running worker limits](https://developer.android.com/develop/background-work/background-tasks/persistent/how-to/long-running).

### Windows and other heads

Keep Windows service hosting and its owned ProgramData storage, but apply the same durable contract, isolation, model-selection ownership and supervision. Heavy native inference should not be able to take down the request coordinator. Expose backend/platform differences as capability limits, not different product semantics. Wearable participation should reflect its actual resources and permissions; it need not host a phone-sized model to access the same work.

## 4. Durable requests and honest readiness

The API should provide capability/settings discovery, setup progress, submit, observe-after-cursor, read-result, cancellation and client authorization. Preserve existing entry points through adapters during migration; avoid a breaking rewrite just to rename APIs.

Each request includes a protocol version, authenticated caller/user identity, stable request ID, session ID, requested capability, supplied context, permissions/action policy and deadline where applicable. Caller identity is derived from authenticated transport, not trusted from a JSON field alone. Persist the request before returning an acceptance receipt.

Return explicit states: queued, preparing, running, waiting for permission, paused, completed, failed, cancelled. A rejected request is not an accepted request. Client disconnection is not cancellation. Allow a client to resume from an event cursor and retrieve the same terminal result. Retrying the same ID/payload must not dispatch another job; a changed payload under that identity is rejected. Record output events and terminal state before announcing completion.

Keep admission/readiness separate from connection: transport connected, feature installed, model loading, inference available, busy, permission missing and recovery needed. Include a monotonically advancing state revision/boot identity so cached client reports cannot silently survive service restart. Push changes and retain a snapshot read for reconnection. Missing voice resources must not prevent ready text work.

Accept a question into the durable queue while the model warms if admission is possible; show preparation and answer when ready. Do not call it failed merely because model preparation has not finished. Enforce queue limits, priorities, cancellation and deadlines; explain rejection or pausing when capacity is unavailable.

A durable execution ledger prevents concurrent duplicate dispatch. Arbitrary external side effects cannot be promised exactly once solely by writing a local ledger: use downstream idempotency/receipts where available; retain an outcome-unknown state for reconciliation instead of automatically repeating an ambiguous purchase/message/action. Respect the originating client's approval controls and ownership throughout recovery.

## 5. Recovery, resources and correctness

### Failure classification

Record whether an interruption was OS timeout, explicit user stop, process kill, low memory, permission loss, native crash, invalid model integrity, unsupported backend or request failure. Treat unexpected termination during a load as inconclusive until examined. Quarantine only a specific model/backend/configuration combination supported by evidence; apply backoff and safe reassessment. Do not delete models to clear an unrelated OS timeout. Persist accepted work before a callback occurs: Android can kill a process without allowing cleanup.

### Automatic preparation and resource control

Circle AI chooses a compatible model using current available memory, storage, accelerator support, thermal/power state and measured runtime behavior. Protect OS/client headroom; distinguish installed, validated, loaded and usable. Keep the smallest suitable usable tier available while preparing a better eligible tier, subject to quality and resource gates. Never demand an LLM choice from an ordinary user.

Every worker needs an owner, a bounded queue, resource/concurrency budget, activity/health reports and an idle reclamation policy. Share validated weights; isolate and bound per-session context/KV memory. Avoid repeatedly loading/unloading a model due to metadata polls. Avoid unlimited process spawning, wake locks, and download fan-out.

For downloads: checkpoint partial bytes atomically, validate server identity/ETag/range support, resume safely after restart, verify hashes before publishing a completed model, and replace active resources atomically. Keep known-good resources until their replacement is verified; clean obsolete assets only when no active request references them. Expose real bytes/stages to every permitted client.

### Answer quality and latency are independent release gates

Reproduce the exact failures with direct service calls and client calls. Inspect model graph/weight/tokenizer/template pairing, role formatting, thinking mode, generation settings, prompt boundaries, backend versions and cache compatibility. Compare cache-off/cold/warm behavior before claiming a cache improvement. Clear or key KV caches correctly across sessions, template/model revisions and caller isolation. Never reuse an answer simply because the question text matches.

Measure acceptance, queue delay, cold load, prompt size, prefill, first output, decode and delivery separately. Investigate AngelDust's measured prefill delay rather than attributing all 31 seconds to a slow UI. Fit the complete prompt including tool schemas/history and response headroom; make full tool definitions discoverable without removing needed abilities. Set performance targets by device class from these baselines, and publish measured percentiles separately from proposed targets.

Use regression suites for arithmetic/time questions, instruction adherence, tool requests, conversation context, grounding and refusal/uncertainty. Appropriate calculation tools belong to Circle AI's capability execution, not a Concierge patch that rewrites a bad answer. A lifecycle fix does not prove answer accuracy.

## 6. Multiple callers, voice and trusted devices

- Authenticate/authorize every caller and scope its sessions, permissions, tools, events and results. Share personal memory across apps only under an explicit user policy; keep family/work profiles distinct. Prevent one caller reading another's prompts or cancelling its work.
- Schedule fairly across apps. Prioritize an interactive voice turn without starving accepted background work. Arbitrate microphone/speech output centrally; support interruption, audio focus, cancellation and correct transcript/result attribution.
- Discover client-provided tools explicitly. Bind every invocation, approval and result to the caller/session/request; if a tool client disconnects, pause that operation rather than inventing success or silently granting permission.
- Local-first execution remains the default where resources and capabilities suffice. Circle AI owns automatic routing to a trusted stronger device when the local device fails or cannot meet requirements; honor manual transfer. Track a single execution owner and result identity. On uncertain remote status, reconcile rather than start a duplicate local/remote job.
- AetherNet or another transport may carry authenticated device messages, but transport alone does not solve request ownership, permissions, NAT reachability or recovery. Do not make a remote connection a prerequisite for ordinary local answers or silently send private context to another endpoint.
- Consuming apps render service-owned settings through a versioned schema: supported parameters, defaults, read-back values, consequences and permission requirements. Unsupported settings must be explicitly unavailable. Do not make users open a sample app to prepare or configure the service.

## 7. Development order and acceptance

Refactor incrementally behind the shared contract; no wholesale rewrite is required to start.

1. **Establish a reproducible baseline.** Fix signing/update compatibility and verify backup/restore. Record identical service/model/backend/configuration hashes on AngelDust and PixieDust; record Windows independently. Equalize or explicitly account for quota history. Current same-build comparison remains blocked.
2. **Stabilize admission and recovery.** Persist-before-acknowledgement, request identity, state/events, cold-model queuing, truthful readiness, specific failure reasons and clean timeout handling. Prove caller disconnect/reconnect, request retries, service kill/restart and no false model ban. This can make failures honest before the complete assistant host is ready.
3. **Implement the Android assistant hosting split.** Prove selected-assistant wake/voice behavior, inference isolation, real transfer handling, resource limits and unsupported-role fallback on stock Android and Circle OS. Preserve the contract on Windows.
4. **Pass correctness and performance gates.** Repeat exact failing questions through direct service, Concierge and B!, with model/configuration identity and timing. Require correct expected results on the regression set before acceptance. Measure warm/cold latency and resource behavior; do not hide wrong output with transport success.
5. **Prove shared operation and recovery.** Two authorized clients at once, caller isolation, missing voice resources with working text, interrupted downloads with verified resume, permission changes, low memory, device sleep, reboot, role revocation, manual transfer and single-owner remote recovery. Confirm client result sync separately from inference.
6. **Release and soak.** Run a proposed minimum 24-hour mixed-use device soak, including periods beyond six hours, and separate accelerated timeout/fault tests on test devices with settings restored afterward. Measure idle memory, battery/wake activity and queue behavior. Test service upgrade/restart during accepted work. Publish compatibility and migration notes, signed artifacts and hashes, and a tested rollback/data schema policy.

For each gate retain device/OS/build identity, request IDs and event cursors, expected/actual results, timings, screenshots where relevant, failure logs and explicit limits. Deliberately break recovery/idempotency/readiness in controlled tests and verify the checks fail. Green unit tests alone cannot close physical-device gates. A service owner should publish the candidate; this task can then consume and test it without accessing Circle AI source.

## Recommended first deliverable

A small candidate release that fixes cold-request retention, timeout classification, false model refusal and readiness, while introducing the durable multi-caller request contract. In parallel validate the selected-assistant host on both Android families. Follow with isolated workers/preparation and quality acceptance. Describe unfinished platform behavior honestly until measured. Do not declare the whole redesign successful because one request returned or one process stayed alive.
