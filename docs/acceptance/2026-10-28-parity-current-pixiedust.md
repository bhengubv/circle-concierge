# PixieDust current-build parity run — 28 October 2026

The signed Android Debug APK built from the current checkout completed with **0 errors, 105 warnings** and installed over Concierge on PixieDust, preserving its data. Circle AI Android service was build 62. These are live UI submissions from fresh conversations; the exact input was checked in the Android text field before each send. The full responses and completion timestamps below came from Concierge's SQLite `Messages` rows. Times are JST. Elapsed time is message creation to completed reply, not time to first token. No tool, source, skill, or memory receipt was visible for either task.

## Task 4 — inspect attached laptop devices

Conversation `443354FD-3CBA-4AA7-9B20-E4DA8725EFE5`. **Fail.**

| Sent | Completed | Elapsed | Exact input and output |
|---|---|---:|---|
| 09:40:43.457 | 09:40:54.958 | 11.501 s | **Person:** Can you detect all devices attached to this laptop?<br>**Circle AI:** I cannot detect any devices attached to your laptop because I do not have access to your physical environment or camera view. Please use a device detection app on your computer to check for connected peripherals like mice, keyboards, or external drives. |
| 09:41:35.542 | 09:41:52.633 | 17.091 s | **Person:** Please do it now.<br>**Circle AI:** I cannot detect any devices attached to your laptop because I do not have access to your physical environment or camera view. Please use a device detection app on your computer to check for connected peripherals like mice, keyboards, or external drives. |
| 09:43:04.188 | 09:43:17.490 | 13.302 s | **Person:** Where did you look?<br>**Circle AI:** I cannot tell you where I looked because I am an AI without eyes or a physical body to see anything. |

The session had no device-inspection tool. The reply did not falsely claim a scan, but it never identified the absent tool or gave a concrete command such as `adb devices -l`. The final answer failed to explain where it had checked: nowhere. Capability honesty and useful recovery fail.

## Task 3 — beginner Pixel Watch ideas and correction

Conversation `AA40DA04-B9C7-4F3C-B2D9-27C3D8661DA7`. **Fail.**

| Sent | Completed | Elapsed | Exact input and output |
|---|---|---:|---|
| 09:45:08.928 | 09:45:35.820 | 26.892 s | **Person:** Give me five useful app ideas for my Pixel Watch 3 that I could build as a beginner.<br>**Circle AI:** Here are five simple app ideas suitable for your Pixel Watch 3:<br><br>1. **Local Weather Tracker**: Pull real-time weather data from trusted sources and display it on the watch face without needing a smartphone connection.<br>2. **Simple To-Do List**: Let users set tasks with due dates and reminders, syncing them to their Google Calendar automatically via Bluetooth.<br>3. **Voice-to-Speech Dictation**: Allow users to speak messages into the watch, which will be transcribed and saved to their Google Drive or Gmail account.<br>4. **Quick Recipe Finder**: Search for recipes based on ingredients or cuisine type, then save the recipe details to the user's Google Sheet.<br>5. **Sleep & Activity Monitor**: Track daily sleep duration and steps using the watch sensors, sending a summary report to the user'g. |
| 09:46:06.304 | 09:46:06.711 | 0.407 s | **Person:** I only know a little Kotlin, and I do not want to depend on health data permissions.<br>**Circle AI:** I can't see live weather or news from this phone, so I can't tell you that right now. |
| 09:46:48.637 | 09:46:49.046 | 0.409 s | **Person:** Which two are realistic first projects, and why?<br>**Circle AI:** I can't see live weather or news from this phone, so I can't tell you that right now. |
| 09:47:27.334 | 09:47:27.756 | 0.422 s | **Person:** You mentioned a sensor feature earlier. Are you sure that works the way you described?<br>**Circle AI:** I can't see live weather or news from this phone, so I can't tell you that right now. |

