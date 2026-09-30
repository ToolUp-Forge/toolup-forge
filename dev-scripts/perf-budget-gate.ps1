#Requires -Version 7.0
# Phase 192 — cold-start / hot-path perf-budget gate (the MEASURING half).
#
# Builds the minimal-shape sample in Release, boots it repeatedly to its
# ready line to measure COLD START, drives an anonymous request through the
# composed pipeline to measure the HOT PATH, inventories the built output so
# the zero-cost-when-unused rule can be asserted byte-level, writes it all to
# one measurement file, and hands that to the deciding half — the
# VerifyPerfBudget FAKE target, whose parser and check live in
# src/ToolUp.Platform.Build/Build/SDK.PerfBudget.fs. Non-zero exit on any
# breach.
#
# The split mirrors dev-scripts/cwv-budget-gate.ps1 and is deliberate for the
# same reason: the ceilings and the comparison are committed F# with a test
# pack over them, so widening a budget is a reviewable file change and a
# defect in the comparison is caught by VerifyAll. This script owns only the
# parts that need a process and a socket.
#
# Local run:
#
#   pwsh ./dev-scripts/perf-budget-gate.ps1
#
# Decide a measurement file a previous run (or CI) already produced, with no
# build and no boots — the fast inner loop while editing a budget:
#
#   pwsh ./dev-scripts/perf-budget-gate.ps1 -EvaluateOnly
#
# Prove the gate has TEETH — re-decide this run's real measurements against a
# deliberately unreachable budget and require the gate to go red:
#
#   pwsh ./dev-scripts/perf-budget-gate.ps1 -TeethCheck
#
# ── The client half (Phase 849) ──────────────────────────────────────────
#
# After the server half, the script measures the BROWSER runtime: it
# Fable-compiles the client-tier harness (src/ToolUp.AI.Client.Tests), runs
# its ClientBench entry under Node with React's production build, and decides
# the run against the `client` block of the same budget file through the
# VerifyClientPerfBudget target. Boot (import of the transpiled minimal client
# to its first render), decode per response (the proxy's reflective JSON
# decode over the wire corpus) and view per dispatch (the render hook's view
# construction) — each printed with its headroom ratio on a green run, exactly
# as the server ceilings are. -SkipClient runs the server half alone.
#
# Phase 909 added the client BUNDLE sizes to the same half: the script
# Fable-compiles samples/MinimalClient, builds it for production with Vite
# twice — its own index.html entry (the minimal sample) and the SDK shell's
# module graph with every export kept (vite.shell.config.mts) — into fresh
# directories under artifacts/perf-budget/bundles/, and ClientBench measures
# the JavaScript each build wrote. A size is decided like any other client
# metric; its teeth check below is a bundle-only block, so the bundle budget
# is shown red on its own rather than behind the boot clock.
#
# ── The load half (Phase 886) ────────────────────────────────────────────
#
# Last, the script measures the FACT and RETRIEVAL paths under concurrent
# load: it builds src/ToolUp.RAG.Benchmarks in Release, runs its `load gate`
# entry (N concurrent callers against one composed retrieval pipeline and
# against the fact store, blob read counts beside every clock), and decides
# the run against the `load` block of the same budget file through the
# VerifyLoadPerfBudget target. -SkipLoad leaves it out; -SkipServer leaves
# out the minimal-app half, so the load half can be run and decided alone:
#
#   pwsh ./dev-scripts/perf-budget-gate.ps1 -SkipServer -SkipClient
#
# Phase 929 — the load half's two STORAGE arms. -LoadArms azurite,postgres
# runs the same harness's gate configuration once per named arm — the blob
# arm on the local Azure Blob emulator (TOOLUP_PARITY_AZURITE); the vector
# store and the fact store on a local PostgreSQL
# (TOOLUP_PGVECTOR_CONNECTION_STRING) — and
# decides each against its own block of the budget file (`loadAzurite`,
# `loadPostgres`). They are off unless named, because each needs a local
# service the CI job does not start; an arm that is named but not armed is a
# failure, never a skip:
#
#   pwsh ./dev-scripts/perf-budget-gate.ps1 -SkipServer -SkipClient -LoadArms azurite,postgres
#
# ── What "cold start" means here, exactly ───────────────────────────────
#
# Process start to the host's "Application started." line on stdout. That is
# the moment the composed pipeline can serve, and it is what a serverless
# cold invocation pays. It does NOT include `dotnet build`, and it does not
# include the one-time creation of the machine's DataProtection keyring —
# the first boot of the run pays that, and the MINIMUM over the run naturally
# selects a boot that did not. The gate defends the SDK's own boot cost, not
# the operating system's.
#
# ── Why the gate reads MIN ───────────────────────────────────────────────
#
# Wallclock on a shared CI runner is a floor plus noise, and the noise is
# one-sided: a neighbouring job can only ever make a boot slower. The minimum
# over N runs is therefore the least contaminated estimate, and the only
# statistic whose spread does not widen as the runner gets busier. The median
# and maximum are recorded alongside for a human, never gated on.
#
# ── A boot that never became ready is NOT a fast boot ────────────────────
#
# Each sample carries an `observed` flag and the evidence for it. A boot
# whose ready line never appeared, or a request burst that did not return
# success every time, sets it false, and the deciding half REFUSES the run
# rather than reading the number. A gate that never ran the thing it times
# measures nothing, and the shape of that failure is a suspiciously good
# result.

