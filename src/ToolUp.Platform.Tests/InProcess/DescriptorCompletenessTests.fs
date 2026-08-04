module ToolUp.Platform.Tests.InProcess.DescriptorCompletenessTests

open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.FileProcessor

// ─── Phase 295 — descriptor completeness round-trip + partial/preset ──
//
// Covers the acceptance shape:
//  1. Completeness round-trip: an arbitrary composed `ServerApp` lowers to
//     a `CompositionDescriptor` (`toDescriptor`) and rebuilds — via
//     `ServerApp.ofManifest` against the same catalogue — to the *same*
//     full component set (lossless over modules + companions + the
//     module-derived datatypes / tools).
//  2. Partial / preset descriptors: a descriptor may leave declared holes
//     unbound (an archetype); `apply` fills them; the filled descriptor
//     composes the equivalent full composition.
//  3. An unfilled hole fails readably, naming it.
//
// NOTE (Phase 286 gate). The phase specifies proving the round-trip via
// Phase 286's structural manifest diff. Phase 286 is not yet shipped
// (Track W35-T4), so the round-trip is proven here by a local structural
// set-comparison of the projected component ids (the same added/removed
// delta a diff would report, computed inline). When Phase 286 lands, this
// local comparison should be swapped for `CompositionManifest`'s diff.

let private stubDataType (id: string) : DataType = {
    Info = {
        Id = id
        DisplayName = id
        Schema = None
    }
    Id = id
    Detect = fun _ -> async { return false }
    Process = fun _ -> async { return failwith "stub DataType.Process is never called by the completeness round-trip" }
}

let private stubTool (name: string) : AIToolDefinition * (HttpContext -> string -> Async<string>) =
    {
        Name = name
        Description = ""
        Parameters = []
        SourceModule = "completeness-test"
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
    },
    (fun _ _ -> async { return "" })

let private billing () : ServerModule =
    ServerModule.create "Billing"
    |> ServerModule.withComponentId "billing-service"
    |> ServerModule.withDataTypes [ stubDataType "Invoices" ]
    |> ServerModule.withAITools [ stubTool "billing.reconcile" ]

let private catalogue2 () : ServerModule = ServerModule.create "Catalogue"

let private sink () : IAuditSink =
    InMemoryAuditSink "s3-archive" :> IAuditSink

// A catalogue registering everything the representative app composes, so a
// lowered descriptor can be rebuilt against it.
let private catalogue () : RegistrationCatalogue =
    let s = sink ()

    RegistrationCatalogue.empty
    |> RegistrationCatalogue.addModule (billing ())
    |> RegistrationCatalogue.addModule (catalogue2 ())
    |> RegistrationCatalogue.add (ComponentId.forCompanionImpl "IAuditSink" s.Name) (fun _ app ->
        ServerApp.withAuditSink s app)

// A representative composed app exercising every manifest kind.
let private representativeApp () : ServerApp =
    ServerApp.empty
    |> ServerApp.addModules [ billing (); catalogue2 () ]
    |> ServerApp.withAuditSink (sink ())

// The full projected component-id set (modules + companions + datatypes +
// tools) — the surface the completeness law must reproduce.
let private fullIds (app: ServerApp) : Set<ComponentId> =
    ServerApp.compositionManifest app
    |> CompositionManifest.allComponents
    |> List.map _.Id
    |> Set.ofList

