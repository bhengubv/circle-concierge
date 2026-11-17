# Concierge → SleptON!: spatial game draft

## Why this exists

Concierge is the creator surface. A person can import a measured room into Space, arrange the objects, and ask for a playable game. SleptON! is the market where that creation can be discovered and, after review, published. The creator keeps the private source scan; SleptON receives a small, static browser game and its draft metadata. This gives SleptON a new kind of interactive content while giving Concierge a concrete route from creation to an audience.

## What Concierge now produces

The `design_package_spatial_game` tool appears on native heads with a local SleptON export folder. It packages the current Space frame as a three-signal room game. A minimum of three walls and three objects is required. The draft is written beneath the head's app-data `content/Exports/SleptON` folder. Exporting never publishes or contacts SleptON.

On Concierge Mobile, Creator → Space has a one-tap original sample room and a Play game action. That action creates the same local ZIP and renders the playable game **inside Concierge**. The person returns to the editable room with Back to Space. No browser app, LAN server, or account is required for this Mobile play loop. Immersive AR is **not** part of this in-app loop yet; it needs a native mobile AR implementation and device acceptance before Concierge offers it as an app capability.

The ZIP contains `index.html`, `game.js`, `scene.json`, `preview.mjs`, a local MIT-licensed Three.js module and core, `vendor/LICENSE`, `README.txt`, and `manifest.json`. It has no CDN dependency, source photos, scan mesh, world map, absolute source path, secret, or user account data. Browser pointer and touch play work. Compatible browsers may offer optional VR or tabletop AR. XR has progress and exit controls in a DOM overlay where supported, with a 3D panel fallback. AR is **not** aligned to the original building and does not promise occlusion.

XR needs a secure context and a browser/device that supports the requested session mode. For SleptON's market-hosted copy, open the game as a top-level HTTPS page on an isolated origin with `xr-spatial-tracking` allowed; a sandboxed media iframe is not a substitute ([WebXR Hit Test](https://immersive-web.github.io/hit-test/), [DOM Overlay](https://immersive-web.github.io/dom-overlays/)). Android Chrome with ARCore can be evaluated for that SleptON browser path, separately from Concierge Mobile. Apple documents WebXR `immersive-vr` on [visionOS Safari](https://webkit.org/blog/15865/webkit-features-in-safari-18-0/), while a [WebKit engineer says](https://bugs.webkit.org/show_bug.cgi?id=309550) `immersive-ar` is not supported on iOS. Do not label a listing “AR compatible” until the specific device/browser path passes acceptance.

The manifest uses the existing media draft's byte/hash convention but a distinct `kind: spatial-game-draft`. `schemaVersion` is 1; `status` is `draft`; `published` is false; `entry` is `index.html`; `files` contains each non-manifest path, media type, byte count, and lowercase SHA-256. `rightsConfirmed` is false for normal user exports until a real rights check happens. The synthetic sample is original and marks rights confirmed. No existing `book-trailer` or `audio-draft` importer should treat this as one of those kinds.

## SleptON! work to receive it

1. Add a **draft-only** `spatial-game-draft` importer. Verify the manifest, every path, every size and hash, the package size limit, and the declared browser entry. Reject path traversal, duplicate names, missing files, or unexpected files.
2. Keep imported content private until the creator signs in, reviews its preview, confirms rights, and explicitly publishes. The local draft and online account are separate decisions.
3. Serve the static game files with correct JavaScript MIME types and a restrictive content security policy. Host from an isolated HTTPS origin so game code cannot read SleptON cookies or account storage, and allow `xr-spatial-tracking` there. Launch as a top-level game page for XR.
4. Show a game preview, title, interaction type, and clear AR/VR device requirements. Provide a real play test on phone and desktop before the publish action.
5. Define revision, takedown, and creator-owned export/delete flows. The draft must not become a listing merely because it was imported.

The current SleptON CLI 1.1.0 integration is read-only, and the current importer covers book trailer and audio drafts. This game has **not** been accepted or published by SleptON. The importer, hosted isolation, rights gate and listing workflow are the receiving team's work; no SleptON backend was changed here.

## Reproduce and check

```powershell
dotnet run --project tools/SpatialGameDemo/SpatialGameDemo.csproj -- <export-folder>
./tools/SpatialGameDemo/Verify-SpatialGamePackage.ps1 -Package <zip-path>
Expand-Archive <zip-path> <preview-folder>
node tools/SpatialGameDemo/browser-smoke.mjs <preview-folder> 'C:\Program Files\Google\Chrome\Application\chrome.exe' <mobile-screenshot.png> <desktop-screenshot.png>
```

The browser smoke loads the real room, collects all three signals by pointer, resets to zero, verifies that an XR session rejection restores normal controls, then checks the narrow mobile layout. The `Concierge.Tests` focused exporter and room-import tests cover package hashes, local-only tool availability, measured geometry, and refusal to package an unplayable room. Live AR/VR hardware, SleptON ingestion, and publishing remain unverified.
