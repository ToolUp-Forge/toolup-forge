#Requires -Version 7.0
# Phase 256 — public-API surface triage (the MEASURING half of the shrink-before-freeze sweep).
#
# Reads every committed `api-baselines/<assembly>.approved.txt` (the Phase 175 renders), takes
# each TOP-LEVEL public type, and answers the two questions the sweep turns on:
#
#   1. Is the type's simple name referenced from any OTHER project in this repo (the
#      cross-package seam test — F# has no visibility between public and assembly-private
#      beyond InternalsVisibleTo, so a symbol a sibling package consumes MUST stay public)?
#   2. Is it referenced from any first-party consumer tree handed in via -ConsumerRoots
#      (Applications/*, cookbook-apps/*, Modules/*, the in-tree samples/ + templates/)?
#
# and tags each type with a name-pattern class (`entry-point`, `impl`, `helper`, `compiler`,
# `contract`). The output is a CSV — one row per top-level type — that the migration doc
# `docs/migrations/256-public-surface-minimization.md` summarises and the sweep's decisions
# cite. Re-run it after regenerating baselines to re-derive the classification; it is the
# auditable record the phase asks for, and it is derived rather than hand-maintained so a
# stale row cannot exist.
#
# What it measures, and its documented limits (read before trusting a number):
#   * Reference detection is by IDENTIFIER TOKEN, not by symbol resolution. A simple name shared
#     with an unrelated identifier reads as "referenced" — deliberately conservative: a false
#     "referenced" keeps a symbol public, a false "unreferenced" would be the dangerous error.
#   * A `Foo` module compiled with `ModuleSuffix` renders as `FooModule`; the trailing `Module`
#     is stripped before matching so the source spelling is what is looked for.
#   * Zero references is a CANDIDATE, never a verdict — a seam shipped ahead of its first
#     implementor is exactly the shape this flags (the same limit tools/ToolUp.DeadCode records).
#
# Usage (from the repo root):
#   pwsh ./dev-scripts/api-surface-triage.ps1 -Out api-surface-triage.csv
#   pwsh ./dev-scripts/api-surface-triage.ps1 -Out triage.csv -ConsumerRoots ../../Applications,../cookbook-apps
#   pwsh ./dev-scripts/api-surface-triage.ps1 -Out triage.csv -Markdown docs/reference/public-surface-triage.md

[CmdletBinding()]
param(
    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)] [string] $Out,
    [string[]] $ConsumerRoots = @(),
    # Optional: also write the human-readable snapshot (per-assembly table + the unreferenced
    # candidates by assembly) that docs/reference/public-surface-triage.md is generated from.
    [string] $Markdown
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$excludedDirs = '\\(bin|obj|node_modules|output|\.git|\.fable)\\'

function Get-Tokens([string[]] $files) {
    $set = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($f in $files) {
        $text = [System.IO.File]::ReadAllText($f)
        foreach ($m in [regex]::Matches($text, '[A-Za-z_][A-Za-z0-9_]*')) { [void] $set.Add($m.Value) }
    }
    # -NoEnumerate: an empty set would otherwise unroll to $null on the pipeline.
    Write-Output -NoEnumerate $set
}

# ── 1. Project roster: assembly name → project dir (mirrors PublicApiApproval.discoverPackable). ──
$projects = @{}
$exeProjects = [System.Collections.Generic.HashSet[string]]::new()
foreach ($fsproj in Get-ChildItem (Join-Path $RepoRoot 'src') -Recurse -Filter *.fsproj | Where-Object { $_.FullName -notmatch $excludedDirs }) {
    $text = Get-Content $fsproj.FullName -Raw
    $name = if ($text -match '<AssemblyName>([^<]+)</AssemblyName>') { $Matches[1].Trim() } else { $fsproj.BaseName }
    $projects[$name] = $fsproj.DirectoryName
    if ($text -match '<OutputType>\s*Exe\s*</OutputType>') { [void] $exeProjects.Add($name) }
}

