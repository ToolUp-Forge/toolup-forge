module ToolUp.Platform.Tests.Contracts.ArchitectureFitness

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text.RegularExpressions

// ─── Phase 174 — architecture-fitness helpers ────────────────────────
//
// Pure(ish) helpers that codify the layer boundaries the Phase 15d
// structural reorg established, so a future `ProjectReference` or `open`
// that re-introduces a forbidden edge fails the build instead of going
// unnoticed until a downstream Fable consumer breaks.
//
// Two detection surfaces:
//
//   1. **Reflection over the compiled assembly graph** — the tri-tier
//      direction rule (`Core` references neither `Server` nor `Client`;
//      `Server` does not reference `Client`; `Client` does not reference
//      `Server`) and the AG Grid Enterprise split (`Client` carries no
//      reference to the opt-in `ToolUp.AgGridEnterprise` companion).
//      `Assembly.GetReferencedAssemblies()` reflects the real IL
//      reference set, so a `ProjectReference` that compiles a forbidden
//      edge shows up here even if no `open` does.
//
//   2. **Source-tree string scans** for the rules reflection can't see —
//      infra/framework `open`s under a cross-tier `Shared/` folder
//      (`Microsoft.AspNetCore` / `Giraffe` / `Saturn`), module-to-module
//      `open`s across the `samples/` module set (GP 9), and the AG Grid
//      Enterprise shim leaking into the default-composed `Client` tree.
//
// Every detector is pure over its inputs (assembly-name lists / file
// text), so the companion test file can feed it synthetic "planted
// violation" fixtures and prove the gate fails closed rather than going
// vacuously green. Same text-scan philosophy as the
// `DomAttrCustomAuditTests` / `SubjectWildcardAnalyzer` packs — no
// Roslyn / FCS hook, just `System.Reflection` + `System.IO` + `Regex`.

// ─── Shared shapes ────────────────────────────────────────────────────

/// A forbidden reference edge in the compiled assembly graph.
type ReferenceEdge = {
    /// The tier whose IL carried the forbidden reference.
    From: string
    /// The forbidden referenced assembly (simple name).
    To: string
}

/// A forbidden source-tree construct (an `open` that crosses a boundary).
type SourceFinding = {
    /// Repo-relative path of the offending file.
    File: string
    /// 1-indexed line of the offending `open`.
    Line: int
    /// Human-readable explanation of why it's forbidden.
    Detail: string
}

// ─── Reflection: assembly-graph direction ─────────────────────────────

/// Simple (comma-stripped) name of a fully-qualified assembly name.
let simpleName (fullName: string) : string =
    match fullName.IndexOf(',') with
    | -1 -> fullName
    | i -> fullName.Substring(0, i)

/// Load a tier assembly by simple name. Prefers an already-loaded
/// instance (the test project references Core/Server/Client directly and
/// exercises all three, so they're in the default load context), falling
/// back to `Assembly.Load` resolving from the test bin directory.
let loadAssembly (asmSimpleName: string) : Assembly =
    AppDomain.CurrentDomain.GetAssemblies()
    |> Array.tryFind (fun a -> a.GetName().Name = asmSimpleName)
    |> Option.defaultWith (fun () -> Assembly.Load asmSimpleName)

/// Simple names of every assembly the given assembly directly references.
let referencedSimpleNames (asm: Assembly) : string list =
    asm.GetReferencedAssemblies()
    |> Array.choose (fun an -> Option.ofObj an.Name)
    |> List.ofArray

/// Pure direction check: given a tier's name + the simple names it
/// references, surface an edge for every reference in `forbidden`.
let forbiddenEdges (tier: string) (referenced: string seq) (forbidden: Set<string>) : ReferenceEdge list =
    referenced
    |> Seq.filter forbidden.Contains
    |> Seq.distinct
    |> Seq.map (fun target -> { From = tier; To = target })
    |> List.ofSeq

// ─── Source-tree scan primitives ──────────────────────────────────────

/// Repo root (`toolup-forge`) resolved from the executing test assembly:
/// `bin/Debug/net10.0` → `ToolUp.Platform.Tests` → `src` → `toolup-forge`.
let repoRoot () =
    let asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."))

/// 1-indexed line number of a character offset within source text.
let lineOf (source: string) (offset: int) : int =
    let mutable line = 1

    for i in 0 .. min (offset - 1) (source.Length - 1) do
        if source[i] = '\n' then
            line <- line + 1

    line

