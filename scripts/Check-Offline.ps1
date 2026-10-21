$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskForbidden = '\b(HttpClient|HttpListener|HttpRequestMessage|HttpWebRequest|TcpClient|TcpListener|UdpClient|ClientWebSocket|WSAConnect|WSASocket|WSASend|WSARecv)\b|using\s+System\.Net\.(Http|Sockets|Security)|DllImport\s*\(\s*"(ws2_32|winhttp|wininet)|/control/v1|/player/v1'
$taskViolations = @()
$taskFiles = @()
foreach ($taskDirectory in @('Client','Desktop','Local')) {
    $taskFiles += @(Get-ChildItem -LiteralPath (Join-Path $taskRoot $taskDirectory) -Recurse -File -Filter '*.cs' | Where-Object { $_.FullName -notmatch '\\(Tests|bin|obj)\\' })
}
foreach ($taskFile in $taskFiles) {
    # Management and the explicitly requested online launcher are the only
    # network boundaries. The isolated native game never opens a socket.
    if($taskFile.FullName -in @((Join-Path $taskRoot 'Client/XFEEliteDangerous.ManagementClient/ManagementApi.cs'),(Join-Path $taskRoot 'Client/XFEEliteDangerous.OnlineClient/OnlineConnection.cs'))){continue}
    $taskMatches = Select-String -LiteralPath $taskFile.FullName -Pattern $taskForbidden
    if ($taskMatches) { $taskViolations += $taskMatches | ForEach-Object { $_.Path.Substring($taskRoot.Length+1) + ':' + $_.LineNumber } }
}
if ($taskViolations.Count -gt 0) { throw ('发现旧网络调用：' + ($taskViolations -join ', ')) }
$taskSolution = [IO.File]::ReadAllText((Join-Path $taskRoot 'XFEEliteDangerous.slnx'))
$taskSolutionXml=[xml]$taskSolution
if ($taskSolution -match 'Datagram|Bridge/' -or @($taskSolutionXml.SelectNodes('//Project') | Where-Object {$_.Path -like 'Server/*' -and $_.Path -ne 'Server/XFEEliteDangerous.Server/XFEEliteDangerous.Server.csproj'}).Count) { throw '解决方案仍包含旧网络组件。' }
$taskOldCheck = [IO.File]::ReadAllText((Join-Path $taskRoot 'scripts/NativeProtocolCheck/Program.cs'))
if ($taskOldCheck -match $taskForbidden) { throw '旧网络测试入口仍有网络调用。' }
Write-Output ('PASS ' + $taskFiles.Count + ' 个生产源码文件：网络仅允许 ManagementApi 与 OnlineConnection；原生游戏仅使用进程内逻辑或本机 IPC。')
