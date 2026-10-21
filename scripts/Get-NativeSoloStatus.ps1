param(
    [Parameter(Mandatory=$true)][string]$RunDirectory,
    [string]$OutputPath
)
$ErrorActionPreference='Stop'
$taskRun=[IO.Path]::GetFullPath($RunDirectory)
function Read-TaskFile([string]$Path) {
    $taskStream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]'ReadWrite,Delete')
    $taskReader=[IO.StreamReader]::new($taskStream)
    try { $taskReader.ReadToEnd() } finally { $taskReader.Dispose() }
}
$taskReceipt=ConvertFrom-Json -InputObject (Read-TaskFile (Join-Path $taskRun 'launch.json')) -AsHashtable
if($taskReceipt.mode -ne 'in-process-offline'){throw '需要进程内离线启动记录。'}
$taskRows=[Collections.Generic.List[object]]::new()
$taskInvalid=0
foreach($taskFile in (Get-ChildItem -LiteralPath (Join-Path $taskRun 'logs') -File -Filter 'offline-runtime.*.jsonl' | Sort-Object Name)) {
    $taskStream=[IO.File]::Open($taskFile.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]'ReadWrite,Delete')
    $taskReader=[IO.StreamReader]::new($taskStream)
    try {
        while($null -ne ($taskLine=$taskReader.ReadLine())) {
            try { $taskRows.Add((ConvertFrom-Json -InputObject $taskLine -AsHashtable)) }
            catch { $taskInvalid++ }
        }
    } finally { $taskReader.Dispose() }
}
if($taskRows.Count -eq 0){throw '没有可读取的离线运行日志。'}
$taskFlow=$taskRows | Where-Object {$_.component -eq 'native.flow' -and $_.event -eq 'FLOW'} | Select-Object -Last 1
$taskModule=$taskRows | Where-Object {$_.event -eq 'module.activated'} | Select-Object -Last 1
$taskContent=$taskRows | Where-Object {$_.event -eq 'NATIVE_CONTENT_MODE'} | Select-Object -Last 1
$taskSave=$taskRows | Where-Object {$_.event -eq 'save.commit'} | Select-Object -Last 1
$taskCompletions=@($taskRows | Where-Object {$_.event -eq 'completion.queued' -and $_.fields.path} |
    Group-Object {$_.fields.method+' '+$_.fields.path} | ForEach-Object {
        $taskLast=$_.Group[-1]
        [ordered]@{operation=$_.Name;status=[int]$taskLast.fields.httpStatus;sequence=$taskLast.sequence;utc=$taskLast.utc;
            traceId=$taskLast.traceId;source=$taskLast.source}
    } | Sort-Object operation)
$taskGameplay=@(foreach($taskFile in (Get-ChildItem -LiteralPath $taskRun -File -Filter 'native-gameplay-test-*.json')) {
    $taskReport=ConvertFrom-Json -InputObject (Read-TaskFile $taskFile.FullName) -AsHashtable
    if($taskReport.pid -eq $taskReceipt.ProcessId) {
        $taskReport['reportFile']=$taskFile.FullName
        $taskReport
    }
})
$taskGameplay=@($taskGameplay | Sort-Object {[DateTimeOffset]$_.finishedUtc})
$taskFull=$taskGameplay | Where-Object {$_.scenario -eq 'all' -and $_.programmaticGameplayVerified -eq $true} | Select-Object -Last 1
$taskLatest=$taskGameplay | Select-Object -Last 1
$taskFailureFlows=@($taskRows | Where-Object {$_.event -eq 'FLOW' -and ($_.fields.argument1 -match 'LoadFailed|FailedToConnect' -or $_.fields.argument2 -match 'LoadFailed|FailedToConnect')})
$taskProblems=@($taskRows | Where-Object {$_.level -in @('warning','error','critical') -and $_.event -notin @('operation.end','data.reply','completion.queued')} |
    Group-Object {$_.component+'|'+$_.event+'|'+$_.message+'|'+$_.fields.path+'|'+$_.source.file+'|'+$_.source.line} |
    ForEach-Object {
        $taskFirst=$_.Group[0];$taskLast=$_.Group[-1]
        [pscustomobject]@{component=$taskLast.component;event=$taskLast.event;message=$taskLast.message;count=$_.Count;
            firstSequence=$taskFirst.sequence;lastSequence=$taskLast.sequence;lastUtc=$taskLast.utc;
            traceId=$taskLast.traceId;source=$taskLast.source;fields=$taskLast.fields;exception=$taskLast.exception}
    } | Sort-Object lastSequence -Descending | Select-Object -First 20)
$taskProcess=Get-Process -Id $taskReceipt.ProcessId -ErrorAction SilentlyContinue
$taskAcceptancePath=Join-Path $taskRun 'user-acceptance.json'
$taskAccepted=$false
if(Test-Path -LiteralPath $taskAcceptancePath){
    $taskAcceptance=ConvertFrom-Json -InputObject (Read-TaskFile $taskAcceptancePath) -AsHashtable
    $taskAccepted=$taskAcceptance.accepted -eq $true -and $taskAcceptance.gameSha256 -eq $taskReceipt.imageSha256 -and $taskAcceptance.runId -eq (Split-Path -Leaf $taskRun)
}
$taskResult=[ordered]@{
    schema=2;generatedUtc=[DateTimeOffset]::UtcNow;gamePid=$taskReceipt.ProcessId;processExists=$null -ne $taskProcess
    runDirectory=$taskRun;lastSequence=$taskRows[-1].sequence;lastLogUtc=$taskRows[-1].utc;unparseableLogLines=$taskInvalid
    latestFlow=$taskFlow.fields;latestContentMode=$taskContent.fields;latestHotModule=$taskModule.fields
    latestSaveCommit=@{sequence=$taskSave.sequence;utc=$taskSave.utc;count=$taskSave.fields.saveCommits}
    nativeLoadFailureEvents=$taskFailureFlows.Count
    programmaticGameplayVerified=$null -ne $taskFull
    gameplayReport=$taskFull.reportFile;flightSavedDistanceMetres=$taskFull.flightDistanceMetres;reloadDifferenceMetres=$taskFull.reloadPositionDifferenceMetres
    latestPersistenceReport=$taskLatest.reportFile;latestPersistenceOutcome=$taskLatest.outcome
    nativeSoloPlayableVerified=$null -ne $taskFull -and $taskAccepted;visualAcceptance=if($taskAccepted){'accepted-user'}else{'pending-user'}
    latestOperationResults=$taskCompletions
    operationsLastObservedUnimplemented=@($taskCompletions | Where-Object {$_.status -eq 501} | ForEach-Object {$_.operation})
    recentObservedProblems=$taskProblems
    remaining=if($taskAccepted){@('unimplemented station/outfitting/trade/world operations')}else{@('physical keyboard and user visual acceptance','local system map visual acceptance','unimplemented station/outfitting/trade/world operations')}
}
$taskJson=$taskResult | ConvertTo-Json -Depth 9
if($OutputPath) {
    $taskOutput=[IO.Path]::GetFullPath($OutputPath)
    [IO.File]::WriteAllText($taskOutput,$taskJson,[Text.UTF8Encoding]::new($false))
}
$taskJson
