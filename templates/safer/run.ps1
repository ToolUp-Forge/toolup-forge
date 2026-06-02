#Requires -Version 7.0

<#
.SYNOPSIS
    Build + launch the ToolUp.SAFER app — server + Vite dev server + browser.

.DESCRIPTION
    Stage-1 launcher: dotnet tool restore → npm install → dotnet build →
    dotnet fable → starts server + Vite in background → opens browser.

    The "Walking tour" section of README.md cites the run.ps1 commands a
    new consumer hits first; this file is the literal "what happens when
    I clone and hit run" entry point.

.PARAMETER SkipBuild
    Skip `dotnet build` (use last build's output). Useful when iterating
    on the client-side .fs files only (Fable will rebuild output itself).

.PARAMETER SkipFable
    Skip `dotnet fable` (use last Fable output). Server-only changes
    don't need a fresh transpile.

.PARAMETER ServerPort
    Server port. Default 5000. Must match the Vite proxy target in
    src/MyApp-Client/vite.config.mts.

.PARAMETER VitePort
    Vite dev-server port. Default 8080.

.PARAMETER NoOpenBrowser
    Skip the `Start-Process http://localhost:<VitePort>/` step (for CI / WSL).
#>

[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipFable,
    [int]$ServerPort = 5000,
    [int]$VitePort = 8080,
    [switch]$NoOpenBrowser
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# ─── Sibling launcher conventions ───────────────────────────────────────
# Per workspace CLAUDE.md "Sibling launcher conventions (mandate)":
# Node 22.x's npm.ps1 / npx.ps1 shims mangle args via Substring slice
# when invoked from inside another .ps1. Resolving npm.cmd / npx.cmd
# directly bypasses the broken shim.

function Invoke-Npm {
    [CmdletBinding()]
    param([Parameter(ValueFromRemainingArguments = $true)] $Arguments)
    $cmd = Get-Command npm.cmd -CommandType Application -ErrorAction Stop
    & $cmd.Source @Arguments
}

# ─── Build pipeline ─────────────────────────────────────────────────────

Write-Host "→ Restoring .NET tools..." -ForegroundColor Cyan
dotnet tool restore | Out-Null

if (-not $SkipBuild) {
    Write-Host "→ Building solution..." -ForegroundColor Cyan
    dotnet build "$PSScriptRoot/MyApp.sln" -c Debug
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
}

Push-Location "$PSScriptRoot/src/MyApp-Client"
try {
    Write-Host "→ Installing npm packages..." -ForegroundColor Cyan
    Invoke-Npm install --silent

    if (-not $SkipFable) {
        Write-Host "→ Transpiling F# → JavaScript (Fable)..." -ForegroundColor Cyan
        dotnet fable -o output
        if ($LASTEXITCODE -ne 0) { throw "dotnet fable failed" }
    }
}
finally {
    Pop-Location
}

# ─── Launch server + Vite in parallel ───────────────────────────────────

$env:SERVER_PORT = "$ServerPort"
$env:TOOLUP_PLATFORM_SURFACES = "anonymous"

Write-Host "→ Starting server on port $ServerPort..." -ForegroundColor Cyan
$serverProcess = Start-Process -FilePath "dotnet" `
    -ArgumentList @("run", "--project", "$PSScriptRoot/src/MyApp-Server/MyApp-Server.fsproj", "--no-build", "--no-restore") `
    -PassThru `
    -WindowStyle Hidden

Write-Host "→ Starting Vite dev server on port $VitePort..." -ForegroundColor Cyan
Push-Location "$PSScriptRoot/src/MyApp-Client"
try {
    $viteProcess = Start-Process -FilePath "cmd" `
        -ArgumentList @("/c", "npm", "run", "dev", "--", "--port", "$VitePort") `
        -PassThru `
        -WindowStyle Hidden
}
finally {
    Pop-Location
}

if (-not $NoOpenBrowser) {
    Start-Sleep -Seconds 3
    Start-Process "http://localhost:$VitePort/"
}

Write-Host ""
Write-Host "MyApp is running:" -ForegroundColor Green
Write-Host "  Server (Giraffe): http://localhost:$ServerPort/" -ForegroundColor Green
Write-Host "  Client (Vite):    http://localhost:$VitePort/" -ForegroundColor Green
Write-Host ""
Write-Host "Open a second tab at the Vite URL to see Tiny Chat sync across tabs." -ForegroundColor Yellow
Write-Host "Press Ctrl+C to stop. (Server PID: $($serverProcess.Id) · Vite PID: $($viteProcess.Id))" -ForegroundColor Yellow

# Block until Ctrl+C — kill children on exit.
try {
    while ($true) {
        Start-Sleep -Seconds 1
        if ($serverProcess.HasExited -or $viteProcess.HasExited) { break }
    }
}
finally {
    if (-not $serverProcess.HasExited) { Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue }
    if (-not $viteProcess.HasExited) { Stop-Process -Id $viteProcess.Id -Force -ErrorAction SilentlyContinue }
}
