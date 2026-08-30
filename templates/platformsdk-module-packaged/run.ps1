#Requires -Version 7.0

<#
.SYNOPSIS
    One-shot happy path for the MyModule packaged module.

.DESCRIPTION
    A packaged module is a library plus a build driver, not a runnable
    app, so the happy path is: restore tools -> format check -> build ->
    conformance -> tests. `-Pack` adds the pack step, which runs both
    conformance layers first by construction (see Build.fs's target
    chain — Pack cannot be reached without them).

.PARAMETER SkipFormat
    Skip the Fantomas check.

.PARAMETER SkipBuild
    Skip the build (use the last one's output).

.PARAMETER SkipTests
    Skip the conformance test project.

.PARAMETER Pack
    Also pack the nupkg into the configured local feed.
#>

[CmdletBinding()]
param(
    [switch] $SkipFormat,
    [switch] $SkipBuild,
    [switch] $SkipTests,
    [switch] $Pack
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "==> dotnet tool restore" -ForegroundColor Cyan
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed." }

if (-not $SkipFormat) {
    Write-Host "==> dotnet fantomas --check src/ tests/" -ForegroundColor Cyan
    dotnet fantomas --check src/ tests/
    if ($LASTEXITCODE -ne 0) {
        throw "Fantomas check failed. Run 'dotnet run --project Build.fsproj -- Format' to fix."
    }
}

if (-not $SkipBuild) {
    Write-Host "==> dotnet run --project Build.fsproj -- Build" -ForegroundColor Cyan
    dotnet run --project Build.fsproj -- Build
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

Write-Host "==> dotnet run --project Build.fsproj -- VerifyPackagedModule" -ForegroundColor Cyan
dotnet run --project Build.fsproj -- VerifyPackagedModule
if ($LASTEXITCODE -ne 0) { throw "Packaged-module layout conformance failed." }

if (-not $SkipTests) {
    Write-Host "==> dotnet run --project Build.fsproj -- Test" -ForegroundColor Cyan
    dotnet run --project Build.fsproj -- Test
    if ($LASTEXITCODE -ne 0) { throw "Conformance tests failed." }
}

if ($Pack) {
    Write-Host "==> dotnet run --project Build.fsproj -- Pack" -ForegroundColor Cyan
    dotnet run --project Build.fsproj -- Pack
    if ($LASTEXITCODE -ne 0) { throw "Pack failed." }
}

Write-Host "Done." -ForegroundColor Green
