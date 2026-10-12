[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$PackageFolder,
 [Parameter(Mandatory)][string]$ConnectionFile,
 [Parameter(Mandatory)][string]$ClientSid,
 # Import source only; the service never uses this profile path.
 [Parameter(Mandatory)][string]$ModelsRoot,
 [int]$Port=5208,
 [switch]$ConfigureExisting,
 [string]$ReportPath
)
$ErrorActionPreference='Stop'
if($ReportPath){Start-Transcript -LiteralPath $ReportPath -Force | Out-Null}
try {
 $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
 if(-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator approval is required.'}
 $serviceName='CircleAI.Inference.Server'
 $account="NT SERVICE\$serviceName"
 $destination=Join-Path $env:ProgramFiles 'Concierge\CircleAIService\3.8.1'
 $binary=Join-Path $destination 'CircleAI.Inference.Server.exe'
 $dataRoot=[IO.Path]::GetFullPath((Join-Path $env:ProgramData 'Concierge\CircleAIService'))
 $sharedModels=Join-Path $dataRoot 'models'
 $runtimeRoot=Join-Path $dataRoot 'runtime'
 $profileRoot=Join-Path $dataRoot 'profile'
 $localRoot=Join-Path $profileRoot 'Local'
 $roamingRoot=Join-Path $profileRoot 'Roaming'
 $clientConnection=Join-Path $dataRoot 'connection.json'
 $connection=Get-Content -LiteralPath $ConnectionFile -Raw | ConvertFrom-Json
 if($connection.BaseUrl -ne "http://127.0.0.1:$Port" -or [string]::IsNullOrWhiteSpace($connection.ApiKey)){throw 'Invalid authenticated loopback connection.'}
 $existing=Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
 if($ConfigureExisting){
  if($null -eq $existing -or !($existing.PathName.Trim('"') -eq $binary -or $existing.PathName.StartsWith('"'+$binary+'" ',[StringComparison]::OrdinalIgnoreCase))){throw 'This is not the service installation owned by Concierge.'}
  Stop-Service -Name $serviceName
 } else {
  if($existing){throw 'The service already exists; use ConfigureExisting.'}
  if(!(Test-Path -LiteralPath (Join-Path $PackageFolder 'CircleAI.Inference.Server.exe'))){throw 'Released executable missing.'}
  New-Item -ItemType Directory -Path $destination -Force | Out-Null
  Get-ChildItem -LiteralPath $PackageFolder | Copy-Item -Destination $destination -Recurse -Force
  & sc.exe create $serviceName binPath= ('"'+$binary+'"') start= auto obj= $account DisplayName= 'Circle AI for Concierge'
  if($LASTEXITCODE -ne 0){throw 'Service registration failed.'}
 }
 $serviceSid=[Security.Principal.NTAccount]::new($account).Translate([Security.Principal.SecurityIdentifier]).Value
 foreach($folder in @($dataRoot,$sharedModels,$runtimeRoot,$localRoot,$roamingRoot)){New-Item -ItemType Directory -Path $folder -Force | Out-Null}
 & icacls.exe $dataRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "*$($ClientSid):(OI)(CI)F" "*$($serviceSid):(OI)(CI)M" | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Could not protect the Concierge data root.'}
 foreach($folder in @($sharedModels,$runtimeRoot,$localRoot,$roamingRoot)){
  & icacls.exe $folder /grant "*$($ClientSid):(OI)(CI)F" "*$($serviceSid):(OI)(CI)M" | Out-Null
  if($LASTEXITCODE -ne 0){throw "Could not provision $folder."}
 }
 $importRoot=[IO.Path]::GetFullPath($ModelsRoot)
 if($importRoot -ne $sharedModels){
  if(!(Test-Path -LiteralPath $importRoot -PathType Container)){throw 'Model import folder missing.'}
  Write-Output "Copying cached models to $sharedModels; preserving the original."
  & robocopy.exe $importRoot $sharedModels /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /XJ /NFL /NDL /NP
  if($LASTEXITCODE -ge 8){throw "Model import failed ($LASTEXITCODE)."}
 }
 # Explicit command-line paths take precedence. Avoid PowerShell 5 native
 # argument conversion removing quotes from the SCM executable path.
 $launch='"'+$binary+'" --environment Production --CircleAIServer:ModelStorageRoot="'+$sharedModels+'" --CircleAIServer:RuntimeCacheRoot="'+$runtimeRoot+'"'
 $native=[Diagnostics.ProcessStartInfo]::new()
 $native.FileName=Join-Path $env:SystemRoot 'System32\sc.exe'
 $native.UseShellExecute=$false
 $native.CreateNoWindow=$true
 $native.Arguments='config '+$serviceName+' binPath= "'+$launch.Replace('"','\"')+'"'
 $process=[Diagnostics.Process]::Start($native)
 $process.WaitForExit()
 if($process.ExitCode -ne 0){throw 'Could not set explicit service launch paths.'}
 & sc.exe sidtype $serviceName unrestricted | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Could not enable service SID.'}
 & sc.exe failure $serviceName reset= 300 actions= restart/5000/restart/15000/restart/30000 | Out-Null
 $settings=@{Urls=$connection.BaseUrl;CircleAIServer=@{
  ModelStorageRoot=$sharedModels;RuntimeCacheRoot=$runtimeRoot;MaxConcurrentRequests=1;RequestTimeoutSeconds=120
  Auth=@{ApiKey=@{Enabled=$true;HeaderName='X-CircleAI-Api-Key';Keys=@($connection.ApiKey)}}
 }}
 $settingsPath=Join-Path $destination 'appsettings.Production.json'
 $settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settingsPath -Encoding utf8
 & icacls.exe $settingsPath /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' "*$($serviceSid):R" "*$($ClientSid):R" | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Could not protect the server key.'}
 @{BaseUrl=$connection.BaseUrl;ApiKey=$connection.ApiKey;ModelsRoot=$sharedModels} | ConvertTo-Json | Set-Content -LiteralPath $clientConnection -Encoding utf8
 & icacls.exe $clientConnection /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' "*$($ClientSid):F" "*$($serviceSid):R" | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Could not protect the client key.'}
 New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name Environment -PropertyType MultiString -Force -Value @(
  'ASPNETCORE_ENVIRONMENT=Production',"ASPNETCORE_URLS=$($connection.BaseUrl)",
  "CircleAIServer__ModelStorageRoot=$sharedModels","CircleAIServer__RuntimeCacheRoot=$runtimeRoot",
  "LOCALAPPDATA=$localRoot","APPDATA=$roamingRoot","USERPROFILE=$profileRoot"
 ) | Out-Null
 # Revoke only the service's earlier grants on the old user-cache ancestry.
 $oldCache=Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($ConnectionFile))) 'models'
 if($importRoot -eq $oldCache -and $importRoot -ne $sharedModels){
  $ancestor=[IO.DirectoryInfo]::new($importRoot)
  while($null -ne $ancestor -and $null -ne $ancestor.Parent){
   & icacls.exe $ancestor.FullName /remove:g "*$serviceSid" | Out-Null
   if($LASTEXITCODE -ne 0){throw "Could not revoke the service grant on $($ancestor.FullName)."}
   $ancestor=$ancestor.Parent
  }
 }
 if(![Diagnostics.EventLog]::SourceExists($serviceName)){[Diagnostics.EventLog]::CreateEventSource($serviceName,'Application')}
 Start-Service -Name $serviceName
 Get-CimInstance Win32_Service -Filter "Name='$serviceName'" | Select-Object Name,State,StartName,PathName
 Write-Output "Client connection: $clientConnection"
} finally {if($ReportPath){Stop-Transcript | Out-Null}}