/// Matches a top-level `open <DottedNamespace>` capturing the opened
/// path. Multiline so it scans the whole file text in one pass.
let openPattern =
    Regex(@"^[ \t]*open[ \t]+([A-Za-z0-9_.]+)", RegexOptions.Multiline ||| RegexOptions.Compiled)

/// `path` expressed relative to `root`, forward-slashed and framed with a
/// leading slash, so a `"/segment/"` test matches a real path segment and
/// nothing above the checkout.
///
/// Every path test below goes through this. Testing the ABSOLUTE path
/// instead reads the machine's directory layout rather than the repo's,
/// and that is not hypothetical: this file's own sweeps run inside
/// `.claude/worktrees/<name>/` whenever a session verifies in an isolated
/// worktree, where every absolute path contains `/.claude/` — so an
/// absolute prune classifies the entire checkout under test as foreign,
/// empties the sweep, and trips the source-count floor. That is the
/// second of the two failures this gate has actually produced.
let pathUnder (root: string) (path: string) : string =
    let r = root.Replace('\\', '/').TrimEnd('/')
    let p = path.Replace('\\', '/')

    let rel =
        if p.StartsWith(r, StringComparison.OrdinalIgnoreCase) then
            p.Substring r.Length
        else
            p

    "/" + rel.TrimStart('/')

/// True for build-output / Fable-output paths under `root` that must
/// never be scanned (they hold generated copies of source that would
/// double-count or, in the Fable test `output/` case, mirror cross-tier
/// files legitimately).
let isGeneratedPathUnder (root: string) (path: string) : bool =
    let n = pathUnder root path
    n.Contains "/bin/" || n.Contains "/obj/" || n.Contains "/output/"

/// `isGeneratedPathUnder` anchored at the live repo root.
let isGeneratedPath (path: string) : bool = isGeneratedPathUnder (repoRoot ()) path

/// Path segments that are not this repo's source. Used only by the
/// fallback walk in `enumerateCheckoutFiles` — the primary enumeration
/// asks git, which needs no such list (see the Phase 635 block below).
///
/// `.claude/` is the load-bearing one. It holds `worktrees/<name>/` — a
/// concurrent agent session's isolated git worktree, i.e. a COMPLETE
/// second checkout of this tree sitting inside it. A sweep that walks it
/// does not merely double-count: the repo-relative exemption prefixes
/// (`fs0025ExemptPrefixes`) are anchored at the root, and a worktree copy
/// is not, so `.claude/worktrees/x/docs-snippets/…` fails to match the
/// `docs-snippets/` exemption and an out-of-scope subtree re-enters the
/// gate under a different name. The gate then fails on files that are
/// neither ours nor in scope, and only while some other session happens
/// to have a worktree open — a false red that appears and disappears
/// with no change to this repo at all.
///
/// `node_modules/` is vendored npm packages (they do ship MSBuild
/// `.props` files, and a vendored one is not ours to gate); `artifacts/`
/// is `Publish` output; `.git/` is repository metadata.
let prunedPathSegments = [ "/.claude/"; "/.git/"; "/node_modules/"; "/artifacts/" ]

/// True for a path under any pruned segment, measured relative to `root`.
/// Complements `isGeneratedPathUnder` — that one covers build output *of*
/// our source, this one covers trees that are not our source at all.
///
/// Relative, not absolute, and that distinction is load-bearing: see
/// `pathUnder`. A checkout that itself lives under `.claude/worktrees/`
/// must be measured as the checkout it is, not pruned out of existence.
let isPrunedPathUnder (root: string) (path: string) : bool =
    let n = pathUnder root path

    prunedPathSegments |> List.exists n.Contains

/// `isPrunedPathUnder` anchored at the live repo root.
let isPrunedPath (path: string) : bool = isPrunedPathUnder (repoRoot ()) path

