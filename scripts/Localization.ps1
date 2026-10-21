function Initialize-AppLanguage {
    param([string]$Language)
    if ([string]::IsNullOrWhiteSpace($Language)) { $Language = $env:XFE_LANGUAGE }
    if (-not [string]::IsNullOrWhiteSpace($Language)) {
        if ($Language -notin @('system','en-US','zh-CN')) { throw 'Supported languages: system, en-US, zh-CN.' }
        $env:XFE_LANGUAGE = $Language
    }
    $taskResolvedLanguage = if (-not $Language -or $Language -eq 'system') {
        if ((Get-UICulture).Name.StartsWith('zh', [StringComparison]::OrdinalIgnoreCase)) { 'zh-CN' } else { 'en-US' }
    } else { $Language }
    $taskResourceFile = Join-Path $PSScriptRoot ('../Client/XFEEliteDangerous.Client/Localization/' + $taskResolvedLanguage + '.json')
    $script:taskAppMessages = Get-Content -LiteralPath $taskResourceFile -Raw | ConvertFrom-Json -AsHashtable
}
function Get-AppText {
    param([Parameter(Mandatory)][string]$Key)
    if (-not $script:taskAppMessages.Contains($Key)) { throw ('Missing localization key: ' + $Key) }
    return $script:taskAppMessages[$Key]
}
