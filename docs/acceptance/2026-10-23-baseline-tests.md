# Baseline build and test recovery — 23 October 2026

## Dependency correction

The browser project initially failed NETSDK1082 because its unused reference to
Shared.Components brought SQLite and the Circle AI server framework into
browser-wasm. Web.Client owns no pages; the Web server references and routes the
shared application pages through InteractiveServer.

Removed that browser reference and its unused imports. Removed the corresponding
WASM0001 suppression because that dependency no longer enters the browser graph.
The actual server page/render-mode configuration was not changed. Browser build
then passed with zero warnings and zero errors. This is compile evidence, not a
new Web product acceptance journey.

## Catalogue fixture correction

The first runnable full suite passed 2,620 tests and failed six. Independent
inspection through the published 3.9.1 catalogue confirmed that Qwen3-0.6B and
Qwen3-30B-A3B are retired. The eligible starter is now Qwen3.5-0.8B, and the SDK
selects Qwen3.5-4B for the existing non-mmap test probe.

Updated those version-pinned test fixtures. Housekeeping now requires proof that
a replacement loaded before reclaiming the previous model. The old test had only
download markers; it now checks preservation before proof. An additional test
checks that the published housekeeping plan reclaims the old model after
RecordLoad stamps its replacement. No production model-selection algorithm was
changed to satisfy the tests.

## Reliable collection and gate

Full MSBuild coverage runs passed their existing gate but reported zero hits in
several exercised modules. A focused cloud run reported hits there; a full
collector run reported hits in all eight included modules. See
[testing instructions](../TESTING.md) for the known flush limitation and command.

`scripts/Test-Concierge.ps1` uses the installed VSTest collector, the same test
project's Include/Exclude filters and existing 65/45/70 total coverage floors,
and Coverlet's own line/branch/method calculator. It rejects test failures,
missing/conflicting or empty reports and any failed floor. TRX's identical
attachment copies are compared by SHA-256, not counted as separate reports.
The gate was seen to reject a deliberately zeroed report for all three metrics.
No CI workflow was added or threshold reduced.

Collector timing exposed three fixture races: reading Skills and approval rows
before their render, and resolving a canvas event target before a pending render
replaced it. The tests now wait for their original expected content and dispatch
canvas interactions on the renderer. Their functional assertions remain.

## Final measured result

Command: `pwsh -NoProfile -File scripts/Test-Concierge.ps1`.

- **2,627 passed, zero failed, zero skipped**, exit code zero.
- Line: **80.65%** — 20,556 / 25,487.
- Branch: **69.09%** — 7,265 / 10,515.
- Method: **86.18%** — 3,088 / 3,583.
- All eight measured modules recorded line hits.

The adjacent JSON records the source base commit, result counters, coverage
counts, per-module rates, artifact path and report digest. Verification includes
pre-existing uncommitted work in this checkout. Those changes were preserved;
these figures are not presented as a clean-checkout result for the resulting
commit. Existing dependency security and compiler/analyzer warnings remain.

This restores the build/test portion of the baseline. It does not close the full
vision, Desktop acceptance journey, mobile/restart recovery or wearable proof.

Local diagnostic logs: `work/2026-10-23-wasm-boundary-before.log`,
`work/2026-10-23-wasm-boundary-after.log`,
`work/2026-10-23-full-suite.log`,
`work/2026-10-23-published-catalogue-probe.log`,
`work/2026-10-23-collector-gate-red.log`, and
`work/2026-10-23-canonical-tests-final.log`.
