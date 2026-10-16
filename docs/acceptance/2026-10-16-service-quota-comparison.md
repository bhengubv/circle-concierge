# Service quota and published-build comparison — October 16, 2026

## Measured baseline

| Observation | AngelDust | PixieDust |
| --- | --- | --- |
| OS release / API | Circle OS, Android 16 / 36 | Android 17 / 37 |
| OS build | Circle/circle_arm64/generic_arm64:16/BP4A.251205.006/eng.geektr:userdebug/dev-keys | google/lynx/lynx:17/CP3A.260905.009/16091614:user/release-keys |
| Service versionCode / target SDK | 56 / 36 | 58 / 36 |
| Boot uptime from `/proc/uptime` | 3,093.55 seconds (about 52 minutes) | 605,041.72 seconds (about 7 days) |
| Effective `data_sync_fgs_timeout_duration` in Activity Manager settings | 21,600,000 ms | 21,600,000 ms |
| Effective crash extra wait | 10,000 ms | 10,000 ms |
| DeviceConfig override for dataSync duration | `null` (no explicit value returned) | `null` (no explicit value returned) |
| Foreground service type | `0x00000001` / dataSync | `0x00000001` / dataSync |
| ServiceRecord age at baseline | 17m54.204s | 1d22h47m20.641s |
| Most recent restart age at baseline | 17m54.202s | 2h28m29.235s |
| Standby bucket | 5 | 10 |
| RUN_ANY_IN_BACKGROUND app-op | Default allow; no explicit operation | Default allow; no explicit operation |

ServiceRecord age is not current process age or consumed quota: PixieDust's record survived a process restart. Boot uptime is not consumed quota either. No per-UID cumulative quota or remaining-time counter was obtained from these dumps; equal configured limits do not establish equal enforcement or available runtime. Both compatibility dumps list `FGS_INTRODUCE_TIME_LIMITS` (317799821), enabled by default starting at target SDK 35; no package-specific exemption appeared in the filtered output. This is not a per-package enabled-state probe.

The prior PixieDust crash log explicitly identifies an exhausted dataSync foreground-service allowance and a timeout exception. AngelDust's latest recorded service exit remains October 13. The shorter AngelDust observation and different uptime are a plausible explanation for the observed difference, not proof that the OS or service build is responsible.

Android documents six hours per 24-hour period for dataSync, resetting when the owning app is brought to the foreground, and termination if the service fails to stop after timeout. These are the service app's limits, not a promise that opening its consuming Concierge client resets them. [Official Android timeout documentation](https://developer.android.com/develop/background-work/services/fgs/timeout).

## Published release checked

The live GitHub Releases API lists v3.8.1, published October 6, with these Android assets:

- `CircleAIService-3.8.1-vc58.apk` (91,234,333 bytes), the service.
- `CircleAI-3.8.1-vc58.apk`, the separate application; it is not substituted for the service.

The service asset's published SHA-256 is `0a79edc8426af5c61a881fdb2aa79720642f75ba2efbc2357a94d683868af43a`. A freshly pulled copy of PixieDust's installed APK has exactly that SHA-256. No debug-signed alternative appears in the v3.8.1 release assets. [Published release](https://github.com/bhengubv/CircleAI/releases/tag/v3.8.1).

Android SDK `apksigner verify --print-certs` validated both APKs and reported different certificate SHA-256 fingerprints:

- AngelDust build 56: `133a6b1f6ad10d33e1b2abf5e43ceee56f370d2ff87ba0f482000689a33ce8c5` (Android Debug).
- Published/PixieDust build 58: `ea7ceb371f1b83f43e47fc67579b99aca1f336ca2add5d1f4b6186f03e1546ed` (release).

AngelDust's existing APK was copied to an untracked local backup before an in-place update was attempted. Android rejected `adb install --no-incremental -r` with **INSTALL_FAILED_UPDATE_INCOMPATIBLE**, explicitly citing the signature mismatch. No uninstall, data clear, model modification, re-signing or quota override was performed. No Circle AI source repository was accessed.

## What remains unverified

**The same-build/comparable-uptime experiment has not been completed.** It requires a build signed compatibly with AngelDust, or an explicitly agreed and verified data-preserving migration to the release signature. The published release alone cannot update this installation in place. No inference or OS-immunity conclusion is drawn from this blocked comparison. Preserve models/settings/history before any later uninstall-based migration; a backup of the APK alone is not a backup of its private data.
