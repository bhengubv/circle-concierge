# PixieDust service defects: reproduction and acceptance

Scope: these observations use the installed Android service and public CircleAI
3.8.1 SDK. No Circle AI repository was read or modified. The user previously
restricted repository access to Concierge and Butler.

Device: PixieDust / Pixel 7a. Service package com.bhengubv.circleai.service,
versionName 0.1, versionCode 58. Its own log identifies Qwen3.5-2B-MNN.
Conversation: 71506603-aa93-45dd-b6b0-380bdd3c234a. All times below are JST,
2026-10-14. Native calls use the published LinkTurnCodec, the same durable
conversation ID, the exact current user text, and Agentic=false. No client system
prompt is flattened into the query.

## 1. Cold turn is refused while service starts the model

Normal Send: "What is two plus three? Reply with the number only."

Service log:

```text
20:38:16.704 CircleAI.Link: serve: ask received
20:38:16.706 CircleAI.Link: serve: authorised, starting the brain
20:38:16.735 CircleAI.Link: serve: node=null
20:38:16.772 CircleAI.Neuron: loading the model…
20:38:16.810 CircleAI.Neuron: model: Qwen3.5-2B-MNN
20:38:35.435 CircleAI.Neuron: brain ready
```

The conversation receipt is failed, with no assistant answer. The service had
already advertised Answering=On. The SDK documents On as actually working, not
merely downloaded. Its coarse setup report cannot substitute here: it remains
NeedsSetup because voice resources are missing.

Required service acceptance: a cold text request should wait for in-flight
initialization and answer, or expose an unambiguous warming state that the client
can follow without repeating a possibly executed request. Do not advertise text
On before that promise holds. Test after a real service restart, not only with a
warm model. Preserve authorization and cancellation semantics.

## 2. Warm local arithmetic is wrong

Normal Send: "Please calculate 2 + 3. Answer with just the number."

At 20:40:46.969 the service logged node=ready. The service prompt diagnostic
reported query=52, matching the 52-character forwarded request. At 20:40:56.541 the assistant event contained
"4", producedBy Circle AI Android service. Expected mathematical result: 5.

A separate practical prompt:
"My meeting starts at 10:00. Travel takes 20 minutes. What time should I leave to
arrive at 10:00?"
returned "You must leave at 9:40 AM." at 20:46:01.940. That result is correct;
it does not erase the failed arithmetic test.

Required service acceptance: investigate model loading, prompt/template handling,
state/cache reuse and inference configuration with the actual installed model.
Do not inject expected answers, replace the model in Concierge, or report one
passing prompt as general accuracy. Check simple arithmetic and practical tasks
in fresh and continued sessions; preserve every result in the report.

## 3. Overall setup cannot complete through the reported plan

After SetupStart finished, SetupProgress returned Done / 1.0 and SetupPlan
returned no rows. SetupReadiness still returned NeedsSetup / canTalk=0.
SetupCensus reported brain and eyes Present, but English voice, South African
voices, ears and wake word Missing. One SetupStart retry finished immediately
without changing those reports. Voice scope was not granted; evaluate whether
the plan should include these resources for this service build, or whether
unsupported/optional voice should be distinguished from required setup.

Required service acceptance: plan, census and readiness must agree for the
requested capability and serving build. Text must remain usable without
requiring voice; voice must have a supported service-owned preparation path if
this service build advertises it.

## Execution attribution

Desktop prompt processing remained unchanged at 19:34:39.4378862 during all these
local turns. Replies synchronized into Desktop history afterwards. They are
Android service replies, not Desktop replies. No Circle AI source changes or
accuracy fixes have been made in Concierge.
