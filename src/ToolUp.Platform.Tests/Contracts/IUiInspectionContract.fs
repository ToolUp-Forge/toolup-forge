// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IUiInspectionContract

// ─── Phase 540 — the read-only UI-inspection conformance pack ────────
//
// A reusable Expecto list any provider of a live-interface inspect tool
// runs against itself: the forge-native `_platform.ui.inspect_active_module`
// (Phase 537), an external host adapter, or a third party. Where
// `IClientToolDispatchContract` pins the DISPATCH round trip for any
// client-resident tool, this pack pins the INSPECTION semantics of one:
//
//   (0) Declaration — client-resident, flagged as a live-interface tool,
//       publishes no client action, declares its effects, and every
//       declared class is read-only, so the Phase 793 read-only ceiling
//       admits it.
//   (1) An observable active module answers `{ moduleId, activePage,
//       snapshot }`, the snapshot carrying the module's fields (as JSON
//       values, not strings of JSON), selections and action states.
//   (2) The module inspected is the request's active module: another
//       module's report never leaks, and a module the model names in its
//       arguments is not read instead.
//   (3) `no-active-module` when the request carries no (or a blank)
//       active module.
//   (4) `no-observable-state` when the active module has published no
//       report.
//   (5) Read-only — invoking it changes no module state, and answers
//       identically when asked twice over unchanged state.
//   (6) The `IClientToolAuthorizer` gate is honoured — a denying
//       authorizer stops the call before the executor runs, and the denial
//       is audited.
//   (7) The tool is offered to the model on exactly the surfaces its
//       declared `AISurfaceFilter` names.
//
// Every behavioural case drives the REAL agent loop through the shared
// harness in `IClientToolDispatchContract` (`runClientToolLoop`): the
// fixture's tool is registered, a scripted provider calls it by its
// provider name as a model would, and the fixture's executor answers the
// `ClientToolInvoke` event from a module-state table the pack owns. The
// pack therefore asserts what the model is shown, not what a unit test
// of the executor would return.
//
// The status vocabulary is the seam's, read from
// `ToolUp.AI.UiAwareness.InspectStatus` — the single home both tiers of the
// forge-native companion read, so the pack and every provider name the same
// strings.

open System
open System.Collections.Generic
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI
open ToolUp.AI.UiAwareness

/// Reads the latest `UiStateReport` a module published, by module id —
/// the shape of `ModuleStateObserver.tryInspect`.
type InspectReader = string -> UiStateReport option

/// One invocation, as the browser receives it on the `ClientToolInvoke`
/// event: the request's active module and page, and the model's arguments.
type InspectInvocation = {
    ActiveModule: string option
    ActivePage: string option
    ArgsJson: string
}

/// Everything the pack needs to validate one inspect-tool provider.
type UiInspectionContractFixture = {
    /// Human label, suffixed onto the test-list name.
    Name: string
    /// The tool as the provider registers it: definition + server
    /// executor, exactly what the agent loop's registry holds.
    Tool: AIToolRegistry.RegisteredTool
    /// The provider's executor for a client-resident call, given the
    /// pack's module-state reader. It answers the `ClientToolInvoke`
    /// event; its result is what the loop hands the model.
    ClientExecutor: InspectReader -> InspectInvocation -> Async<string>
}

// ─── Fixture state ───────────────────────────────────────────────────

let private salesModule = "Sales"
let private otherModule = "Other"
let private salesPage = "/sales/pipeline"

/// The observable module's report: two fields (a string and a number,
/// each a JSON-encoded leaf), one selection and two action states.
///
/// Built as record literals rather than from `UiStateReport.empty`: that is
/// a module VALUE in the client tier's `SDK.ClientTypes.fs`, and touching it
/// on .NET runs that file's static initialiser, which reaches Fable-only
/// binding code and throws. The record type itself is inert.
let private salesReport: UiStateReport = {
    Fields = Map.ofList [ "region", "\"EMEA\""; "threshold", "42" ]
    Selections = Map.ofList [ "accounts", [ "acct-1"; "acct-2" ] ]
    Actions = Map.ofList [ "export", true; "delete", false ]
}

/// A second module's report, carrying a marker no answer about `Sales`
/// may contain.
let private otherMarker = "other-module-marker"

let private otherReport: UiStateReport = {
    Fields = Map.ofList [ "secret", $"\"{otherMarker}\"" ]
    Selections = Map.empty
    Actions = Map.empty
}

