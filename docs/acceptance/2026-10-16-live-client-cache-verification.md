# Live client-cache deployment verification — October 16, 2026

## Result: partial; answer accuracy failed

The user released deployment. Desktop now runs commit `83d1bfe` from the separate Windows build output. The signed Android APK installed successfully on AngelDust. PixieDust subsequently reconnected and accepted the same APK. No pairing records or keys were removed. Its live local conversation test failed in the installed service, as detailed below.

| Check | Observed result |
| --- | --- |
| Desktop conversation restoration | Actual history links restored 5 messages, then the test thread's 4 messages, with input enabled. Observed UI transitions: 18.2 ms and 16.6 ms. These are restoration samples, not a before/after cache comparison. |
| Desktop project writes | Created a temporary project, added a workflow, reopened revision 2 with the saved workflow, then archived only that test project. |
| Repeated prompts reach the service | Two distinct durable runs and updated service prompt diagnostics; no answer reuse. Run durations derived from durable start/end events: 16,778 ms and 32,447 ms. |
| UI during inference | Composer remained enabled; Stop was available. The repeat completed rendering after 32,582.4 ms from the observed click. |
| Desktop answer accuracy | **Failed**, including a minimal direct service request without a system message. |
| AngelDust installation | ADB reported Success. Installed app launched; its background WebView reported the service connected and conversation ready. |
| AngelDust cache activity | Device log at 16:33:11.013 recorded 2 hits, 13 loads, 7 retained entries and 6,991 content-weight units. Weight is not measured RAM. |
| AngelDust visible/local-response test | Follow-up completed at 19:06–19:12 JST: cold request failed while warming; retry returned incorrect **7:55 PM** instead of **09:40**. No new timeout/refusal observed in that short test. See [AngelDust comparison](2026-10-16-angeldust-service-comparison.md); voice and final reply sync remain unverified. |
| PixieDust deployment | **Installed successfully** after its existing mDNS transport recovered. Device last-update time: 16:54:57; APK SHA-256 matches the recorded build. |

## Accuracy control

Question: “My meeting starts at 10:00. Travel takes 20 minutes. What time should I leave to arrive on time? Reply briefly.” Expected answer: **09:40**.

Concierge received invented airport/flight advice on both attempts. A direct request to the same Windows service, with one user message and no system message, instead recommended 09:00–09:30. This establishes that the failure also occurs without Concierge's added system context. It does **not** identify whether weights, tokenization, runtime, or another service component caused it.

Windows service reports version **3.8.1.0** and model **Qwen3.5-0.8B-MNN**. AngelDust's installed service package reports **versionName 0.1, versionCode 56**; these are separate measurements, not an assertion that both installations match. No service repository, model-selection ownership, model files, or service settings were changed during this verification.

[Machine-readable observations and exact replies](2026-10-16-live-client-cache-verification.json) accompany the [Desktop screenshot](2026-10-16-desktop-cache-service-reply.png). The screenshot documents the incorrect answer, not a successful accuracy test.

## Outstanding

- Mobile service interactions are now recorded for both phones. Correct local answering is not accepted; AngelDust generated a wrong answer, while PixieDust failed. AngelDust final reply sync and voice remain unverified.
- PixieDust refresh comparison is now recorded below; no refresh-speed improvement was established.
- PixieDust history/draft restoration and phone-to-Desktop event sync were verified below. Mobile project writes, permission/resume behavior and voice remain unverified.
- Resolve the service accuracy failure through its published contract/build or its owner; do not conceal it with cached responses or client model selection.
- The two previously recorded automated failures remain unresolved; deployment does not make the suite green. See [cache contract and build evidence](2026-10-16-client-read-caches.md).

No new resource downloads were started. The temporary project was archived after verification. The earlier measurement JSON remains a historical pre-deployment snapshot.

## PixieDust follow-up: deployed, local answering failed

The existing mDNS connection became available. Visual Studio SDK ADB installed the verified signed APK with `--no-incremental -r` and reported **Success**. Concierge process 21774 was brought to the foreground; the installed service reports versionName **0.1**, versionCode **58**.

### Controls, cache and restoration

- Settings opened visibly. **Refresh status** completed three actual UI operations in **349.1, 487.3 and 359.3 ms**, using the same 50 ms initial delay and 20 ms polling method as the original PixieDust samples. Earlier samples were **553.2, 336.2 and 343.5 ms**. Median changed from **343.5 to 359.3 ms**; this does **not** establish an improvement. Explicit Refresh deliberately invalidates cached reports.
- Transport/cache logs showed fresh metadata requests and cache hits. At 16:56:05.836 the counters were **9 hits, 51 loads, 7 entries, 7,090 weight units**. These are cumulative counters, not a hit rate for the three refreshes alone or measured RAM.
- **Reconnect Circle AI** completed and re-read reports. The service continued to report text Ready despite failed requests. A successful connection/readiness report is not a successful answer.
- An actual history link restored an existing conversation with **5 messages** and enabled input. Returning restored the new test conversation's **2 user messages** and its exact unsent synthetic draft. The test draft was then cleared; it was never sent.

### Two requests, no local answer

Conversation `58d2c8f1-05ad-44d2-a062-dc742c18ff02` received the same meeting/travel question twice. Both durable runs ended **failed**, with `AnsweredBy` identifying **Circle AI Android service**. The local chat IPC calls were recorded separately; there was no cached answer. No assistant message returned, so mobile answer accuracy cannot be evaluated.

The observed failure sequence is specific:

1. First request at **16:56:53** returned “brain warming up, try again shortly.” The service began loading **Qwen3.5-2B-MNN**.
2. At **16:57:03.712**, Android terminated service process **2360** with `ForegroundServiceDidNotStopInTimeException`: its `dataSync` foreground service did not stop within the time limit. Android's exit record classifies this as **APP CRASH (EXCEPTION)**. This is not evidence of an out-of-memory termination or proof the model caused the crash.
3. Restarted process **21987** logged that the foreground time limit was already exhausted and foreground status was refused.
4. After using Concierge's Reconnect control, the second request at **16:59:00** also failed. The service logged that **Qwen3.5-2B-MNN** was refused because the previous load had stopped the process and that no alternative was installed. That is the service's recorded crash-protection decision, not a client model-selection decision.

All **6 durable events** from the two runs appeared on Desktop. Desktop's prompt diagnostic was identical before and after the retry, still timestamped 16:29:44.7139979 with the earlier Desktop test. This supports local service execution followed by event sync, with no observed Desktop inference fallback during the retry. It does not demonstrate a successful phone reply or two-way sync under every condition.

[Visible Settings](2026-10-16-pixiedust-service-settings.png), [failed local run and restored draft](2026-10-16-pixiedust-failed-local-run.png), and [service crash log](2026-10-16-pixiedust-service-crash.txt) preserve the observed state. No model files, crash-protection state or Android limits were changed to make the test pass. The installed service needs its foreground-lifecycle and recovery behavior addressed through its owner or a published build; its repository remains outside this task's allowed scope.
