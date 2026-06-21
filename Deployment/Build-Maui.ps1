<#
.SYNOPSIS
    Build Concierge MAUI for Android (.apk + .aab) and iOS (.ipa).

.DESCRIPTION
    Mirrors the SDPKT pattern from thegeeknetwork/Deployment/Deploy-Sdpkt.ps1
    but self-contained for the circle-concierge repo. Android builds locally
    on Windows; iOS builds over SSH on the shared Geek Mac build server
    (195.82.45.44) because iOS toolchains don't run on Windows.

    Output lands in $RepoRoot\Deployment\output\:
      Concierge.Android\*-Signed.apk
      Concierge.Android\*-Signed.aab
      Concierge.iOS\*.ipa

.PARAMETER Targets
    Which platforms to build: android, ios, or all (default: all).

.PARAMETER SkipBuild
    Reuse a previous build's publish directory (for retrying packaging).

.PARAMETER DryRun
    Print what would happen without invoking dotnet.

.EXAMPLE
    .\Build-Maui.ps1
    .\Build-Maui.ps1 -Targets android
    .\Build-Maui.ps1 -Targets ios -SkipBuild

.NOTES
    Prerequisites — Android:
      1. %USERPROFILE%\keystore\TheGeekAlias.keystore exists (shared with
         CircleUp, SDPKT, and the rest of the Geek ecosystem).
      2. ANDROID_KEYSTORE_PASS environment variable set (user-scoped).

    Prerequisites — iOS:
      1. OpenSSH client installed on Windows.
      2. ~/.ssh/id_ed25519_tgn_mac present (matches the shared Mac key).
      3. Apple Distribution cert in the Mac login keychain.
      4. Provisioning profile 'Concierge App Store' installed on the Mac
         (or pass -IosProfile to override).
#>

param(
    [ValidateSet('android', 'ios', 'all')]
    [string[]]$Targets = @('all'),

    [switch]$SkipBuild,
    [switch]$DryRun,

    [string]$IosProfile = 'Concierge App Store',

    [string]$MacHost = $(if ($env:MAC_BUILD_SERVER) { $env:MAC_BUILD_SERVER } else { '195.82.45.44' }),
    [string]$MacUser = $(if ($env:MAC_BUILD_USER)   { $env:MAC_BUILD_USER }   else { 'admin' }),
    [string]$SshKey  = $(if ($env:TGN_SSH_KEY_PATH) { $env:TGN_SSH_KEY_PATH }
                         else { Join-Path $env:USERPROFILE '.ssh\id_ed25519_tgn_mac' })
)

$ErrorActionPreference = 'Stop'
$StartTime = Get-Date

# ── Constants ───────────────────────────────────────────────────────────────

$RepoRoot      = Split-Path $PSScriptRoot -Parent
$MauiProject   = Join-Path $RepoRoot 'Concierge\Concierge.csproj'
$PublishRoot   = Join-Path $PSScriptRoot 'publish'
$OutputRoot    = Join-Path $PSScriptRoot 'output'
$MacBuildDir   = '~/concierge-build'
$MacScript     = "$MacBuildDir/build-ios.sh"
$IosSigningKey = 'Apple Distribution: The Other Bhengu (Pty) Ltd (78QHBHRR7Q)'

$buildAndroid = $Targets -contains 'all' -or $Targets -contains 'android'
$buildIos     = $Targets -contains 'all' -or $Targets -contains 'ios'

$Results = [ordered]@{}

# ── Helpers ─────────────────────────────────────────────────────────────────

function Write-Header([string]$Title) {
    Write-Host "`n$('=' * 60)" -ForegroundColor Cyan
    Write-Host "  $Title"             -ForegroundColor Cyan
    Write-Host "$('=' * 60)"           -ForegroundColor Cyan
}
function Write-Step([string]$Msg) { Write-Host "  >> $Msg" -ForegroundColor Yellow }
function Write-Ok  ([string]$Msg) { Write-Host "  OK $Msg" -ForegroundColor Green  }
function Write-Err ([string]$Msg) { Write-Host "  XX $Msg" -ForegroundColor Red    }

function Invoke-MacSsh([string]$Cmd) {
    & ssh -i $SshKey -o StrictHostKeyChecking=no -o BatchMode=yes "$MacUser@$MacHost" $Cmd
}

