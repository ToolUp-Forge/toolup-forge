#Requires -Version 7.0
<#
.SYNOPSIS
    The whole proof leg for every model under proofs/ — Phase 787's
    remoting decoder algebra, Phase 790's disclosure fold, Phase 793's
    tool gate, Phase 792's model-input admissibility, Phase 795's taint
    flow, Phase 788's Elmish ring buffer and subscription diff, Phase
    789's dispatch loop, and Phase 955's array ring in Pulse.

.DESCRIPTION
    Self-contained and runnable from the repository root:

        pwsh ./proofs/check.ps1

    Seven steps, in this order, each refusing rather than warning. Step 2
    runs once PER MODULE in `$modules` below; steps 3–5 run once over the
    WHOLE PROGRAM the nine modules form; step 6 builds the projects every
    extraction compiles into; step 7 runs the differential hosts.

      1. Resolve the pinned F* release named in `fstar-pin.json` — an
         existing $env:FSTAR_HOME first, then a previous download under
         `proofs/.fstar/` IF it is the pinned release (one left by an
         older pin is replaced), then a fresh download whose SHA-256
         must match the pin.
      2. CHECK each module on it, with `--report_assumes error` so an
         `assume` or an `admit` fails the leg rather than quietly
         weakening a theorem.
      3. EXTRACT the nine modules to F# in ONE run of Custard's F#
         backend (`--codegen FSharp --custard_split`, Phase 957), from
         the cache step 2 populated. Custard is a whole-program
         compiler: every module is a root (`--custard_entry_module`),
         and `--custard_split` writes one F# module per F* module under
         its own name, so the hosts bind `ElmishRing.push` as they
         always did. The run writes the modules, the support library
         `FStarCustard.fs` and a project `Custard.fsproj` listing them
         in compile order. Until Phase 957 the eight first-order models
         went through the legacy `--codegen FSharp` printer and a layout
         normaliser; the release this leg pins carries neither the
         printer nor the `--extract` option, so there is no second route
         and nothing to normalise.
      4. Two derived edits, both deterministic. (a) Drop FS0025 from the
         `#nowarn` line Custard heads every module with, so the oracle
         stays under the incomplete-match rule (Phase 1037) — the one
         edit the leg has ever made to Custard's output, and the reason
         a committed extraction with an incomplete match fails the build
         by name rather than compiling with the check off. (b) CUT the
         Fable host's stand-in for the support library out of the
         generated `FStarCustard.fs`: the `Prims` block, verbatim, under
         a fixed header. Fable refuses the file as written (128-bit
         integer arithmetic, console writers, `exit`, a strict UTF-8
         decoder) and the Elmish extractions reach nothing outside
         `Prims`, so the stand-in is a projection of the generated file,
         not a second implementation of anything.
      5. BYTE-DIFF every generated file against its committed copy under
         `oracle/custard/` — the nine modules, the support library and
         the project — and the derived stand-in against
         `oracle/fable/FStarCustard.fs`. A file on one side with no
         counterpart on the other fails too: a model that came to reach
         a ulib definition would make Custard write a tenth module, and
         a byte-diff over a fixed list would not see it. This is the
         step that makes the committed files trustworthy: the repository
         builds and tests against copies, and this says the copies are
         what the prover produced.
      6. BUILD what the extractions compile into: the project Custard
         wrote, AS GENERATED (less the FS0025 token step 4 drops), so a
         layout the compiler refuses fails here by name; the .NET oracle
         (`oracle/`) the hosts reference, which compiles the same
         committed sources and whose file list is checked against
         Custard's; and the Fable-host oracle (`oracle/fable/`), which
         compiles the three Elmish extractions over the stand-in — built
         on .NET here, transpiled by Fable in `VerifyFable`.
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

    **The toolchain is a download of roughly 230 MB and is NOT a build dependency.**
    Nothing in `VerifyAll`, `dotnet build`, or the ordinary CI matrix
    needs it — the extractions are committed precisely so a contributor
    with no interest in proofs never installs a prover. This script is
    the only thing that does.

    **Adding a module** is one entry in `$modules`: its source, the
    Expecto list that is its differential host, and the case-count floor
    that list declares. Nothing else in this file names a module. The
    LIST ORDER is load-bearing twice over: step 2 checks in list order
    into one cache per run, which is how a module that opens another
    resolves the import; and Custard orders the files it writes by the
    command line it was given, so the same list in a different order
    moves bytes the diff holds.