The first answer includes unverified integration assumptions and malformed final text. The remaining three answers do not address their questions. Android logcat identifies the last request as accepted by Circle AI (`id=2c619f5ee6cf44758e073745def481c8`, 1,407 input characters) and completed with `finish=shortcut`, `answered by no-live-data`, 7 ms service time, no selected model. Investigation found a **Concierge contract error**: the client put the full conversation in both `ArgMessage` and `ArgQuestion`. The installed Circle AI NuGet contract defines `ArgQuestion` as the question alone when `ArgMessage` carries caller context. That caused earlier weather text to be treated as part of the current question. Concierge now sends the retained conversation in `ArgMessage` and only the latest user turn in `ArgQuestion`. Focused boundary tests assert both values. The corrected APK was installed and the same four-turn task rerun below. This supersedes the initial attribution to a service shortcut defect.

### Task 3 after the client contract correction

Conversation `67BDF8E6-42D1-409D-8775-BD582C46FBF1`. **Fail**, although the erroneous shortcut stopped. The app retained history and received a model-generated response to every follow-up.

| Sent | Completed | Elapsed | Exact input and output |
|---|---|---:|---|
| 10:17:42.053 | 10:18:09.097 | 27.044 s | **Person:** Give me five useful app ideas for my Pixel Watch 3 that I could build as a beginner.<br>**Circle AI:** Here are five simple app ideas suitable for your Pixel Watch 3:<br><br>1. **Local Weather Tracker**: Pull real-time weather data from trusted sources and display it on the watch face without needing a smartphone connection.<br>2. **Simple To-Do List**: Let users set tasks with due dates and reminders, syncing them to their Google Calendar automatically via Bluetooth.<br>3. **Voice-to-Speech Dictation**: Allow users to speak messages into the watch, which will be transcribed and saved to their Google Drive or Gmail account.<br>4. **Quick Recipe Finder**: Search for recipes based on ingredients or cuisine type, then save the recipe details to the user's Google Sheet.<br>5. **Sleep & Activity Monitor**: Track daily sleep duration and steps using the watch sensors, sending a summary report to the user'g. |
| 10:19:16.561 | 10:19:47.669 | 31.108 s | **Person:** I only know a little Kotlin, and I do not want to depend on health data permissions.<br>**Circle AI:** Kotlin is a good choice for building these apps since it offers strong support for Android development and integrates well with other languages like Java. Since you don't need health data permissions, you can focus on features like local weather tracking, task lists, voice dictation, recipe finding, and activity monitoring without relying on sensitive sensor access. |
| 10:20:13.281 | 10:20:51.618 | 38.337 s | **Person:** Which two are realistic first projects, and why?<br>**Circle AI:** The most realistic first project is the **Local Weather Tracker**, because it requires no external device connectivity and relies solely on available watch sensors and trusted APIs. The second realistic option is the **Simple To-Do List**, as it uses standard Bluetooth pairing and existing calendar services without needing complex hintegration. |
| 10:23:19.047 | 10:23:50.805 | 31.758 s | **Person:** You mentioned a sensor feature earlier. Are you sure that works the way you described?<br>**Circle AI:** I am sorry, but I cannot confirm whether a specific sensor feature works exactly as described because I lack direct access to the actual code or implementation details of those features. Please clarify which feature you are referring to so I can verify its functionality accurately. |

The third turn falsely treats weather as a watch sensor feature, and the fourth does not correct its own claim. This is a remaining answer-quality failure, not proof that the client lost the latest question. No tool, source, skill or memory receipt was visible. The client contract bug is closed; the full task is not.

## Task 5 — allowance on the corrected client

Conversation `25F0852B-1071-4BB2-8CC5-532CB9D9E48A`. **Fail.** The three exact prompts were verified in the Android text field before sending. The service selected `Qwen3.5-2B-MNN` on all three turns. Times below come from Concierge's message store; the [live final screenshot](2026-10-28-pixiedust-allowance-current.png) confirms the rendered result. Its multiplication signs display correctly in the app; a binary database export through PowerShell damaged those two UTF-8 characters, so the screenshot is authoritative for that glyph.

