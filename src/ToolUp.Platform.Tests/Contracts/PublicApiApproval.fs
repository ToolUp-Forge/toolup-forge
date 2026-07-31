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
// ── Reconciling with Phase 258 (`[<Obsolete>]` policy) — DECIDED ──
// Phase 258 (open) teaches this differ that adding `[<Obsolete>]` to an
// existing public member is ADDITIVE (minor), while removing a member stays
// breaking (major). With Phase 618 making additions fail too, both phases
// land on this file, so the interaction is settled here in advance rather
// than discovered by whichever ships second.
//
// The renderer emits no attributes at all today, so an `[<Obsolete>]`
// marking currently changes no token and is invisible to the gate. When
// Phase 258 makes obsolescence visible it MUST do so as a SEPARATE marker
// line (`obsoleteMarker` below) and MUST NOT alter the member's own token:
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
// asks for. Phase 258 therefore needs NO exception carved into the
// comparer — only the rendering convention. Both shapes are pinned by
// fixtures in `PublicApiApprovalTests.fs` so the rule cannot be undone by
// accident.
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
// ── Why MetadataLoadContext ──
// The packable set spans ~90 assemblies, most of which the Tests project
// does NOT reference (Forms.* / Scheduling.* / Stripe.* / Hosts / Encryption
// / …). In-process reflection would silently cover only the referenced
// subset. MetadataLoadContext loads any built DLL from disk uniformly,
// metadata-only — no execution, no runtime load of Fable client assemblies
// — so coverage tracks the Pack set, not the Tests dep graph. A discovered
// packable assembly with NO committed baseline FAILS the test (a new public
// package cannot silently escape the guard); a packable assembly not built
// in the active config FAILS with a "build the solution first" message
// (the canonical gate runs `dotnet build ToolUp.Forge.sln` before VerifyAll,
// so every Debug DLL is present).
//
// This is test-tier + repo-baseline only — zero shipped code, a consumer
// deployment is byte-for-byte unchanged (GP 13).

open System
open System.IO
open System.Reflection
open System.Text

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
/// of comparing.
let approveModeOn () =
    match Environment.GetEnvironmentVariable "TOOLUP_APPROVE_API" with
    | null
    | "" -> false
    | v -> v = "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)

// ─── Packable-set discovery (mirrors the Pack glob) ──────────────────

type PackableAssembly = {
    /// Output assembly name (== the rendered baseline file stem).
    Name: string
    ProjectPath: string
    /// Resolved DLL in the active config, if built.
    DllPath: string option
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

let private locateDll (root: string) (config: string) (projDir: string) (asmName: string) =
    let candidate = Path.Combine(projDir, "bin", config, "net10.0", asmName + ".dll")
    if File.Exists candidate then Some candidate else None

/// Discover every packable assembly under `src/`, sorted by name for a
/// deterministic per-assembly test order.
let discoverPackable (root: string) (config: string) : PackableAssembly list =
    let srcDir = Path.Combine(root, "src")

    Directory.EnumerateFiles(srcDir, "*.fsproj", SearchOption.AllDirectories)
    |> Seq.filter isPackableProject
    |> Seq.map (fun fsproj ->
        let projDir = Path.GetDirectoryName fsproj
        let name = assemblyNameOf fsproj

        {
            Name = name
            ProjectPath = fsproj
            DllPath = locateDll root config projDir name
        })
    // Two fsprojs can share an <AssemblyName> only by mistake; dedup by
    // name so the baseline set is a clean 1:1 with assemblies.
    |> Seq.distinctBy _.Name
    |> Seq.sortBy _.Name
    |> List.ofSeq

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

let private renderType (t: Type) : string list =
    let fullName = if isNull t.FullName then t.Name else t.FullName
    let header = sprintf "%s (%s)" fullName (typeKind t)

    let members =
        try
            let ctors =
                t.GetConstructors memberFlags
                |> Array.filter (fun c -> methodVisible c && not (isCompilerGenerated (c.GetCustomAttributesData())))
                |> Array.map (fun c -> sprintf "%s..ctor(%s)" fullName (paramList (c.GetParameters())))

            let methods =
                t.GetMethods memberFlags
                |> Array.filter (fun m ->
                    methodVisible m
                    && not (isAccessor m)
                    && not (isCompilerGenerated (m.GetCustomAttributesData())))
                |> Array.map (fun m ->
                    sprintf
                        "%s.%s(%s) : %s"
                        fullName
                        (methodName m)
                        (paramList (m.GetParameters()))
                        (typeName m.ReturnType))

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

                    sprintf "%s.%s : %s { %s }" fullName p.Name (typeName p.PropertyType) getSet)

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
                        (if f.IsLiteral then " (literal)" else ""))

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

                    sprintf "%s.%s : %s (event)" fullName e.Name handler)

            [ ctors; methods; props; fields; events ]
            |> Array.concat
            |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
            |> List.ofArray
        with ex ->
            // A dependency the resolver couldn't satisfy makes this one
            // type's members unenumerable. Surface it visibly rather than
            // silently dropping the type (which would mask a real removal).
            [ sprintf "%s  # <members unavailable: %s>" fullName (ex.GetType().Name) ]

    header :: members

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