# ── Preflight ───────────────────────────────────────────────────────────────

if (-not (Test-Path $MauiProject)) {
    Write-Err "Maui project not found: $MauiProject"
    exit 1
}

New-Item -ItemType Directory -Path $PublishRoot, $OutputRoot -Force | Out-Null

if ($DryRun) {
    Write-Host "`n[DRY-RUN] Would build the following targets: $($Targets -join ', ')`n" -ForegroundColor Magenta
}

# ── ANDROID ─────────────────────────────────────────────────────────────────

if ($buildAndroid) {
    Write-Header 'ANDROID -- Concierge'

    if (-not (Test-Path "$env:USERPROFILE\keystore\TheGeekAlias.keystore")) {
        Write-Err "Keystore missing: $env:USERPROFILE\keystore\TheGeekAlias.keystore"
        Write-Host '    Copy it from your password-manager vault or another dev box.' -ForegroundColor DarkYellow
        $Results['Android'] = 'KEYSTORE_MISSING'
    }
    elseif (-not $env:ANDROID_KEYSTORE_PASS) {
        Write-Err 'ANDROID_KEYSTORE_PASS not set in this shell.'
        Write-Host "    Run: setx ANDROID_KEYSTORE_PASS '<password>'  (then open a new shell)" -ForegroundColor DarkYellow
        $Results['Android'] = 'KEYSTORE_PASS_MISSING'
    }
    else {
        $androidPublishDir = Join-Path $PublishRoot 'Concierge.Android'
        $androidOutputDir  = Join-Path $OutputRoot  'Concierge.Android'

        if (-not $SkipBuild) {
            Write-Step 'Publishing net10.0-android (Release, AOT — ~10–15 min)...'
            New-Item -ItemType Directory -Path $androidPublishDir -Force | Out-Null

            if ($DryRun) {
                Write-Host "    dotnet publish '$MauiProject' -c Release -f net10.0-android -m:1 -o '$androidPublishDir'" -ForegroundColor DarkGray
            } else {
                $out = & dotnet publish $MauiProject -c Release -f net10.0-android -m:1 -o $androidPublishDir 2>&1
                if ($LASTEXITCODE -ne 0) {
                    $out | Select-Object -Last 25 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
                    Write-Err 'Android publish failed'
                    $Results['Android'] = 'BUILD_FAILED'
                } else {
                    Write-Ok 'Android publish complete'
                }
            }
        } else {
            Write-Step "Reusing existing publish: $androidPublishDir"
        }

        if (-not $Results['Android'] -or $Results['Android'] -eq 'OK') {
            New-Item -ItemType Directory -Path $androidOutputDir -Force | Out-Null
            $found = $false
            foreach ($pattern in @('*-Signed.apk', '*-Signed.aab')) {
                $file = Get-ChildItem $androidPublishDir -Filter $pattern -ErrorAction SilentlyContinue |
                        Select-Object -First 1
                if ($file) {
                    Copy-Item $file.FullName (Join-Path $androidOutputDir $file.Name) -Force
                    $mb = [math]::Round($file.Length / 1048576, 1)
                    Write-Ok ("$($file.Extension.TrimStart('.').ToUpper()) -> $androidOutputDir\$($file.Name) [$mb MB]")
                    $found = $true
                }
            }
            $Results['Android'] = if ($found) { 'SUCCESS' } else { 'NO_OUTPUT' }
            if (-not $found) {
                Write-Err "No *-Signed.apk / *-Signed.aab in $androidPublishDir"
                Get-ChildItem $androidPublishDir -ErrorAction SilentlyContinue |
                    ForEach-Object { Write-Host "    $($_.Name)" }
            }
        }
    }
}

# ── iOS ─────────────────────────────────────────────────────────────────────

