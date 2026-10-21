param([string]$Account,[ValidateSet('system','en-US','zh-CN')][string]$Language)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Localization.ps1')
Initialize-AppLanguage -Language $Language
& (Join-Path $PSScriptRoot 'Start-ManagementServer.ps1')
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskDeployment=Get-Content -LiteralPath (Join-Path $taskRoot 'runtime/deployment.json') -Raw | ConvertFrom-Json
$taskProfile=Join-Path $taskDeployment.dataRoot 'game-client.json'
if(-not (Test-Path -LiteralPath $taskProfile)){throw (Get-AppText 'Launch.OnlineUnavailable')}
if(-not $Account){$Account=$taskDeployment.account}
if($Account -notmatch '^[a-f0-9]{32}$'){throw (Get-AppText 'Launch.InvalidCommander')}
$taskInfo=[Diagnostics.ProcessStartInfo]::new($taskDeployment.bootstrapperExe)
$taskInfo.WorkingDirectory=$taskDeployment.workingDirectory;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true;$taskInfo.WindowStyle='Hidden'
foreach($taskValue in @('--debug-solo','--account',$Account,'--runtime',$taskDeployment.nativeRuntime,'--server-profile',$taskProfile)){$taskInfo.ArgumentList.Add($taskValue)}
[Diagnostics.Process]::Start($taskInfo) | Select-Object Id
