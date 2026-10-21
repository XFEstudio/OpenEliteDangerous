param(
    [Parameter(Mandatory=$true)][string]$RunDirectory,
    [Parameter(Mandatory=$true)][string]$Python,
    [string]$Account='',
    [ValidateSet('all','save-reload')][string]$Scenario='all'
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskRun=[IO.Path]::GetFullPath($RunDirectory)
if(-not $taskRun.StartsWith((Join-Path $taskRoot 'artifacts')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '测试目录必须位于项目 artifacts。'}
$taskReceipt=Get-Content -LiteralPath (Join-Path $taskRun 'launch.json') -Raw | ConvertFrom-Json
if($taskReceipt.mode -ne 'in-process-offline' -or -not $taskReceipt.zeroNetworkCapabilities){throw '需要隔离副本启动记录。'}
$taskLogs=Join-Path $taskRun 'logs'
$taskStatePath=Join-Path $taskReceipt.workspace 'state.json'
function Read-TaskJson([string]$Path){
    $taskStream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]'ReadWrite,Delete')
    try{$taskReader=[IO.StreamReader]::new($taskStream);ConvertFrom-Json -InputObject $taskReader.ReadToEnd() -AsHashtable}
    finally{$taskStream.Dispose()}
}
function Read-TaskLogs {
    foreach($taskFile in (Get-ChildItem -LiteralPath $taskLogs -File -Filter 'offline-runtime.*.jsonl' | Sort-Object Name)){
        $taskStream=[IO.File]::Open($taskFile.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]'ReadWrite,Delete')
        try{$taskReader=[IO.StreamReader]::new($taskStream);while($null -ne ($taskLine=$taskReader.ReadLine())){try{ConvertFrom-Json -InputObject $taskLine -AsHashtable}catch{}}}
        finally{$taskStream.Dispose()}
    }
}
function Get-TaskSnapshot {
    $taskState=Read-TaskJson $taskStatePath
    $taskOwner=$taskState.accounts[$Account]
    if($null -eq $taskOwner -or $null -eq $taskOwner.nativeSave){throw '原生离线账号不存在。'}
    $taskSave=$taskOwner.nativeSave;$taskShip=@($taskSave.snapshot.vessels | Where-Object {$_.id -eq $taskSave.snapshot.currentVesselId})
    if($taskShip.Count -ne 1){throw '活动飞船不唯一。'}
    @{revision=$taskOwner.commander.revision;location=$taskShip[0].location;fuel=$taskShip[0].fuel;ship=$taskShip[0].id}
}
function Get-TaskDistance($First,$Second){
    if($First.address -ne $Second.address){throw '测试期间发生了未经测试的星系切换。'}
    $taskSquared=0.0
    foreach($taskAxis in @('x','y','z')){$taskDelta=[double]$Second.position[$taskAxis]-[double]$First.position[$taskAxis];$taskSquared+=$taskDelta*$taskDelta}
    [Math]::Sqrt($taskSquared)
}
$taskState=Read-TaskJson $taskStatePath
if($Account -eq ''){
    $taskOwners=@($taskState.accounts.GetEnumerator() | Where-Object {-not $_.Value.disabled -and $null -ne $_.Value.nativeSave})
    if($taskOwners.Count -ne 1){throw '多个本地账号时必须指定 -Account。'}
    $Account=$taskOwners[0].Key
}
$taskRecords=@(Read-TaskLogs)
$taskFlow=$taskRecords | Where-Object {$_.component -eq 'native.flow' -and $_.event -eq 'FLOW'} | Select-Object -Last 1
if($taskFlow.fields.argument1 -ne 'InGame' -or $taskFlow.fields.argument2 -ne 'InGame'){throw '先运行 Test-NativeSolo.ps1，并等待稳定 InGame。'}
$taskStarted=[DateTimeOffset]::UtcNow;$taskBaselineSequence=[long]($taskRecords | Select-Object -Last 1).sequence
$taskId=[Guid]::NewGuid().ToString('N').Substring(0,12)
$taskReportPath=Join-Path $taskRun ('native-gameplay-test-'+$taskId+'.json')
$taskFlightLog=Join-Path $taskRun ('native-flight-'+$taskId+'.jsonl')
$taskExitLog=Join-Path $taskRun ('native-exit-'+$taskId+'.jsonl')
$taskBefore=Get-TaskSnapshot
$taskReport=@{schema=1;scenario=$Scenario;pid=$taskReceipt.ProcessId;startedUtc=$taskStarted;outcome='running';nativeControlIssued=$false;
    flightControlVerified=$false;saveExitVerified=$false;saveReloadVerified=$false;programmaticGameplayVerified=$false;nativeSoloPlayableVerified=$false;
    visualAcceptance='pending-user';before=$taskBefore;flightLog=$taskFlightLog;exitLog=$taskExitLog;failures=[Collections.Generic.List[string]]::new()}