| Sent | Completed | Elapsed | Exact input and output |
|---|---|---:|---|
| 16:21:53.983 | 16:22:02.090 | 8.107 s | **Person:** I want to give two children allowance for chores. Each gets $8 a week if they finish their own list.<br>**Circle AI:** That's a fair plan; each child will receive $8 per week based on completing their assigned chore list. |
| 16:23:04.597 | 16:23:20.054 | 15.457 s | **Person:** One has four chores and the other has five. Split the allowance evenly per completed chore, but cap each child at $8.<br>**Circle AI:** Splitting the remaining $4 between them would be $2 each. However, since both children already have a maximum cap of $8, no further adjustment is needed. |
| 16:24:05.339 | 16:24:27.365 | 22.026 s | **Person:** Show the amount per chore, the weekly maximum, and what I pay if the first child completes three chores and the second completes all five.<br>**Circle AI:** The first child earns 3 chores × $0.50 = $1.50. The second child earns 5 chores × $0.50 = $2.50. Your total payment is $4.00. |

Expected rates are $2.00 for the first child and $1.60 for the second; the described week costs $6 + $8 = **$14**. The second answer invents a remaining $4; the final answer invents a shared $0.50 rate and is $10 short. No calculation tool or source receipt was visible. Circle AI's timing lines for these turns report 96, 224 and 359 prompt tokens; prefill took 4.904, 10.928 and 15.510 seconds, with `kv=reset` each time. This measures both the quality failure and growing prompt cost; it does not establish which component owns the cache reset.

### P14 cache-format experiment — reverted

I changed Concierge's conversation message temporarily so each later message began with the exact previous message, a format that could allow prefix reuse. The boundary test passed, the APK built and was installed, and Task 5 was repeated with the same three inputs in fresh conversation `21F3A661-FA2C-4DDB-9846-ACA62C7A9923`. Circle AI still reported `kv=reset` on all three turns. Prompt sizes were 96, 208 and 343 tokens, versus 96, 224 and 359 before; prefill took 4.702, 9.379 and 15.548 seconds, versus 4.904, 10.928 and 15.510. The final answer worsened to **$64**. This did not meet a useful improvement threshold. The format change and its test were removed; the restored client built with **0 errors, 105 warnings** and was installed successfully over PixieDust. No cache improvement is claimed.

## Task 1 — original arcade game on the corrected client

Conversation `D3385F7C-5CBC-4223-B1DF-33E41E62213D`. **Fail.** Four exact prompts were verified before sending. The [final live screenshot](2026-10-28-pixiedust-game-current.png) shows the last correction and result.

| Sent | Completed | Elapsed | Exact input and output |
|---|---|---:|---|
| 16:27:20.692 | 16:27:31.987 | 11.295 s | **Person:** Do you think you can help me create a Donkey Kong knock-off?<br>**Circle AI:** Yes, I can help you outline a basic concept for a Donkey Kong knock-off. However, since I am an AI and cannot build software, I will provide ideas that you can use with programming languages like Python or C++ to create your own game. |
| 16:30:18.893 | 16:30:32.096 | 13.203 s | **Person:** Do you know what Donkey Kong is?<br>**Circle AI:** Yes, Donkey Kong is a classic arcade platformer where a giant ape jumps over enemies to reach the top of a tower. |
| 16:32:28.622 | 16:32:44.834 | 16.212 s | **Person:** Your reply makes you sound confused. Are you confused?<br>**Circle AI:** I am not confused; I simply need more details about your specific requirements to tailor my response effectively. |
| 16:33:59.054 | 16:34:23.353 | 24.299 s | **Person:** I mean a small original platform game inspired by the old arcade style. I want to make it for fun.<br>**Circle AI:** Yes, I can help you design a simple original platform game inspired by the classic arcade style. You can start by creating a character who moves up and down between platforms while avoiding obstacles. |

