module ToolUp.Platform.Tests.InProcess.CompositionDescriptorTests

open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.FileProcessor

// ─── Phase 284 — declarative composition descriptor + ofManifest ──────
//
// Covers the acceptance shape: a `CompositionDescriptor` (component
// selection by stable Phase 279 `ComponentId` + `ServerConfig`) builds —
// via `CompositionDescriptor.ofManifest` / `ServerApp.ofManifest` against
// a `RegistrationCatalogue` — a `ServerApp` equivalent to the
// fluent-built one; the manifest round-trip law holds
// (`manifest (ofManifest cat d)` reproduces `d`'s selected component ids);
// and a descriptor referencing an unknown component id fails with a
// readable error rather than composing a partial app.

// A minimal `DataType` whose `Detect` / `Process` are never invoked —
// only its `Id` is read by the manifest projector.
let private stubDataType (id: string) : DataType = {
    Info = {
        Id = id
        DisplayName = id
        Schema = None
    }
    Id = id
    Detect = fun _ -> async { return false }
    Process = fun _ -> async { return failwith "stub DataType.Process is never called by the descriptor round-trip" }
}

// A minimal AI tool registration — definition + a no-op executor.
let private stubTool (name: string) : AIToolDefinition * (HttpContext -> string -> Async<string>) =
    {
        Name = name
        Description = ""
        Parameters = []
        SourceModule = "descriptor-test"
        EmitsActions = None
        Location = ServerResident
        Surface = Both
    },
    (fun _ _ -> async { return "" })

// Two representative modules: one declares an explicit id + a datatype +
// a tool, the other is name-derived. Their resolved ids are the catalogue
// keys a descriptor selects.
let private ordersModule () : ServerModule =
    ServerModule.create "Orders"
    |> ServerModule.withComponentId "orders-service"
    |> ServerModule.withDataTypes [ stubDataType "SalesData" ]
    |> ServerModule.withAITools [ stubTool "orders.run" ]

let private inventoryModule () : ServerModule = ServerModule.create "Inventory"

let private auditSink () : IAuditSink =
    InMemoryAuditSink "splunk-archive" :> IAuditSink

// The catalogue: the code side. Registers both modules by their resolved
// id and an audit-sink companion under its slot+sub-id
// (`ComponentId.forCompanionImpl`), so a descriptor selecting those ids
// resolves the real registrations.
let private catalogue () : RegistrationCatalogue =
    let sink = auditSink ()

    RegistrationCatalogue.empty
    |> RegistrationCatalogue.addModule (ordersModule ())
    |> RegistrationCatalogue.addModule (inventoryModule ())
    |> RegistrationCatalogue.add (ComponentId.forCompanionImpl "IAuditSink" sink.Name) (fun _ app ->
        ServerApp.withAuditSink sink app)

// A descriptor selecting all three components (as data), over the default
// ServerConfig.
let private descriptor () : CompositionDescriptor =
    CompositionDescriptor.create
        [
            CompositionDescriptor.select (ComponentId.ofModule "orders-service")
            CompositionDescriptor.select (ComponentId.ofModule "Inventory")
            CompositionDescriptor.select (ComponentId.forCompanionImpl "IAuditSink" "splunk-archive")
        ]
        ServerConfig.defaults

// The equivalent fluent-built app — the composition the descriptor is a
// serializable description of.
let private fluentApp () : ServerApp =
    ServerApp.empty
    |> ServerApp.addModules [ ordersModule (); inventoryModule () ]
    |> ServerApp.withAuditSink (auditSink ())

