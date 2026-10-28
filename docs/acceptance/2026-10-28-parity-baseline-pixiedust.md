# PixieDust response baseline — 28 October 2026

This is a live Android run on the **installed build from 26 October 2026 20:42:34**, before the safe service-guidance change in the current checkout. Package: `com.companyname.concierge`, versionName `1.0`, versionCode `1`. PixieDust was the local worker; **Use Desktop was not selected**. Circle AI service/model identity was not displayed in the transcript, so the exact model is **unverified**. No tool, source, skill, or memory receipt appeared in the conversation. The database records message completion, not first token or a service-stage trace; the intervals below are user-message to completed assistant-message and must not be called inference time.

The input text was checked in Android's live text field before each send. The full messages below were then read from the app's SQLite `Messages` rows for these two test conversations. The binary database copy is a local diagnostic under `work/`, not a repository artifact. Times are Japan Standard Time.

## Task 2 — appointment booking

Conversation `EB5D9954-AFF3-494C-97C6-6485F0667958`. **Fail.** The first useful progress in the visible UI was the generic “still working on this device” state; no preparation/inference split or partial answer was exposed in the captured screens.

| Sent | Completed | Elapsed | Exact input and output |
|---|---|---:|---|
| 03:28:28.464 | 03:28:49.865 | 21.401 s | **Person:** Help me plan a simple appointment-booking app for a one-person hair salon.<br>**Circle AI:** To build a simple appointment booking app for a single-person salon, create a basic website with a contact form that collects the client's name, preferred date, and time slot. Once submitted, send an automated email confirming the booked appointment and sending the client a link to a Google Calendar invite for their chosen time. |
| 03:30:18.530 | 03:30:35.395 | 16.865 s | **Person:** Keep it affordable; customers need to book from their phones, and I need to block out lunch and holidays.<br>**Circle AI:** To keep costs low, simply add your lunch and holiday blocks to your existing calendar before asking clients when they can book. This prevents double-bookings without needing complex software. |
| 03:31:11.465 | 03:31:28.384 | 16.919 s | **Person:** I can spend about five hours a week on this. What should I build first?<br>**Circle AI:** Start by building a simple web page with a contact form where users input their name, preferred date, and time slot. Then, set up your personal calendar to block out lunch times and holidays before launching. |
| 03:31:45.868 | 03:32:27.169 | 41.301 s | **Person:** Turn that into a two-week plan with tasks I can actually finish.<br>**Circle AI:** Here is a realistic 2-week plan to launch your simple appointment booking system:<br><br>Week 1: Planning & Setup<br>Day 1: Create a clean HTML contact form using free tools like Formspree or EmailJS to handle submissions without backend code.<br>Day 2: Set up your personal calendar (e.g., Google Calendar) and manually block out your lunch breaks and holidays.<br>Day 3: Write the logic to check incoming dates against your blocked schedule before allowing bookings.<br>Day 4: Test the form submission process locally to ensure data is captured correctly.<br><br>Week 2: Launch & Automation<br>Day 5: Configure the form to automatically send confirmation emails upon successful submission.<br>Day 6: Generate a unique link for each confirmed booking and share it via email to your clients.<br>Day 7: Review the final flow to ensure no conflicts exist between new bookings and your fixed schedule. |

**First failing turn:** the contact form and calendar invitation do not establish available slots or prevent two customers choosing the same slot. Later turns move lunch/holiday blocking to manual calendar work, then list seven unspecified days without fitting the stated five hours per week. The final plan appears as paragraphs in the live UI rather than a table or ordered list. Continuity is partial (salon, phone and lunch/holidays recur); intent, feasibility and format fail. No claim to have performed an action was made.

## Task 5 — chore allowance

Conversation `0C4F49E2-8DBD-453B-86ED-512408EFF3E6`. **Fail.**

