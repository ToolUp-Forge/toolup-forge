module ToolUp.Platform.Tests.InProcess.ModuleBuilderSemanticsTests

open Expecto
open System
open Feliz
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.VectorisationTypes

// ─── Phase 1050 — a module builder says whether it sets or adds ───────
//
// `withDataTypes` (and its client twin) REPLACES the module's list, while
// `withHandlers` two lines away APPENDS, so two parties registering types
// into one module silently lost the first set. The ruling kept every
// existing builder's behaviour and added an explicitly appending twin.
// These cases pin BOTH halves, in both orders: an `add*` after a `with*`
// keeps both sets, and a `with*` after an `add*` still replaces — so
// neither semantics can drift without going red here.

let private dataType (id: string) : DataType = {
    Info = {
        Id = id
        DisplayName = id
        Schema = None
    }
    Id = id
    SchemaVersion = DataTypes.initialSchemaVersion
    Migrations = []
    Detect = fun _ -> async { return false }
    Process =
        fun _ -> async {
            return
                { TypeName = id; Payload = "{}" },
                {
                    FileName = ""
                    DataType = id
                    ProcessedAt = DateTime.UnixEpoch
                    Summary = None
                    Error = None
                }
        }
}

let private vectorisation (id: string) : VectorisationHandler = {
    DataTypeId = id
    Vectorise = fun _ -> []
    Summarise = None
}

let private display (id: string) : DataTypeDisplay = {
    Info = {
        Id = id
        DisplayName = id
        Schema = None
    }
    RenderSummary = fun _ -> Unchecked.defaultof<ReactElement>
}

let private queryHandler (key: string) : ModuleQueryHandler = {
    QueryKey = key
    Handle = fun _ -> async { return "" }
}

let private flag (key: string) : FeatureFlag = {
    Key = key
    DefaultValue = FlagValue.Bool false
    Description = key
    Owner = None
}

let private clientModule () =
    ClientModule.create {
        Init = fun () -> (), Cmd.none
        Update = fun () () -> (), Cmd.none
        Name = "Builder"
        Icon = Unchecked.defaultof<ReactElement>
    }

let private serverDataTypeIds (m: ServerModule) = m.DataTypes |> List.map _.Id

