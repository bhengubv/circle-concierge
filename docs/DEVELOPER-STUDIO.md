# Concierge Code

## Creative Project workspaces (20 November 2026)

The Project page now offers Code, Games, Video, Audio, and Images. Code keeps the existing `/projects/{id}/studio` route and source folder. The four creative routes use `/projects/{id}/studio/{kind}` and launch the separate Concierge Code host with `studio --project GUID --kind games|video|audio|images --data-dir APP_DATA_ROOT --no-browser`. Concierge reads the installed host's `capabilities` response and disables a creative Open action if that host does not support it. The host writes outputs under `projects/{GUID}/studio/outputs/{kind}` inside that same app data root. Concierge validates the version 1 ready event, Project ID, kind, route, output root, and token location before embedding the loopback view.

The current host provides local starters: a browser game, a canvas animation exported as WebM, a tone sequencer exported as WAV, and procedural PNG art. These are working first slices, not integrations with Orbit, Nota, YuE2, SurgeXT, VCV Rack, or an image model. The larger engines and models require separate adapters, license review, resource checks, and live acceptance. On a device without the external host, the internal editor remains Code-only; a creative workspace reports that it is unavailable.

The shared UI and Desktop builds pass, and four focused bUnit tests cover the five Project routes, launch dispatch, invalid kinds, and an older host with unavailable workspaces. The full Concierge suite passed 2,734 tests before the final capability gate; its focused test passed afterward. The standalone host has separately tested its four output formats and loopback authentication. Playwright connected to the running Desktop WebView2 with a host built from ConciergeCode `8769b4b` and verified that Games, Video, Audio, and Images each open and save from a Concierge Project. Video also remained in the output list after reload and reopened in Preview. The Windows computer-use helper could not bind to the MAUI window; the WebView2 debugging target provided the live check instead. The output list currently shows type and timestamp, not the title entered before saving; that discoverability gap is tracked in the standalone Code chat.

## Standalone host and embedding (20 November 2026)

The editor host now lives in the private `bhengubv/ConciergeCode` repository. Its Windows distribution is built separately with `dotnet publish ConciergeCode.csproj -c Release -r win-x64 --self-contained false -o dist/win-x64`. To include that exact build in Concierge Desktop, build Concierge with `-p:ConciergeCodeDistribution=C:\path\to\dist\win-x64`. MSBuild copies it under the Desktop output's `ConciergeCode` directory. A local development launch may instead set `CONCIERGE_CODE_PATH` to `ConciergeCode.exe`. When neither is present, the previous internal Windows editor remains available during migration.

Concierge starts the external host for the selected Project, checks its version 1 ready event and local token file, and opens its loopback workbench in the Desktop window. The Project source remains under Concierge's app data. Concierge owns B! and Project workflows; the standalone host owns the editor runtime and extensions. The separate build compiles, its four contract tests pass, and its live standalone loopback server accepted the token, rejected an unauthenticated request with HTTP 403, and stopped cleanly. A Concierge Desktop build with the host distribution included passed. The embedded Video workspace, save, output-list reload and saved-video preview were later verified through Playwright on the Desktop WebView2.

Concierge Code is an optional Desktop tool for code in a Concierge Project. The person opens it from the Project page. On first use, Concierge downloads a pinned OpenVSCode Server release into the default WSL distribution, checks its SHA-256 digest, and starts it on the Windows loopback interface with a fresh connection token. The editor opens in a full-screen view inside Concierge. The Project's source folder stays in the person's local Concierge app data. The “Ask B!” action opens a Project-linked Concierge conversation powered by Circle AI; the embedded workbench's separate agent panel is not connected to it.

The tool is currently supported on **Windows x64 with WSL**. It is not installed or started during Concierge startup. Its server is stopped when the editor closes. A recorded Linux process ID lets the next open clean up an editor left behind by an abrupt Desktop exit. No editor port is exposed to the LAN or Internet.

The editor uses a stable loopback port when available so its web client can retain the person's theme and layout choices across restarts. If that port is occupied, it uses an available port for that session; web client preferences may not carry to that temporary origin. Concierge changes the workbench's product title, package display name, and English display-message catalogue to Concierge Code after verifying the pinned runtime. It includes a first-party Circle Metro Dark editor theme and default preferences for a fresh browser profile. The upstream license and attribution remain intact. The workbench can still show its own agent panel; that panel is not Circle AI. The web client stores its explicit appearance and startup choices in browser storage, so the person can override the defaults.

The editor has first-party Circle Metro Dark and Light themes, with accent colours drawn from the Circle Metro token palette. A small Concierge-owned style layer rounds the pinned workbench's tabs, inputs, menus and visible panels. It is applied after the local editor loads and must be visually checked when the pinned editor version changes. The person can still select another installed theme.

The workbench is based on the open-source OpenVSCode Server and offers compatible extensions through Open VSX. People choose a language setup; Concierge installs its extensions only when selected. The offered setups are:

| Language | Extension IDs |
| --- | --- |
| C | `llvm-vs-code-extensions.vscode-clangd` |
| C++ | `llvm-vs-code-extensions.vscode-clangd` |
| Rust | `rust-lang.rust-analyzer` |
| Swift | `swiftlang.swift-vscode` |
| Go | `golang.Go` |
| C# | `muhammad-sammy.csharp`, `EditorConfig.EditorConfig` |
| Java | `redhat.java` |
| Kotlin | `fwcd.kotlin` |
| ArkTS | `arkts-dev.arkts-syntax-highlighter` |
| Python | `ms-python.python`, `ms-python.debugpy`, `charliermarsh.ruff` |

The C and C++ setups share clangd, so installing either marks both as installed. “Installed” means the listed editor extensions appear in the local extension inventory; it does not assert that a compiler, SDK, language server, or debugger is ready. Those tools must be installed separately where required. Extensions are fetched directly from the registry when chosen; they are not bundled with Concierge. A person can also install a VSIX they choose. Extension availability and licenses must be reviewed individually; Microsoft Marketplace access is not assumed. Server data and installed extensions live under Concierge's own `developer-studio` app-data folder, under the same app-data root as Project source. Web client appearance and layout live in WebView browser storage. A future Concierge data export must cover all three; this change does not implement that export. The downloaded runtime is not checked into this repository. OpenVSCode Server's license is MIT; preserve its notices if a future installer redistributes it.

This is the **create and edit** portion of the production path. Concierge can use Project workflows and its tools to plan and review code. Publishing to SleptON! remains separate: its current `publish` command is declared but has no handler wired to it, while the available subprocess adapters are read-only. Live release submission and final approval need a running service and an end-to-end acceptance test. Concierge Code does not publish by itself.

Acceptance evidence for the Windows pilot: an editor opened from a Concierge Project; a `README.md` edit was saved to the Project's local source folder; the extension catalogue populated; a request without the token returned HTTP 403; and closing the editor stopped the loopback listener and its Linux process. After a forced Desktop exit left the editor process running, opening the same Project again stopped that stale process and started a fresh editor. Cross-platform editor packaging and live SleptON! publishing are not verified.

On 20 November 2026, the live Desktop check exposed a CRLF startup-script failure; normalizing the script before WSL execution fixed it. Python and C# setup installed their chosen extensions and the app later reported both packs installed. Both the page and editor toolbar opened a fresh, project-linked Circle AI conversation. The editor opened the Project source in restricted mode. The installed Circle Metro Dark theme appeared in the editor's theme picker and rendered the blue rail, navy panels, and blue status bar after selection. The existing browser profile initially retained its light-theme choice; the default appearance on a wholly new profile is not verified.
