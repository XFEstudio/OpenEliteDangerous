param([string]$OutputDirectory,[string]$ServerDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot ('artifacts/localization-http-' + [guid]::NewGuid().ToString('N')) }
$taskProject = Join-Path $PSScriptRoot 'LocalizationIntegration/LocalizationIntegration.csproj'
$taskArguments = @(([IO.Path]::GetFullPath($OutputDirectory)))
if ($ServerDirectory) { $taskArguments += [IO.Path]::GetFullPath((Join-Path $ServerDirectory 'XFEEliteDangerous.Server.exe')) }
& dotnet run --project $taskProject -c Release -- @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Localization integration checks failed. See the isolated output directory.' }
