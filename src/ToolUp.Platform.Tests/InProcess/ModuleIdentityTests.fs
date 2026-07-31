module ToolUp.Platform.Tests.InProcess.ModuleIdentityTests

open Expecto
open ToolUp.Elmish
open Feliz
open ToolUp.Platform

// ─── Phase 580 — client-shell module identity gate ────────────────────
//
// Three properties:
//
//   1. A duplicate client module id is a compose-time failure naming
//      BOTH display names and the colliding id — the exact
//      `ModuleIdentity.ensureUnique` call `Client.prepareModules` makes
//      over its composed list before `Client.program` builds the shell
//      (`Client.run`'s path), so the `Model.ModuleStates` map can never
//      silently collapse two modules into one entry.
//
//   2. The composed module-id table classifies each row as name-derived
//      or explicitly declared, so `ClientModule.create`'s invisible
//      `Name.Replace(" ", "")` derivation is auditable.
//
//   3. The cross-tier identity law: the client's derived id for a module
//      named "Channel Analysis" resolves to the same `ComponentId` the
//      server resolves for the `ServerModule` it must match by name.
//
// `ClientConfig.defaults` cannot be materialised under .NET (Fable-only
// React / jsNative values), so the tests drive the pure gate + the
// client-tier `moduleIdentityTable` projection over hand-built
// `ErasedModule` values rather than calling `prepareModules` itself —
// same call, same arguments. The wiring inside `prepareModules` is
// covered by the build's type-check and the `samples/MinimalClient`
// Fable transpile.

let private erasedModule (id: string) (name: string) : ErasedModule = {
    Definition = {
        Id = id
        Name = name
        Icon = Unchecked.defaultof<ReactElement>
        Pages = []
    }
    Init = fun _ -> box (), Cmd.none
    Update = fun _ state -> state, Cmd.none
    View = None
    PageViews = None
    NeedsData = None
    NeedsDataKeys = None
    DataTypes = []
    ProvidesProcessedData = None
    ProvidesNarrative = None
    Config = None
    FeatureFlags = []
    Availability = Always
    Group = Some "Workflow"
    // Phase 611 — declares no rail slot, i.e. ordinary group bucketing.
    Placement = None
    NavRole = None
    Area = ModuleArea.Product
    ClientQueryHandlers = []
    QueryTargets = None
    ActionDecoder = None
    ActionKeys = None
    Visibility = Visibility.visibleToAll
    EventSubscriptions = Map.empty
}

/// Resolve a registered server module's id the way `addModule` does, so
/// the cross-tier law is asserted against the real server composition
/// path rather than a re-derivation.
let private serverResolvedIds (modules: ServerModule list) : ComponentId list =
    (ServerApp.empty |> ServerApp.addModules modules).ModuleComponentIds
    |> List.map snd

