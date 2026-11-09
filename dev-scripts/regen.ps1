#Requires -Version 7.0
<#
.SYNOPSIS
    Regenerate every generated artefact of one approval stem, from projections.json.

.DESCRIPTION
    projections.json at the repository root lists every generated artefact:
    its path, what renders it, the stem that approves a regeneration, the check
    that holds it, and how to regenerate it. This script takes the rows of one
    stem and runs each distinct regeneration once:

      a test pack     run under TOOLUP_APPROVE=<stem>, filtered to the lists
                      the row names; the pack writes the artefact instead of
                      comparing it
      a target        dotnet run --project Build.fsproj -- <target> <args>
      a script        pwsh -NoProfile -File <script>
      a build         dotnet build <project>; its generator writes the files

    A regeneration run proves nothing about the artefact it wrote (a pack in
    approval mode passes trivially, and some fail by design after rewriting):
    re-run the row's check WITHOUT TOOLUP_APPROVE, then commit the artefact with
    the change that moved it.

.EXAMPLE
    pwsh ./dev-scripts/regen.ps1 config-reference

.EXAMPLE
    pwsh ./dev-scripts/regen.ps1 api -Name ToolUp.Platform.Core,ToolUp.Platform.Server

.EXAMPLE
    pwsh ./dev-scripts/regen.ps1 -List
#>
param(
    [Parameter(Position = 0)]
    [string] $Stem,
    # Names within the stem (the api stem's assembly names); none means all.
    [string[]] $Name = @(),
    [switch] $List
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot/..

$registry = Get-Content projections.json -Raw | ConvertFrom-Json

if ($List -or -not $Stem) {
    $registry.projections | Group-Object stem | Sort-Object Name | ForEach-Object {
        "{0,-24} {1}" -f $_.Name, (($_.Group | ForEach-Object path) -join ", ")
    }
    if (-not $Stem) { exit 0 }
}

$rows = @($registry.projections | Where-Object { $_.stem -eq $Stem })
if ($rows.Count -eq 0) {
    Write-Error "regen: projections.json has no row with stem '$Stem'. Run with -List for the stems."
    exit 2
}

$approve = if ($Name.Count -gt 0) { ($Name | ForEach-Object { "${Stem}:$_" }) -join "," } else { $Stem }

# One run per distinct regeneration: several rows of a stem share one.
$runs = $rows | Group-Object { $_.regen | ConvertTo-Json -Compress } | ForEach-Object { $_.Group[0].regen }
$failed = @()

foreach ($regen in $runs) {
    $global:LASTEXITCODE = 0

    if ($regen.project) {
        $testArgs = @()
        if ($regen.filter) { $testArgs = @("--", "--filter-test-list", $regen.filter) }
        Write-Host "▶ regen ${Stem}: $($regen.project) under TOOLUP_APPROVE=$approve" -ForegroundColor Cyan
        $env:TOOLUP_APPROVE = $approve
        try {
            dotnet run --project $regen.project @testArgs
        }
        finally {
            Remove-Item Env:TOOLUP_APPROVE -ErrorAction SilentlyContinue
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  exit $LASTEXITCODE — a regeneration run may fail by design after rewriting; verify below." -ForegroundColor Yellow
        }
    }
    elseif ($regen.target) {
        $targetArgs = @()
        if ($regen.args) { $targetArgs = @($regen.args) }
        Write-Host "▶ regen ${Stem}: Build.fsproj -- $($regen.target) $($targetArgs -join ' ')" -ForegroundColor Cyan
        dotnet run --project Build.fsproj -- $regen.target @targetArgs
        if ($LASTEXITCODE -ne 0) { $failed += "$($regen.target) (exit $LASTEXITCODE)" }
    }
    elseif ($regen.script) {
        Write-Host "▶ regen ${Stem}: $($regen.script)" -ForegroundColor Cyan
        pwsh -NoProfile -File $regen.script
        if ($LASTEXITCODE -ne 0) { $failed += "$($regen.script) (exit $LASTEXITCODE)" }
    }
    elseif ($regen.build) {
        Write-Host "▶ regen ${Stem}: dotnet build $($regen.build)" -ForegroundColor Cyan
        dotnet build $regen.build --nologo -v q
        if ($LASTEXITCODE -ne 0) { $failed += "$($regen.build) (exit $LASTEXITCODE)" }
    }
    else {
        $failed += "a row of stem '$Stem' names no regeneration"
    }
}

if ($failed.Count -gt 0) {
    Write-Error ("regen ${Stem}: " + ($failed -join "; "))
    exit 1
}

Write-Host "Regenerated stem '$Stem': $(($rows | ForEach-Object path) -join ', '). Re-run each row's check without TOOLUP_APPROVE before committing." -ForegroundColor Green
$global:LASTEXITCODE = 0
exit 0
