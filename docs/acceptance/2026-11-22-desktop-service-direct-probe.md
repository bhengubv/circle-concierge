# Desktop Circle AI direct allowance probe — 22 November 2026

## Purpose

The live Concierge allowance task returned `$13.34` instead of `$14`. This probe removes the Concierge UI and conversation store to check the installed Circle AI service separately. It does **not** replay the exact live request bytes.

## Request and observations

- Target: the installed service's loopback URL from its protected `connection.json`; the credential was read in memory and not printed or recorded.
- Endpoint: `POST /butler/chat` with `temperature: 0`, a short system instruction to calculate accurately, and one user turn containing all three allowance facts and the requested calculation.
- The first attempt was rejected by this shell's network sandbox before reaching the service. Local network access was then granted and the same probe was retried.
- The retry returned no answer before the HTTP client's **240-second timeout**. Measured elapsed time: **240.12 seconds**. The request was canceled by that timeout.
- An immediate authenticated `GET /v1/status` succeeded. It reported schema `1`, model `Qwen3.5-4B-MNN`, brain phase `Installed`, `canAnswerNow: false`, `canAnswerSoon: true`, and no `recentFailures`. The detail said the brain loads when asked.
- A second direct request sent the same one-turn facts to **`POST /butler/stream`**, the endpoint Concierge uses. It returned HTTP `200` and an SSE `done` event after **278.79 seconds**. Its answer gave `$2` per chore for the first child, `$1.60` for the second, `$6` and `$8` payouts, and **`$14` total**. The response also contained raw TeX-style currency/math syntax, so display formatting still needs client handling.

## Interpretation and limit

The installed service did not complete the non-streaming request within four minutes. The streaming path eventually returned the **correct** calculation, but only after more than four and a half minutes. Concierge's failed live run contained prior assistant turns and its full identity prompt; this one-turn probe did not. Thus the service can solve the isolated problem, but this result does not establish whether the failed multi-turn result came from prior incorrect assistant text, Concierge's longer prompt, or another service effect. The status response gave no queue, load, or inference breakdown. No Circle AI source or installation was changed.

Next: obtain a service-owned request receipt or redacted trace for the streaming allowance run, including the selected model, queue/preparation/inference timings, and received role/content sequence. Compare its answer with Concierge's displayed answer before assigning the arithmetic defect to a component.

## Client display follow-up

The streaming answer's dollar-delimited TeX-style values prompted a focused bUnit regression. It failed before the renderer change and passed after the renderer converted simple values and arithmetic to readable text while preserving ordinary `$8 and $9` prose and fenced code. The complete Concierge test gate passed **2,744/2,744**, 0 failed and 0 skipped. The final report measured 75.71% line, 65.55% branch, and 80.4% method coverage across the assemblies it instrumented; several listed assemblies had zero instrumentation. This is a source/test result, not a live visual recheck of the rebuilt Desktop app.
