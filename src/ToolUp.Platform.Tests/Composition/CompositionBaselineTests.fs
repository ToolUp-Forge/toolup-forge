module ToolUp.Platform.Tests.Composition.CompositionBaselineTests

open System
open System.IO
open System.Reflection
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.FileProcessor
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 287 — composition golden-file CI gate ──────────────────────
//
// Mirrors the Phase 175 public-API baseline guard for the *composed
// surface*: build a reference composition's Phase 280 `CompositionManifest`,
// serialise it to a checked-in `composition-baselines/composition-baseline.json`
// golden file, and fail CI when a PR silently drops a module / companion,
// swaps an impl, or changes a datatype — until the change is acknowledged by
// regenerating the baseline. The failure is rendered through the Phase 286
// `CompositionDiff` so the operator sees exactly what moved, not just "the
// JSON differs".
//
// ── Additive-vs-acknowledged policy (the human checkpoint) ──
// The gate compares the live reference manifest against the committed
// baseline via `CompositionDiff.diff`. A non-empty delta — a module /
// companion / datatype / tool added, removed, or changed, or a config knob
// value moved — fails the gate and prints the readable delta. Accepting the
// change is a deliberate, reviewed edit of the golden file in the SAME PR
// (regenerate with the env flag below), so the composition change is visible
// in review.
//
// ── Regeneration path ──
//   $env:TOOLUP_APPROVE_COMPOSITION = "1"
//   dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
//   $env:TOOLUP_APPROVE_COMPOSITION = $null
//
// This is test-tier + repo-baseline only — zero shipped code, a consumer
// deployment is byte-for-byte unchanged (GP 11 / GP 13). Wired into
// `VerifyAll` by virtue of living in the Platform test pack.

// Indented, byte-stable JSON so the committed golden file is human-diffable
// in review. `create ()` returns a fresh options instance, so setting
// WriteIndented before first use is safe.
let private jsonOptions =
    let o = FableConverters.create ()
    o.WriteIndented <- true
    o

/// Repo root (toolup-forge) derived from the running test assembly, the same
/// way the Phase 175 guard derives it: bin/<Config>/net10.0/…Tests.dll → up 5.
let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

/// `toolup-forge/composition-baselines/composition-baseline.json` — the
/// committed golden file.
let private baselinePath () =
    Path.Combine(repoRoot (), "composition-baselines", "composition-baseline.json")

/// Phase 431 — the event-topology half of the same gate, in its own
/// golden file beside the manifest one. A separate file rather than a
/// field grown onto `CompositionManifest`: growing a shipped F# record
/// breaks its constructor (the reason recorded on `SlotRequirementSet`
/// and `ClassifiedCompositionRule`), and the two baselines join on the
/// `ComponentId`s they both key against. Both are approved by the same
/// `TOOLUP_APPROVE_COMPOSITION` flag, so accepting a composition change
/// accepts its topology consequence in the same act.
let private topologyBaselinePath () =
    Path.Combine(repoRoot (), "composition-baselines", "event-topology-baseline.json")

/// Phase 433 — the data-footprint half of the same gate, in its own golden
/// file beside the other two, for the same reason: a sidecar keyed by
/// `ComponentId` rather than a field grown onto a shipped record. This is
/// the baseline that makes "a component started persisting personal data" a
/// reviewable change rather than a silent one — the delta renders the class
/// with its `PII` marker, so the failure says what moved.
let private footprintBaselinePath () =
    Path.Combine(repoRoot (), "composition-baselines", "data-footprint-baseline.json")

/// Phase 438 — the authorization-surface half of the same gate, in its own
/// golden file beside the other three, for the same sidecar reason. This is
/// the baseline that makes "an endpoint became reachable without
/// authentication" or "a requirement was weakened" a CI failure rather than
/// a pentest finding — the delta leads with its severity, so the failure
/// says not just what moved but how loudly.
let private authorizationBaselinePath () =
    Path.Combine(repoRoot (), "composition-baselines", "authorization-surface-baseline.json")

