# AngelDust Debug acceptance attempt — 24 November 2026

## Question

Does AngelDust reproduce the Desktop original-game response error recorded in
`2026-11-23-desktop-original-game-live.md`?

## Device and build

- AngelDust USB serial `3B221JEHN16653`; `adb devices -l` identified `circle_arm64` / `Circle_OS`.
- Built Concierge Debug for `net10.0-android`, `android-arm64`, APK format. Build: 13 warnings, 0 errors. Installed the signed APK with ADB; `pm` reported `za.co.thegeeknetwork.concierge` version `1.0.1`, code `2`, updated at 16:44:59 device time.
- The installed Circle OS package is `za.co.circleos.circleaiservice`, version name `16`, code `36`, updated 23 October. `pm list packages` found no `com.bhengubv.circleai.service`, the package queried by this Concierge client's shared-service link.
- The installed older `com.companyname.concierge` package was not used or changed.

## Observed result

The new Concierge app opened. It displayed **“Circle AI service could not respond on this device”** and **“Finding Desktop...”**. Its setup card said the phone could not run Circle AI on its own and offered Desktop connection. The screen is captured in [the device screenshot](images/angel-service-unavailable-2026-11-24.png).

I entered the first Task 1 prompt verbatim: “Do you think you can help me create a Donkey Kong knock-off?” The composer showed the complete prompt, but Send remained inactive while no local service or Desktop link was available. No AI request was submitted, so there is no answer or latency to compare with Desktop. **The Desktop five-minute stream error was neither reproduced nor ruled out on AngelDust.** This is a separate service-availability failure.

## Build issue found and resolved

The Debug Android manifest overlaid `android:allowBackup="true"` on a base manifest declaring `false`, causing Android manifest merger error `XAAMM0000`. The Debug overlay now agrees with the base manifest (`false`). The arm64 Debug APK built successfully afterward. Initial sandboxed packaging also failed in `aapt2` with `APT2000` closed-pipe errors; an elevated serial build passed that stage. No service or model installation was changed during this test.

## Remaining acceptance condition

AngelDust needs an installed, authorized Circle AI service that implements the package and link contract consumed by this Concierge build. Then run all four Task 1 turns in a fresh mobile conversation and record the model, transcript, response intervals, and whether failures are shown as failed runs rather than saved assistant answers. Do not score the current unavailable-service screen as a response-quality result.
