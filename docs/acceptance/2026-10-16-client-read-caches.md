# Client read caches — scope and verification

## Ownership

Concierge caches presentation data. Circle AI still owns model selection, weights, model loading, inference and its native prefix/KV caches. No prompt or generated answer is cached or substituted by these changes. No service repository was accessed or changed.

## Cache contract

| Data | Lifetime | Retained entries | Content budget |
| --- | --- | --- | --- |
| Conversation view | 1 second | 16 | 524,288 weight units |
| Conversation lists, keyed by owner | 1 second | 8 | 65,536 weight units |
| Active/all project and Start-Up catalogues | 1 second | 3 | 524,288 serialized UTF-8 bytes |
| Android service reports | Per verb below | 12 | 65,536 weight units |

Conversation and service weights include character counts and fixed overhead allowances. They are eviction weights, **not measured process RAM or a byte-accurate heap limit**. Large snapshots bypass retention. Expired entries are pruned on subsequent reads/metrics inspection; no timer or separate host process is added.

Android report lifetimes: Capabilities 30 seconds; Abilities, SetupPlan, SetupCensus and Footprint 5 seconds; Permissions 2 seconds; SetupReadiness, SetupProgress and ResidentStatus 1 second. Only parameter-free reports enter this cache. Queries, chat and mutation verbs do not.

Concurrent readers share an outstanding load. Cancelling a reader stops that reader waiting, without cancelling other readers' load. Failed reads are not retained. Invalidation prevents an older in-flight read from repopulating the cache, though its original waiting caller can still receive that original read.

Conversation and project writes invalidate before and after the operation, including failed writes. Durable events, model history, project execution reads, revisions and agent events stay live. Model-run conversation reads bypass presentation snapshots. Out-of-process database writes that bypass the decorators become visible after the one-second lifetime; this is bounded staleness, not a cross-process invalidation bus.

Android service errors, connection approval, settings changes and Activity resume invalidate service reports. Explicit **Refresh status** clears reports before reading. Debug timing logs contain durations and counters, not prompts or credentials: queue/total (`Concierge.CircleAI.IPC`), bind/service transaction (`Concierge.CircleAI.Transport`), cache counters (`Concierge.CircleAI.Cache`). The service transaction duration includes service work and is not pure inference time.

Cold database reads run on the thread pool instead of blocking the render thread. Workspace restoration now has an explicit `aria-busy` state. Text, attachments, voice and Send wait for saved session/thread restoration, including manual Desktop transfer. This is a data-restoration gate, **not a wait for the AI engine to finish model setup**: a person can still save a draft before Circle AI is ready. A component test pauses the actual SQLite conversation query, checks the busy/disabled state, releases the query and checks that input becomes available. Project catalogue loading also exposes its existing loading state through `aria-busy`.

Component fixtures wait for restoration rather than the sidebar's first render. Project tests await the whole async click/change handler on the renderer; a partial render is not evidence that a write, refresh or cancellation has finished.

## Measurements already recorded

On October 15, a Windows automated test read a real SQLite conversation containing 40 messages of 200 characters each. SQLite/EF was warmed first. The uncached route performed 100 reads in **197.6253 ms**. The snapshot route's first load took **2.1382 ms**, followed by 100 warm reads in **1.7170 ms** (one source load, 100 cache hits). These are controlled client-data measurements, not phone, end-to-end answer latency, or an accuracy improvement. `SnapshotCacheTests.Measure_repeated_real_sqlite_reads_against_warm_snapshots` reproduces the comparison without asserting a machine-dependent speed threshold.

Before deployment, PixieDust's existing app completed three explicit **Refresh status** operations in **553.2, 336.2 and 343.5 ms**. CDP clicked the real button, waited 50 ms, then polled every 20 ms until the button was enabled again. These samples include rendering and polling granularity. An after-deployment comparison is still required.

The final October 16 full-suite run measured the same 100 conversation reads at **237.8724 ms** from SQLite and **5.9513 ms** warm (first cache load **8.5066 ms**). A catalogue of 10 projects took **170.9787 ms** for 100 source reads and **31.1905 ms** for 100 warm catalogue reads. Both caches recorded one source load and 100 hits. Timings vary with machine load; the tests assert correct source-call counts and returned data, not a speed threshold. The recorded counters, timings and failure messages are in [the measurement snapshot](2026-10-16-client-cache-measurements.json).

## Automated checks and remaining failures