/// Phase 597 — the rule-manifest half of the same gate. Unlike the other
/// four this one is not derived from the reference *composition* at all:
/// it pins the **prover**, not the proven. A rule added, removed,
/// tightened (a minor bump), or reworded (a patch bump) changes what
/// every consumer's preflight means, and before this file that change
/// was invisible in review — the composition baseline is identical
/// whether the rules moved or not. Pinning it here makes a rules-only
/// change a reviewed diff, which is also what enforces the bump
/// discipline: the version string sits beside the description it
/// governs, so a message edit with no patch bump is visible in the same
/// hunk.
let private ruleManifestBaselinePath () =
    Path.Combine(repoRoot (), "composition-baselines", "rule-manifest-baseline.json")

/// Regeneration path: `TOOLUP_APPROVE_COMPOSITION=1` rewrites the baseline
/// instead of comparing.
let private approveModeOn () =
    match Environment.GetEnvironmentVariable "TOOLUP_APPROVE_COMPOSITION" with
    | null
    | "" -> false
    | v -> v = "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)

// ── The reference composition (mirrors CompositionManifestTests' stubs) ──

// A minimal `DataType` whose `Detect` / `Process` are never invoked — the
// manifest reads its `Id` only.
let private stubDataType (id: string) : DataType = {
    Info = {
        Id = id
        DisplayName = id
        Schema = None
    }
    Id = id
    Detect = fun _ -> async { return false }
    Process = fun _ -> async { return failwith "stub DataType.Process is never called by the manifest projector" }
}

let private stubTool
    (name: string)
    (emits: ActionDeclaration list option)
    : AIToolDefinition * (HttpContext -> string -> Async<string>) =
    {
        Name = name
        Description = ""
        Parameters = []
        SourceModule = "baseline-reference"
        EmitsActions = emits
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
    },
    (fun _ _ -> async { return "" })

/// A job handler that is never dispatched — the topology derivation reads
/// the declaration's `Trigger`, never the handler.
type private StubJobHandler() =
    interface IJobHandler with
        member _.Execute(_) = async { return JobResult.Success }

/// The reference composition's modules — a couple of them (explicit
/// stable ids), a datatype + an action-emitting tool on the first, and an
/// `OnEvent` subscription on the second. Representative enough that an
/// accidental drop / swap / datatype change (or a severed messaging edge)
/// in forge's compose surface is caught, small enough to stay stable
/// across unrelated changes. Editing it is a deliberate act that
/// regenerates both baselines.
///
/// The job declaration and the tool's `EmitsActions` are read by the
/// Phase 431 topology gate and by nothing in the Phase 280 manifest — the
/// manifest enumerates modules / companions / datatypes / tools, so the
/// composition baseline is unaffected by them. The Phase 66 route
/// declarations are the same story for the Phase 438 authorization gate:
/// a route prefix under the strict default and one deliberately public
/// endpoint, so the baseline carries at least one entry of each
/// classification — including the anonymous-reachable headline class.
let private referenceModules () : ServerModule list =
    let orders =
        ServerModule.create "Orders"
        |> ServerModule.withComponentId "orders-service"
        |> ServerModule.withDataTypes [ stubDataType "SalesData" ]
        |> ServerModule.withRoutePrefix "/api/orders/"
        |> ServerModule.withAITools [
            stubTool
                "orders.run"
                (Some [
                    {
                        ModuleId = "Inventory"
                        ActionKey = "reserve-stock"
                        Description = ""
                        PayloadSchema = None
                    }
                ])
        ]

    let inventory =
        ServerModule.create "Inventory"
        |> ServerModule.withComponentId "inventory-service"
        |> ServerModule.withDataTypes [ stubDataType "StockData" ]
        |> ServerModule.withRoutePrefix "/api/inventory/"
        |> ServerModule.withRouteSurfaceRequirement "GET" "/api/inventory/public/stock" SurfaceRequirement.public_
        |> ServerModule.withJobHandler ("inventory.on-order", StubJobHandler(), OnEvent "OrderPlaced")

    [ orders; inventory ]

let private referenceApp () : ServerApp =
    ServerApp.empty
    |> ServerApp.addModules (referenceModules ())
    |> ServerApp.withAuditSink (InMemoryAuditSink "primary-archive")

