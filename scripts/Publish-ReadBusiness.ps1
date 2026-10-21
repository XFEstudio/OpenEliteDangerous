param([Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskWorkspace=[IO.Path]::GetFullPath($Workspace)
if (-not (Test-Path -LiteralPath $taskWorkspace -PathType Container)) { throw '离线工作区不存在。' }
$taskChecked=Get-Item -LiteralPath $taskWorkspace
while ($null -ne $taskChecked) {
    if ($taskChecked.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '业务模块工作区不能使用文件系统链接。' }
    $taskChecked=$taskChecked.Parent
}
$taskGeneration='r'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$taskOutput=Join-Path $taskRoot ('artifacts/read-business-'+$taskGeneration)
& dotnet publish (Join-Path $taskRoot 'Local/XFEEliteDangerous.ReadBusiness/XFEEliteDangerous.ReadBusiness.csproj') -c Release -o $taskOutput --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'C# 只读业务模块编译失败，当前游戏模块保持不变。' }
$taskSource=Join-Path $taskOutput 'XFEEliteDangerous.ReadBusiness.dll'
$taskHash=(Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash.ToLowerInvariant()
$taskDestination=Join-Path $taskWorkspace 'native-business'
if (Test-Path -LiteralPath $taskDestination) {
    if ((Get-Item -LiteralPath $taskDestination).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '业务模块目录不能使用文件系统链接。' }
} else { [IO.Directory]::CreateDirectory($taskDestination) | Out-Null }
$taskFilename='read-'+$taskGeneration+'.dll'
[IO.File]::Copy($taskSource,(Join-Path $taskDestination $taskFilename),$false)
$taskSymbols=[IO.Path]::ChangeExtension($taskSource,'pdb')
if (Test-Path -LiteralPath $taskSymbols) { [IO.File]::Copy($taskSymbols,(Join-Path $taskDestination ([IO.Path]::ChangeExtension($taskFilename,'pdb'))),$false) }
$taskOperations=Get-Content -LiteralPath (Join-Path $taskRoot 'Local/XFEEliteDangerous.ReadBusiness/operations.json') -Raw | ConvertFrom-Json
$taskManifest=@{
    schema=1;abi=1;gameSha256='e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988'
    generation=$taskGeneration;file=$taskFilename;sha256=$taskHash;operations=@($taskOperations)
    rpcAbi=1;rpcMethods=@(2,9,17,32,89);saveAbi=1
} | ConvertTo-Json -Depth 4
$taskTemporary=Join-Path $taskDestination ('active-'+$taskGeneration+'.tmp')
[IO.File]::WriteAllText($taskTemporary,$taskManifest,[Text.UTF8Encoding]::new($false))
[IO.File]::Move($taskTemporary,(Join-Path $taskDestination 'active.json'),$true)
Write-Output ('已发布 C# 读取与 RPC 业务模块 '+$taskGeneration+'；支持该 ABI 的运行时在下一次匹配的请求时切换。')