// ─── Checkout scope — what the sweeps may look at (Phase 635) ─────────
//
// Every source-tree sweep below is rooted at the repo root, so it is only
// as correct as its notion of "this checkout". A directory walk gets that
// wrong in a way that stays invisible until it isn't: `.claude/worktrees/
// <name>/` is a COMPLETE second checkout of this tree, created and
// destroyed by concurrent agent sessions, and a walk that descends into
// one measures the machine rather than the repo. Both observed failures
// were that shape and both were spurious — a stale worktree's
// `docs-snippets/ToolUp.DocSnippets.fsproj` (which legitimately carries
// `NoWarn FS0025`, and is exempt at `docs-snippets/` but NOT at
// `.claude/worktrees/x/docs-snippets/`) failed the no-self-exemption gate
// for every session on the machine, and a verification worktree tripped
// the source-count floor.
//
// The fix is not a longer prune list. A prune list is a second copy of
// `.gitignore` that drifts the moment anyone adds an ignore rule, and it
// only ever excludes the paths someone already got burned by. Ask git
// instead: `ls-files --cached --others --exclude-standard` is precisely
// "the files belonging to THIS checkout" — gitignore-aware by
// construction, and git refuses to descend into a nested checkout at all,
// so a worktree parked anywhere, not only under `.claude/`, is excluded
// for free.
//
// `--others` (untracked but not ignored) is deliberate rather than
// tracked-only: a violation introduced in a file that is not committed
// yet must still fail locally, otherwise the gate only ever speaks after
// the fact.
//
// The prune-list walk survives as the fallback for a checkout with no git
// CLI (a source tarball). It is strictly weaker — it cannot see
// `.gitignore` — and it is not the path this repo takes; the Phase 635
// tests hold the git path to the stronger bar and pin the fallback to the
// `.claude/` case it was written for.

/// Run `git` in `workingDir`, returning stdout on a zero exit. `None`
/// whenever git cannot answer — no CLI on PATH, not a work tree, non-zero
/// exit, timeout — so every caller must carry a fallback.
let private runGit (workingDir: string) (args: string list) : string option =
    try
        let psi = ProcessStartInfo("git")
        psi.WorkingDirectory <- workingDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        for a in args do
            psi.ArgumentList.Add a

        use proc = Process.Start psi

        // Drain stderr concurrently — a full stderr pipe deadlocks a
        // process whose stdout we are reading to the end.
        let stderr = proc.StandardError.ReadToEndAsync()
        let stdout = proc.StandardOutput.ReadToEnd()

        if proc.WaitForExit 60_000 then
            stderr.Wait 5_000 |> ignore
            if proc.ExitCode = 0 then Some stdout else None
        else
            None
    with _ ->
        None

/// Absolute paths of every file git considers part of the checkout at
/// `root` — tracked, plus untracked-but-not-ignored. `None` when git
/// cannot answer (see `runGit`); callers fall back to the prune walk.
let gitCheckoutFiles (root: string) : string list option =
    runGit root [
        "--no-optional-locks"
        "ls-files"
        "-z"
        "--cached"
        "--others"
        "--exclude-standard"
    ]
    |> Option.map (fun stdout ->
        stdout.Split('\000')
        |> Array.filter (fun rel -> rel <> "")
        |> Array.map (fun rel -> Path.GetFullPath(Path.Combine(root, rel)))
        |> Array.distinct // an unmerged path is listed once per stage
        |> Array.filter File.Exists // index entries deleted from the tree
        |> List.ofArray)

/// Every file of the checkout at `root` the fitness sweeps may look at.
/// Git-derived where git can answer; the prune-list walk otherwise.
/// Un-memoised — the tests call it against synthetic trees.
let enumerateCheckoutFiles (root: string) : string list =
    match gitCheckoutFiles root with
    | Some files -> files |> List.filter (isGeneratedPathUnder root >> not)
    | None ->
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        |> Seq.filter (isGeneratedPathUnder root >> not)
        |> Seq.filter (isPrunedPathUnder root >> not)
        |> List.ofSeq

/// The live checkout's file set, resolved once per test run — the sweeps
/// below share it rather than each paying for its own walk.
let private repoFilesCache = lazy (enumerateCheckoutFiles (repoRoot ()))

/// Memoised `enumerateCheckoutFiles` over the live repo root.
let repoFiles () : string list = repoFilesCache.Value

/// Every `.fs` file of this checkout under `root` (recursive). Scoped by
/// `repoFiles`, so build output, vendored packages, and a concurrent
/// session's worktree are already gone. Missing root yields an empty list.
let fsFilesUnder (root: string) : string list =
    if not (Directory.Exists root) then
        []
    else
        let prefix =
            Path.GetFullPath(root).TrimEnd([| '/'; '\\' |])
            + string Path.DirectorySeparatorChar

        repoFiles ()
        |> List.filter (fun p ->
            p.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
            && p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))

/// Repo-relative, forward-slashed rendering of an absolute path.
let relative (absolute: string) : string =
    let root = repoRoot ()

    let trimmed =
        if absolute.StartsWith root then
            absolute.Substring(root.Length)
        else
            absolute

    trimmed.TrimStart([| '/'; '\\' |]).Replace('\\', '/')