.PARAMETER Runs
    Repeat the check that many times from a cold cache, with
    `--quake 3`. Default 1. The CI job runs 3.

.PARAMETER SkipHost
    Skip step 7 (the differential hosts). The proofs, the extraction,
    the byte-diffs and the oracle builds still run.

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
# that list declares, asserted after the run for the reason step 7
# gives. A later proof phase APPENDS its entry here — AFTER any module
# it opens, for the two reasons the header gives.

$modules = @(
    @{
        Name         = "RemotingDecode"
        Source       = "RemotingDecode.fst"
        HostList     = "ToolUp.Platform.Tests.Phase 787 - the proved model as oracle"
        HostMinCases = 9 # Phase 800 added the refusal-shape and widened-vocabulary cases
        HostSubject  = "the Phase 784 corpus"
    }
    @{
        Name         = "DisclosureFold"
        Source       = "DisclosureFold.fst"
        HostList     = "ToolUp.Platform.Tests.Phase 790 - the proved fold as oracle"
        HostMinCases = 7
        HostSubject  = "the door packs' rankings and a generated set"
    }
    @{
        Name         = "ToolGate"
        Source       = "ToolGate.fst"
        HostList     = "ToolUp.Platform.Tests.Phase 793 - the proved gate as oracle"
        HostMinCases = 7
        HostSubject  = "every in-tree tool under generated policies"
    }
    @{
        Name         = "ModelInput"
        Source       = "ModelInput.fst"
        HostList     = "ToolUp.Platform.Tests.Phase 792 - the proved model input as oracle"
        HostMinCases = 9
        HostSubject  = "a 250-subject seeded population and a leaky store"
    }
    @{
        Name         = "TaintFlow"
        Source       = "TaintFlow.fst"
        HostList     = "ToolUp.Platform.Tests.Phase 795 - the proved taint flow as oracle"
        HostMinCases = 11
        HostSubject  = "the two-party fixtures and the generated pipelines"
    }
    # Phase 788 — two models, ONE differential host: the ring and the
    # subscription diff are both the Elmish runtime, and the host that
    # drives them beside production is one Expecto list, so the two
    # entries below name the same list. Step 7 therefore runs it twice;
    # that is the price of keeping "one entry per model" true, and it
    # is seconds. The SAME differential also runs under Fable
    # (`src/ToolUp.AI.Client.Tests/ElmishProofOracleTests.fs`, via
    # `VerifyFable`), which this leg does not drive — the fable-tier CI
    # job does.
    @{
        Name         = "ElmishRing"
        Source       = "ElmishRing.fst"
        HostList     = "ToolUp.Platform.Tests.Phase 788 - the proved Elmish runtime as oracle"
        HostMinCases = 8
        HostSubject  = "generated push/pop sequences, past several doublings"
    }
    @{
        Name         = "ElmishSub"
        Source       = "ElmishSub.fst"
        HostList     = "ToolUp.Platform.Tests.Phase 788 - the proved Elmish runtime as oracle"
        HostMinCases = 8
        HostSubject  = "generated subscription sets, with duplicates and the shortcut's exact-key-set case"
    }
    # Phase 789 — the dispatch loop AROUND the ring. The first module that
    # imports another: ElmishLoop.fst opens ElmishRing for the ring, its
    # opt/pair and append, so its `.checked` is an input to this one and
    # the entry MUST follow ElmishRing's.
    @{
        Name         = "ElmishLoop"
        Source       = "ElmishLoop.fst"
        HostList     = "ToolUp.Platform.Tests.Phase 789 - the proved dispatch loop as oracle"
        HostMinCases = 8
        HostSubject  = "generated reentrancy scripts against the real Program.runWithDispatch"
    }
    # Phase 955 — the ring over a mutable ARRAY, written in Pulse and
    # proved to refine ElmishRing, which it opens (so it follows it). Its
    # extraction is run AS the ring rather than beside one. It was the
    # first module through Custard; since Phase 957 every module is, and
    # the entry needs nothing the others do not.
    @{
        Name         = "ElmishRingArray"
        Source       = "ElmishRingArray.fst"
        HostList     = "ToolUp.Platform.Tests.Phase 788 - the proved Elmish runtime as oracle.Phase 955 - the extracted array ring as implementation"
        HostMinCases = 8
        HostSubject  = "the shipped ring and the list model, over the campaign and a live one"
    }
)

