#Requires -Version 7.0
<#
.SYNOPSIS
    The whole proof leg for every model under proofs/ — Phase 787's
    remoting decoder algebra, Phase 790's disclosure fold, Phase 793's
    tool gate, Phase 792's model-input admissibility, Phase 795's taint
    flow, and Phase 788's Elmish ring buffer and subscription diff.

.DESCRIPTION
    Self-contained and runnable from the repository root:

        pwsh ./proofs/check.ps1

    Seven steps, in this order, each refusing rather than warning. Steps
    2–5 and 7 run once PER MODULE in `$modules` below; step 6 builds the
    two oracle projects every extraction compiles into.

      1. Resolve the pinned F* release named in `fstar-pin.json` — an
         existing $env:FSTAR_HOME first, then a previous download under
         `proofs/.fstar/` IF it is the pinned release (one left by an
         older pin is replaced), then a fresh download whose SHA-256
         must match the pin.
      2. CHECK each module on it, with `--report_assumes error` so an
         `assume` or an `admit` fails the leg rather than quietly
         weakening a theorem.
      3. EXTRACT each to F# from the checked cache.
      4. NORMALISE each extraction's layout (Phase 850). The F# backend
         prints a verbose, OCaml-shaped dialect — `begin … end`, match
         arms at column 0 whatever their nesting — that F# 8 rejects
         (FS0058) and F# 10 cannot be told to accept (`#light "off"` is
         refused outright, FS1205). `normalise-extraction.fsx` is a
         deterministic re-layout of exactly that dialect into
         indentation-clean F#: parentheses for `begin … end`, every
         block on its own line at its depth, nothing else touched. It
         parses the dialect rather than patching it, and REFUSES a shape
         it does not know, so a future extraction that reaches one fails
         here loudly rather than compiling into something else.
      5. BYTE-DIFF each normalised extraction against its committed
         `oracle/*.fs`. This is the step that makes the committed file
         trustworthy: the repository builds and tests against a copy,
         and this says the copy is what the prover produced, laid out by
         the one script this leg runs.
      6. BUILD the oracle projects — the .NET one every extraction
         compiles into, and the Fable-host one (`oracle/fable/`) that
         compiles the Elmish ring extraction over a machine-integer
         `Prims`, so a committed extraction that no longer compiles is
         caught here rather than in someone else's `VerifyAll`. Both are
         built on .NET here; the Fable compile of the second is
         `VerifyFable`'s, through `ToolUp.AI.Client.Tests`.
      7. RUN each module's differential host — an Expecto list inside
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

    **The toolchain is a download of roughly 215 MB and is NOT a build dependency.**
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
    Skip step 7 (the differential hosts). The proofs, the extractions,
    the normalisation, the byte-diffs and the oracle builds still run.

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
        HostMinCases = 9 # Phase 800 added the refusal-shape and widened-vocabulary cases
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
    }
    @{
        Name         = "ModelInput"
        Source       = "ModelInput.fst"
        Oracle       = "oracle/ModelInput.fs"
        HostList     = "ToolUp.Platform.Tests.Phase 792 - the proved model input as oracle"
        HostMinCases = 9
        HostSubject  = "a 250-subject seeded population and a leaky store"
    }
    @{
        Name         = "TaintFlow"
        Source       = "TaintFlow.fst"
        Oracle       = "oracle/TaintFlow.fs"
        HostList     = "ToolUp.Platform.Tests.Phase 795 - the proved taint flow as oracle"
        HostMinCases = 11
        HostSubject  = "the two-party fixtures and the generated pipelines"
    }
    # Phase 788 — two models, ONE differential host: the ring and the
    # subscription diff are both the Elmish runtime, and the host that
    # drives them beside production is one Expecto list, so the two
    # entries below name the same list. Step 6 therefore runs it twice;
    # that is the price of keeping "one entry per model" true, and it
    # is seconds. The SAME differential also runs under Fable
    # (`src/ToolUp.AI.Client.Tests/ElmishProofOracleTests.fs`, via
    # `VerifyFable`), which this leg does not drive — the fable-tier CI
    # job does.
    @{
        Name         = "ElmishRing"
        Source       = "ElmishRing.fst"
        Oracle       = "oracle/ElmishRing.fs"
        HostList     = "ToolUp.Platform.Tests.Phase 788 - the proved Elmish runtime as oracle"
        HostMinCases = 8
        HostSubject  = "generated push/pop sequences, past several doublings"
    }
    @{
        Name         = "ElmishSub"
        Source       = "ElmishSub.fst"
        Oracle       = "oracle/ElmishSub.fs"
        HostList     = "ToolUp.Platform.Tests.Phase 788 - the proved Elmish runtime as oracle"
        HostMinCases = 8
        HostSubject  = "generated subscription sets, with duplicates and the shortcut's exact-key-set case"
    }
    # Phase 789 — the dispatch loop AROUND the ring. The FIRST module that
    # imports another: ElmishLoop.fst opens ElmishRing for the ring, its
    # opt/pair and append, so its `.checked` is an input to this one and
    # the entry MUST follow ElmishRing's — step 2 checks in list order
    # into one cache per run, which is what makes the import resolve.
    @{
        Name         = "ElmishLoop"
        Source       = "ElmishLoop.fst"
        Oracle       = "oracle/ElmishLoop.fs"
        HostList     = "ToolUp.Platform.Tests.Phase 789 - the proved dispatch loop as oracle"
        HostMinCases = 8
        HostSubject  = "generated reentrancy scripts against the real Program.runWithDispatch"
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

# What a prover says it is, read out of its own `--version` text: the
# release label and the commit it was built from. The pin is compared on
# BOTH (Phase 955), because the commit is the identity the pin records
# and a version string alone is only a label for it.
function Get-ProverIdentity {
    param([string] $Exe)
    $text = (& $Exe --version) -join " "
    [pscustomobject]@{
        Text    = $text
        Version = if ($text -match 'F\*\s+(\S+)') { "v$($Matches[1])" } else { "" }
        Commit  = if ($text -match 'commit=([0-9a-f]+)') { $Matches[1] } else { "" }
    }
}

function Test-IsPinnedProver {
    param($Identity)
    ($Identity.Version -eq $pin.version) -and ($Identity.Commit -eq $pin.commit)
}

# ─── 1. Resolve the pinned prover ────────────────────────────────────

Write-Step "1/7  Resolving the pinned prover ($($pin.version), Z3 $($pin.z3version))"

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

    # A previous download is reused only if it IS the pin (Phase 955).
    # This branch is the one that resolves the pin, so a directory left
    # by an OLDER pin must not answer for the new one: before this check
    # a checkout that had run the leg once kept running the release it
    # had first downloaded, whatever the pin went on to say. `-FStarHome`
    # and `$env:FSTAR_HOME` are different — there the caller chose the
    # prover, and the run says so rather than replacing it.
    if (Test-Path $candidate) {
        $held = Get-ProverIdentity $candidate

        if (-not (Test-IsPinnedProver $held)) {
            Write-Host "    proofs/.fstar holds $($held.Version)$(if ($held.Commit) { " at $($held.Commit)" }), not the pin ($($pin.version) at $($pin.commit)); replacing it"
            Remove-Item (Join-Path $home_ ($platform.bin -split '/')[0]) -Recurse -Force
        }
    }

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

# What RAN, as the prover names itself — the closing line prints this and
# not the pin (Phase 955). `-FStarHome` and `$env:FSTAR_HOME` both supply
# a prover the pin did not choose, and a closing line that printed the
# pinned version over such a run said the leg was green on a release it
# had not touched.
$ran = Get-ProverIdentity $fstarExe
Write-Host "    $($ran.Text)"

$ranVersion = if ($ran.Version) { $ran.Version } else { "an F* that did not report a version" }
$ranCommit = $ran.Commit
$ranIsPinned = Test-IsPinnedProver $ran

if (-not $ranIsPinned) {
    Write-Host "    WARNING: this is NOT the pinned release ($($pin.version), commit $($pin.commit)). A green run here is a claim about THIS prover." -ForegroundColor Yellow
}

# ─── 2..5. Check, extract, normalise, byte-diff — per module ─────────

$cacheDir = Join-Path $PSScriptRoot ".cache"
$extractDir = Join-Path $PSScriptRoot ".extract"

$checkFlags = @($pin.flags.check)
$extractFlags = @($pin.flags.extract)
$quakeFlags = @($pin.flags.quake)

$moduleNames = ($modules | ForEach-Object { $_.Name }) -join ", "

for ($run = 1; $run -le $Runs; $run++) {
    Write-Step "2/7  Checking $moduleNames (run $run of $Runs, COLD cache$(if ($Runs -gt 1) { ', --quake 3' }))"

    # Cold every run, deliberately. A warm `.checked` file is F* telling
    # you it already believed this, which is exactly the thing a repeat
    # run exists not to take on trust. The modules share one cache per
    # run. Until Phase 789 they shared nothing else — each owned its own
    # small types and imported only Prims; ElmishLoop.fst opens
    # ElmishRing, so the shared cache is now also HOW that import
    # resolves, and the list order above is load-bearing.
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

Write-Step "3/7  Extracting $moduleNames to F#"

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

Write-Step "4/7  Normalising each extraction's layout (normalise-extraction.fsx)"

# In place, over the fresh extractions only. The script accepts the
# extractor's dialect and nothing else — it refuses its own output (the
# header comment it writes is not a token it knows), which is deliberate:
# a normalised file is never an input, so a double pass cannot happen
# quietly.
$freshFiles = @()

foreach ($module in $modules) {
    $fresh = Join-Path $extractDir "$($module.Name).fs"
    if (-not (Test-Path $fresh)) { Fail "the extractor produced no $($module.Name).fs." }
    $freshFiles += $fresh
}

& dotnet fsi (Join-Path $PSScriptRoot "normalise-extraction.fsx") @freshFiles
if ($LASTEXITCODE -ne 0) { Fail "the layout normaliser refused an extraction (exit $LASTEXITCODE) — a shape it does not know. Teach it the shape, or reshape the model; never hand-edit the extraction." }

Write-Step "5/7  Byte-diffing each normalised extraction against its committed oracle"

foreach ($module in $modules) {
    $fresh = Join-Path $extractDir "$($module.Name).fs"
    $committed = Join-Path $PSScriptRoot $module.Oracle

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

        Fail "$($module.Oracle) is stale. Copy the fresh (normalised) extraction over it and commit the two together:  Copy-Item '$fresh' '$committed'"
    }

    Write-Host "    identical" -ForegroundColor Green
}

# ─── 6. Build the oracles ────────────────────────────────────────────

Write-Step "6/7  Building the oracle projects (.NET host, then the Fable host on .NET)"

& dotnet build (Join-Path $PSScriptRoot "oracle/ToolUp.Remoting.Proofs.Oracle.fsproj") --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail "a committed extraction does not compile (exit $LASTEXITCODE)." }

# The Fable-host oracle: the ring extraction over the machine-integer
# `Prims`. Built on .NET here — the same source compiles on both hosts,
# which is half of what Phase 850 established — and compiled by Fable in
# `VerifyFable`, which is the other half.
& dotnet build (Join-Path $PSScriptRoot "oracle/fable/ToolUp.Remoting.Proofs.Oracle.Fable.fsproj") --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail "the Fable-host oracle does not compile on .NET (exit $LASTEXITCODE)." }

# ─── 7. The differential hosts ───────────────────────────────────────

if ($SkipHost) {
    Write-Step "7/7  Differential hosts SKIPPED (-SkipHost)"
}
else {
    Write-Step "7/7  Running the differential hosts"

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
        # over a suite that did not run. (Step 7's case floors are the
        # ones `HostMinCases` carries above.)
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
$ranLabel =
    if ($ranIsPinned) { "$ranVersion (the pinned release)" }
    else { "$ranVersion$(if ($ranCommit) { " at $ranCommit" }), which is NOT the pinned release ($($pin.version))" }

Write-Host "PROOF LEG GREEN — $ranLabel, $Runs cold check(s) of $($modules.Count) module(s), every extraction identical to its committed oracle." -ForegroundColor Green
exit 0
