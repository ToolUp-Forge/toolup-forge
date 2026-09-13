module ToolUp.Platform.Tests.Contracts.PublicApiApproval

// ─── Phase 175 — Public-API approval / baseline (SemVer guard) ───────
//
// Renders a deterministic text snapshot of the *public surface* of every
// packable `ToolUp.*` assembly (the `Pack`-walked, `IsPackable != false`
// set) and diffs it against a committed baseline under
// `toolup-forge/api-baselines/<assembly>.approved.txt`. The diff is the
// mechanical enforcement of the SemVer-on-`0.x` policy (GP 11): the public
// surface must not silently break between releases.
//
// ── Drift policy: BOTH directions fail the gate (Phase 618) ──
// A baseline is an ordered set of one-line member tokens. Comparing a
// freshly-rendered surface against its committed baseline:
//   * a token present in BOTH                       → unchanged, fine.
//   * a token in the baseline but NOT the rendered
//     surface (a REMOVED / RENAMED / RETYPED member
//     — a rename or retype reads as the old token
//     vanishing)                                    → BREAKING. The test
//     fails and names every lost token. Accepting the break is a
//     deliberate, reviewed edit of the `.approved.txt` file in the SAME
//     PR (regenerate with the env flag below), so the breaking diff is
//     visible in review — mirroring GP 11 (`0.x` minor MAY break, patch
//     MUST NOT; either way the baseline edit is the checkpoint).
//   * a token in the rendered surface but NOT the
//     baseline (a NEW public type / member, or an
//     additive overload)                            → UNFOLDED ADDITION.
//     Still NON-BREAKING — additive growth remains allowed under `0.x`
//     minor/patch — but the test fails until the baseline is regenerated
//     and committed alongside the change.
//
// ── Why an addition fails at all (Phase 618) ──
// Phase 175 shipped this comparer removal-only, reasoning that additive
// growth is auto-acceptable. It is; but "auto-acceptable" was implemented
// as "silent", and silence is not acceptance. A surface-growing change got
// a green gate and NO signal that a baseline had moved, so drift piled up
// until some unrelated work was forced to regenerate and found foreign
// members in its diff. The 2026-07-30 sweep folded 457 added lines across
// five baselines from ten originating sources; the fortnight after it
// produced 20 more from two more. The leak set is not enumerable from
// memory — only regeneration reveals it — so a periodic sweep can only ever
// be reactive. Failing on an addition changes WHEN the growth is folded,
// not WHETHER it is allowed, and the two failure messages below say so in
// as many words: the reader of a red CI run is precisely the person who
// most needs to know they have not broken anything.
//
// ── Phase 258 (`[<Obsolete>]` deprecation lifecycle) — SHIPPED ──
// Phase 618 settled this interaction in advance and left the seam
// (`obsoleteMarker`) with nothing calling it. Phase 258 wired it up and
// added the message-policy arm. What 618 decided is unchanged and is
// restated here because it is the reason no comparer exception exists:
//
//   SANCTIONED (two lines)                FORBIDDEN (token rewritten in place)
//     Demo.T.Alpha() : System.Int32         Demo.T.Alpha() : System.Int32 [obsolete]
//     Demo.T.Alpha() : System.Int32  (obsolete)
//
// The forbidden shape fires BOTH arms at once: the member's original token
// vanishes — which this comparer cannot distinguish from a real removal, so
// it reports a BREAKING change for what policy calls a minor — and a new
// token appears alongside. The sanctioned shape leaves the member token
// untouched, so the removal arm stays silent and the marker is an ordinary
// unfolded addition: the deprecation is folded into the baseline in the
// same PR and reviewed there, which is exactly the checkpoint Phase 258
// asks for. Phase 258 therefore needed NO exception in the comparer —
// only the rendering convention. Both shapes stay pinned by fixtures in
// `PublicApiApprovalTests.fs` so the rule cannot be undone by accident.
//
// Phase 258 added two things on top of that decision:
//
//   1. `renderType` now EMITS the marker. Every rendered token — a type
//      header included — whose declaring member carries `[<Obsolete>]`
//      gets its `obsoleteMarker` line beside it. Until this landed the
//      renderer emitted no attributes at all, so a deprecation was
//      invisible to the gate and the seam above was dead code: a
//      convention nothing could violate because nothing exercised it.
//      Wiring it makes a deprecation a reviewed baseline diff, which is
//      the whole point of the policy.
//
//      The marker deliberately carries NO MESSAGE. Baseline tokens are
//      compared as an ordered set, so folding the message in would make
//      every reworded deprecation notice read as a removal-plus-addition
//      — a BREAKING diff for a copy edit. The message is checked by (2)
//      instead, where its content is the subject rather than a token.
//
//   2. `obsoleteDefect` / `describeObsoleteDefects` — the message policy
//      from `docs/platform/deprecation-policy.md`, as a gate. An
//      `[<Obsolete>]` on the public surface must name a REPLACEMENT and a
//      REMOVAL TARGET; a bare `[<Obsolete>]`, or one whose message omits
//      either half, fails the pack. Scoped to the rendered public surface
//      rather than to a source grep, which is why vendored internals are
//      out of scope by CONSTRUCTION rather than by an exclusion list —
//      `TypeShape.fs` is `module internal`, carries two upstream
//      `[<Obsolete>]` members that name no removal target, and is
//      correctly never seen here. The policy governs what the SDK
//      publishes; it has no business policing code no consumer can call.
//
// ── Regeneration path (`--update`) ──
// Set `TOOLUP_APPROVE_API=1` and run the Platform pack; every baseline is
// rewritten from the live surface (additive growth folded in, deliberate
// removals accepted) deterministically — sorted by type then member, so
// re-runs produce no spurious reordering diffs:
//
//   $env:TOOLUP_APPROVE_API = "1"
//   dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
//   $env:TOOLUP_APPROVE_API = $null
//
// **Scope it.** `=1` rewrites all ~95 baselines, which is how a session
// regenerating one assembly ends up hand-reverting other sessions'
// hunks. Name the assemblies instead — see `approveScope`:
//
//   $env:TOOLUP_APPROVE_API = "ToolUp.Platform.Core,ToolUp.Platform.Server"
//
// A scope naming an assembly the run does not discover FAILS rather than
// regenerating nothing, because a filtered regen that silently matched
// nothing is indistinguishable from one that worked.
//
// ── Why MetadataLoadContext ──
// The packable set spans ~90 assemblies, most of which the Tests project
// does NOT reference (Forms.* / Scheduling.* / Stripe.* / Hosts / Encryption
// / …). In-process reflection would silently cover only the referenced
// subset. MetadataLoadContext loads any built DLL from disk uniformly,
// metadata-only — no execution, no runtime load of Fable client assemblies
// — so coverage tracks the Pack set, not the Tests dep graph. A discovered
// packable assembly with NO committed baseline FAILS the test (a new public
// package cannot silently escape the guard).
//
// ── Unbuilt assemblies are ONE finding (Phase 731) ──
// A packable assembly not built in the active config used to fail its own
// case with a "build the solution first" message. Each message was right
// and the SHAPE was wrong: an unbuilt tree is a single fact, and answering
// it with 52 independent assertion failures reads as a catastrophic surface
// break — which is how it was read, costing a session. The DLL is now
// resolved when a case RUNS (`resolveDll`) rather than probed once at
// discovery, and the missing build is reported once by `describeUnbuilt`
// with the per-assembly cases deferring to it. The run is still red; a
// precondition that let the pack report green would be a vacuous pass.
// `VerifyAll` builds the solution itself before any pack, so this only
// arises for a pack run on its own.
//
// This is test-tier + repo-baseline only — zero shipped code, a consumer
// deployment is byte-for-byte unchanged (GP 13).

