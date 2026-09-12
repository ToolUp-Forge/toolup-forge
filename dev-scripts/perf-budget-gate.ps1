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
    [int] $BasePort = 5240
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot/..

$repoRoot = (Get-Location).Path
$projectDir = Split-Path -Parent $Project
$outputDir = Join-Path $projectDir "bin/Release/net10.0"
$exeName = [IO.Path]::GetFileNameWithoutExtension($Project)

# ─── Build ───────────────────────────────────────────────────────────────

if (-not $EvaluateOnly -and -not $SkipBuild) {
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

if (-not $EvaluateOnly) {
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
    #>
    param([string] $BudgetPath)
    $env:TOOLUP_PERF_BUDGET = $BudgetPath
    $env:TOOLUP_PERF_MEASUREMENTS = $MeasurementsFile
    dotnet run --project (Join-Path $repoRoot "Build.fsproj") -- VerifyPerfBudget | Out-Host
    $code = $LASTEXITCODE
    return $code
}

Write-Host "== perf-budget: decide against $Budget" -ForegroundColor Cyan
$verdict = Invoke-Decider -BudgetPath $Budget

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
  "minimumSamples": { "coldStartMs": 1, "hotPathMs": 1 }
}
'@ | Set-Content -Path $teethBudget -Encoding utf8

    $teeth = Invoke-Decider -BudgetPath $teethBudget
    Remove-Item $teethBudget -ErrorAction SilentlyContinue

    if ($teeth -eq 0) {
        Write-Error "perf-budget: TEETH CHECK FAILED — the gate passed a budget of 0.001 ms. It is not deciding anything; do not trust its green."
        exit 1
    }

    Write-Host "== perf-budget: teeth check passed — the gate went red (exit $teeth) on the unreachable budget" -ForegroundColor Green
}

if ($verdict -ne 0) {
    Write-Host "== perf-budget: BREACHED (exit $verdict)" -ForegroundColor Red
    exit $verdict
}

Write-Host "== perf-budget: within budget" -ForegroundColor Green
exit 0
