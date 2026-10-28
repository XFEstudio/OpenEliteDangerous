param(
    [Parameter(Mandatory)][string]$LaunchReceipt,
    [Parameter(Mandatory)][string]$ServerDataRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$VerifyReload,
    [string]$Expectations
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskArtifacts=[IO.Path]::GetFullPath((Join-Path $taskRoot artifacts))+[IO.Path]::DirectorySeparatorChar
foreach($taskPath in @($LaunchReceipt,$ServerDataRoot,$OutputDirectory)){
    if(-not [IO.Path]::GetFullPath($taskPath).StartsWith($taskArtifacts,[StringComparison]::OrdinalIgnoreCase)){throw 'Advanced refit verification requires isolated artifact paths.'}
}
$taskLaunch=Get-Content -LiteralPath $LaunchReceipt -Raw|ConvertFrom-Json
if($taskLaunch.mode -ne 'in-process-offline' -or -not $taskLaunch.zeroNetworkCapabilities -or $taskLaunch.imageSha256 -ne 'e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988' -or
    -not $taskLaunch.workspace.StartsWith($taskArtifacts,[StringComparison]::OrdinalIgnoreCase)){throw 'Pinned owned isolated E-game required.'}
if((Get-Process -Id $taskLaunch.ProcessId).Path -ne 'E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe'){throw 'Owned process identity differs.'}
$taskAccess=Get-Content -LiteralPath (Join-Path $ServerDataRoot 'admin-access.json') -Raw|ConvertFrom-Json
$taskHealth=Invoke-RestMethod -Uri ($taskAccess.address+'health') -TimeoutSec 3
if($taskHealth.workspace -ne $taskLaunch.workspace){throw 'Native game and QA server workspaces differ.'}
$taskServer=(Get-CimInstance Win32_Process -Filter ('ProcessId='+$taskHealth.pid)).ExecutablePath
if(-not $taskServer.StartsWith($taskArtifacts,[StringComparison]::OrdinalIgnoreCase)){throw 'QA executable required.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$taskToken=$taskAccess.key
$taskChecks=[Collections.Generic.List[object]]::new()
$taskPassed=$false;$taskFailure=$null
function Invoke-AdvancedApi([string]$Operation,[hashtable]$Arguments=@{}){
    $taskBody=@{operation=$Operation;arguments=$Arguments}|ConvertTo-Json -Depth 40 -Compress
    Invoke-RestMethod -Uri ($taskAccess.address+'control') -Method Post -Headers @{Authorization=('Bearer '+$taskToken);'Accept-Language'='en-US'} -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($taskBody)) -TimeoutSec 25
}
function Assert-Advanced([string]$Name,[bool]$Condition){
    $taskChecks.Add(@{name=$Name;passed=$Condition})
    if(-not $Condition){throw ('Advanced refit check failed: '+$Name)}
}
function Read-AdvancedStatus {
    Get-Content -LiteralPath (Join-Path $taskLaunch.workspace ('live-refit/'+$taskLaunch.accountId+'/status.json')) -Raw|ConvertFrom-Json
}
function Read-AdvancedSave {Invoke-AdvancedApi 'native/export' @{accountId=$taskLaunch.accountId}}
function Read-AdvancedModule([string]$Slot){
    $taskState=Read-AdvancedStatus
    $taskState.slots|Where-Object {$_.slot -eq $Slot}|Select-Object -First 1
}
function Assert-Ammo([string]$Name,$Module,[uint32]$Clip,[uint32]$Hopper){Assert-Advanced $Name ($null -ne $Module -and $Module.ammo.clip -eq $Clip -and $Module.ammo.hopper -eq $Hopper)}
function Send-Advanced([hashtable]$Change){
    $taskCommander=Invoke-AdvancedApi 'commander' @{accountId=$taskLaunch.accountId}
    $Change.accountId=$taskLaunch.accountId;$Change.shipId=(Read-AdvancedStatus).shipId;$Change.version=$taskCommander.version
    $taskReply=Invoke-AdvancedApi 'fleet/refit' $Change
    Assert-Advanced ($Change.action+' returned a confirmed native save') ($taskReply.saved -and $taskReply.appliedInGame -and -not $taskReply.requiresReload)
    $taskResult=(Read-AdvancedStatus).result
    Assert-Advanced ($Change.action+' native acknowledgement nonce agrees') ($taskResult.commandId -eq $taskReply.commandId -and $taskResult.applied)
    return $taskReply
}
function Assert-SavedAmmo([string]$Name,[string]$Slot,[uint32]$Clip,[uint32]$Hopper){
    $taskSaved=Read-AdvancedSave
    $taskModule=($taskSaved.snapshot.vessels|Where-Object {$_.id -eq $taskSaved.snapshot.currentVesselId}).modules|Where-Object {$_.name -eq $Slot}|Select-Object -ExpandProperty module
    Assert-Ammo $Name $taskModule $Clip $Hopper
}
try{
    $taskOwner=Invoke-AdvancedApi 'auth/local-owner';$taskToken=$taskOwner.token
    $taskState=Read-AdvancedStatus
    Assert-Advanced 'Owned original game is in the world' ($taskState.processId -eq $taskLaunch.ProcessId -and $taskState.flow -eq 'InGame' -and [DateTimeOffset]::Parse($taskState.observedUtc) -ge [DateTimeOffset]::UtcNow.AddSeconds(-5))
    if($VerifyReload){
        if(-not $Expectations -or -not [IO.Path]::GetFullPath($Expectations).StartsWith($taskArtifacts,[StringComparison]::OrdinalIgnoreCase)){throw 'Owned expectation file required.'}
        $taskExpected=Get-Content -LiteralPath $Expectations -Raw|ConvertFrom-Json
        $taskGun=Read-AdvancedModule $taskExpected.weaponSlot;$taskFsd=Read-AdvancedModule FrameShiftDrive
        Assert-Advanced 'Reload kept the same native process' ($taskExpected.processId -eq $taskState.processId)
        Assert-Advanced 'Reload reconstructed the previously unseen weapon' ($taskGun.module.name -eq 'Hpt_MultiCannon_Fixed_Small')
        Assert-Ammo 'Reload restored magazine and reserve counts' $taskGun.module 23 321
        Assert-Advanced 'Reload restored weapon blueprint and experimental effect' ($taskGun.module.engineering.recipeId -eq $taskExpected.weaponEngineering.recipeId -and @($taskGun.module.engineering.modifiers|Where-Object {$_.name -eq 'special_corrosive_shell'}).Count -eq 1)
        Assert-Advanced 'Reload restored actual engineered magazine capacity' ($taskGun.ammoCapacity.clip -eq $taskExpected.weaponCapacity.clip -and $taskGun.ammoCapacity.clip -gt 100)
        Assert-Advanced 'Reload restored FSD blueprint grade and engineer identity' ($taskFsd.module.engineering.recipeName -eq 'FSD_LongRange' -and $taskFsd.module.engineering.level -eq 5 -and $taskFsd.module.engineering.engineerId -eq $taskExpected.fsdEngineering.engineerId)
        Assert-Advanced 'Reload restored FSD physical maximum integrity' ([Math]::Abs($taskFsd.maximumHealth-$taskExpected.fsdMaximumHealth) -lt .003)
        Assert-SavedAmmo 'Server save still contains the restored ammunition' $taskExpected.weaponSlot 23 321
    }else{
        $taskSlot='SmallHardpoint2';$taskBefore=Read-AdvancedModule $taskSlot;$taskFsdBefore=Read-AdvancedModule FrameShiftDrive
        Assert-Advanced 'Unseen-weapon scenario starts with a different module' ($taskBefore.module.name -ne 'Hpt_MultiCannon_Fixed_Small')
        Assert-Advanced 'Native resource library supports on-demand recipes' ($null -eq $taskState.availableModules)
        Send-Advanced @{action='module';slot=$taskSlot;module='Hpt_MultiCannon_Fixed_Small'}|Out-Null
        $taskGun=Read-AdvancedModule $taskSlot
        Assert-Advanced 'The original game installed the multicannon and exposes ammunition' ($taskGun.module.name -eq 'Hpt_MultiCannon_Fixed_Small' -and $taskGun.ammoEditable)
        Assert-Advanced 'Native stock magazine capacity is 100 rounds' ($taskGun.ammoCapacity.clip -eq 100)
        Send-Advanced @{action='ammunition';slot=$taskSlot;clip=23;hopper=321}|Out-Null
        Assert-Ammo 'Native current rounds are distinct from capacity' (Read-AdvancedModule $taskSlot).module 23 321
        Assert-SavedAmmo 'Current rounds are committed to the server save' $taskSlot 23 321
        Send-Advanced @{action='ammunition';slot=$taskSlot;clip=77;hopper=999}|Out-Null
        Assert-Ammo 'Native ammunition can be replenished upward' (Read-AdvancedModule $taskSlot).module 77 999
        Send-Advanced @{action='ammunition';slot=$taskSlot;clip=23;hopper=321}|Out-Null
        $taskCommander=Invoke-AdvancedApi 'commander' @{accountId=$taskLaunch.accountId};$taskRejected=$false
        try{Invoke-AdvancedApi 'fleet/refit' @{accountId=$taskLaunch.accountId;shipId=$taskState.shipId;slot=$taskSlot;action='ammunition';clip=101;hopper=321;version=$taskCommander.version}|Out-Null}
        catch{if([int]$_.Exception.Response.StatusCode -ge 400 -and [int]$_.Exception.Response.StatusCode -lt 500){$taskRejected=$true}else{throw}}
        Assert-Advanced 'Oversized ammunition is rejected with verified native recovery' ($taskRejected -and (Read-AdvancedStatus).result.restored)
        Assert-Ammo 'Recovery retains the original current rounds' (Read-AdvancedModule $taskSlot).module 23 321
        $taskFleet=Invoke-AdvancedApi 'fleet' @{accountId=$taskLaunch.accountId}
        $taskShip=$taskFleet.ships|Where-Object {$_.id -eq $taskState.shipId}
        $taskGunSlot=$taskShip.slots|Where-Object {$_.name -eq $taskSlot}
        $taskBlueprint=$taskGunSlot.engineeringBlueprints|Where-Object {$_.state.recipeName -eq 'Weapon_HighCapacity'}|Sort-Object {$_.state.level} -Descending|Select-Object -First 1
        Assert-Advanced 'Captured compatible multicannon engineering preset is available' ($null -ne $taskBlueprint)
        Send-Advanced @{action='engineering';slot=$taskSlot;blueprintKey=$taskBlueprint.key;experimental='special_corrosive_shell'}|Out-Null
        $taskGun=Read-AdvancedModule $taskSlot
        Assert-Advanced 'Engineering changes actual native magazine capacity' ($taskGun.ammoCapacity.clip -gt 100)
        Assert-Ammo 'Engineering retains current ammunition' $taskGun.module 23 321
        Assert-Advanced 'Native experimental effect is present' (@($taskGun.module.engineering.modifiers|Where-Object {$_.name -eq 'special_corrosive_shell' -and $_.type -eq 3}).Count -eq 1)
        Send-Advanced @{action='store';slot=$taskSlot}|Out-Null
        Assert-Advanced 'Native slot is empty after storage' ($null -eq (Read-AdvancedModule $taskSlot).module)
        $taskSave=Read-AdvancedSave;$taskStored=$taskSave.storedModules|Where-Object {$_.module.name -eq 'Hpt_MultiCannon_Fixed_Small'}|Sort-Object slotId -Descending|Select-Object -First 1
        Assert-Ammo 'Warehouse retains actual magazine and reserve counts' $taskStored.module 23 321
        Assert-Advanced 'Warehouse retains the blueprint' ($taskStored.module.engineering.recipeId -eq $taskGun.module.engineering.recipeId)
        Send-Advanced @{action='retrieve';slot=$taskSlot;storageId=$taskStored.slotId}|Out-Null
        $taskGun=Read-AdvancedModule $taskSlot
        Assert-Ammo 'Retrieval restores live ammunition' $taskGun.module 23 321
        Assert-Advanced 'Retrieval restores the physical engineered capacity' ($taskGun.ammoCapacity.clip -gt 100)
        $taskFsdSlot=$taskShip.slots|Where-Object {$_.name -eq 'FrameShiftDrive'}
        $taskFsdBlueprint=$taskFsdSlot.engineeringBlueprints|Where-Object {$_.state.recipeName -eq 'FSD_LongRange' -and $_.state.level -eq 5}|Select-Object -First 1
        Assert-Advanced 'Captured compatible G5 FSD blueprint is available' ($null -ne $taskFsdBlueprint)
        Send-Advanced @{action='engineering';slot='FrameShiftDrive';blueprintKey=$taskFsdBlueprint.key;experimental='special_fsd_heavy'}|Out-Null
        $taskFsd=Read-AdvancedModule FrameShiftDrive
        Assert-Advanced 'FSD engineering changes physical maximum integrity' ($taskFsd.maximumHealth -lt $taskFsdBefore.maximumHealth -and $taskFsd.maximumHealth -gt 0)
        Assert-Advanced 'Native blueprint ID and engineer ID retain their distinct fields' ($taskFsd.module.engineering.recipeId -eq $taskFsdBlueprint.state.recipeId -and $taskFsd.module.engineering.engineerId -eq $taskFsdBlueprint.state.engineerId)
        Send-Advanced @{action='engineering';slot='FrameShiftDrive';blueprintKey=$taskFsdBlueprint.key;experimental='special_fsd_heavy';values=@{mod_health=-0.05}}|Out-Null
        $taskCustom=Read-AdvancedModule FrameShiftDrive
        Assert-Advanced 'Custom engineering parameters change physical native integrity' ($taskCustom.maximumHealth -gt $taskFsd.maximumHealth -and [Math]::Abs(($taskCustom.module.engineering.modifiers|Where-Object {$_.name -eq 'mod_health'}).value+0.05) -lt .00001)
        Send-Advanced @{action='engineering';slot='FrameShiftDrive';remove=$true}|Out-Null
        $taskPlain=Read-AdvancedModule FrameShiftDrive
        Assert-Advanced 'Removing engineering restores physical native integrity' ($null -eq $taskPlain.module.engineering -and [Math]::Abs($taskPlain.maximumHealth-$taskFsdBefore.maximumHealth) -lt .003)
        Send-Advanced @{action='engineering';slot='FrameShiftDrive';blueprintKey=$taskFsdBlueprint.key;experimental='special_fsd_heavy'}|Out-Null
        $taskFsd=Read-AdvancedModule FrameShiftDrive
        @{processId=$taskState.processId;weaponSlot=$taskSlot;weaponEngineering=$taskGun.module.engineering;weaponCapacity=$taskGun.ammoCapacity;fsdEngineering=$taskFsd.module.engineering;fsdMaximumHealth=$taskFsd.maximumHealth}|ConvertTo-Json -Depth 20|Set-Content -LiteralPath (Join-Path $OutputDirectory expectations.json) -Encoding utf8NoBOM
    }
    Assert-Advanced 'No game restart was used during this suite' ($taskState.processId -eq (Read-AdvancedStatus).processId)
    $taskPassed=$true
}catch{$taskFailure=$_.Exception.Message;throw}
finally{
    $taskAssemblies=@{};foreach($taskName in @('XFEEliteDangerous.Core.dll','XFEEliteDangerous.Server.dll','XFEEliteDangerous.NativeFormat.dll','XFEEliteDangerous.Client.dll')){$taskAssemblies[$taskName]=(Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $taskServer) $taskName)).Hash}
    $taskBusiness=Get-Content -LiteralPath (Join-Path $taskLaunch.workspace native-business/active.json) -Raw|ConvertFrom-Json
    @{schema=1;passed=$taskPassed;nativeClientValidated=$taskPassed;nativeClientUsed=$true;desktopInputUsed=$false;online=[bool]$taskLaunch.online;reload=[bool]$VerifyReload;processId=$taskLaunch.ProcessId;checks=@($taskChecks);failure=$taskFailure;
        nativeRuntimeSha256=(Get-FileHash -LiteralPath $taskLaunch.ownedNativeRuntime).Hash;serverAssemblies=$taskAssemblies;businessModuleSha256=$taskBusiness.sha256;businessGeneration=$taskBusiness.generation;
        scope='Pinned Odyssey private fixture; native resource loading, engineering, experimental effect, current ammunition and persistence.'}|ConvertTo-Json -Depth 30|Set-Content -LiteralPath (Join-Path $OutputDirectory validation.json) -Encoding utf8NoBOM
}
Write-Output ('PASS '+$taskChecks.Count+' advanced native refit checks; online='+[bool]$taskLaunch.online+'; reload='+[bool]$VerifyReload)