let private referenceManifest () : CompositionManifest =
    ServerApp.compositionManifest (referenceApp ())

let private serialise (m: CompositionManifest) : string =
    JsonSerializer.Serialize(m, jsonOptions).Replace("\r\n", "\n")

/// Phase 431 — the reference composition's derived event topology. Same
/// modules, second lens.
let private referenceTopology () : EventTopology =
    EventTopology.ofModules (referenceModules ())

/// The topology persisted through its plain-string wire projection, so the
/// golden file never depends on the `Set` / single-case-union shapes
/// round-tripping through a serialiser.
let private serialiseTopology (topology: EventTopology) : string =
    JsonSerializer.Serialize(EventTopology.toWire topology, jsonOptions).Replace("\r\n", "\n")

/// Phase 433 — the reference composition's derived data footprint. Same
/// modules + audit sink, third lens: what each component leaves at rest and
/// behind which store seam.
let private referenceFootprint () : FootprintSignature =
    DataFootprintDerivation.ofApp (referenceApp ())

/// The footprint persisted through its plain-string wire projection, so the
/// golden file never depends on the `Set` / union shapes round-tripping
/// through a serialiser.
let private serialiseFootprint (signature: FootprintSignature) : string =
    JsonSerializer.Serialize(DataFootprint.toWire signature, jsonOptions).Replace("\r\n", "\n")

/// Phase 438 — the reference composition's derived authorization surface.
/// Same modules, fourth lens: what each component exposes, and what each
/// exposed entry requires.
let private referenceAuthorizationSurface () : AuthorizationSurface =
    AuthorizationSurface.ofModules (referenceModules ())

/// The surface persisted through its plain-string wire projection, so the
/// golden file never depends on the union shapes round-tripping through a
/// serialiser.
let private serialiseAuthorizationSurface (surface: AuthorizationSurface) : string =
    JsonSerializer.Serialize(AuthorizationSurface.toWire surface, jsonOptions).Replace("\r\n", "\n")

/// Phase 597 — the shipped rule manifest with versions, as its published
/// wire document. Composition-independent: this is the prover, so the
/// reference app does not enter into it.
let private ruleManifestDocument () : RuleManifestWireDocument =
    CompositionRuleVersions.toWireDocument CompositionRuleVersions.allRules

let private serialiseRuleManifest (document: RuleManifestWireDocument) : string =
    JsonSerializer.Serialize(document, jsonOptions).Replace("\r\n", "\n")

/// A readable delta between two rule manifests: added / removed rules,
/// and — the case this gate exists for — a rule whose version moved,
/// with the bump classified so the reviewer reads "tightened", not
/// "1.0.0 became 1.1.0".
let private renderRuleManifestDelta (baseline: RuleManifestWireDocument) (current: RuleManifestWireDocument) : string =
    let byRule (doc: RuleManifestWireDocument) =
        doc.Rules |> List.map (fun r -> r.Rule, r) |> Map.ofList

    let before, after = byRule baseline, byRule current

    let added =
        current.Rules
        |> List.filter (fun r -> not (before.ContainsKey r.Rule))
        |> List.map (fun r -> sprintf "  + rule '%s' (%s) added at version %s" r.Rule r.Family r.Version)

    let removed =
        baseline.Rules
        |> List.filter (fun r -> not (after.ContainsKey r.Rule))
        |> List.map (fun r -> sprintf "  - rule '%s' (%s) removed (was version %s)" r.Rule r.Family r.Version)

    let describeBump (fromVersion: string) (toVersion: string) =
        match RuleVersion.tryParse fromVersion, RuleVersion.tryParse toVersion with
        | Some a, Some b ->
            match RuleVersion.bumpBetween a b with
            | Some PatchBump -> "patch — message / implementation only, prior conclusions stand"
            | Some MinorBump -> "MINOR — the rule TIGHTENED; prior passes are no longer evidence"
            | Some MajorBump -> "MAJOR — the rule's meaning changed; prior conclusions do not carry over"
            | None -> "not a forward bump"
        | _ -> "unparseable version"

    let changed =
        current.Rules
        |> List.choose (fun r ->
            match before.TryFind r.Rule with
            | Some prior when prior.Version <> r.Version ->
                Some(
                    sprintf
                        "  ~ rule '%s' version %s -> %s (%s)"
                        r.Rule
                        prior.Version
                        r.Version
                        (describeBump prior.Version r.Version)
                )
            | Some prior when prior.RuleDescription <> r.RuleDescription ->
                Some(
                    sprintf
                        "  ~ rule '%s' description changed with NO version bump (a message change is a patch bump) at version %s"
                        r.Rule
                        r.Version
                )
            | Some prior when prior.Severity <> r.Severity ->
                Some(sprintf "  ~ rule '%s' severity %s -> %s" r.Rule prior.Severity r.Severity)
            | _ -> None)

    let manifestVersionLine =
        if baseline.ManifestVersion <> current.ManifestVersion then
            [
                sprintf "  ~ manifest version %s -> %s" baseline.ManifestVersion current.ManifestVersion
            ]
        else
            []

    manifestVersionLine @ added @ removed @ changed |> String.concat "\n"

