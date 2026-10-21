param(
    [Parameter(Mandatory=$true)][string]$RunDirectory,
    [ValidateRange(10,540)][int]$TimeoutSeconds=180,
    [ValidateRange(1,3)][int]$Attempts=1,
    [ValidateRange(1,60)][int]$HoldSeconds=15
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskArtifacts=Join-Path $taskRoot 'artifacts'
$taskRun=[IO.Path]::GetFullPath($RunDirectory)
if(-not $taskRun.StartsWith($taskArtifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '测试目录必须位于本项目 artifacts。'}
$taskReceipt=Get-Content -LiteralPath (Join-Path $taskRun 'launch.json') -Raw | ConvertFrom-Json
$taskExpected='E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe'
$taskRuntime=Join-Path $taskRun 'XFEEliteDangerous.OfflineRuntime.dll'
if($taskReceipt.mode -ne 'in-process-offline' -or -not $taskReceipt.zeroNetworkCapabilities -or
    $taskReceipt.imageSha256 -ne 'e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988' -or
    [IO.Path]::GetFullPath($taskReceipt.ownedNativeRuntime) -ne $taskRuntime){throw '启动记录不符合隔离副本约束。'}
foreach($taskPath in @($taskRun,$taskRuntime,$taskExpected)){
    $taskChecked=Get-Item -LiteralPath $taskPath
    while($null -ne $taskChecked){
        if($taskChecked.Attributes -band [IO.FileAttributes]::ReparsePoint){throw '测试路径不能使用文件系统链接。'}
        $taskChecked=$taskChecked.Parent
    }
}
if((Get-FileHash -LiteralPath $taskExpected -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskReceipt.imageSha256){throw '固定游戏版本哈希不符。'}
$taskProcess=Get-Process -Id $taskReceipt.ProcessId
if($taskProcess.Path -ne $taskExpected -or -not @($taskProcess.Modules | Where-Object {$_.FileName -eq $taskRuntime})){throw '目标不是该启动记录拥有的 E 盘进程。'}
$taskLogs=Join-Path $taskRun 'logs'
$taskSupport=Get-Content -LiteralPath (Join-Path $taskLogs 'native-automation.json') -Raw | ConvertFrom-Json
if($taskSupport.schema -ne 1 -or -not $taskSupport.nativeEligibilityRequired){throw '当前运行时不支持此程序化测试命令。'}
function Read-TaskRecords {
    foreach($taskFile in (Get-ChildItem -LiteralPath $taskLogs -Filter 'offline-runtime.*.jsonl' -File | Sort-Object Name)){
        $taskInput=[IO.FileStream]::new($taskFile.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        $taskReader=[IO.StreamReader]::new($taskInput)
        try{while($null -ne ($taskLine=$taskReader.ReadLine())){try{ConvertFrom-Json -InputObject $taskLine -AsHashtable}catch{}}}
        finally{$taskReader.Dispose()}
    }
}
$taskInitialRecords=@(Read-TaskRecords)
$taskBaseline=@($taskInitialRecords | Select-Object -Last 1)
$taskSequence=if($taskBaseline.Count){[long]$taskBaseline[0].sequence}else{0L}
$taskCommandId=[Guid]::NewGuid().ToString('N');$taskPrefix=$taskCommandId.Substring(0,12)
$taskStarted=[DateTimeOffset]::UtcNow
$taskCommand=@{Schema=1;CommandId=$taskCommandId;Action='enter-solo';ExpiresAtUtc=$taskStarted.AddSeconds($TimeoutSeconds+15);Attempts=$Attempts}|ConvertTo-Json
$taskPending=Join-Path $taskLogs ('solo-control-'+$taskCommandId+'.tmp')
[IO.File]::WriteAllText($taskPending,$taskCommand,[Text.UTF8Encoding]::new($false))
[IO.File]::Move($taskPending,(Join-Path $taskLogs 'solo-control.json'),$true)
Write-Output ('已向现有进程提交原生 SOLO 测试 '+$taskPrefix+'；不启动新游戏、不发送桌面输入。')
$taskEvidence=[Collections.Generic.List[object]]::new();$taskDeadline=$taskStarted.AddSeconds($TimeoutSeconds)
$taskMode=@($taskInitialRecords | Where-Object {$_.event -eq 'NATIVE_CONTENT_MODE'} | Select-Object -Last 1)
$taskCheckpoint=@{odysseyMode=($taskMode.Count -gt 0 -and $taskMode[0].fields.argument1 -eq 'Odyssey' -and $taskMode[0].fields.argument2 -eq '1');commandAccepted=$false;soloIssued=$false;fullSaveParsed=$false;locationReady=$false;saveUploadCommitted=$false;inGameFlow=$false;stableInGame=$false}
$taskInGameSince=$null;$taskCurrentFlow='';$taskTargetFlow=''
$taskBlockers='';$taskOutcome='timeout';$taskFailureCount=0
while([DateTimeOffset]::UtcNow -lt $taskDeadline){
    $taskProcess.Refresh();if($taskProcess.HasExited){$taskOutcome='process-exited';break}
    foreach($taskEntry in (Read-TaskRecords | Where-Object {$_.sequence -gt $taskSequence})){
        $taskSequence=[Math]::Max($taskSequence,[long]$taskEntry.sequence)
        if($taskEntry.component -eq 'native.flow'){
            $taskEvent=$taskEntry.event;$taskFields=$taskEntry.fields
            if($taskEvent -eq 'NATIVE_CONTENT_MODE'){$taskCheckpoint.odysseyMode=($taskFields.argument1 -eq 'Odyssey' -and $taskFields.argument2 -eq '1')}
            if($taskEvent -eq 'AUTOMATION_ARMED' -and $taskFields.argument1 -eq $taskPrefix){$taskCheckpoint.commandAccepted=$true}
            if($taskEvent -eq 'AUTOMATION_ACTION' -and $taskFields.argument1 -eq $taskPrefix -and $taskFields.argument2 -eq 'SetupPlaySolo'){$taskCheckpoint.soloIssued=$true}
            if($taskCheckpoint.soloIssued -and $taskEvent -eq 'NATIVE_SAVE_RESULT' -and $taskFields.argument1 -eq '0'){$taskCheckpoint.fullSaveParsed=$true}
            if($taskCheckpoint.soloIssued -and $taskEvent -eq 'FLOW' -and $taskFields.argument1 -eq 'InGame' -and $taskFields.argument2 -eq 'InGame'){$taskCheckpoint.inGameFlow=$true}
            if($taskEvent -eq 'FLOW'){$taskCurrentFlow=$taskFields.argument1;$taskTargetFlow=$taskFields.argument2}
            if($taskCheckpoint.soloIssued -and $taskEvent -eq 'FLOW' -and $taskFields.argument1 -eq 'FailedToConnect'){$taskFailureCount++}
            if($taskEvent -in @('AUTOMATION_ARMED','AUTOMATION_ACTION','AUTOMATION_ACK_DISCONNECT','FLOW','NATIVE_SAVE_RESULT','NATIVE_CONTENT_MODE')){
                $taskEvidence.Add(@{sequence=$taskEntry.sequence;utc=$taskEntry.utc;event=$taskEvent;fields=$taskFields;source=$taskEntry.source})
                Write-Output ($taskEvent+' '+($taskFields.Values -join ' '))
            }
        }
        if($taskCheckpoint.soloIssued -and $taskEntry.event -eq 'save.commit'){
            $taskCheckpoint.saveUploadCommitted=$true
            $taskEvidence.Add(@{sequence=$taskEntry.sequence;utc=$taskEntry.utc;event=$taskEntry.event;fields=$taskEntry.fields;source=$taskEntry.source})
            Write-Output ('SAVE_COMMIT '+$taskEntry.fields.saveCommits)
        }
        if($taskCheckpoint.soloIssued -and $taskEntry.event -eq 'location.dependencies'){
            $taskBlockers=$taskEntry.fields.locationActivationDelay
            if($taskEntry.fields.locationKind -eq 'Location' -and $taskEntry.fields.locationLifecycle -eq '6'){$taskCheckpoint.locationReady=$true}
            $taskEvidence.Add(@{sequence=$taskEntry.sequence;utc=$taskEntry.utc;event=$taskEntry.event;fields=$taskEntry.fields;source=$taskEntry.source})
            Write-Output ('LOCATION '+$taskEntry.fields.locationLifecycle+' '+$taskBlockers)
        }
    }
    if($taskFailureCount -ge $Attempts){$taskOutcome='native-load-failed';break}
    if($taskCheckpoint.odysseyMode -and $taskCheckpoint.inGameFlow -and $taskCheckpoint.locationReady -and $taskCheckpoint.fullSaveParsed -and $taskCheckpoint.saveUploadCommitted -and $taskCurrentFlow -eq 'InGame' -and $taskTargetFlow -eq 'InGame'){
        if($null -eq $taskInGameSince){$taskInGameSince=[DateTimeOffset]::UtcNow}
        if(([DateTimeOffset]::UtcNow-$taskInGameSince).TotalSeconds -ge $HoldSeconds){$taskCheckpoint.stableInGame=$true;$taskOutcome='native-world-entered';break}
    } else {$taskInGameSince=$null}
    Start-Sleep -Milliseconds 1000
}
$taskReport=@{
    schema=1;commandId=$taskPrefix;runDirectory=$taskRun;pid=$taskReceipt.ProcessId;imageSha256=$taskReceipt.imageSha256
    startedUtc=$taskStarted;finishedUtc=[DateTimeOffset]::UtcNow;outcome=$taskOutcome;checkpoints=$taskCheckpoint
    lastActivationDelay=$taskBlockers;evidence=$taskEvidence.ToArray();visualAcceptance='user';stableInGameSeconds=$HoldSeconds
    flightControlVerified=$false;saveReloadVerified=$false;nativeSoloPlayableVerified=$false
}
$taskReportPath=Join-Path $taskRun ('native-solo-test-'+$taskPrefix+'.json')
[IO.File]::WriteAllText($taskReportPath,($taskReport|ConvertTo-Json -Depth 9),[Text.UTF8Encoding]::new($false))
Write-Output ('原生测试结果：'+$taskOutcome+'；记录：'+$taskReportPath)
if($taskOutcome -eq 'native-world-entered'){exit 0}else{exit 2}