# What one run of Custard writes besides the modules: the support
# library every module opens and the project that lists them. Both are
# the prover's output as much as the modules are, and both are
# byte-held.
$supportLibrary = "FStarCustard.fs"
$generatedProject = "Custard.fsproj"

# Where the committed copies live, relative to this directory.
$committedDir = "oracle/custard"
$fableStandIn = "oracle/fable/FStarCustard.fs"

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

# ─── 2. Check — per module ───────────────────────────────────────────

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

# ─── 3. Extract — the whole program, once ────────────────────────────

Write-Step "3/7  Extracting $moduleNames to F# (Custard, one whole-program run, split per module)"

Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
$custardDir = Join-Path $extractDir "custard"
New-Item -ItemType Directory -Force -Path $custardDir | Out-Null

# Custard refuses an unchecked module (error 317), which is why this is
# a second invocation against the cache step 2 populated rather than one
# combined pass. Every module is a root and every source is on the
# command line, in list order — the pin's `extract` set carries the
# `--ext fly_deps=0` that lets one run take more than one file.
$fstarArgs = @("--cache_dir", $cacheDir) + $checkFlags + $extractFlags + @("--odir", $custardDir)
foreach ($module in $modules) { $fstarArgs += @("--custard_entry_module", $module.Name) }
foreach ($module in $modules) { $fstarArgs += $module.Source }

& $fstarExe @fstarArgs
if ($LASTEXITCODE -ne 0) { Fail "Custard extraction failed (exit $LASTEXITCODE)." }

$generatedNames = @($supportLibrary; $generatedProject) + @($modules | ForEach-Object { "$($_.Name).fs" })

foreach ($name in $generatedNames) {
    if (-not (Test-Path (Join-Path $custardDir $name))) { Fail "Custard wrote no $name." }
}

# ─── 4. The derived edits ────────────────────────────────────────────

Write-Step "4/7  Dropping FS0025 from each module's #nowarn line; cutting the Fable host's support stand-in"

# Custard's ONE edit (Phase 1037). Its backend heads every module it
# writes with a `#nowarn` line that names FS0025, which would switch the
# incomplete-match rule off for the whole file — exactly the file-scoped
# suppression the Phase 624 gate pins at zero, and since 1037 the oracle
# is under that gate (`oracle/Directory.Build.props` states FS0025 as an
# error). The token is removed and nothing else: the line keeps every
# other id Custard names, the layout is untouched (step 6 still builds
# what Custard laid out, so a layout the compiler refuses fails THERE),
# and a module with an incomplete match fails the build by name instead
# of compiling with the check off. Any other spelling of the suppression
# left behind is refused here rather than byte-held below. A match F*
# left incomplete because it erased the refinement that made it total is
# therefore the MODEL's to complete, by a respelling that changes no
# theorem — the decoder's `item_at` is the one instance so far (Phase 957).
foreach ($module in $modules) {
    $fresh = Join-Path $custardDir "$($module.Name).fs"

    $text = [IO.File]::ReadAllText((Resolve-Path $fresh).Path)
    $text = [regex]::Replace($text, '(?m)^(#nowarn(?:[ \t]+"[0-9]+")*?)[ \t]+"25"(?=[ \t"]|$)', '$1')

    if ([regex]::IsMatch($text, '(?m)^[ \t]*#nowarn\b.*(?<![0-9A-Za-z])"?(FS0*)?25"?(?![0-9])')) {
        Fail "Custard's $($module.Name).fs still suppresses FS0025 after the one edit this step makes — a #nowarn shape it does not know. Teach this step the shape; never suppress the rule."
    }

    [IO.File]::WriteAllText((Resolve-Path $fresh).Path, $text)
}

