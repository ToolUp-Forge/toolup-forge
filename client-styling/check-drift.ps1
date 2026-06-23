<#
.SYNOPSIS
    Fails if any ToolUp client has drifted from the canonical Tailwind v4
    contract in client-styling/tailwind/.
.DESCRIPTION
    The canonical index.css region (between the >>> markers) and the exact
    toolchain pin in deps.json are the single source of truth. Every
    consumer client embeds the marked region verbatim and pins exactly
    those versions. This script:

      1. Discovers consumer clients: any *index.css anywhere under the
         workspace root that contains the canonical start marker
         (skips node_modules / bin / obj / output / .vite / .git).
      2. Diffs each consumer's marked region against the canonical region
         (CRLF-normalised, ordinal compare).
      3. Checks the sibling client package.json carries exactly the
         canonical devDependency pins and none of the v3/PostCSS deps or
         config files that v4 + @tailwindcss/vite makes obsolete.

    Exit 0 = in sync. Exit 1 = drift (details printed). Read-only.
.EXAMPLE
    pwsh -File toolup-forge/client-styling/check-drift.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$styleDir = Join-Path $PSScriptRoot 'tailwind'
$canonCss = Join-Path $styleDir 'index.css'
$depsFile = Join-Path $styleDir 'deps.json'
$canonMjs = Join-Path $styleDir 'vite-fable-tailwind.mjs'
$wsRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path

$START = '>>> @toolup/tailwind canonical'
$END   = '>>> end @toolup/tailwind canonical <<<'

function Get-Region([string] $text) {
    $t = $text -replace "`r`n", "`n"
    $s = $t.IndexOf($START)
    $e = $t.IndexOf($END)
    if ($s -lt 0 -or $e -lt 0) { return $null }
    $ls = $t.LastIndexOf("`n", $s)
    $startIdx = if ($ls -lt 0) { 0 } else { $ls + 1 }
    $le = $t.IndexOf("`n", $e)
    $endIdx = if ($le -lt 0) { $t.Length } else { $le }
    return $t.Substring($startIdx, $endIdx - $startIdx).TrimEnd()
}

$canonRegion = Get-Region (Get-Content -Raw -LiteralPath $canonCss)
if (-not $canonRegion) { Write-Error "Canonical markers not found in $canonCss"; exit 1 }
$canonMjsText = ((Get-Content -Raw -LiteralPath $canonMjs) -replace "`r`n", "`n").TrimEnd()
$deps = Get-Content -Raw -LiteralPath $depsFile | ConvertFrom-Json
$pinTw  = $deps.devDependencies.'tailwindcss'
$pinVit = $deps.devDependencies.'@tailwindcss/vite'
$banned = @($deps.remove.devDependencies) + @($deps.remove.files)

$noise = '[\\/](node_modules|bin|obj|output|\.vite|\.git)[\\/]'
$drift = @()

$cssFiles =
    Get-ChildItem -Path $wsRoot -Recurse -Filter 'index.css' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch $noise -and $_.FullName -ne $canonCss }

foreach ($f in $cssFiles) {
    $raw = Get-Content -Raw -LiteralPath $f.FullName
    if ($raw -notlike "*$START*") { continue }   # not a canonical consumer
    $rel = $f.FullName.Substring($wsRoot.Length).TrimStart('\', '/')
    $region = Get-Region $raw
    if (-not [string]::Equals($region, $canonRegion, [System.StringComparison]::Ordinal)) {
        $drift += "CSS  $rel : marked region differs from canonical"
    }

    $pkg = Join-Path (Split-Path -Parent $f.FullName) 'package.json'
    if (Test-Path -LiteralPath $pkg) {
        $j = Get-Content -Raw -LiteralPath $pkg | ConvertFrom-Json
        $dd = $j.devDependencies
        if ($dd.'tailwindcss' -ne $pinTw) { $drift += "PKG  $rel : tailwindcss '$($dd.'tailwindcss')' != pinned '$pinTw'" }
        if ($dd.'@tailwindcss/vite' -ne $pinVit) { $drift += "PKG  $rel : @tailwindcss/vite '$($dd.'@tailwindcss/vite')' != pinned '$pinVit'" }
        foreach ($b in $banned) {
            if ($b -like '*.js' -or $b -like '*.cjs') {
                if (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $f.FullName) $b)) {
                    $drift += "FILE $rel : obsolete '$b' still present (delete it under v4+@tailwindcss/vite)"
                }
            } else {
                $name = ($b -replace '@\^?\d.*$', '')
                if ($dd.PSObject.Properties.Name -contains $name -or $j.dependencies.PSObject.Properties.Name -contains $name) {
                    $drift += "PKG  $rel : obsolete dep '$name' still declared (v4 folds it into the engine)"
                }
            }
        }
    }

    $mjs = Join-Path (Split-Path -Parent $f.FullName) 'vite-fable-tailwind.mjs'
    if (-not (Test-Path -LiteralPath $mjs)) {
        $drift += "MJS  $rel : canonical vite-fable-tailwind.mjs missing (copy it next to vite.config.mts + add fableTailwindGitignore())"
    } elseif (-not [string]::Equals(((Get-Content -Raw -LiteralPath $mjs) -replace "`r`n", "`n").TrimEnd(), $canonMjsText, [System.StringComparison]::Ordinal)) {
        $drift += "MJS  $rel : vite-fable-tailwind.mjs differs from canonical"
    }
}

if ($drift.Count -eq 0) {
    Write-Host "Tailwind contract: all consumers in sync with canonical." -ForegroundColor Green
    exit 0
}
Write-Host "Tailwind contract DRIFT:" -ForegroundColor Red
$drift | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
Write-Host ""
Write-Host "Re-stamp the drifted consumer(s) from client-styling/tailwind/." -ForegroundColor Red
exit 1
