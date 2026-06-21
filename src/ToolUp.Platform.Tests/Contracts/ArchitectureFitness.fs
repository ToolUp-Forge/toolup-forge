module ToolUp.Platform.Tests.Contracts.ArchitectureFitness

open System
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
        if source.[i] = '\n' then
            line <- line + 1

    line

/// Matches a top-level `open <DottedNamespace>` capturing the opened
/// path. Multiline so it scans the whole file text in one pass.
let openPattern =
    Regex(@"^[ \t]*open[ \t]+([A-Za-z0-9_.]+)", RegexOptions.Multiline ||| RegexOptions.Compiled)

/// True for build-output / Fable-output paths that must never be scanned
/// (they hold generated copies of source that would double-count or, in
/// the Fable test `output/` case, mirror cross-tier files legitimately).
let isGeneratedPath (path: string) : bool =
    let n = path.Replace('\\', '/')
    n.Contains "/bin/" || n.Contains "/obj/" || n.Contains "/output/"

/// Enumerate `.fs` files under `root` (recursive), skipping generated
/// paths. Returns absolute paths; missing root yields an empty list.
let fsFilesUnder (root: string) : string list =
    if not (Directory.Exists root) then
        []
    else
        Directory.EnumerateFiles(root, "*.fs", SearchOption.AllDirectories)
        |> Seq.filter (isGeneratedPath >> not)
        |> List.ofSeq

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
        let opened = m.Groups.[1].Value

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
        |> Seq.filter (fun d -> not (isGeneratedPath d))
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
        yield m.Groups.[1].Value
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
                let opened = m.Groups.[1].Value

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
        |> Seq.filter (fun d -> not (isGeneratedPath d))
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

// ─── Formatting ───────────────────────────────────────────────────────

let formatEdge (e: ReferenceEdge) : string = sprintf "  %s → %s" e.From e.To

let formatSourceFinding (f: SourceFinding) : string =
    sprintf "  %s:%d — %s" f.File f.Line f.Detail