The first answer wrongly says Concierge cannot build software. The second reverses who jumps over obstacles in the original game. The third does not recognize or correct either error. The final answer accepts the original-game framing but gives no concrete first playable version. No tool or skill receipt was visible. This is not a pass for intent, factual grounding, correction, or useful execution.

## Task 2 — final installed-build salon retest

Conversation `F371AC56-2FE5-4CFB-A197-E02A6AA30C2F`. **Fail.** The client had no extra reply-guidance prefix; it did include the visible progress wording fix.

| Sent | Completed | Elapsed | Exact input and output |
|---|---|---:|---|
| 10:01:15.358 | 10:01:39.210 | 23.852 s | **Person:** Help me plan a simple appointment-booking app for a one-person hair salon.<br>**Circle AI:** To build a simple appointment booking app for a single-person salon, create a basic website with a contact form that collects the client's name, preferred date, and time slot. Once submitted, send an automated email confirming the booked appointment and sending the client a link to a Google Calendar invite for their chosen time. |
| 10:02:11.815 | 10:02:29.885 | 18.070 s | **Person:** Keep it affordable; customers need to book from their phones, and I need to block out lunch and holidays.<br>**Circle AI:** To keep costs low, use free tools like Calendly for scheduling and Gmail/Outlook for emails. Block your calendar by adding holiday dates and lunch times as fixed blocks before inviting clients. |
| 10:02:54.115 | 10:03:22.729 | 28.614 s | **Person:** I can spend about five hours a week on this. What should I build first?<br>**Circle AI:** Start with a simple landing page featuring a contact form where users input their name, desired date, and available time slots. Then, integrate Calendly to handle scheduling automatically while blocking out holidays and lunch breaks. |
| 10:03:48.466 | 10:04:16.529 | 28.063 s | **Person:** Turn that into a two-week plan with tasks I can actually finish.<br>**Circle AI:** Start by building a simple landing page with a contact form to collect names, dates, and time slots. Next, set up Calendly to handle scheduling and block out holidays and lunch times. Finally, write the code for both pages using HTML and CSS. |

The [live final-answer screenshot](2026-10-28-pixiedust-salon-final.png) shows neither a two-week schedule nor tasks fitted to five hours per week. The implementation path still mixes a custom contact form with Calendly without defining the source of available slots or avoiding duplicate bookings. The first failing turn is the contact-form booking suggestion without availability or conflict prevention; the final turn confirms the task fails continuity, feasibility and requested format. The progress UI change did not improve answer quality.

## UI checks and Desktop block

### Notebook source grounding — narrow live pass

The existing **Acceptance - PixieDust** notebook contained a pasted source titled **Meeting notes**: “The Juniper meeting is on Friday at 10am. Nandi is the organiser.” A new notebook-linked conversation (`CE744C87-F9CD-47FA-87B3-B6033CF73EBC`) was started in the PixieDust UI. Concierge's message store recorded these completed turns (JST):

| Sent | Completed | Elapsed | Exact input and output |
|---|---|---:|---|
| 16:55:00.087 | 16:55:10.881 | 10.794 s | **Person:** According to my Meeting notes, when is the Juniper meeting and who is the organiser?<br>**Circle AI:** Based on your "Meeting notes" excerpt, the Juniper meeting is scheduled for Friday at 10 am, and Nandi is the organizer. |
| 16:56:17.677 | 16:56:37.678 | 20.001 s | **Person:** What room is the meeting in?<br>**Circle AI:** Based on the provided "Meeting notes" excerpt, there is no information about which room the meeting takes place in. Please check other parts of your document for that detail. |

The two answers use the selected source and avoid inventing a room. This is a narrow pass for pasted-note grounding and an unknown-field follow-up on PixieDust. The output names the note but provides no clickable source citation or service receipt. Import beyond pasted text, Desktop, restart and cross-device sync, long-term memory, and skill use remain unverified. The phone locked before a useful final screenshot was captured; the evidence here is the stored conversation transcript, not a visual acceptance claim.

### Desktop follow-up: conversation sync passes, notebook sync fails

