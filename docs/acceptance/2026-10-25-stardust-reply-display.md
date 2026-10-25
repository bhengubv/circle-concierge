# StarDust reply visibility — 25 October 2026

## Finding

StarDust submitted a valid input and Desktop durably recorded the assistant answer, but the
watch home screen remained on “Thinking…” until the original HTTP request returned. The
request also waited for additional status reads after the answer was already in the conversation
log. This made a successful answer look like it had disappeared.

## Change

While a watch turn is in progress, Concierge now checks that watch's conversation log and
shows the matching assistant message in the B! audiogram as soon as it is recorded. Matching
uses the new prompt, the event sequence captured before sending, and the send time, so an old
answer to an identical prompt is not mistaken for the current reply. The original send still
finishes normally and updates approvals/status. The transcript remains in Inbox for reading in
full; the audiogram shows a short visual preview and exposes the full text as its accessibility
description.

## Verification

- Focused `WristInboxTests`: **12 passed, 0 failed**. The normal coverage gate is disabled for
  this filtered run because it exercises only a small slice of the full test assembly.
- Wear ARM32 Debug build: **0 errors**. Existing package vulnerability, Android target API,
  duplicate SQLite native library and obsolete back-navigation warnings remain.
- Deployed in place to the already connected Pixel Watch 3 (StarDust), retaining app data.
- Typed “Hello” on the watch's real on-screen keyboard. Concierge's input callback recorded a
  five-character result. Desktop recorded the user event at 14:55:44.574 JST and an assistant
  event at 14:56:42.079 JST (57.5 seconds later).
- The watch's accessibility tree then exposed that assistant message in the audiogram, and a
  screenshot showed its text preview below B!. The conversation endpoint independently showed
  the paired user and assistant events. Thus the end-to-end text reply path is verified.

This does not verify spoken audio playback; this fix makes the text answer visible on the watch.

## Follow-up: long replies and arrival feedback

The audiogram now keeps three lines visible and automatically scrolls longer answers inside
the ring. It pauses briefly before moving, and respects Android's system reduced-motion
setting. A reply also triggers the watch's standard `Confirm` haptic once; Concierge does not
override the user's global haptic setting.

On 25 October, a real StarDust request produced a 5-item answer. Desktop recorded the user
message at 15:13:49.158 JST and the assistant reply at 15:15:24.805 JST. The watch's vibrator
history recorded Concierge's `Confirm` haptic at 15:15:24.829 JST: two `THUD` primitives,
219 ms total. Consecutive watch screenshots six seconds apart showed different portions of
the long answer in the audiogram, confirming automatic motion without touching the watch.

Evidence: `work/stardust-scroll-haptic-reply.png`,
`work/stardust-scroll-haptic-a.png`, `work/stardust-scroll-haptic-b.png`, and
`work/stardust-vibrator-manager.txt`.
