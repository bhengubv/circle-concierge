# Service transport follow-up — 25 October 2026

Scope: Desktop, PixieDust (primary phone), StarDust. Circle AI source and models are unchanged.

## Baseline

PixieDust conversation `3d989298-f84e-430e-9f49-c0370fd8ba69` asked:
“I have three apples and buy two more. How many apples do I have? Answer briefly.”

Durable start: 2026-10-24T15:26:56.010+09:00.
Durable completion: 2026-10-24T15:27:01.714+09:00.
Elapsed: 5.704 seconds. Correct answer: five apples. Local Circle AI Android service.

Transport logs showed repeated binds and service status long polls around 1005 ms,
with a further fixed 250 ms client pause between polls.

## Changes under verification

- Reuse one healthy Binder connection per Android client service instance.
- Invalidate metadata on disconnection; release failed bindings. No automatic replay of writes/questions.
- Dispose the binding only after the owned native transaction has finished.
- Count time spent in a long poll toward the 250 ms minimum polling interval.
- Include the published service's timing fields in debug request diagnostics (no question/answer text).

## Measured after the changes

PixieDust conversation `4c1004f1-4629-47b0-adb3-eb4b7313642d` used the same apples
prompt. The local model returned the correct answer.

- Durable run: 2026-10-25T13:54:09.756+09:00 to 13:54:15.385+09:00: **5.629 s**.
- User tap to durable completion: **8.131 s**; about 2.5 s elapsed before durable run start.
- Circle AI service reported 5.306 s total, 4.744 s prefill, 0.541 s decode,
  92 prompt tokens, and `KvPath=reset` (Qwen3.5-2B-MNN).
- Binder reuse reduced measured bind overhead from roughly 2–50 ms to 0.05–0.19 ms.

Desktop conversation `3eddee19-04e8-45db-aeb5-183c84a165d5` also returned the
correct answer using Qwen3.5-4B-MNN, served by Circle AI:

- Durable run: 2026-10-25T13:55:41.130+09:00 to 13:56:20.250+09:00: **39.120 s**.
- Prompt diagnostic: 456 estimated tokens, including 432 system tokens.
- There is no Circle AI service timing payload in the Desktop bridge result, so the
  39.120 s cannot be split into queue, prefill, and decode time from this evidence.

The two Android 1 s-poll runs took 5.834 s and 5.480 s; the 250 ms-poll run took
5.629 s. This small sample does **not** demonstrate faster inference. The shorter
poll can reduce the wait to observe a completed answer, at the cost of more status
requests. The dominant measured PixieDust time is service prefill, not Binder setup.

StarDust (`48081JEAYL006S`) was found via the existing mDNS advertisement and its
identity verified. The app opened and the audiogram launched Wear OS speech input.
That recognizer captured unrelated ambient words, which were discarded. A synthetic
keyboard submission ended with `request=1 result=Canceled keys=none`; no watch
message reached Desktop and no watch response time can be claimed. Logs show the
app reports double-pinch and wrist-turn as enabled, but an actual wrist gesture was
not generated or verified. The watch therefore remains unaccepted for input and
gesture delivery.

## Build and test evidence

- Android ARM64-targeted Debug build succeeded with 0 errors.
- APK installed on PixieDust: SHA-256
  `0DB2C5009DC6FAD1D41453AB973CD4EED82B10569BA12E8BCF84D5D557DF0106`.
- Focused queued-chat tests passed 12/12.
- The latest full test run did not pass: 46 of 2,637 tests failed under system
  memory pressure, including media/encoder worker startup errors. No unrelated
  processes were stopped to make the suite pass.

Desktop already reuses its HTTP client and reads streamed HTTP answers. Android
still displays the final answer; partial-text UI integration remains pending.
Service-owned session/prefix-cache behavior has not been changed or verified.
