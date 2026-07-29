module ToolUp.Platform.Tests.InProcess.ModuleGraphRuleTests

open Expecto
open ToolUp.Elmish
open Feliz
open ToolUp.Platform

// ─── Phase 583 — module-graph composition rules ───────────────────────
//
// The four rules extend the Phase 281 preflight over the composed MODULE
// GRAPH, reading the pre-collapse registration edges on
// `CompositionReferences.ModuleGraph` rather than the `ComponentId`-keyed
// manifest — because the collapse IS the blind spot the rules exist for.
//
// Shape of the coverage, per the phase's acceptance criteria:
//   * each rule FIRES on a synthetic bad composition;
//   * every rule stays SILENT on a well-formed reference composition;
//   * the parity rule is DORMANT undeclared (GP 13) — the "costs nothing"
//     claim is a test, not a comment;
//   * the client half (`ModuleParityValidator`) agrees with the server
//     rule on the same declared list, which is what makes one declaration
//     establish parity across two roots that never see each other.
//
// The manifest ⇔ rule-code bijection lives in `InvariantRuleManifestTests`
// (one crafted fixture per rule); this file is the behavioural half.

let private moduleEntry (name: string) =
    CompositionManifest.moduleEntry (name, ComponentId.ofModule name)

let private queryKey (moduleName: string) (key: string) : ModuleGraphKey = {
    DeclaringModule = moduleName
    RegisteredKey = key
}

let private dataTypeKey (moduleName: string) (typeName: string) : ModuleGraphKey = {
    DeclaringModule = moduleName
    RegisteredKey = typeName
}

let private withGraph (graph: ModuleGraphReferences) : CompositionReferences = {
    CompositionReferences.empty with
        ModuleGraph = graph
}

/// A well-formed reference composition: two modules, distinct bus keys,
/// distinct data-type wire names, every declared need provided, and no
/// expected-module list. Every rule in the SDK must stay silent on it.
let private referenceManifest =
    CompositionManifest.build
        [ moduleEntry "Sales"; moduleEntry "Forecasting" ]
        []
        [
            CompositionManifest.dataTypeEntry "SalesData"
            CompositionManifest.dataTypeEntry "ForecastData"
        ] [] []

let private referenceRefs =
    withGraph {
        QueryHandlerKeys = [
            queryKey "Sales" "latest"
            queryKey "Sales" "history"
            // The same QueryKey under a DIFFERENT module is a different
            // bus key — legal by construction, and the rule must not
            // mistake it for a collision.
            queryKey "Forecasting" "latest"
        ]
        DataTypeNames = [ dataTypeKey "Sales" "SalesData"; dataTypeKey "Forecasting" "ForecastData" ]
        DataNeeds = [
            {
                NeedingModule = "Forecasting"
                NeedField = "VectorisationHandlers"
                NeededDataType = "SalesData"
            }
        ]
        ExpectedModules = None
    }

let private codesFor (refs: CompositionReferences) (manifest: CompositionManifest) : string list =
    CompositionValidator.checkWith refs manifest
    |> List.map _.RuleCode
    |> List.distinct

/// A hand-built `ErasedModule` carrying only what the parity check reads
/// (`Definition.Id`). `Icon` / `View` are Fable-only render machinery this
/// test path never invokes — same construction shape as
/// `ModuleGroupingValidatorTests`.
let private erased (id: string) : ErasedModule = {
    Definition = {
        Id = id
        Name = id
        Icon = Unchecked.defaultof<ReactElement>
        Pages = []
    }
    Init = fun _ -> box (), Cmd.none
    Update = fun _ state -> state, Cmd.none
    View = None
    PageViews = None
    NeedsData = None
    DataTypes = []
    ProvidesProcessedData = None
    ProvidesNarrative = None
    Config = None
    FeatureFlags = []
    Availability = Always
    Group = Some "Work"
    // Phase 611 — declares no rail slot, i.e. ordinary group bucketing.
    Placement = None
    NavRole = None
    Area = ModuleArea.Product
    ClientQueryHandlers = []
    ActionDecoder = None
    Visibility = Visibility.visibleToAll
    EventSubscriptions = Map.empty
}