/// Pure open-scan: for every `open` in `source`, consult `classify`; a
/// `Some detail` result becomes a finding. `filename` is echoed verbatim
/// into the finding so callers control the displayed path.
let scanOpens (classify: string -> string option) (filename: string) (source: string) : SourceFinding list = [
    for m in openPattern.Matches source do
        let opened = m.Groups[1].Value

        match classify opened with
        | Some detail ->
            yield {
                File = filename
                Line = lineOf source m.Index
                Detail = detail
            }
        | None -> ()
]

// ─── Rule: no infra/framework opens under a Shared/ folder (GP 10) ────

/// Namespace prefixes that are server/framework infrastructure and must
/// never be `open`ed from a cross-tier `Shared/` file — a `Shared` file
/// compiles on the Fable client too, so an ASP.NET Core / Giraffe open
/// breaks the client build (or worse, leaks an infra type into a
/// cross-tier contract).
let infraOpenPrefixes = [ "Microsoft.AspNetCore"; "Giraffe"; "Saturn" ]

/// Classifier for the Shared-folder infra rule.
let classifyInfraOpen (opened: string) : string option =
    infraOpenPrefixes
    |> List.tryFind (fun p -> opened = p || opened.StartsWith(p + "."))
    |> Option.map (fun p ->
        sprintf
            "`open %s` is forbidden under a Shared/ folder — infra/framework types must not enter the cross-tier shared layer (GP 10). Matched prefix `%s`."
            opened
            p)

/// Every `.fs` file that sits under a folder named exactly `Shared`
/// anywhere in `src/` (generated paths excluded).
let sharedTierFiles () : string list =
    let srcRoot = Path.Combine(repoRoot (), "src")

    if not (Directory.Exists srcRoot) then
        []
    else
        Directory.EnumerateDirectories(srcRoot, "Shared", SearchOption.AllDirectories)
        |> Seq.filter (fun d -> not (isGeneratedPath d || isPrunedPath d))
        |> Seq.collect fsFilesUnder
        |> Seq.distinct
        |> List.ofSeq

// ─── Rule: AG Grid Enterprise stays off the default Client path (GP 2) ─

/// The Enterprise companion's module roots — opening either from the
/// default-composed `ToolUp.Platform.Client` tree would pull the paid
/// tier onto the path every consumer composes by default.
let enterpriseModuleRoots = [ "AgGridEnterprise"; "AgGridEnterpriseTypes" ]

/// Classifier for the AG Grid Enterprise split rule.
let classifyEnterpriseOpen (opened: string) : string option =
    enterpriseModuleRoots
    |> List.tryFind (fun r -> opened = r || opened.StartsWith(r + "."))
    |> Option.map (fun r ->
        sprintf
            "`open %s` is forbidden in ToolUp.Platform.Client — the AG Grid Enterprise init shim lives only in the opt-in AgGridEnterprise companion (GP 2). Matched root `%s`."
            opened
            r)

