The WearOS client is Concierge. Its voice control adopts Butler / B!'s 64-bar audiogram and B! centre; its app icon retains Concierge's Circle ring.

`Resources/values/circlemetro_colors.xml` is an unchanged copy of CircleMetroUI's generated `platforms/android/src/main/res/values-night/circlemetro_colors.xml`. The OLED watch currently uses the dark Circle finish. Regenerate in CircleMetroUI from `tokens/circlemetro.tokens.json`, then copy the output; do not hand-edit these colours.

The ring breathes in idle and changes pace while a request is pending. This animation represents interaction state, not a measured microphone amplitude. System-disabled animations leave a static ring.

The audiogram remains Home's primary control. Inbox is secondary and opens Conversations, Queue, Status, Devices and Gestures. Headers use light Metro typography; panels use CircleMetro raised surfaces and accent text. The crown and public Wear SDK gestures operate native views without WebView. Detailed device verification is in `docs/acceptance/2026-10-13-stardust-wrist-inbox.md`.
