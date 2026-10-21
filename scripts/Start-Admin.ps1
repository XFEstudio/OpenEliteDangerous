param([ValidateSet('system','en-US','zh-CN')][string]$Language)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Localization.ps1')
Initialize-AppLanguage -Language $Language
& (Join-Path $PSScriptRoot 'Start-ManagementServer.ps1')
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskManifest=Get-Content -LiteralPath (Join-Path $taskRoot 'runtime/deployment.json') -Raw | ConvertFrom-Json
Start-Process -FilePath $taskManifest.adminExe -WorkingDirectory (Split-Path -Parent $taskManifest.adminExe) -WindowStyle Normal
