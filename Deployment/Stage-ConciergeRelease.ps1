#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('android-aab', 'android-apk', 'wear-aab', 'wear-apk', 'windows-x64', 'web', 'ios-ipa', 'linux-x64')]
    [string]$Platform,

    [Parameter(Mandatory)]
    [string]$ArtifactPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+(?:\.\d+)?$')]
    [string]$VersionName,

    [Parameter(Mandatory)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$VersionCode,

    [string]$DestinationRoot = 'C:\Deployment\repos\thegeeknetwork\Apps'
)

$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceFile = (Resolve-Path -LiteralPath $ArtifactPath).Path
$artifact = Get-Item -LiteralPath $sourceFile
if ($artifact.Length -eq 0) { throw 'The release artifact is empty.' }

$channel = switch ($Platform) {
    { $_ -in 'android-aab', 'wear-aab' } { 'Google'; break }
    'windows-x64' { 'Microsoft'; break }
    'ios-ipa' { 'Apple'; break }
    'linux-x64' { 'Linux'; break }
    default { 'TheGeek' }
}
$extension = switch ($Platform) {
    { $_ -in 'android-aab', 'wear-aab' } { '.aab'; break }
    { $_ -in 'android-apk', 'wear-apk' } { '.apk'; break }
    'ios-ipa' { '.ipa'; break }
    'linux-x64' { '.tar.gz'; break }
    default { '.zip' }
}
if (-not $artifact.Name.EndsWith($extension, [StringComparison]::OrdinalIgnoreCase)) {
    throw "$Platform requires a $extension artifact."
}

# A readable ZIP and a platform entry establish structure, not signing or runtime quality.
$structure = 'not-checked'
if ($extension -ne '.tar.gz') {
    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead($sourceFile)
    try {
        if ($archive.Entries.Count -eq 0) { throw 'The archive has no entries.' }
        $required = switch ($Platform) {
            { $_ -in 'android-aab', 'wear-aab' } { 'base/manifest/AndroidManifest.xml'; break }
            { $_ -in 'android-apk', 'wear-apk' } { 'AndroidManifest.xml'; break }
            'windows-x64' { 'Concierge.exe'; break }
            'web' { 'Concierge.Web.dll'; break }
            default { $null }
        }
        if ($required -and -not @($archive.Entries | Where-Object {
            $_.FullName.TrimStart('/') -ieq $required
        }).Count) {
            throw "The archive is missing $required."
        }
        $structure = 'archive-structure-passed'
    }
    finally { $archive.Dispose() }
}

$sourceCommit = (& git -C $sourceRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'The Concierge source commit could not be established.'
}
$sourceDirty = @(& git -C $sourceRoot status --porcelain=v1).Count -gt 0
if ($LASTEXITCODE -ne 0) { throw 'The Concierge source status could not be established.' }

$destination = Join-Path $DestinationRoot "$channel\Concierge\$VersionName-$VersionCode"
$stagedFile = Join-Path $destination $artifact.Name
$manifestPath = Join-Path $destination 'release.json'
if (Test-Path -LiteralPath $stagedFile) { throw "The staged artifact already exists: $stagedFile" }

$manifest = if (Test-Path -LiteralPath $manifestPath) {
    Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
} else {
    [ordered]@{
        status = 'staged-unverified'
        app = 'Concierge'
        versionName = $VersionName
        versionCode = $VersionCode
        sourceCommit = $sourceCommit
        sourceIncludesUncommittedChanges = $sourceDirty
        stagedAtJst = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::UtcNow, 'Tokyo Standard Time').ToString('o')
        publishReady = $false
        artifacts = @()
    }
}
if ($manifest.app -ne 'Concierge' -or $manifest.versionName -ne $VersionName -or
    [int]$manifest.versionCode -ne $VersionCode -or $manifest.sourceCommit -ne $sourceCommit) {
    throw "Existing release.json belongs to a different source or version: $manifestPath"
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null
$temporaryFile = Join-Path $destination ('.' + [guid]::NewGuid().ToString('N') + '.partial')
$temporaryManifest = Join-Path $destination ('.' + [guid]::NewGuid().ToString('N') + '.json')
$artifactMoved = $false
try {
    Copy-Item -LiteralPath $sourceFile -Destination $temporaryFile
    $sourceHash = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $stagedHash = (Get-FileHash -LiteralPath $temporaryFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHash -ne $stagedHash) { throw 'The staged copy does not match the source artifact.' }
    $manifest.artifacts = @($manifest.artifacts) + @([ordered]@{
        platform = $Platform
        artifact = $artifact.Name
        bytes = $artifact.Length
        sha256 = $stagedHash
        structure = $structure
        signatureVerified = $false
        deviceSmokeTest = 'pending'
    })
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporaryManifest -Encoding utf8
    Move-Item -LiteralPath $temporaryFile -Destination $stagedFile
    $artifactMoved = $true
    Move-Item -LiteralPath $temporaryManifest -Destination $manifestPath -Force
    $artifactMoved = $false
}
catch {
    # Only roll back the artifact introduced by this invocation. Older releases stay untouched.
    if ($artifactMoved -and (Test-Path -LiteralPath $stagedFile)) {
        Remove-Item -LiteralPath $stagedFile
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $temporaryFile) { Remove-Item -LiteralPath $temporaryFile }
    if (Test-Path -LiteralPath $temporaryManifest) { Remove-Item -LiteralPath $temporaryManifest }
}

Write-Output "Staged: $stagedFile"
Write-Output "Manifest: $manifestPath"
Write-Output 'Publish ready: no (signature and device checks remain pending)'