# The Fable host's stand-in (Phase 957). `FStarCustard.fs` as Custard
# writes it does not transpile — Fable refuses `System.Int128` /
# `System.UInt128` arithmetic, `TextWriter.Write` / `Flush`, `exit` and
# the `UTF8Encoding` constructor — and it is one module, so the refusal
# is of the whole file. The three Elmish extractions the Fable host
# compiles reach `Prims` and nothing else in it, so the stand-in is
# that block, CUT from the generated file verbatim: from the line
# `module Prims =` to the line before the next top-level item. It is
# derived here on every run and byte-held in step 5, so it cannot drift
# from the support library it stands in for — which is what made the
# hand-written stand-in Phase 955 committed, and the hand-written Prims
# shims before it, assumptions on the ladder rather than projections.
$fableDir = Join-Path $extractDir "custard-fable"
New-Item -ItemType Directory -Force -Path $fableDir | Out-Null

$supportLines = [IO.File]::ReadAllLines((Join-Path $custardDir $supportLibrary))
$primsStart = [Array]::FindIndex($supportLines, [Predicate[string]] { param($l) $l -cmatch '^module Prims =\s*$' })
if ($primsStart -lt 0) { Fail "the generated $supportLibrary has no `module Prims =` block to cut the Fable stand-in from." }

$primsEnd = $primsStart + 1
while ($primsEnd -lt $supportLines.Length -and ($supportLines[$primsEnd] -match '^\s' -or $supportLines[$primsEnd] -eq '')) { $primsEnd++ }
while ($primsEnd -gt $primsStart -and $supportLines[$primsEnd - 1] -eq '') { $primsEnd-- }

$standIn = @(
    "// Derived by proofs/check.ps1 from the FStarCustard.fs Custard wrote beside the"
    "// extractions (Phase 957). Do not edit: the leg re-derives and byte-holds it."
    "//"
    "// The Fable host's stand-in for Custard's F# support library. The generated file"
    "// does not transpile (128-bit integer arithmetic, console writers, exit, a strict"
    "// UTF-8 decoder), and the Elmish extractions this host compiles reach only Prims,"
    "// so this is that block, verbatim, and nothing else: an extraction that came to"
    "// need another name from the support library fails to compile here, naming it."
    "module FStarCustard"
    ""
) + $supportLines[$primsStart..($primsEnd - 1)] + @("")

[IO.File]::WriteAllText((Join-Path $fableDir $supportLibrary), (($standIn -join "`n")))

# ─── 5. Byte-diff ────────────────────────────────────────────────────

Write-Step "5/7  Byte-diffing every generated file against its committed copy"

# A file on either side without a counterpart is a failure in its own
# right, before any hash is compared: Custard writes one file per module
# the program REACHES, so a model that came to call a ulib definition
# would add a file this list does not name.
$committedFiles = @(Get-ChildItem (Join-Path $PSScriptRoot $committedDir) -File | ForEach-Object { $_.Name } | Sort-Object)
$freshFiles = @(Get-ChildItem $custardDir -File | ForEach-Object { $_.Name } | Sort-Object)

foreach ($name in $freshFiles) {
    if ($committedFiles -notcontains $name) { Fail "Custard wrote $name, which has no committed copy under $committedDir/. If a model now reaches a definition outside the nine modules, say so in the README and commit the file:  Copy-Item '$(Join-Path $custardDir $name)' '$(Join-Path $PSScriptRoot $committedDir)/'" }
}

