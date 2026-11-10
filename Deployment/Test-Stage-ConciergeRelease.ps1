#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('concierge-stage-' + [guid]::NewGuid().ToString('N'))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixturePath = [IO.Path]::GetFullPath($fixtureRoot)
if (-not $fixturePath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The fixture directory must stay inside the system temp directory.'
}

[IO.Directory]::CreateDirectory($fixturePath) | Out-Null
try {
    $source = Join-Path $fixturePath 'source'
    [IO.Directory]::CreateDirectory($source) | Out-Null
    Set-Content -LiteralPath (Join-Path $source 'Concierge.Web.dll') -Value 'fixture'
    $zip = Join-Path $fixturePath 'concierge-web.zip'
    Compress-Archive -Path (Join-Path $source '*') -DestinationPath $zip
    $destination = Join-Path $fixturePath 'Apps'
    $stage = Join-Path $PSScriptRoot 'Stage-ConciergeRelease.ps1'

    & $stage -Platform web -ArtifactPath $zip -VersionName 1.0 -VersionCode 1 -DestinationRoot $destination | Out-Null
    $releaseDir = Join-Path $destination 'TheGeek\Concierge\1.0-1'
    $manifest = Get-Content -LiteralPath (Join-Path $releaseDir 'release.json') -Raw | ConvertFrom-Json
    $expectedHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($manifest.artifacts.Count -ne 1 -or
        $manifest.artifacts[0].sha256 -ne $expectedHash -or
        $manifest.artifacts[0].structure -ne 'archive-structure-passed' -or
        $manifest.publishReady -or
        -not (Test-Path -LiteralPath (Join-Path $releaseDir 'concierge-web.zip'))) {
        throw 'The staged artifact or manifest does not match the fixture.'
    }

    $duplicateRejected = $false
    try {
        & $stage -Platform web -ArtifactPath $zip -VersionName 1.0 -VersionCode 1 -DestinationRoot $destination | Out-Null
    }
    catch { $duplicateRejected = $_.Exception.Message.Contains('already exists') }
    if (-not $duplicateRejected) { throw 'An existing release artifact was overwritten.' }

    foreach ($case in @(
        @{ Platform = 'windows-x64'; Channel = 'Microsoft'; Entry = 'Concierge.exe'; Name = 'Concierge-Windows-x64.zip' },
        @{ Platform = 'android-aab'; Channel = 'Google'; Entry = 'base/manifest/AndroidManifest.xml'; Name = 'com.companyname.concierge-Signed.aab' },
        @{ Platform = 'wear-apk'; Channel = 'TheGeek'; Entry = 'AndroidManifest.xml'; Name = 'com.companyname.concierge.wear.apk' }
    )) {
        $folder = Join-Path $fixturePath $case.Platform
        $entry = Join-Path $folder ($case.Entry.Replace('/', [IO.Path]::DirectorySeparatorChar))
        [IO.Directory]::CreateDirectory((Split-Path $entry -Parent)) | Out-Null
        Set-Content -LiteralPath $entry -Value 'fixture'
        $package = Join-Path $fixturePath $case.Name
        Compress-Archive -Path (Join-Path $folder '*') -DestinationPath $package
        & $stage -Platform $case.Platform -ArtifactPath $package -VersionName 1.0 -VersionCode 1 -DestinationRoot $destination | Out-Null
        $staged = Join-Path $destination "$($case.Channel)\Concierge\1.0-1\$($case.Name)"
        if (-not (Test-Path -LiteralPath $staged)) { throw "$($case.Platform) went to the wrong channel." }
    }

    Write-Output 'Concierge release staging fixture passed.'
}
finally {
    if (Test-Path -LiteralPath $fixturePath) { [IO.Directory]::Delete($fixturePath, $true) }
}
