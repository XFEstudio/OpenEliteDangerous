param(
    [string]$DataRoot=(Join-Path $env:LOCALAPPDATA 'XFEEliteDangerous/Server'),
    [string]$SourceWorkspace,
    [string]$Account,
    [ValidateRange(1024,65535)][int]$Port=48080,
    [string]$GameBindAddress='0.0.0.0',
    [ValidateRange(1024,65535)][int]$GamePort=48081
)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell 7 or later is required.'}
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskReleaseFile=Join-Path $taskRoot 'release-manifest.json'
if(-not (Test-Path -LiteralPath $taskReleaseFile)){throw 'Run this script inside the extracted Windows release package.'}
$taskRelease=Get-Content -LiteralPath $taskReleaseFile -Raw|ConvertFrom-Json
function Assert-LocalDirectory([string]$Path){
    $resolved=[IO.Path]::GetFullPath($Path)
    if($resolved.StartsWith('\\') -or $resolved -notmatch '^[A-Za-z]:\\'){throw 'Use an absolute local Windows drive path.'}
    for($cursor=[IO.DirectoryInfo]::new($resolved);$null -ne $cursor;$cursor=$cursor.Parent){
        if($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Filesystem links are unsupported for release data.'}
    }
    return $resolved
}
$taskRoot=Assert-LocalDirectory $taskRoot
$taskData=Assert-LocalDirectory $DataRoot
if($taskRelease.schema -ne 1 -or -not $taskRelease.files){throw 'Unsupported release manifest.'}
foreach($taskEntry in $taskRelease.files){
    $taskFile=[IO.Path]::GetFullPath((Join-Path $taskRoot $taskEntry.path))
    if(-not $taskFile.StartsWith($taskRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $taskFile -PathType Leaf) -or (Get-FileHash -LiteralPath $taskFile).Hash -ne $taskEntry.sha256){throw ('Release integrity check failed: '+$taskEntry.path)}
}
$taskConfigFile=Join-Path $taskData 'server.json'
$taskConfig=if(Test-Path -LiteralPath $taskConfigFile){Get-Content -LiteralPath $taskConfigFile -Raw|ConvertFrom-Json -AsHashtable}else{
    @{workspace=(Join-Path $taskData 'Workspace');dataRoot=$taskData;port=$Port;online=@{enabled=$true;bindAddress=$GameBindAddress;port=$GamePort}}
}
if($PSBoundParameters.ContainsKey('Port')){$taskConfig.port=$Port}
if(-not $taskConfig.online -and ($PSBoundParameters.ContainsKey('GameBindAddress') -or $PSBoundParameters.ContainsKey('GamePort'))){$taskConfig.online=@{enabled=$true;bindAddress=$GameBindAddress;port=$GamePort}}
if($PSBoundParameters.ContainsKey('GameBindAddress')){$taskConfig.online.bindAddress=$GameBindAddress}
if($PSBoundParameters.ContainsKey('GamePort')){$taskConfig.online.port=$GamePort}
if($taskConfig.online -and $taskConfig.online.enabled){
    $taskIp=$null
    if(-not [Net.IPAddress]::TryParse($taskConfig.online.bindAddress,[ref]$taskIp) -or $taskIp.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $taskConfig.online.port -eq $taskConfig.port){throw 'Game listener requires a valid IPv4 address and a separate port.'}
}
$taskAddress='http://127.0.0.1:'+$taskConfig.port+'/api/'
try{$taskRunning=Invoke-RestMethod -Uri ($taskAddress+'health') -TimeoutSec 2}catch{$taskRunning=$null}
if($taskRunning){throw 'A server is running on the selected port. Save and exit private games, then stop that server before initializing/upgrading this package.'}
$taskWorkspace=Assert-LocalDirectory $taskConfig.workspace
$taskStateFile=Join-Path $taskWorkspace 'state.json'
if($SourceWorkspace){
    if(Test-Path -LiteralPath $taskWorkspace){throw 'Import requires a new destination workspace; existing data is never overwritten.'}
    $taskSource=Assert-LocalDirectory $SourceWorkspace
    if(-not (Test-Path -LiteralPath (Join-Path $taskSource 'state.json'))){throw 'The supplied workspace has no state.json.'}
    # A short exclusive lease makes this an atomic snapshot of the source's save.
    $taskLease=[IO.File]::Open((Join-Path $taskSource 'workspace.lock'),'OpenOrCreate','ReadWrite','None')
    try{
        New-Item -ItemType Directory -Path $taskWorkspace -Force|Out-Null
        [IO.File]::Copy((Join-Path $taskSource 'state.json'),$taskStateFile,$false)
        foreach($taskName in @('native-resources','native-journal','galaxy.json')){
            $taskFrom=Join-Path $taskSource $taskName
            if(Test-Path -LiteralPath $taskFrom){
                if((Get-Item -LiteralPath $taskFrom).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Imported workspace entries must not be filesystem links.'}
                if(@(Get-ChildItem -LiteralPath $taskFrom -Recurse -Force -ErrorAction Stop|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Imported workspace entries must not contain filesystem links.'}
                Copy-Item -LiteralPath $taskFrom -Destination (Join-Path $taskWorkspace $taskName) -Recurse
            }
        }
    }finally{$taskLease.Dispose()}
}
New-Item -ItemType Directory -Path $taskWorkspace -Force|Out-Null
$taskState=if(Test-Path -LiteralPath $taskStateFile){Get-Content -LiteralPath $taskStateFile -Raw|ConvertFrom-Json -AsHashtable}else{$null}
$taskDataDeployment=Join-Path $taskData 'deployment.json'
$taskPrior=if(Test-Path -LiteralPath $taskDataDeployment){Get-Content -LiteralPath $taskDataDeployment -Raw|ConvertFrom-Json -AsHashtable}else{$null}
if(-not $Account -and $taskState){
    if($taskPrior -and $taskPrior.account -and $taskState.accounts.Contains($taskPrior.account)){$Account=$taskPrior.account}
    if(-not $Account){$Account=@($taskState.accounts.GetEnumerator()|Where-Object {$_.Value.nativeSave -and -not $_.Value.disabled}|Select-Object -First 1 -ExpandProperty Key)[0]}
}
if($Account -and ($Account -notmatch '^[a-f0-9]{32}$' -or -not $taskState -or -not $taskState.accounts.Contains($Account) -or -not $taskState.accounts[$Account].nativeSave -or $taskState.accounts[$Account].disabled)){throw 'Select an enabled private account with a native save in this workspace.'}
$taskGeneration='release-'+$taskRelease.version+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$taskBackup=Join-Path $taskData ('deployment-backups/'+$taskGeneration)
$taskBackup=Assert-LocalDirectory $taskBackup
New-Item -ItemType Directory -Path $taskBackup -Force|Out-Null
foreach($taskFile in @($taskConfigFile,$taskDataDeployment,(Join-Path $taskWorkspace 'native-business/active.json'))){
    if(Test-Path -LiteralPath $taskFile){Copy-Item -LiteralPath $taskFile -Destination (Join-Path $taskBackup ([IO.Path]::GetFileName($taskFile)))}
}
$taskBusinessRoot=Join-Path $taskWorkspace 'native-business'
$taskBusinessRoot=Assert-LocalDirectory $taskBusinessRoot
New-Item -ItemType Directory -Path $taskBusinessRoot -Force|Out-Null
$taskBusinessFile='read-'+$taskGeneration+'.dll'
Copy-Item -LiteralPath (Join-Path $taskRoot 'business/XFEEliteDangerous.ReadBusiness.dll') -Destination (Join-Path $taskBusinessRoot $taskBusinessFile)
$taskBusiness=@{schema=1;abi=1;rpcAbi=1;saveAbi=1;rpcMethods=@(2,9,17,32,89);generation=$taskGeneration;file=$taskBusinessFile;
    sha256=$taskRelease.businessModuleSha256;gameSha256=$taskRelease.gameSha256;
    operations=@(Get-Content -LiteralPath (Join-Path $taskRoot 'business/operations.json') -Raw|ConvertFrom-Json)}
function Write-ReleaseJson([string]$Path,$Value){
    $temporary=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    [IO.File]::WriteAllText($temporary,($Value|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary,$Path,$true)
}
Write-ReleaseJson (Join-Path $taskBusinessRoot 'active.json') $taskBusiness
$taskConfig.dataRoot=$taskData;$taskConfig.workspace=$taskWorkspace;$taskConfig.background=$true;$taskConfig.artifactsRoot=Join-Path $taskRoot 'artifacts'
$taskManifest=@{schema=1;createdUtc=[DateTimeOffset]::UtcNow;release=$taskRoot;serverExe=(Join-Path $taskRoot 'server/XFEEliteDangerous.Server.exe');
    adminExe=(Join-Path $taskRoot 'admin/XFEEliteDangerous.Desktop.exe');bootstrapperExe=(Join-Path $taskRoot 'bootstrapper/XFEEliteDangerous.Bootstrapper.exe');
    nativeRuntime=(Join-Path $taskRoot 'native/XFEEliteDangerous.OfflineRuntime.dll');configuration=$taskConfigFile;dataRoot=$taskData;workspace=$taskWorkspace;
    account=$Account;address=$taskAddress;workingDirectory=$taskRoot;reports=(Join-Path $taskRoot 'artifacts');gameProfile=(Join-Path $taskData 'game-client.json')}
$taskRuntimeDirectory=Assert-LocalDirectory (Join-Path $taskRoot 'runtime')
New-Item -ItemType Directory -Path $taskRuntimeDirectory -Force|Out-Null
Write-ReleaseJson $taskConfigFile $taskConfig
Write-ReleaseJson $taskDataDeployment $taskManifest
Write-ReleaseJson (Join-Path $taskRoot 'runtime/deployment.json') $taskManifest
Write-Output ('Initialized '+$taskRelease.version+'. Start-Admin.cmd opens the management app. Data: '+$taskData)
if(-not $Account){Write-Output 'No playable native commander is bundled. Create/import your own commander and native reference data in the management app; use its Play button or pass -Account to the game scripts.'}
