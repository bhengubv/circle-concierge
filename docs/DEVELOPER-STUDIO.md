# Developer Studio

Developer Studio is an optional Desktop tool for code in a Concierge Project. The person opens it from the Project page. On first use, Concierge downloads a pinned OpenVSCode Server release into the default WSL distribution, checks its SHA-256 digest, and starts it on the Windows loopback interface with a fresh connection token. The editor opens in a full-screen view inside Concierge. The Project's source folder stays in the person's local Concierge app data.

The tool is currently supported on **Windows x64 with WSL**. It is not installed or started during Concierge startup. Its server is stopped when the editor closes. A recorded Linux process ID lets the next open clean up an editor left behind by an abrupt Desktop exit. No editor port is exposed to the LAN or Internet.

The workbench is based on VS Code and offers compatible extensions through Open VSX. Extension availability and licenses must be reviewed individually; Microsoft Marketplace access is not assumed. The downloaded runtime is not checked into this repository. OpenVSCode Server's license is MIT; preserve its notices if a future installer redistributes it.

This is the **create and edit** portion of the production path. Concierge can use Project workflows and its tools to plan and review code. Publishing to SleptON! remains separate: its current `publish` command is declared but has no handler wired to it, while the available subprocess adapters are read-only. Live release submission and final approval need a running service and an end-to-end acceptance test. Developer Studio does not publish by itself.

Acceptance evidence for the Windows pilot: an editor opened from a Concierge Project; a `README.md` edit was saved to the Project's local source folder; the extension catalogue populated; a request without the token returned HTTP 403; and closing the editor stopped the loopback listener and its Linux process. After a forced Desktop exit left the editor process running, opening the same Project again stopped that stale process and started a fresh editor. Cross-platform editor packaging and live SleptON! publishing are not verified.
