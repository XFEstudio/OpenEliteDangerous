param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
# Read-only labels from the authorized, hash-pinned image. Never loads native
# code or accesses a process. Keep decompiled output in the ignored lab folder.
$imagePath = 'E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe'
$image = [System.IO.File]::ReadAllBytes($imagePath)
$digest = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($image)).ToLowerInvariant()
if ($digest -ne 'e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988') { throw 'Native image pin differs.' }
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$labRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\recon-local')) + [System.IO.Path]::DirectorySeparatorChar
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
if (!$sourcePath.StartsWith($labRoot, [StringComparison]::OrdinalIgnoreCase) -or !$destinationPath.StartsWith($labRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Use the local static-analysis folder.' }
if ((Get-Item -LiteralPath $sourcePath).Length -gt 2MB) { throw 'Decompiled input exceeds its bound.' }
$header = [BitConverter]::ToInt32($image, 0x3c)
if ([BitConverter]::ToUInt32($image, $header) -ne 0x4550) { throw 'Invalid PE.' }
$sectionCount = [BitConverter]::ToUInt16($image, $header + 6)
$optionalSize = [BitConverter]::ToUInt16($image, $header + 20)
$imageBase = [BitConverter]::ToUInt64($image, $header + 24 + 24)
$sections = @()
for ($index = 0; $index -lt $sectionCount; $index++) {
    $offset = $header + 24 + $optionalSize + $index * 40
    $name = [Text.Encoding]::ASCII.GetString($image, $offset, 8).TrimEnd([char]0)
    if ($name -ne '.rdata') { continue }
    $sections += [pscustomobject]@{ Address=[BitConverter]::ToUInt32($image, $offset+12); Size=[BitConverter]::ToUInt32($image, $offset+16); Offset=[BitConverter]::ToUInt32($image, $offset+20) }
}
$sourceText = [System.IO.File]::ReadAllText($sourcePath)
$labels = @{}
foreach ($match in [regex]::Matches($sourceText, '(?:UNK|DAT)_([0-9a-fA-F]{9,16})')) {
    $symbol = $match.Value
    if ($labels.ContainsKey($symbol)) { continue }
    $address = [Convert]::ToUInt64($match.Groups[1].Value, 16)
    if ($address -lt $imageBase) { continue }
    $relative = $address - $imageBase
    foreach ($section in $sections) {
        if ($relative -lt $section.Address -or $relative -ge [ulong]$section.Address + $section.Size) { continue }
        $begin = [int]($section.Offset + $relative - $section.Address)
        $end = $begin
        while ($end -lt [Math]::Min($begin+161, $image.Length) -and $image[$end] -ge 32 -and $image[$end] -lt 127) { $end++ }
        if ($end-$begin -ge 2 -and $end-$begin -le 160 -and $image[$end] -eq 0) {
            $labels[$symbol] = [Text.Encoding]::ASCII.GetString($image, $begin, $end-$begin).Replace('*/', '* /')
        }
    }
}
$annotated = [regex]::Replace($sourceText, '(?:UNK|DAT)_[0-9a-fA-F]{9,16}', { param($match)
    if ($labels.ContainsKey($match.Value)) { return $match.Value + ' /* ' + $labels[$match.Value] + ' */' }
    return $match.Value
})
[System.IO.File]::WriteAllText($destinationPath, $annotated, [Text.UTF8Encoding]::new($false))
[pscustomobject]@{source=$sourcePath; destination=$destinationPath; staticLabels=$labels.Count}
