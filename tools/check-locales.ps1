# Validate translation files against the English source.
#
# Catches the three things that actually break a community translation:
#   1. missing keys      -> that line silently falls back to English
#   2. unknown keys      -> usually a typo, and the intended line stays untranslated
#   3. placeholder drift -> a dropped {0} loses data; an extra one makes Loc.T fall back to English at runtime
#
# Usage:  pwsh tools\check-locales.ps1
# Exit code is non-zero if any file has a problem, so this can gate a release.

$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot '..\Localization'

function Read-Locale($path) {
    $map = @{}
    foreach ($raw in Get-Content $path -Encoding UTF8) {
        $line = $raw.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
        $eq = $line.IndexOf('=')
        if ($eq -le 0) { continue }
        $map[$line.Substring(0, $eq).Trim()] = $line.Substring($eq + 1).Trim()
    }
    return $map
}

# Set of distinct placeholder indices in a value, e.g. "{1} of {0}" -> "0,1"
function Get-Slots($value) {
    $found = [regex]::Matches($value, '\{(\d+)\}') | ForEach-Object { [int]$_.Groups[1].Value }
    if (-not $found) { return '' }
    return (($found | Sort-Object -Unique) -join ',')
}

$en = Read-Locale (Join-Path $dir 'en.txt')
Write-Host "en.txt: $($en.Count) keys (source)`n"

$failed = $false
foreach ($file in Get-ChildItem $dir -Filter *.txt | Where-Object { $_.Name -ne 'en.txt' } | Sort-Object Name) {
    $loc = Read-Locale $file.FullName
    $problems = @()

    foreach ($k in $en.Keys | Sort-Object) {
        if (-not $loc.ContainsKey($k)) { $problems += "  missing key      : $k"; continue }
        $a = Get-Slots $en[$k]
        $b = Get-Slots $loc[$k]
        if ($a -ne $b) { $problems += "  placeholder drift: $k  (en has [$a], this has [$b])" }
    }
    foreach ($k in $loc.Keys | Sort-Object) {
        if (-not $en.ContainsKey($k)) { $problems += "  unknown key      : $k" }
    }

    $pct = if ($en.Count -gt 0) { [math]::Round(100 * (($en.Keys | Where-Object { $loc.ContainsKey($_) }).Count) / $en.Count) } else { 100 }
    if ($problems.Count -eq 0) {
        Write-Host ("{0,-12} OK    {1,3}% ({2} keys)" -f $file.Name, $pct, $loc.Count) -ForegroundColor Green
    } else {
        $failed = $true
        Write-Host ("{0,-12} FAIL  {1,3}%" -f $file.Name, $pct) -ForegroundColor Red
        $problems | ForEach-Object { Write-Host $_ -ForegroundColor DarkYellow }
    }
}

if ($failed) { Write-Host "`nOne or more locales have problems." -ForegroundColor Red; exit 1 }
Write-Host "`nAll locales consistent with en.txt." -ForegroundColor Green
