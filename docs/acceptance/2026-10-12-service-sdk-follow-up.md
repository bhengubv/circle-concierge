# Shared service and SDK follow-up — 12 October 2026

This is incomplete integration evidence, not acceptance of the product vision.

## Device inventory checked this turn

- Windows: CIM service inventory found no installed CircleAI service. The shell
  is not elevated; the supplied installer requires administrator rights.
- AngelDust: Android package `com.bhengubv.circleai.service`, versionCode 56,
  versionName 0.1; `ai.circle.CircleNeuronService` running, PID 5199. The previously
  attempted data-preserving build 58 update failed with a signing mismatch.
- PixieDust: build 58 installation succeeded in the preceding work, but the
  device is not currently reachable through ADB. Running status is unverified.
- StarDust: connected at `192.168.0.241:39527`; package query returns no CircleAI
  service and service inventory returns nothing. ABI list is armeabi-v7a,armeabi;
  the supplied build 58 APK contains ARM64 and x64, so it cannot install here.

## SDK changes and verification

Production references now use merged CircleAI 3.8.1. Microsoft.Extensions
dependency floors and the Android native asset package path were updated.
The old hand-marshalled WhisperTranscriber is marked unsafe by this SDK.
Concierge now uses WhisperNetTranscriber, its runtime loader, and its bounded
thread default. Existing local voice tests pass (15); live recognition is not
proven. Android package visibility now includes the shared service package;
the modified manifest has now been built and deployed to AngelDust without
clearing its data. The live Engineering service check reached the standalone
service and received `not linked for Chat — approve in Circle AI first`.
Installed build 56 was displayed, authorization was No, and model readiness
remained Unverified. Screenshot: `2026-10-12-service-link-before.png`.

The full production test graph compiled. Test execution with coverage disabled
finished with **2,446 passed, 48 failed, 0 skipped, 2,494 total**. This is not a
passing baseline. Package vulnerability warnings remain unresolved.

An isolated media export failure reports: "There is not enough free memory to
start another managed worker." Free RAM after build-server shutdown was
1,080,676 KiB; the worker guard reserves 1.5 GiB plus a 512 MiB minimum worker.
The guard was retained. Do not attribute every media failure to the SDK without
examining each failure.

The cancellation/restart bundle test fails because weights.bin.tmp is absent
after cancellation. A cancellation-aware HTTP fixture overload was tried and
the test still failed; that speculative fixture edit was reverted. Resume
behavior under this release remains a demonstrated regression against the
required test. Subsequent metadata/IL inspection of the installed net10.0
CircleAI 3.8.1 assembly identified the cause: the bundle failure handler checks
File.Exists and calls File.Delete at IL offset 0x0886 without a cancellation
guard. No partial files remain in the failed test's model directory. The test
was not weakened or suppressed.

The Android SDK-upgrade build completed with 177 warnings and zero errors.
The fresh service-discovery build completed with 164 warnings and zero errors;
it removed the explicit duplicate MNN NuGet asset packaging. Package advisories,
other native duplicates, and 16 KiB page-size warnings remain open. The first
APK's ARM64 MNN bridge hash matched the SDK bridge hash; this is packaging
evidence, not inference acceptance.

Three service-UI tests passed before adding the approval button. The button's
new approval/decline tests and final device flow still require verification.

## Service contract findings and remaining work

The inspected CircleAI source exposes Android cross-app access through
CircleNeuronLinkService with caller package/signature checks and scoped grants.
CircleNeuronConnection is same-process access, not the cross-app connection.
Android linking is therefore not established merely by finding the process.

Correction after inspecting the compiled SDK constants: the released 3.8.1
LinkIpc.HostPackage already names `com.bhengubv.circleai.service`. The older
local source's hybrid package constant was not evidence of a released SDK bug.
Concierge uses the released SDK's constant. No other repository was accessed
after the user's restriction to Concierge and Butler.

The inspected Windows builder registers a Companion factory but no IAIService.
CompanionSession emits an offline reply when that dependency is absent. This is
a source finding; the released binary's Companion behavior is unverified. Both
temporary loopback probe commands were rejected by automatic approval review,
including the authenticated retry. Neither launched a service process.

Next: establish the actual released service contract, preserve automatic model
selection and live readiness, then wire Concierge without duplicate model
ownership. Prove real answers and route → execute → sync on running clients.
Do not present a static capability catalogue as model readiness.

## Evidence

Logs under `C:\Users\user\AppData\Local\Temp\ConciergeDeviceProof`:

- sdk381-production-restore-fixed.log
- sdk381-production-build.log
- sdk381-production-tests.log
- sdk381-focused-tests.log
- sdk381-focused-tests-fixed.log (failed fixture experiment)
- sdk381-media-failure.log
- sdk381-resume-diagnostic.log
- sdk381-download-il.txt
- sdk381-android-build.log
- sdk381-android-service-probe-build.log
- shared-service-ui-tests.log

The currently running Desktop has not been replaced with the migrated build.
No service was uninstalled, device data cleared, model downloads started, or
workflow files changed in this follow-up.