let tests =
    testList "CompositionDescriptor" [

        // ── ofManifest builds an app equivalent to the fluent one ─────
        testCase "ofManifest builds a ServerApp equivalent to the fluent-built app"
        <| fun _ ->
            let built = ServerApp.ofManifest (catalogue (), descriptor ())

            let builtManifest = ServerApp.compositionManifest built
            let fluentManifest = ServerApp.compositionManifest (fluentApp ())

            // The projected manifests match across every kind — the
            // descriptor drives the same registrations the builders would.
            Expect.equal
                (CompositionManifest.allComponents builtManifest |> List.map _.Id |> List.sort)
                (CompositionManifest.allComponents fluentManifest |> List.map _.Id |> List.sort)
                "descriptor-built and fluent-built apps project to the same component set"

        // ── datatypes + tools fall out of the module registrations ────
        testCase "module-derived datatypes and tools appear transitively"
        <| fun _ ->
            let manifest =
                ServerApp.compositionManifest (ServerApp.ofManifest (catalogue (), descriptor ()))

            Expect.equal
                (manifest.DataTypes |> List.map _.Id)
                [ ComponentId.forDataType "SalesData" ]
                "the module's datatype is present though the descriptor never lists it directly"

            Expect.equal
                (manifest.Tools |> List.map _.Id)
                [ ComponentId.forTool "orders.run" ]
                "the module's tool is present transitively"

        // ── round-trip law: manifest reproduces the selected ids ──────
        testCase "round-trip law: the manifest reproduces the descriptor's module + companion selections"
        <| fun _ ->
            let d = descriptor ()

            let manifest =
                ServerApp.compositionManifest (ServerApp.ofManifest (catalogue (), d))

            let projectedSelectedIds =
                (manifest.Modules @ manifest.CompanionSlots) |> List.map _.Id |> Set.ofList

            Expect.equal
                projectedSelectedIds
                (CompositionDescriptor.componentIds d |> Set.ofList)
                "the descriptor's selected component ids are exactly the projected module + companion ids"

        // ── config carried by the descriptor seeds the app ────────────
        testCase "the descriptor's ServerConfig seeds the built app"
        <| fun _ ->
            let config = {
                ServerConfig.defaults with
                    RateLimiter = EnabledRateLimiter
            }

            let d = CompositionDescriptor.create [] config
            let built = ServerApp.ofManifest (catalogue (), d)

            Expect.equal
                built.Config.RateLimiter
                EnabledRateLimiter
                "the descriptor's config is carried onto the built app"

        // ── unknown component id fails readably (total) ───────────────
        testCase "an unknown component id fails with a readable error naming it"
        <| fun _ ->
            let d =
                CompositionDescriptor.create
                    [
                        CompositionDescriptor.select (ComponentId.ofModule "orders-service")
                        CompositionDescriptor.select (ComponentId.ofModule "not-registered")
                    ]
                    ServerConfig.defaults

            match CompositionDescriptor.ofManifest (catalogue ()) d with
            | Ok _ -> failtest "expected an UnknownComponents error for the unregistered id"
            | Error(UnknownComponents ids) ->
                Expect.equal ids [ ComponentId.ofModule "not-registered" ] "the single unresolved id is reported"

                let message = CompositionDescriptor.renderError (UnknownComponents ids)
                Expect.stringContains message "module:not-registered" "the readable message names the offending id"
            | Error(UnfilledHoles holes) -> failtestf "expected UnknownComponents; got UnfilledHoles %A" holes

        // ── every unresolved id is reported, not just the first ───────
        testCase "ofManifest reports every unresolved id together"
        <| fun _ ->
            let d =
                CompositionDescriptor.create
                    [
                        CompositionDescriptor.select (ComponentId.ofModule "ghost-a")
                        CompositionDescriptor.select (ComponentId.ofModule "orders-service")
                        CompositionDescriptor.select (ComponentId.ofModule "ghost-b")
                    ]
                    ServerConfig.defaults

            match CompositionDescriptor.ofManifest (catalogue ()) d with
            | Error(UnknownComponents ids) ->
                Expect.equal
                    (ids |> List.map ComponentId.value |> List.sort)
                    [ "module:ghost-a"; "module:ghost-b" ]
                    "both unresolved ids are reported; the resolvable one is not"
            | Ok _ -> failtest "expected an UnknownComponents error"
            | Error(UnfilledHoles holes) -> failtestf "expected UnknownComponents; got UnfilledHoles %A" holes

        // ── the raising ServerApp.ofManifest surfaces the message ─────
        testCase "ServerApp.ofManifest raises the readable message on an unknown id"
        <| fun _ ->
            let d =
                CompositionDescriptor.create
                    [ CompositionDescriptor.select (ComponentId.ofModule "phantom") ]
                    ServerConfig.defaults

            Expect.throws (fun () -> ServerApp.ofManifest (catalogue (), d) |> ignore) "an unknown id raises"

        // ── GP 11 / GP 13: an empty descriptor builds an empty app ────
        testCase "an empty descriptor builds a component-free app"
        <| fun _ ->
            let built =
                ServerApp.ofManifest (catalogue (), CompositionDescriptor.create [] ServerConfig.defaults)

            let manifest = ServerApp.compositionManifest built

            Expect.isEmpty (CompositionManifest.allComponents manifest) "no components composed from an empty selection"
    ]