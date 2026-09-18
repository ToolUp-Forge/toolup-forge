#Requires -Version 7.0
<#
.SYNOPSIS
    The whole proof leg for every model under proofs/ — Phase 787's
    remoting decoder algebra, Phase 790's disclosure fold and Phase 793's
    tool gate.
    remoting decoder algebra, Phase 790's disclosure fold, and Phase
    792's model-input admissibility.

.DESCRIPTION
    Self-contained and runnable from the repository root:

        pwsh ./proofs/check.ps1

    Six steps, in this order, each refusing rather than warning. Steps
    2–4 and 6 run once PER MODULE in `$modules` below; step 5 builds the
    one oracle project every extraction compiles into.

      1. Resolve the pinned F* release named in `fstar-pin.json` — an
         existing $env:FSTAR_HOME first, then a previous download under
         `proofs/.fstar/`, then a fresh download whose SHA-256 must match
         the pin.
      2. CHECK each module on it, with `--report_assumes error` so an
         `assume` or an `admit` fails the leg rather than quietly
         weakening a theorem.
      3. EXTRACT each to F# from the checked cache.
      4. BYTE-DIFF each extraction against its committed `oracle/*.fs`.
         This is the step that makes the committed file trustworthy:
         the repository builds and tests against a copy, and this says
         the copy is what the prover produced.
      5. BUILD the oracle project, so a committed extraction that no
         longer compiles is caught here rather than in someone else's
         `VerifyAll`.
      6. RUN each module's differential host — an Expecto list inside
         `ToolUp.Platform.Tests` that runs the extracted model beside
         production and requires them to agree. Skippable with
         `-SkipHost`, because it needs the whole solution built and the
         proof half does not.

    Two things about reproducibility are worth knowing before reading
    the flags.

    **Proof hints no longer exist.** `--record_hints` and `--use_hints`
    were removed from F*, so the usual way of pinning a proof's SMT
    search is unavailable. What replaces it is the PIN (this is why
    `fstar-pin.json` records the release, its hash, and the bundled Z3
    version), a MARGIN on `--z3rlimit` well above what any query here
    needs, and `--quake`, which re-runs every query under different
    seeds and fails unless it succeeds every time. `-Runs N` repeats the
    whole check N times from a COLD cache with `--quake 3` on, which is
    what the CI job does: a proof that passes once may be riding a
    lucky seed, and three cold runs of three seeds is cheap evidence
    that it is not.

    **The toolchain is a 198 MB download and is NOT a build dependency.**
    Nothing in `VerifyAll`, `dotnet build`, or the ordinary CI matrix
    needs it — the extractions are committed precisely so a contributor
    with no interest in proofs never installs a prover. This script is
    the only thing that does.

    **Adding a module** is one entry in `$modules`: its source, its
    committed extraction, the Expecto list that is its differential
    host, and the case-count floor that list declares. Nothing else in
    this file names a module.

.PARAMETER Runs
    Repeat the check that many times from a cold cache, with
    `--quake 3`. Default 1. The CI job runs 3.

.PARAMETER SkipHost
    Skip step 6 (the differential hosts). The proofs, the extractions,
    the byte-diffs and the oracle build still run.

.PARAMETER FStarHome
    An F* installation to use instead of resolving the pin. Overrides
    $env:FSTAR_HOME. The version is REPORTED and, when it differs from
    the pin, called out loudly — a green run against an unpinned prover
    is a different claim from a green run against the pinned one.
