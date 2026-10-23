# Concierge: live Circle AI readiness

**Later acceptance:** the protocol-2 service queue and missing safety-wrapper
forwarding were connected and tested on both phones after this earlier record.
See [request-queue acceptance](2026-10-23-service-queue-acceptance.md). Queue
acceptance and loaded inference readiness are separate; the strict local wait
described below now applies to the legacy service path, not a queue-capable host.

## Change

Concierge consumes the published CircleAI 3.9.1 `Status` contract. Text readiness requires the live brain's `Ready` phase, not `SetupReadiness.canTalk`, an ability label, or downloaded files. Installed and loading are distinct. Speech, spoken replies, wake word, downloads and assistant role are reported separately; absent live reports remain **Not reported**.

An opt-in runtime preparation interface allows a question to wait in the active Concierge run while the service reports preparation. No inference request is sent during downloading or loading. When the service reports Ready, or Installed with on-demand loading available, Concierge sends once. This waiting has no two-minute inference deadline. It respects the existing run cancellation; it does not cancel the service's downloads. A fresh status check precedes every turn. The existing conversation/run log remains responsible for recovery; automatic continuation after app termination has not been demonstrated here.

Settings refreshes live facts alongside preparation progress, does not treat text readiness as completion of every download, and preserves setup errors across later polls. Explicit manual preparation requests the service's Setup grant. This does not change automatic download policy.

The service's legacy `TooBig` ability value is a blocked **resource-plan diagnostic**, not a user setting. Its meaning remains visible: the service reports a plan that does not fit. Concierge neither chooses another model nor offers an LLM picker. Correcting such a plan belongs to Circle AI's selector. This change does not claim that selector defect is fixed.

## Verification

- 47 targeted tests passed: live-state projection, feature independence, malformed/unknown reports, local waiting and single send after readiness, cancellation before dispatch, existing conversation identity, setup/connection UI behavior, and removal of a stale Ready summary when a live poll fails.
- The tests were compiled from the checked-in test files using the local `work/readiness-tests/readiness-tests.csproj` harness, referencing the actual Shared, Ai and Components projects. This avoids the unrelated WebAssembly project in the monolithic test graph.
- To reproduce that focused run, save the adjacent `2026-10-23-service-live-readiness-harness.csproj.txt` as `work/readiness-tests/readiness-tests.csproj`, then run `dotnet test work/readiness-tests/readiness-tests.csproj -p:UseSharedCompilation=false -m:1` from the repository root.
- Full `Concierge.Tests` build is **blocked** by `NETSDK1082`: the published CircleAI 3.9.1 dependency graph brings `Microsoft.AspNetCore.App` into `browser-wasm`. No full-suite pass is claimed.
- Android and Windows builds passed. Existing dependency vulnerability, nullable, native-library alignment and packaging warnings remain; no warning was suppressed to obtain these passes.
- Updated Concierge installed in place on AngelDust (USB `3B221JEHN16653`). Circle AI service remains 3.9.1 build 60. No service/model uninstall, wipe or manual model selection was performed for this change.
- Final APK SHA-256: `9197F6E55AF2735718EFA53C643DFB695150C50A206B44D76ACDBC6162C166F0`; the final revision was installed and launched at approximately 01:38 JST.

### Live Settings

At 01:26:53 JST, the deployed UI showed:

- Text conversations: **Ready**; service detail “Ready to answer.”
- Spoken replies, speech recognition, wake word: **Not reported**.
- Resource downloads: **Ready**; service detail “Nothing left to fetch.”
- Assistant role: the service says Circle AI is not the phone's assistant.

The first deployed revision labelled the assistant's Installed phase as “Downloaded; not loaded.” The final revision labels that role “Not selected” and removes the idle “nothing is being set up” error when live fetch status already confirms completion.

### Human question through the actual composer

Prompt: “My meeting starts at 10:00. Travel takes 20 minutes. What time should I leave to arrive on time? Reply briefly.”

- Submit: **01:28:15.484 JST**.
- Answer visible: **01:28:21.709 JST**.
- Reply: **“Leave at 9:40 AM.”**
- DOM observation elapsed: **6224.8 ms**.
- Service request: `309936980677434bba0dbb2c0de4b070`, accepted once at 01:28:15.867.
- Local model: **Qwen3.5-2B-MNN**; service total **5756 ms**, finished `eos`.

See the adjacent chat JSON and service trace for the measured values.

## Not established

The phone already had a ready text model. Waiting through a fresh multi-minute physical download, process death/resume, voice inference, background foreground-service quota exhaustion and reboot recovery were **not** re-tested. The local preparation behavior is covered by controlled tests; it is not a fresh-install device acceptance pass. The service does not currently provide live voice/ears/wake states in its Status response on AngelDust, so Concierge cannot honestly label those ready.
