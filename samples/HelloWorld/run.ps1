#!/usr/bin/env pwsh
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

<#
.SYNOPSIS
    Run the HelloWorld sample: the server and the client, signed in as the
    development identity.

.DESCRIPTION
    One command for both halves. It restores the tools and the client's npm
    tree, compiles the client with Fable once, then starts:

      * the server (Debug) on the sample's claimed server port, with
        TOOLUP_PROFILE=helloworld-dev-identity: the team surface, with the
        header identity `dev-admin` as platform admin and owner of the
        bootstrap team "Hello World team";
      * `dotnet fable watch`, recompiling the client on every save;
      * the Vite dev server on the sample's claimed client port, proxying
        /api to the server.

    Open the URL it prints. Ctrl+C stops all three.

    The development identity exists only in a Debug build of the server, and
    only when the profile is selected; see the sample's README.

.PARAMETER SkipInstall
    Skip `dotnet tool restore` and `npm ci` (they have run before).
#>
[CmdletBinding()]
param(
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'

function Invoke-Npm {
    # Node 22.x ships an npm.ps1 shim that rebuilds args from the caller's command-line text via
    # Substring(InvocationName.Length). Called from inside another .ps1 as `& npm ci ...`, the
    # 3-char slice eats `& n` and npm sees `pm ci ...` — `Unknown command: "pm"`. Resolving npm.cmd
    # directly skips the shim.
    #
    # `Get-Command npm.cmd` returns EVERY npm.cmd on PATH — typically two (Program Files installer
    # shim + %APPDATA%\npm self-update shim). `$cmd.Source` would then be an array and `& $cmd.Source`
    # concatenates the paths into one bogus string. Pin to the first match — both shims behave alike.
    [CmdletBinding()]
    param([Parameter(ValueFromRemainingArguments = $true)] $Arguments)
    $cmd = Get-Command npm.cmd -CommandType Application -ErrorAction Stop | Select-Object -First 1
    & $cmd.Source @Arguments
}

function Invoke-Npx {
    # Same shim, same bug — npx.ps1 mirrors npm.ps1. Same multi-shim hazard — pin to the first match.
    [CmdletBinding()]
    param([Parameter(ValueFromRemainingArguments = $true)] $Arguments)
    $cmd = Get-Command npx.cmd -CommandType Application -ErrorAction Stop | Select-Object -First 1
    & $cmd.Source @Arguments
}

function Invoke-NpmAnyOs {
    # Windows takes the canonical helper (the shim bug it dodges is Windows-only); elsewhere resolve the
    # npm APPLICATION on PATH explicitly — never a bare `& npm`. Pin to the first match, as above.
    param([Parameter(ValueFromRemainingArguments = $true)] $Arguments)
    if ($IsWindows) {
        Invoke-Npm @Arguments
    }
    else {
        $cmd = Get-Command npm -CommandType Application -ErrorAction Stop | Select-Object -First 1
        & $cmd.Source @Arguments
    }
}

function Invoke-NpxAnyOs {
    # Windows takes the canonical helper (the shim bug it dodges is Windows-only); elsewhere resolve the
    # npx APPLICATION on PATH explicitly — never a bare `& npx`. Pin to the first match, as above.
    param([Parameter(ValueFromRemainingArguments = $true)] $Arguments)
    if ($IsWindows) {
        Invoke-Npx @Arguments
    }
    else {
        $cmd = Get-Command npx -CommandType Application -ErrorAction Stop | Select-Object -First 1
        & $cmd.Source @Arguments
    }
}

function Assert-Stage([string]$Stage) {
    if ($global:LASTEXITCODE -ne 0) {
        Write-Error "run.ps1: $Stage failed (exit $global:LASTEXITCODE)."
        exit $global:LASTEXITCODE
    }
}

function Assert-PortFree([int]$Port, [string]$Role) {
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try { $probe.Start() }
    catch {
        Write-Error "run.ps1: port $Port, the sample's claimed $Role port (ports.json), is in use. Stop whatever holds it and run again."
        exit 1
    }
    finally { $probe.Stop() }
}

$sample = $PSScriptRoot
$client = Join-Path $sample 'HelloWorld.Client'
$ports = Get-Content -Raw (Join-Path $sample 'ports.json') | ConvertFrom-Json
$serverPort = [int]$ports.server[0]
$clientPort = [int]$ports.client[0]

Assert-PortFree $serverPort 'server'
Assert-PortFree $clientPort 'client'

# One switch for both halves: the server selects its profile from it, and
# the client's vite.config.mts reads it to define the matching identity.
$env:TOOLUP_PROFILE = 'helloworld-dev-identity'
$env:SERVER_PORT = "$serverPort"

if (-not $SkipInstall) {
    $global:LASTEXITCODE = 0
    dotnet tool restore
    Assert-Stage 'dotnet tool restore'

    $global:LASTEXITCODE = 0
    Push-Location $client
    try { Invoke-NpmAnyOs ci --no-fund --no-audit } finally { Pop-Location }
    Assert-Stage 'npm ci'
}

# One compile before anything serves the page, so output/Client.js exists
# when Vite first asks for it.
$global:LASTEXITCODE = 0
Push-Location $client
try { dotnet fable -o output } finally { Pop-Location }
Assert-Stage 'dotnet fable'

$started = @()
try {
    # The server runs from the sample directory, so its local store is
    # samples/HelloWorld/data (git-ignored).
    $started += Start-Process dotnet -PassThru -NoNewWindow -WorkingDirectory $sample `
        -ArgumentList @('run', '--project', (Join-Path $sample 'HelloWorld.Server/HelloWorld.Server.fsproj'))

    $started += Start-Process dotnet -PassThru -NoNewWindow -WorkingDirectory $client `
        -ArgumentList @('fable', 'watch', '-o', 'output')

    Write-Host ""
    Write-Host "HelloWorld: open http://localhost:$clientPort (the server is on http://localhost:$serverPort). Ctrl+C stops it."
    Write-Host ""

    $global:LASTEXITCODE = 0
    Push-Location $client
    try { Invoke-NpxAnyOs vite } finally { Pop-Location }
}
finally {
    foreach ($p in $started) {
        if ($p -and -not $p.HasExited) {
            # The whole tree: `dotnet run` and `dotnet fable watch` each start a child.
            $p.Kill($true)
        }
    }
}
