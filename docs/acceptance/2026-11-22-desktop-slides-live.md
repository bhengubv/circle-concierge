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

- This check proves creation in a running Desktop window. Reopening the Project after leaving Slides was not verified in this run; the UI input session was interrupted before that check. The existing Project route test covers saved design state, but it is not a live persistence check.
- PPTX/PDF export and Handheld visual acceptance were not exercised in this run.
- The full test and coverage gate passed after concurrent model work stopped: 2,738 passed, 0 failed, 0 skipped; 71.06% line, 60.64% branch, and 76.43% method coverage against 65/45/70 thresholds. The preceding full runs failed intermittently in SQLite cache, media export, and MCP start tests under machine load. The cache and media tests passed when isolated. The MCP fixture now supplies a fixed memory budget, so it tests the intended connection failure independent of current free RAM. The exact cause of the earlier media export failures under load remains unverified.
