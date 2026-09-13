// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AIQueryabilityOptInTests

// ─── Phase 36.C — per-module AI-queryability opt-in ──────────────────
//
// Phase 36.B gave an LLM six tools that roam across module data, gated
// by the caller's RBAC; Phase 730 added the grant/consent gate on top.
// Both gates are about the CALLER. Neither asks the question a module
// author has: may this module's data be reached by the cross-module AI
// surface AT ALL. Installing a third-party module should not make its
// data AI-readable as a side effect of installing it.
//
// This pack proves the third gate, and proves the three things about it
// that are easy to get wrong:
//
//   1. **It is default OFF.** Not "off when a registry says so" — off
//      when nothing is declared, which is every deployment that has not
//      adopted this phase. `moduleAIQueryGate` on a context with no
//      registry is a constant `false`, and the tools behave accordingly.
//      This is the one place the SDK's usual GP 11 posture is inverted
//      deliberately, so it is asserted rather than assumed.
//
//   2. **`UnqueryableModule` is distinct from `PermissionDenied`, and is
//      reported AFTER it.** Distinct, because no grant fixes it and a
//      model told "denied" would keep re-planning around a door that
//      cannot open. After, because a caller who may not read the module
//      must learn nothing about which modules the deployment exposed.
//      Both halves are asserted: the distinction, and the ordering.
//
//   3. **The declaration narrows only.** A composition root may revoke a
//      module author's opt-in; it may not confer one the author declined.
//
// **Non-vacuity.** Every tool case asserts the negative and the positive
// against the SAME context shape, differing only in whether the module
// opted in — so a gate that refused everything, or nothing, fails one
// half rather than passing both. Where a positive half asserts a
// DOWNSTREAM error (`ResultStoreUnavailable` / `EntityStoreUnavailable`),
// reaching it is the proof that the gate let the call through — without
// this pack having to stand up a store it is not testing.

open System
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open DataManagementTypes

// ─── Fixtures ────────────────────────────────────────────────────────

let private Read = ModulePermission.Read

/// A catalogue over `(moduleName, typeId)` pairs — enough for the two
/// enumeration tools and for `query_entity`'s producer attribution.
/// Every other member is unreachable from the tools under test and says
/// so by throwing rather than returning a plausible empty value.
type private StubCatalog(registrations: (string * string) list) =
    interface IDataCatalog with
        member _.ListTypes() = async {
            return
                registrations
                |> List.map snd
                |> List.distinct
                |> List.map (fun id -> {
                    Id = id
                    DisplayName = id
                    Schema = None
                })
        }

        member _.GetSchema _ = async { return None }

        member _.GetProducers typeId = async {
            return
                registrations
                |> List.filter (fun (_, id) -> id = typeId)
                |> List.map fst
                |> List.distinct
        }

        member _.ListObjects(_, _) = async { return [] }
        member _.CountObjects(_, _) = async { return 0 }

        member _.GetSyntheticSample(_, _, _) =
            failwith "StubCatalog: GetSyntheticSample is not reachable from the _platform.ai.* tools"

/// The real in-process bus's RBAC behaviour, and nothing else: it
/// answers `PermissionDenied` exactly where the shipped one does, and
/// `Ok` otherwise. That is the whole of what the ordering assertion below
/// needs — and the reason a stub is needed at all is itself the point.
/// With NO bus registered, `query_module` short-circuits on
/// `ModuleQueryBusUnavailable` before either gate speaks, so a test
/// asserting the ordering against an unregistered bus would have proved
/// nothing about the ordering.
type private StubQueryBus() =
    interface IModuleQueryBus with
        member _.Ask(context, request) = async {
            if not (AccessContext.hasPermission request.TargetModule ModulePermission.Read context) then
                return Some(Error(PermissionDenied request.TargetModule))
            else
                return Some(Ok { Payload = """{"ok":true}""" })
        }

