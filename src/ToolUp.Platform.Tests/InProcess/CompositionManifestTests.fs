module ToolUp.Platform.Tests.InProcess.CompositionManifestTests

open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.FileProcessor

// ─── Phase 280 — introspectable composition manifest ──────────────────
//
// Covers the acceptance shape: a composed app yields a `CompositionManifest`
// enumerating every module, companion slot, datatype, and tool by its
// stable Phase 279 `ComponentId`, derived from the live registry (not a
// hand-declared list); a never-introspected / empty pipeline yields an
// empty manifest (no components) and the projector is pure + on demand
// (GP 13 — nothing is built unless `compositionManifest` is called).

// A minimal `DataType` whose `Detect` / `Process` are never invoked here —
// the manifest reads its `Id` only.
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

// A minimal AI tool registration — definition + a no-op executor.
let private stubTool (name: string) : AIToolDefinition * (HttpContext -> string -> Async<string>) =
    {
        Name = name
        Description = ""
        Parameters = []
        SourceModule = "manifest-test"
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
    },
    (fun _ _ -> async { return "" })

/// A representative composed app: two modules (one with an explicit id,
/// one name-derived), a datatype + a tool on the first module, and an
/// audit-sink companion. Exercises every manifest kind through the real
/// `addModule` / `with*` path rather than a re-derivation.
let private sampleApp () : ServerApp =
    let orders =
        ServerModule.create "Orders"
        |> ServerModule.withComponentId "orders-service"
        |> ServerModule.withDataTypes [ stubDataType "SalesData" ]
        |> ServerModule.withAITools [ stubTool "orders.run" ]

    let inventory = ServerModule.create "Inventory"

    ServerApp.empty
    |> ServerApp.addModules [ orders; inventory ]
    |> ServerApp.withAuditSink (InMemoryAuditSink "splunk-archive")

let tests =
    testList "CompositionManifest" [

        // ── modules enumerated by their resolved Phase 279 id ─────────
        testCase "enumerates every module by its resolved ComponentId"
        <| fun _ ->
            let manifest = ServerApp.compositionManifest (sampleApp ())
            let ids = manifest.Modules |> List.map _.Id

            // Explicit id resolves to ofModule(declaredId); the other to
            // ofModule(Name) — exactly what addModule accumulated.
            Expect.containsAll
                ids
                [ ComponentId.ofModule "orders-service"; ComponentId.ofModule "Inventory" ]
                "both module ids present, derived from the live registry"

            Expect.equal manifest.Modules.Length 2 "exactly the two registered modules"

        // ── datatypes namespaced under the datatype slot ─────────────
        testCase "enumerates datatypes by ComponentId.forDataType"
        <| fun _ ->
            let manifest = ServerApp.compositionManifest (sampleApp ())

            Expect.equal
                (manifest.DataTypes |> List.map _.Id)
                [ ComponentId.forDataType "SalesData" ]
                "datatype id namespaced under the datatype slot"

        // ── tools namespaced under the tool slot ─────────────────────
        testCase "enumerates tools by ComponentId.forTool"
        <| fun _ ->
            let manifest = ServerApp.compositionManifest (sampleApp ())

            Expect.equal
                (manifest.Tools |> List.map _.Id)
                [ ComponentId.forTool "orders.run" ]
                "tool id namespaced under the tool slot"

        // ── companion slot: multi-impl composes slot + sub-id ────────
        testCase "enumerates a companion impl by slot + sub-id, never positional"
        <| fun _ ->
            let manifest = ServerApp.compositionManifest (sampleApp ())

            let auditEntry =
                manifest.CompanionSlots |> List.tryFind (fun e -> e.Label = "IAuditSink")

            match auditEntry with
            | None -> failtest "expected an IAuditSink companion entry"
            | Some e ->
                Expect.equal
                    e.Id
                    (ComponentId.forCompanionImpl "IAuditSink" "splunk-archive")
                    "id composes slot + impl sub-id"

                Expect.equal e.Impl (Some "splunk-archive") "the impl sub-id is the sink Name"
                Expect.equal e.Kind CompanionComponent "kind is CompanionComponent"

        // ── config knobs: composition-shaping switches enumerated ────
        testCase "summarises the composition-shaping config knobs"
        <| fun _ ->
            let manifest = ServerApp.compositionManifest (sampleApp ())
            let knobNames = manifest.ConfigKnobs |> List.map _.Name

            Expect.containsAll
                knobNames
                [ "ConfigDriftDetection"; "JobScheduler"; "RateLimiter"; "ProcessProfile" ]
                "the principal composition-shaping knobs are present"

        // ── GP 13: empty / never-introspected pipeline → no components ─
        testCase "an empty pipeline yields a manifest with no components"
        <| fun _ ->
            let manifest = ServerApp.compositionManifest ServerApp.empty

            Expect.isEmpty (CompositionManifest.allComponents manifest) "no modules / companions / datatypes / tools"
            Expect.isEmpty manifest.Modules "no modules"
            Expect.isEmpty manifest.CompanionSlots "no companion slots"
            Expect.isEmpty manifest.DataTypes "no datatypes"
            Expect.isEmpty manifest.Tools "no tools"

        // ── pure projection: same registry → same manifest ───────────
        testCase "projection is deterministic (pure over the registry)"
        <| fun _ ->
            let app = sampleApp ()

            Expect.equal
                (ServerApp.compositionManifest app)
                (ServerApp.compositionManifest app)
                "same app projects to an identical manifest"

        // ── derived from the live registry, not a declared list ──────
        testCase "manifest grows when the registry grows (no drift gap)"
        <| fun _ ->
            let before = ServerApp.compositionManifest (sampleApp ())

            let after =
                ServerApp.compositionManifest (sampleApp () |> ServerApp.addModule (ServerModule.create "Shipping"))

            Expect.equal
                (after.Modules.Length - before.Modules.Length)
                1
                "adding a module to the registry adds exactly one module entry"

            Expect.contains
                (after.Modules |> List.map _.Id)
                (ComponentId.ofModule "Shipping")
                "the new module is enumerated"
    ]