let tests =
    testList "Phase 580 — ModuleIdentity" [

        // ── name → id derivation ─────────────────────────────────────
        testCase "deriveId strips spaces (the ClientModule.create default)"
        <| fun _ ->
            Expect.equal (ModuleIdentity.deriveId "Channel Analysis") "ChannelAnalysis" "spaces stripped"
            Expect.equal (ModuleIdentity.deriveId "Orders") "Orders" "space-free name is unchanged"
            Expect.equal (ModuleIdentity.deriveId "") "" "empty name derives empty"
            Expect.equal (ModuleIdentity.deriveId null) "" "null name derives empty rather than raising"

        testCase "ClientModule.create derives exactly deriveId(Name)"
        <| fun _ ->
            // The rule lifted into Core must stay the rule the client
            // registration surface actually applies.
            let m =
                ClientModule.create {
                    Init = fun () -> (), Cmd.none
                    Update = fun () () -> (), Cmd.none
                    Name = "Channel Analysis"
                    Icon = Unchecked.defaultof<ReactElement>
                }

            Expect.equal m.Definition.Id "ChannelAnalysis" "create auto-derives the id from Name"

            Expect.equal
                m.Definition.Id
                (ModuleIdentity.deriveId m.Definition.Name)
                "Core's derivation matches the registration surface's"

        // ── the cross-tier identity law ──────────────────────────────
        testCase "client-derived id and the server's ComponentId agree for the same module"
        <| fun _ ->
            // `ServerModule.Name` is documented as "must match the client
            // ClientModule.Definition.Id", so a client module displayed as
            // "Channel Analysis" pairs with a server module named
            // "ChannelAnalysis". Both tiers must land on one ComponentId.
            let clientModule =
                ClientModule.create {
                    Init = fun () -> (), Cmd.none
                    Update = fun () () -> (), Cmd.none
                    Name = "Channel Analysis"
                    Icon = Unchecked.defaultof<ReactElement>
                }

            let serverSide = serverResolvedIds [ ServerModule.create "ChannelAnalysis" ]

            Expect.equal
                [ ModuleIdentity.componentIdOf clientModule.Definition.Id ]
                serverSide
                "client Definition.Id and server ServerModule.Name resolve to the same ComponentId"

            Expect.equal
                (ModuleIdentity.componentIdOf "ChannelAnalysis")
                (ComponentId.ofModule "ChannelAnalysis")
                "componentIdOf is ComponentId.ofModule — one derivation, not two"

        // ── derived-vs-explicit classification ───────────────────────
        testCase "origin classifies name-derived vs explicitly-declared ids"
        <| fun _ ->
            Expect.equal
                (ModuleIdentity.originOf "Channel Analysis" "ChannelAnalysis")
                DerivedFromName
                "an id equal to the derivation reads as derived"

            Expect.equal
                (ModuleIdentity.originOf "Channel Analysis" "_sdk.channels")
                ExplicitlyDeclared
                "an id differing from the derivation must have been declared"

        testCase "moduleIdentityTable projects the composed client list in order"
        <| fun _ ->
            let rows =
                Client.moduleIdentityTable [
                    erasedModule "ChannelAnalysis" "Channel Analysis"
                    erasedModule "_sdk.DataManager" "Data Manager"
                ]

            Expect.equal (rows |> List.map _.Id) [ "ChannelAnalysis"; "_sdk.DataManager" ] "composition order preserved"

            Expect.equal
                (rows |> List.map _.Origin)
                [ DerivedFromName; ExplicitlyDeclared ]
                "each row carries its own derivation origin"

            Expect.equal
                (rows |> List.map _.ComponentId)
                [
                    ComponentId.ofModule "ChannelAnalysis"
                    ComponentId.ofModule "_sdk.DataManager"
                ]
                "each row lifts its id to the module-slot ComponentId"

        testCase "the rendered report names each id, its origin and its display name"
        <| fun _ ->
            let report =
                Client.moduleIdentityReport [
                    erasedModule "ChannelAnalysis" "Channel Analysis"
                    erasedModule "_sdk.DataManager" "Data Manager"
                ]

            Expect.stringContains report "ChannelAnalysis" "names the derived id"
            Expect.stringContains report "derived from Name" "marks the derived row"
            Expect.stringContains report "_sdk.DataManager" "names the explicit id"
            Expect.stringContains report "explicitly declared" "marks the explicit row"
            Expect.stringContains report "module:ChannelAnalysis" "shows the resolved ComponentId"
            Expect.stringContains report "Channel Analysis" "shows the display name behind a derived id"

        testCase "an empty composition renders a report rather than raising"
        <| fun _ ->
            Expect.stringContains (Client.moduleIdentityReport []) "no modules composed" "degenerate case is legible"

        // ── the gate ─────────────────────────────────────────────────
        testCase "duplicate client module id fails composition naming both modules and the id"
        <| fun _ ->
            // Two distinct modules, one id. Pre-580 this silently
            // collapsed `Model.ModuleStates` to a single entry.
            let rows =
                Client.moduleIdentityTable [
                    erasedModule "ChannelAnalysis" "Channel Analysis"
                    erasedModule "ChannelAnalysis" "Channel Performance"
                ]

            Expect.throwsC (fun () -> ModuleIdentity.ensureUnique "client module composition" rows) (fun ex ->
                Expect.stringContains ex.Message "client module composition" "names the composition stage"

                Expect.stringContains ex.Message "ChannelAnalysis" "names the colliding id"

                Expect.stringContains ex.Message "module:ChannelAnalysis" "names the resolved ComponentId"

                Expect.stringContains ex.Message "Channel Analysis" "names the first colliding module"

                Expect.stringContains ex.Message "Channel Performance" "names the second colliding module"

                Expect.stringContains ex.Message "ClientModule.withId" "points at the fix-site API surface")

        testCase "two modules whose different names derive the same id collide"
        <| fun _ ->
            // The invisible-derivation hazard in its sharpest form:
            // "Channel Analysis" and "ChannelAnalysis" are different
            // display names that derive one id.
            let a =
                ClientModule.create {
                    Init = fun () -> (), Cmd.none
                    Update = fun () () -> (), Cmd.none
                    Name = "Channel Analysis"
                    Icon = Unchecked.defaultof<ReactElement>
                }

            let b =
                ClientModule.create {
                    Init = fun () -> (), Cmd.none
                    Update = fun () () -> (), Cmd.none
                    Name = "ChannelAnalysis"
                    Icon = Unchecked.defaultof<ReactElement>
                }

            let rows =
                ModuleIdentity.table [ a.Definition.Name, a.Definition.Id; b.Definition.Name, b.Definition.Id ]

            Expect.throwsC (fun () -> ModuleIdentity.ensureUnique "client module composition" rows) (fun ex ->
                Expect.stringContains ex.Message "Channel Analysis" "names the spaced display name"

                Expect.stringContains ex.Message "ChannelAnalysis" "names the derived id / the unspaced name")

        testCase "every collision is reported, not just the first"
        <| fun _ ->
            let rows =
                Client.moduleIdentityTable [
                    erasedModule "Alpha" "Alpha"
                    erasedModule "Alpha" "Alpha Reporting"
                    erasedModule "Bravo" "Bravo"
                    erasedModule "Bravo" "Bravo Reporting"
                ]

            Expect.throwsC (fun () -> ModuleIdentity.ensureUnique "client module composition" rows) (fun ex ->
                Expect.stringContains ex.Message "Alpha Reporting" "first collision reported"

                Expect.stringContains ex.Message "Bravo Reporting" "second collision reported")

        testCase "collisions reports each duplicate id once with every colliding name"
        <| fun _ ->
            let rows =
                Client.moduleIdentityTable [
                    erasedModule "Alpha" "Alpha"
                    erasedModule "Alpha" "Alpha Reporting"
                    erasedModule "Bravo" "Bravo"
                ]

            Expect.equal
                (ModuleIdentity.collisions rows)
                [ ComponentId.ofModule "Alpha", [ "Alpha"; "Alpha Reporting" ] ]
                "one entry per colliding id, carrying both display names"

        testCase "distinct module ids pass the gate untouched (GP 11)"
        <| fun _ ->
            let modules = [
                erasedModule "ChannelAnalysis" "Channel Analysis"
                erasedModule "_sdk.DataManager" "Data Manager"
                erasedModule "_ai.AIAssistant" "AI Assistant"
            ]

            ModuleIdentity.ensureUnique "client module composition" (Client.moduleIdentityTable modules)

            Expect.isEmpty
                (ModuleIdentity.collisions (Client.moduleIdentityTable modules))
                "a unique-id composition reports no collision and is left alone"

        testCase "an empty composition passes the gate"
        <| fun _ ->
            ModuleIdentity.ensureUnique "client module composition" (Client.moduleIdentityTable [])
            Expect.isTrue true "no modules, no collision"
    ]