# Batch-append new locale keys to all 9 shipped locale files.
#
# Usage:  pwsh -NoProfile -File <thisfile> -Json <path-to-keys.json> [-Banner "wave 1"]
#
# Input JSON: an array of objects, each with the fields
#   key, en, de, fr, es, it, ptBR, pl, nl, sv
# (this is exactly the shape the implementation agents return, so their output can be piped straight in).
#
# Why this script exists rather than ad-hoc edits:
#  * ABSOLUTE paths only. On 2026-07-22 a scripted edit used [IO.File] with a RELATIVE path — those static
#    methods ignore PowerShell's Set-Location and resolve against [Environment]::CurrentDirectory — and
#    truncated all 9 locale files to 0 bytes. Everything here is rooted at $LocaleDir.
#  * Encoding discipline: the shipped files are UTF-8 *without* BOM, CRLF, trailing newline. Windows
#    PowerShell 5.1's -Encoding UTF8 writes a BOM; this script writes bytes itself so the edition can't
#    change the outcome.
#  * Glyph guard: Loc.ResolveFontSafety scans every active-locale value, and ONE character the Norse font
#    can't draw forces the whole panel onto Unity's fallback font — or, on systems with no OS fallback
#    font (Linux / Steam Deck), draws as a box. The allowed set lives in font-glyphs.ps1 and was read from
#    the fonts' own cmap tables, not guessed. Anything outside it aborts the run before a file is touched.
#  * Idempotent: a key already present in en.txt is skipped everywhere, so re-running after a partial
#    failure can't create duplicates.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Json,
    [string]$Banner = "new keys"
)

$ErrorActionPreference = 'Stop'

# Rooted at this script's own (absolute) folder, so the copy in the git mirror edits the mirror's locale
# files and the build folder's copy edits the build folder's — never the other tree. Same lookup as
# check-locales.ps1: the mirror keeps the panel source under AdminPanel\.
$Root = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath (Join-Path $Root 'AdminPanel\Localization')) { $Root = Join-Path $Root 'AdminPanel' }
$LocaleDir = Join-Path $Root 'Localization'
$Validator = Join-Path $PSScriptRoot 'check-locales.ps1'
if (-not (Test-Path -LiteralPath $LocaleDir)) { throw "No Localization folder under $Root" }
if (-not (Test-Path -LiteralPath $Validator)) { throw "Validator not found next to this script: $Validator" }

# field name in the JSON -> locale file stem
$Map = [ordered]@{
    en    = 'en'
    de    = 'de'
    fr    = 'fr'
    es    = 'es'
    it    = 'it'
    ptBR  = 'pt-BR'
    pl    = 'pl'
    nl    = 'nl'
    sv    = 'sv'
}

# Get-UnsafeGlyphs: the one glyph rule, shared with the check-locales.ps1 release gate.
. (Join-Path $PSScriptRoot 'font-glyphs.ps1')

function Test-Glyphs {
    param([string]$Text, [string]$Where)
    $bad = @(Get-UnsafeGlyphs $Text)
    if ($bad.Count -gt 0) {
        throw "Unsafe glyph(s) $($bad -join ', ') in $Where -- no Valheim font can draw them. Rewrite the string with characters from tools\font-glyphs.ps1."
    }
}

if (-not (Test-Path -LiteralPath $Json)) { throw "Key file not found: $Json" }
$entries = Get-Content -LiteralPath $Json -Raw -Encoding UTF8 | ConvertFrom-Json
if ($entries -isnot [System.Array]) { $entries = @($entries) }
if ($entries.Count -eq 0) { Write-Host 'No keys to append.'; exit 0 }

