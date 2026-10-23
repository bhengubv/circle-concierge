# Concierge tests

From the repository root, with PowerShell 7 and the .NET 10 SDK:

```powershell
pwsh -NoProfile -File scripts/Test-Concierge.ps1
```

This builds and runs the full test project with Coverlet's VSTest collector.
It then checks total line, branch and method coverage against the existing
`Threshold`, `ThresholdType` and `ThresholdStat` in `Concierge.Tests.csproj`.
The same project's Include/Exclude filters define the measured assemblies.
Coverlet's own calculator evaluates the report; the script does not maintain
a second coverage-counting algorithm or a second set of threshold numbers.

Each run writes its TRX, collector settings, JSON and Cobertura reports under a
new `TestResults/coverage-*` directory. Test failure, missing/conflicting reports,
empty coverage, and coverage below any configured floor fail the command.
Identical TRX attachment copies are accepted by SHA-256 comparison.

To check an existing collector JSON report without running tests:

```powershell
pwsh -NoProfile -File scripts/Test-Concierge.ps1 -ReportPath <coverage.json>
```

That mode checks coverage only; it does not verify that the report's test run
passed. A deliberately zeroed report was used to verify that all three unchanged
coverage floors reject it.

## Why the collector

Plain `dotnet test Concierge.Tests/Concierge.Tests.csproj` retains the existing
MSBuild integration and gate. On this machine, full MSBuild runs reported zero
hits in several assemblies whose tests passed, while a focused run recorded
hits in the same code. The full collector run recorded hits in all included
assemblies. This is consistent with Coverlet's documented
[MSBuild shutdown-flush problem](https://github.com/coverlet-coverage/coverlet/blob/master/Documentation/KnownIssues.md#vstest-stops-process-execution-early).
The exact process shutdown race was not separately traced.

Use the script for acceptance coverage. It disables MSBuild instrumentation for
that invocation to avoid double instrumentation, activates the existing collector,
and applies the original thresholds after collection. No CI workflow is added.

For a focused functional test, use the ordinary test command with a filter and
`-p:CollectCoverage=false`. Project-wide coverage floors are not meaningful for
a small filtered subset; the full script must still pass for baseline acceptance.

## Scope

Passing tests and coverage do not prove a person-facing app journey, actual model
quality, completed downloads, secure networking or device/restart recovery.
Record those separately from the running application. In a dirty checkout,
results describe that working tree, including its uncommitted changes; they are
not automatically evidence for a clean checkout of a commit.
