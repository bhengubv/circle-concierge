# PixieDust service-guidance retest — 28 October 2026

The signed Debug APK built from the current checkout was installed at **03:40:06 JST** over the existing app. It includes a short, safe Concierge reply-guidance prefix in `CircleAiSharedServiceChatRuntime`; the private tool and approval prompt remains client-side. The service package on PixieDust is `com.bhengubv.circleai.service`, versionName `1.1`, build **62**. Debug service traces identify **Qwen3.5-2B-MNN** for these requests. The UI did not display that model or the service-stage timings to the person.

Exact inputs were verified in the live text field before sending. Full outputs and completion times are from the app's SQLite `Messages` rows. Times are Japan Standard Time; elapsed is user-message to completed assistant-message, not first token. No tool, source, service skill, or memory receipt appeared. This is one run per task, so differences from the [earlier installed-build baseline](2026-10-28-parity-baseline-pixiedust.md) are observations, not proof that the prefix alone caused them.

## Task 2 — salon app, conversation `8702E352-AB62-4264-A156-F44C4D12A4F3`

**Fail.**

| Sent | Completed | Elapsed | Exact input and output |
|---|---|---:|---|
| 03:43:26.076 | 03:43:38.502 | 12.426 s | **Person:** Help me plan a simple appointment-booking app for a one-person hair salon.<br>**Circle AI:** To build a simple appointment booking app for your one-person hair salon, create a single-page website with a contact form where clients can select their preferred date and time slots. Ensure the form includes fields for name, email, and availability preferences to streamline scheduling without needing a database. |
| 03:43:54.853 | 03:44:19.065 | 24.212 s | **Person:** Keep it affordable; customers need to book from their phones, and I need to block out lunch and holidays.<br>**Circle AI:** To keep the project affordable, focus on building a static HTML/JavaScript single-page application using free tools like Google Forms or Calendly. Block out your specific dates and holidays manually before finalizing the schedule. |
| 03:44:38.894 | 03:45:16.447 | 37.553 s | **Person:** I can spend about five hours a week on this. What should I build first?<br>**Circle AI:** Start by creating a static HTML file containing a basic calendar view and a contact form. This allows you to visualize the layout while you refine the logic later. |
| 03:45:39.713 | 03:46:33.125 | 53.412 s | **Person:** Turn that into a two-week plan with tasks I can actually finish.<br>**Circle AI:** Here is a two-week plan to complete your appointment-booking app within five hours per week:<br><br>**Week 1: Foundation & Structure**<br>* [ ] Create a single `index.html` file using raw HTML/CSS.<br>* [ ] Implement a visual calendar grid using CSS Grid to display available days.<br>* [ ] Build a responsive `<form>` element with inputs for Name, Email, and Availability.<br>* [ ] Test the page locally to ensure all links and styles render correctly.<br><br>**Week 2: Logic & Integration**<br>* [ ] Add JavaScript to handle form submission data capture.<br>* [ ] Integrate a third-party service (e.g., Calendly) via API key to replace manual blocking.<br>* [ ] Verify the integration works by submitting dummy test data.<br>* [ ] Save the completed code as a downloadable `.zip` package. |

The final response now has a readable two-week checklist and explicitly repeats the five-hour weekly limit. It still does not allocate work within that limit or establish a reliable available-slot and conflict-prevention flow. The static page/calendar/Form/Calendly suggestions are not a coherent implementation path. It therefore fails the MVP planning acceptance. The service trace for the third turn reported **QueueMs 7, EnrichMs 3, PrefillMs 33,365, DecodeMs 3,913, PromptTokens 723, OutputTokens 31, TotalMs 37,327, KvPath `reset`**. The final turn reported **PrefillMs 26,671, DecodeMs 26,276, TotalMs 52,998**, with 187 output tokens. These are service timings, not the app's entire response interval.

## Task 5 — allowance, conversation `45EBFD9F-5D27-4DC0-9AF0-D8C732215E83`

**Fail.**

| Sent | Completed | Elapsed | Exact input and output |
|---|---|---:|---|
| 03:47:11.066 | 03:47:23.283 | 12.217 s | **Person:** I want to give two children allowance for chores. Each gets $8 a week if they finish their own list.<br>**Circle AI:** Here is your request to pay each child $8 per week for completing their chore lists. Would you like me to create a printable schedule or checklist for them? |
| 03:47:40.170 | 03:48:04.521 | 24.351 s | **Person:** One has four chores and the other has five. Split the allowance evenly per completed chore, but cap each child at $8.<br>**Circle AI:** Here is the split based on your request: Child A earns $4 ($8 total ÷ 2 chores), and Child B earns $5 ($8 total ÷ 5 chores). Both amounts respect the $8 weekly cap. Which child should receive which amount? |
| 03:48:18.633 | 03:48:52.763 | 34.130 s | **Person:** Show the amount per chore, the weekly maximum, and what I pay if the first child completes three chores and the second completes all five.<br>**Circle AI:** Here is the breakdown with the specified limits applied:<br>- Child A (3 chores): Earns $12 ($8 max)<br>- Child B (5 chores): Earns $10 ($8 max) |

The second turn invents two chores and miscalculates both rates. The last turn exceeds the stated cap, omits the per-chore rates, and does not give the $14 total. The guidance prefix did **not** make basic arithmetic or continuity reliable. The service team needs the exact transcript and model/timing trace; Concierge must keep this task open.

## Decision

The response-guidance prefix is **not accepted as a quality fix** and was removed from the source after this retest. It improved the salon plan's visible structure in one run, but neither tested task passed and the allowance result remained wrong. P12 stays open. The separate live progress and drawer-navigation changes require their own device acceptance.

**Circle AI service team dependency:** On service build 62 with Qwen3.5-2B-MNN, explain and correct the arithmetic and constraint failures in the exact Task 2 and Task 5 transcripts above. The client supplies the earlier conversation turns (covered by `Prior_conversation_turns_are_sent_with_the_follow_up_question`), and the live replies show the service did not use them reliably. Please also explain why `KvPath` is `reset` on the measured turns and whether prompt preparation can reuse prior context. **Retest condition:** both entire conversations pass on PixieDust, including conflict-safe booking, a feasible five-hour weekly plan, and the $14 allowance result; then run the same acceptance on Desktop.
