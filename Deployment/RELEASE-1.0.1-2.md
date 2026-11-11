# Concierge 1.0.1-2 staged candidate

Built from clean source commit `a6c4ccc39150b50a96073d5f5429950e9af87976` on 11 November 2026. The files are staged only. They were not installed, deployed, published, or approved for production.

| Target | Staged file | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| Web | `C:\Deployment\repos\thegeeknetwork\Apps\TheGeek\Concierge\1.0.1-2\Concierge-Web-1.0.1-2.zip` | 268104127 | `c3ae44bb530493fd7df95b296ed0a714828556cd6b12fdfca680dd7a685cc307` |
| Mobile sideload | `C:\Deployment\repos\thegeeknetwork\Apps\TheGeek\Concierge\1.0.1-2\Concierge-Mobile-1.0.1-2-AndroidDebug.apk` | 159404275 | `1c699226a16c3412c771f8855367c271db0e4f01e31aeb3f4ae3a94ae0cba9bb` |
| Wear sideload | `C:\Deployment\repos\thegeeknetwork\Apps\TheGeek\Concierge\1.0.1-2\Concierge-Wear-1.0.1-2-AndroidDebug.apk` | 91827537 | `4b27f8df8b8b9691c1795490ab431237b999050e01d46d119f811d7c341db155` |
| Windows x64 | `C:\Deployment\repos\thegeeknetwork\Apps\Microsoft\Concierge\1.0.1-2\Concierge-Windows-x64-1.0.1-2.zip` | 114838314 | `6e61fb53a10faa0fbb31c95c3439438c704e0c5cde94d2850bd63bac6780dd80` |

Each staged file's byte count and SHA-256 were recomputed and matched its channel's `release.json`. Web's archive contains `Concierge.Web.dll`; Windows' archive contains `Concierge.exe`. `aapt2` confirmed both APK IDs and version `1.0.1` / code `2`. `apksigner` verified their signatures: both use **CN=Android Debug**, so they are not production signed. Mobile targets SDK 36; Wear targets SDK 34 while compiling against API 36.

`Deployment/Test-ConciergeReleasePreflight.ps1` and `Deployment/Test-Stage-ConciergeRelease.ps1` passed. Web, Windows, Mobile, and Wear Release publishes exited successfully. Mobile needed a separate `Concierge.Model.Host` restore before its publish; the current `CopyModelHost` target builds that project without restoring it on a clean checkout.

## Still required before a public release

- The Android production keystore and password were unavailable. No Google Play AAB was staged.
- The two APKs were not run on devices. Web and Windows did not receive live acceptance testing in this staging run.
- Android Release builds reported EF Core trimming and native 16 KB page-size warnings. Wear also reported the target SDK mismatch.
- Windows output is framework dependent and needs the .NET 10 desktop runtime on the target computer.
- iOS and Linux artifacts were not built.

Both staged manifests retain `publishReady: false`.
