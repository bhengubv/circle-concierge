# Project Slides acceptance — 20 November 2026

## What works in code

- A Presentation starter creates an editable brief-to-export workflow.
- Open Slides from a Project creates one linked conversation, then reuses it on later opens.
- The route opens a Slides canvas on Desktop and Handheld. Each Project keeps its own local canvas history and undo position under the Concierge data directory.
- Slides exposes PPTX and PDF actions. Document export remains available without a media encoder; sound and video still require one.

## Checks run

- `dotnet build Concierge.Shared.Components/Concierge.Shared.Components.csproj --no-restore -v:q`: 0 errors.
- Focused route and design-save tests after the encoder dependency change: 15 passed. An initial route test failed because its fixture had not registered the design tool source, then passed after the fixture was corrected.
- Full `Concierge.Tests` suite and coverage gate: 2,730 passed, 0 failed, 0 skipped. Total measured coverage: 75.48% line, 65.36% branch, 80.33% method.
- Desktop and Handheld route tests: 2 passed.

## Not yet proven

- No live browser, Desktop window, or physical phone visual acceptance was run for this change. The route tests use bUnit and cannot verify layout or native file sharing.
- Project canvases are local to each device. Their files are not yet synchronized across devices, even if the Project and conversation are.
- The existing PDF renderer does not reproduce every HTML layout detail and may omit unsupported pictures. Review the exported PDF before sharing.
