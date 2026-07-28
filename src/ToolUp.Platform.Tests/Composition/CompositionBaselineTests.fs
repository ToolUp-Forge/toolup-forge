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
/// composition baseline is unaffected by them.
let private referenceModules () : ServerModule list =
    let orders =
        ServerModule.create "Orders"
        |> ServerModule.withComponentId "orders-service"
        |> ServerModule.withDataTypes [ stubDataType "SalesData" ]
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
    ]

let tests =
    testList "CompositionBaseline" [ gate; topologyGate; footprintGate; mechanism ]