let tests =
    testList "DescriptorCompleteness" [

        // ── completeness round-trip (lossless over the full surface) ──
        testCase "an arbitrary composed app round-trips losslessly through the descriptor"
        <| fun _ ->
            let app = representativeApp ()

            let rebuilt =
                ServerApp.ofManifest (catalogue (), CompositionDescriptor.toDescriptor app)

            let before = fullIds app
            let after = fullIds rebuilt

            // The structural "diff" a Phase 286 comparison would report:
            // both directions empty ⇒ lossless.
            let added = Set.difference after before
            let removed = Set.difference before after

            Expect.isEmpty (Set.toList added) "no component appears that the original did not compose"
            Expect.isEmpty (Set.toList removed) "no component the original composed is lost in the round-trip"
            Expect.equal after before "the round-trip reproduces the full component set exactly"

        // ── the lowered descriptor lists only modules + companions ────
        testCase "toDescriptor lowers modules + companions; datatypes/tools reappear transitively"
        <| fun _ ->
            let descriptor = CompositionDescriptor.toDescriptor (representativeApp ())

            // Direct selections: the two modules + the one audit-sink impl.
            Expect.equal
                (CompositionDescriptor.componentIds descriptor |> List.sort)
                ([
                    ComponentId.ofModule "billing-service"
                    ComponentId.ofModule "Catalogue"
                    ComponentId.forCompanionImpl "IAuditSink" "s3-archive"
                 ]
                 |> List.sort)
                "the descriptor lists exactly the module + companion selections"

            // But the rebuilt manifest still carries the module-derived datatype + tool.
            let rebuiltManifest =
                ServerApp.compositionManifest (ServerApp.ofManifest (catalogue (), descriptor))

            Expect.contains
                (rebuiltManifest.DataTypes |> List.map _.Id)
                (ComponentId.forDataType "Invoices")
                "datatype reappears"

            Expect.contains
                (rebuiltManifest.Tools |> List.map _.Id)
                (ComponentId.forTool "billing.reconcile")
                "tool reappears"

        // ── partial / preset descriptor + hole binding → equivalent ───
        testCase "a preset descriptor + a hole-binding composes the equivalent full composition"
        <| fun _ ->
            // A preset: the base module fixed, an "audit" hole left unbound.
            let preset =
                CompositionDescriptor.create
                    [ CompositionDescriptor.select (ComponentId.ofModule "billing-service") ]
                    ServerConfig.defaults
                |> CompositionDescriptor.withHoles [ "audit" ]

            Expect.equal (CompositionDescriptor.unfilledHoles preset) [ "audit" ] "the preset declares one unbound hole"

            // Fill the hole with the audit-sink selection.
            let bound =
                preset
                |> CompositionDescriptor.apply "audit" [
                    CompositionDescriptor.select (ComponentId.forCompanionImpl "IAuditSink" "s3-archive")
                ]

            Expect.isEmpty (CompositionDescriptor.unfilledHoles bound) "the hole is now filled"

            // The bound preset composes the same surface as a directly-built
            // billing + audit-sink app.
            let boundApp = ServerApp.ofManifest (catalogue (), bound)

            let directApp =
                ServerApp.empty
                |> ServerApp.addModule (billing ())
                |> ServerApp.withAuditSink (sink ())

            Expect.equal
                (fullIds boundApp)
                (fullIds directApp)
                "the filled preset composes the equivalent full composition"

        // ── an unfilled hole fails readably, naming it ────────────────
        testCase "composing a descriptor with an unfilled hole fails, naming the hole"
        <| fun _ ->
            let preset =
                CompositionDescriptor.create
                    [ CompositionDescriptor.select (ComponentId.ofModule "billing-service") ]
                    ServerConfig.defaults
                |> CompositionDescriptor.withHoles [ "audit"; "storage" ]

            match CompositionDescriptor.ofManifest (catalogue ()) preset with
            | Ok _ -> failtest "expected an UnfilledHoles error for an unbound preset"
            | Error(UnfilledHoles names) ->
                Expect.equal (List.sort names) [ "audit"; "storage" ] "every unfilled hole is reported"

                let message = CompositionDescriptor.renderError (UnfilledHoles names)
                Expect.stringContains message "audit" "the readable message names the unfilled hole"
            | Error other -> failtestf "expected UnfilledHoles, got %A" other

        // ── apply against a typo'd hole name leaves it unfilled ───────
        testCase "apply against an undeclared hole name is a no-op that surfaces as an unfilled hole"
        <| fun _ ->
            let preset =
                CompositionDescriptor.create [] ServerConfig.defaults
                |> CompositionDescriptor.withHoles [ "audit" ]
                // Typo: "audi" is not the declared "audit".
                |> CompositionDescriptor.apply "audi" [
                    CompositionDescriptor.select (ComponentId.forCompanionImpl "IAuditSink" "s3-archive")
                ]

            Expect.equal
                (CompositionDescriptor.unfilledHoles preset)
                [ "audit" ]
                "the mistyped apply left the real hole unbound rather than binding stray components"

        // ── a fully-bound descriptor with no holes is unaffected ──────
        testCase "a descriptor with no holes composes exactly its Components (GP 11)"
        <| fun _ ->
            let d =
                CompositionDescriptor.create
                    [ CompositionDescriptor.select (ComponentId.ofModule "Catalogue") ]
                    ServerConfig.defaults

            Expect.isEmpty (CompositionDescriptor.unfilledHoles d) "no holes declared"

            let app = ServerApp.ofManifest (catalogue (), d)

            Expect.equal
                (ServerApp.compositionManifest app |> _.Modules |> List.map _.Id)
                [ ComponentId.ofModule "Catalogue" ]
                "the holeless descriptor composes exactly its declared component"
    ]