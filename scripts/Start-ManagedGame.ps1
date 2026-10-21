param([string]$Account,[ValidateSet('system','en-US','zh-CN')][string]$Language)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Localization.ps1')
Initialize-AppLanguage -Language $Language
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskManifest=Get-Content -LiteralPath (Join-Path $taskRoot 'runtime/deployment.json') -Raw | ConvertFrom-Json
if(-not $Account){$Account=$taskManifest.account}
if($Account -notmatch '^[a-f0-9]{32}$'){throw (Get-AppText 'Launch.InvalidAccount')}
$taskInfo=[Diagnostics.ProcessStartInfo]::new($taskManifest.bootstrapperExe)
$taskInfo.WorkingDirectory=$taskManifest.workingDirectory;$taskInfo.UseShellExecute=$false
foreach($taskArgument in @('--debug-solo','--workspace',$taskManifest.workspace,'--account',$Account,'--runtime',$taskManifest.nativeRuntime)){$taskInfo.ArgumentList.Add($taskArgument)}
$taskInfo.CreateNoWindow=$true;$taskInfo.WindowStyle='Hidden'
[Diagnostics.Process]::Start($taskInfo) | Select-Object Id