/// `exposures = None` is the shape of every deployment that has not
/// adopted this phase: no `ModuleAIExposureRegistry` in DI at all.
let private buildContext
    (exposures: ModuleAIExposureRegistry option)
    (perms: (string * ModulePermission list) list)
    (registrations: (string * string) list)
    =
    let services = ServiceCollection()

    match exposures with
    | Some r -> services.AddSingleton<ModuleAIExposureRegistry>(r) |> ignore
    | None -> ()

    services.AddSingleton<IDataCatalog>(StubCatalog registrations :> IDataCatalog)
    |> ignore

    services.AddSingleton<IModuleQueryBus>(StubQueryBus() :> IModuleQueryBus)
    |> ignore

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Items["ToolUp.UserId"] <- box "alice"

    ctx.Items["ToolUp.StorageScope"] <-
        box {
            ScopeId = "t1"
            Container = "team-t1"
            Persist = true
        }

    if not (List.isEmpty perms) then
        ctx.Items["ToolUp.ModulePermissions"] <- box (Map.ofList perms)

    ctx :> HttpContext

let private queryableOf (names: string list) =
    ModuleAIExposureRegistry.ofDeclarations (names |> List.map (fun n -> n, ModuleAIExposure.Queryable))

let private toolNamed (name: string) =
    PlatformAITools.builtIn
    |> List.tryFind (fun t -> t.Definition.Name = name)
    |> Option.defaultWith (fun () -> failwithf "no built-in tool named '%s'" name)

let private run (name: string) (ctx: HttpContext) (args: string) =
    (toolNamed name).Execute ctx args |> Async.RunSynchronously

/// The `error` discriminator, or `None` on a success payload. Reading
/// the field rather than substring-matching the JSON: a message that
/// happens to mention `UnqueryableModule` must not read as one.
let private errorOf (json: string) : string option =
    use doc = JsonDocument.Parse json

    match doc.RootElement.TryGetProperty "error" with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

/// `[ moduleName, queryable ]` from a `list_accessible_modules` payload.
let private queryableFlags (json: string) : (string * bool) list =
    use doc = JsonDocument.Parse json

    doc.RootElement.GetProperty("modules").EnumerateArray()
    |> Seq.map (fun m -> m.GetProperty("moduleName").GetString(), m.GetProperty("queryable").GetBoolean())
    |> Seq.sortBy fst
    |> List.ofSeq

