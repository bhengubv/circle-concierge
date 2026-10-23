# Concierge client package upgrade — 23 October 2026

**Subsequent result:** authorization was completed, the queue consumer and safety
wrapper were corrected, and both final phone clients answered locally and synced
their answers to Desktop. See [the later acceptance](2026-10-23-service-queue-acceptance.md).
The pending observations below describe the earlier APK at the stated time.

## Source and package boundary

Concierge directly consumes CircleAI 3.9.1 and AetherNet 3.4.0. The old direct
Aether.Core 1.0.1 reference was removed. Mesh adapter and test namespaces now use
the unified AetherNet assembly through an explicit compile alias. CircleAI 3.9.1
still transitively requires modular AetherNet 3.0 assemblies; these have not been
removed or falsely reported as upgraded. The separate media package is unchanged.

The CircleAI package merges Android host/sample components into consumers. On
StarDust the merged sample BootReceiver started an embedded CircleNeuronService
after package replacement, followed by ForegroundServiceDidNotStartInTimeException
at 04:02 JST. Concierge is a client of the standalone service, so phone and watch
manifest sources now exclude those host components. The generated-manifest build
guard rejects their reintroduction. A deliberately bad manifest fixture failed;
a client-only fixture passed. Generated production manifests were not hand-edited.

Wear ARM32 builds use `-p:ConciergeWearRuntimeIdentifier=android-arm` so a global
runtime identifier does not request a nonexistent ARM32 CoreCLR pack for the
portable Shared project.

## Measured verification

- Windows build: 0 errors, 49 warnings.
- Final phone build: 0 errors, 110 warnings.
- Watch diagnostic build: 0 errors, 29 warnings.
- Existing LAN mesh tests against AetherNet 3.4.0: 12 passed.
- Live-readiness/settings/runtime focused tests: 47 passed.
- Full suite remains blocked by the published CircleAI dependency graph bringing
  Microsoft.AspNetCore.App into browser-wasm. No full-suite pass is claimed.
- Dependency security warnings remain, including MailKit, MimeKit and
  System.Security.Cryptography.Pkcs. Older baseline claims of zero vulnerable
  packages do not describe this upgraded graph.

At approximately 13:06–13:07 JST the corrected phone APK was installed in place
on AngelDust and PixieDust. Both launched, restored existing conversations and
reported three connected devices. No models, settings or history were wiped.
Phone APK SHA-256:
`6E98FC3DF8E77AE3E34408AB415DE5D1AC93D731AEF5757E0B22FEAA0227D923`.

Both Settings panels connected to their standalone local service but reported
text readiness as starting/Not reported. Preparation opened the service-owned
authorization screen on each phone. Human authentication is pending; no fresh
local answer is claimed for this final APK. PixieDust's crash buffer also records
a standalone build-60 dataSync foreground-service timeout at 04:53:01 JST.
This is a service failure, not a new Concierge client crash.

The earlier AngelDust SDK-upgrade build did answer the 14:00 appointment /
25-minute travel question correctly as 13:35 in 8997.9 ms. That evidence predates
the final client-boundary APK and is not substituted for its acceptance.

## Installed services are separate releases

- AngelDust and PixieDust standalone Android service: build 60, release 3.9.1.
- Windows installed service: 3.8.1, verified from its binary and HTTP diagnostics.
  Updating the client NuGet package has not upgraded this service.
- StarDust: ARM32. Published CircleAIService 3.9.1 APK contains only arm64-v8a and
  x86_64 native libraries; no compatible standalone service has been installed.
  Watch input currently executes on Desktop. See the separate watch acceptance.

## Local evidence and reproduction

Build logs: `work/2026-10-23-aethernet-3.4-windows.log`,
`work/2026-10-23-android-client-boundary-build.log`,
`work/2026-10-23-watch-input-gestures-build.log`.
Tests: `work/2026-10-23-aethernet-3.4-mesh-tests.log`,
`work/2026-10-23-aethernet-3.4-readiness-tests.log`.
Guard: `work/2026-10-23-client-boundary-red.log`,
`work/2026-10-23-client-boundary-green.log`.

Phone build command: `dotnet build Concierge/Concierge.csproj -f net10.0-android -c Debug`.
Watch command: `dotnet build Concierge.Wear/Concierge.Wear.csproj -c Debug -p:ConciergeWearRuntimeIdentifier=android-arm`.
