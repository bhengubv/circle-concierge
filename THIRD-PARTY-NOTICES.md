# Other people's work, and what its licence asks of us

Concierge is held to a rule: everything it carries is free and open source under a
permissive licence — MIT, Apache, BSD, OFL or public domain. Two things in this file
are carried under that rule, and **one is not**, which is why this file exists rather
than a line in a README.

## Carried in the repository, permissive

| What | Where | Licence |
| --- | --- | --- |
| three.js | `Concierge.Shared.Components/wwwroot/lib/three` | MIT — `LICENSE` beside it |
| mermaid | `Concierge.Shared.Components/wwwroot/lib/mermaid` | MIT — `LICENSE` beside it |

Both are carried rather than fetched from a CDN, for a reason written up where they
live: a design surface that needs the internet to draw a box is not a local-first
product.

## ffmpeg, on Android only — LGPL, and redistributed

**This is the exception to the rule above, taken deliberately.**

ffmpeg is LGPL-2.1-or-later. On Windows, macOS and Linux that has never mattered
here: Concierge *finds* ffmpeg on the machine and runs it as a program, the way a
shell does. Nothing is redistributed, so no obligation attaches.

Android cannot work that way. An APK carries no executables beside the app, and
Android has refused to run a binary out of the app's own data directory since API 29.
So on that head ffmpeg ships **inside the app** as shared libraries, and shipping it
is redistribution.

| | |
| --- | --- |
| What | ffmpeg 8.1.2, LGPL build (no `--enable-gpl`, no `--enable-nonfree`) |
| How it arrives | NuGet: `FFmpegKit.Net.Full.Android` 8.1.2.5, wrapping `ffmpeg-kit-full` 8.1.7 |
| Where | `Concierge/Concierge.csproj`, in an Android-only `ItemGroup` |
| ABIs | `arm64-v8a`, `x86_64` |
| Licence | LGPL-2.1-or-later; LGPL-3.0 text ships inside the package under `licenses/` |
| Upstream source | https://ffmpeg.org/download.html and https://github.com/arthenica/ffmpeg-kit |

**The `Full` variant, not `FullGpl`.** That is the whole difference between an LGPL
build and a GPL one in this package's naming, and it is the reason the Android head
encodes H.264 with `libopenh264` rather than `libx264` — x264 is GPL and is not in
this build. The export asks the encoder what it has rather than assuming, which is
how that was found.

**What LGPL asks, and how it is met.** The libraries are dynamically linked shared
objects that the app calls; they are not statically linked into anything, so a
recipient can replace them with their own build of ffmpeg by swapping the `.so` files
in the APK. The licence text ships with the app inside the package, the version and
origin are recorded above, and Engineering names the encoder on screen under
**Media read and written**.

**Anybody redistributing a built Concierge APK inherits this.** Keep this file with
it, keep the licence text in the package, and be able to point at the upstream source
for the exact version above.

## What this is not

Nothing here is vendored source we have modified. Every entry is upstream, unaltered,
at a pinned version.
