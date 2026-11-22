# Desktop Slides acceptance — 22 November 2026

## Human task

From a Project, open Slides and type: “Add a slide titled Project status with three points: goal, current work, next step.” A novice should see an editable slide without choosing a model or waiting for an unrelated chat answer.

## Before

- The request did not match the local canvas command. It went to Circle AI instead.
- The Desktop showed “Waiting for first response” for 4 minutes 33 seconds; the canvas was still empty after 4 minutes 41 seconds. The eventual text answer asked for clarification and did not create a slide.

## After

- The same request in the rebuilt Desktop created a slide. The observed send-to-refreshed-screen interval was 496 ms.
- The live accessibility tree showed the title and all three points, “1 of 1,” and an enabled Go back control. The app reported “Added a slide.” No Circle AI answer was needed.
- The 33 DesignMedium tests passed. A broader 97-test workspace/Slides selection passed. After the handheld route test was strengthened to send this same sentence and inspect the saved title and points, a 60-test Slides/MCP selection passed. The Windows Desktop build passed with 0 warnings and 0 errors.

## Limits

- This initial check proved creation in a running Desktop window. A later restart check below verifies persistence.
- PPTX/PDF export and Handheld visual acceptance were not exercised in this run.
- The full test and coverage gate passed after concurrent model work stopped: 2,738 passed, 0 failed, 0 skipped; 71.06% line, 60.64% branch, and 76.43% method coverage against 65/45/70 thresholds. The preceding full runs failed intermittently in SQLite cache, media export, and MCP start tests under machine load. The cache and media tests passed when isolated. The MCP fixture now supplies a fixed memory budget, so it tests the intended connection failure independent of current free RAM. The exact cause of the earlier media export failures under load remains unverified.

## Later restart and draft check

- After closing and reopening the Desktop app, I opened the same Project's Slides canvas. The title and points were still present. This verifies live Project slide persistence across an app restart.
- The composer also restored the request that had already created that slide. This was a defect: the canvas cleared its visible text but did not clear the per-conversation session draft. Canvas edits, imports, and saves now persist the cleared draft after consuming a request.
- The Desktop and handheld route tests reproduced the stale-draft defect, then passed after the fix. Their saved session state has no unsent text after the slide request.
- A full post-fix test and coverage run passed: 2,738 passed, 0 failed, 0 skipped; 76.59% line, 66.01% branch, and 81.38% method coverage. The Windows Desktop build passed with 0 warnings and 0 errors.
- The rebuilt Desktop's live draft recheck was interrupted by WebView input that did not activate the Project link. The code-path tests prove draft clearing; a fresh live restart check of the cleared composer remains open. PPTX/PDF export and physical phone acceptance remain open.
- The SQLite cache measurement fixture previously put `projects.db` directly in the shared temporary folder despite a unique chat filename. It now uses a unique directory, removing interference from other test Projects.
