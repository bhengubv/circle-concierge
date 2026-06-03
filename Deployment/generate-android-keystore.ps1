<#
.SYNOPSIS
    Generates an Android upload keystore for signing Concierge.apk / Concierge.aab.

.DESCRIPTION
    Calls Java's keytool to create a fresh keystore + key pair sized for Play Store
    upload signing (RSA-2048, 10,000-day validity). The keystore is written to the
    `secrets/` folder which is excluded from git via .gitignore — the operator must
    back it up out-of-band (1Password / hardware token / secure share).

    This script does NOT push the APK to the Play Store. It only generates the
    keystore that the MAUI Release build consumes. The MAUI signing properties in
    Concierge.csproj read from environment variables so the keystore path + password
    never appear in source.

.PARAMETER KeystorePath
    Where to write the .jks file. Defaults to `secrets/concierge-android-upload.jks`.

.PARAMETER Alias
    Key alias inside the keystore. Defaults to `concierge-upload`.

.PARAMETER Dname
    Distinguished name for the certificate. Defaults to a Concierge-shaped value.

.EXAMPLE
    pwsh ./generate-android-keystore.ps1
    # Prompts twice for the keystore password + key password.
#>
param(
    [string]$KeystorePath = (Join-Path $PSScriptRoot 'secrets/concierge-android-upload.jks'),
    [string]$Alias = 'concierge-upload',
    [string]$Dname = 'CN=Concierge, OU=The Other Bhengu PTY Ltd, O=The Geek Network, L=Cape Town, ST=Western Cape, C=ZA'
)

$ErrorActionPreference = 'Stop'

$keytool = Get-Command keytool -ErrorAction SilentlyContinue
if (-not $keytool) {
    throw "keytool not on PATH. Install a JDK (e.g. Microsoft OpenJDK 21) and re-run."
}

if (Test-Path $KeystorePath) {
    throw "Refusing to overwrite existing keystore at $KeystorePath. Move it aside first."
}

$secretsDir = Split-Path -Parent $KeystorePath
New-Item -ItemType Directory -Force -Path $secretsDir | Out-Null

Write-Host "Generating RSA-2048 / SHA256withRSA upload keystore at $KeystorePath" -ForegroundColor Cyan
Write-Host "Alias: $Alias" -ForegroundColor Cyan
Write-Host "Dname: $Dname" -ForegroundColor Cyan
Write-Host ""
Write-Host "You will be prompted for the keystore password and the key password." -ForegroundColor Yellow
Write-Host "BACK THESE UP OUT-OF-BAND. Losing them means losing your Play Store upload identity." -ForegroundColor Yellow
Write-Host ""

& keytool -genkeypair `
    -v `
    -keystore $KeystorePath `
    -alias $Alias `
    -keyalg RSA `
    -keysize 2048 `
    -validity 10000 `
    -dname $Dname

if ($LASTEXITCODE -ne 0) { throw "keytool failed (exit $LASTEXITCODE)." }

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Add the absolute keystore path to the env var CONCIERGE_ANDROID_KEYSTORE."
Write-Host "  2. Add the keystore password to CONCIERGE_ANDROID_KEYSTORE_PASS."
Write-Host "  3. Add the key alias ('$Alias') to CONCIERGE_ANDROID_KEY_ALIAS."
Write-Host "  4. Add the key password to CONCIERGE_ANDROID_KEY_PASS."
Write-Host "  5. Run: dotnet publish Concierge/Concierge.csproj -c Release -f net10.0-android"
Write-Host ""
Write-Host "The .jks lives under secrets/ which is gitignored — keep the backup secure." -ForegroundColor Yellow