// ── The gate ──

let private gate = test "reference composition matches the committed baseline" {
    let manifest = referenceManifest ()
    let rendered = serialise manifest
    let path = baselinePath ()

    if approveModeOn () then
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, rendered)
    elif not (File.Exists path) then
        failtestf
            "no committed composition baseline at %s. Generate it with TOOLUP_APPROVE_COMPOSITION=1 and commit composition-baselines/composition-baseline.json in the same PR."
            path
    else
        let baseline =
            JsonSerializer.Deserialize<CompositionManifest>(File.ReadAllText path, jsonOptions)

        let delta = CompositionDiff.diff baseline manifest

        if not (CompositionDiff.isEmpty delta) then
            failtestf
                "Composition drift vs the committed baseline:\n%s\n\nIf this change is intentional, regenerate the baseline (TOOLUP_APPROVE_COMPOSITION=1) and commit the composition-baseline.json edit in the same PR so the change is reviewed."
                (CompositionDiff.render delta)
}

/// Phase 431 — the same gate over the derived event topology: a new edge,
/// a removed subscriber, or a dropped emitter fails CI until the change is
/// acknowledged by regenerating the golden file in the same PR. The
/// failure is rendered through `EventTopology.renderDelta`, so an operator
/// sees which component stopped talking to which.
let private topologyGate = test "reference event topology matches the committed baseline" {
    let topology = referenceTopology ()
    let rendered = serialiseTopology topology
    let path = topologyBaselinePath ()

    if approveModeOn () then
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, rendered)
    elif not (File.Exists path) then
        failtestf
            "no committed event-topology baseline at %s. Generate it with TOOLUP_APPROVE_COMPOSITION=1 and commit composition-baselines/event-topology-baseline.json in the same PR."
            path
    else
        let baseline =
            JsonSerializer.Deserialize<EventTopologyWireEntry list>(File.ReadAllText path, jsonOptions)
            |> EventTopology.ofWire

        let delta = EventTopology.diff baseline topology

        if not (EventTopology.isEmptyDelta delta) then
            failtestf
                "Event-topology drift vs the committed baseline:\n%s\n\nIf this change is intentional, regenerate the baseline (TOOLUP_APPROVE_COMPOSITION=1) and commit the event-topology-baseline.json edit in the same PR so the messaging-graph change is reviewed."
                (EventTopology.renderDelta delta)
}

/// Phase 433 — the same gate over the derived data footprint: a component
/// that starts persisting a class, stops persisting one, or has its class
/// re-classified as personal data fails CI until the change is acknowledged
/// by regenerating the golden file in the same PR. The failure is rendered
/// through `DataFootprint.renderDelta`, so an operator sees which component
/// started storing what, and whether it is PII.
let private footprintGate = test "reference data footprint matches the committed baseline" {
    let signature = referenceFootprint ()
    let rendered = serialiseFootprint signature
    let path = footprintBaselinePath ()

    if approveModeOn () then
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, rendered)
    elif not (File.Exists path) then
        failtestf
            "no committed data-footprint baseline at %s. Generate it with TOOLUP_APPROVE_COMPOSITION=1 and commit composition-baselines/data-footprint-baseline.json in the same PR."
            path
    else
        let baseline =
            JsonSerializer.Deserialize<DataFootprintWireEntry list>(File.ReadAllText path, jsonOptions)
            |> DataFootprint.ofWire

        let delta = DataFootprint.diff baseline signature

        if not (DataFootprint.isEmptyDelta delta) then
            failtestf
                "Data-footprint drift vs the committed baseline:\n%s\n\nIf this change is intentional, regenerate the baseline (TOOLUP_APPROVE_COMPOSITION=1) and commit the data-footprint-baseline.json edit in the same PR so the data-at-rest change is reviewed."
                (DataFootprint.renderDelta delta)
}