[<Tests>]
let tests =
    testList "Phase 1050 — module builders say whether they set or add" [

        // ── ServerModule ─────────────────────────────────────────────
        testCase "ServerModule: withDataTypes then addDataTypes keeps both sets"
        <| fun _ ->
            let m =
                ServerModule.create "Trading"
                |> ServerModule.withDataTypes [ dataType "Generated.A"; dataType "Generated.B" ]
                |> ServerModule.addDataTypes [ dataType "Authored.C" ]

            Expect.equal
                (serverDataTypeIds m)
                [ "Generated.A"; "Generated.B"; "Authored.C" ]
                "both parties' types are registered, in registration order"

        testCase "ServerModule: withDataTypes after addDataTypes still replaces"
        <| fun _ ->
            let m =
                ServerModule.create "Trading"
                |> ServerModule.addDataTypes [ dataType "Authored.C" ]
                |> ServerModule.withDataTypes [ dataType "Generated.A" ]

            Expect.equal (serverDataTypeIds m) [ "Generated.A" ] "withDataTypes sets the list — Phase 1050 kept that"

        testCase "ServerModule: addDataTypes on a fresh module is the first registration"
        <| fun _ ->
            let m =
                ServerModule.create "Trading"
                |> ServerModule.addDataTypes [ dataType "A" ]
                |> ServerModule.addDataTypes [ dataType "B" ]

            Expect.equal (serverDataTypeIds m) [ "A"; "B" ] "repeated adds accumulate"

        testCase "ServerModule: withVectorisation sets, addVectorisation adds"
        <| fun _ ->
            let ids (m: ServerModule) =
                m.VectorisationHandlers |> List.map _.DataTypeId

            let added =
                ServerModule.create "Trading"
                |> ServerModule.withVectorisation [ vectorisation "A" ]
                |> ServerModule.addVectorisation [ vectorisation "B" ]

            let replaced =
                ServerModule.create "Trading"
                |> ServerModule.addVectorisation [ vectorisation "B" ]
                |> ServerModule.withVectorisation [ vectorisation "A" ]

            Expect.equal (ids added) [ "A"; "B" ] "addVectorisation keeps the earlier handlers"
            Expect.equal (ids replaced) [ "A" ] "withVectorisation still replaces"

        // ── ClientModule ─────────────────────────────────────────────
        testCase "ClientModule: withDataTypes then addDataTypes keeps both sets"
        <| fun _ ->
            let m =
                clientModule ()
                |> ClientModule.withDataTypes [ display "Generated.A" ]
                |> ClientModule.addDataTypes [ display "Authored.C" ]

            Expect.equal (m.DataTypes |> List.map _.Info.Id) [ "Generated.A"; "Authored.C" ] "both parties' types"

        testCase "ClientModule: withDataTypes after addDataTypes still replaces"
        <| fun _ ->
            let m =
                clientModule ()
                |> ClientModule.addDataTypes [ display "Authored.C" ]
                |> ClientModule.withDataTypes [ display "Generated.A" ]

            Expect.equal (m.DataTypes |> List.map _.Info.Id) [ "Generated.A" ] "withDataTypes sets the list"

        testCase "ClientModule: withQueryHandlers sets, addQueryHandlers adds"
        <| fun _ ->
            let keys (m: ClientModule<unit, unit>) =
                m.ClientQueryHandlers |> List.map _.QueryKey

            let added =
                clientModule ()
                |> ClientModule.withQueryHandlers [ queryHandler "a" ]
                |> ClientModule.addQueryHandlers [ queryHandler "b" ]

            let replaced =
                clientModule ()
                |> ClientModule.addQueryHandlers [ queryHandler "b" ]
                |> ClientModule.withQueryHandlers [ queryHandler "a" ]

            Expect.equal (keys added) [ "a"; "b" ] "addQueryHandlers keeps the earlier handlers"
            Expect.equal (keys replaced) [ "a" ] "withQueryHandlers still replaces"

        testCase "ClientModule: withFeatureFlags sets, addFeatureFlags adds"
        <| fun _ ->
            let keys (m: ClientModule<unit, unit>) = m.FeatureFlags |> List.map _.Key

            let added =
                clientModule ()
                |> ClientModule.withFeatureFlags [ flag "a" ]
                |> ClientModule.addFeatureFlags [ flag "b" ]

            let replaced =
                clientModule ()
                |> ClientModule.addFeatureFlags [ flag "b" ]
                |> ClientModule.withFeatureFlags [ flag "a" ]

            Expect.equal (keys added) [ "a"; "b" ] "addFeatureFlags keeps the earlier flags"
            Expect.equal (keys replaced) [ "a" ] "withFeatureFlags still replaces"

        testCase "ClientModule: withQueryTargets sets, addQueryTargets adds — and declares on a fresh module"
        <| fun _ ->
            let catalogue = ModuleQueryTarget.create "Catalogue" "sku-lookup"
            let pricing = ModuleQueryTarget.create "Pricing" "quote"

            let fresh = clientModule () |> ClientModule.addQueryTargets [ catalogue ]

            let added =
                clientModule ()
                |> ClientModule.withQueryTargets [ catalogue ]
                |> ClientModule.addQueryTargets [ pricing ]

            let replaced =
                clientModule ()
                |> ClientModule.addQueryTargets [ pricing ]
                |> ClientModule.withQueryTargets [ catalogue ]

            Expect.equal
                fresh.QueryTargets
                (Some [ catalogue ])
                "an add on an undeclared module is its first declaration"

            Expect.equal added.QueryTargets (Some [ catalogue; pricing ]) "addQueryTargets keeps the earlier targets"
            Expect.equal replaced.QueryTargets (Some [ catalogue ]) "withQueryTargets still replaces"
    ]