# MCP dependency audit — 19 November 2026

## What is connected

This inventory covers Concierge's code and this laptop's local MCP configuration. It does not assert that a configured server is running, that its tools work, or that any vendor will keep distributing it. No credentials or server arguments were copied into this document.

| Connection | Current state | If it disappears |
| --- | --- | --- |
| `phoneuse` | The only enabled entry in this laptop's `mcp.json`; it points to an existing `phoneuse-mcp.exe` outside the Concierge worktree. Its source, license, current tools, and live behavior were not verified in this audit. | Device-control tool calls disappear. Concierge must continue to show the conversation and explain that the device action was not performed. |
| SolidCraft | Built-in, opt-in Windows connector to the separately installed `solidcraft-cli.exe`. It writes an MCP configuration entry only when someone connects it from Projects. It is not in this laptop's current MCP configuration. | Automated SolidCraft modeling stops. The brief, Project and already attached artifacts remain; a native SolidCraft design may still need SolidCraft to edit. |
| Other user-configured servers | Concierge accepts arbitrary local commands in `mcp.json`; none are currently configured here. No MCP servers are bundled by default. | The advertised tool and anything relying on it can disappear without a replacement. Each future server needs its own dependency review. |

MCP itself is a protocol, not a continuity guarantee. Concierge owns its small JSON-RPC client, but the capabilities published by a server belong to that server. The Desktop starts enabled workers as bounded child processes, namespaces tool names, and asks for approval before invoking them. The current worker implementation runs on Windows only. These measures address execution risk, not commercial or product continuity.

## Findings

1. **Core capability exposure:** `phoneuse` is configured as an external executable. Concierge has no verified first-party device-control replacement in this worktree. Treat it as an optional accelerator until the needed phone/watch actions are implemented and tested within Concierge-owned code.
2. **Editable-source lock-in:** SolidCraft exports can be attached to a Project, but the native editable design remains tied to SolidCraft. Require a portable export plus a preview and record the original design's dependency before calling a project portable.
3. **Stale tools after worker exit:** the source previously kept publishing a dead worker's tools. The regression test kills a real test MCP process; Concierge now removes its tools from the live catalogue and reports the worker as exited. This handles a worker vanishing mid-session, not a provider withdrawing its product.
4. **No provider-independent result contract:** a generic tool can return text, but Concierge does not require a durable, app-owned artifact or a stable status/result schema for every MCP workflow. A completed action may therefore be hard to resume with another implementation.
5. **No exit drill for each integration:** the generic MCP tests cover connection, approval and resource limits. They do not prove that a phone-control or CAD project can be completed when its named provider is absent.

## Rule for new integrations

The product direction is to rebuild essential **capabilities as Concierge extensions**, not to make a third-party app or its MCP server the permanent engine. Recreate the user workflow with Concierge-owned code and design; do not copy a third party's protected code, assets, branding or private interfaces. Review licenses and distribution terms before reusing any source or model weights.

Before an MCP tool becomes a primary Studio or Project action, define the capability in Concierge's words, its local input/output format, where its artifacts live, and what the person can still do when the server is missing. Keep source content and project state in Concierge-owned storage. Add an outage test that disables or kills the provider during a real workflow and verifies an honest, resumable result. Build and test the first-party extension as the default path; an MCP adapter may remain an optional bridge during migration or an optional specialist tool afterward.

Initial extension sequence:

1. **Device actions:** inventory the actual `phoneuse` tools used by Concierge, define the smallest cross-device action contract, and implement the Android/WearOS/Windows actions already available through platform APIs. Keep each platform's unsupported actions explicit. Test a watch request reaching a phone or desktop without `phoneuse-mcp.exe`.
2. **3D work:** define a Concierge model/draft format and portable preview/export before replacing SolidCraft automation. Build the basic modeling path in Studio, then keep SolidCraft as an optional advanced editor. Test that a Project opens and exports when SolidCraft is absent.
3. **Every further MCP app:** add it to this inventory before making it visible as a primary action. Give it a first-party extension owner, a portable artifact contract and a provider-off test.

Next reviews: inspect `phoneuse` provenance and actual tool list without assuming its executable is owned by Concierge; identify the portable editable/preview format for SolidCraft work; and audit non-MCP dependencies (cloud models, media services and APIs) separately under the same continuity rule.

**IDE example outside MCP:** Microsoft's Code - OSS repository is MIT licensed, but Microsoft's VS Code distribution and Marketplace have separate terms. A Concierge IDE extension can be evaluated against Code - OSS and independently licensed extensions; it must not assume access to Microsoft's Marketplace or its proprietary extensions. Sources: [VS Code repository](https://github.com/microsoft/vscode), [Microsoft's FAQ](https://code.visualstudio.com/Docs/supporting/FAQ).
