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

let private stubTool (name: string) : AIToolDefinition * (HttpContext -> string -> Async<string>) =
    {
        Name = name
        Description = ""
        Parameters = []
        SourceModule = "baseline-reference"
        EmitsActions = None
        Location = ServerResident
        Surface = Both
    },
    (fun _ _ -> async { return "" })

/// The reference composition the gate snapshots — a couple of modules
/// (explicit stable ids), a datatype + tool on the first, and an audit-sink
/// companion. Representative enough that an accidental drop / swap /
/// datatype change in forge's compose surface is caught, small enough to
/// stay stable across unrelated changes. Editing it is a deliberate act
/// that regenerates the baseline.
let private referenceApp () : ServerApp =
    let orders =
        ServerModule.create "Orders"
        |> ServerModule.withComponentId "orders-service"
        |> ServerModule.withDataTypes [ stubDataType "SalesData" ]
        |> ServerModule.withAITools [ stubTool "orders.run" ]

    let inventory =
        ServerModule.create "Inventory"
        |> ServerModule.withComponentId "inventory-service"
        |> ServerModule.withDataTypes [ stubDataType "StockData" ]

    ServerApp.empty
    |> ServerApp.addModules [ orders; inventory ]
    |> ServerApp.withAuditSink (InMemoryAuditSink "primary-archive")

let private referenceManifest () : CompositionManifest =
    ServerApp.compositionManifest (referenceApp ())

let private serialise (m: CompositionManifest) : string =
    JsonSerializer.Serialize(m, jsonOptions).Replace("\r\n", "\n")

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
    ]

let tests = testList "CompositionBaseline" [ gate; mechanism ]