The full run in `client-cache-full-final-4.trx` executed **2,602 tests: 2,600 passed, 2 failed**, with no skipped tests. This includes the uncommitted compaction/tool-discovery work already present in this checkout. Cache and UI checks cover expiration, eviction budgets, failed reads, oversized values, concurrent loads, cancellation, mutation isolation, owner separation, failed writes, sync invalidation, live model history, live project revisions, explicit Refresh invalidation and repeated prompts reaching the service adapter. The query-handoff test holds a fake answer open and verifies the composer becomes usable after restoration, before that answer finishes. These are unit/component checks with controlled dependencies, not a running-device proof.

The two failures also reproduce in a separate two-test run (`client-cache-sdk-check.trx`):

1. `CircleAiModelSelectionTests.CatalogueSelectionAccountsForMnnMappingModeAndOffersBestCompatibleModel`: the test expects `Qwen3-30B-A3B-MNN`; the SDK-backed selector returns `Qwen3-14B-MNN` for its declared test probe. This is a model-selection expectation mismatch, not proof that a particular model belongs on PixieDust.
2. `CircleAiBundleDownloadRecoveryTests.Cancelled_bundle_download_resumes_saved_bytes_after_service_recreation`: the direct `ModelDownloadService.EnsureBundleAsync` test loses the partial file after cancellation and cannot exercise its intended resume. The fixture uses 3 MiB of synthetic bytes and a fake HTTP handler; it does not download a real model.

Neither check calls the new read caches. Their assertions remain intact. They are unresolved; the full suite is **not green**. A direct SDK check also does not establish the installed service's end-to-end behavior. Fixing or updating service/SDK behavior requires work from its owner or an updated published build; this task has not accessed a Circle AI repository or copied model-selection/download ownership back into Concierge.

## Build and deployment state

Android Debug build completed October 15: **0 errors, 162 warnings**, elapsed **7m 04.56s**. Signed APK size: **185,646,276 bytes**. Warnings include existing vulnerable dependencies and duplicate native libraries; this work does not clear them.

**The user released deployment on October 16.** The new APK was installed on AngelDust; PixieDust installation remains blocked. Investigation on October 16 confirmed that TCP connectivity and the ADB command were correct; TLS failed with `CERTIFICATE_UNKNOWN`. Visual Studio's SDK ADB loaded the existing host key with a fingerprint matching the key file, and the saved known-host records contained PixieDust's exact mDNS identity. The user identified their script in another session as the cause. No keys were changed or pairing records removed. The temporary diagnostic ADB server was stopped.

The final Windows build succeeded with **0 errors, 79 warnings**, elapsed **1m 05.08s**, using `-p:OutDir=C:/Development/repos/circle-concierge/work/cache-windows-build/`. The final Android build succeeded with **0 errors, 171 warnings**, elapsed **2m 44.29s**. These warnings include dependency vulnerabilities, duplicate native libraries and native page-size compatibility; they remain unresolved. The APK size/hash and both build results are recorded in the measurement snapshot. Desktop was restarted with the final build and exercised through real conversations and project writes. AngelDust accepted the APK and its background client reported a ready service with actual cache hits. Its visible conversation test remains pending because another app was foreground. PixieDust still rejects the ADB certificate. See [live deployment verification](2026-10-16-live-client-cache-verification.md): deployment and client-data checks do not establish answer accuracy, which failed on Desktop and in a direct service control request.

## Remaining device acceptance

Once PixieDust connectivity is restored, install there and complete the following. Desktop navigation, project write/reopen/archive and repeated service calls have been exercised; mobile equivalents and the same-device refresh comparison remain outstanding:

1. Open and refresh service Settings; compare the same UI-timed refresh operation with the recorded baseline and inspect actual cache/transport counters. Explicit Refresh must read current reports.
2. Navigate away/back through conversations and project catalogues; confirm restored drafts and current messages survive writes and Desktop sync. Check that the app remains usable during cold reads and service work.
3. Send a new prompt through the local service, then repeat it. Verify two actual service calls and durable replies, not reused cached answers. Record client queue/bind/transaction time separately from native generation timing where the service logs supply it.
4. Change service permissions/setup, resume Concierge and verify that old readiness is not presented as current. Record voice readiness independently from text.
5. Repeat the relevant running-app journey on Desktop. Do not claim improved inference latency, model selection, arithmetic accuracy, or native prefix-cache operation from these client-only changes.

## Known service limitations remain

The installed Android service previously answered `2 + 3` as `4` and logged `setPrefixCacheFile requires kvcache_mmap=true`. Its setup/housekeeping selection audit is documented separately. Client read caching neither resolves nor masks these engine issues. Voice permission/setup is also not established by successful text replies.

