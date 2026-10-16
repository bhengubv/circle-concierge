# Live client-cache deployment verification — October 16, 2026

## Result: partial; answer accuracy failed

The user released deployment. Desktop now runs commit `83d1bfe` from the separate Windows build output. The signed Android APK installed successfully on AngelDust. PixieDust remains blocked by ADB TLS certificate rejection despite a reachable TCP endpoint. No pairing records or keys were removed.

| Check | Observed result |
| --- | --- |
| Desktop conversation restoration | Actual history links restored 5 messages, then the test thread's 4 messages, with input enabled. Observed UI transitions: 18.2 ms and 16.6 ms. These are restoration samples, not a before/after cache comparison. |
| Desktop project writes | Created a temporary project, added a workflow, reopened revision 2 with the saved workflow, then archived only that test project. |
| Repeated prompts reach the service | Two distinct durable runs and updated service prompt diagnostics; no answer reuse. Run durations derived from durable start/end events: 16,778 ms and 32,447 ms. |
| UI during inference | Composer remained enabled; Stop was available. The repeat completed rendering after 32,582.4 ms from the observed click. |
| Desktop answer accuracy | **Failed**, including a minimal direct service request without a system message. |
| AngelDust installation | ADB reported Success. Installed app launched; its background WebView reported the service connected and conversation ready. |
| AngelDust cache activity | Device log at 16:33:11.013 recorded 2 hits, 13 loads, 7 retained entries and 6,991 content-weight units. Weight is not measured RAM. |
| AngelDust visible/local-response test | **Unverified.** Another app occupied the foreground. Awaiting confirmation that the device is available. Reported readiness does not prove voice or inference works. |
| PixieDust deployment | **Blocked:** `192.168.0.140:35035` is TCP-reachable but ADB returns `SSLV3_ALERT_CERTIFICATE_UNKNOWN`. No APK installed this round. |

## Accuracy control

Question: “My meeting starts at 10:00. Travel takes 20 minutes. What time should I leave to arrive on time? Reply briefly.” Expected answer: **09:40**.

Concierge received invented airport/flight advice on both attempts. A direct request to the same Windows service, with one user message and no system message, instead recommended 09:00–09:30. This establishes that the failure also occurs without Concierge's added system context. It does **not** identify whether weights, tokenization, runtime, or another service component caused it.

Windows service reports version **3.8.1.0** and model **Qwen3.5-0.8B-MNN**. AngelDust's installed service package reports **versionName 0.1, versionCode 56**; these are separate measurements, not an assertion that both installations match. No service repository, model-selection ownership, model files, or service settings were changed during this verification.

[Machine-readable observations and exact replies](2026-10-16-live-client-cache-verification.json) accompany the [Desktop screenshot](2026-10-16-desktop-cache-service-reply.png). The screenshot documents the incorrect answer, not a successful accuracy test.

## Outstanding

- Complete visible mobile service interaction when a device is available; verify local replies and explicit Refresh invalidation.
- Repeat PixieDust's original refresh measurement on that same device after installation. AngelDust's timings cannot stand in for it.
- Verify mobile restoration, project writes, sync invalidation and permission/resume behavior. Voice remains separately unverified.
- Resolve the service accuracy failure through its published contract/build or its owner; do not conceal it with cached responses or client model selection.
- The two previously recorded automated failures remain unresolved; deployment does not make the suite green. See [cache contract and build evidence](2026-10-16-client-read-caches.md).

No new resource downloads were started. The temporary project was archived after verification. The earlier measurement JSON remains a historical pre-deployment snapshot.