[CmdletBinding()]
param(
    # The declarative budget.
    [string] $Budget = "perf-budgets.json",

    # The minimal-shape sample project measured.
    [string] $Project = "samples/MinimalApp/src/MinimalApp.Server/MinimalApp.Server.fsproj",

    # Where the measurement file is written (and, with -EvaluateOnly, read
    # from).
    [string] $MeasurementsFile = "artifacts/perf-budget/measurements.json",

    # Cold-start boots. Must be at least the budget's minimumSamples.
    [int] $Boots = 6,

    # Hot-path requests timed, after the warm-up burst.
    [int] $Requests = 300,

    # Hot-path requests issued and discarded first, so the measurement is of
    # a warm path rather than of JIT.
    [int] $WarmupRequests = 50,

    # The anonymous route driven for the hot-path measurement. Its handler is
    # PlatformInfoApi.GetPlatformInfo — [<AllowAnonymous>], auto-injected into
    # every composition, and the call the client shell makes before sign-in,
    # so it exists on the minimal shape and traverses the full pipeline.
    #
    # NOT /health: MetricsMiddleware, RequestTimingMiddleware and
    # RateLimiting all short-circuit on paths starting /health so probes do
    # not pollute metrics, which means a /health probe skips the very
    # middleware this budget exists to measure.
    [string] $HotPathRoute = "/api/PlatformInfoApi/GetPlatformInfo",

    # Skip build + boots + requests; decide the measurement file already
    # present at -MeasurementsFile.
    [switch] $EvaluateOnly,

    # Reuse an existing Release build of the sample.
    [switch] $SkipBuild,

    # Re-decide the measurements against an unreachable budget and require a
    # non-zero exit. Proves the gate fails when a regression is introduced,
    # without needing a synthetic slow app to introduce one.
    [switch] $TeethCheck,

    # First port tried. Each boot takes the next one.
    [int] $BasePort = 5240,

    # Phase 849 — the Fable-tier harness whose ClientBench entry measures the
    # browser runtime, and where its measurement file is written.
    [string] $ClientHarness = "src/ToolUp.AI.Client.Tests",
    [string] $ClientMeasurementsFile = "artifacts/perf-budget/client-measurements.json",

    # Fresh-process client boots. Must be at least client.minimumSamples.bootMs.
    [int] $ClientBoots = 6,

    # The seed ClientBench orders its fixtures and dispatches by; printed on
    # every run, so a surprising number is reproducible.
    [int] $ClientSeed = 849001,

    # Measure and decide the server half only.
    [switch] $SkipClient,

    # Phase 909 — the sample whose production builds the bundle budget
    # measures, and where the two builds are written.
    [string] $BundleSample = "samples/MinimalClient",
    [string] $BundleDirectory = "artifacts/perf-budget/bundles",

    # Phase 886 — the load harness, and where its measurement file is
    # written.
    [string] $LoadHarness = "src/ToolUp.RAG.Benchmarks/ToolUp.RAG.Benchmarks.fsproj",
    [string] $LoadMeasurementsFile = "artifacts/perf-budget/load-measurements.json",

    # Leave out the load half.
    [switch] $SkipLoad,

    # Phase 929 — the load half's storage arms to run and decide as well:
    # any of azurite, postgres, as a list or one comma-separated string (the
    # shape `pwsh -File` delivers a list in). None by default.
    [string[]] $LoadArms = @(),

    # Leave out the minimal-app (cold start + hot path) half.
    [switch] $SkipServer
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot/..

$repoRoot = (Get-Location).Path
$projectDir = Split-Path -Parent $Project
$outputDir = Join-Path $projectDir "bin/Release/net10.0"
$exeName = [IO.Path]::GetFileNameWithoutExtension($Project)

# ─── Build ───────────────────────────────────────────────────────────────

if (-not $EvaluateOnly -and -not $SkipBuild -and -not $SkipServer) {
    Write-Host "== perf-budget: build $Project (Release)" -ForegroundColor Cyan
    dotnet build $Project -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error "perf-budget: the sample did not build; there is nothing to measure."
        exit 1
    }
}