foreach ($name in $committedFiles) {
    if ($freshFiles -notcontains $name) { Fail "$committedDir/$name is committed but Custard no longer writes it. Remove it with whatever change made it unreachable." }
}

$pairs = @()
foreach ($name in $freshFiles) {
    $pairs += @{ Fresh = (Join-Path $custardDir $name); Committed = "$committedDir/$name" }
}
$pairs += @{ Fresh = (Join-Path $fableDir $supportLibrary); Committed = $fableStandIn }

foreach ($pair in $pairs) {
    $fresh = $pair.Fresh
    $committed = Join-Path $PSScriptRoot $pair.Committed

    if (-not (Test-Path $committed)) { Fail "no committed copy at $($pair.Committed). Copy the fresh file there and commit it:  Copy-Item '$fresh' '$committed'" }

    $freshHash = (Get-FileHash $fresh -Algorithm SHA256).Hash.ToLower()
    $committedHash = (Get-FileHash $committed -Algorithm SHA256).Hash.ToLower()

    Write-Host "    --- $($pair.Committed)"
    Write-Host "    fresh     sha256:$freshHash"
    Write-Host "    committed sha256:$committedHash"

    if ($freshHash -ne $committedHash) {
        Write-Host ""
        Write-Host "    The committed copy is not what the prover produced. Diff:" -ForegroundColor Yellow
        Compare-Object (Get-Content $committed) (Get-Content $fresh) |
            Select-Object -First 40 |
            Format-Table -AutoSize |
            Out-String |
            Write-Host

        Fail "$($pair.Committed) is stale. Copy the fresh file over it and commit the two together:  Copy-Item '$fresh' '$committed'"
    }

    Write-Host "    identical" -ForegroundColor Green
}

# ─── 6. Build the oracles ────────────────────────────────────────────

Write-Step "6/7  Building the generated project as generated, the .NET oracle, then the Fable host on .NET"

# The project Custard wrote, as generated: its .fsproj over its sources,
# with nothing of this repository's added — the one thing taken away is
# the FS0025 token step 4 drops, and the project inherits
# `oracle/Directory.Build.props`, so it builds under the incomplete-match
# rule (Phase 1037). This is the build that says the project a reader
# would get from the extractor alone stands on .NET 10.
& dotnet build (Join-Path $PSScriptRoot "$committedDir/$generatedProject") --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail "the project Custard generated does not build as generated (exit $LASTEXITCODE)." }

# The .NET oracle the hosts reference compiles the SAME committed files.
# Its file list is checked against Custard's project rather than trusted:
# an extraction Custard wrote that the oracle does not compile would be
# byte-held and never run.
$custardCompiles = @(
    Select-String -Path (Join-Path $PSScriptRoot "$committedDir/$generatedProject") -Pattern '<Compile Include="([^"]+)"' |
        ForEach-Object { $_.Matches[0].Groups[1].Value }
)
$oracleCompiles = @(
    Select-String -Path (Join-Path $PSScriptRoot "oracle/ToolUp.Remoting.Proofs.Oracle.fsproj") -Pattern '<Compile Include="custard/([^"]+)"' |
        ForEach-Object { $_.Matches[0].Groups[1].Value }
)

if (($custardCompiles -join "|") -ne ($oracleCompiles -join "|")) {
    Fail "oracle/ToolUp.Remoting.Proofs.Oracle.fsproj compiles [$($oracleCompiles -join ', ')] but Custard's project lists [$($custardCompiles -join ', ')]. The oracle compiles every generated file, in Custard's order."
}

& dotnet build (Join-Path $PSScriptRoot "oracle/ToolUp.Remoting.Proofs.Oracle.fsproj") --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail "a committed extraction does not compile in the .NET oracle (exit $LASTEXITCODE)." }

# The Fable-host oracle: the three Elmish extractions over the derived
# stand-in. Built on .NET here — the same source compiles on both hosts,
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

Write-Host "PROOF LEG GREEN — $ranLabel, $Runs cold check(s) of $($modules.Count) module(s), every extraction identical to its committed copy." -ForegroundColor Green
exit 0
