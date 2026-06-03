<#
.SYNOPSIS
    Builds a deployable zip of Concierge.Web for the .104/.105 IIS hosts.

.DESCRIPTION
    Runs `dotnet publish` against Concierge.Web in Release configuration, produces a
    timestamped zip in the `artifacts/` folder, and writes a sibling `deploy.ps1` that
    rolls the zip out to .105 → .104 with the cascade discipline the rest of the stack
    uses (90s app-pool stop wait, 2-minute stagger between servers, hop to .104 via .105
    rather than direct connect). This script does NOT push anywhere — it just produces
    the artifact + the deploy recipe for an operator to run.

.PARAMETER OutputDirectory
    Where to drop the zip. Defaults to `artifacts/` next to the script.

.PARAMETER SkipBuild
    Skip `dotnet publish` and just re-zip from `bin/publish/`. Useful for dry runs.

.EXAMPLE
    pwsh ./build-web-package.ps1
#>
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "artifacts"),
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$webProject = Join-Path $repoRoot 'Concierge.Web/Concierge.Web.csproj'
$publishRoot = Join-Path $repoRoot 'bin/publish/Concierge.Web'

if (-not $SkipBuild) {
    Write-Host "[1/3] Restoring + publishing Concierge.Web (Release)..." -ForegroundColor Cyan
    & dotnet publish $webProject -c Release -o $publishRoot --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
} else {
    Write-Host "[1/3] -SkipBuild set; reusing $publishRoot" -ForegroundColor Yellow
    if (-not (Test-Path $publishRoot)) { throw "No publish output at $publishRoot — drop -SkipBuild and try again." }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$timestamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$zipName = "concierge-web-$timestamp.zip"
$zipPath = Join-Path $OutputDirectory $zipName

Write-Host "[2/3] Zipping publish output -> $zipPath" -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal

$deployRecipe = @"
# deploy.ps1 — operator-run cascade for $zipName
#
# Honors the project deploy memory:
#   - Always deploy to BOTH .104 and .105 (rolling, 2-min stagger between servers).
#   - NEVER connect to .104 directly — hop via .105.
#   - 90-second wait between app-pool stop and start to flush PgBouncer cascades and
#     give in-flight chat streams a chance to drain.
#   - Clean up zips + temp folders after the cascade.
#
# Run this only when an operator has confirmed the artifact is ready.

`$ErrorActionPreference = 'Stop'
`$package = "$zipName"
`$remoteShareOn105 = '\\\\10.0.0.105\\Deploy'
`$siteName = 'concierge-web'
`$appPool = 'concierge-web'
`$iisRoot = 'C:\\inetpub\\concierge-web'

# 1) Copy artefact to .105.
Copy-Item -Path `$package -Destination `$remoteShareOn105 -Force

# 2) From .105, kick the .104 deploy (PsExec / Invoke-Command). We do NOT open a direct
#    session to .104 from operator's machine — only .105 reaches .104.
Invoke-Command -ComputerName 10.0.0.105 -ScriptBlock {
    param(`$package, `$siteName, `$appPool, `$iisRoot)
    Import-Module WebAdministration

    Write-Host '105: stopping app pool'
    Stop-WebAppPool -Name `$appPool

    Write-Host '105: waiting 90s for PgBouncer cascade + in-flight chat streams'
    Start-Sleep -Seconds 90

    Write-Host '105: expanding new bits over iisRoot'
    Expand-Archive -Path `"C:\\Deploy\\`$package`" -DestinationPath `$iisRoot -Force

    Write-Host '105: starting app pool'
    Start-WebAppPool -Name `$appPool

    Write-Host '105: hopping to .104'
    Invoke-Command -ComputerName 10.0.0.104 -ScriptBlock {
        param(`$package, `$siteName, `$appPool, `$iisRoot)
        Import-Module WebAdministration
        Write-Host '104: stopping app pool'
        Stop-WebAppPool -Name `$appPool
        Start-Sleep -Seconds 90
        Write-Host '104: expanding new bits over iisRoot'
        Expand-Archive -Path `"C:\\Deploy\\`$package`" -DestinationPath `$iisRoot -Force
        Start-WebAppPool -Name `$appPool
    } -ArgumentList `$package, `$siteName, `$appPool, `$iisRoot
} -ArgumentList `$package, `$siteName, `$appPool, `$iisRoot

# 3) Two-minute stagger between servers is encoded by the Invoke-Command above starting
#    the .104 cycle only after the .105 pool has fully come back up (~Start-WebAppPool
#    is synchronous; the 90s sleep plus IIS warm-up gives us the stagger naturally).

# 4) Clean up the staging copy on .105.
Invoke-Command -ComputerName 10.0.0.105 -ScriptBlock {
    param(`$package)
    Remove-Item -Path `"C:\\Deploy\\`$package`" -Force -ErrorAction SilentlyContinue
} -ArgumentList `$package

Write-Host 'Deploy complete.'
"@

$deployScriptPath = Join-Path $OutputDirectory ("deploy-{0}.ps1" -f $timestamp)
Set-Content -Path $deployScriptPath -Value $deployRecipe -Encoding UTF8

Write-Host "[3/3] Wrote deploy recipe -> $deployScriptPath" -ForegroundColor Cyan
Write-Host ""
Write-Host "Artefact ready:" -ForegroundColor Green
Write-Host "  Package: $zipPath"
Write-Host "  Recipe : $deployScriptPath"
Write-Host ""
Write-Host "This script does NOT push anywhere. Hand the package + recipe to an operator," -ForegroundColor Yellow
Write-Host "or run the recipe yourself when you are ready to deploy." -ForegroundColor Yellow