try{
    if($Scenario -eq 'all'){
        Write-Output '开始原生油门保持、刹车和暂停测试。'
        & $Python (Join-Path $PSScriptRoot 'capture/inspect_offline_controls.py') --launch (Join-Path $taskRun 'launch.json') --out $taskFlightLog --seconds 40 --action flight-axis-and-pause
        $taskFlightResult=$LASTEXITCODE
        $taskFlightRecords=@(Get-Content -LiteralPath $taskFlightLog | ForEach-Object {ConvertFrom-Json -InputObject $_ -AsHashtable})
        $taskReport.nativeControlIssued=@($taskFlightRecords | Where-Object {$_.kind -eq 'native-axis-hold' -and $_.applied -eq 0.25}).Count -ge 2
        $taskReport.inputInjection='bounded-internal-forward-axis-restored-after-each-original-evaluation'
        if($taskFlightResult -ne 0){$taskReport.failures.Add('原生飞行与暂停阶段未完成。')}
    }
    Write-Output '通过原生保存退出流程返回主菜单。'
    & $Python (Join-Path $PSScriptRoot 'capture/inspect_offline_controls.py') --launch (Join-Path $taskRun 'launch.json') --out $taskExitLog --seconds 60 --action save-and-exit
    if($LASTEXITCODE -ne 0){throw '原生保存退出未返回主菜单。'}
    $taskSaved=Get-TaskSnapshot;$taskReport.saved=$taskSaved
    $taskNewRecords=@(Read-TaskLogs | Where-Object {$_.sequence -gt $taskBaselineSequence})
    $taskCommits=@($taskNewRecords | Where-Object {$_.event -eq 'save.commit'})
    if($taskCommits.Count -eq 0){throw '退出阶段没有真实 StateStore 提交。'}
    $taskReport.saveExitVerified=$true;$taskReport.saveCommitEvidence=$taskCommits
    $taskMoved=Get-TaskDistance $taskBefore.location $taskSaved.location
    $taskReport.flightDistanceMetres=$taskMoved
    $taskReport.flightControlVerified=$taskReport.nativeControlIssued -and $taskMoved -gt 0.5
    if($Scenario -eq 'all' -and -not $taskReport.flightControlVerified){$taskReport.failures.Add('油门测试未产生超过 0.5 米的已保存位置变化。')}
    Write-Output ('保存的位置变化：'+$taskMoved.ToString('F3',[Globalization.CultureInfo]::InvariantCulture)+' 米；正在重载 SOLO。')
    $taskReloadStarted=[DateTimeOffset]::UtcNow
    & (Join-Path $PSScriptRoot 'Test-NativeSolo.ps1') -RunDirectory $taskRun -TimeoutSeconds 180 -Attempts 1 -HoldSeconds 15
    if($LASTEXITCODE -ne 0){throw '同一进程重新进入 SOLO 失败。'}
    $taskEntryReport=Get-ChildItem -LiteralPath $taskRun -Filter 'native-solo-test-*.json' -File | Where-Object {$_.LastWriteTimeUtc -ge $taskReloadStarted.UtcDateTime} | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if($null -eq $taskEntryReport){throw '缺少重载验收记录。'}
    $taskEntry=Read-TaskJson $taskEntryReport.FullName
    if($taskEntry.pid -ne $taskReceipt.ProcessId -or -not $taskEntry.checkpoints.fullSaveParsed -or -not $taskEntry.checkpoints.stableInGame){throw '重载缺少原生解析或稳定世界证据。'}
    $taskReloaded=Get-TaskSnapshot;$taskReloadDistance=Get-TaskDistance $taskSaved.location $taskReloaded.location
    $taskReport.reloaded=$taskReloaded;$taskReport.reloadPositionDifferenceMetres=$taskReloadDistance;$taskReport.reloadEntryReport=$taskEntryReport.FullName
    $taskReport.saveReloadVerified=$taskReloadDistance -le 1 -and $taskSaved.ship -eq $taskReloaded.ship
    if(-not $taskReport.saveReloadVerified){$taskReport.failures.Add('原生重载位置与退出时存档的差异超过 1 米。')}
    $taskReport.programmaticGameplayVerified=$taskReport.flightControlVerified -and $taskReport.saveExitVerified -and $taskReport.saveReloadVerified -and $taskReport.failures.Count -eq 0
    # This script cannot accept the user's visual result or verify physical
    # keyboard input. Keep that separate from synthetic internal input evidence.
    $taskReport.outcome=if($taskReport.programmaticGameplayVerified){'programmatic-gameplay-passed'}elseif($Scenario -eq 'save-reload' -and $taskReport.saveExitVerified -and $taskReport.saveReloadVerified -and $taskReport.failures.Count -eq 0){'native-save-reload-passed'}else{'programmatic-gameplay-failed'}
}
catch{
    $taskReport.failures.Add($_.Exception.Message);$taskReport.outcome='programmatic-gameplay-failed'
    Write-Output $_.Exception.Message
}
finally{
    $taskReport.finishedUtc=[DateTimeOffset]::UtcNow;$taskReport.failures=$taskReport.failures.ToArray()
    [IO.File]::WriteAllText($taskReportPath,($taskReport | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    Write-Output ('原生完整测试：'+$taskReport.outcome+'；记录：'+$taskReportPath)
}
if($taskReport.outcome -in @('programmatic-gameplay-passed','native-save-reload-passed')){exit 0}else{exit 2}
