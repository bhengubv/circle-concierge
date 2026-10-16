# AngelDust local-service comparison — October 16, 2026

## Outcome

**PixieDust's timeout/model-refusal sequence was not reproduced in this short AngelDust test. Local answer accuracy still failed.** The installations are different: AngelDust has Android 16 and service versionName 0.1/build 56; PixieDust's tested service was build 58. This is not a controlled service-version comparison.

| Check | AngelDust observation |
| --- | --- |
| Connection/readiness | Concierge Settings reported connected, conversation Ready and all listed setup resources Present. Voice was not exercised. |
| Cold request | A fresh conversation received the meeting/travel question. The request failed while the service loaded Qwen3.5-2B-MNN. The service subsequently logged “brain ready” at 19:07:58.246 JST. |
| Warm retry | The same question returned **7:55 PM**. The correct answer is **09:40**. |
| Timing | Retry click 19:08:25.536 to observed reply 19:08:56.398: **30,862 ms**. Native logs recorded 636 prompt tokens, 58 ms tokenization, 29,166 ms prefill, 692 ms decode and 29,916 ms total. Client chat IPC: 30,180.82 ms. These measure different boundaries; none is a before/after performance comparison. |
| Crash/refusal | Service PID remained **4012**. No new crash entry or model-refusal message was observed during this test. Android's newest recorded service exit still dated October 13. |
| Timeout risk | The running service still advertises foreground type **dataSync**, `0x00000001`. A short successful generation does not prove it can survive the long-duration Android quota. No quota was changed or long-duration timeout induced. |
| Background/return | Another app became foreground during the retry. Concierge's background WebView received the reply; later the activity was observed foreground again, displaying the same reply with input enabled. |
| Desktop | Windows inference diagnostics stayed unchanged. Desktop's conversation endpoint showed only the initial failed run's 3 events at the final read; synchronization of the retry and reply is **not verified**. |

The automatic approval review rejected an attempted foreground-switch command with “blocked by policy,” with no specific reason. That command was not retried. Concierge later became foreground independently; its visible state was then read and captured. No service source, model files, service settings, or Android quota settings were changed. No resource download was started.

## Conclusion

AngelDust can generate a local response after model warm-up, unlike PixieDust's observed refusal state. **It did not generate a correct answer.** The first-request warm-up failure, misleading Ready report before answering is possible, and incorrect response remain relevant defects. The shared dataSync lifecycle remains a risk requiring service-owner investigation; this observation does not prove the timeout occurs on AngelDust or that changing build version fixes it.

[Exact measurements](2026-10-16-angeldust-service-comparison.json), [filtered service timings](2026-10-16-angeldust-local-service.txt), and [visible reply](2026-10-16-angeldust-local-answer.png) accompany this report. Compare with the [PixieDust test](2026-10-16-live-client-cache-verification.md).
