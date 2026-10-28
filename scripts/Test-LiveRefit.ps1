param(
    [Parameter(Mandatory)][string]$LaunchReceipt,
    [Parameter(Mandatory)][string]$ServerDataRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$VerifyReload,
    [switch]$ModuleReplacement
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskArtifacts=[IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts'))+[IO.Path]::DirectorySeparatorChar
foreach($taskPath in @($LaunchReceipt,$ServerDataRoot,$OutputDirectory)){
    if(-not [IO.Path]::GetFullPath($taskPath).StartsWith($taskArtifacts,[StringComparison]::OrdinalIgnoreCase)){throw 'Live refit tests require isolated artifact paths.'}
}
$taskLaunch=Get-Content -LiteralPath $LaunchReceipt -Raw | ConvertFrom-Json
if($taskLaunch.mode -ne 'in-process-offline' -or -not $taskLaunch.zeroNetworkCapabilities -or $taskLaunch.imageSha256 -ne 'e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988') {throw 'Owned isolated pinned E-game receipt required.'}
if(-not [IO.Path]::GetFullPath($taskLaunch.workspace).StartsWith($taskArtifacts,[StringComparison]::OrdinalIgnoreCase)){throw 'Test saves must be inside artifacts.'}
$taskProcess=Get-Process -Id $taskLaunch.ProcessId
if($taskProcess.Path -ne 'E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe'){throw 'Unexpected game process.'}
$taskAccess=Get-Content -LiteralPath (Join-Path $ServerDataRoot 'admin-access.json') -Raw | ConvertFrom-Json
$taskHealth=Invoke-RestMethod -Uri ($taskAccess.address+'health') -TimeoutSec 3
if($taskHealth.workspace -ne $taskLaunch.workspace){throw 'Test server and game workspaces differ.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$taskToken=$taskAccess.key
$taskChecks=[Collections.Generic.List[object]]::new()
function Invoke-RefitApi([string]$Operation,[hashtable]$Arguments=@{}){
    $taskBody=@{operation=$Operation;arguments=$Arguments} | ConvertTo-Json -Depth 32 -Compress
    Invoke-RestMethod -Uri ($taskAccess.address+'control') -Method Post -Headers @{Authorization=('Bearer '+$taskToken);'Accept-Language'='en-US'} -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($taskBody)) -TimeoutSec 20
}
function Test-RefitCondition([string]$Name,[bool]$Passed){
    $taskChecks.Add(@{name=$Name;passed=$Passed})
    if(-not $Passed){throw ('Live refit verification failed: '+$Name)}
}
function Read-RefitStatus {
    Get-Content -LiteralPath (Join-Path $taskLaunch.workspace ('live-refit/'+$taskLaunch.accountId+'/status.json')) -Raw | ConvertFrom-Json
}
function Read-RefitSave {Invoke-RefitApi 'native/export' @{accountId=$taskLaunch.accountId}}
function Send-Refit([hashtable]$Change){
    $taskCommander=Invoke-RefitApi 'commander' @{accountId=$taskLaunch.accountId}
    $Change.accountId=$taskLaunch.accountId;$Change.version=$taskCommander.version
    $taskTimer=[Diagnostics.Stopwatch]::StartNew()
    $taskReply=Invoke-RefitApi 'fleet/refit' $Change
    $taskTimer.Stop()
    Test-RefitCondition ($Change.action+' acknowledged by game and saved') ($taskReply.appliedInGame -and $taskReply.saved -and -not $taskReply.requiresReload)
    $taskStatus=Read-RefitStatus
    Test-RefitCondition ($Change.action+' result nonce matches request') ($taskStatus.result.commandId -eq $taskReply.commandId -and $taskStatus.result.applied)
    $taskChecks.Add(@{name=($Change.action+' latency');milliseconds=$taskTimer.ElapsedMilliseconds;passed=$true})
    return $taskReply
}
$taskOwner=Invoke-RefitApi 'auth/local-owner'
$taskToken=$taskOwner.token
try{
    $taskStatus=Read-RefitStatus
    Test-RefitCondition 'native game is InGame' ($taskStatus.flow -eq 'InGame' -and $taskStatus.processId -eq $taskLaunch.ProcessId)
    $taskShip=$taskStatus.shipId
    $taskSlot='SmallHardpoint1'
    $taskModule=($taskStatus.slots | Where-Object slot -eq $taskSlot).module
    if($VerifyReload){
        Test-RefitCondition 'reloaded native enabled state' ($taskModule.on -eq $false)
        Test-RefitCondition 'reloaded native priority' ($taskModule.priority -eq 3)
        Test-RefitCondition 'reloaded native integrity' ([Math]::Abs([long]$taskModule.healthMillion-910000) -le 2)
    }else{
        $taskOriginal=Read-RefitSave
        $taskOriginal | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'before-save.json') -Encoding utf8NoBOM
        $null=Send-Refit @{action='properties';shipId=$taskShip;slot=$taskSlot;enabled=$true;priority=0;health=1.0}
        $null=Send-Refit @{action='properties';shipId=$taskShip;slot=$taskSlot;enabled=$false;priority=3;health=.91}
        $taskAfter=Read-RefitSave
        $taskSavedModule=(($taskAfter.snapshot.vessels | Where-Object id -eq $taskShip).modules | Where-Object name -eq $taskSlot).module
        Test-RefitCondition 'confirmed properties reach native save' (-not $taskSavedModule.on -and $taskSavedModule.priority -eq 3 -and $taskSavedModule.healthMillion -eq 910000)
        $taskStoredBefore=@($taskAfter.storedModules).Count
        $null=Send-Refit @{action='store';shipId=$taskShip;slot=$taskSlot}
        $taskAfter=Read-RefitSave
        Test-RefitCondition 'weapon removed from native slot' ($null -eq (($taskAfter.snapshot.vessels | Where-Object id -eq $taskShip).modules | Where-Object name -eq $taskSlot).module)
        Test-RefitCondition 'removed weapon retained in warehouse' (@($taskAfter.storedModules).Count -eq $taskStoredBefore+1)
        $taskStored=$taskAfter.storedModules | Sort-Object slotId | Select-Object -Last 1
        Test-RefitCondition 'warehouse retains weapon condition' ($taskStored.module.healthMillion -eq 910000 -and $taskStored.module.priority -eq 3 -and -not $taskStored.module.on)
        $null=Send-Refit @{action='retrieve';shipId=$taskShip;slot=$taskSlot;storageId=$taskStored.slotId}
        $taskAfter=Read-RefitSave
        Test-RefitCondition 'retrieved weapon removed from warehouse' (@($taskAfter.storedModules).Count -eq $taskStoredBefore)
        $taskCurrent=(Read-RefitStatus).slots | Where-Object slot -eq $taskSlot
        Test-RefitCondition 'retrieved weapon is live with retained state' ($taskCurrent.module.name -eq $taskModule.name -and $taskCurrent.module.priority -eq 3 -and -not $taskCurrent.module.on -and [Math]::Abs([long]$taskCurrent.module.healthMillion-910000) -le 2)
        if($ModuleReplacement){
            $taskBeforeCapacity=(Read-RefitStatus).cargoCapacity
            $taskShield=((Read-RefitStatus).slots | Where-Object slot -eq 'Slot01_Size2').module
            if($null -eq $taskBeforeCapacity -or $taskShield.name -ne 'Int_ShieldGenerator_Size2_Class1'){throw 'Replacement test requires the prepared starter fixture.'}
            $null=Send-Refit @{action='module';shipId=$taskShip;slot='Slot01_Size2';module='Int_CargoRack_Size2_Class1'}
            $taskReplacement=Read-RefitStatus
            Test-RefitCondition 'different module family fitted into live slot' (($taskReplacement.slots | Where-Object slot -eq 'Slot01_Size2').module.name -eq 'Int_CargoRack_Size2_Class1')
            Test-RefitCondition 'native cargo capacity increased by fitted rack' ($taskReplacement.cargoCapacity -eq $taskBeforeCapacity+4)
            $taskStoredShield=(Read-RefitSave).storedModules | Where-Object {$_.module.name -eq $taskShield.name} | Sort-Object slotId | Select-Object -Last 1
            $null=Send-Refit @{action='retrieve';shipId=$taskShip;slot='Slot01_Size2';storageId=$taskStoredShield.slotId}
            $taskRestored=Read-RefitStatus
            Test-RefitCondition 'original shield restored from warehouse' (($taskRestored.slots | Where-Object slot -eq 'Slot01_Size2').module.name -eq $taskShield.name)
            Test-RefitCondition 'native cargo capacity restored after rack removal' ($taskRestored.cargoCapacity -eq $taskBeforeCapacity)
        }
    }
    Test-RefitCondition 'same game process remains running' ((Get-Process -Id $taskLaunch.ProcessId).StartTime -eq $taskProcess.StartTime)
    $taskFinal=Read-RefitSave
    $taskFinal | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'after-save.json') -Encoding utf8NoBOM
    $taskServerPath=(Get-Process -Id $taskHealth.pid).Path
    $taskAssemblies=@{}
    foreach($taskName in @('XFEEliteDangerous.Server.dll','XFEEliteDangerous.Core.dll','XFEEliteDangerous.Client.dll','XFEEliteDangerous.NativeFormat.dll')){
        $taskAssemblies[$taskName]=(Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $taskServerPath) $taskName)).Hash
    }
    @{passed=$true;scope='real native E-game, original module setters and original outfitting lifecycle';processId=$taskLaunch.ProcessId;
        launchReceipt=[IO.Path]::GetFullPath($LaunchReceipt);online=[bool]$taskLaunch.online;reload=[bool]$VerifyReload;desktopInputUsed=$false;
        nativeRuntimeSha256=(Get-FileHash -LiteralPath $taskLaunch.ownedNativeRuntime).Hash;serverExecutable=$taskServerPath;serverAssemblies=$taskAssemblies;checks=$taskChecks} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'validation.json') -Encoding utf8NoBOM
    Write-Output ('Live refit checks passed: '+$taskChecks.Count)
}catch{
    @{passed=$false;processId=$taskLaunch.ProcessId;error=$_.Exception.Message;checks=$taskChecks} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'validation.json') -Encoding utf8NoBOM
    throw
}finally{
    try{$null=Invoke-RefitApi 'auth/logout'}catch{}
    $taskToken=$null;$taskAccess=$null
}