open System
open System.IO
open System.Reflection
open System.Text
open System.Text.RegularExpressions

// Phase 260 - the pure comparer, extracted verbatim so the release
// bump check runs the SAME set difference this gate does. See that
// file for why a second copy of it would be a correctness defect.
open ToolUp.Platform.Tests.Contracts.SurfaceDiff

// ─── Repo / config grounding ─────────────────────────────────────────

/// Repo root (toolup-forge) derived from the running test assembly:
/// bin/<Config>/net10.0/ToolUp.Platform.Tests.dll → up 5 = repo root.
let repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

/// Active build config ("Debug" / "Release") inferred from the running
/// test assembly path — the same config every packable DLL was built in
/// for this run.
let activeConfig () =
    let dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    let parent = DirectoryInfo(dir).Parent // net10.0 → Config
    if isNull parent then "Debug" else parent.Name

/// `toolup-forge/api-baselines/` — the committed `.approved.txt` files.
let baselineDir (root: string) = Path.Combine(root, "api-baselines")

/// Regeneration path: `TOOLUP_APPROVE_API=1` rewrites baselines instead
/// of comparing. Whether ANY regeneration is armed — see
/// `approveModeFor` for which assemblies it actually covers.
let approveModeOn () =
    match Environment.GetEnvironmentVariable "TOOLUP_APPROVE_API" with
    | null
    | "" -> false
    | _ -> true

