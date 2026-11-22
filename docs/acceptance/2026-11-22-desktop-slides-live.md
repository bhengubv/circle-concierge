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
- The full test-suite gate remains red: one serial run had 2,737 passing and one failing SQLite cache measurement; another had 2,735 passing and three failures in media export and MCP start. The cache test and all media export tests passed when run in isolation. The MCP test was then changed to give its fixture a fixed memory budget, so it tests connection failure instead of depending on the laptop's free RAM. A complete post-change suite has not yet passed. The media export failures reported that no sound could be decoded; the underlying encoder failure under whole-suite load is not yet isolated.
