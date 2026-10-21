param([ValidateSet('system','en-US','zh-CN')][string]$Language = 'system')
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
# Native host and business modules are independent. This update retains the
# previously validated native bridge and does not touch the workspace or game.
Copy-Item -LiteralPath (Split-Path -Parent $taskPrevious.nativeRuntime) -Destination (Join-Path $taskRelease 'native') -Recurse
$taskNative = Join-Path $taskRelease 'native/XFEEliteDangerous.OfflineRuntime.dll'
if ((Get-FileHash $taskNative).Hash -ne (Get-FileHash -LiteralPath $taskPrevious.nativeRuntime).Hash) { throw 'Native bridge copy differs.' }
$taskAdmin = Join-Path $taskRelease 'admin/XFEEliteDangerous.Desktop.exe'
foreach ($taskLocale in @('en-US','zh-CN')) {
    $taskReport = Join-Path $taskReports ('desktop-' + $taskLocale + '.json')
    $taskProcess = Start-Process -FilePath $taskAdmin -ArgumentList @('--language=' + $taskLocale,'--verify-desktop=' + $taskReport) -WindowStyle Hidden -PassThru
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
try { & (Join-Path $PSScriptRoot 'Start-ManagementServer.ps1') -Language $Language }
catch {
    $taskOriginal = $taskPrevious | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $taskPrevious.dataRoot 'deployment.json'), $taskOriginal, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($taskManifestFile, $taskOriginal, [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'Start-ManagementServer.ps1') -Language $Language
    throw
}
Write-Output ('Management update deployed: ' + $taskRelease)
