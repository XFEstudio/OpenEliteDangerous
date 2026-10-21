param(
    [Parameter(Mandatory)][uint32]$GameProcessId,
    [Parameter(Mandatory)][string]$GamePath,
    [Parameter(Mandatory)][string]$DumpcapPath,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string[]]$Interfaces = @(),
    [string]$InterfaceCsv = ''
)
$ErrorActionPreference = 'Stop'
if ($InterfaceCsv) { $Interfaces = @($InterfaceCsv.Split(',')) }
if ($Interfaces.Count -eq 0 -or @($Interfaces | Where-Object { $_ -notmatch '^\d{1,3}$' }).Count -gt 0) { throw 'Specify the verified numeric capture interface IDs.' }
$wireGamePath = [IO.Path]::GetFullPath($GamePath)
$wireDumpcapPath = [IO.Path]::GetFullPath($DumpcapPath)
$wireOutput = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $wireOutput) { throw 'Wire evidence directory already exists; refusing to overwrite.' }
if (-not (Test-Path -LiteralPath $wireDumpcapPath -PathType Leaf)) { throw 'dumpcap was not found.' }
New-Item -ItemType Directory -Path $wireOutput | Out-Null
$wireChildren = [Collections.Generic.List[object]]::new()
$wireLastFilter = ''; $wireLastStart = [datetime]::MinValue; $wirePart = 0
$wireStopReason = 'stopped'
try {
    while (-not (Test-Path -LiteralPath (Join-Path $wireOutput 'stop-capture'))) {
        $wireGame = Get-Process -Id $GameProcessId -ErrorAction SilentlyContinue
        if ($null -eq $wireGame) { $wireStopReason = 'game-exited'; break }
        if ($wireGame.Path -ne $wireGamePath) { throw 'The game process no longer matches the selected path.' }
        $wireBytes = (Get-ChildItem -LiteralPath $wireOutput -Filter '*.pcapng' -File | Measure-Object Length -Sum).Sum
        if ($wireBytes -ge 512MB) { $wireStopReason = '512MiB-wire-evidence-limit'; break }
        $wireTcp = @(Get-NetTCPConnection -OwningProcess $GameProcessId -ErrorAction SilentlyContinue | Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,State)
        $wireUdp = @(Get-NetUDPEndpoint -OwningProcess $GameProcessId -ErrorAction SilentlyContinue | Select-Object LocalAddress,LocalPort)
        $wireFilter = (@($wireUdp | Select-Object -ExpandProperty LocalPort -Unique | Sort-Object | ForEach-Object { 'udp port ' + $_ }) + @($wireTcp | Select-Object -ExpandProperty LocalPort -Unique | Sort-Object | ForEach-Object { 'tcp port ' + $_ })) -join ' or '
        $wireNow = [datetime]::UtcNow
        if ($wireFilter -and (($wireFilter -ne $wireLastFilter -and ($wireNow-$wireLastStart).TotalSeconds -ge 10) -or ($wireNow-$wireLastStart).TotalSeconds -ge 50)) {
            $wirePart++
            $wireName = 'part-' + $wirePart.ToString('D4')
            $wirePacketPath = Join-Path $wireOutput ($wireName + '.pcapng')
            $wireInfo = [Diagnostics.ProcessStartInfo]::new($wireDumpcapPath)
            $wireInfo.UseShellExecute = $false; $wireInfo.CreateNoWindow = $true
            $wireInfo.RedirectStandardError = $true; $wireInfo.RedirectStandardOutput = $true
            foreach ($wireArgument in @('-q','-f',$wireFilter)) { $wireInfo.ArgumentList.Add($wireArgument) }
            foreach ($wireInterface in $Interfaces) { $wireInfo.ArgumentList.Add('-i'); $wireInfo.ArgumentList.Add($wireInterface) }
            foreach ($wireArgument in @('-s','0','-a','duration:60','-a','filesize:32768','-w',$wirePacketPath)) { $wireInfo.ArgumentList.Add($wireArgument) }
            $wireChild = [Diagnostics.Process]::Start($wireInfo)
            $wireChildren.Add([pscustomobject]@{ Process=$wireChild; Name=$wireName; ErrorTask=$wireChild.StandardError.ReadToEndAsync(); OutputTask=$wireChild.StandardOutput.ReadToEndAsync(); Reported=$false })
            @{ utc=$wireNow.ToString('o'); gamePid=$GameProcessId; gamePath=$wireGamePath; capturePid=$wireChild.Id; file=$wireName+'.pcapng'; filter=$wireFilter; tcp=$wireTcp; udp=$wireUdp; interfaces=$Interfaces; scope='PID-correlated socket-port filter; overlapping segments may contain duplicate packets' } |
                ConvertTo-Json -Depth 6 -Compress | Add-Content -LiteralPath (Join-Path $wireOutput 'ownership.jsonl') -Encoding utf8
            $wireLastFilter = $wireFilter; $wireLastStart = $wireNow
            Write-Output ('WIRE ' + $wireName + ' capture PID=' + $wireChild.Id)
        }
        foreach ($wireEntry in $wireChildren) {
            if ($wireEntry.Process.HasExited -and -not $wireEntry.Reported) {
                [IO.File]::WriteAllText((Join-Path $wireOutput ($wireEntry.Name+'.log')), $wireEntry.ErrorTask.GetAwaiter().GetResult() + $wireEntry.OutputTask.GetAwaiter().GetResult())
                $wireEntry.Reported = $true
                if ($wireEntry.Process.ExitCode -ne 0) { throw ('dumpcap failed for '+$wireEntry.Name) }
            }
        }
        Start-Sleep -Seconds 2
    }
} finally {
    # Only handles returned by this script are stopped. Never stop the game or another capture.
    foreach ($wireEntry in $wireChildren) {
        if (-not $wireEntry.Process.HasExited) { $wireEntry.Process.Kill(); $wireEntry.Process.WaitForExit() }
        if (-not $wireEntry.Reported) {
            [IO.File]::WriteAllText((Join-Path $wireOutput ($wireEntry.Name+'.log')), $wireEntry.ErrorTask.GetAwaiter().GetResult() + $wireEntry.OutputTask.GetAwaiter().GetResult())
        }
        $wireEntry.Process.Dispose()
    }
    @{ utc=[datetime]::UtcNow.ToString('o'); reason=$wireStopReason; gameStopped=$false; parts=$wirePart } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $wireOutput 'completed.json') -Encoding utf8
}
