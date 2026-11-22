# Concierge Code project route — 23 November 2026

Concierge Code's version 1 `ready` event now includes a route to the requested project folder. Concierge Desktop reads that route instead of opening the editor root. The client accepts only a single encoded absolute folder in the `/?folder=` form, constructs a loopback URL, and appends the session token. Creative workspace routes retain their existing project, kind, and output-root checks.

## Live Desktop acceptance

Opened the existing **Developer Studio acceptance** Project in the rebuilt Concierge Desktop app and selected **Code**. The embedded editor's Explorer showed `SOURCE/README.md`. Opening it showed `# Developer Studio acceptance` and `Source files for this Concierge Project.` This establishes that the editor opened this Project's source folder rather than an empty editor. The editor was closed after the check.

The editor's own default walkthrough still says “Get Started with VS Code for the Web”; that visible branding gap was sent to the Concierge Code session for assessment. This change does not rebrand upstream editor assets.

## Automated acceptance

- The route's focused tests passed 10/10, including rejection of missing, external, and extra-parameter routes.
- The Windows MAUI Debug build passed with zero errors and zero warnings.
- The complete Concierge test suite passed **2,755/2,755**, zero failed and zero skipped, after closing the live editor. Instrumented totals were 70.12% line, 60.14% branch, and 75.43% method coverage.

The first full-suite run while the embedded editor was open had 45 failures, concentrated in media and process tests. An isolated process test reported `There is not enough free memory to start another managed worker.` After closing the editor, that test passed and the complete suite passed. This is evidence of a memory-sensitive test environment, not a measured memory profile or proof that every failure had the same cause.
