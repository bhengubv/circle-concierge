# Concierge client efficiency — 24 October 2026

Only Concierge was changed. Circle AI owns models, inference and service lifetime.

## Before and after, observed through Desktop

Question: “I have three apples and buy two more. How many apples do I have? Answer briefly.”

| Run | Prompt characters reported by Concierge | Recorded run seconds | Answer |
|---|---:|---:|---|
| Original, `887c0e11-7c5d-40cb-87fd-d220d6fbcdd8` | 8008 | 215.009 | 5 |
| Original repeat, `20075e39-14f7-42ad-8fcd-b5595474b138` | 8008 | 239.494 | 5 |
| Compact prompt, `4d30dcfb-0c6e-4cd1-ad0d-d2bfbb22614a` | 1760 | 15.342 | 5 |

Times come from durable run-start and completion records returned by Desktop's
local diagnostic bridge, not stopwatch estimates. The compact run began at
02:31:35.112 JST and completed at 02:31:50.454 JST. A follow-up, “If I give one
away, how many are left?”, correctly returned four in 22.329 seconds.

The running build for these measurements precedes the final identity wording
restoration required by regression assertions. Final wording adds a small amount
of text; these figures must not be presented as measurements of that final build.
The final source passed the full test gate and a staged Desktop build (zero errors,
49 warnings). Desktop was subsequently restarted with this final build; the staged output is
`work/efficiency-final-desktop/`.

These are individual local trials, not a benchmark across devices or load levels.
The original run had memory pressure; the repeat was also slow with several GB
available. Exact allocation of all elapsed time to inference, paging and decoding
is not exposed by this Windows endpoint. Short diagnostic requests to the same
service completed in 14.707 seconds (chat) and 4.325 seconds (stream); they omit
Concierge's action context and are not acceptance tests for the complete client.

## Changes

- Shared identity instructions are shorter. Tool names and approval categories
  remain visible; the existing `describe_tools` facility supplies complete
  descriptions and schemas on demand. History and host-side permissions remain.
- Android caches Hello for five seconds. Read-only uncached requests do not
  invalidate all metadata; mutations, unknown verbs and errors still invalidate.
  Answers and AskStatus are not cached.
- A sidebar test now waits for the rendered state before asserting its content.

## AngelDust

Patched Concierge installed in place successfully. Local service answered five,
then four for the follow-up. Both records synced to Desktop. Conversation:
`11833ec4-6ae6-4e0e-b88b-50263721071a`.
First run: 02:26:04.907–02:26:24.984 JST (20.077 seconds).
Service logs reported preparation/queue waiting in this run; it does not establish
a mobile answer-speed improvement over the prior 17.932-second run.
Settings logs show retained cache entries and hits; no cross-device speed claim.

## Verification and remaining work

- Focused efficiency/cache/discovery/Windows transport checks: 34 passed.
- Final full working-tree suite: 2632 passed, zero failed or skipped.
- Coverage gate: lines 80.58%, branches 69.08%, methods 86.16%.
  Report: `TestResults/coverage-b81a8aa34db240f0872eb4a2720b2a71/867a8ea7-0c2c-4821-ba63-8166b016559e/coverage.json`.
- Android and initial patched Windows builds succeeded, with existing warnings.
- The working tree includes pre-existing uncommitted history/discovery work;
  this is not evidence for a clean HEAD build.
- AngelDust and PixieDust have the phone APK with SHA-256
  `C0C6D3362952667E52F8D1EE5F0819033FEE515B4A7A2864D08AF6C77A5AB560`.
  Both installed base.apk hashes were checked against the build. PixieDust
  serial `32231JEHN17663` was confirmed after user-authorized wireless pairing;
  Concierge launches and is the resumed activity. No new PixieDust AI-answer
  timing is claimed from this deployment verification.
- ARM32 Wear build succeeded (zero errors, 29 warnings). StarDust was updated;
  installed SHA-256 matches
  `F9529A845B44147CB8AF0A888801C7CBDB2054D27FC9A2D99364043EC881CA93`.
  Home and Conversations open and report connected to Desktop. This verifies
  installation and navigation, not a new end-to-end watch answer.
- Circle AI 3.9.1 remains a standalone Windows process; persistent Windows service
  installation is a separate unresolved issue. No wrapper was introduced.

## Final-build comparison

Same apples question, single runs, durable run-start to completion (not tap-to-paint):

| Device | Seconds | Conversation | Result |
|---|---:|---|---|
| Desktop | 15.692 | `223ea647-0cf6-4dc8-85f5-8a535e470d65` | Correct, Windows service |
| AngelDust | 8.602 | `93b936b2-b9a2-4348-a05d-c72011daca79` | Correct, local Android service |
| PixieDust | 6.181 | `eb02bc17-35b3-4d9f-aff2-7442efec523b` | Correct, local Android service |
| StarDust | Unavailable | No request created | Native input returned no usable words |

StarDust's keyboard closed but the callback logged `firstLength=0` and
`Input returned OK without usable words; nothing sent.` This is not a successful
watch round trip and provides no inference timing. A retry was started but no
completed result was established before the user's next instruction.

## Next integration measurements

Priorities, not claims of implemented or measured improvements:

1. Instrument submission, service acceptance, readiness waiting, first text,
   completion and UI display separately on every client.
2. Check whether answer streaming reaches the UI incrementally; use the existing
   service contract where supported. This improves time to visible text, not
   necessarily total generation time.
3. Measure binding/connection reuse and avoid duplicate readiness requests while
   a request is running. Invalidate cached metadata on changes or disconnect.
4. Keep stable prompt prefixes and bounded durable history. Ask the Circle AI
   service team whether its supported session/prefix cache can reuse that work;
   Concierge must not implement another model runtime.
5. Restore a successful watch input-to-answer path before comparing its latency.

Model selection, runtime caches and model residency remain Circle AI responsibilities.
Keep device-local answering first; offload only on resource limits, failure or
explicit user transfer. Moving all work to one phone is a separate routing choice.
