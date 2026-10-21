param([ValidateSet('system','en-US','zh-CN')][string]$Language)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Localization.ps1')
Initialize-AppLanguage -Language $Language
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskManifest=Get-Content -LiteralPath (Join-Path $taskRoot 'runtime/deployment.json') -Raw | ConvertFrom-Json
$taskHealth=Invoke-RestMethod -Uri ($taskManifest.address+'health') -TimeoutSec 2
if($taskHealth.framework -ne 'XFEExtension.NetCore.ServerInteractive' -or $taskHealth.workspace -ne $taskManifest.workspace){throw (Get-AppText 'Launch.StopMismatch')}
$taskAccess=Get-Content -LiteralPath (Join-Path $taskManifest.dataRoot 'admin-access.json') -Raw | ConvertFrom-Json
Invoke-RestMethod -Uri ($taskManifest.address+'shutdown') -Method Post -Headers @{Authorization=('Bearer '+$taskAccess.key)} | Out-Null
$taskProcess=Get-Process -Id $taskHealth.pid -ErrorAction SilentlyContinue
if($null -ne $taskProcess -and -not $taskProcess.WaitForExit(15000)){throw (Get-AppText 'Launch.StopPending')}
Write-Output (Get-AppText 'Launch.StopRequested')