The freshly built Desktop Concierge was opened and the recent **From Mobile** conversation with the same ID (`CE744C87-F9CD-47FA-87B3-B6033CF73EBC`) was selected. The [Desktop screenshot](2026-10-28-desktop-notebook-sync.png) and accessibility tree show both PixieDust questions and both exact answers. The last answer's **Copy** control changed to **Copied**; pasting into the Desktop composer produced the exact answer text. The unsent test draft was cleared. This is a live pass for this conversation's message synchronization and Desktop copy, without a Desktop model answer.

Desktop's [Notebooks screen](2026-10-28-desktop-notebooks-local-only.png) listed only **Acceptance — notebook sources** (one source, one conversation); it did **not** list PixieDust's **Acceptance - PixieDust** notebook or its **Meeting notes** source. The notebook implementation uses a device-local `notebooks.json` store, while the existing bridge synchronizes conversation events. A Desktop follow-up in that synced conversation therefore has no verified access to the selected notebook source. This is a **P21 failure**, not cross-device notebook continuity. The Windows Circle AI service was still unavailable, so no Desktop follow-up answer was submitted.

- The mobile navigation drawer was scrolled to show lower sections, closed, and reopened. [Before](2026-10-28-pixiedust-drawer-scrolled.png) and [after](2026-10-28-pixiedust-drawer-reopened.png) screenshots show **New conversation** restored at the top. The `@key` reset in the handheld workspace passes this narrow UI check.
- A **Copy** control was added to person and assistant messages on both layouts. The Android Debug APK built with **0 errors, 114 warnings**, installed over PixieDust, and the [live screenshot](2026-10-28-pixiedust-copy-verified.png) shows the final assistant answer marked **Copied** with Android's clipboard preview. Pasting into the composer produced the exact opening of that answer in the Android text field. The test draft was cleared without sending. This closes the narrow inability-to-copy defect; it does not establish all of P13's formatting requirements or Desktop live acceptance.
- During the first live run, the new visible run line read **“Working on this device · Running.”** After the service-state wording fix was built (0 errors, 105 warnings), installed, and rechecked with the salon prompt, the [intermediate screenshot](2026-10-28-pixiedust-progress-live.png) showed a readable sentence with a repeated location phrase. A final wording adjustment passed **13 focused queue tests**, built with **0 errors, 96 warnings**, installed over the app, and produced the [final live progress screenshot](2026-10-28-pixiedust-progress-final.png): **“Working on this device · Circle AI is processing your question.”** This proves the terse `Running` state is translated and displayed. It still lacks a preparation/inference split, percent where available, and tool/source/skill receipts; P02 remains open.
- The running Desktop Concierge window showed **“Circle AI is not ready on this device”** and connection refused at `127.0.0.1:53771`. No Circle AI process or registered Windows service was found at that moment. A fresh Desktop response-quality run is blocked by that live service state; it is not scored as an answer failure or claimed complete.
- A fresh Windows `dotnet build Concierge/Concierge.csproj -f net10.0-windows10.0.19041.0 -c Debug --no-restore` from this checkout succeeded with **0 errors and 40 warnings**. This verifies compilation of the shared Desktop code; it does not verify the Desktop UI or service answers. A subsequent Windows service inventory still found no Circle AI or Concierge service registration.
- The queue progress translator now gives readable sentences for `WaitingForPermission` (including the named permission when supplied) and `Paused` (Android may later resume the same request), as well as the already verified queued/setup/model/running states. **13 focused queue tests passed** with coverage enforcement disabled for this narrow test selection. These two new states have not been observed live on PixieDust; this is code and contract evidence only.

## Status

On PixieDust, all **five** response-quality tasks have now been run in fresh threads on the current build: Tasks 2 and 4 before the raw-question correction; Tasks 1, 3 and 5 afterward. Task 3 was also measured before the correction to isolate the shortcut defect. All five failed their full acceptance criteria. Tasks 2 and 4 should be rerun after a meaningful quality change. Desktop's five-task baseline is incomplete. This evidence does not close P00 or P12.