#>
[CmdletBinding()]
param(
    [int] $Runs = 1,
    [switch] $SkipHost,
    [string] $FStarHome
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$repoRoot = Split-Path -Parent $PSScriptRoot
$pin = Get-Content (Join-Path $PSScriptRoot "fstar-pin.json") -Raw | ConvertFrom-Json

# ─── The modules ─────────────────────────────────────────────────────
#
# One entry per proved model, in the order they landed. `HostList` is
# the Expecto list's FULL path (the pack's root list name, a dot, the
# list name — Expecto joins with `.`, and a slash-shaped filter matches
# nothing and reports success). `HostMinCases` is the number of cases
# that list declares, asserted after the run for the reason step 6
# gives. A later proof phase APPENDS its entry here.

$modules = @(
    @{
        Name         = "RemotingDecode"
        Source       = "RemotingDecode.fst"
        Oracle       = "oracle/RemotingDecode.fs"
        HostList     = "ToolUp.Platform.Tests.Phase 787 - the proved model as oracle"
        HostMinCases = 7
        HostSubject  = "the Phase 784 corpus"
    }
    @{
        Name         = "DisclosureFold"
        Source       = "DisclosureFold.fst"
        Oracle       = "oracle/DisclosureFold.fs"
        HostList     = "ToolUp.Platform.Tests.Phase 790 - the proved fold as oracle"
        HostMinCases = 7
        HostSubject  = "the door packs' rankings and a generated set"
    }
    @{
        Name         = "ToolGate"
        Source       = "ToolGate.fst"
        Oracle       = "oracle/ToolGate.fs"
        HostList     = "ToolUp.Platform.Tests.Phase 793 - the proved gate as oracle"
        HostMinCases = 7
        HostSubject  = "every in-tree tool under generated policies"
        Name         = "ModelInput"
        Source       = "ModelInput.fst"
        Oracle       = "oracle/ModelInput.fs"
        HostList     = "ToolUp.Platform.Tests.Phase 792 - the proved model input as oracle"
        HostMinCases = 9
        HostSubject  = "a 250-subject seeded population and a leaky store"
    }
)

function Write-Step {
    param([string] $Text)
    Write-Host ""
    Write-Host "=== $Text" -ForegroundColor Cyan
}

function Fail {
    param([string] $Text)
    Write-Host ""
    Write-Host "PROOF LEG FAILED: $Text" -ForegroundColor Red
    exit 1
}

# ─── 1. Resolve the pinned prover ────────────────────────────────────

Write-Step "1/6  Resolving the pinned prover ($($pin.version), Z3 $($pin.z3version))"

# Windows only for now, and that is declared rather than assumed: the
# pin's linux-x64 entry carries an EMPTY sha256 because nothing has run
# this leg on Linux, and a hash recorded as if it were verified is worse
# than an absent one. The CI job runs windows-latest for this reason.
$platformKey =
    if ($IsWindows) { "windows-x64" }
    elseif ($IsLinux) { "linux-x64" }
    else { $null }

if (-not $platformKey) {
    Fail "no pinned F* asset for this platform. The pin declares: $($pin.platforms.PSObject.Properties.Name -join ', ')."
}

$platform = $pin.platforms.$platformKey

$fstarExe = $null

if ($FStarHome) {
    $fstarExe = Join-Path $FStarHome "bin/fstar.exe"
    if (-not (Test-Path $fstarExe)) { $fstarExe = Join-Path $FStarHome "bin/fstar" }
    if (-not (Test-Path $fstarExe)) { Fail "-FStarHome '$FStarHome' has no bin/fstar(.exe)." }
    Write-Host "    using -FStarHome $FStarHome"
}
elseif ($env:FSTAR_HOME) {
    $fstarExe = Join-Path $env:FSTAR_HOME "bin/fstar.exe"
    if (-not (Test-Path $fstarExe)) { $fstarExe = Join-Path $env:FSTAR_HOME "bin/fstar" }
    if (-not (Test-Path $fstarExe)) { Fail "`$env:FSTAR_HOME '$($env:FSTAR_HOME)' has no bin/fstar(.exe)." }
    Write-Host "    using `$env:FSTAR_HOME $($env:FSTAR_HOME)"
}
else {
    $home_ = Join-Path $PSScriptRoot ".fstar"
    $candidate = Join-Path $home_ $platform.bin

    if (-not (Test-Path $candidate)) {
        if (-not $platform.sha256) {
            Fail "the pin declares no verified hash for $platformKey, so this script will not download it. Install F* $($pin.version) yourself and pass -FStarHome, or run this leg on a platform the pin has verified."
        }

        Write-Host "    downloading $($platform.asset) (this is a one-off; it lands in proofs/.fstar/, which is gitignored)"
        New-Item -ItemType Directory -Force -Path $home_ | Out-Null
        $archive = Join-Path $home_ $platform.asset

        try {
            Invoke-WebRequest -Uri $platform.url -OutFile $archive -MaximumRedirection 5
        }
        catch {
            Fail "could not download the pinned release: $($_.Exception.Message)"
        }

        $actual = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLower()

        if ($actual -ne $platform.sha256.ToLower()) {
            Remove-Item $archive -Force -ErrorAction SilentlyContinue
            Fail "downloaded archive hash $actual does not match the pin ($($platform.sha256)). Refusing to use it."
        }

        Write-Host "    hash verified: $actual"

        if ($platform.asset.EndsWith(".zip")) {
            Expand-Archive -Path $archive -DestinationPath $home_ -Force
        }
        else {
            tar -xzf $archive -C $home_
        }

        Remove-Item $archive -Force -ErrorAction SilentlyContinue
    }

    $fstarExe = $candidate
    if (-not (Test-Path $fstarExe)) { Fail "the extracted release has no $($platform.bin)." }
}

$reported = (& $fstarExe --version) -join " "
Write-Host "    $reported"

if ($reported -notmatch [regex]::Escape($pin.version.TrimStart("v"))) {
    Write-Host "    WARNING: this is NOT the pinned release ($($pin.version)). A green run here is a claim about THIS prover." -ForegroundColor Yellow
}

# ─── 2..4. Check, extract, byte-diff — per module ────────────────────

$cacheDir = Join-Path $PSScriptRoot ".cache"
$extractDir = Join-Path $PSScriptRoot ".extract"

$checkFlags = @($pin.flags.check)
$extractFlags = @($pin.flags.extract)
$quakeFlags = @($pin.flags.quake)

$moduleNames = ($modules | ForEach-Object { $_.Name }) -join ", "

for ($run = 1; $run -le $Runs; $run++) {
    Write-Step "2/6  Checking $moduleNames (run $run of $Runs, COLD cache$(if ($Runs -gt 1) { ', --quake 3' }))"

    # Cold every run, deliberately. A warm `.checked` file is F* telling
    # you it already believed this, which is exactly the thing a repeat
    # run exists not to take on trust. The modules share one cache per
    # run because they share nothing else: each owns its own small
    # types and imports only Prims, so no `.checked` of one is an input
    # to another.
    Remove-Item $cacheDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null

    foreach ($module in $modules) {
        Write-Host "    --- $($module.Source)"
        $fstarArgs = @("--cache_dir", $cacheDir) + $checkFlags
        if ($Runs -gt 1) { $fstarArgs += $quakeFlags }
        $fstarArgs += $module.Source

        & $fstarExe @fstarArgs
        if ($LASTEXITCODE -ne 0) { Fail "$($module.Source) did not check (run $run of $Runs, exit $LASTEXITCODE)." }
    }
}

Write-Step "3/6  Extracting $moduleNames to F#"

Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $extractDir | Out-Null

# Extraction refuses on an unchecked module ("Cross-module inlining
# expects all modules to be checked first"), which is why this is a
# second invocation against the cache step 2 populated rather than one
# combined pass.
foreach ($module in $modules) {
    Write-Host "    --- $($module.Source)"
    $fstarArgs = @("--cache_dir", $cacheDir, "--odir", $extractDir) + $checkFlags + $extractFlags + @("--extract", $module.Name, $module.Source)
    & $fstarExe @fstarArgs
    if ($LASTEXITCODE -ne 0) { Fail "extraction of $($module.Source) failed (exit $LASTEXITCODE)." }
}

Write-Step "4/6  Byte-diffing each extraction against its committed oracle"

foreach ($module in $modules) {
    $fresh = Join-Path $extractDir "$($module.Name).fs"
    $committed = Join-Path $PSScriptRoot $module.Oracle

    if (-not (Test-Path $fresh)) { Fail "the extractor produced no $($module.Name).fs." }
    if (-not (Test-Path $committed)) { Fail "no committed oracle at $($module.Oracle). Copy the fresh extraction there and commit it:  Copy-Item '$fresh' '$committed'" }

    $freshHash = (Get-FileHash $fresh -Algorithm SHA256).Hash.ToLower()
    $committedHash = (Get-FileHash $committed -Algorithm SHA256).Hash.ToLower()

    Write-Host "    --- $($module.Oracle)"
    Write-Host "    fresh     sha256:$freshHash"
    Write-Host "    committed sha256:$committedHash"

    if ($freshHash -ne $committedHash) {
        Write-Host ""
        Write-Host "    The committed oracle is not what the prover produced. Diff:" -ForegroundColor Yellow
        Compare-Object (Get-Content $committed) (Get-Content $fresh) |
            Select-Object -First 40 |
            Format-Table -AutoSize |
            Out-String |
            Write-Host

        Fail "$($module.Oracle) is stale. Copy the fresh extraction over it and commit the two together:  Copy-Item '$fresh' '$committed'"
    }

    Write-Host "    identical" -ForegroundColor Green
}

# ─── 5. Build the oracle ─────────────────────────────────────────────

Write-Step "5/6  Building the oracle project"

& dotnet build (Join-Path $PSScriptRoot "oracle/ToolUp.Remoting.Proofs.Oracle.fsproj") --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail "a committed extraction does not compile (exit $LASTEXITCODE)." }