/// Phase 438 — the same gate over the derived authorization surface: an
/// endpoint that becomes reachable without authentication, a requirement
/// that is weakened, or a newly-exposed surface fails CI until the change
/// is acknowledged by regenerating the golden file in the same PR. The
/// failure is rendered through `AuthorizationSurface.renderDelta`, which
/// leads with the severity and marks the anonymous-reachable additions —
/// so the reviewer sees the attack-surface growth, not just a JSON diff.
let private authorizationGate = test "reference authorization surface matches the committed baseline" {
    let surface = referenceAuthorizationSurface ()
    let rendered = serialiseAuthorizationSurface surface
    let path = authorizationBaselinePath ()

    if approveModeOn () then
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, rendered)
    elif not (File.Exists path) then
        failtestf
            "no committed authorization-surface baseline at %s. Generate it with TOOLUP_APPROVE_COMPOSITION=1 and commit composition-baselines/authorization-surface-baseline.json in the same PR."
            path
    else
        let baseline =
            JsonSerializer.Deserialize<AuthorizationSurfaceWireEntry list>(File.ReadAllText path, jsonOptions)
            |> AuthorizationSurface.ofWire

        let delta = AuthorizationSurface.diff baseline surface

        if not (AuthorizationSurface.isEmptyDelta delta) then
            failtestf
                "Authorization-surface drift vs the committed baseline:\n%s\n\nIf this change is intentional, regenerate the baseline (TOOLUP_APPROVE_COMPOSITION=1) and commit the authorization-surface-baseline.json edit in the same PR so the exposure change is reviewed."
                (AuthorizationSurface.renderDelta delta)
}

/// Phase 597 — the same gate over the versioned rule manifest: a rule
/// added, removed, tightened, or reworded fails CI until the change is
/// acknowledged by regenerating the golden file in the same PR. The
/// failure classifies the bump, so the reviewer is told whether prior
/// conclusions still hold — the whole reason the rules carry versions.
let private ruleManifestGate = test "shipped rule manifest matches the committed baseline" {
    let document = ruleManifestDocument ()
    let rendered = serialiseRuleManifest document
    let path = ruleManifestBaselinePath ()

    if approveModeOn () then
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, rendered)
    elif not (File.Exists path) then
        failtestf
            "no committed rule-manifest baseline at %s. Generate it with TOOLUP_APPROVE_COMPOSITION=1 and commit composition-baselines/rule-manifest-baseline.json in the same PR."
            path
    else
        let baseline =
            JsonSerializer.Deserialize<RuleManifestWireDocument>(File.ReadAllText path, jsonOptions)

        if baseline <> document then
            failtestf
                "Rule-manifest drift vs the committed baseline:\n%s\n\nIf this change is intentional, bump the affected rule's version in CompositionRuleVersions.overrides per the discipline (patch = message / fix, minor = tightening, major = meaning change), regenerate the baseline (TOOLUP_APPROVE_COMPOSITION=1) and commit the rule-manifest-baseline.json edit in the same PR so the change to what preflight MEANS is reviewed."
                (renderRuleManifestDelta baseline document)
}

// ── Gate-mechanism fixtures: the load-bearing logic is the diff-driven
//    comparison; pin that it fails-closed on a regression and round-trips
//    the baseline JSON faithfully — WITHOUT touching the committed file. ──

