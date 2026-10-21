param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location $taskRoot
try {
    & dotnet publish 'Client/XFEEliteDangerous.Desktop/XFEEliteDangerous.Desktop.csproj' -c $Configuration -p:Platform=x64 -o 'artifacts/publish-offline-client' --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw '离线客户端发布失败。' }
    Write-Output '客户端输出：artifacts/publish-offline-client。原生 SOLO 尚未接通。'
} finally { Pop-Location }