# ── 2. Tokenise every project's sources once. ──
Write-Host "tokenising $($projects.Count) projects…"
$projectTokens = @{}
foreach ($name in $projects.Keys) {
    $files = Get-ChildItem $projects[$name] -Recurse -Include *.fs, *.fsi | Where-Object { $_.FullName -notmatch $excludedDirs } | Select-Object -ExpandProperty FullName
    $projectTokens[$name] = Get-Tokens $files
}

# In-tree consumers that are not packable projects: samples, templates, docs snippets, browser tests.
$inTreeConsumerDirs = @('samples', 'templates', 'docs-snippets', 'tests', 'evals', 'tools') | ForEach-Object { Join-Path $RepoRoot $_ } | Where-Object { Test-Path $_ }
$inTreeFiles = $inTreeConsumerDirs | ForEach-Object { Get-ChildItem $_ -Recurse -Include *.fs, *.fsx -ErrorAction SilentlyContinue } | Where-Object { $_.FullName -notmatch $excludedDirs } | Select-Object -ExpandProperty FullName
Write-Host "tokenising $($inTreeFiles.Count) in-tree consumer files…"
$inTreeTokens = Get-Tokens $inTreeFiles

$consumerTokens = @{}
# pwsh -File hands a comma-joined list through as ONE string; split it back.
$ConsumerRoots = $ConsumerRoots | ForEach-Object { $_ -split ',' } | Where-Object { $_ }
foreach ($root in $ConsumerRoots) {
    $resolved = Resolve-Path $root -ErrorAction SilentlyContinue
    if (-not $resolved) { Write-Warning "consumer root not found: $root"; continue }
    foreach ($consumer in Get-ChildItem $resolved -Directory) {
        if ($consumer.Name -eq 'toolup-forge') { continue } # the SDK itself is never its own consumer
        $files = Get-ChildItem $consumer.FullName -Recurse -Include *.fs, *.fsx -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch $excludedDirs } | Select-Object -ExpandProperty FullName
        if ($files) { $consumerTokens[$consumer.Name] = Get-Tokens $files }
    }
}
Write-Host "tokenised $($consumerTokens.Count) external consumers"

# ── 3. Walk the baselines. ──
$rows = [System.Collections.Generic.List[object]]::new()
foreach ($baseline in Get-ChildItem (Join-Path $RepoRoot 'api-baselines') -Filter *.approved.txt | Where-Object Name -ne 'doc-coverage.approved.txt') {
    $asm = $baseline.BaseName -replace '\.approved$', ''
    foreach ($line in Get-Content $baseline.FullName) {
        if ($line -notmatch '^(\S+) \((class|interface)\)$') { continue }
        $fullName = $Matches[1]
        if ($fullName -match '\+') { continue } # nested: rides its declaring type's verdict
        # Strip the CLR generic arity (`Foo`2`) — source spells the type `Foo`.
        $simple = (($fullName -split '\.')[-1]) -replace '`\d+$', ''
        $lookup = if ($simple -match '^(.+)Module$' -and $simple.Length -gt 6) { $Matches[1] } else { $simple }

        $class =
            if ($simple -eq 'Program' -and $exeProjects.Contains($asm)) { 'entry-point' }
            elseif ($simple -match 'Impl$') { 'impl' }
            elseif ($simple -match '(Helpers?|Util|Utils|Utilities|InternalUtilities)$' -or $simple -match '^Internal') { 'helper' }
            elseif ($simple -match '^<|@|\$') { 'compiler' }
            else { 'contract' }

        $inRepo = @()
        foreach ($p in $projectTokens.Keys) {
            if ($p -eq $asm) { continue }
            if ($projectTokens[$p].Contains($lookup)) { $inRepo += $p }
        }
        $consumers = @()
        foreach ($c in $consumerTokens.Keys) { if ($consumerTokens[$c].Contains($lookup)) { $consumers += $c } }

        $rows.Add([pscustomobject]@{
            Assembly       = $asm
            Type           = $fullName
            Class          = $class
            InRepoRefs     = $inRepo.Count
            InRepoProjects = ($inRepo | Sort-Object) -join ';'
            InTreeConsumer = $inTreeTokens.Contains($lookup)
            ConsumerRefs   = $consumers.Count
            Consumers      = ($consumers | Sort-Object) -join ';'
        })
    }
}

