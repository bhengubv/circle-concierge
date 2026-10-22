# StarDust input and gesture investigation — 23 October 2026

## What was measured

Pixel Watch 3, API 37, ARM32; existing ADB connection only. No pairing, data wipe,
model deletion or gesture constraint override was used.

- The public Wear gesture service reports Concierge as a foreground subscriber
  for Primary and Dismiss. Both actions are enabled; off-body state was false.
- An OS-injected DoublePinch opened the native input screen. An independently
  tested WristTurn returned the Inbox menu to Home.
- After the diagnostic build, logs recorded both Java delivery and managed
  handlers: Primary action 1 and Dismiss action 2, with window focus true.
- Physical hand recognition while worn remains **unverified**. These injected
  events do not establish that the user's physical gestures work reliably.
- At 04:31 JST the watch reported 15% battery, not charging, and displayed an
  automatic Battery Saver notification. Its effect on the reported physical
  gesture problem has not been established.

## Input finding

Automated `adb shell input text` displayed words in Gboard but returned a speech
result containing one empty string. That is not proof that Concierge dropped
valid words. Tapping actual on-screen keyboard keys instead returned a two
character result at 04:34:58 JST. Desktop received `Hi`; the watch subsequently
displayed the returned assistant text.

The response was inappropriate to a greeting and Desktop labelled the run as
stopped before completion. This is transport evidence, **not** a passing answer
quality or successful completed-run test. The installed Windows service is still
3.8.1; the client SDK upgrade does not upgrade that separately installed service.

## Changes and build

- Gesture status now reports Primary and Dismiss enablement separately.
- The Gestures screen retains the last received hand event in the current app
  session. Native and managed callbacks log event delivery without user text.
- A successful input callback with no usable words now displays an explicit
  retry message. Debug logging records alternative count and text length only.
- ARM32 Debug build: 0 errors, 29 warnings. Existing dependency security,
  Android target API and duplicate SQLite native library warnings remain.
- Deployed in place. APK SHA-256:
  `F4D470DD09F84E943A46B03050A23BB191F1ACBC7140C631243872C34AB96376`.

Local evidence: `work/2026-10-23-watch-input-gestures-build.log`,
`work/2026-10-23-watch-input-gesture-events.log`,
`work/2026-10-23-watch-gesture-state.txt`,
`work/stardust-menu-before-gesture.xml`, `work/stardust-after-wristturn.xml`,
and `work/stardust-touch-send-result.xml`.

Public contracts: [Wear gesture API](https://developer.android.com/reference/com/google/wear/input/GestureInputManager)
and [OS gesture injection and foreground constraints](https://developer.android.com/training/wearables/compose/one-handed-gestures).
