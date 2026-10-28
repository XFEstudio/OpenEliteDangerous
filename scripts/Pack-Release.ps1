param(
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$')][string]$Version,
    [Parameter(Mandatory)][string]$CompiledRelease,
    [string]$OutputDirectory,
    [string]$NativeValidationReport
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskCompiled=[IO.Path]::GetFullPath($CompiledRelease)
$taskOutput=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $taskRoot ('artifacts/github-release-'+$Version)}
if(-not $taskOutput.StartsWith((Join-Path $taskRoot 'artifacts')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Release outputs must stay under this repository artifacts directory.'}
$taskName='OpenEliteDangerous-'+$Version+'-win-x64'
$taskPayload=Join-Path $taskOutput $taskName
$taskSymbols=Join-Path $taskOutput ($taskName+'-symbols')
if((Test-Path -LiteralPath $taskPayload) -or (Test-Path -LiteralPath $taskSymbols)){throw 'Use a new staging directory; existing packages are never overwritten.'}
if(-not $NativeValidationReport){$NativeValidationReport=Join-Path $taskCompiled 'reports/live-refit-validation.json'}
$taskProof=Get-Content -LiteralPath $NativeValidationReport -Raw|ConvertFrom-Json
if(-not $taskProof.passed -or $taskProof.desktopInputUsed -or -not $taskProof.offline -or -not $taskProof.online -or -not $taskProof.offlineReload -or -not $taskProof.onlineReload){throw 'A successful offline/online native validation is required.'}
$taskNative=Join-Path $taskCompiled 'native/XFEEliteDangerous.OfflineRuntime.dll'
$taskBusiness=Join-Path $taskCompiled 'business/XFEEliteDangerous.ReadBusiness.dll'
if((Get-FileHash -LiteralPath $taskNative).Hash -ne $taskProof.nativeRuntimeSha256 -or (Get-FileHash -LiteralPath $taskBusiness).Hash -ne $taskProof.businessModuleSha256){throw 'Native binaries differ from the validated build.'}
foreach($taskAssembly in $taskProof.serverAssemblies.PSObject.Properties){
    if((Get-FileHash -LiteralPath (Join-Path $taskCompiled ('server/'+$taskAssembly.Name))).Hash -ne $taskAssembly.Value){throw ('Validated server differs: '+$taskAssembly.Name)}
}
New-Item -ItemType Directory -Path $taskPayload,$taskSymbols -Force|Out-Null
foreach($taskComponent in @('server','admin','bootstrapper','native','business')){
    $taskComponentRoot=Join-Path $taskCompiled $taskComponent
    foreach($taskFile in Get-ChildItem -LiteralPath $taskComponentRoot -Recurse -File){
        $taskRelative=[IO.Path]::GetRelativePath($taskComponentRoot,$taskFile.FullName)
        if($taskRelative -match '(?i)(^|[\\/])(ProfilesRoot|Credentials|captures|logs|diagnostics|reports)([\\/]|$)|(^|[\\/])(state|server|admin-access|game-client|deployment)\.json$' -or $taskFile.Extension -match '^\.(pfx|p12|pem|key|pcap|pcapng|har|jsonl|log|bak|tmp)$'){throw ('Private/generated content in binary input: '+$taskRelative)}
        $taskDestination=Join-Path $(if($taskFile.Extension -eq '.pdb'){$taskSymbols}else{$taskPayload}) ($taskComponent+'/'+$taskRelative)
        New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force|Out-Null
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
    }
}
foreach($taskFile in @('README.md','README.zh-CN.md','LICENSE','LICENSE.zh-CN.md','THIRD-PARTY-NOTICES.md')){Copy-Item -LiteralPath (Join-Path $taskRoot $taskFile) -Destination (Join-Path $taskPayload $taskFile)}
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination (Join-Path $taskPayload 'docs') -Recurse
Copy-Item -LiteralPath (Join-Path $taskRoot 'licenses') -Destination (Join-Path $taskPayload 'licenses') -Recurse
Copy-Item -LiteralPath (Join-Path $taskRoot 'Local/XFEEliteDangerous.ReadBusiness/operations.json') -Destination (Join-Path $taskPayload 'business/operations.json')
New-Item -ItemType Directory -Path (Join-Path $taskPayload 'scripts'),(Join-Path $taskPayload 'Client/XFEEliteDangerous.Client/Localization') -Force|Out-Null
foreach($taskScript in @('Initialize-Release.ps1','Start-Admin.ps1','Start-ManagementServer.ps1','Stop-ManagementServer.ps1','Start-ManagedGame.ps1','Start-OnlineGame.ps1','Localization.ps1','Read-OfflineLog.ps1')){
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskScript) -Destination (Join-Path $taskPayload ('scripts/'+$taskScript))
}
foreach($taskLanguage in @('en-US','zh-CN')){Copy-Item -LiteralPath (Join-Path $taskRoot ('Client/XFEEliteDangerous.Client/Localization/'+$taskLanguage+'.json')) -Destination (Join-Path $taskPayload ('Client/XFEEliteDangerous.Client/Localization/'+$taskLanguage+'.json'))}
foreach($taskEntry in @(@('Initialize','Initialize-Release'),@('Start-Admin','Start-Admin'),@('Start-Server','Start-ManagementServer'),@('Stop-Server','Stop-ManagementServer'),@('Start-Game','Start-ManagedGame'),@('Start-Online','Start-OnlineGame'))){
    $taskText="@echo off`r`npwsh -NoProfile -ExecutionPolicy Bypass -File `"%~dp0scripts\$($taskEntry[1]).ps1`" %*`r`nif errorlevel 1 pause`r`n"
    [IO.File]::WriteAllText((Join-Path $taskPayload ($taskEntry[0]+'.cmd')),$taskText,[Text.ASCIIEncoding]::new())
}
# Retain resolved package notices, including bundled .NET and Windows App SDK licenses.
$taskPackages=@{}
foreach($taskProject in @('Client/XFEEliteDangerous.Desktop','Client/XFEEliteDangerous.Bootstrapper','Client/XFEEliteDangerous.OfflineRuntime','Server/XFEEliteDangerous.Server','Local/XFEEliteDangerous.ReadBusiness')){
    $taskAssets=Get-Content -LiteralPath (Join-Path $taskRoot ($taskProject+'/obj/project.assets.json')) -Raw|ConvertFrom-Json -AsHashtable
    foreach($taskLibrary in $taskAssets.libraries.GetEnumerator()|Where-Object {$_.Value.type -eq 'package'}){
        if($taskPackages.ContainsKey($taskLibrary.Key)){continue}
        $taskPackageRoot=$null
        foreach($taskFolder in $taskAssets.packageFolders.Keys){$taskCandidate=Join-Path $taskFolder $taskLibrary.Value.path;if(Test-Path -LiteralPath $taskCandidate){$taskPackageRoot=$taskCandidate;break}}
        if(-not $taskPackageRoot){throw ('Resolved package cache missing: '+$taskLibrary.Key)}
        $taskNuspec=Get-ChildItem -LiteralPath $taskPackageRoot -Filter '*.nuspec' -File|Select-Object -First 1
        $taskXml=[xml](Get-Content -LiteralPath $taskNuspec.FullName -Raw)
        $taskMetadata=$taskXml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        $taskRecord=@{package=$taskLibrary.Key;license=$taskMetadata.SelectSingleNode('*[local-name()="license"]')?.InnerText;licenseUrl=$taskMetadata.SelectSingleNode('*[local-name()="licenseUrl"]')?.InnerText;notices=@()}
        $taskNoticeFiles=@(Get-ChildItem -LiteralPath $taskPackageRoot -Recurse -File|Where-Object {$_.Name -match '(?i)^(license|licence|third[-_. ]?party[-_. ]?notices?|notice|copyright)(\.[a-z0-9_-]+)?\.(txt|md|html)$|^(LICENSE|LICENCE|NOTICE|COPYRIGHT)$'})
        foreach($taskNotice in $taskNoticeFiles){
            $taskRelative=[IO.Path]::GetRelativePath($taskPackageRoot,$taskNotice.FullName)
            $taskDestination=Join-Path $taskPayload ('licenses/nuget/'+$taskLibrary.Key+'/'+$taskRelative)
            New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force|Out-Null
            Copy-Item -LiteralPath $taskNotice.FullName -Destination $taskDestination
            $taskRecord.notices+=([IO.Path]::GetRelativePath($taskPayload,$taskDestination).Replace('\','/'))
        }
        $taskPackages[$taskLibrary.Key]=$taskRecord
    }
}
# SDK-injected self-contained runtime packs are not listed as normal NuGet libraries.
$taskRuntimeConfiguration=Get-Content -LiteralPath (Join-Path $taskCompiled 'server/XFEEliteDangerous.Server.runtimeconfig.json') -Raw|ConvertFrom-Json
foreach($taskFramework in $taskRuntimeConfiguration.runtimeOptions.includedFrameworks){
    $taskPackageId=$taskFramework.name.ToLowerInvariant()+'.runtime.win-x64/'+$taskFramework.version
    $taskRuntimeRoot=$null
    foreach($taskFolder in $taskAssets.packageFolders.Keys){$taskCandidate=Join-Path $taskFolder $taskPackageId;if(Test-Path -LiteralPath $taskCandidate){$taskRuntimeRoot=$taskCandidate;break}}
    if(-not $taskRuntimeRoot){throw ('Self-contained runtime pack notices missing: '+$taskPackageId)}
    $taskRecord=@{package=$taskPackageId;license='MIT';licenseUrl='https://github.com/dotnet/runtime';notices=@()}
    foreach($taskNotice in Get-ChildItem -LiteralPath $taskRuntimeRoot -File|Where-Object {$_.Name -match '(?i)license|third.party.notices'}){
        $taskDestination=Join-Path $taskPayload ('licenses/nuget/'+$taskPackageId+'/'+$taskNotice.Name)
        New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force|Out-Null
        Copy-Item -LiteralPath $taskNotice.FullName -Destination $taskDestination
        $taskRecord.notices+=([IO.Path]::GetRelativePath($taskPayload,$taskDestination).Replace('\','/'))
    }
    if($taskRecord.notices.Count -lt 2){throw 'Missing .NET runtime license or third-party notices.'}
    $taskPackages[$taskPackageId]=$taskRecord
}
($taskPackages.Values|Sort-Object package)|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $taskPayload 'licenses/resolved-packages.json') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $taskPayload 'LICENSE') -Destination (Join-Path $taskSymbols 'LICENSE')
Copy-Item -LiteralPath (Join-Path $taskPayload 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $taskSymbols 'THIRD-PARTY-NOTICES.md')
$taskRevision=(& git -C $taskRoot rev-parse HEAD).Trim();if($LASTEXITCODE -ne 0){throw 'Cannot resolve release source revision.'}
$taskFiles=@(Get-ChildItem -LiteralPath $taskPayload -Recurse -File|Sort-Object FullName|ForEach-Object {@{path=[IO.Path]::GetRelativePath($taskPayload,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})
$taskManifest=@{schema=1;version=$Version;rid='win-x64';sourceRevision=$taskRevision;repository='https://github.com/XFEstudio/OpenEliteDangerous';
    createdUtc=[DateTimeOffset]::UtcNow;buildOrigin='Previously published binaries matching native validation; see validation scope below.';
    gameBuild='2026.09.03.332841';gameSha256='e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988';
    nativeRuntimeSha256=$taskProof.nativeRuntimeSha256.ToLowerInvariant();businessModuleSha256=$taskProof.businessModuleSha256.ToLowerInvariant();
    validation=@{nativeChecks=$taskProof.nativeChecks;advancedNativeChecks=$taskProof.advancedNativeChecks;offline=$taskProof.offline;offlineReload=$taskProof.offlineReload;online=$taskProof.online;onlineReload=$taskProof.onlineReload;
        scope='Historical native acceptance on 2026-10-22: pinned Odyssey, private fixtures, one client at a time, offline and loopback HTTPS; not all ships or network topologies.'};files=$taskFiles}
$taskManifest|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $taskPayload 'release-manifest.json') -Encoding utf8NoBOM
$taskManifest|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $taskOutput ($taskName+'-manifest.json')) -Encoding utf8NoBOM
$taskAssets=@()
foreach($taskDirectory in @($taskPayload,$taskSymbols)){
    $taskArchive=$taskDirectory+'.zip'
    if(Test-Path -LiteralPath $taskArchive){throw 'Release archive already exists.'}
    [IO.Compression.ZipFile]::CreateFromDirectory($taskDirectory,$taskArchive,[IO.Compression.CompressionLevel]::Optimal,$true)
    $taskAssets+=$taskArchive
}
$taskAssets+=(Join-Path $taskOutput ($taskName+'-manifest.json'))
@($taskAssets|ForEach-Object {((Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($_))})|Set-Content -LiteralPath (Join-Path $taskOutput 'SHA256SUMS.txt') -Encoding ascii
Write-Output ('Packed '+$taskName+'; '+$taskFiles.Count+' payload files. No workspace, credentials or game assets included.')
$taskAssets|ForEach-Object {Get-Item -LiteralPath $_}|Select-Object Name,Length