/// The module-state table one test owns. `Reader` is the only access the
/// provider is given.
type private StateTable(seed: (string * UiStateReport) list) =
    let table = Dictionary<string, UiStateReport>()

    do
        for moduleId, report in seed do
            table[moduleId] <- report

    member _.Reader: InspectReader =
        fun moduleId ->
            lock table (fun () ->
                match table.TryGetValue moduleId with
                | true, report -> Some report
                | _ -> None)

    member _.Snapshot() : Map<string, UiStateReport> =
        lock table (fun () -> table |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)

// ─── Authorizers the pack owns ───────────────────────────────────────

type private AllowAll() =
    interface IClientToolAuthorizer with
        member _.Authorize(_toolName, _argsJson, _activeModule, _activePage) = Allow

type private DenyAll() =
    interface IClientToolAuthorizer with
        member _.Authorize(toolName, _argsJson, _activeModule, _activePage) =
            Deny $"ui-inspection contract policy denied '{toolName}'"

// ─── Driving the loop ────────────────────────────────────────────────

/// The surfaces the declared filter offers the tool on, stated from the
/// filter's cases rather than through the helper the loop itself uses, so
/// a regression in that helper cannot agree with itself.
let private surfacesOf (filter: AISurfaceFilter) : AISurface list =
    match filter with
    | Both -> [ SidePanel; FullPage ]
    | SidePanelOnly -> [ SidePanel ]
    | FullPageOnly -> [ FullPage ]

/// A surface the tool is offered on — the behavioural cases run there.
let private visibleSurface (f: UiInspectionContractFixture) : AISurface =
    match surfacesOf f.Tool.Definition.Surface with
    | surface :: _ -> surface
    | [] -> failtestf "%s declares no surface it is offered on" f.Tool.Definition.Name

type private InspectRun = {
    Run: IClientToolDispatchContract.ClientToolLoopRun
    /// The executor's invocations, in order.
    Invocations: InspectInvocation list
    /// The results the model received for the tool.
    Results: string list
}