/// Render the deterministic public-surface text for one packable DLL.
/// `resolverPaths` is the shared MLC dependency pool. Sorted by type
/// (ordinal), then by member within each type — re-runs are byte-stable.
let renderSurface (dllPath: string) (resolverPaths: string seq) : string =
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

    let body =
        exportedTypes asm
        |> Array.filter isRenderableType
        |> Array.sortWith (fun a b ->
            let an = if isNull a.FullName then a.Name else a.FullName
            let bn = if isNull b.FullName then b.Name else b.FullName
            String.CompareOrdinal(an, bn))
        |> Array.collect (renderType >> List.toArray)

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

    sb.ToString().Replace("\r\n", "\n")

// ─── Diff (pure — the comparer the gate + fixtures exercise) ─────────

let private significantLines (text: string) =
    text.Replace("\r\n", "\n").Split('\n')
    |> Array.map _.TrimEnd()
    |> Array.filter (fun l -> l <> "" && not (l.StartsWith "#"))

/// Significant tokens of `candidates` that `present` does not carry.
/// Both directions of the comparison are this same set difference — which
/// is why neither arm can drift from the other in how it tokenises.
let private missingFrom (present: string) (candidates: string) : string list =
    let presentSet = significantLines present |> Set.ofArray

    significantLines candidates
    |> Array.filter (fun l -> not (presentSet.Contains l))
    |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
    |> List.ofArray

/// Tokens present in `baseline` but absent from `current` — i.e. removed,
/// renamed, or retyped public members. A non-empty result is a BREAKING
/// diff.
let removedMembers (baseline: string) (current: string) : string list = missingFrom current baseline

/// Tokens present in `current` but absent from `baseline` — i.e. new public
/// types, members, or overloads. Non-breaking, but UNFOLDED: the baseline
/// must be regenerated and committed with the change (Phase 618).
let addedMembers (baseline: string) (current: string) : string list = missingFrom baseline current

/// The two-directional comparison. `Removed` is breaking; `Added` is
/// non-breaking but unfolded. Both fail the gate — see the drift-policy
/// note in this file's header.
type SurfaceDrift = {
    Removed: string list
    Added: string list
}

[<RequireQualifiedAccess>]
module SurfaceDrift =
    let isClean (d: SurfaceDrift) =
        List.isEmpty d.Removed && List.isEmpty d.Added

let compareSurface (baseline: string) (current: string) : SurfaceDrift = {
    Removed = removedMembers baseline current
    Added = addedMembers baseline current
}

/// Phase 258 seam — the sanctioned rendering of an `[<Obsolete>]` marking:
/// a SEPARATE line derived from the member's token, never a rewrite of it.
/// See the "Reconciling with Phase 258" note in this file's header for why
/// the in-place alternative is forbidden.
let obsoleteMarker (memberToken: string) = memberToken + "  (obsolete)"

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