let tests =
    testList "Phase 583 — module-graph composition rules" [

        // ── the reference composition is silent ──────────────────────
        testCase "a well-formed module graph triggers no rule"
        <| fun _ ->
            Expect.isEmpty
                (codesFor referenceRefs referenceManifest)
                "a well-formed reference composition yields no defects at all"

        testCase "the empty reference set triggers no module-graph rule"
        <| fun _ ->
            Expect.isEmpty
                (codesFor CompositionReferences.empty CompositionManifest.empty)
                "the base case (nothing registered, nothing declared) is silent"

        // ── duplicate-query-handler-key ──────────────────────────────
        testCase "the same (module, QueryKey) registered twice fires the bus-key rule"
        <| fun _ ->
            let refs =
                withGraph {
                    ModuleGraphReferences.empty with
                        QueryHandlerKeys = [ queryKey "Sales" "latest"; queryKey "Sales" "latest" ]
                }

            let defects =
                CompositionValidator.checkWith refs (CompositionManifest.build [ moduleEntry "Sales" ] [] [] [] [])

            Expect.contains
                (defects |> List.map _.RuleCode)
                "duplicate-query-handler-key"
                "a shadowed bus key is a defect"

            let message = defects |> List.head |> _.Message
            Expect.stringContains message "'latest'" "the message names the colliding key"
            Expect.stringContains message "Sales" "the message names the declaring module"

        testCase "two modules sharing a registration Name merge their query namespaces"
        <| fun _ ->
            // Distinct ComponentIds (so `duplicate-component-id` has
            // nothing to say) but one shared Name — the registry groups by
            // Name, so by the time it looks the two buckets are already one.
            let manifest =
                CompositionManifest.build
                    [
                        CompositionManifest.moduleEntry ("Reports", ComponentId.ofModule "reports-a")
                        CompositionManifest.moduleEntry ("Reports", ComponentId.ofModule "reports-b")
                    ]
                    []
                    [] [] []

            let refs =
                withGraph {
                    ModuleGraphReferences.empty with
                        QueryHandlerKeys = [ queryKey "Reports" "latest"; queryKey "Reports" "history" ]
                }

            let codes = codesFor refs manifest

            Expect.contains codes "duplicate-query-handler-key" "a merged bus namespace is a defect"

            Expect.isFalse
                (codes |> List.contains "duplicate-component-id")
                "the ids are distinct — this is the case the identity rule cannot see"

        testCase "a shared module Name with no query handlers does not fire the bus-key rule"
        <| fun _ ->
            let manifest =
                CompositionManifest.build
                    [
                        CompositionManifest.moduleEntry ("Reports", ComponentId.ofModule "reports-a")
                        CompositionManifest.moduleEntry ("Reports", ComponentId.ofModule "reports-b")
                    ]
                    []
                    [] [] []

            Expect.isFalse
                (codesFor CompositionReferences.empty manifest
                 |> List.contains "duplicate-query-handler-key")
                "the rule is about the bus namespace — no handlers, nothing merged"

        // ── duplicate-datatype-typename ──────────────────────────────
        testCase "two modules registering one wire TypeName fires the datatype rule"
        <| fun _ ->
            // The manifest carries ONE entry (the projector distincts the
            // ComponentIds), which is exactly why the rule reads the
            // pre-collapse registrations instead.
            let manifest =
                CompositionManifest.build
                    [ moduleEntry "Sales"; moduleEntry "Forecasting" ]
                    []
                    [ CompositionManifest.dataTypeEntry "SalesData" ] [] []

            let refs =
                withGraph {
                    ModuleGraphReferences.empty with
                        DataTypeNames = [ dataTypeKey "Sales" "SalesData"; dataTypeKey "Forecasting" "SalesData" ]
                }

            let defects = CompositionValidator.checkWith refs manifest

            Expect.contains
                (defects |> List.map _.RuleCode)
                "duplicate-datatype-typename"
                "a shared wire TypeName across distinct registrations is a defect"

            let message =
                defects
                |> List.find (fun d -> d.RuleCode = "duplicate-datatype-typename")
                |> _.Message

            Expect.stringContains message "'SalesData'" "the message names the colliding TypeName"
            Expect.stringContains message "'Forecasting'" "the message names both declaring modules"

            Expect.isFalse
                (defects |> List.exists (fun d -> d.RuleCode = "duplicate-component-id"))
                "the manifest collapsed the collision — the identity rule cannot see it"

        testCase "the datatype rule is an error, and the needs rule a warning"
        <| fun _ ->
            let severityOf code =
                CompositionValidator.ruleManifest
                |> List.tryFind (fun r -> r.Code = code)
                |> Option.map _.Severity

            Expect.equal (severityOf "duplicate-datatype-typename") (Some DefectError) "duplicates abort startup"
            Expect.equal (severityOf "duplicate-query-handler-key") (Some DefectError) "duplicates abort startup"
            Expect.equal (severityOf "client-server-module-parity") (Some DefectError) "a declared parity breach aborts"

            Expect.equal
                (severityOf "unsatisfied-needs-data")
                (Some DefectWarning)
                "a partly-enumerable need must not be able to block a boot"

        // ── unsatisfied-needs-data ───────────────────────────────────
        testCase "a data need no module provides fires the needs rule"
        <| fun _ ->
            let refs =
                withGraph {
                    ModuleGraphReferences.empty with
                        DataNeeds = [
                            {
                                NeedingModule = "Search"
                                NeedField = "VectorisationHandlers"
                                NeededDataType = "GhostData"
                            }
                        ]
                }

            let defects = CompositionValidator.checkWith refs CompositionManifest.empty

            Expect.contains
                (defects |> List.map _.RuleCode)
                "unsatisfied-needs-data"
                "a need nothing provides is reported"

            let message = defects |> List.head |> _.Message
            Expect.stringContains message "'GhostData'" "the message names the missing data type"
            Expect.stringContains message "VectorisationHandlers" "the message names the declaring field"

        testCase "a need satisfied only by a pre-collapse registration is satisfied"
        <| fun _ ->
            // The provided set unions the manifest labels with the
            // registration edges, so a need is not falsely reported when
            // the producing registration exists but the manifest projection
            // has not been supplied.
            let refs =
                withGraph {
                    ModuleGraphReferences.empty with
                        DataTypeNames = [ dataTypeKey "Sales" "SalesData" ]
                        DataNeeds = [
                            {
                                NeedingModule = ""
                                NeedField = "VectorisationHandlers"
                                NeededDataType = "SalesData"
                            }
                        ]
                }

            Expect.isEmpty
                (codesFor refs CompositionManifest.empty)
                "a registered producer satisfies the need without a manifest entry"

        // ── client-server-module-parity ──────────────────────────────
        testCase "an undeclared expected-module list costs nothing"
        <| fun _ ->
            // GP 13, as a test: a composition with modules but no
            // declaration must never produce a parity defect, whatever it
            // composed.
            let manifest =
                CompositionManifest.build [ moduleEntry "A"; moduleEntry "B" ] [] [] [] []

            Expect.isFalse
                (codesFor CompositionReferences.empty manifest
                 |> List.contains "client-server-module-parity")
                "the rule is dormant until the consumer declares a list"

        testCase "a declared list that matches the composed set is silent"
        <| fun _ ->
            let manifest =
                CompositionManifest.build [ moduleEntry "A"; moduleEntry "B" ] [] [] [] []

            let refs =
                withGraph {
                    ModuleGraphReferences.empty with
                        ExpectedModules = Some [ "B"; "A" ]
                }

            Expect.isEmpty (codesFor refs manifest) "set equality, not list order"

        testCase "a declared list that mismatches names both directions"
        <| fun _ ->
            let manifest =
                CompositionManifest.build [ moduleEntry "A"; moduleEntry "Surprise" ] [] [] [] []

            let refs =
                withGraph {
                    ModuleGraphReferences.empty with
                        ExpectedModules = Some [ "A"; "Missing" ]
                }

            let defects = CompositionValidator.checkWith refs manifest

            Expect.contains
                (defects |> List.map _.RuleCode)
                "client-server-module-parity"
                "a mismatch against a declared list is a defect"

            let message = defects |> List.head |> _.Message
            Expect.stringContains message "'Missing'" "the message names what was declared but not composed"
            Expect.stringContains message "'Surprise'" "the message names what was composed but not declared"

        testCase "Some [] is a real declaration, distinct from None"
        <| fun _ ->
            let manifest = CompositionManifest.build [ moduleEntry "A" ] [] [] [] []

            let declaredEmpty =
                withGraph {
                    ModuleGraphReferences.empty with
                        ExpectedModules = Some []
                }

            Expect.contains
                (codesFor declaredEmpty manifest)
                "client-server-module-parity"
                "declaring an empty expected set asserts that nothing is composed"

        // ── the client half agrees with the server rule ──────────────
        testCase "ModuleParityValidator is dormant when undeclared"
        <| fun _ -> Expect.isOk (ModuleParityValidator.result None [ erased "A" ]) "no declaration, no check"

        testCase "ModuleParityValidator accepts a matching client module set"
        <| fun _ ->
            Expect.isOk
                (ModuleParityValidator.result (Some [ "B"; "A" ]) [ erased "A"; erased "B" ])
                "set equality on ModuleDefinition.Id, order-independent"

        testCase "ModuleParityValidator rejects the same mismatch the server rule rejects"
        <| fun _ ->
            // The two roots never see each other; they see the same
            // declared list. This is the test that they agree about it.
            let declared = [ "A"; "Missing" ]

            let serverCodes =
                codesFor
                    (withGraph {
                        ModuleGraphReferences.empty with
                            ExpectedModules = Some declared
                    })
                    (CompositionManifest.build [ moduleEntry "A"; moduleEntry "Surprise" ] [] [] [] [])

            Expect.contains serverCodes "client-server-module-parity" "the server root refuses the mismatch"

            match ModuleParityValidator.result (Some declared) [ erased "A"; erased "Surprise" ] with
            | Ok() -> failtest "the client root must refuse the same mismatch"
            | Error message ->
                Expect.stringContains message "'Missing'" "declared but not composed"
                Expect.stringContains message "'Surprise'" "composed but not declared"

        testCase "ModuleParityValidator.validate throws on a mismatch and passes a match"
        <| fun _ ->
            Expect.throws
                (fun () -> ModuleParityValidator.validate (Some [ "Expected" ]) [ erased "Actual" ])
                "a declared mismatch is a loud boot failure"

            ModuleParityValidator.validate (Some [ "Actual" ]) [ erased "Actual" ]
            ModuleParityValidator.validate None [ erased "Anything" ]
    ]