let private mechanism =
    testList "gate mechanism" [

        // The reference manifest must diff clean against itself — otherwise
        // the gate would false-positive on every run.
        test "the reference composition diffs clean against itself" {
            let m = referenceManifest ()
            Expect.isTrue (CompositionDiff.isEmpty (CompositionDiff.diff m m)) "a manifest is identical to itself"
        }

        // The baseline persistence format must round-trip losslessly — if a
        // converter mangled a field, the gate would false-positive forever.
        test "the manifest round-trips through the baseline JSON format" {
            let m = referenceManifest ()
            let back = JsonSerializer.Deserialize<CompositionManifest>(serialise m, jsonOptions)

            Expect.isTrue
                (CompositionDiff.isEmpty (CompositionDiff.diff m back))
                "serialize -> deserialize preserves the composition structurally"
        }

        // A silently-dropped module trips the gate with a readable delta.
        test "a dropped module trips the gate with a readable delta" {
            let baseline = referenceManifest ()

            let regressed = {
                baseline with
                    Modules = baseline.Modules |> List.tail
            }

            let delta = CompositionDiff.diff baseline regressed
            Expect.isFalse (CompositionDiff.isEmpty delta) "a dropped module is not an empty diff"
            Expect.isNonEmpty delta.ModulesRemoved "the drop surfaces as a removed module"

            Expect.stringContains
                (CompositionDiff.render delta)
                "Modules"
                "the readable failure names the Modules section"
        }

        // A silently-dropped audit-sink companion trips the gate.
        test "a dropped companion trips the gate" {
            let baseline = referenceManifest ()

            let regressed = { baseline with CompanionSlots = [] }

            let delta = CompositionDiff.diff baseline regressed
            Expect.isFalse (CompositionDiff.isEmpty delta) "dropping the companion is not an empty diff"
            Expect.isNonEmpty delta.CompanionSlotsRemoved "the drop surfaces as a removed companion slot"
        }

        // Phase 431 — the same two properties for the topology gate.
        test "the reference topology diffs clean against itself" {
            let t = referenceTopology ()

            Expect.isTrue (EventTopology.isEmptyDelta (EventTopology.diff t t)) "a topology is identical to itself"
        }

        test "the topology round-trips through the baseline JSON format" {
            let t = referenceTopology ()

            let back =
                JsonSerializer.Deserialize<EventTopologyWireEntry list>(serialiseTopology t, jsonOptions)
                |> EventTopology.ofWire

            Expect.isTrue
                (EventTopology.isEmptyDelta (EventTopology.diff t back))
                "serialize -> deserialize preserves the topology structurally"
        }

        test "a severed messaging edge trips the topology gate" {
            let baseline = referenceTopology ()
            // The subscriber module drops out of the composition.
            let regressed = EventTopology.ofModules [ List.head (referenceModules ()) ]

            let delta = EventTopology.diff baseline regressed
            Expect.isFalse (EventTopology.isEmptyDelta delta) "a dropped subscriber is not an empty diff"

            Expect.isNonEmpty delta.SubscriptionsRemoved "the drop surfaces as removed subscriptions"

            Expect.stringContains
                (EventTopology.renderDelta delta)
                "Subscriptions"
                "the readable failure names the Subscriptions section"
        }

        // Phase 433 — the same two properties for the footprint gate.
        test "the reference footprint diffs clean against itself" {
            let f = referenceFootprint ()

            Expect.isTrue (DataFootprint.isEmptyDelta (DataFootprint.diff f f)) "a footprint is identical to itself"
        }

        test "the footprint round-trips through the baseline JSON format" {
            let f = referenceFootprint ()

            let back =
                JsonSerializer.Deserialize<DataFootprintWireEntry list>(serialiseFootprint f, jsonOptions)
                |> DataFootprint.ofWire

            Expect.isTrue
                (DataFootprint.isEmptyDelta (DataFootprint.diff f back))
                "serialize -> deserialize preserves the footprint structurally"
        }

        test "a newly-persisted PII class trips the footprint gate" {
            let baseline = referenceFootprint ()

            let regressed =
                baseline
                |> DataFootprint.reclassify [ DataClass.pii "SalesData" EntityClass DataObjectStoreSeam ]

            let delta = DataFootprint.diff baseline regressed
            Expect.isFalse (DataFootprint.isEmptyDelta delta) "a PII re-classification is not an empty diff"

            Expect.stringContains
                (DataFootprint.renderDelta delta)
                "PII"
                "the readable failure says the class now carries personal data"
        }

        // Phase 438 — the same two properties for the authorization gate.
        test "the reference authorization surface diffs clean against itself" {
            let s = referenceAuthorizationSurface ()

            Expect.isTrue
                (AuthorizationSurface.isEmptyDelta (AuthorizationSurface.diff s s))
                "a surface is identical to itself"
        }

        test "the authorization surface round-trips through the baseline JSON format" {
            let s = referenceAuthorizationSurface ()

            let back =
                JsonSerializer.Deserialize<AuthorizationSurfaceWireEntry list>(
                    serialiseAuthorizationSurface s,
                    jsonOptions
                )
                |> AuthorizationSurface.ofWire

            Expect.isTrue
                (AuthorizationSurface.isEmptyDelta (AuthorizationSurface.diff s back))
                "serialize -> deserialize preserves the surface structurally"
        }

        test "a newly anonymous-reachable endpoint trips the authorization gate at critical severity" {
            let baseline = referenceAuthorizationSurface ()

            // The admin sub-tree is opened to anonymous callers — the
            // single highest-signal change this gate exists to catch.
            let regressed =
                AuthorizationSurface.ofModules [
                    ServerModule.create "Orders"
                    |> ServerModule.withComponentId "orders-service"
                    |> ServerModule.withRoutePrefix "/api/orders/admin/"
                    |> ServerModule.withDefaultSurfaceRequirement SurfaceRequirement.public_
                ]

            let delta = AuthorizationSurface.diff baseline regressed

            Expect.isFalse (AuthorizationSurface.isEmptyDelta delta) "an opened endpoint is not an empty diff"

            Expect.equal
                (AuthorizationSurface.severity delta)
                CriticalAuthorizationDrift
                "a new anonymous-reachable entry is the critical class"

            let rendered = AuthorizationSurface.renderDelta delta
            Expect.stringContains rendered "CRITICAL" "the readable failure leads with the severity"

            Expect.stringContains rendered "/api/orders/admin/" "and names the endpoint that became reachable"
        }

        // Phase 597 — the same two properties for the rule-manifest gate.
        test "the rule manifest round-trips through the baseline JSON format" {
            let document = ruleManifestDocument ()

            let back =
                JsonSerializer.Deserialize<RuleManifestWireDocument>(serialiseRuleManifest document, jsonOptions)

            Expect.equal back document "serialize -> deserialize preserves the published rule manifest"
        }

        test "a tightened rule trips the rule-manifest gate and is reported as a tightening" {
            let baseline = ruleManifestDocument ()

            // The first rule tightens: strictly more compositions now
            // fail, so every prior pass under 1.0.0 stops being evidence.
            let regressed = {
                baseline with
                    Rules =
                        match baseline.Rules with
                        | first :: rest -> { first with Version = "1.1.0" } :: rest
                        | [] -> []
            }

            Expect.notEqual regressed baseline "a version bump is not an empty diff"

            let rendered = renderRuleManifestDelta baseline regressed

            Expect.stringContains rendered "TIGHTENED" "the readable failure says prior passes are no longer evidence"

            Expect.stringContains rendered (List.head baseline.Rules).Rule "and names the rule that moved"
        }

        test "a reworded rule with no version bump is reported as a missing patch bump" {
            let baseline = ruleManifestDocument ()

            let regressed = {
                baseline with
                    Rules =
                        match baseline.Rules with
                        | first :: rest ->
                            {
                                first with
                                    RuleDescription = first.RuleDescription + " (reworded)"
                            }
                            :: rest
                        | [] -> []
            }

            let rendered = renderRuleManifestDelta baseline regressed

            Expect.stringContains
                rendered
                "NO version bump"
                "a message change without a patch bump is called out, not silently accepted"
        }
    ]

let tests =
    testList "CompositionBaseline" [
        gate
        topologyGate
        footprintGate
        authorizationGate
        ruleManifestGate
        mechanism
    ]