# PixieDust: Circle AI service 3.9.1 — October 22, 2026

## Result

The existing mDNS connection returned. Visual Studio SDK ADB installed the published **CircleAIService-3.9.1-vc60.apk** in place with `--no-incremental -r`: **Success**. Android reports versionCode **60**, last update **22:52:49 JST**. No uninstall or data clear was performed; existing Concierge conversations remained visible.

The APK SHA-256 matched the GitHub asset digest: `b6109069220bf8852d500d70d3da89f3e126cd30856af4d1fec6a6140afdee7b`. The signing certificate matched the prior release installation. [Published release](https://github.com/bhengubv/CircleAI/releases/tag/v3.9.1).

Concierge connected through its existing service adapter. Service process **22029** logged a supervised **Qwen3.5-2B-MNN** load, with the brain ready after **13,719 ms**. No model setup/download action was requested. Settings reported text Ready. It also reported voice resources Present while Talking/Listening/Waking abilities said TooBig; voice readiness is therefore not accepted by this test.

## Actual local answer

Fresh Concierge conversation: `51c1304a-d6c4-4ef5-a9b3-4a914947a3ef`.

Question: “My meeting starts at 10:00. Travel takes 20 minutes. What time should I leave to arrive on time? Reply briefly.”

Observed reply: **“Leave at 9:40 AM.”** This passes this specific question.

The real composer sent the question at **22:56:07.973 JST**. The first captured assistant-reply observation measured **6,787.3 ms** using the browser's monotonic clock. This is one UI sample, not a latency percentile or a controlled performance comparison. A previously retained synthetic draft was identified and replaced only in the new test conversation; no real user draft was submitted.

Service request **86e17710e331490db7c7266323720421** was accepted on the Foreground lane. Logs identify inference worker process **22137**, local model **Qwen3.5-2B-MNN**, terminal finish `eos`, and service turn total **6,163 ms**. The client chat transaction recorded **6,349.15 ms**. These establish a real local service call rather than a saved reply or a Desktop answer. Native generation and service timing reports use different measurement boundaries; retain both rather than forcing their token counts/timings to agree.

[Machine-readable observation](2026-10-22-pixiedust-circleai-3.9.1.json) and [service timing evidence](2026-10-22-pixiedust-circleai-3.9.1-timings.txt) preserve the result. A subsequent DevTools screenshot request timed out, so no screenshot is claimed.

## Limits and remaining work

This verifies installation, connection and one correct local answer. It does not verify cold-request durability, multiple callers, restart recovery, voice, cross-device result sync, the old six-hour timeout case or long-duration stability. The service release notes are publisher claims until exercised here.

AngelDust remains on debug-signed build **56**. Android rejected the release-signed build 60 in-place update with `INSTALL_FAILED_UPDATE_INCOMPATIBLE`. Nothing was uninstalled or cleared. A safe backed-up migration is awaiting the person's decision; the same-build two-phone comparison remains incomplete.