if ($buildIos) {
    Write-Header "iOS -- Concierge (Mac: $MacUser@$MacHost)"

    if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) {
        Write-Err 'ssh not found -- install OpenSSH (Windows Settings > Apps > Optional Features)'
        $Results['iOS'] = 'SSH_NOT_FOUND'
    } elseif (-not (Test-Path $SshKey)) {
        Write-Err "SSH key not found at $SshKey"
        Write-Host '    Provision a key for the Mac build server first.' -ForegroundColor DarkYellow
        $Results['iOS'] = 'SSH_KEY_MISSING'
    } elseif ($DryRun) {
        Write-Host "    [DRY-RUN] would tar+scp Concierge source to $MacUser@$MacHost:$MacBuildDir" -ForegroundColor DarkGray
        Write-Host "    [DRY-RUN] would run dotnet publish on Mac with profile '$IosProfile'" -ForegroundColor DarkGray
        $Results['iOS'] = 'DRY_RUN'
    } else {
        $ok = $true

        if (-not $SkipBuild) {
            Write-Step 'Creating source archive...'
            $tarFile = Join-Path $env:TEMP "concierge-src-$(Get-Date -Format 'yyyyMMddHHmmss').tar.gz"
            Push-Location $RepoRoot
            try {
                # Concierge's solution structure has 11 sibling project folders rather
                # than a single code/ dir, so we include every Concierge* folder + the
                # solution + NuGet.Config so MSBuild can restore on the Mac side.
                $srcDirs = @(
                    'Concierge', 'Concierge.Ai', 'Concierge.Chat.Cloud', 'Concierge.Cli',
                    'Concierge.Diagrams.Design', 'Concierge.Media', 'Concierge.Media.Cloud',
                    'Concierge.Mesh', 'Concierge.Shared', 'Concierge.Shared.Components',
                    'Concierge.Tests', 'Concierge.Web', 'Concierge.Web.Client',
                    'Concierge.slnx', 'NuGet.Config', 'Directory.Build.props'
                ) | Where-Object { Test-Path (Join-Path $RepoRoot $_) }
                tar --exclude='*/bin' --exclude='*/obj' --exclude='*/.git' --exclude='*/node_modules' -czf $tarFile @srcDirs
                if ($LASTEXITCODE -ne 0) { throw 'tar failed' }
            } finally { Pop-Location }
            $mb = [math]::Round((Get-Item $tarFile).Length / 1048576, 1)
            Write-Ok "Archive: $mb MB"

            Write-Step 'Transferring to Mac...'
            Invoke-MacSsh "mkdir -p $MacBuildDir" | Out-Null
            $macDest = "${MacUser}@${MacHost}:$MacBuildDir/src.tar.gz"
            & scp -i $SshKey -o StrictHostKeyChecking=no -o BatchMode=yes $tarFile $macDest
            if ($LASTEXITCODE -ne 0) {
                Write-Err 'SCP transfer failed'
                $ok = $false
            }
            Remove-Item $tarFile -Force -ErrorAction SilentlyContinue

            if ($ok) {
                Write-Step 'Extracting on Mac...'
                $extractOut = Invoke-MacSsh "cd $MacBuildDir && chmod -R u+rwX . 2>/dev/null; rm -rf Concierge* NuGet.Config Directory.Build.props 2>/dev/null; tar xzf src.tar.gz && echo EXTRACT_OK"
                if ($extractOut -notmatch 'EXTRACT_OK') {
                    Write-Err 'Extraction failed'
                    $ok = $false
                } else {
                    $csCount = (Invoke-MacSsh "find $MacBuildDir -name '*.cs' | wc -l").Trim()
                    Write-Ok "Extracted ($csCount .cs files)"
                }
            }

            if ($ok) {
                Write-Step 'Writing build script on Mac...'
                $buildScript = @"
#!/bin/zsh
set -e
export PATH=`$PATH:/Users/$MacUser/.dotnet
mkdir -p $MacBuildDir/output/ios

KEYCHAIN=~/Library/Keychains/login.keychain-db
KC_PASS=""
if [ -n "`$TGN_KEYCHAIN_PASS" ]; then
  KC_PASS=`$(printf '%s' "`$TGN_KEYCHAIN_PASS" | tr -d '\r\n')
elif [ -f ~/keychain-pass ]; then
  KC_PASS=`$(tr -d '\r\n' < ~/keychain-pass)
else
  echo 'ERR: no keychain pass available (env TGN_KEYCHAIN_PASS empty, ~/keychain-pass missing)'
  exit 1
fi
security unlock-keychain -p "`$KC_PASS" `$KEYCHAIN || { echo 'ERR: unlock-keychain failed'; exit 1; }
security set-keychain-settings `$KEYCHAIN 2>&1 | head -3
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "`$KC_PASS" `$KEYCHAIN >/dev/null 2>&1 || true

echo '=== iOS Build: '`$(date)' ==='
~/.dotnet/dotnet publish $MacBuildDir/Concierge/Concierge.csproj \
  -c Release -f net10.0-ios \
  -p:RuntimeIdentifier=ios-arm64 \
  -p:PlatformTarget=ARM64 \
  -m:1 \
  -p:CodesignKey='$IosSigningKey' \
  -p:CodesignProvision='$IosProfile' \
  -o $MacBuildDir/output/ios 2>&1
echo '=== Exit: '`$?' at '`$(date)' ==='
"@
                $buildScriptLf = $buildScript.Replace("`r`n", "`n").Replace("`r", "`n")
                $buildScriptLf | ssh -i $SshKey -o StrictHostKeyChecking=no -o BatchMode=yes `
                    "$MacUser@$MacHost" "cat > $MacScript && chmod +x $MacScript"
                Write-Ok 'Build script written'
            }
        } else {
            Write-Step 'Reusing existing source tree on Mac'
        }

        if ($ok) {
            Write-Step 'Building on Mac (~20–30 min)...'
            Write-Host "    Monitor: ssh -i `"$SshKey`" $MacUser@$MacHost 'tail -f /tmp/concierge-build.log'" -ForegroundColor DarkGray

            $buildOut = Invoke-MacSsh "$MacScript 2>&1 | tee /tmp/concierge-build.log; echo BUILD_EXIT:`$?"
            $exitLine = $buildOut | Where-Object { $_ -match '^BUILD_EXIT:' } | Select-Object -Last 1
            $exitCode = if ($exitLine -match 'BUILD_EXIT:(\d+)') { [int]$Matches[1] } else { 1 }

            if ($exitCode -ne 0) {
                Write-Err "iOS build failed (exit $exitCode)"
                $buildOut | Select-Object -Last 30 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
                $ok = $false
                $Results['iOS'] = 'BUILD_FAILED'
            } else {
                Write-Ok 'iOS build succeeded on Mac'
            }
        }

        if ($ok) {
            $iosOutputDir = Join-Path $OutputRoot 'Concierge.iOS'
            New-Item -ItemType Directory -Path $iosOutputDir -Force | Out-Null
            Write-Step 'Downloading .ipa...'
            $ipaPath = (Invoke-MacSsh "find $MacBuildDir/output/ios -name '*.ipa' -type f 2>/dev/null | head -1").Trim()
            if (-not $ipaPath) {
                Write-Err "No .ipa produced in $MacBuildDir/output/ios"
                $Results['iOS'] = 'NO_OUTPUT'
            } else {
                & scp -i $SshKey -o StrictHostKeyChecking=no -o BatchMode=yes `
                    "${MacUser}@${MacHost}:$ipaPath" $iosOutputDir
                if ($LASTEXITCODE -ne 0) {
                    Write-Err 'SCP download failed'
                    $Results['iOS'] = 'DOWNLOAD_FAILED'
                } else {
                    $ipaName = Split-Path $ipaPath -Leaf
                    $localIpa = Join-Path $iosOutputDir $ipaName
                    $mb = [math]::Round((Get-Item $localIpa).Length / 1048576, 1)
                    Write-Ok "IPA -> $localIpa [$mb MB]"
                    $Results['iOS'] = 'SUCCESS'
                }
            }
        }
    }
}

# ── Summary ─────────────────────────────────────────────────────────────────

$elapsed = (Get-Date) - $StartTime
Write-Header "Summary -- elapsed $([int]$elapsed.TotalMinutes)m $($elapsed.Seconds)s"
$Results.GetEnumerator() | ForEach-Object {
    $colour = if ($_.Value -eq 'SUCCESS') { 'Green' } elseif ($_.Value -eq 'DRY_RUN') { 'DarkGray' } else { 'Red' }
    Write-Host ("  {0,-10} : {1}" -f $_.Key, $_.Value) -ForegroundColor $colour
}

$failed = $Results.Values | Where-Object { $_ -notin 'SUCCESS', 'DRY_RUN' }
if ($failed) { exit 1 }