let private dataTypeIds (json: string) : string list =
    use doc = JsonDocument.Parse json

    doc.RootElement.GetProperty("dataTypes").EnumerateArray()
    |> Seq.map (fun t -> t.GetProperty("id").GetString())
    |> Seq.sort
    |> List.ofSeq

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 36.C — per-module AI-queryability opt-in" [

        testList "the declaration itself" [

            testCase "a fresh module has declared nothing, which reads as not queryable"
            <| fun _ ->
                let m = ServerModule.create "MoodJournal"
                Expect.isNone m.AIExposure "no declaration until the author makes one"

                Expect.isFalse
                    (ModuleAIExposureRegistry.isQueryable (ModuleAIExposureRegistry.ofDeclarations []) "MoodJournal")
                    "and an undeclared module is not queryable"

            testCase "withAIExposure Queryable opts the module in"
            <| fun _ ->
                let m =
                    ServerModule.create "MoodJournal"
                    |> ServerModule.withAIExposure ModuleAIExposure.Queryable

                Expect.equal m.AIExposure (Some ModuleAIExposure.Queryable) "the author's declaration stands"

            testCase "composition may REVOKE an opt-in"
            <| fun _ ->
                let m =
                    ServerModule.create "MoodJournal"
                    |> ServerModule.withAIExposure ModuleAIExposure.Queryable
                    |> ServerModule.withAIExposure ModuleAIExposure.NotQueryable

                Expect.equal
                    m.AIExposure
                    (Some ModuleAIExposure.NotQueryable)
                    "narrowing is the direction that is always allowed"

            testCase "composition may NOT confer an opt-in the author declined"
            <| fun _ ->
                // The half that makes the narrowing rule worth having. A
                // deployment silently widening a module's AI exposure is
                // exactly the accidental exposure this phase prevents, and
                // a composition root is the last place it is free to catch.
                Expect.throws
                    (fun () ->
                        ServerModule.create "Payroll"
                        |> ServerModule.withAIExposure ModuleAIExposure.NotQueryable
                        |> ServerModule.withAIExposure ModuleAIExposure.Queryable
                        |> ignore)
                    "widening an explicit NotQueryable must fail at compose"

            testCase "the registry holds only the modules that opted IN"
            <| fun _ ->
                let registry =
                    ModuleAIExposureRegistry.ofDeclarations [
                        "MoodJournal", ModuleAIExposure.Queryable
                        "Payroll", ModuleAIExposure.NotQueryable
                    ]

                Expect.isTrue (ModuleAIExposureRegistry.isQueryable registry "MoodJournal") "declared in"
                Expect.isFalse (ModuleAIExposureRegistry.isQueryable registry "Payroll") "declared out"
                Expect.isFalse (ModuleAIExposureRegistry.isQueryable registry "Unknown") "never mentioned"

            testCase "an all-default deployment projects the empty registry"
            <| fun _ ->
                Expect.isTrue
                    (ModuleAIExposureRegistry.isEmpty (
                        ModuleAIExposureRegistry.ofDeclarations [ "Payroll", ModuleAIExposure.NotQueryable ]
                    ))
                    "a declared NotQueryable is the default and composes nothing"

            testCase "orphans names a declaration keyed to no composed module"
            <| fun _ ->
                // The silent failure this guards: a module the author opted
                // IN would simply never appear to the model, with no
                // refusal, no audit row, and nothing to grep for.
                Expect.equal
                    (ModuleAIExposureRegistry.orphans (queryableOf [ "MoodJournal"; "Typo" ]) [ "MoodJournal" ])
                    [ "Typo" ]
                    "the name outside the composed set is named"

            testCase "ModuleSurface classifies AIExposure, and reports it only when declared"
            <| fun _ ->
                // The `ServerModule` drift guard fails on any registration
                // field the descriptor does not classify — deliberately, so
                // a new field gets classified by a decision rather than by
                // omission. It caught this one: the first full gate on this
                // phase went red here, which is the guard working.
                //
                // `AIExposure` is `Provides` on the `GrantPolicy`
                // precedent (a module-declared access posture a composition
                // reads off the registration, implying no substrate), and
                // its ENTRY is conditional on the `BindingStamp` one,
                // because `None` is this field's "declares nothing".
                let plain = ServerModule.create "Plain"
                let plainSurface = ModuleSurface.describe plain

                match
                    plainSurface.Coverage
                    |> List.filter (fun c -> c.Origin = "server" && c.Field = nameof plain.AIExposure)
                with
                | [ c ] -> Expect.equal c.Facet ProvidesFacet "classified as a declaration the module offers"
                | other -> failtestf "expected exactly one AIExposure coverage row, got %A" other

                Expect.isEmpty plainSurface.Unclassified "the field is classified, so nothing drifts"

                Expect.isEmpty
                    (plainSurface.Provides |> List.filter (fun e -> e.Kind = "ai-exposure"))
                    "an undeclared module reports no ai-exposure entry — byte-identical to pre-36.C"

                let declared =
                    ServerModule.create "MoodJournal"
                    |> ServerModule.withAIExposure ModuleAIExposure.Queryable

                match
                    (ModuleSurface.describe declared).Provides
                    |> List.filter (fun e -> e.Kind = "ai-exposure")
                with
                | [ e ] ->
                    Expect.equal e.Field (nameof declared.AIExposure) "attributed to the registration field"
                    Expect.equal e.Key (ModuleAIExposure.toToken ModuleAIExposure.Queryable) "the wire token is the key"
                | other -> failtestf "expected exactly one ai-exposure entry, got %A" other

                // The declaration is reported, not the effective value: a
                // module that explicitly declined is as much a declaration
                // as one that opted in, and the surface should say so.
                match
                    (ModuleSurface.describe (
                        ServerModule.create "Payroll"
                        |> ServerModule.withAIExposure ModuleAIExposure.NotQueryable
                    ))
                        .Provides
                    |> List.filter (fun e -> e.Kind = "ai-exposure")
                with
                | [ e ] ->
                    Expect.equal
                        e.Key
                        (ModuleAIExposure.toToken ModuleAIExposure.NotQueryable)
                        "an explicit refusal is a declaration and is surfaced as one"
                | other -> failtestf "expected exactly one ai-exposure entry, got %A" other

            testCase "the exposure token round-trips, and an unknown token fails CLOSED"
            <| fun _ ->
                for exposure in [ ModuleAIExposure.NotQueryable; ModuleAIExposure.Queryable ] do
                    Expect.equal (ModuleAIExposure.ofToken (ModuleAIExposure.toToken exposure)) exposure "round-trip"

                for token in [ ""; "QUERYABLE?"; "yes"; "true"; null ] do
                    Expect.equal
                        (ModuleAIExposure.ofToken token)
                        ModuleAIExposure.NotQueryable
                        $"a token this node cannot interpret must not be the one that exposes data: '{token}'"
        ]

        testList "the gate" [

            testCase "a deployment that declared nothing exposes NOTHING (default off)"
            <| fun _ ->
                // The inversion of the usual GP 11 posture, asserted rather
                // than assumed: no registry in DI is the shape of every
                // deployment predating this phase, and it reads as "nobody
                // opted in", never as "everything is fine".
                let gate = moduleAIQueryGate (buildContext None [ "MoodJournal", [ Read ] ] [])
                Expect.isFalse (gate "MoodJournal") "absent registry ⇒ not queryable"

            testCase "a declared deployment admits exactly what opted in"
            <| fun _ ->
                let ctx =
                    buildContext (Some(queryableOf [ "MoodJournal" ])) [ "MoodJournal", [ Read ]; "Payroll", [ Read ] ] []

                let gate = moduleAIQueryGate ctx
                Expect.isTrue (gate "MoodJournal") "opted in"
                Expect.isFalse (gate "Payroll") "did not opt in"
        ]

        testList "list_accessible_modules ANNOTATES rather than filters" [

            testCase "a non-queryable module is still named, flagged queryable: false"
            <| fun _ ->
                // Deliberately the one tool of the six that does not hide the
                // module: the model can then route the user to the module's
                // own UI instead of inferring the module is absent.
                let ctx =
                    buildContext (Some(queryableOf [ "MoodJournal" ])) [ "MoodJournal", [ Read ]; "Payroll", [ Read ] ] []

                Expect.equal
                    (queryableFlags (run "_platform.ai.list_accessible_modules" ctx "{}"))
                    [ "MoodJournal", true; "Payroll", false ]
                    "both listed; only the opted-in one is queryable"

            testCase "with nothing declared, every module reports queryable: false"
            <| fun _ ->
                let ctx = buildContext None [ "MoodJournal", [ Read ]; "Payroll", [ Read ] ] []

                Expect.equal
                    (queryableFlags (run "_platform.ai.list_accessible_modules" ctx "{}"))
                    [ "MoodJournal", false; "Payroll", false ]
                    "the pre-adoption deployment: visible, none reachable"
        ]

        testList "list_data_types FILTERS by producer opt-in" [

            testCase "a type whose only producer is not queryable is omitted"
            <| fun _ ->
                let ctx =
                    buildContext (Some(queryableOf [ "MoodJournal" ])) [ "MoodJournal", [ Read ]; "Payroll", [ Read ] ] [
                        "MoodJournal", "Mood"
                        "Payroll", "Salary"
                    ]

                Expect.equal
                    (dataTypeIds (run "_platform.ai.list_data_types" ctx "{}"))
                    [ "Mood" ]
                    "Salary's only producer stayed off the AI surface"

            testCase "a type with one queryable producer among several IS listed"
            <| fun _ ->
                // The intersection has to be per-PRODUCER, not per-type: a
                // shared cross-domain shape is reachable through whichever
                // producer opted in, and hiding it because a sibling did not
                // would be over-refusal.
                let ctx =
                    buildContext (Some(queryableOf [ "MoodJournal" ])) [ "MoodJournal", [ Read ]; "Payroll", [ Read ] ] [
                        "MoodJournal", "Shared"
                        "Payroll", "Shared"
                    ]

                Expect.equal (dataTypeIds (run "_platform.ai.list_data_types" ctx "{}")) [ "Shared" ] "still reachable"

            testCase "with nothing declared, no data type is listed at all"
            <| fun _ ->
                let ctx = buildContext None [ "MoodJournal", [ Read ] ] [ "MoodJournal", "Mood" ]

                Expect.isEmpty
                    (dataTypeIds (run "_platform.ai.list_data_types" ctx "{}"))
                    "default off reaches the enumeration too"
        ]

        testList "the four reach tools refuse with UnqueryableModule" [

            testCase "query_module refuses a module that did not opt in"
            <| fun _ ->
                let ctx =
                    buildContext (Some(queryableOf [ "MoodJournal" ])) [ "Payroll", [ Read ] ] []

                Expect.equal
                    (errorOf (run "_platform.ai.query_module" ctx """{"moduleName":"Payroll","queryKey":"summary"}"""))
                    (Some "UnqueryableModule")
                    "refused before the bus, so the module's handler never ran"

            testCase "query_module lets an opted-in module through to the bus"
            <| fun _ ->
                // The positive half, against the same context shape. A gate
                // that refused everything would pass the case above and fail
                // this one.
                let ctx =
                    buildContext (Some(queryableOf [ "MoodJournal" ])) [ "MoodJournal", [ Read ] ] []

                Expect.isNone
                    (errorOf (
                        run "_platform.ai.query_module" ctx """{"moduleName":"MoodJournal","queryKey":"summary"}"""
                    ))
                    "past the opt-in gate and dispatched — the bus answered"

            testCase "PermissionDenied still wins over UnqueryableModule"
            <| fun _ ->
                // The ordering half. A caller who may not read the module
                // must not learn from the refusal which modules the
                // deployment put on the AI surface — so the caller-side
                // answer is given first, and it is the pre-36.C answer.
                let ctx =
                    buildContext (Some(queryableOf [ "MoodJournal" ])) [ "MoodJournal", [ Read ] ] []

                Expect.equal
                    (errorOf (run "_platform.ai.query_module" ctx """{"moduleName":"Payroll","queryKey":"summary"}"""))
                    (Some "PermissionDenied")
                    "no permission on Payroll ⇒ the permission answer, not the exposure answer"

            testCase "list_results refuses a module that did not opt in, and admits one that did"
            <| fun _ ->
                let ctx =
                    buildContext (Some(queryableOf [ "MoodJournal" ])) [ "MoodJournal", [ Read ]; "Payroll", [ Read ] ] []

                Expect.equal
                    (errorOf (run "_platform.ai.list_results" ctx """{"moduleName":"Payroll"}"""))
                    (Some "UnqueryableModule")
                    "the negative"

                Expect.equal
                    (errorOf (run "_platform.ai.list_results" ctx """{"moduleName":"MoodJournal"}"""))
                    (Some "ResultStoreUnavailable")
                    "the positive — past the gate, refused by the absent store instead"

            testCase "get_latest_result refuses a module that did not opt in, and admits one that did"
            <| fun _ ->
                let ctx =
                    buildContext (Some(queryableOf [ "MoodJournal" ])) [ "MoodJournal", [ Read ]; "Payroll", [ Read ] ] []

                Expect.equal
                    (errorOf (
                        run "_platform.ai.get_latest_result" ctx """{"moduleName":"Payroll","resultType":"summary"}"""
                    ))
                    (Some "UnqueryableModule")
                    "the negative"

                Expect.equal
                    (errorOf (
                        run
                            "_platform.ai.get_latest_result"
                            ctx
                            """{"moduleName":"MoodJournal","resultType":"summary"}"""
                    ))
                    (Some "ResultStoreUnavailable")
                    "the positive"

            testCase "query_entity refuses a type whose only producer did not opt in"
            <| fun _ ->
                let ctx =
                    buildContext (Some(queryableOf [ "MoodJournal" ])) [ "MoodJournal", [ Read ]; "Payroll", [ Read ] ] [
                        "MoodJournal", "Mood"
                        "Payroll", "Salary"
                    ]

                Expect.equal
                    (errorOf (run "_platform.ai.query_entity" ctx """{"entityType":"Salary"}"""))
                    (Some "UnqueryableModule")
                    "attributed to Payroll, which stayed off the surface"

                Expect.equal
                    (errorOf (run "_platform.ai.query_entity" ctx """{"entityType":"Mood"}"""))
                    (Some "EntityStoreUnavailable")
                    "the positive — past the gate, refused by the absent entity store"

            testCase "query_entity passes an entity type the catalogue cannot attribute"
            <| fun _ ->
                // The documented limit of the module-level grain:
                // `EntityRegistration` carries no module attribution
                // (entities are registered app-level by
                // `ServerApp.withEntity`), so an entity type with no
                // catalogued producer has no module to gate on. Refusing it
                // would block an opted-IN module's own entities whenever it
                // does not also declare a `DataType`, with a remedy
                // unrelated to the opt-in — so it is passed through, and the
                // gap is recorded rather than papered over.
                let ctx =
                    buildContext (Some(queryableOf [ "MoodJournal" ])) [ "MoodJournal", [ Read ] ] []

                Expect.equal
                    (errorOf (run "_platform.ai.query_entity" ctx """{"entityType":"Untracked"}"""))
                    (Some "EntityStoreUnavailable")
                    "no producer to attribute ⇒ this gate has nothing to say"
        ]
    ]