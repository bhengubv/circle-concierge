# Desktop allowance task — live acceptance, 22 November 2026

## Setup

- Rebuilt Concierge Desktop from `db1816f` with 0 warnings and 0 errors, then ran it from the isolated worktree. The source-only acceptance commits `f0473dd` and `e7f29ca` do not alter that binary.
- Started a fresh conversation, ID `425e7d22-3043-42cb-b0dc-9f507df3f8a3`, in the Desktop app. The UI showed “Powered by Circle AI” and “Working on this device.” It did not show a model ID or service binary version, so those remain unverified.
- Sent all three Task 5 prompts exactly as written in [the response-quality task](2026-10-26-response-quality-tasks.md#task-5--concrete-calculations-and-useful-formatting), in order. The setting was the default “Ask first.” No tool activity was shown. The UI said service agentic work was not connected.

## Observed replies

1. For “Each gets $8 a week,” Concierge asked three clarifying questions. One asked whether payment should be weekly, biweekly, or monthly, repeating a fact already supplied. The completed first turn took more than 2:06 and less than 2:36 based on UI observations; no exact completion timestamp was captured.
2. For four chores and five chores, it said “The total pool ($8 + $8 = $16) is divided equally among all completed chores,” then said Child A earned `$4` and Child B `$5` for completing all their chores. That contradicts the separate `$8` cap and does not account for the stated `$16` total. The completed second turn took more than 1:46 and less than 2:16 by UI observation.
3. For three completed chores and five completed chores, it calculated `$16 ÷ 9 = $1.78` per chore, `$5.34` for the first child, `$8.00` for the second, and **`$13.34` total**. The correct separate-child calculation is `$8 ÷ 4 = $2.00` and `$8 ÷ 5 = $1.60` per chore, then `$6 + $8 = $14`. The completed third turn took more than 3:19 and less than 3:39 by UI observation.

The final answer contained a real HTML table in the accessibility tree, so table structure rendered. Several math expressions remained visible as literal `\(\$1.78\)` syntax in that tree; visual math rendering was not separately checked. The answer also invented an “unspent” `$0.66` from its mistaken pooled calculation.

## Verdict and next isolation step

**Fail.** The first turn asked unnecessary questions, the second turn changed the payment rule, and the third gave the wrong total. The visible conversation did retain earlier turns. Source inspection shows `TurnRunner` derives stored history and `CircleAiWindowsServiceRuntime` serializes the message roles and content to `/butler/stream`, but this run did not capture the service's received payload or model-side interpretation. Do not assign the arithmetic failure to the client or service conclusively from this observation alone.

The new `Loopback_service_receives_the_separate_allowance_rules_and_prior_turns` regression test exercises the Desktop runtime's loopback HTTP boundary. It verifies the six ordered system/user/assistant roles, both separate-child rules, the completion counts, and the local service credential header. Its 15-test class passed. A separate `TurnRunner` regression sends the three exact user prompts through one conversation and verifies their order, the prior assistant replies, and the system guidance at the runtime boundary. Its focused class passed 19/19.

The first full gate after the HTTP test passed **2,740/2,740**, 0 skipped, with 80.23% line, 68.09% branch, and 85.63% method coverage. After the history test, a fresh-build full gate passed **2,741/2,741**, 0 skipped, with 75.78% line, 65.55% branch, and 80.45% method coverage. That latter coverage report showed zero instrumentation for several assemblies, so the percentage varies with the instrumented assembly set; it still passed the configured threshold. These tests prove the client can carry the facts intact through both stages. They do not capture the exact bytes received by the live service in this run.

Next: capture a redacted live service-boundary request/response for this exact conversation, including the role sequence and model ID, then compare a direct service response to the Concierge response. If the service receives the correct three turns and still pools the allowances, hand the trace to the Circle AI team. Separately check whether the renderer should support or normalize the emitted `\(...\)` math syntax. No backend code was changed in this acceptance run.