# ---- validate the whole batch before touching any file ----
$seen = @{}
foreach ($e in $entries) {
    if ([string]::IsNullOrWhiteSpace($e.key)) { throw "Entry with empty key." }
    if ($e.key -match '[=\r\n]') { throw "Key '$($e.key)' contains '=' or a newline." }
    if ($seen.ContainsKey($e.key)) { throw "Duplicate key in input: $($e.key)" }
    $seen[$e.key] = $true

    foreach ($field in $Map.Keys) {
        $val = [string]$e.$field
        if ([string]::IsNullOrEmpty($val)) { throw "Key '$($e.key)' is missing a value for '$field'." }
        if ($val -match '[\r\n]') { throw "Key '$($e.key)' value for '$field' contains a newline." }
        if ($val -ne $val.Trim()) {
            throw "Key '$($e.key)' value for '$field' has leading/trailing whitespace -- Loc.Parse trims it, so pad in code instead."
        }
        Test-Glyphs -Text $val -Where "$($e.key) [$field]"

        # Placeholder drift is a release gate in check-locales.ps1; catch it here with a clearer message.
        $ph = [regex]::Matches($val, '\{(\d+)\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
        if ($field -eq 'en') { $e | Add-Member -NotePropertyName '_ph' -NotePropertyValue ($ph -join ',') -Force }
        elseif ((($ph -join ',')) -ne $e._ph) {
            throw "Placeholder drift on '$($e.key)': en has [$($e._ph)] but $field has [$($ph -join ',')]."
        }
    }
}

# ---- skip keys already present (idempotent re-runs) ----
$enPath = Join-Path $LocaleDir 'en.txt'
if (-not (Test-Path -LiteralPath $enPath)) { throw "Missing $enPath" }
$existing = @{}
foreach ($line in [IO.File]::ReadAllLines($enPath, [Text.UTF8Encoding]::new($false))) {
    if ($line.Length -eq 0 -or $line[0] -eq '#') { continue }
    $eq = $line.IndexOf('=')
    if ($eq -gt 0) { $existing[$line.Substring(0, $eq)] = $true }
}
$fresh = @($entries | Where-Object { -not $existing.ContainsKey($_.key) })
$skipped = $entries.Count - $fresh.Count
if ($skipped -gt 0) { Write-Host "Skipping $skipped key(s) already present in en.txt." }
if ($fresh.Count -eq 0) { Write-Host 'Nothing new to append.'; exit 0 }

# ---- verify every target file exists and is non-empty BEFORE writing anything ----
foreach ($field in $Map.Keys) {
    $p = Join-Path $LocaleDir ($Map[$field] + '.txt')
    if (-not (Test-Path -LiteralPath $p)) { throw "Missing locale file: $p" }
    if ((Get-Item -LiteralPath $p).Length -eq 0) { throw "Locale file is EMPTY (truncation incident?): $p" }
}

# ---- append (bytes written directly: UTF-8 no BOM, CRLF) ----
$utf8NoBom = [Text.UTF8Encoding]::new($false)
foreach ($field in $Map.Keys) {
    $path = Join-Path $LocaleDir ($Map[$field] + '.txt')
    $sb = [Text.StringBuilder]::new()
    [void]$sb.Append("# ---- $Banner ----`r`n")
    foreach ($e in $fresh) { [void]$sb.Append("$($e.key)=$([string]$e.$field)`r`n") }

    $existingBytes = [IO.File]::ReadAllBytes($path)
    # Guarantee the previous content ends with a newline before appending.
    $needsNl = $existingBytes.Length -gt 0 -and $existingBytes[-1] -ne 0x0A
    $addition = $utf8NoBom.GetBytes(($(if ($needsNl) { "`r`n" } else { '' }) + $sb.ToString()))

    $fs = [IO.File]::Open($path, [IO.FileMode]::Append, [IO.FileAccess]::Write)
    try { $fs.Write($addition, 0, $addition.Length) } finally { $fs.Dispose() }
}
Write-Host "Appended $($fresh.Count) key(s) x 9 locales."

# ---- gate: the validator must be green, or the release is blocked ----
& pwsh -NoProfile -File $Validator
if ($LASTEXITCODE -ne 0) { throw "check-locales.ps1 FAILED (exit $LASTEXITCODE) -- fix before continuing." }
Write-Host 'Locale validator green.'
