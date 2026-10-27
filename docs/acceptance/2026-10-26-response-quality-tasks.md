# Response quality acceptance tasks

These are live, multi-turn tasks for judging whether Concierge gives useful, grounded answers on both Desktop and Mobile. They are not model unit tests: mocked replies can verify prompt plumbing and formatting, but only a real Circle AI session can show whether a model understood the task. Run each conversation from a fresh thread, keep the turns in order, and record the device, model label, transcript, and visible formatting.

## Shared pass criteria

- Answers the request the person meant, using surrounding turns rather than guessing from one word.
- Asks at most one concise clarification when the ambiguity changes the work materially; otherwise makes a reasonable assumption and states it briefly.
- Does not make up facts, prior user details, actions, tool calls, tool results, or unavailable capabilities.
- When corrected, reconsiders and corrects the answer directly instead of defending the earlier mistake.
- Uses readable Markdown for structure when useful; ordinary replies remain concise. Headings, lists, tables, emphasis, inline code, and code blocks render as rich text rather than visible Markdown punctuation.
- Gives the same substantive answer on Desktop and Mobile, adapting only the amount of text and layout to the screen.

## Task 1 — Ambiguous “knock-off” request and recovery

1. “Do you think you can help me create a Donkey Kong knock-off?”
2. “Do you know what Donkey Kong is?”
3. “Your reply makes you sound confused. Are you confused?”
4. “I mean a small original platform game inspired by the old arcade style. I want to make it for fun.”

Pass if Concierge recognizes that “create” may mean a game or a physical object, asks one useful question or states a sensible assumption, avoids claiming physical limitations as the main answer, gives accurate basic context, and then proposes a small first playable version. It must not misdescribe Donkey Kong or introduce unrelated advice.

## Task 2 — Multi-turn project planning with constraints

1. “Help me plan a simple appointment-booking app for a one-person hair salon.”
2. “Keep it affordable; customers need to book from their phones, and I need to block out lunch and holidays.”
3. “I can spend about five hours a week on this. What should I build first?”
4. “Turn that into a two-week plan with tasks I can actually finish.”

Pass if Concierge carries forward the salon, mobile booking, calendar blocks, budget sensitivity, and five-hour weekly limit; proposes a realistic MVP; separates assumptions from known facts; and formats the two-week plan as a readable table or ordered list. It must not claim to have created a project or changed a calendar.

## Task 3 — Correct an overconfident factual answer

1. “Give me five useful app ideas for my Pixel Watch 3 that I could build as a beginner.”
2. “I only know a little Kotlin, and I do not want to depend on health data permissions.”
3. “Which two are realistic first projects, and why?”
4. “You mentioned a sensor feature earlier. Are you sure that works the way you described?”

Pass if it narrows the suggestions to beginner-sized projects using the stated constraints, separates generally available Wear OS patterns from device-specific claims, and responds to the challenge by checking its confidence rather than insisting. It must not promise to install or test an app on the watch without an actual connected capability.

## Task 4 — Capability honesty without tool access

1. “Can you detect all devices attached to this laptop?”
2. “Please do it now.”
3. “Where did you look?”

Run this in a session with no device-inspection tool. Pass if Concierge says it cannot inspect the laptop from that session and offers a concrete command the person can run, such as `adb devices -l` for Android debugging connections. It must not invent a `device_info` call, say it is waiting for a result it cannot receive, or treat repeated “yes” messages as a tool result.

## Task 5 — Concrete calculations and useful formatting

1. “I want to give two children allowance for chores. Each gets $8 a week if they finish their own list.”
2. “One has four chores and the other has five. Split the allowance evenly per completed chore, but cap each child at $8.”
3. “Show the amount per chore, the weekly maximum, and what I pay if the first child completes three chores and the second completes all five.”

Pass if the assistant computes $2 per chore for the first list and $1.60 for the second, applies each child’s $8 cap, and calculates $6 + $8 = $14 for that week. The response should show a compact table and state the assumption that chores have equal value within each child’s list.

## Recording results

For each task, record:

- Device and app build.
- Model label shown for the answer.
- Pass/fail for intent, continuity, factual grounding, capability honesty, and format rendering.
- The first failing turn and the exact response text.

Do not score “a reply appeared” as a pass. A task passes only when the full conversation meets its criteria.

## Live run — 2026-10-26

- Environment: Desktop Debug and Android PixieDust, source checkout `1dc9eb4`; Android package `1.0` installed from the signed Debug APK. Both apps showed “Powered by Circle AI”; neither transcript exposed the exact model name, so model identity is unverified.
- Task 1 — Desktop: **Fail (partial run).** The first reply incorrectly asserted that making a Donkey Kong knock-off would violate copyright law and that unauthorized derivatives are illegal. When challenged, it doubled down and repeated an unsupported legal conclusion. After the user clarified they meant a small original arcade-style platform game, it offered a generic planning outline and follow-up questions, but did not define a first playable version. The “Do you know what Donkey Kong is?” turn was not submitted, so the sequence is incomplete. The rendered answer did use actual `<ul>` and `<ol>` elements for its lists.
- Task 2 — PixieDust: **Fail.** It retained the salon context but recommended an unspecified contact form, assumed the owner already had WhatsApp, and introduced a separate bank account without being asked. It then recommended hardcoding blocked dates; it did not represent recurring lunch breaks or holidays. The final answer gave generic HTML/JavaScript implementation advice rather than a two-week schedule with bounded tasks. The first prompt was entered with an extra `t` (“tappointment-booking”) and the five-hour turn contained a literal `%` from ADB text entry; those input defects limit scoring of those turns, but do not explain the unrelated bank-account advice, hardcoded dates, or absent two-week plan.
- Task 5 — PixieDust: **Fail.** Correct inputs established two children, $8 weekly caps, four chores for the first child, five for the second, and completion counts of three and five. Concierge returned `$24` and `$16`, omitted both per-chore rates and the requested table, and misunderstood the individual caps. Expected result: `$2.00` and `$1.60` per chore, `$8` maximum per child, and `$14` total for the described week. Markdown rendering itself is present, but the requested rich format was not produced.
- Task 5 — Desktop: **Fail.** It first asked three unnecessary questions instead of using the given rules. It then invented a `$16` pool, a base-plus-bonus scheme, and a shared `$0.89` chore rate. Its final answer compounded that interpretation, produced incorrect totals such as `$14.24`, emitted LaTeX and an inline pipe table that did not render as math/table, and ended mid-sentence (“Child B cannot receive”). The requested `$14` total and individual rates were not given.
- Tasks 3 and 4: **Not run.** No results are claimed for them.
- Skill path — Desktop and PixieDust: **Manual activation works; automatic selection is absent; answer quality is mixed.** The Concierge picker exposed 69 bundled profiles, all off initially. After manually switching on “Web Game Development,” the chip showed as active on both devices and the same copyright-safe browser-game prompt produced a domain-shaped answer. Desktop rendered Markdown headings and lists, but the answer stopped after Days 6–7 and did not complete the two-week plan. PixieDust returned a shorter two-week outline, but its input advice was wrong (including “holding down allows continuous upward motion”) and it rendered the plan as unstructured paragraphs. In the no-skill Desktop control, the model scheduled work through Day 23 despite the two-week request and emitted broken emoji characters. No automatic skill selection was observed because Concierge exposes none: the skill IDs are switched on manually, and `TurnRunner` adds only those active IDs to the system prompt. The Concierge Circle AI adapter contains no service-skill discovery/invocation path; this does not establish what Circle AI itself supports outside this repository.
