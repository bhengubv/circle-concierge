[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BinaryPath,
    [Parameter(Mandatory)][string]$ModelsRoot,
    [Parameter(Mandatory)][string]$DataRoot,
    [Parameter(Mandatory)][string]$ConnectionPath,
    [int]$StartupSeconds = 180
)

$ErrorActionPreference = 'Stop'
$binary = [IO.Path]::GetFullPath($BinaryPath)
$models = [IO.Path]::GetFullPath($ModelsRoot)
$data = [IO.Path]::GetFullPath($DataRoot)
$connection = [IO.Path]::GetFullPath($ConnectionPath)
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Service executable missing: $binary" }
if (-not (Test-Path -LiteralPath $models -PathType Container)) { throw "Model directory missing: $models" }
if (-not (Test-Path -LiteralPath $connection -PathType Leaf)) { throw "Existing protected connection file missing: $connection" }

function Write-HostState([string]$state, [string]$detail) {
    # Task Scheduler does not retain the host's stdout. Keep a small local
    # diagnostic without credentials, questions, answers, or model paths.
    [IO.Directory]::CreateDirectory($data) | Out-Null
    $statusPath = Join-Path $data 'host-state.json'
    $safeDetail = $detail -replace '(?i)X-Butler-Token:\s*\S+', 'X-Butler-Token: [redacted]'
    $status = @{ at = [DateTimeOffset]::UtcNow.ToString('O'); state = $state; detail = $safeDetail }
    [IO.File]::WriteAllText($statusPath, ($status | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
}

# Task Scheduler did not restart a manually started task after its child died in
# the live crash test. Keep recovery in this host, with a bounded delay so a
# failing executable cannot spin or fill memory with parallel copies.
$consecutiveFailures = 0
while ($true) {
# The released host generates a fresh token on every launch. Running two copies
# against the same model directory wastes memory and can race its housekeeping.
$existing = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -in @('CircleAI.Inference.Server.exe', 'CircleAIService-Windows-3.9.2-win-x64.exe')
})
if ($existing.Count) {
    if ($existing.Count -ne 1 -or -not [string]::Equals($existing[0].ExecutablePath, $binary,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Another Circle AI host is already running (PID $($existing.ProcessId -join ', '))."
    }
    # Task Scheduler can stop its PowerShell action without terminating the
    # released service child. Reattach only when the protected connection
    # proves that the live child owns the endpoint; never start a second model
    # process against the same weights.
    $saved = Get-Content -LiteralPath $connection -Raw | ConvertFrom-Json
    if (-not $saved.BaseUrl -or -not $saved.Token) {
        throw 'The existing Circle AI child has no protected connection to verify.'
    }
    $health = Invoke-RestMethod -Uri "$($saved.BaseUrl)/v1/health" -Headers @{'X-Butler-Token' = $saved.Token} -TimeoutSec 15
    if ([string]$health.pid -ne [string]$existing[0].ProcessId) {
        throw 'The protected connection does not belong to the existing Circle AI child.'
    }
    Write-HostState 'ready' "Adopted existing service child PID $($existing[0].ProcessId)."
    while (Get-Process -Id $existing[0].ProcessId -ErrorAction SilentlyContinue) {
        Start-Sleep -Seconds 3
    }
    continue
}

[IO.Directory]::CreateDirectory($data) | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new($binary)
$start.WorkingDirectory = [IO.Path]::GetDirectoryName($binary)
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.EnvironmentVariables['CIRCLEAI_MODELS'] = $models
$start.EnvironmentVariables['CIRCLEAI_DATA'] = $data
$start.EnvironmentVariables['CIRCLEAI_LAZY'] = '1'
$child = [Diagnostics.Process]::new()
$child.StartInfo = $start
if (-not $child.Start()) { throw 'Circle AI did not start.' }

try {
    $baseUrl = $null
    $token = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupSeconds)
    $read = $child.StandardOutput.ReadLineAsync()
    while (-not ($baseUrl -and $token)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Circle AI did not announce its endpoint and token in time.' }
        if (-not $read.Wait([TimeSpan]::FromSeconds(1))) {
            if ($child.HasExited) { throw "Circle AI exited during startup ($($child.ExitCode))." }
            continue
        }
        $line = $read.Result
        if ($null -eq $line) { throw "Circle AI closed its output during startup ($($child.ExitCode))." }
        if ($line -match '^listening on (http://127\.0\.0\.1:\d+)$') { $baseUrl = $Matches[1] }
        if ($line -match '^X-Butler-Token:\s*(\S+)$') { $token = $Matches[1] }
        if (-not ($baseUrl -and $token)) { $read = $child.StandardOutput.ReadLineAsync() }
    }

    $status = Invoke-WebRequest -Uri "$baseUrl/v1/status" -Headers @{'X-Butler-Token' = $token} -TimeoutSec 15 -UseBasicParsing
    if ($status.StatusCode -ne 200) { throw 'Circle AI did not accept its announced credential.' }
    $health = Invoke-RestMethod -Uri "$baseUrl/v1/health" -Headers @{'X-Butler-Token' = $token} -TimeoutSec 15
    if ([string]$health.pid -ne [string]$child.Id) { throw 'The announced endpoint does not belong to the launched Circle AI process.' }

    # Keep the existing restricted ACL and replace the config atomically. Never
    # print or persist the generated credential in a transcript or command line.
    $temp = Join-Path ([IO.Path]::GetDirectoryName($connection)) ('.connection-' + [Guid]::NewGuid().ToString('N') + '.json')
    $backup = Join-Path ([IO.Path]::GetDirectoryName($connection)) ('.connection-backup-' + [Guid]::NewGuid().ToString('N') + '.json')
    try {
        $json = @{ BaseUrl = $baseUrl; Token = $token; ModelsRoot = $models } | ConvertTo-Json -Compress
        [IO.File]::WriteAllText($temp, $json, [Text.UTF8Encoding]::new($false))
        $sourceAcl = Get-Acl -LiteralPath $connection
        $tempAcl = Get-Acl -LiteralPath $temp
        $accessSection = [Security.AccessControl.AccessControlSections]::Access
        $tempAcl.SetSecurityDescriptorSddlForm($sourceAcl.GetSecurityDescriptorSddlForm($accessSection), $accessSection)
        Set-Acl -LiteralPath $temp -AclObject $tempAcl
        [IO.File]::Replace($temp, $connection, $backup)
    }
    finally {
        [IO.File]::Delete($temp)
        [IO.File]::Delete($backup)
    }
    $token = $null
    $consecutiveFailures = 0
    Write-HostState 'ready' "Connected service child PID $($child.Id)."
    Write-Host "Circle AI connected at $baseUrl (PID $($child.Id)). Keep this host running."

    # Continue draining output so the child cannot block on a full pipe. Its
    # prompts and generated answers are deliberately not copied to this console.
    while ($null -ne $child.StandardOutput.ReadLine()) { }
    throw "Circle AI exited ($($child.ExitCode))."
}
catch {
    if ($_.Exception -is [System.Management.Automation.PipelineStoppedException]) { throw }
    $consecutiveFailures++
    $delay = [int][Math]::Min(60, [Math]::Pow(2, [Math]::Min($consecutiveFailures, 6)))
    Write-HostState 'retrying' "$($_.Exception.GetType().Name): $($_.Exception.Message)"
    Write-Warning "Circle AI host stopped ($($_.Exception.GetType().Name)); retrying in $delay seconds."
}
finally {
    if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit() }
    $child.Dispose()
}
Start-Sleep -Seconds $delay
}