$rows | Export-Csv -NoTypeInformation -Path $Out
Write-Host "wrote $($rows.Count) rows to $Out"
$rows | Group-Object Class | Sort-Object Count -Descending | ForEach-Object { "{0,6}  {1}" -f $_.Count, $_.Name }
"unreferenced anywhere: $(@($rows | Where-Object { $_.InRepoRefs -eq 0 -and -not $_.InTreeConsumer -and $_.ConsumerRefs -eq 0 }).Count)"

if ($Markdown) {
    $unref = $rows | Where-Object { $_.InRepoRefs -eq 0 -and -not $_.InTreeConsumer -and $_.ConsumerRefs -eq 0 }
    $md = [System.Collections.Generic.List[string]]::new()
    $md.Add('# Public-API surface triage (generated)')
    $md.Add('')
    $md.Add("Generated by ``dev-scripts/api-surface-triage.ps1`` on $(Get-Date -Format 'yyyy-MM-dd') from the committed ``api-baselines/*.approved.txt`` renders")
    $md.Add("(Phase 175) and the sources of $($projects.Count) in-repo projects, the in-tree samples/templates/snippets, and $($consumerTokens.Count) external consumer trees.")
    $md.Add('**Do not edit by hand** — re-run the script. What it measures and its limits are in the script header; the decisions taken over')
    $md.Add('it are in `docs/migrations/256-public-surface-minimization.md`.')
    $md.Add('')
    $md.Add("Top-level public types: **$($rows.Count)**. Referenced from another in-repo project: **$(@($rows | Where-Object { $_.InRepoRefs -gt 0 }).Count)**. Referenced from an external first-party consumer: **$(@($rows | Where-Object { $_.ConsumerRefs -gt 0 }).Count)**. Referenced nowhere the script can see: **$($unref.Count)** (candidates for a domain owner's judgement, never verdicts).")
    $md.Add('')
    $md.Add('## Per assembly')
    $md.Add('')
    $md.Add('| Assembly | Public types | Used by another package | Used by an external consumer | Unreferenced (candidates) |')
    $md.Add('|---|---:|---:|---:|---:|')
    foreach ($grp in ($rows | Group-Object Assembly | Sort-Object Count -Descending)) {
        $inRepo = @($grp.Group | Where-Object { $_.InRepoRefs -gt 0 }).Count
        $ext = @($grp.Group | Where-Object { $_.ConsumerRefs -gt 0 }).Count
        $cand = @($grp.Group | Where-Object { $_.InRepoRefs -eq 0 -and -not $_.InTreeConsumer -and $_.ConsumerRefs -eq 0 }).Count
        $md.Add("| $($grp.Name) | $($grp.Count) | $inRepo | $ext | $cand |")
    }
    $md.Add('')
    $md.Add('## Unreferenced candidates, by assembly')
    $md.Add('')
    $md.Add('A type listed here is public, and no identifier of its name occurs in any other project, sample, template, snippet or consumer')
    $md.Add('tree the run could see. That is where a review starts, not where it ends: a seam shipped ahead of its first implementor, a')
    $md.Add('record a consumer only ever receives, and an options type set through a builder all land here legitimately.')
    $md.Add('')
    foreach ($grp in ($unref | Group-Object Assembly | Sort-Object Count -Descending)) {
        $names = ($grp.Group | ForEach-Object { '`' + $_.Type + '`' } | Sort-Object) -join ', '
        $md.Add("- **$($grp.Name)** ($($grp.Count)): $names")
    }
    $md.Add('')
    # LF, no BOM — the repo pins canonical text to LF (.gitattributes) and the snapshot is committed.
    [System.IO.File]::WriteAllText($Markdown, (($md -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
    Write-Host "wrote markdown snapshot to $Markdown"
}