/// Every `.fs` file under the default-composed `ToolUp.Platform.Client`
/// project tree (generated paths excluded).
let clientTierFiles () : string list =
    fsFilesUnder (Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client"))

// ─── Rule: sample modules are self-contained (GP 9) ───────────────────

/// One sample-module compilation unit: the declared module/namespace
/// roots it owns plus the (filename, source) pairs to scan.
type ModuleUnit = {
    UnitId: string
    /// Fully-qualified `module` / `namespace` declarations this unit owns.
    Decls: Set<string>
    Files: (string * string) list
}

let private declPattern =
    Regex(@"^[ \t]*(?:module|namespace)[ \t]+(?:rec[ \t]+)?([A-Za-z0-9_.]+)", RegexOptions.Multiline)

/// Declared module/namespace paths in a source text.
let declaredNamespaces (source: string) : string list = [
    for m in declPattern.Matches source do
        yield m.Groups[1].Value
]

let private touches (a: string) (b: string) : bool =
    a = b || a.StartsWith(b + ".") || b.StartsWith(a + ".")

/// Pure cross-module check: flag any `open` in one unit that resolves to
/// a namespace owned by a *different* sample-module unit. Intra-unit
/// opens (a module opening its own `SharedTypes` / `ClientModel`) are
/// allowed — only cross-unit imports violate GP 9.
let crossModuleOpenFindings (units: ModuleUnit list) : SourceFinding list = [
    for unit in units do
        let otherDecls =
            units
            |> List.filter (fun u -> u.UnitId <> unit.UnitId)
            |> List.collect (fun u -> u.Decls |> Set.toList |> List.map (fun d -> u.UnitId, d))

        for (filename, source) in unit.Files do
            for m in openPattern.Matches source do
                let opened = m.Groups[1].Value

                match otherDecls |> List.tryFind (fun (_, d) -> touches opened d) with
                | Some(ownerUnit, d) ->
                    yield {
                        File = filename
                        Line = lineOf source m.Index
                        Detail =
                            sprintf
                                "sample module `%s` opens `%s`, owned by sibling sample module `%s` (decl `%s`) — modules must be self-contained (GP 9)."
                                unit.UnitId
                                opened
                                ownerUnit
                                d
                    }
                | None -> ()
]

/// Build the live sample-module unit set from `samples/`: one unit per
/// directory whose name ends with `.Module`. Each unit's `Decls` are the
/// declared module/namespace paths across its files.
let sampleModuleUnits () : ModuleUnit list =
    let samplesRoot = Path.Combine(repoRoot (), "samples")

    if not (Directory.Exists samplesRoot) then
        []
    else
        Directory.EnumerateDirectories(samplesRoot, "*.Module", SearchOption.AllDirectories)
        |> Seq.filter (fun d -> not (isGeneratedPath d || isPrunedPath d))
        |> Seq.map (fun dir ->
            let files =
                fsFilesUnder dir |> List.map (fun path -> relative path, File.ReadAllText path)

            let decls = files |> List.collect (snd >> declaredNamespaces) |> Set.ofList

            {
                UnitId = relative dir
                Decls = decls
                Files = files
            })
        |> List.ofSeq

// ─── Rule: FS0025 stays a build error (Phase 624) ─────────────────────
//
// `Directory.Build.props` promotes FS0025 (incomplete pattern match)
// from a warning to an error tree-wide. That gate has three ways to be
// defeated, and only one of them is the wildcard everyone worries about:
//
//   1. `#nowarn "25"` at the top of a source file — silences the check
//      for the WHOLE file, permanently and invisibly.
//   2. `FS0025` in a project's `NoWarn` / `WarningsNotAsErrors` —
//      silences it for the whole PROJECT.
//   3. a `| _ ->` wildcard arm — silences it for one match expression.
//
// (1) and (2) are strictly worse than (3): they are broad, they are far
// from the code they affect, and nothing in a later diff hints that a
// table stopped being checked. They are also exactly detectable by text,
// so this rule pins them at zero. What remains is (3), which is at least
// local and lands on the diff line the compiler just pointed at.
//
// The wildcard itself is deliberately NOT counted here. It emits no
// diagnostic at all, so no census over compiler output can see it, and a
// blanket count is meaningless in a tree with ~2,400 legitimate
// wildcards over options, strings and ints. Deciding whether a given
// wildcard hides missing DU cases needs the scrutinee's TYPE, which
// needs a typed whole-repo check this repo does not have. Where a table
// over a closed DU must provably stay in step, the wildcard-immune
// mechanism is a reflection census over `FSharpType.GetUnionCases` —
// see `AuditEventRegistryTests` (Phase 114), which never reads the match
// expression and therefore cannot be fooled by a wildcard at all.

/// Spellings of warning 25 that MSBuild / the F# compiler both accept.
let fs0025Spellings = Set.ofList [ "25"; "FS25"; "FS0025" ]

/// Split an MSBuild warning-list property value (`FS0025;NU5128`) into
/// comparable tokens, dropping property references like `$(NoWarn)`.
let warningListTokens (value: string) : string list =
    value.Split([| ';'; ','; ' '; '\t'; '\n'; '\r' |])
    |> Array.map (fun t -> t.Trim().ToUpperInvariant())
    |> Array.filter (fun t -> t <> "" && not (t.StartsWith "$("))
    |> List.ofArray

/// True when a warning-list property value names FS0025.
let listSuppressesFs0025 (value: string) : bool =
    warningListTokens value |> List.exists fs0025Spellings.Contains

/// A `#nowarn` directive and the warning ids it names (quoted or bare).
let nowarnPattern =
    Regex(@"^[ \t]*#nowarn[ \t]+(.+)$", RegexOptions.Multiline ||| RegexOptions.Compiled)

/// MSBuild properties that would exempt a whole project from the gate.
let projectSuppressionPattern =
    Regex(@"<(NoWarn|WarningsNotAsErrors)[^>]*>([^<]*)</\1>", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// Findings for file-scoped `#nowarn` suppressions of FS0025.
let scanNowarnSuppressions (filename: string) (source: string) : SourceFinding list = [
    for m in nowarnPattern.Matches source do
        let ids = m.Groups[1].Value.Replace("\"", " ")

        if listSuppressesFs0025 ids then
            yield {
                File = filename
                Line = lineOf source m.Index
                Detail =
                    "`#nowarn \"25\"` silences the incomplete-match gate for this ENTIRE file (Phase 624). Enumerate the missing cases instead; if a table over a closed DU must stay in step, gate it by reflection over its union cases (see AuditEventRegistryTests) rather than switching the compiler off."
            }
]

/// Findings for project-scoped `NoWarn` / `WarningsNotAsErrors` exemptions.
let scanProjectSuppressions (filename: string) (source: string) : SourceFinding list = [
    for m in projectSuppressionPattern.Matches source do
        if listSuppressesFs0025 m.Groups[2].Value then
            yield {
                File = filename
                Line = lineOf source m.Index
                Detail =
                    sprintf
                        "`<%s>` names FS0025, exempting this ENTIRE project from the incomplete-match gate (Phase 624). Fix the matches instead; a deliberate exemption belongs in the documented list in Directory.Build.props, not added quietly here."
                        m.Groups[1].Value
            }
]

/// A subtree the gate deliberately does not cover.
type Fs0025Exemption = {
    /// Repo-relative path prefix, forward-slashed.
    Prefix: string
    /// Why this subtree is out of scope. Full reasoning lives beside the
    /// policy in Directory.Build.props.
    Reason: string
}

/// Repo-relative path prefixes deliberately exempt from the gate, each
/// with its reason. Kept here (rather than left implicit) so the
/// exemption set is reviewable — a policy that silently exempts part of
/// the tree reads as stronger than it is. Mirrors the documented list in
/// `Directory.Build.props`; a test asserts the two agree, so an
/// exemption cannot be widened here without saying so where authors read
/// the policy.
let fs0025ExemptPrefixes: Fs0025Exemption list = [
    {
        Prefix = "docs-snippets/"
        Reason = "generated doc snippets — prose that must resolve, not lint-clean code; outside the sln (Phase 620)"
    }
    {
        Prefix = "templates/safer/"
        Reason = "template content — its own Directory.Build.props has no parent import, so the policy never reaches it"
    }
    {
        Prefix = "templates/platformsdk-solution/"
        Reason = "template content — same structural reason as templates/safer/"
    }
]

/// Extensions the gate applies to: F# sources (`#nowarn`) and the
/// MSBuild files that could carry a project-wide exemption.
let private fs0025ScannedExtensions =
    Set.ofList [ ".fs"; ".fsx"; ".fsproj"; ".props" ]

/// Every source / project file the gate applies to: this checkout's file
/// set (`repoFiles` — git-derived, so build output, vendored npm
/// packages and a concurrent session's `.claude/worktrees/` checkout are
/// all already absent), filtered to the scanned extensions and to the
/// subtrees the policy does not cover.
let fs0025ScannableFiles () : string list =
    let exemptPrefixes = fs0025ExemptPrefixes |> List.map _.Prefix

    repoFiles ()
    |> List.filter (fun p -> fs0025ScannedExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
    |> List.filter (fun p ->
        let rel = relative p
        not (exemptPrefixes |> List.exists rel.StartsWith))

/// The tree-wide policy declaration, read from `Directory.Build.props`.
/// A gate that can be deleted to make a build go green is not a gate, so
/// its presence is asserted like any other rule.
let policyDeclaresFs0025AsError () : bool =
    let path = Path.Combine(repoRoot (), "Directory.Build.props")

    if not (File.Exists path) then
        false
    else
        let text = File.ReadAllText path

        Regex.Matches(text, @"<WarningsAsErrors[^>]*>([^<]*)</WarningsAsErrors>", RegexOptions.IgnoreCase)
        |> Seq.exists (fun m -> listSuppressesFs0025 m.Groups[1].Value)

// ─── Formatting ───────────────────────────────────────────────────────

let formatEdge (e: ReferenceEdge) : string = sprintf "  %s → %s" e.From e.To

let formatSourceFinding (f: SourceFinding) : string =
    sprintf "  %s:%d — %s" f.File f.Line f.Detail