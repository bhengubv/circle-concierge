# AngelDust: fresh service installation and use through Concierge — October 23, 2026

## Outcome: first use did not produce an answer

Service build 60 / release APK 3.9.1 is installed. The person reports no permission requests at installation. The published APK has no MAIN/LAUNCHER activity (`cmd package resolve-activity` returned No activity found). Installation alone was not evidence of a completed first-use flow.

The old service was removed through Android App info / Uninstall; Keep app data was observed unchecked. The APK was placed in Downloads and opened through the native Files/installer flow. The person completed the update. Package records subsequently showed firstInstallTime 00:42:20 and lastUpdateTime 00:45:28 JST. No backup was made in this operation. The installed Concierge remains the October 16 build; the tentative client SDK/setup changes in the worktree were not deployed for this test.

## Observed request

- The person approved the Circle AI connection with their screen-lock pattern. Native logs record GRANTED to com.companyname.concierge at **00:47:38.001 JST**. A reconnect at **00:48:25.855** returned the existing approval.
- The exact meeting/travel question used on PixieDust was submitted from Concierge's actual composer at **00:49:31.253 JST** in conversation **ed4418f7-7e35-4f6d-9f59-6656e2afcd3c**.
- Service process **15412** accepted request **80bc287a3bde4a4ea7bcb412b8eba809** at **00:49:31.866**, Foreground lane, 111 characters.
- The supervisor reported that its selected **Qwen3.5-2B-MNN** was not installed and no alternative was installed. The request waited with **NotInstalled**, with repeated checks about every 30 seconds. No inference result returned.
- Concierge initially showed thinking/working. By **00:51:50.817**, it showed a failed run and “Circle AI service could not answer on this device.” Its chat transport uses a two-minute caller timeout. The service was still recording waits after submission; this is not proof that its accepted request was cancelled when the client timed out.
- No resource setup/download approval prompt appeared during this attempt. The fresh use therefore differs materially from PixieDust's in-place update, which retained a usable model and returned a correct answer.

[Native request/approval evidence](2026-10-23-angeldust-first-use-service.txt) records the accepted request and missing-model waits. Old saved answers, including 7:55 PM, were not treated as answers to this test. The question was not resubmitted automatically.

## Related earlier observations, kept separate

Before this reinstall, the Prepare button returned “not linked for Setup — approve in Circle AI first.” An observer captured that exact error before a progress poll overwrote it. Chat and Setup approvals are distinct in the published 3.9.1 SDK. Android also denied an automatic ModelFetchService foreground start at the earlier service startup; that denial preceded the Prepare-button attempt and was not established as its cause. Neither observation proves the current queued request ran inference.

No Circle AI source was accessed or edited. Correct local answering, automatic first-use setup, progress delivery, accepted-request recovery and voice remain unverified on this fresh installation. The first-use acceptance gate is not passed.

## Corrected follow-up: automatic download ran; warm retry passed

A broader inspection of **CircleAI.Fetch plus Android ActivityManager** logs corrected the earlier interpretation of NotInstalled waits. Missing resources at a moment in time did not establish that the downloader was inactive. The manual Setup permission rejection did not establish a block on the service's own automatic preparation.

- **00:42:23.438 JST:** Android denied the initial background ModelFetchService start.
- **00:45:29.553:** After the package update, Android explicitly allowed that service under its package-replacement allowance.
- **00:49:38.844:** SmolVLM download completed; the service started fetching the selected Qwen3.5-2B-MNN (1,386,691,327 planned bytes).
- **00:52:32.565:** Original question expired after a service-reported **181 seconds** waiting. Concierge had already reached its separate two-minute transport limit.
- **00:53:00.900:** Qwen3.5-2B-MNN completed downloading, about **28 seconds after** the service expired that request.
- **00:53:13.611:** The model was loaded in the separate inference process and reported ready. The larger Qwen3.5-4B tier was deferred pending Wi-Fi and a charger.
- **00:56:25.870:** After confirming the original request was terminal, one retry was accepted as request **051a4c3096594f0c84574112d40e1566**.
- **00:56:32.179:** Concierge visibly displayed **“Leave at 9:40 AM.”** The UI observer measured **6,432.7 ms** from click to reply. Service turn total was **5,962 ms**, with the local Qwen3.5-2B-MNN backend and finish=eos.

**Selection and automatic download worked following the update.** The original fresh-use test failed because the wait expired before preparation completed; the displayed progress/readiness did not explain the real state clearly. This was not the same as a permanently blocked downloader. The earlier report that automatic download had not started was incorrect. This follow-up supersedes that interpretation while retaining the initial request's actual failure.

No new Concierge binary was deployed for the successful retry. Client SDK/setup changes discussed earlier are uncommitted work, not the explanation for this passing answer. One correct warm answer does not close voice, multi-client, upgrade recovery or long-duration acceptance.

[Measured retry](2026-10-23-angeldust-ready-retry.json) and [automatic download/startup timeline](2026-10-23-angeldust-download-timeline.txt) preserve the correction.