/// The assemblies a regeneration run is scoped to, or `None` for "every
/// discovered baseline" — the historical, and still the default,
/// behaviour of `TOOLUP_APPROVE_API=1`.
///
/// **Why a scope exists at all.** `=1` rewrites every built baseline —
/// ~95 files — so a session regenerating ONE assembly's surface also
/// folds in whatever unrelated additive drift and EOL churn the tree
/// happens to be carrying, including files a concurrent session has in
/// flight. Three agents in one day had to hand-revert foreign hunks
/// before committing (Phase 318 ship report). The recipe those sessions
/// were following — regen everything, then `git restore` all but your
/// targets — works, but it is a manual filter applied AFTER the damage
/// is on disk, and it only protects the baselines the session thought to
/// name.
///
/// So: `TOOLUP_APPROVE_API=ToolUp.Platform.Core` regenerates exactly that
/// one, and a comma- or semicolon-separated list scopes to several:
///
///   $env:TOOLUP_APPROVE_API = "ToolUp.Platform.Core,ToolUp.Platform.Server"
///
/// `=1` / `=true` keep meaning "all of them", so nothing that worked
/// before changes (GP 11). Matching is by assembly name — the baseline
/// file stem — case-insensitively, with a trailing `.approved.txt`
/// tolerated so a name pasted from a failure message works.
let approveScope () : Set<string> option =
    match Environment.GetEnvironmentVariable "TOOLUP_APPROVE_API" with
    | null
    | "" -> None
    | v when v = "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) -> None
    | v ->
        v.Split([| ','; ';' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun name ->
            let trimmed = name.Trim()

            if trimmed.EndsWith(".approved.txt", StringComparison.OrdinalIgnoreCase) then
                trimmed.Substring(0, trimmed.Length - ".approved.txt".Length)
            else
                trimmed)
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Set.ofArray
        |> Some

/// Whether THIS assembly's baseline is to be rewritten on this run.
let approveModeFor (assemblyName: string) =
    approveModeOn ()
    && (match approveScope () with
        | None -> true
        | Some names ->
            names
            |> Set.exists (fun n -> n.Equals(assemblyName, StringComparison.OrdinalIgnoreCase)))

// ─── Packable-set discovery (mirrors the Pack glob) ──────────────────

type PackableAssembly = {
    /// Output assembly name (== the rendered baseline file stem).
    Name: string
    ProjectPath: string
    /// Directory holding the project. The built DLL is resolved FROM this
    /// at case-execution time (`resolveDll`) rather than probed here —
    /// see the note on `discoverPackable`.
    ProjectDir: string
}

// Directory-based exclusions Pack applies that are NOT expressed via
// `<IsPackable>false</IsPackable>` (reference-app + private-package dirs).
// Most are absent in the OSS forge tree; kept for faithfulness to Pack.
let private excludedDirSegments = [
    "ToolUpApp-Server"
    "ToolUpApp-Client"
    "Modules"
    "TestHarness"
    "ToolUp.Algorithms"
]

let private isPackableProject (fsprojPath: string) =
    let text = File.ReadAllText fsprojPath

    let notMarkedUnpackable =
        not (text.Replace(" ", "").Contains "<IsPackable>false</IsPackable>")

    let name = Path.GetFileNameWithoutExtension fsprojPath
    let notTestProject = not (name.EndsWith ".Tests")
    // FSharp.Analyzers.SDK packages ship analyzer entry points consumed as
    // build-time tooling (PrivateAssets="all"), not a consumer-callable API
    // — there is no public surface to SemVer-guard, and they pull a distinct
    // (FCS) dependency graph. Excluded by reference, not by name.
    let notAnalyzer = not (text.Contains "FSharp.Analyzers.SDK")
    let normalised = fsprojPath.Replace('\\', '/')

    let notExcludedDir =
        excludedDirSegments
        |> List.forall (fun seg -> not (normalised.Contains(sprintf "/%s/" seg)))

    notMarkedUnpackable && notTestProject && notAnalyzer && notExcludedDir

/// `<AssemblyName>` override, else the project filename (== the on-disk
/// DLL stem — several companions override it, e.g. AzureBlobStorage.fsproj
/// → ToolUp.Storage.AzureBlob.dll).
let private assemblyNameOf (fsprojPath: string) =
    let text = File.ReadAllText fsprojPath
    let openTag = "<AssemblyName>"
    let closeTag = "</AssemblyName>"

    match text.IndexOf openTag with
    | -1 -> Path.GetFileNameWithoutExtension fsprojPath
    | i ->
        let start = i + openTag.Length
        let stop = text.IndexOf(closeTag, start)

        if stop < 0 then
            Path.GetFileNameWithoutExtension fsprojPath
        else
            text.Substring(start, stop - start).Trim()

/// Discover every packable assembly under `src/`, sorted by name for a
/// deterministic per-assembly test order.
///
/// **Discovery does NOT probe for built DLLs (Phase 731).** It used to,
/// and the field it filled was read by each per-assembly case — so the
/// answer to "is this assembly built?" was a snapshot taken once, when
/// the module initialised at process start. Two consequences, both
/// observed: a build landing DURING a run was invisible, and a pack run
/// before `dotnet build ToolUp.Forge.sln` had populated the companion
/// bins failed 52 cases independently, which reads as a surface break
/// rather than as the single missing precondition it is. Resolution now
/// happens per case, at execution (`resolveDll`), and the precondition is
/// reported once (`describeUnbuilt`).
/// The `config` argument it used to take went with the probe.
let discoverPackable (root: string) : PackableAssembly list =
    let srcDir = Path.Combine(root, "src")

    Directory.EnumerateFiles(srcDir, "*.fsproj", SearchOption.AllDirectories)
    |> Seq.filter isPackableProject
    |> Seq.map (fun fsproj -> {
        Name = assemblyNameOf fsproj
        ProjectPath = fsproj
        ProjectDir = Path.GetDirectoryName fsproj
    })
    // Two fsprojs can share an <AssemblyName> only by mistake; dedup by
    // name so the baseline set is a clean 1:1 with assemblies.
    |> Seq.distinctBy _.Name
    |> Seq.sortBy _.Name
    |> List.ofSeq

/// The assembly's built DLL in `config`, read from disk NOW. Called per
/// case rather than per discovery — see `discoverPackable`.
let resolveDll (config: string) (a: PackableAssembly) : string option =
    let candidate =
        Path.Combine(a.ProjectDir, "bin", config, "net10.0", a.Name + ".dll")

    if File.Exists candidate then Some candidate else None

/// Those of `assemblies` with no built DLL in `config`, in discovery order.
let unbuiltAssemblies (config: string) (assemblies: PackableAssembly list) : PackableAssembly list =
    assemblies |> List.filter (resolveDll config >> Option.isNone)

/// The ONE message a missing solution build earns (Phase 731).
///
/// `None` when every assembly in `total` is built. Otherwise a single
/// report naming the count and a bounded sample — bounded because the
/// unbuilt case is routinely "all 52 of them", and a report that prints
/// 52 names is the wall of text this exists to replace.
///
/// Lives here rather than inline in the test so it can be falsified
/// directly: a guard whose only evidence is that it passed on a built
/// tree has not been shown able to fire.
let describeUnbuilt (config: string) (total: int) (missing: PackableAssembly list) : string option =
    match missing with
    | [] -> None
    | _ ->
        let sampleSize = 5
        let sample = missing |> List.truncate sampleSize |> List.map _.Name

        let elided =
            match missing.Length - sample.Length with
            | 0 -> ""
            | n -> sprintf ", and %d more" n

        Some(
            sprintf
                "SOLUTION NOT BUILT — %d of %d packable assemblies have no DLL in bin/%s/net10.0: %s%s.\n\nRun `dotnet build ToolUp.Forge.sln` first, then re-run this pack. The Public-API approval gate renders each assembly's surface from its built DLL, so an unbuilt tree has nothing to compare and this is a PRECONDITION, not a public-surface break — no baseline has drifted and nothing needs regenerating. The per-assembly cases below defer to this one finding rather than each reporting the same fact."
                missing.Length
                total
                config
                (String.concat ", " sample)
                elided
        )

// ─── Resolver pool (transitive deps for MetadataLoadContext) ─────────

/// Every `*.dll` under `src/**/bin/<config>/net10.0/` — the union of all
/// packable assemblies + their copied NuGet deps — gives MLC enough to
/// resolve base types / interfaces during surface enumeration.
let resolverPool (root: string) (config: string) : string list =
    let srcDir = Path.Combine(root, "src")
    let needle = sprintf "/bin/%s/net10.0/" config

    Directory.EnumerateFiles(srcDir, "*.dll", SearchOption.AllDirectories)
    |> Seq.filter (fun p ->
        let n = p.Replace('\\', '/')
        n.Contains needle && not (n.Contains "/obj/") && not (n.Contains "/ref/"))
    |> List.ofSeq

// ─── Surface rendering (metadata-only) ───────────────────────────────

let private isCompilerGenerated (attrs: Collections.Generic.IList<CustomAttributeData>) =
    attrs
    |> Seq.exists (fun a -> a.AttributeType.FullName = "System.Runtime.CompilerServices.CompilerGeneratedAttribute")

/// Phase 258 seam — the sanctioned rendering of an `[<Obsolete>]` marking:
/// a SEPARATE line derived from the member's token, never a rewrite of it.
/// See the Phase 258 note in this file's header for why the in-place
/// alternative is forbidden, and why the message is deliberately absent.
let obsoleteMarker (memberToken: string) = memberToken + "  (obsolete)"

/// Phase 258 — the `[<Obsolete>]` message on a declared member or type.
///
/// `None` when the member is not marked. `Some msg` when it is, with `msg`
/// exactly as authored — `""` for a bare `[<Obsolete>]`, which is the one
/// shape the message policy rejects outright. Read metadata-only, so it
/// works under `MetadataLoadContext` like everything else here.
let private obsoleteMessageOf (attrs: Collections.Generic.IList<CustomAttributeData>) : string option =
    attrs
    |> Seq.tryFind (fun a -> a.AttributeType.FullName = "System.ObsoleteAttribute")
    |> Option.map (fun a ->
        // `[<Obsolete>]`               → no ctor args
        // `[<Obsolete("why")>]`        → one string
        // `[<Obsolete("why", true)>]`  → string first, error-flag second
        match a.ConstructorArguments |> Seq.tryHead with
        | Some arg ->
            match arg.Value with
            | :? string as s -> s
            | _ -> ""
        | None -> "")

// Clean, ASSEMBLY-QUALIFIER-FREE type name. `Type.FullName` bakes the
// `, Assembly, Version=x.y.z.w, Culture=…, PublicKeyToken=…` suffix into
// every generic argument — so a routine assembly-version bump would rewrite
// every line and drown the diff. Render generic args recursively from the
// open definition instead, keeping only namespace + name + arity.
let rec private typeName (t: Type) : string =
    if t.IsGenericParameter then
        t.Name
    elif t.IsArray then
        typeName (t.GetElementType()) + "[]"
    elif t.IsByRef then
        typeName (t.GetElementType()) + "&"
    elif t.IsPointer then
        typeName (t.GetElementType()) + "*"
    elif t.IsGenericType then
        let def = t.GetGenericTypeDefinition()
        let baseName = if isNull def.FullName then def.Name else def.FullName
        let args = t.GetGenericArguments() |> Array.map typeName |> String.concat ", "
        sprintf "%s[%s]" baseName args
    else
        match t.FullName with
        | null -> t.Name
        | fn -> fn

let private paramList (ps: ParameterInfo[]) =
    ps |> Array.map (fun p -> typeName p.ParameterType) |> String.concat ", "

let private typeKind (t: Type) =
    if t.IsEnum then
        "enum"
    elif t.IsInterface then
        "interface"
    elif not (isNull t.BaseType) && t.BaseType.FullName = "System.MulticastDelegate" then
        "delegate"
    elif t.IsValueType then
        "struct"
    else
        "class"

let private isAccessor (m: MethodInfo) =
    m.IsSpecialName
    && (m.Name.StartsWith "get_"
        || m.Name.StartsWith "set_"
        || m.Name.StartsWith "add_"
        || m.Name.StartsWith "remove_")

let private memberFlags =
    BindingFlags.Public
    ||| BindingFlags.NonPublic
    ||| BindingFlags.Instance
    ||| BindingFlags.Static
    ||| BindingFlags.DeclaredOnly

// "Public surface" = members callable / overridable from outside the
// assembly: public + protected (family). Internal (assembly) members are
// excluded.
let private methodVisible (m: MethodBase) =
    m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly

let private fieldVisible (f: FieldInfo) =
    f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly

let private methodName (m: MethodInfo) =
    if m.IsGenericMethodDefinition then
        sprintf "%s`%d" m.Name (m.GetGenericArguments().Length)
    else
        m.Name

/// One `[<Obsolete>]`-marked entry of a rendered surface: the member's own
/// surface token, and the message exactly as authored. `Message = ""` is a
/// bare `[<Obsolete>]`. Phase 258 — the input the message policy grades.
type ObsoleteMember = { Token: string; Message: string }

// ─── Phase 261 — doc-comment ids (pure over the same metadata) ───────
//
// The XML documentation file the compiler emits beside each DLL keys
// every documented member by its DOC-COMMENT ID (`T:` / `M:` / `P:` /
// `F:` / `E:` plus an ECMA-shaped signature). To ask "does this member
// of the tracked public surface carry a doc comment?" we therefore need
// the same id for each member the renderer above already walks — which
// is why it is computed HERE, in the one pass, rather than by a second
// walk that could drift from the first in what it considers public.
//
// The id spelling differs from the baseline token spelling in four ways,
// none of them optional:
//   * nested types are `.`-separated, not `+`-separated;
//   * a CONSTRUCTED generic drops the arity backtick and carries its
//     arguments in braces: `…FSharpList{System.String}`, not
//     `…FSharpList`1[System.String]`;
//   * a generic PARAMETER is positional — `` `0 `` for the declaring
//     type's, ``` ``0 ``` for the method's own;
//   * the return type is absent, except on a conversion operator, where
//     it is the only thing distinguishing two overloads.
//
// A mismatch here understates coverage rather than overstating it (a
// documented member reads as undocumented), so the failure mode is a
// baseline recording a lower floor than the truth — safe, but silent.
// The vacuity guard in `PublicApiApprovalTests.fs` is what keeps it from
// being silent ALL the way down to zero.

/// The `+`-free spelling of a type's full name used inside a doc-comment
/// id: nested types are `.`-separated there, unlike CLR metadata.
let private docTypeName (fullName: string) = fullName.Replace('+', '.')

/// Drop the ``\`n`` generic-arity suffix from every dotted segment. Applied
/// only where the arguments follow in braces, which is the one place the
/// arity is redundant.
let private stripArity (dotted: string) =
    dotted.Split('.')
    |> Array.map (fun seg ->
        match seg.IndexOf '`' with
        | -1 -> seg
        | i -> seg.Substring(0, i))
    |> String.concat "."

/// One parameter (or conversion-operator return) type in doc-comment-id
/// form. `methodTypeParams` are the enclosing METHOD's own generic
/// parameter names: a generic parameter found there is the method's
/// (```` ``n ````), anything else is the declaring type's (``` `n ```).
/// Resolved by name rather than through `Type.DeclaringMethod`, which is
/// not dependable under `MetadataLoadContext`.
let rec private docTypeRef (methodTypeParams: string list) (t: Type) : string =
    if t.IsGenericParameter then
        match methodTypeParams |> List.tryFindIndex (fun n -> n = t.Name) with
        | Some i -> sprintf "``%d" i
        | None -> sprintf "`%d" t.GenericParameterPosition
    elif t.IsArray then
        let elem = docTypeRef methodTypeParams (t.GetElementType())

        match t.GetArrayRank() with
        | 1 -> elem + "[]"
        | r -> elem + "[" + (List.replicate r "0:" |> String.concat ",") + "]"
    elif t.IsByRef then
        docTypeRef methodTypeParams (t.GetElementType()) + "@"
    elif t.IsPointer then
        docTypeRef methodTypeParams (t.GetElementType()) + "*"
    elif t.IsGenericType then
        let def = t.GetGenericTypeDefinition()
        let baseName = if isNull def.FullName then def.Name else def.FullName

        let args =
            t.GetGenericArguments()
            |> Array.map (docTypeRef methodTypeParams)
            |> String.concat ","

        sprintf "%s{%s}" (stripArity (docTypeName baseName)) args
    else
        match t.FullName with
        | null -> t.Name
        | fn -> docTypeName fn

let private docParamList (methodTypeParams: string list) (ps: ParameterInfo[]) =
    if ps.Length = 0 then
        ""
    else
        ps
        |> Array.map (fun p -> docTypeRef methodTypeParams p.ParameterType)
        |> String.concat ","
        |> sprintf "(%s)"

/// A method's name as a doc-comment id spells it: `.` becomes `#` (an
/// explicit interface implementation), and a generic method carries its
/// arity after a DOUBLE backtick.
let private docMethodName (m: MethodInfo) =
    let n = m.Name.Replace('.', '#')

    if m.IsGenericMethodDefinition then
        sprintf "%s``%d" n (m.GetGenericArguments().Length)
    else
        n

/// One documentable subject of the tracked public surface: the baseline
/// token (so a failure names the member the way the rest of this gate
/// does) and the doc-comment id the XML file would key it by.
type DocSubject = { Token: string; DocId: string }

/// A rendered type: its surface lines (member tokens plus any Phase 258
/// obsolete markers), the obsolete markings the same walk observed, and
/// the Phase 261 documentable subjects it contributes. All three come out
/// of ONE pass so the pack pays one `MetadataLoadContext` load per
/// assembly, not three.
type private RenderedType = {
    Lines: string list
    Obsolete: ObsoleteMember list
    DocSubjects: DocSubject list
}

let private renderType (t: Type) : RenderedType =
    let fullName = if isNull t.FullName then t.Name else t.FullName
    let header = sprintf "%s (%s)" fullName (typeKind t)
    let typeDoc = docTypeName fullName

    // Each entry is (surface token, the [<Obsolete>] message if marked,
    // the Phase 261 doc-comment id — `None` where the entry is not a
    // documentable subject, i.e. the unenumerable-members placeholder).
    let members: (string * string option * string option)[] =
        try
            let ctors =
                t.GetConstructors memberFlags
                |> Array.filter (fun c -> methodVisible c && not (isCompilerGenerated (c.GetCustomAttributesData())))
                |> Array.map (fun c ->
                    sprintf "%s..ctor(%s)" fullName (paramList (c.GetParameters())),
                    obsoleteMessageOf (c.GetCustomAttributesData()),
                    Some(sprintf "M:%s.#ctor%s" typeDoc (docParamList [] (c.GetParameters()))))

            let methods =
                t.GetMethods memberFlags
                |> Array.filter (fun m ->
                    methodVisible m
                    && not (isAccessor m)
                    && not (isCompilerGenerated (m.GetCustomAttributesData())))
                |> Array.map (fun m ->
                    let methodTypeParams =
                        if m.IsGenericMethodDefinition then
                            m.GetGenericArguments() |> Array.map _.Name |> List.ofArray
                        else
                            []

                    // A conversion operator's return type is the only
                    // thing separating two otherwise-identical ids, so it
                    // is the one place the id carries one.
                    let conversionSuffix =
                        if m.Name = "op_Implicit" || m.Name = "op_Explicit" then
                            "~" + docTypeRef methodTypeParams m.ReturnType
                        else
                            ""

                    sprintf
                        "%s.%s(%s) : %s"
                        fullName
                        (methodName m)
                        (paramList (m.GetParameters()))
                        (typeName m.ReturnType),
                    obsoleteMessageOf (m.GetCustomAttributesData()),
                    Some(
                        sprintf
                            "M:%s.%s%s%s"
                            typeDoc
                            (docMethodName m)
                            (docParamList methodTypeParams (m.GetParameters()))
                            conversionSuffix
                    ))

            let props =
                t.GetProperties memberFlags
                |> Array.filter (fun p ->
                    let acc = p.GetAccessors true
                    acc.Length > 0 && acc |> Array.exists methodVisible)
                |> Array.map (fun p ->
                    let getSet =
                        [
                            if not (isNull (p.GetMethod)) && methodVisible p.GetMethod then
                                "get"
                            if not (isNull (p.SetMethod)) && methodVisible p.SetMethod then
                                "set"
                        ]
                        |> String.concat "; "

                    sprintf "%s.%s : %s { %s }" fullName p.Name (typeName p.PropertyType) getSet,
                    obsoleteMessageOf (p.GetCustomAttributesData()),
                    Some(sprintf "P:%s.%s%s" typeDoc p.Name (docParamList [] (p.GetIndexParameters()))))

            let fields =
                t.GetFields memberFlags
                |> Array.filter (fun f ->
                    fieldVisible f
                    && not f.IsSpecialName
                    && not (isCompilerGenerated (f.GetCustomAttributesData())))
                |> Array.map (fun f ->
                    sprintf
                        "%s.%s : %s%s"
                        fullName
                        f.Name
                        (typeName f.FieldType)
                        (if f.IsLiteral then " (literal)" else ""),
                    obsoleteMessageOf (f.GetCustomAttributesData()),
                    Some(sprintf "F:%s.%s" typeDoc f.Name))

            let events =
                t.GetEvents memberFlags
                |> Array.filter (fun e ->
                    let add = e.GetAddMethod true
                    not (isNull add) && methodVisible add)
                |> Array.map (fun e ->
                    let handler =
                        if isNull e.EventHandlerType then
                            "?"
                        else
                            typeName e.EventHandlerType

                    sprintf "%s.%s : %s (event)" fullName e.Name handler,
                    obsoleteMessageOf (e.GetCustomAttributesData()),
                    Some(sprintf "E:%s.%s" typeDoc e.Name))

            [ ctors; methods; props; fields; events ]
            |> Array.concat
            |> Array.sortWith (fun (a, _, _) (b, _, _) -> String.CompareOrdinal(a, b))
        with ex ->
            // A dependency the resolver couldn't satisfy makes this one
            // type's members unenumerable. Surface it visibly rather than
            // silently dropping the type (which would mask a real removal).
            // It contributes NO doc subject: counting a placeholder as an
            // undocumented member would make an unresolvable dependency
            // read as a documentation shortfall.
            [|
                sprintf "%s  # <members unavailable: %s>" fullName (ex.GetType().Name), None, None
            |]

    // The type's own `[<Obsolete>]` (an F# module or a retired type carries
    // it here, not on its members) leads, then the members in token order.
    let entries =
        Array.append
            [|
                header, obsoleteMessageOf (t.GetCustomAttributesData()), Some("T:" + typeDoc)
            |]
            members

    {
        Lines =
            entries
            |> Array.toList
            |> List.collect (fun (token, marked, _) ->
                match marked with
                | None -> [ token ]
                | Some _ -> [ token; obsoleteMarker token ])
        Obsolete =
            entries
            |> Array.toList
            |> List.choose (fun (token, marked, _) -> marked |> Option.map (fun m -> { Token = token; Message = m }))
        DocSubjects =
            entries
            |> Array.toList
            |> List.choose (fun (token, _, docId) -> docId |> Option.map (fun d -> { Token = token; DocId = d }))
    }

let private exportedTypes (asm: Assembly) =
    try
        asm.GetExportedTypes()
    with
    | :? ReflectionTypeLoadException as ex -> ex.Types |> Array.filter (fun t -> not (isNull t))
    | _ ->
        asm.GetTypes()
        |> Array.filter (fun t -> t.IsPublic || t.IsNestedPublic || t.IsNestedFamily)

let private isRenderableType (t: Type) =
    let fn = if isNull t.FullName then t.Name else t.FullName

    not (fn.Contains "@")
    && not (fn.Contains "<")
    && not (isCompilerGenerated (t.GetCustomAttributesData()))

/// One assembly's rendered surface: the baseline text, the `[<Obsolete>]`
/// markings the same walk observed (Phase 258), and its documentable
/// subjects (Phase 261).
type SurfaceRender = {
    Text: string
    Obsolete: ObsoleteMember list
    DocSubjects: DocSubject list
}

/// Render the deterministic public-surface text for one packable DLL,
/// together with its `[<Obsolete>]` markings. `resolverPaths` is the shared
/// MLC dependency pool. Sorted by type (ordinal), then by member within
/// each type — re-runs are byte-stable.
let renderSurfaceDetail (dllPath: string) (resolverPaths: string seq) : SurfaceRender =
    let tpa =
        (AppContext.GetData "TRUSTED_PLATFORM_ASSEMBLIES" :?> string).Split(Path.PathSeparator)
        |> Array.filter (String.IsNullOrWhiteSpace >> not)

    let allPaths =
        Seq.append tpa resolverPaths
        |> Seq.filter File.Exists
        // PathAssemblyResolver rejects two files with the same simple
        // name — dedup, runtime (TPA) wins over copied bin duplicates.
        |> Seq.distinctBy (fun p -> Path.GetFileName(p).ToLowerInvariant())
        |> Seq.toArray

    let resolver = PathAssemblyResolver allPaths
    use mlc = new MetadataLoadContext(resolver)
    let asm = mlc.LoadFromAssemblyPath dllPath

    let rendered =
        exportedTypes asm
        |> Array.filter isRenderableType
        |> Array.sortWith (fun a b ->
            let an = if isNull a.FullName then a.Name else a.FullName
            let bn = if isNull b.FullName then b.Name else b.FullName
            String.CompareOrdinal(an, bn))
        |> Array.map renderType

    let body = rendered |> Array.collect (_.Lines >> List.toArray)

    let sb = StringBuilder()

    sb.AppendLine(sprintf "# Public API baseline — %s" (asm.GetName().Name))
    |> ignore

    sb.AppendLine "# Generated by Phase 175 PublicApiApproval — regenerate with TOOLUP_APPROVE_API=1."
    |> ignore

    sb.AppendLine "# Do not edit by hand. Drift in EITHER direction fails the gate (Phase 618): a"
    |> ignore

    sb.AppendLine "# removal is breaking and must be reviewed; an addition is non-breaking but must"
    |> ignore

    sb.AppendLine "# still be folded into this file in the same PR." |> ignore

    for line in body do
        sb.AppendLine line |> ignore

    {
        Text = sb.ToString().Replace("\r\n", "\n")
        Obsolete =
            rendered
            |> Array.toList
            |> List.collect _.Obsolete
            // A single source deprecation can render more than once (an
            // F# module's type header and a re-export sharing a token);
            // grade each distinct marking once.
            |> List.distinct
            |> List.sortWith (fun a b -> String.CompareOrdinal(a.Token, b.Token))
        DocSubjects =
            rendered
            |> Array.toList
            |> List.collect _.DocSubjects
            // Deduped by ID for the same reason the markings are: one
            // documentable member reached twice must count once, or the
            // denominator inflates and coverage reads lower than it is.
            |> List.distinctBy _.DocId
            |> List.sortWith (fun a b -> String.CompareOrdinal(a.DocId, b.DocId))
    }

/// The baseline text alone — the shape every existing caller and fixture
/// wants. `renderSurfaceDetail` is the same walk with the Phase 258
/// obsolete markings retained.
let renderSurface (dllPath: string) (resolverPaths: string seq) : string =
    (renderSurfaceDetail dllPath resolverPaths).Text

// ─── Failure text (pure — so the wording itself is unit-testable) ────

let private bullets (tokens: string list) =
    tokens |> List.map (sprintf "  - %s") |> String.concat "\n"

let private regenRecipe (assemblyName: string) =
    sprintf
        "  $env:TOOLUP_APPROVE_API = \"1\"\n  dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj\n  $env:TOOLUP_APPROVE_API = $null\n\nthen commit api-baselines/%s.approved.txt in the SAME PR."
        assemblyName

/// The gate's failure text for a drift, or `None` when the surface matches
/// its baseline. Removal and addition get deliberately DIFFERENT messages;
/// when both directions moved, the breaking one leads and the additions are
/// reported after it, because a reader triaging a red run needs the
/// breaking change first.
let describeDrift (assemblyName: string) (drift: SurfaceDrift) : string option =
    let addedTail =
        if List.isEmpty drift.Added then
            ""
        else
            sprintf
                "\n\nThe same regeneration also folds in %d ADDED member(s) (non-breaking):\n%s"
                drift.Added.Length
                (bullets drift.Added)

    match drift.Removed, drift.Added with
    | [], [] -> None
    | [], added ->
        sprintf
            "%s: %d public member(s) ADDED vs the committed baseline — the public surface grew and api-baselines/%s.approved.txt was not regenerated:\n%s\n\nNOTHING IS BROKEN. Additive growth is non-breaking under the SemVer-on-0.x policy (GP 11) and is allowed; this failure is about WHEN the growth is folded into the baseline, not whether it is permitted. Folding it here keeps the addition reviewable in the PR that made it, instead of leaving it to surface months later in an unrelated phase's diff (Phase 618).\n\nRegenerate and commit the baseline with your change:\n%s"
            assemblyName
            added.Length
            assemblyName
            (bullets added)
            (regenRecipe assemblyName)
        |> Some
    | removed, _ ->
        sprintf
            "%s: %d public member(s) removed/renamed/retyped vs the committed baseline — a BREAKING change under the SemVer-on-0.x policy (GP 11):\n%s\n\nIf this break is intentional, regenerate the baseline and commit the api-baselines/%s.approved.txt edit in the same PR so the removal is reviewed:\n%s%s"
            assemblyName
            removed.Length
            (bullets removed)
            assemblyName
            (regenRecipe assemblyName)
            addedTail
        |> Some
// ─── Phase 258 — the deprecation-message policy (pure) ───────────────
//
// `docs/platform/deprecation-policy.md` is the prose; this is the gate.
// A deprecation notice a consumer cannot act on is worse than none: it
// reports that something is going away and withholds both the thing to
// move to and the release by which they must have moved. So a public
// `[<Obsolete>]` must name BOTH halves.
//
// Recognition is by phrase, not by a rigid template, and that is
// deliberate. A template ("Use X instead. Removed in N.0.") would be
// exactly checkable and would reject the eight conforming notices this
// SDK already ships — each of which says the right two things in its own
// words. A gate whose first act is to rewrite compliant prose teaches
// authors to satisfy the parser rather than the reader. The phrase lists
// below are therefore permissive about WORDING and strict about the two
// FACTS; extend them when a genuinely new phrasing appears, rather than
// bending a notice to fit.
//
// The removal target may be a version ("removed in 1.0") or a boundary
// ("removed in a future major") — the policy doc explains why the
// vaguer form is admitted while `0.x` runs, and when it stops being.

/// Phrases that name the REPLACEMENT half of a deprecation notice.
let private replacementPhrases = [
    "use "
    "prefer "
    "replaced by "
    "replacement"
    "moved to "
    "see "
    "compose "
    "call "
]

/// Phrases that name the REMOVAL-TARGET half.
let private removalPhrases = [
    "removed in"
    "removed at"
    "removal in"
    "retired in"
    "retired at"
    "will be removed"
]

let private mentionsAny (phrases: string list) (message: string) =
    let lower = message.ToLowerInvariant()
    phrases |> List.exists lower.Contains

/// The policy defect in one `[<Obsolete>]` marking, or `None` when it
/// conforms. Pure, so the policy itself is unit-testable without a build.
let obsoleteDefect (marking: ObsoleteMember) : string option =
    if String.IsNullOrWhiteSpace marking.Message then
        Some "carries no message at all — a bare [<Obsolete>] tells a consumer nothing"
    else
        match mentionsAny replacementPhrases marking.Message, mentionsAny removalPhrases marking.Message with
        | true, true -> None
        | true, false -> Some "names a replacement but no removal target"
        | false, true -> Some "names a removal target but no replacement"
        | false, false -> Some "names neither a replacement nor a removal target"

/// Every non-conforming marking in a rendered surface, paired with its
/// defect, in token order.
let obsoleteDefects (markings: ObsoleteMember list) : (ObsoleteMember * string) list =
    markings
    |> List.choose (fun m -> obsoleteDefect m |> Option.map (fun defect -> m, defect))

/// The gate's failure text for a set of message defects, or `None` when
/// every `[<Obsolete>]` on this assembly's public surface conforms.
let describeObsoleteDefects (assemblyName: string) (defects: (ObsoleteMember * string) list) : string option =
    match defects with
    | [] -> None
    | _ ->
        let listed =
            defects
            |> List.map (fun (m, defect) ->
                let shown =
                    if String.IsNullOrWhiteSpace m.Message then
                        "(empty)"
                    else
                        "\"" + m.Message + "\""

                sprintf "  - %s\n      %s: %s" m.Token defect shown)
            |> String.concat "\n"

        sprintf
            "%s: %d public [<Obsolete>] marking(s) do not meet the deprecation-message policy:\n%s\n\nEvery deprecation on the public surface must name BOTH a REPLACEMENT (what to move to) and a REMOVAL TARGET (the release by which the member goes away) — a consumer who reads only the compiler warning has to be able to act on it. NOTHING IS BROKEN and no baseline has drifted: this is about the WORDING of a notice, not the shape of the surface.\n\nThe shape:\n\n  [<Obsolete(\"Use Foo.bar instead. Removed in 1.0.\")>]\n\nWhile the SDK is on 0.x, \"removed in a future major\" is an accepted removal target. Full policy, including the deprecation window and the removal-only-at-a-major rule: docs/platform/deprecation-policy.md."
            assemblyName
            defects.Length
            listed
        |> Some
// ─── Phase 261 — the public XML-doc coverage gate (pure) ─────────────
//
// `docs/platform/doc-coverage.md` is the policy; this is the gate.
//
// **What is measured.** For each packable assembly: how many of the
// documentable subjects of its TRACKED PUBLIC SURFACE carry a doc
// comment. The subject set is `SurfaceRender.DocSubjects`, produced by
// the same `renderType` walk that produces the api-baseline text — so
// the coverage denominator is, by construction, exactly what Phase 175
// tracks and Phase 618 drift-gates in both directions. Nothing internal,
// nothing private, nothing a consumer cannot call. That is the whole
// reason it is computed here rather than from a source scan or from a
// compiler warning: the gate must never pressure plumbing into carrying
// docs, and the only defensible definition of "not plumbing" this repo
// has is the surface it already froze.
//
// **What "documented" means.** The member's doc-comment id appears in the
// `<AssemblyName>.xml` the compiler emits beside the DLL. That file
// exists because `Directory.Build.props` sets `GenerateDocumentationFile`
// — see the Phase 261 note there for why it is repo-wide and why it adds
// no warnings on an F# tree.
//
// **The gate is a RATCHET, and the recorded quantity is the UNDOCUMENTED
// COUNT.** `api-baselines/doc-coverage.approved.txt` records one
// `<assembly> <documented>/<total>` line per assembly, measured on the
// tree as it stood. The graded property is:
//
//     total - documented  must not INCREASE
//
// One number, and it is the one that covers both regressions worth
// catching: a doc comment DELETED from an existing member, and a NEW
// public member landing without one. Both raise the undocumented count;
// neither is caught by a bare fraction, which a large enough denominator
// hides. And because the denominator may grow, an undocumented count
// that does not rise means the documented FRACTION did not fall — which
// is the threshold the phase asked for, expressed in the one quantity
// that does not churn on unrelated surface growth.
//
// **It deliberately does NOT fail when coverage IMPROVES**, and that is
// the one place this departs from its two nearest neighbours (Phase 618's
// both-directions api-baseline, Phase 259's both-directions conformance
// registry). Those record a SET OF NAMES, where an entry that no longer
// matches the tree is a false statement about the surface and must be
// folded. This records a FLOOR, and a floor that is beaten is not stale —
// it is doing its job. Failing a run for documenting something would tax
// exactly the act the gate exists to encourage. The gains are locked in
// by regenerating deliberately (the ratchet step in the policy doc),
// which is a review event rather than a chore imposed on every PR.
//
// **The regeneration path is the SAME switch the api-baselines use**
// (`TOOLUP_APPROVE_API`), scoped the same way, for the reason Phase 259
// gives: a reviewer accepting the tree's current shape wants one pass,
// and a second env var would need its own config surface to say the same
// thing. A scoped regen rewrites only its own assemblies' lines.
//
// **Everything below is pure over its inputs** — text in, data out — so
// the companion pack drives each arm from synthetic fixtures and proves
// the gate fails closed, rather than only proving it passed once on a
// tree that happened to be green.

/// The XML documentation file the compiler emits beside a built DLL, or
/// `None` when it is absent.
let docFileFor (dllPath: string) : string option =
    let candidate = Path.ChangeExtension(dllPath, ".xml")
    if File.Exists candidate then Some candidate else None

/// Whether an assembly's committed baseline records any tracked public
/// surface at all.
///
/// **Why the precondition needs this, and why it reads the BASELINE.**
/// `ToolUp.Hosts.Docker` is a packable, published package that ships no
/// DLL (`IncludeBuildOutput=false`) and no `.fs` at all — its payload is
/// a set of `contentFiles/` templates — so it sets
/// `GenerateDocumentationFile=false` in its own `.fsproj`, entirely
/// correctly: there is nothing to document and no assembly to document it
/// in. Its api-baseline is a header and no members. Demanding a sidecar
/// there would be demanding documentation of the empty set, which is how
/// a precondition earns a permanent exception list.
///
/// It is derived from the committed baseline rather than from a name, so
/// the day that package (or any like it) grows public code, its baseline
/// grows lines and the precondition starts applying to it with no edit
/// here. A MISSING baseline reads as "has surface" — the drift arm is
/// already failing that assembly, and the conservative answer keeps the
/// two findings from cancelling each other out.
let hasTrackedSurface (root: string) (assemblyName: string) : bool =
    let path = Path.Combine(baselineDir root, assemblyName + ".approved.txt")

    if not (File.Exists path) then
        true
    else
        File.ReadAllText path |> significantLines |> Array.isEmpty |> not

/// Those of `assemblies` that ARE built in `config` but carry no XML
/// documentation file, in discovery order. An assembly that is not built
/// at all is the Phase 731 precondition's business, not this one; one
/// with no tracked public surface is filtered by the caller
/// (`hasTrackedSurface`) before it gets here.
let missingDocFiles (config: string) (assemblies: PackableAssembly list) : PackableAssembly list =
    assemblies
    |> List.filter (fun a ->
        match resolveDll config a with
        | None -> false
        | Some dll -> (docFileFor dll).IsNone)

/// The ONE message a tree built without XML documentation earns — the
/// same shape Phase 731 gave the unbuilt tree, and for the same reason.
///
/// Without the sidecar every assembly measures 0 documented, which is
/// indistinguishable from an SDK that documents nothing. A gate that
/// reported that as a coverage shortfall would send its reader off to
/// write doc comments for what is a build-property problem, so the
/// precondition is named once and the per-assembly cases defer to it.
let describeMissingDocFiles (config: string) (total: int) (missing: PackableAssembly list) : string option =
    match missing with
    | [] -> None
    | _ ->
        let sample = missing |> List.truncate 5 |> List.map _.Name

        let elided =
            match missing.Length - sample.Length with
            | 0 -> ""
            | n -> sprintf ", and %d more" n

        Some(
            sprintf
                "NO XML DOCUMENTATION FILES — %d of %d built packable assemblies have no <name>.xml beside their DLL in bin/%s/net10.0: %s%s.\n\nThis is a BUILD-PROPERTY precondition, not a documentation shortfall: `GenerateDocumentationFile` in Directory.Build.props is what emits the file, and without it every assembly measures 0%% documented no matter how many doc comments the source carries. Nothing has regressed and no floor needs regenerating — check that the property is still set (and that the project has not opted out in its own .fsproj), then rebuild. The per-assembly doc-coverage cases defer to this one finding rather than each restating it."
                missing.Length
                total
                config
                (String.concat ", " sample)
                elided
        )

// The compiler writes one `<member name="…">` element per documented
// member. Matched by pattern rather than parsed as a document on purpose:
// the shape is fixed and machine-written, and a malformed doc comment
// somewhere in a 40k-line file must degrade to "that member reads as
// undocumented", never to an exception that takes the whole gate down.
let private memberNameAttr =
    Regex("<member\\s+name\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled)

/// Every doc-comment id an XML documentation file carries.
let documentedIdsIn (xmlText: string) : Set<string> =
    memberNameAttr.Matches xmlText
    |> Seq.map (fun m -> m.Groups[1].Value)
    |> Set.ofSeq

/// One assembly's measured coverage over its tracked public surface.
type DocCoverage = {
    Assembly: string
    Documented: int
    Total: int
}

[<RequireQualifiedAccess>]
module DocCoverage =
    let undocumented (c: DocCoverage) = c.Total - c.Documented

    /// Percentage, to one decimal place. `100.0` for an empty surface —
    /// an assembly with nothing public has nothing undocumented, and
    /// reporting 0% there would read as a catastrophe.
    let percent (c: DocCoverage) =
        if c.Total = 0 then
            100.0
        else
            Math.Round(100.0 * float c.Documented / float c.Total, 1)

/// Measure one assembly's coverage: how many of its documentable surface
/// subjects the XML file keys.
let docCoverageOf (assemblyName: string) (subjects: DocSubject list) (documented: Set<string>) : DocCoverage = {
    Assembly = assemblyName
    Documented = subjects |> List.filter (fun s -> documented.Contains s.DocId) |> List.length
    Total = subjects.Length
}

/// The subjects of `subjects` the XML file does not key, in token order —
/// the sample a failure names so the reader has somewhere to start.
let undocumentedSubjects (subjects: DocSubject list) (documented: Set<string>) : DocSubject list =
    subjects
    |> List.filter (fun s -> not (documented.Contains s.DocId))
    |> List.sortWith (fun a b -> String.CompareOrdinal(a.Token, b.Token))

// ─── The committed ratchet floor ─────────────────────────────────────

/// `api-baselines/doc-coverage.approved.txt` — one line per assembly. It
/// sits WITH the api-baselines rather than beside the gate because it is
/// a statement about exactly their contents, and a reader looking at the
/// tracked surface should meet the coverage floor for it in the same
/// directory.
let docCoveragePath (root: string) =
    Path.Combine(baselineDir root, "doc-coverage.approved.txt")

let private docCoverageHeader = [
    "# Public XML-doc coverage floor — Phase 261."
    "# Generated by PublicApiApproval — regenerate with TOOLUP_APPROVE_API (the same switch as the"
    "# api-baselines beside this file; a scoped regen rewrites only its own assemblies' lines)."
    "# One line per packable assembly: <assembly> <documented>/<total>, measured over exactly the"
    "# Phase 175 tracked public surface. The GRADED property is (total - documented): it must not"
    "# increase. Improving coverage never fails — see docs/platform/doc-coverage.md."
]

let private docCoverageLine =
    Regex(@"^(?<name>\S+)\s+(?<documented>\d+)/(?<total>\d+)\s*$", RegexOptions.Compiled)

/// Parse the committed floor. Comment and blank lines are ignored; a line
/// that does not parse is DROPPED rather than guessed at, so a hand-edit
/// that mangles a line reads as "no floor recorded for this assembly" —
/// which fails loudly at that assembly — instead of as a silently wrong
/// number.
let parseDocCoverage (text: string) : Map<string, DocCoverage> =
    text.Replace("\r\n", "\n").Split('\n')
    |> Array.choose (fun line ->
        let trimmed = line.Trim()

        if trimmed = "" || trimmed.StartsWith "#" then
            None
        else
            let m = docCoverageLine.Match trimmed

            if not m.Success then
                None
            else
                Some(
                    m.Groups["name"].Value,
                    {
                        Assembly = m.Groups["name"].Value
                        Documented = int m.Groups["documented"].Value
                        Total = int m.Groups["total"].Value
                    }
                ))
    |> Map.ofArray

/// Render a whole floor file, assemblies in ordinal order so re-runs
/// produce no reordering diffs.
let renderDocCoverage (entries: DocCoverage seq) : string =
    let sb = StringBuilder()

    for line in docCoverageHeader do
        sb.Append(line).Append('\n') |> ignore

    entries
    |> Seq.sortWith (fun a b -> String.CompareOrdinal(a.Assembly, b.Assembly))
    |> Seq.iter (fun c -> sb.Append(sprintf "%s %d/%d\n" c.Assembly c.Documented c.Total) |> ignore)

    sb.ToString()

/// Replace one assembly's line in a floor file's text, leaving every
/// other line byte-identical.
///
/// Read-modify-write of the whole file rather than one file per assembly
/// on disk, because 165 further `.approved.txt` files carrying two
/// integers each is a worse artefact than one file of 165 lines. The
/// surgical semantics that matters — a scoped regen touches only its own
/// assemblies — is preserved by rewriting only the named line, which is
/// what a session regenerating ONE baseline needs and what Phase 318's
/// hand-reverting of foreign hunks was about.
let upsertDocCoverage (existing: string) (entry: DocCoverage) : string =
    parseDocCoverage existing
    |> Map.add entry.Assembly entry
    |> Map.toSeq
    |> Seq.map snd
    |> renderDocCoverage

// The floor file is rewritten per assembly under an approval run, and a
// pack invoked with `--parallel` would otherwise interleave two
// read-modify-write cycles and lose one. Cheap insurance: approval runs
// are rare and the file is a few KB.
let private docCoverageWriteLock = obj ()

/// Fold one assembly's measurement into the committed floor file on disk.
let writeDocCoverageEntry (path: string) (entry: DocCoverage) : unit =
    lock docCoverageWriteLock (fun () ->
        Directory.CreateDirectory(Path.GetDirectoryName path: string) |> ignore

        let existing = if File.Exists path then File.ReadAllText path else ""

        File.WriteAllText(path, upsertDocCoverage existing entry))

// ─── The grading (pure) ──────────────────────────────────────────────

let private sampleTokens (subjects: DocSubject list) =
    let shown = subjects |> List.truncate 10

    let elided =
        match subjects.Length - shown.Length with
        | 0 -> ""
        | n -> sprintf "\n  … and %d more" n

    (shown |> List.map (fun s -> "  - " + s.Token) |> String.concat "\n") + elided

/// The failure text for an assembly the floor file does not mention.
///
/// A NEW packable package reaches this before anyone has measured it, and
/// the answer is the same as Phase 175's for a missing api-baseline:
/// generate it and commit it with the change, so the package enters the
/// ratchet at whatever coverage it actually has rather than escaping it.
let describeMissingDocCoverageFloor (assemblyName: string) (measured: DocCoverage) : string =
    sprintf
        "%s: no doc-coverage floor recorded in api-baselines/doc-coverage.approved.txt. This is a NEW public package (or a line that was hand-edited into an unparseable shape). Measured now: %d/%d documented (%.1f%%). Record the floor with the same switch the api-baselines use and commit it in the SAME PR:\n\n  $env:TOOLUP_APPROVE_API = \"%s\"\n  dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj\n  $env:TOOLUP_APPROVE_API = $null\n\nNothing is broken — a package with no recorded floor is simply outside the ratchet, which is the one state this gate cannot allow. Policy: docs/platform/doc-coverage.md."
        assemblyName
        measured.Documented
        measured.Total
        (DocCoverage.percent measured)
        assemblyName

/// The gate's failure text for a coverage regression, or `None` when the
/// assembly is at or better than its recorded floor.
///
/// `undocumented` is the current undocumented subject list, used only for
/// the sample — the verdict is decided by the two counts alone, so the
/// wording is testable without a build.
let describeDocCoverageRegression
    (recorded: DocCoverage)
    (current: DocCoverage)
    (undocumented: DocSubject list)
    : string option =
    let before = DocCoverage.undocumented recorded
    let now = DocCoverage.undocumented current

    if now <= before then
        None
    else
        sprintf
            "%s: public XML-doc coverage REGRESSED — %d undocumented public member(s) against a recorded floor of %d (+%d). Coverage is now %d/%d (%.1f%%); the floor was recorded at %d/%d (%.1f%%).\n\nNOTHING IS BROKEN and no public surface has drifted: this is about doc comments, not shapes. It fires for exactly two reasons, and the remedy differs:\n\n  * a NEW public member landed without a doc comment — document it. The tracked public surface IS the contracted surface (GP 11), and an adopter meets it through hover-docs and the generated reference, not the source.\n  * an existing doc comment was DELETED, or moved off the public surface — restore it, or accept the loss by regenerating the floor in the same PR.\n\nUndocumented members in this assembly (a sample — not necessarily the new ones):\n%s\n\nTo accept the new level deliberately:\n\n  $env:TOOLUP_APPROVE_API = \"%s\"\n  dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj\n  $env:TOOLUP_APPROVE_API = $null\n\nthen commit api-baselines/doc-coverage.approved.txt in the SAME PR. Improving coverage never fails this gate — only regressing it does. Policy, threshold and the ratchet plan toward the 1.0 tag: docs/platform/doc-coverage.md."
            current.Assembly
            now
            before
            (now - before)
            current.Documented
            current.Total
            (DocCoverage.percent current)
            recorded.Documented
            recorded.Total
            (DocCoverage.percent recorded)
            (sampleTokens undocumented)
            current.Assembly
        |> Some

/// Floor lines naming an assembly the packable walk does not discover —
/// a package that was renamed or deleted. Reported so the file cannot
/// silently accumulate floors for things that no longer exist, which is
/// how a ratchet baseline rots into decoration.
let staleDocCoverageFloors (discovered: Set<string>) (recorded: Map<string, DocCoverage>) : string list =
    recorded
    |> Map.toList
    |> List.map fst
    |> List.filter (fun name -> not (discovered.Contains name))
    |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))