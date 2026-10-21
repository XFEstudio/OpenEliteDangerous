param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location $taskRoot
try {
    & dotnet build 'XFEEliteDangerous.slnx' -c $Configuration -p:Platform=x64 --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw '离线解决方案编译失败。' }
} finally { Pop-Location }