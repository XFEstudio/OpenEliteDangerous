$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $taskRoot
try {
    foreach ($taskPath in @('artifacts/local-commander.json','runtime/state.json','Local/example/state.json','Local/example/state.json.bak','Local/example/workspace.lock','Local/example/galaxy.json','Local/example/galaxy.lock','Local/example/galaxy-backups/previous.json','Local/example/native-journal/account/receipt.bin','Client/XFEEliteDangerous.Desktop/bin/app.exe','Client/XFEEliteDangerous.Desktop/obj/project.assets.json','Desktop/ProfilesRoot/SystemProfile.xpf','.vs/state.suo','private.pfx','trace.log','admin-access.json','config/game-client.json','trace.pcapng','request.har','capture.jsonl','game.exe','game.pak')) {
        git check-ignore -q --no-index -- $taskPath
        if ($LASTEXITCODE -ne 0) { throw "Private or generated path is not ignored: $taskPath" }
    }
    foreach ($taskPath in @('Local/XFEEliteDangerous.Core/OfflineWorkspace.cs','Client/XFEEliteDangerous.Desktop/XFEEliteDangerous.Desktop.csproj','Desktop/SharedUI/LocalClient.cs','Local/data/catalog.json','Local/data/catalog-source.json')) {
        git check-ignore -q --no-index -- $taskPath
        if ($LASTEXITCODE -eq 0) { throw "Source path was ignored: $taskPath" }
    }
    if (@(git ls-files --cached --ignored --exclude-standard).Count) { throw 'Generated or private files are tracked.' }
    git -c core.safecrlf=false diff --check
    if ($LASTEXITCODE -ne 0) { throw 'Working diff contains whitespace errors.' }
    Write-Output 'PASS Git keeps sources and catalog data, and ignores saves, credentials, generated files and recovery archives.'
} finally { Pop-Location }
