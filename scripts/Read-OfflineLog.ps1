param(
    [Parameter(Mandatory=$true)][string]$RunDirectory,
    [string]$TraceId,
    [switch]$AllLevels,
    [long]$FromSequence=0,
    [ValidateRange(1,500)][int]$Limit=40
)
$ErrorActionPreference='Stop'
$logRoot=Join-Path ([IO.Path]::GetFullPath($RunDirectory)) 'logs'
$records=[Collections.Generic.List[object]]::new()
$invalid=0
foreach($file in (Get-ChildItem -LiteralPath $logRoot -File | Where-Object {$_.Name -match '^(offline-runtime|bootstrapper)\.\d{4}\.jsonl$'} | Sort-Object Name)) {
    $logInput=[IO.FileStream]::new($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $logReader=[IO.StreamReader]::new($logInput)
    try { while($null -ne ($line=$logReader.ReadLine())) {
        try {
            $entry=ConvertFrom-Json -InputObject $line -AsHashtable
            if($entry.sequence -lt $FromSequence){continue}
            $fields=if($entry.ContainsKey('fields')){$entry.fields}else{@{}}
            if($TraceId -and $entry.traceId -ne $TraceId -and $fields.httpTraceId -ne $TraceId){continue}
            if(-not $AllLevels -and $entry.level -notin @('warning','error','critical')){continue}
            $entry['logFile']=$file.FullName
            $records.Add($entry)
        } catch { $invalid++ }
    } } finally { $logReader.Dispose() }
}
if($invalid){Write-Warning "未能解析 $invalid 条记录（运行中的最后一条可能尚未写完）。"}
$groups=$records | Group-Object { $_.component+'|'+$_.event+'|'+$_.message+'|'+$_.source.file+'|'+$_.source.line }
$result=foreach($group in ($groups | Sort-Object {$_.Group[0].utc} | Select-Object -First $Limit)) {
    $first=$group.Group[0];$last=$group.Group[-1]
    $fields=if($first.ContainsKey('fields')){$first.fields}else{@{}}
    [pscustomobject]@{
        firstUtc=$first.utc;lastUtc=$last.utc;count=$group.Count;sequence=$first.sequence
        level=$first.level;component=$first.component;event=$first.event;message=$first.message
        sourceFile=$first.source.file;sourceLine=$first.source.line;sourceMember=$first.source.member
        nativeFunction=$fields.nativeFunctionRva;nativeReturn=$fields.nativeReturnRva
        traceId=$first.traceId;httpTraceId=$fields.httpTraceId;fields=$fields
        exception=$first.exception;logFile=$first.logFile
    }
}
ConvertTo-Json -InputObject @($result) -Depth 8
