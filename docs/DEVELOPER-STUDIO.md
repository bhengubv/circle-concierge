# Concierge Code

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
