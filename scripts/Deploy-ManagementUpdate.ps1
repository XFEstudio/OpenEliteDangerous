param(
    [ValidateSet('system','en-US','zh-CN')][string]$Language = 'system',
    [string]$NativeRuntime,
    [string]$NativeValidationReport,
    [string]$ReadBusinessModule
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskManifestFile = Join-Path $taskRoot 'runtime/deployment.json'
$taskPrevious = Get-Content -LiteralPath $taskManifestFile -Raw | ConvertFrom-Json -AsHashtable
$taskRelease = Join-Path $taskRoot ('runtime/management-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $taskRelease) { throw 'Release directory already exists.' }
$taskReports = Join-Path $taskRelease 'reports'
New-Item -ItemType Directory -Path $taskReports | Out-Null
foreach ($taskPair in @(@('server','Server/XFEEliteDangerous.Server/XFEEliteDangerous.Server.csproj'),@('admin','Client/XFEEliteDangerous.Desktop/XFEEliteDangerous.Desktop.csproj'),@('bootstrapper','Client/XFEEliteDangerous.Bootstrapper/XFEEliteDangerous.Bootstrapper.csproj'))) {
    $taskName = $taskPair[0]
    & dotnet publish (Join-Path $taskRoot $taskPair[1]) -c Release -r win-x64 -p:Platform=x64 --self-contained true -o (Join-Path $taskRelease $taskName) --nologo -v minimal *> (Join-Path $taskReports ('publish-' + $taskName + '.log'))
    if ($LASTEXITCODE -ne 0) { throw ('Publish failed: ' + $taskName) }
}
# A normal management update retains the validated native host. A native host
# update requires matching real-game offline/online and save/reload evidence.
$taskNativeSource=$taskPrevious.nativeRuntime
$taskBusinessSource=$null
if($ReadBusinessModule -and -not $NativeValidationReport){throw 'A business-module update requires native validation evidence.'}
if($NativeRuntime -or $NativeValidationReport){
    if(-not $NativeRuntime -or -not $NativeValidationReport){throw 'Native runtime and validation report must be supplied together.'}
    $taskNativeProof=Get-Content -LiteralPath $NativeValidationReport -Raw | ConvertFrom-Json
    if(-not $taskNativeProof.passed -or $taskNativeProof.desktopInputUsed -or -not $taskNativeProof.offline -or -not $taskNativeProof.offlineReload -or -not $taskNativeProof.online -or -not $taskNativeProof.onlineReload){throw 'Complete native live-refit validation is required.'}
    if((Get-FileHash -LiteralPath $NativeRuntime).Hash -ne $taskNativeProof.nativeRuntimeSha256){throw 'Native runtime differs from the real-game validation.'}
    if($taskNativeProof.businessModuleSha256){
        if(-not $ReadBusinessModule -or (Get-FileHash -LiteralPath $ReadBusinessModule).Hash -ne $taskNativeProof.businessModuleSha256){throw 'The native save/read business module must match the real-game engineering validation.'}
        $taskBusinessSource=[IO.Path]::GetFullPath($ReadBusinessModule)
        $taskBusinessArchive=Join-Path $taskRelease 'business'
        New-Item -ItemType Directory -Path $taskBusinessArchive|Out-Null
        Copy-Item -LiteralPath $taskBusinessSource -Destination (Join-Path $taskBusinessArchive 'XFEEliteDangerous.ReadBusiness.dll')
        $taskSymbols=[IO.Path]::ChangeExtension($taskBusinessSource,'pdb')
        if(Test-Path -LiteralPath $taskSymbols){Copy-Item -LiteralPath $taskSymbols -Destination (Join-Path $taskBusinessArchive 'XFEEliteDangerous.ReadBusiness.pdb')}
    }
    $taskNativeSource=[IO.Path]::GetFullPath($NativeRuntime)
    Copy-Item -LiteralPath $NativeValidationReport -Destination (Join-Path $taskReports 'live-refit-validation.json')
    foreach($taskAssembly in $taskNativeProof.serverAssemblies.PSObject.Properties){
        if((Get-FileHash -LiteralPath (Join-Path $taskRelease ('server/'+$taskAssembly.Name))).Hash -ne $taskAssembly.Value){throw ('Server differs from native refit validation: '+$taskAssembly.Name)}
    }
}
Copy-Item -LiteralPath (Split-Path -Parent $taskNativeSource) -Destination (Join-Path $taskRelease 'native') -Recurse
$taskNative = Join-Path $taskRelease 'native/XFEEliteDangerous.OfflineRuntime.dll'
if ((Get-FileHash $taskNative).Hash -ne (Get-FileHash -LiteralPath $taskNativeSource).Hash) { throw 'Native bridge copy differs.' }
$taskAdmin = Join-Path $taskRelease 'admin/XFEEliteDangerous.Desktop.exe'
foreach ($taskLocale in @('en-US','zh-CN')) {
    $taskReport = Join-Path $taskReports ('desktop-' + $taskLocale + '.json')
    $taskProcess = Start-Process -FilePath $taskAdmin -ArgumentList @(("--language=" + $taskLocale),("--verify-desktop=" + $taskReport)) -WindowStyle Hidden -PassThru
    if (-not $taskProcess.WaitForExit(45000)) { throw 'Hidden UI verification timed out.' }
    if ($taskProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskReport) -or -not (Get-Content -LiteralPath $taskReport -Raw | ConvertFrom-Json).passed) { throw ('Hidden UI verification failed: ' + $taskLocale) }
}
& (Join-Path $taskRelease 'bootstrapper/XFEEliteDangerous.Bootstrapper.exe') --loader-check *> (Join-Path $taskReports 'loader-check.log')
if ($LASTEXITCODE -ne 0) { throw 'Native loader checks failed.' }
& (Join-Path $PSScriptRoot 'Test-Localization.ps1') -OutputDirectory (Join-Path $taskReports 'http') -ServerDirectory (Join-Path $taskRelease 'server') *> (Join-Path $taskReports 'localization-http.log')
if ($LASTEXITCODE -ne 0) { throw 'Localization integration failed.' }
$taskHttpProof = Get-Content -LiteralPath (Join-Path $taskReports 'http/validation.json') -Raw | ConvertFrom-Json -AsHashtable
if (-not $taskHttpProof.passed -or $taskHttpProof.serverExecutable -ne (Join-Path $taskRelease 'server/XFEEliteDangerous.Server.exe')) { throw 'HTTP verification did not use this published server.' }
foreach ($taskAssembly in $taskHttpProof.serverAssemblies.Keys) {
    $taskPublishedAssembly = Join-Path $taskRelease ('server/' + $taskAssembly)
    if ($taskHttpProof.serverAssemblies[$taskAssembly] -ne (Get-FileHash $taskPublishedAssembly).Hash) { throw ('Published server changed after HTTP verification: ' + $taskAssembly) }
}
# Do not interrupt a running private game to replace its server.
$taskPinnedGame = 'E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe'
if (Get-CimInstance Win32_Process -Filter "Name='EliteDangerous64.exe'" | Where-Object { $_.ExecutablePath -eq $taskPinnedGame }) { throw 'Save and exit the private game before promoting this prepared update.' }
try { $taskHealth = Invoke-RestMethod -Uri ($taskPrevious.address + 'health') -TimeoutSec 2 } catch { $taskHealth = $null }
if ($taskHealth) { & (Join-Path $PSScriptRoot 'Stop-ManagementServer.ps1') }
$taskBusinessActive=$null;$taskPreviousBusiness=$null
try {
if($taskBusinessSource){
    $taskBusinessRoot=Join-Path $taskPrevious.workspace 'native-business'
    New-Item -ItemType Directory -Path $taskBusinessRoot -Force|Out-Null
    $taskBusinessActive=Join-Path $taskBusinessRoot 'active.json'
    if(Test-Path -LiteralPath $taskBusinessActive){$taskPreviousBusiness=[IO.File]::ReadAllBytes($taskBusinessActive)}
    $taskBusinessGeneration='refit-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
    $taskBusinessFilename='read-'+$taskBusinessGeneration+'.dll'
    Copy-Item -LiteralPath $taskBusinessSource -Destination (Join-Path $taskBusinessRoot $taskBusinessFilename)
    if(Test-Path -LiteralPath ([IO.Path]::ChangeExtension($taskBusinessSource,'pdb'))){Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($taskBusinessSource,'pdb')) -Destination (Join-Path $taskBusinessRoot ([IO.Path]::ChangeExtension($taskBusinessFilename,'pdb')))}
    $taskBusinessManifest=@{schema=1;abi=1;saveAbi=1;rpcAbi=1;rpcMethods=@(2,9,17,32,89);generation=$taskBusinessGeneration;file=$taskBusinessFilename;
        sha256=(Get-FileHash -LiteralPath $taskBusinessSource).Hash.ToLowerInvariant();gameSha256='e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988';
        operations=@(Get-Content -LiteralPath (Join-Path $taskRoot 'Local/XFEEliteDangerous.ReadBusiness/operations.json') -Raw|ConvertFrom-Json)}|ConvertTo-Json -Depth 5
    $taskBusinessTemporary=Join-Path $taskBusinessRoot ($taskBusinessGeneration+'.tmp')
    [IO.File]::WriteAllText($taskBusinessTemporary,$taskBusinessManifest,[Text.UTF8Encoding]::new($false))
    [IO.File]::Move($taskBusinessTemporary,$taskBusinessActive,$true)
}
$taskManifest = $taskPrevious.Clone()
$taskManifest.release = $taskRelease
$taskManifest.createdUtc = [DateTimeOffset]::UtcNow
$taskManifest.serverExe = Join-Path $taskRelease 'server/XFEEliteDangerous.Server.exe'
$taskManifest.adminExe = $taskAdmin
$taskManifest.bootstrapperExe = Join-Path $taskRelease 'bootstrapper/XFEEliteDangerous.Bootstrapper.exe'
$taskManifest.nativeRuntime = $taskNative
$taskManifest.reports = $taskReports
$taskManifestJson = $taskManifest | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $taskPrevious.dataRoot 'deployment.json'), $taskManifestJson, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($taskManifestFile, $taskManifestJson, [Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot 'Start-ManagementServer.ps1') -Language $Language
}
catch {
    if($taskBusinessActive -and $null -ne $taskPreviousBusiness){
        $taskBusinessTemporary=$taskBusinessActive+'.restore.tmp'
        [IO.File]::WriteAllBytes($taskBusinessTemporary,$taskPreviousBusiness);[IO.File]::Move($taskBusinessTemporary,$taskBusinessActive,$true)
    }
    elseif($taskBusinessActive -and (Test-Path -LiteralPath $taskBusinessActive)){
        $taskCurrentBusiness=Get-Content -LiteralPath $taskBusinessActive -Raw|ConvertFrom-Json
        if($taskCurrentBusiness.generation -eq $taskBusinessGeneration){[IO.File]::Delete($taskBusinessActive)}
    }
    $taskOriginal = $taskPrevious | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $taskPrevious.dataRoot 'deployment.json'), $taskOriginal, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($taskManifestFile, $taskOriginal, [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'Start-ManagementServer.ps1') -Language $Language
    throw
}
Write-Output ('Management update deployed: ' + $taskRelease)
