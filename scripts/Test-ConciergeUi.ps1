#Requires -Version 7.0
[CmdletBinding()]
param([string]$Configuration = 'Debug')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'Concierge.Tests/Concierge.Tests.csproj'
$settings = Join-Path $repo 'tests/Concierge.FeatureAcceptance/ui-coverage.runsettings'
$run = Join-Path $repo ('TestResults/ui-coverage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null

# Instrument the component journeys without running process/host tests under
# Razor coverage. Those have separate gates and can fail on OS process timing.
$uiClasses = @(
    'ApiRoomHonestyTests', 'BeyondHonestyTests', 'CanvasAnswersTests',
    'CanvasOffTheDispatcherTests', 'CanvasRestoreTests', 'CanvasSpeechTests',
    'CircleAiDeviceSettingsTests', 'CircleAiServiceSettingsTests',
    'CircleAiSharedServiceStatusTests', 'DiagramsRoomTests',
    'DownloadOfferSummaryTests', 'FeatureGapTests', 'FilterRecordTests',
    'HandheldWorkspaceTests', 'HelpAndPaperclipTests', 'ImagesRoomTests',
    'MicrophoneTests', 'ModelSetupTutorialTests', 'ProjectPageTests',
    'RoadmapHonestyTests', 'RoomTests', 'ShellPageTests', 'VocationModeTests',
    'WatchFootageTests', 'WelcomePanelTests', 'WorkspaceComponentTests',
    'WorkspaceComposerTests', 'WorkspaceHandoffTests', 'WorkspaceSettingsTests',
    'WorkspaceSidebarTests', 'WorkspaceSkillsTests', 'WorkspaceThreadTests'
)
$filter = ($uiClasses | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'

& dotnet test $project -c $Configuration -m:1 -p:UseSharedCompilation=false `
    -p:CollectCoverage=false --settings $settings --collect 'XPlat Code Coverage' `
    --results-directory $run --logger 'trx;LogFileName=tests.trx' --filter $filter
if ($LASTEXITCODE -ne 0) { throw "UI tests failed. Evidence: $run" }

$reports = @(Get-ChildItem -LiteralPath $run -Recurse -Filter coverage.cobertura.xml -File)
$unique = @($reports | Group-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
if ($unique.Count -ne 1) { throw "Expected one unique fresh UI coverage report; found $($unique.Count): $run" }
[xml]$coverage = Get-Content -LiteralPath $reports[0].FullName -Raw
if ($coverage.coverage.packages.package.name -ne 'Concierge.Shared.Components') {
    throw "The coverage report does not contain the shared UI assembly: $run"
}

foreach ($check in @(@('line', 55), @('branch', 50))) {
    $name, $floor = $check
    $ratio = [double]::Parse($coverage.coverage.GetAttribute("$name-rate"), [Globalization.CultureInfo]::InvariantCulture)
    $percent = [math]::Round(100 * $ratio, 2)
    Write-Host "$name coverage: $percent% (required $floor%)"
    if ($percent -lt $floor) { throw "Shared UI $name coverage fell below $floor%. Report: $run" }
}
Write-Host "Shared UI coverage gate passed. Report: $run"
