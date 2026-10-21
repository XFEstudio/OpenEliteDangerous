param([ValidateSet('system','en-US','zh-CN')][string]$Language)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Localization.ps1')
Initialize-AppLanguage -Language $Language
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskManifest=Get-Content -LiteralPath (Join-Path $taskRoot 'runtime/deployment.json') -Raw | ConvertFrom-Json
try{$taskHealth=Invoke-RestMethod -Uri ($taskManifest.address+'health') -TimeoutSec 2}catch{$taskHealth=$null}
if($null -ne $taskHealth){
    if($taskHealth.framework -ne 'XFEExtension.NetCore.ServerInteractive' -or $taskHealth.workspace -ne $taskManifest.workspace){throw (Get-AppText 'Launch.PortBusy')}
    Write-Output ((Get-AppText 'Launch.AlreadyRunning')+$taskManifest.address);return
}
$taskInfo=[Diagnostics.ProcessStartInfo]::new($taskManifest.serverExe)
$taskInfo.ArgumentList.Add('--config');$taskInfo.ArgumentList.Add($taskManifest.configuration)
$taskInfo.WorkingDirectory=$taskManifest.release;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true;$taskInfo.WindowStyle='Hidden'
$taskProcess=[Diagnostics.Process]::Start($taskInfo)
# The deployed server writes its own console log. It must not depend on pipes
# drained by this short-lived launcher process.
for($taskAttempt=0;$taskAttempt -lt 50;$taskAttempt++){
    if($taskProcess.HasExited){throw ((Get-AppText 'Launch.FailedLog')+(Join-Path $taskManifest.dataRoot 'logs/startup-errors.log'))}
    try{$taskHealth=Invoke-RestMethod -Uri ($taskManifest.address+'health') -TimeoutSec 1;if($taskHealth.status -eq 'ready'){break}}catch{}
    Start-Sleep -Milliseconds 200
}
if($null -eq $taskHealth -or $taskHealth.status -ne 'ready'){throw (Get-AppText 'Launch.NotReady')}
@{pid=$taskProcess.Id;exe=$taskManifest.serverExe;address=$taskManifest.address;startedUtc=[DateTimeOffset]::UtcNow} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskManifest.dataRoot 'server-process.json') -Encoding utf8NoBOM
Write-Output ((Get-AppText 'Launch.Ready')+$taskManifest.address+'; PID='+$taskProcess.Id)
