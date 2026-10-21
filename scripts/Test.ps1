param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location $taskRoot
try {
    foreach ($taskProject in @('Client/Tests/XFEEliteDangerous.Client.Tests/XFEEliteDangerous.Client.Tests.csproj','Local/Tests/XFEEliteDangerous.Tests/XFEEliteDangerous.Tests.csproj')) {
        & dotnet test $taskProject -c $Configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw ('测试失败：' + $taskProject) }
    }
    & (Join-Path $PSScriptRoot 'Check-Offline.ps1')
    & (Join-Path $PSScriptRoot 'Check-Git.ps1')
} finally { Pop-Location }