param(
    [Parameter(Mandatory)][string]$RunDirectoryA,
    [Parameter(Mandatory)][string]$RunDirectoryB,
    [Parameter(Mandatory)][string]$FixtureDirectory,
    [Parameter(Mandatory)][string]$Python,
    [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $taskRoot ('artifacts/native-online-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
& $Python (Join-Path $PSScriptRoot 'capture/test_native_online.py') --launch-a (Join-Path $RunDirectoryA 'launch.json') --launch-b (Join-Path $RunDirectoryB 'launch.json') --fixture $FixtureDirectory --out $OutputDirectory
if($LASTEXITCODE -ne 0){throw ('双原生客户端测试失败；请检查 '+(Join-Path $OutputDirectory 'native-online-validation.json'))}
Write-Output ('双原生客户端测试通过：'+(Join-Path $OutputDirectory 'native-online-validation.json'))
