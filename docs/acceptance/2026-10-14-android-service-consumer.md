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
  button, and polls SetupProgress without blocking the UI thread. Percentages are
  per resource, not an invented overall percentage. Readiness comes from the
  separate service readiness report, never from a 100% download.
- Opening Settings performs reads only. Closing the panel stops its polling, not
  service-owned preparation. Failed progress checks clear the stale percentage.
- Android no longer sends users to the sample/controls app for settings. Android
  permission management and service-owned connection consent still use their
  respective platform approval screens.

## Measured verification and limits

- The previous Android service-chat build was installed on PixieDust successfully.
  The service reported NeedsSetup; its setup plan reported 1,386,691,327 bytes for
  the brain and 311,428,914 bytes for the eyes (1,698,120,241 bytes total).
- The focused runtime, routing, settings, handheld, and durability suite passed
  70 tests with zero failures after adding setup controls. Tests cover no download
  on panel opening, genuine service progress, invalid progress rejection, setup
  refusal, local-first routing and manual transfer visibility.
- Final Android Debug build passed with zero errors and 166 warnings, elapsed
  6 minutes 51.58 seconds. Warnings include existing package vulnerability and
  native-library compatibility reports; a successful compile does not clear them.
  PixieDust disappeared from existing ADB connections before this in-app setup
  build could be installed or visually checked. Only AngelDust remained connected.
- No service setup download was started. Earlier instructions stopped downloads;
  the later permission question has not received an answer.
- A real local service answer and actual SetupStart/download completion on
  PixieDust are **not verified**. The service cannot answer locally until setup is
  completed. A prior answer from Concierge's own worker failed a simple arithmetic
  check and is not evidence for this integration.
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

The final setup build is not an end-to-end acceptance claim. Service setup,
accurate local answers, native action approvals, and missing preference setters
remain explicit acceptance work.
