param(
    [string]$SourceWorkspace=(Join-Path $PSScriptRoot '../artifacts/offline-conversion-20261020/workspace'),
    [string]$DataRoot=(Join-Path $env:LOCALAPPDATA 'XFEEliteDangerous/Server'),
    [int]$Port=48080,
    [switch]$EnableOnline,
    [string]$GameBindAddress='0.0.0.0',
    [int]$GamePort=48081
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskData=[IO.Path]::GetFullPath($DataRoot)
$taskWorkspace=Join-Path $taskData 'Workspace'
$taskRelease=Join-Path $taskRoot ('runtime/local-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
if(Test-Path -LiteralPath $taskRelease){throw '本次部署目录已存在。'}
New-Item -ItemType Directory -Path $taskRelease | Out-Null
$taskReports=Join-Path $taskRelease 'reports';New-Item -ItemType Directory -Path $taskReports | Out-Null
foreach($taskPair in @(@('server','Server/XFEEliteDangerous.Server/XFEEliteDangerous.Server.csproj'),@('admin','Client/XFEEliteDangerous.Desktop/XFEEliteDangerous.Desktop.csproj'),@('bootstrapper','Client/XFEEliteDangerous.Bootstrapper/XFEEliteDangerous.Bootstrapper.csproj'),@('native','Client/XFEEliteDangerous.OfflineRuntime/XFEEliteDangerous.OfflineRuntime.csproj'))) {
    $taskName=$taskPair[0];$taskProject=Join-Path $taskRoot $taskPair[1];$taskOutput=Join-Path $taskRelease $taskName
    Write-Output ('正在发布 '+$taskName+'…')
    & dotnet publish $taskProject -c Release -r win-x64 -p:Platform=x64 --self-contained true -o $taskOutput --nologo -v minimal *> (Join-Path $taskReports ('publish-'+$taskName+'.log'))
    if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $taskReports ('publish-'+$taskName+'.log')) -Tail 25;throw ($taskName+' 发布失败，当前部署入口保持不变。')}
}
if(-not (Test-Path -LiteralPath (Join-Path $taskWorkspace 'state.json'))) {
    if(Test-Path -LiteralPath $taskWorkspace){throw '目标工作区存在但没有存档；请检查后重试，部署不会覆盖它。'}
    $taskSource=[IO.Path]::GetFullPath($SourceWorkspace)
    New-Item -ItemType Directory -Path $taskWorkspace | Out-Null
    $taskLease=$null;$taskDeadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
    try {
        while($null -eq $taskLease) {
            try{$taskLease=[IO.File]::Open((Join-Path $taskSource 'workspace.lock'),'OpenOrCreate','ReadWrite','None')}
            catch [IO.IOException]{if([DateTimeOffset]::UtcNow -gt $taskDeadline){throw};Start-Sleep -Milliseconds 50}
        }
        [IO.File]::Copy((Join-Path $taskSource 'state.json'),(Join-Path $taskWorkspace 'state.json'),$false)
    } finally {if($null -ne $taskLease){$taskLease.Dispose()}}
    foreach($taskDirectory in @('native-resources','native-journal')){
        $taskFrom=Join-Path $taskSource $taskDirectory
        if(Test-Path -LiteralPath $taskFrom){Copy-Item -LiteralPath $taskFrom -Destination (Join-Path $taskWorkspace $taskDirectory) -Recurse}
    }
}
$taskReferenceRoot=Join-Path $taskWorkspace 'native-resources'
$taskReferenceManifest=Join-Path $taskReferenceRoot 'manifest.json'
if(Test-Path -LiteralPath $taskReferenceManifest){
    $taskReference=Get-Content -LiteralPath $taskReferenceManifest -Raw | ConvertFrom-Json
    if(-not @($taskReference.documents | Where-Object {$_.operation -eq '/2.0/elite/resources/commodities'}).Count){
        $taskCommodity=Join-Path $taskRoot 'Local/data/native-commodities.json'
        $taskHash=(Get-FileHash -LiteralPath $taskCommodity -Algorithm SHA256).Hash.ToLowerInvariant()
        $taskFilename='reference-commodities-'+$taskHash.Substring(0,12)+'.json'
        Copy-Item -LiteralPath $taskCommodity -Destination (Join-Path $taskReferenceRoot $taskFilename)
        $taskReference.documents=@($taskReference.documents)+@(@{operation='/2.0/elite/resources/commodities';file=$taskFilename;sha256=$taskHash;length=(Get-Item -LiteralPath $taskCommodity).Length;source='Local/data/native-commodities.json'})
        $taskReference | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskReferenceManifest -Encoding utf8NoBOM
    }
}
Write-Output '正在发布配套的原生存档与业务热模块…'
& (Join-Path $PSScriptRoot 'Publish-ReadBusiness.ps1') -Workspace $taskWorkspace *> (Join-Path $taskReports 'publish-business.log')
if($LASTEXITCODE -ne 0){throw '业务模块发布失败。'}
$taskState=Get-Content -LiteralPath (Join-Path $taskWorkspace 'state.json') -Raw | ConvertFrom-Json -AsHashtable
$taskAccount=@($taskState.accounts.GetEnumerator() | Where-Object {$null -ne $_.Value.nativeSave -and -not $_.Value.disabled})[0].Key
if(-not $taskAccount){throw '需要至少一个已准备的原生指挥官档案。'}
# Switch only after all new components have been built. A healthy old service
# would otherwise be reused by Start-ManagementServer after manifest replacement.
$taskPreviousManifest=Join-Path $taskRoot 'runtime/deployment.json'
if(Test-Path -LiteralPath $taskPreviousManifest){
    $taskPrevious=Get-Content -LiteralPath $taskPreviousManifest -Raw | ConvertFrom-Json
    try{$taskPreviousHealth=Invoke-RestMethod -Uri ($taskPrevious.address+'health') -TimeoutSec 2}catch{$taskPreviousHealth=$null}
    if($null -ne $taskPreviousHealth){
        & (Join-Path $PSScriptRoot 'Stop-ManagementServer.ps1')
    }
}
$taskConfig=Join-Path $taskData 'server.json'
$taskServerConfig=@{workspace=$taskWorkspace;dataRoot=$taskData;port=$Port;artifactsRoot=(Join-Path $taskRoot 'artifacts');background=$true}
if($EnableOnline){$taskServerConfig.online=@{enabled=$true;bindAddress=$GameBindAddress;port=$GamePort}}
elseif(Test-Path -LiteralPath $taskConfig){$taskOldConfig=Get-Content -LiteralPath $taskConfig -Raw | ConvertFrom-Json -AsHashtable;if($taskOldConfig.online){$taskServerConfig.online=$taskOldConfig.online}}
$taskServerConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskConfig -Encoding utf8NoBOM
$taskManifest=@{schema=1;createdUtc=[DateTimeOffset]::UtcNow;release=$taskRelease;serverExe=(Join-Path $taskRelease 'server/XFEEliteDangerous.Server.exe');adminExe=(Join-Path $taskRelease 'admin/XFEEliteDangerous.Desktop.exe');
    bootstrapperExe=(Join-Path $taskRelease 'bootstrapper/XFEEliteDangerous.Bootstrapper.exe');nativeRuntime=(Join-Path $taskRelease 'native/XFEEliteDangerous.OfflineRuntime.dll');
    configuration=$taskConfig;dataRoot=$taskData;workspace=$taskWorkspace;address=('http://127.0.0.1:'+$Port+'/api/');account=$taskAccount;workingDirectory=$taskRoot;reports=$taskReports;gameProfile=(Join-Path $taskData 'game-client.json')}
New-Item -ItemType Directory -Path (Join-Path $taskRoot 'runtime') -Force | Out-Null
$taskManifestJson=$taskManifest | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $taskData 'deployment.json'),$taskManifestJson,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $taskRoot 'runtime/deployment.json'),$taskManifestJson,[Text.UTF8Encoding]::new($false))
Write-Output ('发布完成：'+$taskRelease)
& (Join-Path $PSScriptRoot 'Start-ManagementServer.ps1')
