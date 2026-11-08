# Phone control through Concierge

Phone control is a Concierge tool. The person uses the existing Concierge
Desktop, Mobile, or Wear OS app. A request from the watch can reach Desktop
through Concierge's Away channel; Desktop offers tools from its MCP registry
and records the result in the same conversation.

The `phoneuse` MCP server controls an iPhone through an AirPlay receiver and a
Bluetooth HID bridge on the Desktop host. It is not an Android or Windows UI
controller. No standalone phoneuse client app belongs on the user's devices.

On Desktop, install the phoneuse tool runtime and add an explicit `phoneuse`
entry to `%LOCALAPPDATA%/Concierge/mcp.json` with the absolute path to its
`phoneuse-mcp` executable. Concierge starts it as a bounded child process,
namespaces its commands as `phoneuse__*`, and asks for approval before each
call. The server's `status` tool reports whether the AirPlay mirror and bridge
are actually ready. Without a bridge, action tools cannot control the phone.

For this Windows development installation, the isolated runtime is at
`C:\Development\Concierge\tools\phoneuse-venv`. Its editable package resolves
to this repository's `tools/phoneuse/phoneuse` directory; Concierge's local
`mcp.json` points to that runtime, not to a separate clone.

The integration test in `Concierge.Tests/PhoneUseMcpIntegrationTests.cs` runs
against a locally installed server when `CONCIERGE_TEST_PHONEUSE_MCP` contains
its executable path. It verifies discovery and a real `status` call. Saying
`Phoneuse status` in Concierge runs that diagnostic through the normal tool
scheduler and approval gate, records the call and result in the conversation,
and returns the result without waiting for model inference. Plan only does not
run it.

This does not prove an iPhone action. A live bridge and mirror are needed for
that. The watch-to-Desktop test on 9 November 2026 reached Desktop, but the
4B Circle AI service sent no answer for five minutes; that run invoked no tool.
The explicit diagnostic route avoids that inference delay for this one request.

PixieDust's existing Concierge app then sent `Phoneuse status` to the patched
Desktop through **Use Desktop**. Desktop recorded a tool call, approval request,
tool result, and assistant answer in the conversation. PixieDust displayed the
answer: no ESP32 bridge, no AirPlay mirror, and no live frames. Automatic
discovery did not find the reachable Desktop on this Wi-Fi, so the test used
Concierge's manual LAN address field. The address remained saved after updating
the app. The first run required answering Desktop's approval through its API
because Mobile showed zero pending requests. After updating Concierge Mobile,
a second run displayed
the Desktop request in **Needs your OK**; tapping **Allow** on PixieDust cleared
the Desktop request and delivered the answer. The tool chip now presents the
result's name and completion state instead of the protocol marker. A final
device run confirmed the Allow label uses legible on-accent text. These are
live cross-device observations, not proof of iPhone control. StarDust has not
been retested against the explicit diagnostic route.

Verification on 9 November 2026: the vendored Python tests passed 18/18;
the three focused Concierge tests passed 3/3, including discovery and a real
`status` call against the vendored MCP process; the Windows Desktop build passed
with zero warnings and zero errors. The host reports no ESP32 bridge and no
live AirPlay mirror. The PixieDust arm64 build with the Mobile approval and
tool-result changes passed with 13 warnings and zero errors; the updated app
was installed and the second approval flow was tested on the device.