# ─── 6. The differential hosts ───────────────────────────────────────

if ($SkipHost) {
    Write-Step "6/6  Differential hosts SKIPPED (-SkipHost)"
}
else {
    Write-Step "6/6  Running the differential hosts"

    $testProject = Join-Path $repoRoot "src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj"
    & dotnet build $testProject --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "ToolUp.Platform.Tests did not build (exit $LASTEXITCODE)." }

    $dll = Join-Path $repoRoot "src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll"
    if (-not (Test-Path $dll)) { Fail "no test assembly at $dll." }

    foreach ($module in $modules) {
        Write-Host "    --- $($module.Name) over $($module.HostSubject)"

        $output = & dotnet $dll --filter $module.HostList 2>&1
        $exit = $LASTEXITCODE
        $output | ForEach-Object { Write-Host "    $_" }

        # A filter that matches nothing prints `0 tests run ... Success!`
        # and exits 0. So the COUNT is asserted, never the exit code alone
        # — the one shape in which this whole leg could report a green
        # over a suite that did not run.
        #
        # **Strip ANSI first, and the reason is the same trap one level
        # down.** Expecto colourises the count, so the bytes are
        # `ESC[36m7ESC[37m tests run` and a `(\d+)\s+tests run` regex over
        # the raw text matches NOTHING — reading as zero cases, from a run
        # that was green. This script fails closed on that, so the mistake
        # cost a refusal rather than a false pass, but a harness that
        # compared against zero the other way round would have reported
        # exactly the vacuous green the count exists to prevent.
        $plain = [regex]::Replace((($output | ForEach-Object { "$_" }) -join "`n"), "\x1b\[[0-9;]*[A-Za-z]", "")

        $ran = if ($plain -match '(\d+)\s+tests run') { [int]$Matches[1] } else { -1 }

        if ($ran -lt 0) { Fail "could not read a case count out of the $($module.Name) host's output." }

        if ($exit -ne 0) { Fail "the $($module.Name) differential host reported failures (exit $exit)." }
        if ($ran -lt $module.HostMinCases) { Fail "the $($module.Name) differential host ran $ran case(s); its list declares at least $($module.HostMinCases). A filter that matches nothing reports success, so this is checked rather than trusted." }

        Write-Host "    $ran case(s) ran, all green" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "PROOF LEG GREEN — $($pin.version), $Runs cold check(s) of $($modules.Count) module(s), every extraction identical to its committed oracle." -ForegroundColor Green
exit 0
