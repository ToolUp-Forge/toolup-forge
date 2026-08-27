#Requires -Version 7.0
# Phase 213 — Lighthouse / Core-Web-Vitals budget gate (the MEASURING half).
#
# Builds a public-rendering site, serves it on a throwaway port, drives
# Lighthouse over the page set the budget declares, samples the cheap
# server-side companion signal, and hands everything to the deciding half —
# the VerifyCoreWebVitalsBudget FAKE target, whose parser and check live in
# src/ToolUp.Platform.Build/Build/SDK.CoreWebVitalsBudget.fs. Non-zero exit
# on any breach.
#
# The split is deliberate: the thresholds and the comparison are committed
# F# with a test pack over them, so widening a budget is a reviewable file
# change and a defect in the comparison is caught by VerifyAll. This script
# owns only the parts that need a browser and a socket.
#
# Local run (needs Node + a Chromium-family browser):
#
#   pwsh ./dev-scripts/cwv-budget-gate.ps1
#
# Evaluate reports a previous run (or CI) already produced, with no build,
# no server and no browser — the fast inner loop while editing a budget:
#
#   pwsh ./dev-scripts/cwv-budget-gate.ps1 -EvaluateOnly `
#       -Budget src/ToolUp.Platform.Build.Tests/fixtures/cwv/fixture-budget.json `
#       -ReportsDirectory src/ToolUp.Platform.Build.Tests/fixtures/cwv/within-budget `
#       -ServerMetrics src/ToolUp.Platform.Build.Tests/fixtures/cwv/server-metrics.json
#
# Swap `within-budget` for `breaching` to watch the gate go red against the
# deliberately-degraded fixture set. The budget and the reports must cover the
# same pages — the default budget covers the sample site, the fixture budget
# covers the fixtures.
#
# Lighthouse resolves its browser through CHROME_PATH when Chrome is not on
# the default install path — on a machine carrying only Edge, set
# $env:CHROME_PATH to msedge.exe before running.

[CmdletBinding()]
param(
    # The declarative budget. Its `pages` array is the page set measured —
    # there is no second list to keep in step with it.
    [string] $Budget = "samples/PublicSite/cwv-budget.json",

    # The site project served for the run.
    [string] $Site = "samples/PublicSite/PublicSite.fsproj",

    # Where Lighthouse JSON reports are written (and, with -EvaluateOnly,
    # read from).
    [string] $ReportsDirectory = "artifacts/cwv-reports",

    # Skip build + serve + measure; evaluate the reports already present in
    # -ReportsDirectory.
    [switch] $EvaluateOnly,

    # Reuse an existing build of the site project.
    [switch] $SkipBuild,

    # A server-counter snapshot to cross-check. Defaults to the one this
    # script samples during a full run.
    [string] $ServerMetrics,

    # Pin the served port instead of taking a free ephemeral one. Provided
    # for a constrained CI network; leave unset in normal use.
    [int] $Port = 0
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot/..

# Sibling launcher conventions — see the workspace CLAUDE.md
# "Sibling launcher conventions (mandate)". Copy-pasted from the canonical
# body there; do not diverge without updating that document.
function Invoke-Npx {
    # Node 22.x ships an npx.ps1 shim that rebuilds args from the caller's
    # command-line text via Substring(InvocationName.Length). Called from
    # inside another .ps1 as `& npx ...`, the slice eats the leading
    # characters and npx sees a mangled command. Resolving npx.cmd directly
    # skips the shim.
    #
    # `Get-Command npx.cmd` returns EVERY npx.cmd on PATH — typically two
    # (Program Files installer shim + %APPDATA%\npm self-update shim).
    # `$cmd.Source` would then be an array and `& $cmd.Source` concatenates
    # the paths into one bogus string. Pin to the first match — both shims
    # behave alike.
    [CmdletBinding()]
    param([Parameter(ValueFromRemainingArguments = $true)] $Arguments)
    $cmd = Get-Command npx.cmd -CommandType Application -ErrorAction Stop | Select-Object -First 1
    & $cmd.Source @Arguments
}

# Ports the workspace declares reserved-unsafe in every estate: 5040
# (Windows CDPSvc claims it intermittently), 6000 (browsers hardcode it as
# restricted — the X11 default), 7680 (Windows Delivery Optimization). A
# free-port draw that lands on one is redrawn rather than used.
$reservedUnsafePorts = @(5040, 6000, 7680)

function Get-ThrowawayPort {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $candidate = $listener.LocalEndpoint.Port
        $listener.Stop()

        if ($reservedUnsafePorts -notcontains $candidate) {
            return $candidate
        }
    }

    throw "Could not draw a free ephemeral port outside the reserved-unsafe set ($($reservedUnsafePorts -join ', '))."
}

function Get-ReportFileName([string] $pagePath) {
    if ($pagePath -eq "/") { return "root.json" }
    return ($pagePath.Trim("/") -replace "[^A-Za-z0-9._-]", "-") + ".json"
}

# ---------------------------------------------------------------------------
# Resolve the budget. The page set is read from the budget itself so the
# measured pages and the asserted pages cannot drift apart.
# ---------------------------------------------------------------------------

if (-not (Test-Path $Budget)) {
    throw "Budget file '$Budget' does not exist."
}

$budgetDocument = Get-Content -Raw -Path $Budget | ConvertFrom-Json
$pages = @($budgetDocument.pages)

if ($pages.Count -eq 0) {
    throw "Budget file '$Budget' declares no pages."
}

$reportsFull = [System.IO.Path]::GetFullPath($ReportsDirectory)