# ─── Measuring helpers ───────────────────────────────────────────────────

function Start-Sample {
    <#
      Start the sample on $Port with stdout on a PIPE. Precise, and safe
      only for a short-lived process: a child blocks once an unread pipe
      buffer fills, so anything that must keep serving uses
      Start-SampleToFile below instead.
    #>
    param([string] $ExePath, [string] $WorkingDirectory, [int] $Port)

    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $ExePath
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables['SERVER_PORT'] = "$Port"
    [Diagnostics.Process]::Start($psi)
}

function Stop-Sample {
    param([Diagnostics.Process] $Process)
    if ($null -eq $Process) { return }
    try { $Process.Kill($true) } catch { }
    try { $Process.WaitForExit(10000) | Out-Null } catch { }
}

function Measure-ColdStarts {
    param([string] $ExePath, [string] $WorkingDirectory, [int] $Count, [int] $FirstPort)

    $observations = [Collections.Generic.List[double]]::new()

    for ($i = 0; $i -lt $Count; $i++) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $proc = Start-Sample -ExePath $ExePath -WorkingDirectory $WorkingDirectory -Port ($FirstPort + $i)
        $ready = $null

        while (-not $proc.StandardOutput.EndOfStream) {
            $line = $proc.StandardOutput.ReadLine()
            if ($line -match 'Application started') {
                $ready = $sw.Elapsed.TotalMilliseconds
                break
            }
            if ($sw.Elapsed.TotalSeconds -gt 60) { break }
        }

        Stop-Sample -Process $proc

        if ($null -ne $ready) {
            $observations.Add($ready)
            Write-Host ("   boot {0}: {1:N0} ms" -f ($i + 1), $ready)
        }
        else {
            Write-Host ("   boot {0}: NEVER READY (no 'Application started.' within 60s)" -f ($i + 1)) -ForegroundColor Yellow
        }
    }

    $observed = $observations.Count -gt 0

    $evidence = if ($observed) {
        "'Application started.' observed on stdout for $($observations.Count)/$Count boots"
    }
    else {
        "no boot reached 'Application started.' within 60s"
    }

    [pscustomobject]@{
        Observations = $observations.ToArray()
        Observed     = $observed
        Evidence     = $evidence
    }
}

function Measure-HotPath {
    param(
        [string] $ExePath,
        [string] $WorkingDirectory,
        [int] $Port,
        [string] $Route,
        [int] $Warmup,
        [int] $Count
    )

    # stdout to a FILE, not a pipe: this host serves several hundred
    # requests and logs a line per request, which would fill an unread pipe
    # buffer and block the child — every later request would then look slow
    # for a reason that has nothing to do with the pipeline.
    $stdout = Join-Path ([IO.Path]::GetTempPath()) "toolup-perf-hotpath-$Port.log"
    Remove-Item $stdout -ErrorAction SilentlyContinue

    $previousPort = $env:SERVER_PORT
    $env:SERVER_PORT = "$Port"

    try {
        $proc = Start-Process -FilePath $ExePath -WorkingDirectory $WorkingDirectory `
            -RedirectStandardOutput $stdout -RedirectStandardError "$stdout.err" `
            -PassThru -NoNewWindow
    }
    finally {
        $env:SERVER_PORT = $previousPort
    }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $ready = $false

    while ($sw.Elapsed.TotalSeconds -le 60) {
        if (Test-Path $stdout) {
            $text = Get-Content $stdout -Raw -ErrorAction SilentlyContinue
            if ($text -and $text -match 'Application started') { $ready = $true; break }
        }
        Start-Sleep -Milliseconds 25
    }

    if (-not $ready) {
        Stop-Sample -Process $proc
        return [pscustomobject]@{
            Observations = @()
            Observed     = $false
            Evidence     = "the hot-path host never reached 'Application started.' within 60s"
        }
    }

    $client = [Net.Http.HttpClient]::new()
    $uri = "http://127.0.0.1:$Port$Route"
    $observations = [Collections.Generic.List[double]]::new()
    $succeeded = 0
    $lastStatus = 0

    $hit = {
        $body = [Net.Http.StringContent]::new('[]', [Text.Encoding]::UTF8, 'application/json')
        $response = $client.PostAsync($uri, $body).GetAwaiter().GetResult()
        $code = [int]$response.StatusCode
        $response.Dispose()
        $code
    }

    try {
        for ($i = 0; $i -lt $Warmup; $i++) { $lastStatus = & $hit }

        for ($i = 0; $i -lt $Count; $i++) {
            $s = [Diagnostics.Stopwatch]::StartNew()
            $code = & $hit
            $elapsed = $s.Elapsed.TotalMilliseconds
            $lastStatus = $code
            if ($code -ge 200 -and $code -lt 300) {
                $succeeded++
                $observations.Add($elapsed)
            }
        }
    }
    finally {
        $client.Dispose()
        Stop-Sample -Process $proc
    }

    [pscustomobject]@{
        Observations = $observations.ToArray()
        Observed     = ($Count -gt 0) -and ($succeeded -eq $Count)
        Evidence     = "HTTP $lastStatus from POST $Route — $succeeded/$Count requests returned success"
    }
}

