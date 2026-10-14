# Android service consumer and setup

Concierge consumes the installed Circle AI service. Circle AI owns resource
selection, preparation, session history and inference. The consuming app exposes
supported setup commands and progress in its own Settings panel; it does not
require the separate Circle AI controls app.

## Implemented

- Android chat uses the published Binder chat link and durable conversation ID.
  Concierge's isolated model worker and Android download coordinator are no
  longer registered as the Android chat/preparation owner.
- Local ready conversations stay on the phone even when Desktop has more RAM.
  Setup or authorization does not silently transfer the query. Explicit capability
  unavailability or local failure can select a ready Desktop; manual Use Desktop
  remains available independently.
- Settings reads the service's setup plan, invokes SetupStart only from an explicit
  button, and polls SetupProgress without blocking the UI thread. The published
  SDK documents the fraction as byte-weighted across the whole planned download;
  the initial per-resource label was wrong and has been corrected. Current step,
  phase and the service's remaining-time estimate are retained. Readiness comes from the
  separate service readiness report, never from a 100% download.
- Opening Settings performs reads only. Closing the panel stops its polling, not
  service-owned preparation. Failed progress checks clear the stale percentage.
- Android no longer sends users to the sample/controls app for settings. Android
  permission management and service-owned connection consent still use their
  respective platform approval screens.
- Connection and conversation readiness are visibly separate. Approval refreshes
  reports automatically; Refresh status shows its completion time and readiness.
  Reopening the panel during Fetching/Checking does not enable duplicate setup.

## Measured verification and limits

- The updated Android builds were installed on PixieDust successfully.
  The service reported NeedsSetup; its setup plan reported 1,386,691,327 bytes for
  the brain and 311,428,914 bytes for the eyes (1,698,120,241 bytes total).
- The focused runtime, routing, settings, handheld, and durability suite passed
  73 tests with zero failures after fixing connection/refresh feedback. Tests cover no download
  on panel opening, genuine service progress, invalid progress rejection, setup
  refusal, local-first routing and manual transfer visibility.
- Latest Android Debug build passed with zero errors and 175 warnings, elapsed
  2 minutes 42.14 seconds. Warnings include existing package vulnerability and
  native-library compatibility reports; a successful compile does not clear them.
  Both installs returned Success. Screenshots and live UI checks were performed
  on PixieDust, using its existing pairing.
- User approved starting service setup. Circle AI accepted SetupStart and reported
  Fetching, Checking and Done through Concierge. The UI remained responsive while
  changing settings sections and while manually sending a query to Desktop.
- Normal Send during NeedsSetup retained the query on the phone. Desktop's prompt
  diagnostic remained at 14:01:32.1339814 JST. Manual Use Desktop then updated it
  to 19:34:39.4378862 JST; its reply at 19:34:54.101 JST was
  "Two plus three equals **five**." The conversation event identifies the producer
  as "Qwen3.5-0.8B-MNN (Circle AI service)".
- On the updated client, Refresh status completed at 19:52:55 JST. Reconnect
  obtained native service consent GRANTED at 19:53:08.371 JST, automatically
  refreshed the report, and displayed Connected to Circle AI service.
- **Local inference is still blocked by the service's reported readiness.** After
  setup reached Done/100%, SetupReadiness remained NeedsSetup/canTalk=0 and
  SetupPlan returned no rows. SetupCensus reported brain and eyes Present, but
  English voice, South African voices, ears and wake word Missing. One setup retry
  completed immediately without changing readiness. No model/file provisioning
  logic was added to Concierge to bypass that service-owned plan.
- No actual Android service answer is claimed. A prior answer from Concierge's own
  worker failed arithmetic and is not evidence for this integration.
- The installed SDK link has no language, cache-limit or borrowing preference
  getters/setters. Concierge labels those gaps rather than storing ineffective
  local switches. Resident/wake mutation verbs exist but are not yet surfaced in
  this settings panel.
- Android chat currently sends Agentic=false. Native service actions are not
  enabled without a verified approval/cancellation contract. Text replies arrive
  as one completed reply because this published Binder chat link does not stream
  chunks. These are real differences from the Windows service adapter.
- Older client-only conversation history is not imported into the service session.
  The service receives the current user query and owns history from its first
  service turn; Concierge retains its displayed conversation log.

The settings controls and manual Desktop path have practical verification. Local
Android answers, native action approvals and missing preference setters remain
explicit acceptance work.

![Verified connection and refreshed readiness](2026-10-14-pixiedust-service-controls.png)

![Service inventory after its download plan completed](2026-10-14-pixiedust-service-missing-resources.png)
