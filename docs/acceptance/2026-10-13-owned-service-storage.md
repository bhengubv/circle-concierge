# Concierge-owned Windows service storage and StarDust proof

## Storage and service

The Windows Circle AI 3.8.1 installation now uses
`C:\ProgramData\Concierge\CircleAIService` for models, runtimes, service profile
folders and the client connection file. Binaries remain under Program Files.
The signed-in user has FullControl on the data root; the virtual service account
has Modify. SYSTEM and Administrators have FullControl. The connection key file
has a separate restricted ACL. A user write/read/delete probe passed.

The import copied 33 existing cache files. The original cache was subsequently
measured at 35,410,223,670 bytes and remains intact. Robocopy reported zero failed
files. SHA-256 of the active `llm.mnn.weight` matches between old and new caches:
`4E25EF4CBFB49A33013D17300C2F68CD9C9CE8F2E3F7B947FD2C574EC297CA5E`.
The old user-cache service grant was read back as absent.

SCM launch arguments explicitly name the new model/runtime paths. The client
reads its connection file from CommonApplicationData rather than LocalApplicationData.
The service's profile environment paths also belong to this directory.
Authenticated diagnostics reported Circle AI 3.8.1.0, loaded
`Qwen3.5-0.8B-MNN`, and successful native preloading from the owned runtime cache.
Unauthenticated `/v1/models` returned 401. Concierge reported `canAnswer: true`.
No `Concierge.Model.Host` process was present.

The released runtime still reports an optional flatten/copy permission error in
its read-only Program Files native directory. Preloading from the writable cache
succeeded, and real inference worked. No write permission was granted on the
service binaries to conceal that warning.

## Answer tests and collision fix

The first full Concierge UI arithmetic turn returned **9** for 6 + 7, taking
56.616 seconds. A simultaneous real watch request returned a service 503.
These were failed acceptance attempts, not successes.

A direct service request with temperature 0 and a 16-token limit returned **13**
in 0.5969088 seconds. The client was then changed to deterministic generation
and a shared inference semaphore, matching the service's one-request limit.
A new actual Desktop UI turn returned **13**, with durable start/end timestamps
showing 38.318 seconds. This verifies that answer; it does not establish general
model accuracy, and the full-context latency remains a product gap.

The queue regression test was deliberately broken by allowing two concurrent
requests in the test build: it failed. The one-request gate was restored, and
the queue/cancellation and durable-receipt tests passed 9/9. Windows Debug built
with 0 errors and 70 warnings; existing package vulnerability warnings remain.

## StarDust

ADB identified `192.168.0.241:36333` as StarDust / Pixel Watch 3 / WearOS, with
`com.companyname.concierge.wear` installed and armeabi-v7a support. Its saved
Desktop address was configured as `http://192.168.0.250:5198`. The running
Desktop registry then listed both Desktop and StarDust as connected.

A pre-existing queued watch request reached Desktop, executed through the
service, and left a completed durable answer receipt. The watch reconnect path
discarded that answer in favor of a delivery summary. A new test exposed this
and failed; the fix retains the latest flushed answer and displays it alongside
the delivery summary. The focused tests then passed 16/16. The watch also shows
Thinking while a queued delivery is in flight.

The updated ARM32 watch APK built with 0 errors / 19 warnings and installed with
`adb install -r` successfully. Existing app data was preserved.

The physical-device test used `AwayOutbox.Keep` to serialize one labelled input,
then placed that unique file in StarDust's outbox through ADB. The actual watch
app sent it through its ordinary reconnect path. Request
`04bec064-a44f-437a-8ce4-7a06161fb830` asked eight plus five. Desktop recorded the
run at 01:14:03.168 and its terminal receipt at 01:14:50.696: 47.528 seconds.
The service returned **Seven**. The watch removed the delivered outbox item and
displayed **Sent what was waiting. Seven** in its audiogram. This proves actual
watch → Desktop → service → watch delivery/display, but **fails answer accuracy**.
The screenshot shows the real wrong answer; it has not been substituted or edited.

The same question directly sent to the service returned **13** in 1.4858512
seconds (nonstreaming), and **13** in 4.2521138 seconds with the same streaming
and temperature settings as the client and no token-limit override. Thus
streaming alone does not explain the discrepancy. The full Concierge context
remains the outstanding accuracy/performance problem. The source currently
compacts conversation history before adding identity and the complete tool
catalogue, so that budget does not measure the full outgoing request. This is
a code finding and possible contributor, not a demonstrated sole cause.
Guidance on the intended service tool/context contract was requested from the user.

A restart also exposed double-counted memory: the client re-assessed allocation
fit despite the service already owning a loaded model. The client now attaches
to the single loaded streaming model first; a real restart reported ready.
Final focused tests (resident-model classification, request serialization,
cancellation, watch replies and durable receipts): 20/20 passing. This is not
a full-suite or release acceptance claim.

Acoustic microphone recognition was not part of the queued-input test. The
requested fresh spoken test has not yet been confirmed by the user. Multiple
deferred answers, an unread-response inbox, and persistence of displayed answers
across watch process death remain open; this change shows the latest flushed
answer only. A real earlier spoken inbox-feature request reached Desktop but
received the pre-fix 503 and was incorrectly recorded as a completed turn.
Transport error terminal-state handling also needs correction.

Evidence logs reside under `%TEMP%\ConciergeDeviceProof`; Desktop screenshot:
`2026-10-13-desktop-service-math.png`, `2026-10-13-stardust-queued-answer.png`.
