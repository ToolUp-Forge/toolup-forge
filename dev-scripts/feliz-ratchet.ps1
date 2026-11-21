#Requires -Version 7.0
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

<#
.SYNOPSIS
    The Feliz ratchet: hold the committed census against the tree, or rewrite it.

.DESCRIPTION
    feliz-census.json, at the repository root, counts per project the source
    files carrying an `open Feliz` line (test files apart), and per assembly
    the public-API baseline lines naming ReactElement. A source or baseline
    cell may only fall. docs/platform/budgets.md says what is counted and what
    is not.

    Without -Write this runs the check the gate runs (VerifyAll reaches it
    through the Build test pack): red on a rise, and red on a fall the census
    does not yet record.

    With -Write it rewrites the census from the tree. It refuses, writing
    nothing, when that would raise a source or baseline cell: removing Feliz
    is the only way down, and a hand edit of the committed file, reviewed, is
    the only way up. A test-file cell may rise. Commit the rewritten file in
    the same change as the files that moved it.

    Both are the FelizRatchet target of the build driver; this script is the
    short spelling.

.PARAMETER Write
    Rewrite feliz-census.json from the tree instead of checking it.

.EXAMPLE
    pwsh ./dev-scripts/feliz-ratchet.ps1
    pwsh ./dev-scripts/feliz-ratchet.ps1 -Write
#>

[CmdletBinding()]
param(
    [switch] $Write
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)

$driverArgs = @('run', '--project', 'Build.fsproj', '--', 'FelizRatchet')
if ($Write) { $driverArgs += '--write' }

$global:LASTEXITCODE = 0
& dotnet @driverArgs
exit $LASTEXITCODE
