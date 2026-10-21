param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [ValidateSet('Client')][string]$Role = 'Client')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location $taskRoot
try {
    & dotnet build 'Client/XFEEliteDangerous.Desktop/XFEEliteDangerous.Desktop.csproj' -c $Configuration -p:Platform=x64 --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw '客户端编译失败。' }
    $taskExe = Join-Path $taskRoot ('Client/XFEEliteDangerous.Desktop/bin/x64/' + $Configuration + '/net10.0-windows10.0.22621.0/win-x64/XFEEliteDangerous.Desktop.exe')
    if (!(Test-Path -LiteralPath $taskExe)) { throw '未找到客户端程序。' }
    Start-Process -FilePath $taskExe -WorkingDirectory (Split-Path -Parent $taskExe) | Out-Null
} finally { Pop-Location }