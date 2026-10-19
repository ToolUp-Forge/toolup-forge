#Requires -Version 7.0

<#
.SYNOPSIS
    One-shot happy path for the MyDataPlatform application.

.DESCRIPTION
    Restore tools -> format check -> build -> the end-to-end test. With -Run,
    start the server afterwards on the synthetic data's port.

.PARAMETER SkipFormat
    Skip the Fantomas check.

.PARAMETER SkipBuild
    Skip the build (use the last one's output).

.PARAMETER SkipTests
    Skip the test project.

.PARAMETER Run
    Start the server once the checks pass, with the local run's settings
    (src/MyDataPlatform-Server/LocalRun.fs): a bootstrap team, a dev secrets
    key under data/, and forge's dev-only header identity.
#>

[CmdletBinding()]
param(
    [switch] $SkipFormat,
    [switch] $SkipBuild,
    [switch] $SkipTests,
    [switch] $Run
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$global:LASTEXITCODE = 0
Write-Host "==> dotnet tool restore" -ForegroundColor Cyan
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed." }

if (-not $SkipFormat) {
    $global:LASTEXITCODE = 0
    Write-Host "==> dotnet fantomas --check src/ tests/" -ForegroundColor Cyan
    dotnet fantomas --check src/ tests/
    if ($LASTEXITCODE -ne 0) { throw "Fantomas check failed. Run 'dotnet fantomas src/ tests/' to fix." }
}

if (-not $SkipBuild) {
    $global:LASTEXITCODE = 0
    Write-Host "==> dotnet build MyDataPlatform.slnx" -ForegroundColor Cyan
    dotnet build MyDataPlatform.slnx --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

if (-not $SkipTests) {
    $global:LASTEXITCODE = 0
    Write-Host "==> dotnet run --project tests/MyDataPlatform.Tests" -ForegroundColor Cyan
    dotnet run --project tests/MyDataPlatform.Tests/MyDataPlatform.Tests.fsproj --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

if ($Run) {
    Write-Host "==> dotnet run --project src/MyDataPlatform-Server" -ForegroundColor Cyan
    dotnet run --project src/MyDataPlatform-Server/MyDataPlatform-Server.fsproj --no-build -- --local
}

Write-Host "Done." -ForegroundColor Green
