#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    # Check an existing collector report without running tests (also exercises the gate).
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repo 'Concierge.Tests/Concierge.Tests.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw

function Read-ProjectProperty([string]$Name) {
    $node = $project.SelectSingleNode("/Project/PropertyGroup/$Name")
    if ($null -eq $node) { throw "Missing test-project property: $Name" }
    return $node.InnerText
}

if ((Read-ProjectProperty 'ThresholdStat') -ne 'total') {
    throw 'This gate expects the test project to specify total coverage.'
}
$types = (Read-ProjectProperty 'ThresholdType').Split(',')
$floors = (Read-ProjectProperty 'Threshold').Split(',')
if ($types.Count -ne $floors.Count) { throw 'Coverage threshold types and values differ in length.' }

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $run = Join-Path $repo ('TestResults/coverage-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $run | Out-Null
    # The collector flushes before the test host exits. Do not also instrument
    # through coverlet.msbuild; its existing thresholds are checked below.
    [xml]$settings = '<RunSettings><DataCollectionRunSettings><DataCollectors><DataCollector friendlyName="XPlat code coverage"><Configuration><Format>json,cobertura</Format><Include/><Exclude/></Configuration></DataCollector></DataCollectors></DataCollectionRunSettings></RunSettings>'
    $config = $settings.RunSettings.DataCollectionRunSettings.DataCollectors.DataCollector.Configuration
    $config.SelectSingleNode('Include').InnerText = Read-ProjectProperty 'Include'
    $config.SelectSingleNode('Exclude').InnerText = Read-ProjectProperty 'Exclude'
    $settingsPath = Join-Path $run 'coverage.runsettings'
    $settings.Save($settingsPath)
    & dotnet test $projectPath -c $Configuration -m:1 -p:UseSharedCompilation=false `
        -p:CollectCoverage=false --settings $settingsPath --collect 'XPlat Code Coverage' `
        --results-directory $run --logger 'trx;LogFileName=tests.trx'
    if ($LASTEXITCODE -ne 0) { throw "Tests failed. Evidence: $run" }
    $reports = @(Get-ChildItem -LiteralPath $run -Recurse -Filter coverage.json -File)
    # TRX can copy the collector attachment into its deployment directory.
    # Accept identical copies, never silently choose between differing reports.
    $unique = @($reports | Group-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
    if ($unique.Count -ne 1) { throw "Expected one unique fresh collector report, found $($unique.Count): $run" }
    $ReportPath = $reports[0].FullName
}

# Use Coverlet's own coverage calculations, not a second implementation of
# line/branch/method counting. Resolve the already-restored collector version.
$assets = Get-Content -LiteralPath (Join-Path $repo 'Concierge.Tests/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$collector = @($assets.libraries.Keys | Where-Object { $_.StartsWith('coverlet.collector/') })
if ($collector.Count -ne 1) { throw 'The test project must restore exactly one coverlet.collector package.' }
$relative = Join-Path $assets.libraries[$collector[0]].path 'build/netstandard2.0/coverlet.core.dll'
$corePath = @($assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $relative } | Where-Object { Test-Path -LiteralPath $_ })
if ($corePath.Count -eq 0) { throw 'Restored Coverlet coverage calculator was not found.' }
$core = [Reflection.Assembly]::LoadFrom($corePath[0])
$modules = [Newtonsoft.Json.JsonConvert]::DeserializeObject(
    [IO.File]::ReadAllText([IO.Path]::GetFullPath($ReportPath)), $core.GetType('Coverlet.Core.Modules', $true))
if ($null -eq $modules -or $modules.Count -eq 0) { throw 'Empty coverage report; no coverage was verified.' }
$summary = [Activator]::CreateInstance($core.GetType('Coverlet.Core.CoverageSummary', $true))
$failed = @()
for ($index = 0; $index -lt $types.Count; $index++) {
    $type = $types[$index].Trim().ToLowerInvariant()
    $metric = switch ($type) { 'line' { 'Line' } 'branch' { 'Branch' } 'method' { 'Method' } default { throw "Unsupported coverage metric: $type" } }
    $floor = [double]::Parse($floors[$index], [Globalization.CultureInfo]::InvariantCulture)
    $details = $summary."Calculate${metric}Coverage"($modules)
    if ($details.Total -eq 0) { throw "No measurable $type coverage in report." }
    Write-Host ("{0}: {1}% ({2}/{3}); required {4}%" -f $metric, $details.Percent, $details.Covered, $details.Total, $floor)
    if ($details.Percent -lt $floor) { $failed += $type }
}
if ($failed.Count -gt 0) { throw "Coverage below the existing project thresholds: $($failed -join ', '). Report: $ReportPath" }
Write-Host "Coverage gate passed. Report: $ReportPath"
