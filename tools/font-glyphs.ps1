# The characters the panel's default font can draw — dot-sourced by check-locales.ps1 (release gate) and
# append-locale-keys.ps1 (pre-write guard), so the two can never disagree.
#
# Source of truth: the cmap table of Valheim's "Norsebold" font (what "Norse (auto)" picks), extracted from
# valheim_Data\resources.assets on Valheim 1.0.20 (2026-10-15). Not guessed: the old hand list called
# ⚠ ⚔ ★ ⬆ → "proven", but no Valheim font contains any of them. They only drew where the OS supplied a
# fallback font (Windows) and showed as boxes elsewhere (Linux / Steam Deck) — the Discord "⚠ is a □" report.
#
# A language whose letters are missing here (CJK, Arabic, ...) is not a string bug: Loc.ResolveFontSafety
# moves the whole panel to Unity's default font for it. This list is for SYMBOLS chosen by us.
#
# Re-extract after a Valheim update that touches fonts (UnityPy: Font objects in resources.assets ->
# m_FontData; fontTools: TTFont(...).getBestCmap()).

$script:NorseboldRanges = @(
    @(0x00A0, 0x00A3), @(0x00A5, 0x00A6), @(0x00A8, 0x00A9), @(0x00AB, 0x00B1), @(0x00B4, 0x00B4),
    @(0x00B6, 0x00B8), @(0x00BB, 0x00BB), @(0x00BF, 0x0137), @(0x0139, 0x0149), @(0x014C, 0x017E),
    @(0x01FC, 0x01FF), @(0x0218, 0x0219), @(0x02C6, 0x02C7), @(0x02D8, 0x02DD), @(0x0401, 0x0401),
    @(0x0404, 0x0404), @(0x0406, 0x0407), @(0x0410, 0x044F), @(0x0451, 0x0451), @(0x0454, 0x0454),
    @(0x0456, 0x0457), @(0x0490, 0x0491), @(0x16A0, 0x16F0), @(0x1E80, 0x1E85), @(0x1EF2, 0x1EF3),
    @(0x2013, 0x2014), @(0x2018, 0x201A), @(0x201C, 0x201E), @(0x2020, 0x2022), @(0x2026, 0x2026),
    @(0x2030, 0x2030), @(0x2039, 0x203A), @(0x2044, 0x2044), @(0x20AC, 0x20AC), @(0x2122, 0x2122),
    @(0x2212, 0x2212), @(0xFB01, 0xFB02)
)

# Returns 'U+XXXX' for every character in $Text the default font cannot draw (empty = safe).
# ASCII is always safe, matching Loc.ResolveFontSafety. Emoji arrive as surrogate pairs and are
# reported as one code point.
function Get-UnsafeGlyphs([string]$Text) {
    $bad = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $Text.Length; $i++) {
        $code = [int]$Text[$i]
        if ($code -lt 0x80) { continue }
        if ([char]::IsHighSurrogate($Text[$i]) -and $i + 1 -lt $Text.Length -and [char]::IsLowSurrogate($Text[$i + 1])) {
            $code = [char]::ConvertToUtf32($Text[$i], $Text[$i + 1]); $i++
        }
        $ok = $false
        foreach ($r in $script:NorseboldRanges) { if ($code -ge $r[0] -and $code -le $r[1]) { $ok = $true; break } }
        if (-not $ok) { $tag = 'U+{0:X4}' -f $code; if (-not $bad.Contains($tag)) { $bad.Add($tag) } }
    }
    return $bad
}
