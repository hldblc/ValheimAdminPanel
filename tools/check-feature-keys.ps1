# Cross-check the locale keys used by the feature modules against en.txt.
#
#   pwsh -NoProfile -File <thisfile>
#
# Complements check-locales.ps1: that one proves the 9 locale files agree with each other, this one
# proves the CODE and en.txt agree. A key used in code but absent from en.txt renders as the raw key
# string in-game (Loc.T falls back active -> English -> the key itself), which is ugly but not fatal —
# so it never breaks a build and is easy to miss without this check.
#
# Key detection deliberately scans for the key SHAPE ("prefix.name") in any string literal rather than
# only `Loc.T("...")`, because keys are routinely passed indirectly:
#     Loc.T(cond ? "mod.lockdown_on" : "mod.lockdown_off")     <- ternary
#     new FeatureSection { LocKey = "mod.chip", ... }          <- registration data
# A `Loc.T("...")`-only regex misses both and reports false orphans.

$ErrorActionPreference = 'Stop'

$FeatureDir  = 'C:\Users\HEPHAESTUS\ValheimAdminPanel\Features'
$EnPath      = 'C:\Users\HEPHAESTUS\ValheimAdminPanel\Localization\en.txt'

# Prefixes owned by the feature modules are DERIVED from the code, not hardcoded: a hand-maintained list
# silently under-reports the moment a new wave picks a prefix nobody added to it (which is exactly what
# happened with area./ux2./bld./pdat./sdk./grd./dm. — the check passed while ignoring them entirely).
# Base-panel prefixes are excluded instead, since that set is stable and small.
$BasePrefixes = @('tab', 'common', 'chrome', 'cat', 'sort', 'items', 'cre', 'boss', 'player', 'se', 'world', 'players', 'srv', 'set', 'side')

if (-not (Test-Path -LiteralPath $EnPath)) { throw "Missing $EnPath" }

$en = @{}
foreach ($line in [IO.File]::ReadAllLines($EnPath, [Text.UTF8Encoding]::new($false))) {
    if ($line.Length -eq 0 -or $line[0] -eq '#') { continue }
    $eq = $line.IndexOf('=')
    if ($eq -gt 0) { $en[$line.Substring(0, $eq)] = $true }
}

# A locale key literal: lowercase prefix, a dot, then lowercase/digits/underscore. Anchored so ordinary
# strings ("AP_SrvX", "0.00", file names) can't match.
$keyShape = '^[a-z][a-z0-9]*\.[a-z0-9_]+$'

$used = @{}
$files = @(Get-ChildItem -LiteralPath $FeatureDir -Filter *.cs -File -ErrorAction SilentlyContinue)
foreach ($f in $files) {
    # Skip comment lines before scanning. Doc comments legitimately contain key-shaped EXAMPLES
    # (`Loc.T("x.action")` in a <c> block, "prefix.name" in prose), and counting those as real
    # references produces phantom "missing key" failures.
    $lines = [IO.File]::ReadAllLines($f.FullName, [Text.UTF8Encoding]::new($false))
    $code = foreach ($line in $lines) {
        $t = $line.TrimStart()
        if ($t.StartsWith('//') -or $t.StartsWith('*') -or $t.StartsWith('/*')) { continue }
        $line
    }
    $text = $code -join "`n"
    foreach ($m in [regex]::Matches($text, '"([^"\\]*)"')) {
        $lit = $m.Groups[1].Value
        if ($lit -cmatch $keyShape) {
            if (-not $used.ContainsKey($lit)) { $used[$lit] = @() }
            if ($used[$lit] -notcontains $f.Name) { $used[$lit] += $f.Name }
        }
    }
}

Write-Host "Scanned $($files.Count) feature file(s); en.txt has $($en.Count) keys; $($used.Count) feature key(s) referenced in code."

$missing = @($used.Keys | Where-Object { -not $en.ContainsKey($_) } | Sort-Object)

# Orphan scope = every prefix the feature code actually uses, minus the base panel's own prefixes.
$featurePrefixes = @{}
foreach ($k in $used.Keys) {
    $p = $k.Split('.')[0]
    if ($BasePrefixes -notcontains $p) { $featurePrefixes[$p] = $true }
}
Write-Host ("Feature prefixes in use: " + (($featurePrefixes.Keys | Sort-Object) -join ', '))

$orphaned = @($en.Keys | Where-Object {
    $featurePrefixes.ContainsKey($_.Split('.')[0]) -and -not $used.ContainsKey($_)
} | Sort-Object)

if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host "MISSING from en.txt ($($missing.Count)) - these render as raw key text in-game:"
    foreach ($k in $missing) { Write-Host ("  {0,-40} used in {1}" -f $k, ($used[$k] -join ', ')) }
}

if ($orphaned.Count -gt 0) {
    Write-Host ''
    Write-Host "ORPHANED in the locale files ($($orphaned.Count)) - defined but never referenced:"
    foreach ($k in $orphaned) { Write-Host "  $k" }
}

if ($missing.Count -eq 0 -and $orphaned.Count -eq 0) {
    Write-Host 'Feature locale keys and en.txt agree exactly.'
    exit 0
}
# Orphans are untidy but harmless; missing keys are a visible defect. Only the latter fails the gate.
exit ($(if ($missing.Count -gt 0) { 1 } else { 0 }))
