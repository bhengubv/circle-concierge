[CmdletBinding()]
param(
    [ValidateSet('CurrentUser', 'System')][string]$Scope = 'CurrentUser',
    [string]$TaskName
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdministrator = [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($Scope -eq 'System' -and -not $isAdministrator) {
    throw 'Administrator approval is required to register the independent Circle AI startup task.'
}
if (-not $TaskName) {
    $TaskName = if ($Scope -eq 'System') { 'CircleAIService-3.9.2' } else { 'CircleAIService-3.9.2-User' }
}

$root = 'C:\ProgramData\Concierge\CircleAIService'
$binary = Join-Path $root 'bin\3.9.2\CircleAIService-Windows-3.9.2-win-x64.exe'
$hostScript = Join-Path $root 'host\Run-CircleAIStandalone.ps1'
$models = Join-Path $root 'models'
$data = Join-Path $root 'service-3.9.2'
$connection = Join-Path $root 'connection.json'
$powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
foreach ($file in @($binary, $hostScript, $connection, $powerShell)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required file missing: $file" }
}
if (-not (Test-Path -LiteralPath $models -PathType Container)) { throw "Model directory missing: $models" }

# Pin this installation to the released 3.9.2 executable already proven live.
$expectedHash = '4438809D6054A7CB48B6FFC80FD767B17CAEAA778349D32C00992F50CB45AEC9'
if ((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'The installed Circle AI executable does not match the verified 3.9.2 package.'
}

$arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy RemoteSigned -File "{0}" -BinaryPath "{1}" -ModelsRoot "{2}" -DataRoot "{3}" -ConnectionPath "{4}"' -f $hostScript, $binary, $models, $data, $connection
$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments -WorkingDirectory $root
$trigger = if ($Scope -eq 'System') { New-ScheduledTaskTrigger -AtStartup } else { New-ScheduledTaskTrigger -AtLogOn -User $identity.Name }
$principal = if ($Scope -eq 'System') {
    New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
} else {
    New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Limited
}
$settings = New-ScheduledTaskSettingsSet -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable
$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) {
    $oldAction = $existing.Actions | Select-Object -First 1
    if ($oldAction.Execute -ne $powerShell -or $oldAction.Arguments -notmatch [regex]::Escape($hostScript)) {
        throw "An unrelated scheduled task already uses the name $TaskName."
    }
}
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Independent Circle AI 3.9.2 host for Concierge and other local clients; publishes a protected loopback connection.' -Force | Out-Null
Get-ScheduledTask -TaskName $TaskName | Select-Object TaskName, State, TaskPath
