#Requires -Version 7.0
<#
.SYNOPSIS
    The one-command consumer upgrade for the server tier's per-part namespaces (0.39.0):
    rewrites the `open` lines of a consumer tree, and derives the opens each file needs.

.DESCRIPTION
    Since 0.39.0 each part of the server tier owns a namespace: `ToolUp.Platform.Infra`,
    `.Config`, `.Scope`, `.Jobs`, `.Audit`, the umbrella's `.Compose` and `.Files`, and the
    remoting part's `ToolUp.Remoting.Server.Http` for its one platform-named module. A consumer
    that compiled against 0.38 with `open ToolUp.Platform` alone needs the opens of the parts it
    reaches. This script runs the Build project's `Codemod` target with `--phase 1198` over a
    consumer source directory: it rewrites an `open` of a path that moved, rewrites a qualified
    `ToolUp.Platform.X` path that moved, and inserts the part opens a file needs, DERIVED from
    the names its code uses (never a blanket list, and never a name inside a string or a
    comment), beside its `open ToolUp.Platform` or above its own opens, and it reports the opens
    the move looks to have left dead without deleting them. Idempotent; `-Check` prints the
    pending diff and changes nothing. Full account: docs/migrations/1198-subsystem-namespaces.md.

    The name table the codemod reads ships inside ToolUp.Platform.Build as an embedded
    resource. In this repository, `-Declarations` is the first half of the same move: it
    rewrites every server-tier file's `namespace` / `module` header to the part it is compiled
    in (read from the part project files, never the folder), and `-WriteTable` regenerates the
    shipped table from the sources. Consumers need neither.

.PARAMETER Path
    The consumer source directory to rewrite. Defaults to the current directory.

.PARAMETER Check
    Print the diff the codemod would apply and change nothing (exit 1 when anything is pending).

.PARAMETER Declarations
    Repository-internal: rewrite the server tier's declaration headers per the map.

.PARAMETER WriteTable
    Repository-internal: regenerate the shipped name table from the tier's sources.

.PARAMETER Map
    Repository-internal: write the per-file map (the migration doc's table) to this path.

.EXAMPLE
    pwsh ./dev-scripts/codemod-1198-namespaces.ps1 -Path C:\src\my-app -Check
    pwsh ./dev-scripts/codemod-1198-namespaces.ps1 -Path C:\src\my-app
#>
[CmdletBinding()]
param(
    [string] $Path = (Get-Location).Path,
    [switch] $Check,
    [switch] $Declarations,
    [switch] $WriteTable,
    [string] $Map
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$internal = @()
if ($WriteTable) { $internal += "--write-table" }
if ($Declarations) { $internal += "--apply-declarations" }
if ($Map) { $internal += @("--map", $Map) }

if ($internal.Count -gt 0) {
    & dotnet run --project (Join-Path $repoRoot "Build.fsproj") -- SubsystemNamespaces @internal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if ($WriteTable) {
        # The table is an embedded resource: the Build project must be rebuilt before the
        # codemod below reads the new one.
        & dotnet build (Join-Path $repoRoot "Build.fsproj") --nologo -v q
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

$target = (Resolve-Path $Path).Path
$codemodArgs = @("Codemod", $target, "--phase", "1198")
if ($Check) { $codemodArgs += "--check" }

& dotnet run --project (Join-Path $repoRoot "Build.fsproj") -- @codemodArgs
exit $LASTEXITCODE
