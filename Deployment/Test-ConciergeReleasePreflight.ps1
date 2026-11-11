#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$androidNamespace = 'http://schemas.android.com/apk/res/android'

[xml]$mobileProject = Get-Content -LiteralPath (Join-Path $root 'Concierge/Concierge.csproj') -Raw
[xml]$wearProject = Get-Content -LiteralPath (Join-Path $root 'Concierge.Wear/Concierge.Wear.csproj') -Raw
[xml]$mobileManifest = Get-Content -LiteralPath (Join-Path $root 'Concierge/Platforms/Android/AndroidManifest.xml') -Raw
[xml]$wearManifest = Get-Content -LiteralPath (Join-Path $root 'Concierge.Wear/Properties/AndroidManifest.xml') -Raw

$mobileId = $mobileProject.SelectSingleNode('/Project/PropertyGroup/ApplicationId')?.InnerText
$wearId = $wearProject.SelectSingleNode('/Project/PropertyGroup/ApplicationId')?.InnerText
if ($mobileId -ne 'za.co.thegeeknetwork.concierge' -or $wearId -ne "$mobileId.wear") {
    throw "Unexpected release identities: Mobile=$mobileId Wear=$wearId"
}

$mobileVersion = $mobileProject.SelectSingleNode('/Project/PropertyGroup/ApplicationDisplayVersion')?.InnerText
$wearVersion = $wearProject.SelectSingleNode('/Project/PropertyGroup/ApplicationDisplayVersion')?.InnerText
$mobileCode = $mobileProject.SelectSingleNode('/Project/PropertyGroup/ApplicationVersion')?.InnerText
$wearCode = $wearProject.SelectSingleNode('/Project/PropertyGroup/ApplicationVersion')?.InnerText
if ($mobileVersion -ne $wearVersion -or $mobileCode -ne $wearCode) {
    throw "Mobile and Wear release versions differ: Mobile=$mobileVersion-$mobileCode Wear=$wearVersion-$wearCode"
}

$mobileBackup = $mobileManifest.manifest.application.GetAttribute('allowBackup', $androidNamespace)
$wearBackup = $wearManifest.manifest.application.GetAttribute('allowBackup', $androidNamespace)
if ($mobileBackup -ne 'false' -or $wearBackup -ne 'false') {
    throw "Device-owned data must not be backed up: Mobile=$mobileBackup Wear=$wearBackup"
}

$downloadService = @($mobileManifest.manifest.application.service | Where-Object {
    $_.GetAttribute('node', 'http://schemas.android.com/tools') -ne 'remove'
}) | Select-Object -First 1
$downloadService = $downloadService.GetAttribute('name', $androidNamespace)
if ($downloadService -ne "$mobileId.AndroidModelDownloadForegroundService") {
    throw "The download service still has an old package name: $downloadService"
}

Write-Output "Concierge release preflight passed: $mobileVersion-$mobileCode, $mobileId, $wearId, backup disabled."