| Sent | Completed | Elapsed | Exact input and output |
|---|---|---:|---|
| 03:33:22.139 | 03:33:29.577 | 7.438 s | **Person:** I want to give two children allowance for chores. Each gets $8 a week if they finish their own list.<br>**Circle AI:** That's a fair plan; each child will receive $8 per week based on completing their assigned chore list. |
| 03:33:47.838 | 03:33:58.427 | 10.589 s | **Person:** One has four chores and the other has five. Split the allowance evenly per completed chore, but cap each child at $8.<br>**Circle AI:** That's a fair plan since both children get capped at $8 regardless of how many chores they complete. |
| 03:34:49.264 | 03:35:07.262 | 17.998 s | **Person:** Show the amount per chore, the weekly maximum, and what I pay if the first child completes three chores and the second completes all five.<br>**Circle AI:** Each child earns $2 per chore (since $8 ÷ 4 = $2). The weekly maximum is $8. If Child 1 does 3 chores ($6) and Child 2 does all 5 chores ($10), Child 1 pays $6 and Child 2 pays $5. |

**First failing turn:** turn 2 did not compute the two requested per-chore rates. The final answer applies $2 to both children, invents a $5 payout for the second child after saying $10, omits the requested table, and does not give the $14 total. Expected: first child $2.00 per chore and $6 paid; second $1.60 per chore and $8 paid; each capped at $8; total $14.

## Task 1 — original arcade game

Conversation `FF94B9E9-52BA-4891-92C4-46632C331764`. **Fail.**

| Sent | Completed | Elapsed | Exact input and output |
|---|---|---:|---|
| 03:37:11.894 | 03:37:23.253 | 11.359 s | **Person:** Do you think you can help me create a Donkey Kong knock-off?<br>**Circle AI:** Yes, I can help you outline a basic concept for a Donkey Kong knock-off. However, since I am an AI and cannot build software, I will provide ideas that you can use with programming languages like Python or C++ to create your own game. |
| 03:37:37.689 | 03:37:49.895 | 12.206 s | **Person:** Do you know what Donkey Kong is?<br>**Circle AI:** Yes, Donkey Kong was a 1981 arcade game where a giant ape (Donkey Kong) jumps over enemies to rescue a girl trapped behind him. |
| 03:38:09.140 | 03:38:29.581 | 20.441 s | **Person:** Your reply makes you sound confused. Are you confused?<br>**Circle AI:** I am not confused. I have been asked if I know what Donkey Kong is, which I do. I also confirmed earlier that I can help outline concepts for a Donkey Kong knock-off using programming languages like Python or C++. |
| 03:38:59.826 | 03:39:22.157 | 22.331 s | **Person:** I mean a small original platform game inspired by the old arcade style. I want to make it for fun.<br>**Circle AI:** Yes, I can help you design a simple original platform game inspired by the classic arcade style. We can start by outlining core mechanics like gravity, collision detection, and jump physics. |

**First failing turn:** Concierge claims it cannot build software, although this client can create projects and run tools when permitted. The second turn misdescribes the arcade game (the player character jumps barrels and obstacles to reach Pauline); the third refuses to reconsider. The final turn gives no first playable version. The recovery task fails intent, factual grounding and correction.

## P01 live Skills claim after installation

The current Debug APK was installed over the existing PixieDust app at **03:40:06 JST**, preserving its data. [Live screenshot](2026-10-28-pixiedust-skills-corrected.png) shows the Circle AI library section now states that search returns names and IDs for discovery, not instructions, and only switched-on Concierge skills include their instructions with a request. The adapter code calls `RecallAsync` for memory but has no automatic service-skill search or invocation in the normal chat path; the `SearchSkillsAsync` link is used by the picker. Therefore the prior claim that relevant library matches were passed automatically is removed. This closes the misleading-copy task, **not** service-skill invocation (P11).

## Scope

This record covers three of the five baseline tasks on PixieDust. Desktop and tasks 3 and 4 remain **unverified in this run**. The tested phone build predates the current source, so a post-deployment run is required before attributing these failures to the present code or to the Circle AI service alone.