/// One loop run in which the model calls the tool once, with `argsJson`.
let private inspect
    (f: UiInspectionContractFixture)
    (authorizer: IClientToolAuthorizer)
    (table: StateTable)
    (activeModule: string option)
    (activePage: string option)
    (argsJson: string)
    : Async<InspectRun> =
    async {
        let invocations = ResizeArray<InspectInvocation>()

        let simulator (evt: AIStreamEvent) : string option =
            match evt with
            | ClientToolInvoke(_, _, _, args, module', page) ->
                let invocation = {
                    ActiveModule = module'
                    ActivePage = page
                    ArgsJson = args
                }

                lock invocations (fun () -> invocations.Add invocation)
                Some(f.ClientExecutor table.Reader invocation |> Async.RunSynchronously)
            | _ -> None

        let script = [
            IClientToolDispatchContract.toolUseResponse [
                IClientToolDispatchContract.mkToolCallWith f.Tool.ProviderDef.Name argsJson
            ]
            IClientToolDispatchContract.endTurnResponse
        ]

        let! run =
            IClientToolDispatchContract.runClientToolLoop
                {
                    Tools = [ f.Tool ]
                    Authorizer = authorizer
                    Surface = visibleSurface f
                    ActiveModule = activeModule
                    ActivePage = activePage
                }
                script
                simulator

        let results =
            run.Events
            |> List.choose (function
                | ToolCallCompleted(_, _, content) -> Some content
                | _ -> None)

        return {
            Run = run
            Invocations = lock invocations (fun () -> List.ofSeq invocations)
            Results = results
        }
    }

/// The single result of a run that called the tool once, parsed.
let private singleResult (r: InspectRun) : JsonElement =
    match r.Results with
    | [ content ] ->
        try
            (JsonDocument.Parse content).RootElement
        with ex ->
            failtestf "the tool's result must be a JSON object; got %s (%s)" content ex.Message
    | other -> failtestf "expected exactly one tool result, got %d: %A" (List.length other) other

let private tryProp (name: string) (e: JsonElement) : JsonElement option =
    match e.ValueKind with
    | JsonValueKind.Object ->
        match e.TryGetProperty name with
        | true, v -> Some v
        | _ -> None
    | _ -> None

let private prop (name: string) (e: JsonElement) : JsonElement =
    match tryProp name e with
    | Some v -> v
    | None -> failtestf "the result must carry '%s'; got %s" name (e.GetRawText())

let private expectStatus (expected: string) (result: JsonElement) =
    Expect.equal ((prop "status" result).GetString()) expected $"status must be '{expected}'"
    Expect.isNone (tryProp "snapshot" result) "a status answer carries no snapshot"

// ─── Tests ───────────────────────────────────────────────────────────

let tests (f: UiInspectionContractFixture) : Test =
    let def = f.Tool.Definition

    testList $"IUiInspection contract — {f.Name}" [

        testList "(0) declaration" [
            test "client-resident: the body runs where the module state lives" {
                Expect.equal def.Location ClientResident "an inspect tool reads the browser's module state"
            }

            test "flagged as a live-interface tool" {
                Expect.isTrue def.IsLiveInterface "the Phase 14r framing keys off IsLiveInterface"
            }

            test "publishes no client action" {
                Expect.isNone def.EmitsActions "a read-only inspect tool must not publish client actions"
            }

            test "declares its effects, and every declared class is read-only" {
                let classes = ToolEffectDeclaration.classes def.Effects

                Expect.isNonEmpty classes "an undeclared tool is one the read-only ceiling cannot measure"

                Expect.isTrue
                    (Set.isSubset classes ToolEffectClass.readOnly)
                    $"declared classes {ToolEffectDeclaration.describe def.Effects} must all be read-only"
            }

            test "the read-only ceiling admits it for an unrestricted caller" {
                let verdict =
                    AIToolRegistry.ToolGate.decide
                        (AccessContext.unrestricted (AuthenticatedUser "contract-user"))
                        (fun _ -> true)
                        AIToolRegistry.ToolPolicy.readOnly
                        def

                Expect.isTrue (AIToolRegistry.ToolGate.admits verdict) $"ToolPolicy.readOnly refused it: %A{verdict}"
            }
        ]

        testCaseAsync "(1) an observable active module answers { moduleId, activePage, snapshot }"
        <| async {
            let table = StateTable [ salesModule, salesReport ]
            let! r = inspect f (AllowAll()) table (Some salesModule) (Some salesPage) "{}"

            Expect.equal
                r.Invocations
                [
                    {
                        ActiveModule = Some salesModule
                        ActivePage = Some salesPage
                        ArgsJson = "{}"
                    }
                ]
                "the executor runs once, with the request's active module and page"

            let result = singleResult r
            Expect.isNone (tryProp "status" result) "a snapshot answer carries no status"
            Expect.equal ((prop "moduleId" result).GetString()) salesModule "moduleId is the active module"
            Expect.equal ((prop "activePage" result).GetString()) salesPage "activePage is the request's page"

            let snapshot = prop "snapshot" result
            let fields = prop "fields" snapshot

            Expect.equal
                ((prop "region" fields).ValueKind)
                JsonValueKind.String
                "a field's JSON-encoded leaf is embedded as JSON, not as a string of JSON"

            Expect.equal ((prop "region" fields).GetString()) "EMEA" "string field value"
            Expect.equal ((prop "threshold" fields).GetInt32()) 42 "numeric field value stays a number"

            let accounts =
                (prop "accounts" (prop "selections" snapshot)).EnumerateArray()
                |> Seq.map _.GetString()
                |> List.ofSeq

            Expect.equal accounts [ "acct-1"; "acct-2" ] "selection keys, in order"

            let actions = prop "actions" snapshot
            Expect.isTrue ((prop "export" actions).GetBoolean()) "an enabled action reads true"
            Expect.isFalse ((prop "delete" actions).GetBoolean()) "a disabled action reads false"
        }

        testCaseAsync "(1b) with no active page, activePage is null or absent"
        <| async {
            let table = StateTable [ salesModule, salesReport ]
            let! r = inspect f (AllowAll()) table (Some salesModule) None "{}"
            let result = singleResult r

            match tryProp "activePage" result with
            | None -> ()
            | Some page ->
                Expect.equal page.ValueKind JsonValueKind.Null "activePage must be null when no page is active"

            Expect.equal ((prop "moduleId" result).GetString()) salesModule "still answers for the active module"
            prop "snapshot" result |> ignore
        }

        testCaseAsync "(2) only the active module is read — another module's report never leaks"
        <| async {
            let table = StateTable [ salesModule, salesReport; otherModule, otherReport ]
            let! r = inspect f (AllowAll()) table (Some salesModule) (Some salesPage) "{}"

            Expect.equal ((prop "moduleId" (singleResult r)).GetString()) salesModule "answers for the active module"

            Expect.isFalse
                (List.exists (fun (c: string) -> c.Contains otherMarker) r.Results)
                "the answer must not carry another module's state"
        }

        testCaseAsync "(2b) a module named in the model's arguments is not read instead of the active one"
        <| async {
            let table = StateTable [ salesModule, salesReport; otherModule, otherReport ]

            let! r =
                inspect f (AllowAll()) table (Some salesModule) (Some salesPage) $"""{{"moduleId":"{otherModule}"}}"""

            Expect.equal
                ((prop "moduleId" (singleResult r)).GetString())
                salesModule
                "the model cannot redirect the inspection to a module the user is not viewing"

            Expect.isFalse
                (List.exists (fun (c: string) -> c.Contains otherMarker) r.Results)
                "the named module's state must not be returned"
        }

        testCaseAsync $"(3) no active module answers status '{InspectStatus.NoActiveModule}'"
        <| async {
            let table = StateTable [ salesModule, salesReport ]
            let! r = inspect f (AllowAll()) table None None "{}"
            expectStatus InspectStatus.NoActiveModule (singleResult r)
        }

        testCaseAsync $"(3b) a blank active module is no active module"
        <| async {
            let table = StateTable [ salesModule, salesReport ]
            let! r = inspect f (AllowAll()) table (Some "   ") None "{}"
            expectStatus InspectStatus.NoActiveModule (singleResult r)
        }

        testCaseAsync
            $"(4) an active module with no published report answers status '{InspectStatus.NoObservableState}'"
        <| async {
            let table = StateTable [ otherModule, otherReport ]
            let! r = inspect f (AllowAll()) table (Some salesModule) (Some salesPage) "{}"
            expectStatus InspectStatus.NoObservableState (singleResult r)

            Expect.isFalse
                (List.exists (fun (c: string) -> c.Contains otherMarker) r.Results)
                "falling back to another module's report is a leak, not an answer"
        }

        testCaseAsync "(5) read-only — invoking it changes no module state, and repeats identically"
        <| async {
            let table = StateTable [ salesModule, salesReport; otherModule, otherReport ]
            let before = table.Snapshot()

            let! first = inspect f (AllowAll()) table (Some salesModule) (Some salesPage) "{}"
            let! second = inspect f (AllowAll()) table (Some salesModule) (Some salesPage) "{}"

            Expect.equal (table.Snapshot()) before "the module-state table is unchanged by two invocations"

            let firstText = (singleResult first).GetRawText()
            let secondText = (singleResult second).GetRawText()

            Expect.equal secondText firstText "asked twice over unchanged state, it answers identically"

            let stateChanging =
                first.Run.Events @ second.Run.Events
                |> List.filter (function
                    | ClientToolInvoke(_, _, name, _, _, _) -> name <> def.Name && name <> f.Tool.ProviderDef.Name
                    | AIConsentRequired _ -> true
                    | _ -> false)

            Expect.isEmpty stateChanging "inspection asks the client to run nothing but itself, and needs no consent"
        }

        testCaseAsync "(6) a denying IClientToolAuthorizer stops the call before the executor runs, audited"
        <| async {
            let table = StateTable [ salesModule, salesReport ]
            let! r = inspect f (DenyAll()) table (Some salesModule) (Some salesPage) "{}"

            Expect.isEmpty r.Invocations "the executor must not run for a denied call"

            Expect.isEmpty
                (r.Run.Events
                 |> List.filter (function
                     | ClientToolInvoke _ -> true
                     | _ -> false))
                "no ClientToolInvoke is emitted for a denied call"

            match r.Results with
            | [ content ] ->
                Expect.stringContains content "was denied:" "the model receives the Denied-shaped result"
                Expect.isFalse (content.Contains "EMEA") "a denied call returns no module state"
            | other -> failtestf "expected exactly one (denied) tool result, got %A" other

            let! audits = r.Run.EventStore.ReadBySource("anonymous", "_platform.ai.tool_allowlist_denial")
            Expect.isNonEmpty audits "the denial is audited"
            Expect.stringContains (Seq.head audits).Payload def.Name "the denial audit names the inspect tool"
        }

        testCaseAsync "(7) offered to the model on exactly the surfaces its AISurfaceFilter names"
        <| async {
            for surface in [ SidePanel; FullPage ] do
                let! run =
                    IClientToolDispatchContract.runClientToolLoop
                        {
                            Tools = [ f.Tool ]
                            Authorizer = AllowAll()
                            Surface = surface
                            ActiveModule = Some salesModule
                            ActivePage = Some salesPage
                        }
                        [ IClientToolDispatchContract.endTurnResponse ]
                        (fun _ -> None)

                let offered =
                    match run.OfferedTools with
                    | firstTurn :: _ -> List.contains f.Tool.ProviderDef.Name firstTurn
                    | [] -> failtest "the provider was never called"

                let expected = List.contains surface (surfacesOf def.Surface)

                Expect.equal
                    offered
                    expected
                    $"surface %A{surface} under filter %A{def.Surface}: offered=%b{offered}, expected=%b{expected}"
        }
    ]