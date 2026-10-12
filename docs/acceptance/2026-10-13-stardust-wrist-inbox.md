# StarDust: wrist inbox and native controls — 13 October 2026

## Implemented

- Home keeps the B! audiogram. An Inbox link, upward/leftward swipe, or long press opens the secondary workspace.
- Conversations reads Desktop's local-owner catalogue (latest 50), shows new activity markers, opens actual messages, and continues the selected conversation by voice. New conversation assigns a durable shared ID.
- A bounded cache retains up to 100 messages per opened thread. Read markers survive process restart. Earlier message pages can be fetched while connected.
- Queue shows actual persisted unsent requests, delivery retry, and conversations working, interrupted or needing retry. This is not the Project agent scheduler.
- Status shows current readiness, reported preparation/download activity (progress only when supplied), approvals, and saved run outcomes. An orphaned start is not labelled working without an active Desktop run. Historical stream-error receipts are labelled Needs retry.
- Crown input scrolls native lists. Native Wear SDK primary gesture talks on Home or scrolls secondary screens; dismiss navigates back. Approvals retain explicit Yes/No touch, horizontal swipe, and speech controls; native primary opens speech for that decision.
- Native gestures register only while the app is resumed. No accelerometer guesses or private Binder APIs are used. Optional Wear SDK loading leaves touch/crown available on unsupported watches.
- Inbox reads time out after three seconds. Foreground polling stops on pause. Execution requests keep their separate longer timeout.

## Automated verification

24 focused tests passed: WristInboxTests, WatchHandsTests, WatchInteractionTests. Includes SQLite projection bounds and owner isolation, latest run receipt, actual transcript words/status, restart persistence, unread markers, selected-conversation HTTP routing, and a slow server timeout.

Unread detection was deliberately disabled in the implementation. The restart/new-reply test failed (one failed, zero passed), then the original source was restored and all 24 passed. The broken implementation was never deployed.

ARM32 watch build: zero errors, 19 warnings. Windows Desktop build: zero errors, 70 warnings. Existing dependency security warnings, Android target API mismatch and ONNX page-size warning remain.

Logs: `%TEMP%/ConciergeDeviceProof/wrist-final-tests.log`, `wrist-unread-red.log`, `wrist-final-build.log`, `wrist-final-desktop-build.log`.

## Physical device evidence

Device: StarDust, Pixel Watch 3, Android API 37, ARM32. Native IWearGestureService exists. Public SDK registration logged Double pinch / Wrist turn. Injected native double pinch scrolled the secondary menu; injected native wrist turn returned Home. These are OS-delivered gesture tests, not proof of physical hand recognition while worn.

Installed the ARM32 debug APK in place, preserving pairing and history. With Desktop closed, opened From the watch and read cached messages under an explicit Offline label. Restarted Desktop, watched the menu return to Connected to Desktop with 17 new conversations, opened Queue and Status, and read Ready to answer plus saved run outcomes. Selected a real thread and viewed its actual transcript.

On the final feature build, IWearGestureService reported both DoublePinch and WristTurn. Injected WristTurn returned Queue to the menu; DoublePinch scrolled Status. Injected rotary-encoder SCROLL input scrolled further. Off-body test overrides were reset afterwards. A last UI-only correction changes the empty queue heading to No pending delivery and hides Retry when nothing is queued.

Screenshots: `2026-10-13-wrist-inbox.png`, `2026-10-13-wrist-offline-history.png`, `2026-10-13-wrist-conversation.png`, `2026-10-13-wrist-queue.png`, `2026-10-13-wrist-status.png`, `2026-10-13-wrist-pinch-scroll.png`, `2026-10-13-wrist-crown-scroll.png`. These are unedited physical-watch captures. The pictured Seven is the previous incorrect AI answer, not a passing accuracy test.

## Boundaries

The watch is still a thin Concierge client. Desktop remains the execution endpoint in this proof. No fake answers or new model selector were added. The previous eight-plus-five response Seven remains in real history: this work does not resolve the separately recorded Circle AI full-context accuracy failure.

Foreground refresh is ten seconds; this is not background push notification delivery. A complete cross-device unread inbox, background notifications and Project scheduler queue integration remain vision work. Microphone acoustics and physical worn-watch gestures require a human device trial; this run tests injected OS events and the ordinary history/queue UI.

Public API references: [GestureInputManager](https://developer.android.com/reference/com/google/wear/input/GestureInputManager), [native gesture testing](https://developer.android.com/training/wearables/compose/one-handed-gestures). Test overrides must be reset after use.