if (-not $EvaluateOnly) {
    New-Item -ItemType Directory -Force -Path $reportsFull | Out-Null
    Get-ChildItem -Path $reportsFull -Filter *.json -ErrorAction SilentlyContinue | Remove-Item -Force

    # -- build -------------------------------------------------------------
    if (-not $SkipBuild) {
        Write-Host "==> building $Site" -ForegroundColor Cyan
        dotnet build $Site --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet build '$Site' failed." }
    }

    # -- serve on a throwaway port ----------------------------------------
    $servedPort = if ($Port -gt 0) { $Port } else { Get-ThrowawayPort }
    $origin = "http://127.0.0.1:$servedPort"

    # SERVER_PORT overrides ServerConfig.Port at compose time, so the sample
    # binds wherever this run drew — no source edit, no fixed port, and two
    # concurrent gate runs never contend.
    $env:SERVER_PORT = "$servedPort"

    Write-Host "==> serving $Site on $origin" -ForegroundColor Cyan
    $server = Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", $Site, "--no-build") `
        -PassThru -NoNewWindow

    try {
        # -- readiness -----------------------------------------------------
        $ready = $false
        for ($i = 0; $i -lt 60; $i++) {
            if ($server.HasExited) {
                throw "The site process exited with code $($server.ExitCode) before it began serving."
            }

            try {
                Invoke-WebRequest -Uri $origin -UseBasicParsing -TimeoutSec 5 | Out-Null
                $ready = $true
                break
            }
            catch {
                Start-Sleep -Seconds 1
            }
        }

        if (-not $ready) { throw "The site did not begin serving on $origin within 60s." }

        # -- server-side companion signal ----------------------------------
        #
        # The conditional-GET 304/200 split is observable from the wire: one
        # cold GET per page, then one revalidation carrying the ETag and
        # Last-Modified the cold response returned. That mirrors what the
        # server's own counter records, because it tags every rendered
        # response 200 and every not-modified response 304 — so a collapsed
        # rate here is exactly the crawl-budget regression the counter would
        # report.
        #
        # Render duration is NOT sampled from the wire: a round-trip is not
        # the server's render time, and recording one as the other would be
        # a fabricated signal. A deployment that exposes its metrics sink
        # writes `renderMsMax` into this snapshot and the budget's
        # `serverSignals.maxRenderMs` then bites.
        $notModified = 0
        $fullBody = 0

        foreach ($page in $pages) {
            $url = "$origin$page"
            $cold = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30
            $fullBody++

            $revalidationHeaders = @{}
            if ($cold.Headers.ETag) { $revalidationHeaders["If-None-Match"] = ($cold.Headers.ETag -join ",") }
            if ($cold.Headers["Last-Modified"]) { $revalidationHeaders["If-Modified-Since"] = ($cold.Headers["Last-Modified"] -join ",") }

            if ($revalidationHeaders.Count -gt 0) {
                $revalidated = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30 `
                    -Headers $revalidationHeaders -SkipHttpErrorCheck

                if ($revalidated.StatusCode -eq 304) { $notModified++ } else { $fullBody++ }
            }
        }

        $snapshotPath = Join-Path $reportsFull "server-metrics.json"
        @{ conditionalGet = @{ "304" = $notModified; "200" = $fullBody } } |
            ConvertTo-Json -Depth 5 | Set-Content -Path $snapshotPath -Encoding utf8

        Write-Host "==> conditional-GET probe: $notModified x 304, $fullBody x 200" -ForegroundColor Cyan

        # -- Lighthouse ----------------------------------------------------
        foreach ($page in $pages) {
            $url = "$origin$page"
            $outPath = Join-Path $reportsFull (Get-ReportFileName $page)

            Write-Host "==> lighthouse $url" -ForegroundColor Cyan
            Invoke-Npx --yes "lighthouse@12" $url `
                --output=json `
                --output-path=$outPath `
                --only-categories=performance,accessibility,best-practices,seo `
                --chrome-flags="--headless=new --no-sandbox --disable-gpu" `
                --quiet

            if ($LASTEXITCODE -ne 0) { throw "Lighthouse failed on $url (exit $LASTEXITCODE)." }
        }
    }
    finally {
        if ($server -and -not $server.HasExited) {
            Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
        }

        Remove-Item Env:SERVER_PORT -ErrorAction SilentlyContinue
    }

    if (-not $ServerMetrics) {
        $ServerMetrics = Join-Path $reportsFull "server-metrics.json"
    }
}

# ---------------------------------------------------------------------------
# Decide. The comparison lives in F# with a test pack over it; this step is
# the one that fails the build.
# ---------------------------------------------------------------------------

$env:TOOLUP_CWV_BUDGET = [System.IO.Path]::GetFullPath($Budget)
$env:TOOLUP_CWV_REPORTS = $reportsFull

if ($ServerMetrics) {
    $env:TOOLUP_CWV_SERVER_METRICS = [System.IO.Path]::GetFullPath($ServerMetrics)
}

try {
    dotnet run --project Build.fsproj -- VerifyCoreWebVitalsBudget
    $gateExit = $LASTEXITCODE
}
finally {
    Remove-Item Env:TOOLUP_CWV_BUDGET -ErrorAction SilentlyContinue
    Remove-Item Env:TOOLUP_CWV_REPORTS -ErrorAction SilentlyContinue
    Remove-Item Env:TOOLUP_CWV_SERVER_METRICS -ErrorAction SilentlyContinue
}

if ($gateExit -ne 0) {
    Write-Host "Core-Web-Vitals budget gate FAILED." -ForegroundColor Red
    exit $gateExit
}

Write-Host "Core-Web-Vitals budget gate passed." -ForegroundColor Green
