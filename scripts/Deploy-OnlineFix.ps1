param(
    [Parameter(Mandatory)][string]$NativeReport,
    [Parameter(Mandatory)][string]$ValidatedServerDirectory
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskProof=Get-Content -LiteralPath $NativeReport -Raw | ConvertFrom-Json
if(-not $taskProof.passed -or $taskProof.desktopInputUsed -or $taskProof.gameRestarts -ne 0 -or $taskProof.clients.Count -ne 2 -or $taskProof.trajectories.Count -ne 4 -or @($taskProof.checks|Where-Object {-not $_.passed}).Count){throw '需要完整通过的原生双客户端报告。'}
$taskManifestPath=Join-Path $taskRoot 'runtime/deployment.json'
$taskPrevious=Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json -AsHashtable
$taskRelease=Join-Path $taskRoot ('runtime/online-fix-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
if(Test-Path -LiteralPath $taskRelease){throw '发布目录已存在。'}
$taskReports=Join-Path $taskRelease 'reports'
New-Item -ItemType Directory -Path $taskReports | Out-Null
foreach($taskPair in @(@('server','Server/XFEEliteDangerous.Server/XFEEliteDangerous.Server.csproj'),@('bootstrapper','Client/XFEEliteDangerous.Bootstrapper/XFEEliteDangerous.Bootstrapper.csproj'))){
    $taskOutput=Join-Path $taskRelease $taskPair[0]
    & dotnet publish (Join-Path $taskRoot $taskPair[1]) -c Release -r win-x64 -p:Platform=x64 --self-contained true -o $taskOutput --nologo -v minimal *> (Join-Path $taskReports ('publish-'+$taskPair[0]+'.log'))
    if($LASTEXITCODE -ne 0){throw ('发布失败，原服务器保持运行：'+$taskPair[0])}
}
foreach($taskName in @('XFEEliteDangerous.Core.dll','XFEEliteDangerous.NativeFormat.dll')){
    $taskExpected=(Get-FileHash -LiteralPath (Join-Path $ValidatedServerDirectory $taskName) -Algorithm SHA256).Hash
    $taskActual=(Get-FileHash -LiteralPath (Join-Path $taskRelease ('server/'+$taskName)) -Algorithm SHA256).Hash
    if($taskExpected -ne $taskActual){throw ('候选服务器与原生测试使用的程序集不一致：'+$taskName)}
}
# Retain the exact native bridge and unchanged administrator that were already
# validated; keep the new deployment independently usable as one complete folder.
Copy-Item -LiteralPath (Split-Path -Parent $taskPrevious.adminExe) -Destination (Join-Path $taskRelease 'admin') -Recurse
Copy-Item -LiteralPath (Split-Path -Parent $taskPrevious.nativeRuntime) -Destination (Join-Path $taskRelease 'native') -Recurse
$taskNative=Join-Path $taskRelease 'native/XFEEliteDangerous.OfflineRuntime.dll'
if((Get-FileHash -LiteralPath $taskNative).Hash -ne (Get-FileHash -LiteralPath $taskPrevious.nativeRuntime).Hash){throw '原生桥接副本校验失败。'}
$taskBootstrapper=Join-Path $taskRelease 'bootstrapper/XFEEliteDangerous.Bootstrapper.exe'
& $taskBootstrapper --loader-check *> (Join-Path $taskReports 'loader-check.log')
if($LASTEXITCODE -ne 0){throw '候选启动器的原生加载回归检查失败。'}
Copy-Item -LiteralPath $NativeReport -Destination (Join-Path $taskReports 'native-online-validation.json')
Copy-Item -LiteralPath $taskManifestPath -Destination (Join-Path $taskReports 'previous-deployment.json')
$taskManifest=$taskPrevious.Clone()
$taskManifest.createdUtc=[DateTimeOffset]::UtcNow
$taskManifest.release=$taskRelease;$taskManifest.reports=$taskReports
$taskManifest.serverExe=Join-Path $taskRelease 'server/XFEEliteDangerous.Server.exe'
$taskManifest.bootstrapperExe=$taskBootstrapper
$taskManifest.adminExe=Join-Path $taskRelease 'admin/XFEEliteDangerous.Desktop.exe'
$taskManifest.nativeRuntime=$taskNative
& (Join-Path $PSScriptRoot 'Stop-ManagementServer.ps1')
$taskText=$taskManifest|ConvertTo-Json -Depth 8
[IO.File]::WriteAllText((Join-Path $taskPrevious.dataRoot 'deployment.json'),$taskText,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($taskManifestPath,$taskText,[Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot 'Start-ManagementServer.ps1')
Write-Output ('联机修复部署完成：'+$taskRelease)