function Get-Statistics {
    param([double[]] $Values)
    if ($null -eq $Values -or $Values.Count -eq 0) { return $null }
    $sorted = [double[]]($Values | Sort-Object)
    [pscustomobject]@{
        Min    = $sorted[0]
        Median = $sorted[[int][Math]::Floor($sorted.Count / 2)]
        Max    = $sorted[$sorted.Count - 1]
        Count  = $sorted.Count
    }
}

function New-Sample {
    param(
        [string] $Metric,
        $Statistics,
        [bool] $Observed,
        [string] $Evidence,
        [int] $Digits
    )

    $min = 0.0
    $median = 0.0
    $max = 0.0
    $count = 0

    if ($null -ne $Statistics) {
        $min = [Math]::Round($Statistics.Min, $Digits)
        $median = [Math]::Round($Statistics.Median, $Digits)
        $max = [Math]::Round($Statistics.Max, $Digits)
        $count = $Statistics.Count
    }

    [ordered]@{
        metric    = $Metric
        statistic = "min"
        value     = $min
        samples   = $count
        observed  = $Observed
        evidence  = $Evidence
        median    = $median
        max       = $max
    }
}

# ─── Measure ─────────────────────────────────────────────────────────────

if (-not $EvaluateOnly -and -not $SkipServer) {
    $suffix = if ($IsWindows) { ".exe" } else { "" }
    $exePath = Join-Path $outputDir ($exeName + $suffix)

    if (-not (Test-Path $exePath)) {
        Write-Error "perf-budget: '$exePath' does not exist. Build the sample, or drop -SkipBuild."
        exit 1
    }

    Write-Host "== perf-budget: cold start ($Boots boots of $exeName)" -ForegroundColor Cyan
    $cold = Measure-ColdStarts -ExePath $exePath -WorkingDirectory $outputDir -Count $Boots -FirstPort $BasePort
    $coldStats = Get-Statistics -Values $cold.Observations

    if ($null -ne $coldStats) {
        Write-Host ("   cold start: min {0:N0} ms  median {1:N0} ms  max {2:N0} ms  (n={3})" -f `
                $coldStats.Min, $coldStats.Median, $coldStats.Max, $coldStats.Count)
    }

    Write-Host "== perf-budget: hot path ($Requests timed requests to $HotPathRoute, after $WarmupRequests warm-up)" -ForegroundColor Cyan
    $hot = Measure-HotPath -ExePath $exePath -WorkingDirectory $outputDir -Port ($BasePort + $Boots + 1) `
        -Route $HotPathRoute -Warmup $WarmupRequests -Count $Requests
    $hotStats = Get-Statistics -Values $hot.Observations

    if ($null -ne $hotStats) {
        Write-Host ("   hot path:   min {0:N3} ms  median {1:N3} ms  max {2:N3} ms  (n={3})" -f `
                $hotStats.Min, $hotStats.Median, $hotStats.Max, $hotStats.Count)
    }

    # The output inventory backs the zero-cost-when-unused rule.
    # Deliberately EVERY assembly present, not a search for the forbidden
    # ones: the deciding half owns which names are forbidden, and an empty
    # inventory is refused there rather than passing as "nothing forbidden
    # found".
    $assemblies = @(
        Get-ChildItem -Path $outputDir -Filter *.dll -File -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Name }
    )
    Write-Host ("== perf-budget: output inventory — {0} assemblies in {1}" -f $assemblies.Count, $outputDir) -ForegroundColor Cyan

    $run = [ordered]@{
        schema           = "toolup.perf-measurements/v1"
        label            = "$exeName Release — $Boots boots, $Requests requests"
        appDirectory     = $outputDir
        machine          = [ordered]@{
            os         = [Runtime.InteropServices.RuntimeInformation]::OSDescription
            processors = [Environment]::ProcessorCount
            takenAt    = (Get-Date).ToUniversalTime().ToString("o")
        }
        outputAssemblies = $assemblies
        samples          = @(
            (New-Sample -Metric "coldStartMs" -Statistics $coldStats -Observed ([bool]$cold.Observed) -Evidence $cold.Evidence -Digits 3)
            (New-Sample -Metric "hotPathMs" -Statistics $hotStats -Observed ([bool]$hot.Observed) -Evidence $hot.Evidence -Digits 4)
        )
    }

    $measurementsDir = Split-Path -Parent $MeasurementsFile
    if ($measurementsDir -and -not (Test-Path $measurementsDir)) {
        New-Item -ItemType Directory -Force -Path $measurementsDir | Out-Null
    }

    $run | ConvertTo-Json -Depth 6 | Set-Content -Path $MeasurementsFile -Encoding utf8
    Write-Host "== perf-budget: measurements written to $MeasurementsFile" -ForegroundColor Cyan
}

# ─── Measure the client (Phase 849) ──────────────────────────────────────

# npm is called through this helper, never as a bare `& npm`: on Windows the npm.ps1 shim that
# ships with Node corrupts arguments when invoked from inside another script (see below).
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
    <#
      The canonical helper above resolves `npm.cmd`, which exists only on
      Windows — and this script also runs on the Linux CI runner, where npm
      is a plain executable with no PowerShell shim to dodge. Windows takes
      the canonical path; elsewhere the npm APPLICATION on PATH is resolved
      explicitly (never a bare `& npm`).
    #>
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

$clientMeasurementsPath = Join-Path $repoRoot $ClientMeasurementsFile
$minimalBundlePath = Join-Path $repoRoot (Join-Path $BundleDirectory "minimal")
$shellBundlePath = Join-Path $repoRoot (Join-Path $BundleDirectory "shell")

if (-not $SkipClient -and -not $EvaluateOnly) {
    # ── The client bundles (Phase 909) ──
    #
    # Both build directories are REMOVED first, so a build that fails or
    # writes elsewhere leaves nothing behind for ClientBench to measure: a
    # missing directory is an unobserved bundle, never last run's size.
    $sampleDir = Join-Path $repoRoot $BundleSample
    foreach ($dir in @($minimalBundlePath, $shellBundlePath)) {
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    }

    Push-Location $sampleDir
    try {
        if (-not $SkipBuild -or -not (Test-Path "output/Client.js")) {
            Write-Host "== perf-budget: client bundles — Fable-compile $BundleSample" -ForegroundColor Cyan
            dotnet tool restore
            if ($LASTEXITCODE -ne 0) { Write-Error "perf-budget: dotnet tool restore failed in $BundleSample."; exit 1 }
            Invoke-NpmAnyOs ci --no-fund --no-audit
            if ($LASTEXITCODE -ne 0) { Write-Error "perf-budget: npm ci failed in $BundleSample."; exit 1 }
            dotnet fable -o output --noCache
            if ($LASTEXITCODE -ne 0) {
                Write-Error "perf-budget: $BundleSample did not transpile; there is no bundle to measure."
                exit 1
            }
        }

        Write-Host "== perf-budget: client bundles — production builds of the minimal sample and the SDK shell" -ForegroundColor Cyan
        Invoke-NpxAnyOs --no-install vite build --outDir $minimalBundlePath --emptyOutDir --logLevel warn | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Error "perf-budget: the minimal sample's production build failed."; exit 1 }
        Invoke-NpxAnyOs --no-install vite build --config vite.shell.config.mts --outDir $shellBundlePath --emptyOutDir --logLevel warn | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Error "perf-budget: the SDK shell's production build failed."; exit 1 }
    }
    finally {
        Pop-Location
    }

    $harnessDir = Join-Path $repoRoot $ClientHarness
    $bench = Join-Path $harnessDir "output/Program.js"

    Push-Location $harnessDir
    try {
        if (-not $SkipBuild -or -not (Test-Path $bench)) {
            Write-Host "== perf-budget: client — Fable-compile $ClientHarness" -ForegroundColor Cyan
            dotnet tool restore
            if ($LASTEXITCODE -ne 0) { Write-Error "perf-budget: dotnet tool restore failed in $ClientHarness."; exit 1 }
            Invoke-NpmAnyOs ci --no-fund --no-audit
            if ($LASTEXITCODE -ne 0) { Write-Error "perf-budget: npm ci failed in $ClientHarness."; exit 1 }
            dotnet fable -o output --noCache
            if ($LASTEXITCODE -ne 0) {
                Write-Error "perf-budget: the client harness did not transpile; there is nothing to measure."
                exit 1
            }
        }

        if (Test-Path $clientMeasurementsPath) { Remove-Item $clientMeasurementsPath }

        # React picks its build from NODE_ENV at first import. A browser bundle
        # runs the production build, and ClientBench REFUSES boot and view
        # numbers taken under the development one.
        Write-Host "== perf-budget: client — ClientBench ($ClientBoots boots, seed $ClientSeed, React production build)" -ForegroundColor Cyan
        $previousNodeEnv = $env:NODE_ENV
        $env:NODE_ENV = "production"
        try {
            node --import ./register-loader.mjs output/Program.js ClientBench `
                --out $clientMeasurementsPath --seed $ClientSeed --boot-samples $ClientBoots `
                --minimal-bundle $minimalBundlePath --shell-bundle $shellBundlePath | Out-Host
            $benchExit = $LASTEXITCODE
        }
        finally {
            $env:NODE_ENV = $previousNodeEnv
        }
    }
    finally {
        Pop-Location
    }

    # A non-zero ClientBench exit means some measurement was not OBSERVED;
    # it still wrote every sample with its evidence, and the decider refuses
    # the unobserved ones by name. No file at all is a harness failure.
    if (-not (Test-Path $clientMeasurementsPath)) {
        Write-Error "perf-budget: ClientBench exited $benchExit and wrote no measurement file."
        exit 1
    }
}

# ─── Measure the fact and retrieval paths under load (Phase 886) ─────────

$loadMeasurementsPath = Join-Path $repoRoot $LoadMeasurementsFile

if (-not $SkipLoad -and -not $EvaluateOnly) {
    $loadDll = Join-Path (Split-Path -Parent (Join-Path $repoRoot $LoadHarness)) "bin/Release/net10.0/ToolUp.RAG.Benchmarks.dll"

    if (-not $SkipBuild -or -not (Test-Path $loadDll)) {
        Write-Host "== perf-budget: load — build $LoadHarness (Release)" -ForegroundColor Cyan
        dotnet build (Join-Path $repoRoot $LoadHarness) -c Release --nologo
        if ($LASTEXITCODE -ne 0) {
            Write-Error "perf-budget: the load harness did not build; there is nothing to measure."
            exit 1
        }
    }

    if (Test-Path $loadMeasurementsPath) { Remove-Item $loadMeasurementsPath }

    Write-Host "== perf-budget: load — the gate configurations (concurrent retrieval + fact store, memory blob arm)" -ForegroundColor Cyan
    dotnet $loadDll load gate --measurements $loadMeasurementsPath | Out-Host
    $loadExit = $LASTEXITCODE

    # The harness writes NOTHING when any operation failed — a failed call
    # timed as a fast one is a suspiciously good result — so a missing file
    # is the failure, named here, and never a skipped half.
    if (-not (Test-Path $loadMeasurementsPath)) {
        Write-Error "perf-budget: the load harness exited $loadExit and wrote no measurement file."
        exit 1
    }
}

# ─── The load half's storage arms (Phase 929) ────────────────────────────

$loadArmBlocks = @{ azurite = "VerifyLoadAzuritePerfBudget"; postgres = "VerifyLoadPostgresPerfBudget" }
$LoadArms = @($LoadArms | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($arm in $LoadArms) {
    if (-not $loadArmBlocks.ContainsKey($arm)) {
        Write-Error "perf-budget: unknown load arm '$arm' (expected azurite or postgres)."
        exit 1
    }
}
$loadArmPaths = @{}
foreach ($arm in $LoadArms) {
    $loadArmPaths[$arm] = Join-Path $repoRoot "artifacts/perf-budget/load-$arm-measurements.json"
}

if ($LoadArms.Count -gt 0 -and -not $EvaluateOnly) {
    $loadDll = Join-Path (Split-Path -Parent (Join-Path $repoRoot $LoadHarness)) "bin/Release/net10.0/ToolUp.RAG.Benchmarks.dll"

    if (-not (Test-Path $loadDll)) {
        $global:LASTEXITCODE = 0
        dotnet build (Join-Path $repoRoot $LoadHarness) -c Release --nologo
        if ($LASTEXITCODE -ne 0) {
            Write-Error "perf-budget: the load harness did not build; there is nothing to measure."
            exit 1
        }
    }

    foreach ($arm in $LoadArms) {
        if (Test-Path $loadArmPaths[$arm]) { Remove-Item $loadArmPaths[$arm] }

        Write-Host "== perf-budget: load — the $arm arm's gate configuration" -ForegroundColor Cyan
        $global:LASTEXITCODE = 0
        dotnet $loadDll load gate --arm $arm --measurements $loadArmPaths[$arm] | Out-Host
        $armExit = $LASTEXITCODE

        if (-not (Test-Path $loadArmPaths[$arm])) {
            Write-Error "perf-budget: the load harness's $arm arm exited $armExit and wrote no measurement file."
            exit 1
        }
    }
}

# ─── Decide ──────────────────────────────────────────────────────────────

function Invoke-Decider {
    <#
      Returns ONLY the decider's exit code.

      `| Out-Host` is load-bearing, not cosmetic: a native command's stdout
      lands in a PowerShell function's success stream, so without it the
      function returns the whole FAKE log with the exit code appended, and
      `$verdict -ne 0` is then true for a run that passed. That defect was
      in the first draft and the end-to-end run caught it — a green budget
      reported as BREACHED.

      Phase 849: the target and the measurement file are parameters, so the
      client half is decided by the same function against the budget's
      `client` block (VerifyClientPerfBudget).
    #>
    param(
        [string] $BudgetPath,
        [string] $Target = "VerifyPerfBudget",
        [string] $Measurements = $MeasurementsFile
    )
    $env:TOOLUP_PERF_BUDGET = $BudgetPath
    $env:TOOLUP_PERF_MEASUREMENTS = $Measurements
    dotnet run --project (Join-Path $repoRoot "Build.fsproj") -- $Target | Out-Host
    $code = $LASTEXITCODE
    return $code
}

$decideClient = -not $SkipClient

if ($decideClient -and -not (Test-Path $clientMeasurementsPath)) {
    Write-Error "perf-budget: no client measurement file at $clientMeasurementsPath — run without -EvaluateOnly, or pass -SkipClient."
    exit 1
}

$decideLoad = -not $SkipLoad

if ($decideLoad -and -not (Test-Path $loadMeasurementsPath)) {
    Write-Error "perf-budget: no load measurement file at $loadMeasurementsPath — run without -EvaluateOnly, or pass -SkipLoad."
    exit 1
}

$verdict = 0
if (-not $SkipServer) {
    Write-Host "== perf-budget: decide against $Budget" -ForegroundColor Cyan
    $verdict = Invoke-Decider -BudgetPath $Budget
}

$clientVerdict = 0
if ($decideClient) {
    Write-Host "== perf-budget: client — decide against the 'client' block of $Budget" -ForegroundColor Cyan
    $clientVerdict = Invoke-Decider -BudgetPath $Budget -Target "VerifyClientPerfBudget" -Measurements $clientMeasurementsPath
}

$loadVerdict = 0
if ($decideLoad) {
    Write-Host "== perf-budget: load — decide against the 'load' block of $Budget" -ForegroundColor Cyan
    $loadVerdict = Invoke-Decider -BudgetPath $Budget -Target "VerifyLoadPerfBudget" -Measurements $loadMeasurementsPath
}

$loadArmVerdict = 0
foreach ($arm in $LoadArms) {
    if (-not (Test-Path $loadArmPaths[$arm])) {
        Write-Error "perf-budget: no $arm measurement file at $($loadArmPaths[$arm]) — run without -EvaluateOnly."
        exit 1
    }

    Write-Host "== perf-budget: load — decide the $arm arm against its block of $Budget" -ForegroundColor Cyan
    $armVerdict = Invoke-Decider -BudgetPath $Budget -Target $loadArmBlocks[$arm] -Measurements $loadArmPaths[$arm]
    if ($armVerdict -ne 0 -and $loadArmVerdict -eq 0) { $loadArmVerdict = $armVerdict }
}

if ($TeethCheck) {
    # A gate nobody has watched fail is a gate nobody knows works. Re-decide
    # THIS run's real numbers against a budget no machine can meet, and
    # require the red. The unreachable budget is generated rather than
    # committed so it can never be mistaken for one anything ships against.
    Write-Host "== perf-budget: teeth check — re-deciding the same measurements against an unreachable budget" -ForegroundColor Cyan

    $teethBudget = Join-Path ([IO.Path]::GetTempPath()) "toolup-perf-teeth-budget.json"

    @'
{
  "schema": "toolup.perf-budget/v1",
  "label": "TEETH CHECK - deliberately unreachable, generated by dev-scripts/perf-budget-gate.ps1",
  "subject": "teeth check",
  "statistic": "min",
  "ceilings": { "coldStartMs": 0.001, "hotPathMs": 0.0001 },
  "minimumSamples": { "coldStartMs": 1, "hotPathMs": 1 },
  "client": {
    "label": "TEETH CHECK - deliberately unreachable client block",
    "subject": "teeth check",
    "statistic": "min",
    "ceilings": { "bootMs": 0.001, "decodePerResponseUs": 0.0001, "viewPerDispatchUs": 0.0001 },
    "minimumSamples": { "bootMs": 1, "decodePerResponseUs": 1, "viewPerDispatchUs": 1 }
  },
  "load": {
    "label": "TEETH CHECK - deliberately unreachable load block",
    "subject": "teeth check",
    "statistic": "min",
    "ceilings": { "retrievalP95Ms": 0.0001, "factPointReadMs": 0.0001 },
    "minimumSamples": { "retrievalP95Ms": 1, "factPointReadMs": 1 }
  }
}
'@ | Set-Content -Path $teethBudget -Encoding utf8

    $teeth = 1
    if (-not $SkipServer) {
        $teeth = Invoke-Decider -BudgetPath $teethBudget
    }

    $clientTeeth = 1
    if ($decideClient) {
        $clientTeeth = Invoke-Decider -BudgetPath $teethBudget -Target "VerifyClientPerfBudget" -Measurements $clientMeasurementsPath
    }

    # Phase 909 — the bundle budget on its own. The client block above goes
    # red on its clocks first, which says nothing about the sizes; this one
    # budgets ONLY the two bundles, at 1 KiB, so its red is theirs.
    $bundleTeethBudget = Join-Path ([IO.Path]::GetTempPath()) "toolup-perf-teeth-bundle-budget.json"

    @'
{
  "schema": "toolup.perf-budget/v1",
  "label": "TEETH CHECK - bundle sizes only, deliberately unreachable, generated by dev-scripts/perf-budget-gate.ps1",
  "subject": "teeth check",
  "statistic": "min",
  "ceilings": { "coldStartMs": 0.001 },
  "minimumSamples": { "coldStartMs": 1 },
  "client": {
    "label": "TEETH CHECK - bundle sizes only",
    "subject": "teeth check",
    "statistic": "min",
    "ceilings": { "minimalBundleKiB": 1, "shellBundleKiB": 1 },
    "minimumSamples": { "minimalBundleKiB": 1, "shellBundleKiB": 1 }
  }
}
'@ | Set-Content -Path $bundleTeethBudget -Encoding utf8

    $bundleTeeth = 1
    if ($decideClient) {
        $bundleTeeth = Invoke-Decider -BudgetPath $bundleTeethBudget -Target "VerifyClientPerfBudget" -Measurements $clientMeasurementsPath
    }

    Remove-Item $bundleTeethBudget -ErrorAction SilentlyContinue

    $loadTeeth = 1
    if ($decideLoad) {
        $loadTeeth = Invoke-Decider -BudgetPath $teethBudget -Target "VerifyLoadPerfBudget" -Measurements $loadMeasurementsPath
    }

    Remove-Item $teethBudget -ErrorAction SilentlyContinue

    if ($loadTeeth -eq 0) {
        Write-Error "perf-budget: TEETH CHECK FAILED — the load gate passed an unreachable load block. It is not deciding anything; do not trust its green."
        exit 1
    }

    if ($teeth -eq 0) {
        Write-Error "perf-budget: TEETH CHECK FAILED — the gate passed a budget of 0.001 ms. It is not deciding anything; do not trust its green."
        exit 1
    }

    if ($clientTeeth -eq 0) {
        Write-Error "perf-budget: TEETH CHECK FAILED — the client gate passed an unreachable client block. It is not deciding anything; do not trust its green."
        exit 1
    }

    if ($bundleTeeth -eq 0) {
        Write-Error "perf-budget: TEETH CHECK FAILED — the client gate passed a 1 KiB bundle budget. The bundle sizes are not being decided; do not trust their green."
        exit 1
    }

    Write-Host "== perf-budget: teeth check passed — the gate went red ($(if (-not $SkipServer) { "server exit $teeth" } else { "server skipped" })$(if ($decideClient) { ", client exit $clientTeeth, bundles exit $bundleTeeth" })$(if ($decideLoad) { ", load exit $loadTeeth" })) on the unreachable budget" -ForegroundColor Green
}

if ($verdict -ne 0) {
    Write-Host "== perf-budget: BREACHED (exit $verdict)" -ForegroundColor Red
    exit $verdict
}

if ($clientVerdict -ne 0) {
    Write-Host "== perf-budget: client BREACHED (exit $clientVerdict)" -ForegroundColor Red
    exit $clientVerdict
}

if ($loadVerdict -ne 0) {
    Write-Host "== perf-budget: load BREACHED (exit $loadVerdict)" -ForegroundColor Red
    exit $loadVerdict
}

if ($loadArmVerdict -ne 0) {
    Write-Host "== perf-budget: load arm BREACHED (exit $loadArmVerdict)" -ForegroundColor Red
    exit $loadArmVerdict
}

$halves = @(
    if (-not $SkipServer) { 'server' }
    if ($decideClient) { 'client' }
    if ($decideLoad) { 'load' }
    foreach ($arm in $LoadArms) { "load ($arm)" }
)
Write-Host "== perf-budget: within budget ($($halves -join ', '))" -ForegroundColor Green
exit 0
