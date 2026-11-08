# Phone control tool for Concierge

This directory carries the MIT-licensed `phoneuse` host and MCP tool, adapted
from [lxe/phoneuse](https://github.com/lxe/phoneuse). Concierge Desktop runs
`phoneuse-mcp` as a bounded tool process. Its existing Mobile and Wear OS apps
send requests to Concierge; there are no separate phoneuse client apps.

The host receives an iPhone mirror through AirPlay and sends input through an
ESP32 Bluetooth HID bridge. The host code supports Linux, Windows and macOS,
but the platform dependencies must be installed on the computer that hosts it.
The ESP32 firmware source is in `bridge/`. UxPlay is a separate dependency and
is not copied into Concierge.

From this directory, install the tool runtime in an isolated Python environment:

```sh
python -m venv .venv
.venv/Scripts/python -m pip install -e ".[bridge,portable,mcp]"
```

On Linux or macOS, use `.venv/bin/python` instead. Add the resulting
`phoneuse-mcp` executable to Concierge's MCP settings. The tool is offered only
on the Desktop host where it runs; requests from Mobile and Wear OS can be
handed to that Desktop through Concierge.

No real iPhone control has been verified in this workspace because the ESP32
bridge is unavailable. `phoneuse status` currently reports no bridge and no
live mirror. The source is included to make this Concierge tooling rather than
a separate application; that does not make its hardware requirements vanish.
