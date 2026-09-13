// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AIConsentTests

// ─── Phase 36.D — consent UX + per-conversation allowlist ────────────
//
// Three gates already stand between an LLM and a module's data: the
// caller's `Read` permission (36.A), the liveness of the authority behind
// it (730), and the module's own AI-queryability declaration (36.C). All
// three are answered by the DEPLOYMENT. This is the fourth, and the only
// one that asks the USER, in the conversation, before the agent goes and
// reads from a module they did not name.
//
// What this pack proves, and why each is easy to get wrong:
//
//   1. **The three allow/deny cases have three different LIFETIMES**, and
//      the difference is the whole feature. `AllowForConversation` is
//      remembered; `AllowOnce` authorises one read and the next one asks
//      again; `Denied` is remembered too, so a user who said no is not
//      asked once per turn while the model re-plans. Each is asserted by
//      COUNTING prompts across two reads, not by reading the record —
//      a lookup that returned the right value but was never consulted
//      would pass the second and fail the first.
//
//   2. **It is INNERMOST.** A caller who fails an outer gate must be
//      refused with NO dialog shown, or the dialog leaks which modules
//      exist and which ones the deployment exposed. Asserted as a prompt
//      COUNT of zero beside the refusal, for the same reason.
//
//   3. **The decision is authorised and attributed from the SERVER's
//      pending record, never from the POST body** — which is why the body
//      carries no conversation and no module to lie about. Asserted by
//      posting a decision and checking which conversation and module the
//      record and the audit row landed against.
//
//   4. **A never-answered prompt fails the turn CLEANLY**, on the one
//      suspended-dispatch budget the client-resident tool round trip
//      already uses rather than on a second timeout of its own.
//
// **Non-vacuity.** Every refusal case has a positive twin against the same
// context shape, differing only in the decision or the mode — so a gate
// that refused everything, or nothing, fails one half rather than passing
// both. Where a positive half asserts a DOWNSTREAM error
// (`ResultStoreUnavailable` / `EntityStoreUnavailable`), reaching it is
// the proof that consent let the call through.

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.FSharp.Reflection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.AI
open ToolUp.Platform.Tests.Contracts
open DataManagementTypes

// ─── Fixtures ────────────────────────────────────────────────────────

let private Read = ModulePermission.Read

let private jsonOptions =
    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

let private scope = {
    ScopeId = "t1"
    Container = "team-t1"
    Persist = true
}

/// The 36.C fixture, unchanged: a catalogue over `(moduleName, typeId)`
/// pairs, enough for `query_entity`'s producer attribution.
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

/// The real bus's RBAC behaviour and nothing else — `PermissionDenied`
/// exactly where the shipped one answers it, `Ok` otherwise. Needed for
/// the ordering assertion: with no bus registered `query_module`
/// short-circuits on `ModuleQueryBusUnavailable` before any gate speaks.
type private StubQueryBus() =
    interface IModuleQueryBus with
        member _.Ask(context, request) = async {
            if not (AccessContext.hasPermission request.TargetModule ModulePermission.Read context) then
                return Some(Error(PermissionDenied request.TargetModule))
            else
                return Some(Ok { Payload = """{"ok":true}""" })
        }

let private queryableOf (names: string list) =
    ModuleAIExposureRegistry.ofDeclarations (names |> List.map (fun n -> n, ModuleAIExposure.Queryable))

let private toolNamed (name: string) =
    PlatformAITools.builtIn
    |> List.tryFind (fun t -> t.Definition.Name = name)
    |> Option.defaultWith (fun () -> failwithf "no built-in tool named '%s'" name)

/// The `error` discriminator, or `None` on a success payload. Reading the
/// field rather than substring-matching: a message that happens to mention
/// `UserDenied` must not read as one.
let private errorOf (json: string) : string option =
    use doc = JsonDocument.Parse json

    match doc.RootElement.TryGetProperty "error" with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

/// Everything one simulated conversation needs, wired the way `composeAI`
/// wires it.
type private Harness = {
    Ctx: HttpContext
    Services: IServiceProvider
    Registry: AIConsentDispatch.AIConsentRegistry
    Storage: IBlobStorage
    Events: IEventStore
    ConversationId: Guid
    Emitted: ResizeArray<AIStreamEvent>
    /// What the simulated user clicks. `None` = they never answer, which
    /// takes the abandon path rather than sitting out the 90 s budget.
    AnswerWith: AllowDecision option ref
    Perms: (string * ModulePermission list) list
}

/// Post a decision through the REAL `/api/ai/consent` handler.
///
/// Deliberately not a shortcut into the registry: persistence, audit and
/// completion all live in that handler, and a test that reimplemented them
/// would assert its own copy rather than the shipped path.
let private postDecision
    (services: IServiceProvider)
    (perms: (string * ModulePermission list) list)
    (userId: string)
    (consentId: Guid)
    (decisionToken: string)
    : int =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services
    ctx.Items["ToolUp.UserId"] <- box userId
    ctx.Items["ToolUp.StorageScope"] <- box scope
    ctx.Items["ToolUp.ModulePermissions"] <- box (Map.ofList perms)

    let request: AIConsentDecisionRequest = {
        ConsentId = consentId
        Decision = decisionToken
    }

    let body = JsonSerializer.Serialize(request, jsonOptions)

    ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes body)
    ctx.Response.Body <- new MemoryStream()

    AIConsentHandler.consentDecisionHandler (fun c -> System.Threading.Tasks.Task.FromResult(Some c)) ctx
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> ignore

    ctx.Response.StatusCode

let private buildHarness
    (mode: AIConsentMode)
    (perms: (string * ModulePermission list) list)
    (queryable: string list)
    (registrations: (string * string) list)
    : Harness =
    let services = ServiceCollection()

    services.AddSingleton<ModuleAIExposureRegistry>(queryableOf queryable) |> ignore

    services.AddSingleton<IDataCatalog>(StubCatalog registrations :> IDataCatalog)
    |> ignore

    services.AddSingleton<IModuleQueryBus>(StubQueryBus() :> IModuleQueryBus)
    |> ignore

    let registry = AIConsentDispatch.AIConsentRegistry()
    services.AddSingleton<AIConsentDispatch.AIConsentRegistry>(registry) |> ignore
    services.AddSingleton<AIConsentMode>(mode) |> ignore

    let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
    services.AddSingleton<IBlobStorage>(storage) |> ignore

    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    services.AddSingleton<IEventStore>(events) |> ignore

    let provider = services.BuildServiceProvider() :> IServiceProvider
    let conversationId = Guid.NewGuid()
    let emitted = ResizeArray<AIStreamEvent>()
    let answerWith = ref None

    // The simulated browser. `requireConsent` registers the pending entry
    // BEFORE it emits, so answering synchronously from inside the emitter
    // leaves the decision already settled when the tool's `WhenAny` runs —
    // deterministic, no polling, no second thread.
    let emit (evt: AIStreamEvent) =
        emitted.Add evt

        match evt with
        | AIConsentRequired(_, consentId, _, _, _, _, _) ->
            match answerWith.Value with
            | Some decision ->
                postDecision provider perms "alice" consentId (AllowDecision.toToken decision)
                |> ignore
            | None ->
                // Nobody answered. The production path reaches the same
                // resolution through the shared suspended-dispatch budget;
                // taking it directly keeps the pack off a 90-second wait.
                registry.TryAbandon consentId |> ignore
        | _ -> ()

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Items["ToolUp.UserId"] <- box "alice"
    ctx.Items["ToolUp.StorageScope"] <- box scope
    ctx.Items["ToolUp.ModulePermissions"] <- box (Map.ofList perms)
    ctx.Items[AIConsentDispatch.ItemsKeys.ConversationId] <- box conversationId
    ctx.Items[AIConsentDispatch.ItemsKeys.TaskId] <- box (Guid.NewGuid())

    ctx.Items[AIConsentDispatch.ItemsKeys.StreamEmitter] <- box ({ Emit = emit }: AIConsentDispatch.StreamEmitter)

    {
        Ctx = ctx
        Services = provider
        Registry = registry
        Storage = storage
        Events = events
        ConversationId = conversationId
        Emitted = emitted
        AnswerWith = answerWith
        Perms = perms
    }

let private run (h: Harness) (name: string) (args: string) =
    (toolNamed name).Execute h.Ctx args |> Async.RunSynchronously

let private promptCount (h: Harness) =
    h.Emitted
    |> Seq.filter (fun e ->
        match e with
        | AIConsentRequired _ -> true
        | _ -> false)
    |> Seq.length

let private consentEvents (h: Harness) =
    h.Events.ReadBySource(scope.ScopeId, AIConsentHandler.ConsentAuditSource)
    |> Async.RunSynchronously

let private recordedState (h: Harness) =
    AIConsentDispatch.loadState h.Storage scope.Container h.ConversationId
    |> Async.RunSynchronously

// A module the caller may read, which opted into the AI surface, and a
// result store that is absent — so "past every gate" surfaces as
// `ResultStoreUnavailable`, which is the positive half's marker.
let private moodPerms = [ "MoodJournal", [ Read ]; "Payroll", [ Read ] ]
let private moodArgs = """{"moduleName":"MoodJournal"}"""

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 36.D — cross-module read consent" [

        testList "the decision vocabulary" [

            testCase "AllowDecision tokens round-trip, and an unknown token fails CLOSED"
            <| fun _ ->
                for decision in [ AllowOnce; AllowForConversation; Denied ] do
                    Expect.equal (AllowDecision.ofToken (AllowDecision.toToken decision)) decision "round-trip"

                for token in [ ""; "allow"; "ALLOWONCE"; "true"; null ] do
                    Expect.equal
                        (AllowDecision.ofToken token)
                        Denied
                        $"a token this node cannot interpret must not be the one that authorises a read: '{token}'"

            testCase "AIConsentMode tokens round-trip, and an unknown token is NOT TrustEverything"
            <| fun _ ->
                for mode in [ AlwaysAsk; RememberPerConversation; TrustEverything ] do
                    Expect.equal (AIConsentMode.ofToken (AIConsentMode.toToken mode)) mode "round-trip"

                for token in [ ""; "trust"; "off"; null ] do
                    Expect.equal
                        (AIConsentMode.ofToken token)
                        RememberPerConversation
                        $"an unreadable mode must never be the one that turns the gate off: '{token}'"

            testCase "only AllowForConversation satisfies a later lookup"
            <| fun _ ->
                // The entire behavioural difference between the two allow
                // cases, in one function so no call site can implement half
                // of it.
                Expect.isTrue (AIConsentState.satisfies AllowForConversation) "remembered"
                Expect.isFalse (AIConsentState.satisfies AllowOnce) "spent on the read it was given for"
                Expect.isFalse (AIConsentState.satisfies Denied) "refused, and stays refused"

            testCase "a record written before the allowlist field existed reads as no decision"
            <| fun _ ->
                // The documented additive-field hazard on the STJ read path:
                // an absent reference-type field deserialises to `null`, and
                // a null F# `Map` throws on every operation. Coerced in one
                // place so a pre-36.D blob re-prompts rather than crashing
                // the turn.
                let legacy: AIConsentState = {
                    CrossModuleAllowlist = Unchecked.defaultof<_>
                }

                Expect.isNone (AIConsentState.decisionFor "MoodJournal" legacy) "null map ⇒ nothing recorded"

                Expect.equal
                    (AIConsentState.record "MoodJournal" AllowForConversation legacy
                     |> AIConsentState.decisionFor "MoodJournal")
                    (Some AllowForConversation)
                    "and a write over it still lands"
        ]

        testList "the gate" [

            testCase "TrustEverything never prompts, and the read goes through"
            <| fun _ ->
                let h = buildHarness TrustEverything moodPerms [ "MoodJournal" ] []

                Expect.equal
                    (errorOf (run h "_platform.ai.list_results" moodArgs))
                    (Some "ResultStoreUnavailable")
                    "past every gate — refused by the absent store, not by consent"

                Expect.equal (promptCount h) 0 "the one mode that opts out of the dialog shows none"

            testCase "the default mode prompts on the first read, and the prompt says what it is for"
            <| fun _ ->
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []
                h.AnswerWith.Value <- Some AllowForConversation

                Expect.equal
                    (errorOf (
                        run h "_platform.ai.get_latest_result" """{"moduleName":"MoodJournal","resultType":"q1"}"""
                    ))
                    (Some "ResultStoreUnavailable")
                    "allowed, so the read proceeded to the absent store"

                match List.ofSeq h.Emitted with
                | [ AIConsentRequired(_, _, conversationId, toolName, targetModule, queryKey, preview) ] ->
                    Expect.equal conversationId h.ConversationId "the prompt names this conversation"
                    Expect.equal toolName "_platform.ai.get_latest_result" "and the tool that is about to read"
                    Expect.equal targetModule "MoodJournal" "and the module it is about to read FROM"
                    Expect.equal queryKey "q1" "and what it intends to read"

                    Expect.stringContains
                        preview
                        "MoodJournal"
                        "the preview shows the model's own arguments, which is what the user is judging"
                | other -> failtestf "expected exactly one consent prompt, got %A" other

            testCase "AllowForConversation persists — the second read from the same module does NOT prompt"
            <| fun _ ->
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []
                h.AnswerWith.Value <- Some AllowForConversation

                run h "_platform.ai.list_results" moodArgs |> ignore
                Expect.equal (promptCount h) 1 "the first read asks"

                // If the second read prompted, the emitter would answer it
                // again and the count would rise — so this counts prompts
                // rather than reading the record, which a never-consulted
                // lookup would also satisfy.
                Expect.equal
                    (errorOf (run h "_platform.ai.list_results" moodArgs))
                    (Some "ResultStoreUnavailable")
                    "and still goes through"

                Expect.equal (promptCount h) 1 "the second read flows on the recorded decision"

                Expect.equal
                    (AIConsentState.decisionFor "MoodJournal" (recordedState h))
                    (Some AllowForConversation)
                    "recorded against this conversation"

            testCase "AllowOnce re-prompts on the next read"
            <| fun _ ->
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []
                h.AnswerWith.Value <- Some AllowOnce

                run h "_platform.ai.list_results" moodArgs |> ignore
                run h "_platform.ai.list_results" moodArgs |> ignore

                Expect.equal (promptCount h) 2 "the allowance was spent on the read it was given for"

                Expect.equal
                    (AIConsentState.decisionFor "MoodJournal" (recordedState h))
                    (Some AllowOnce)
                    "it is still RECORDED — the audit trail and the history show it; it just never satisfies a lookup"

            testCase "Denied returns a typed UserDenied, and does not ask again"
            <| fun _ ->
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []
                h.AnswerWith.Value <- Some Denied

                let refused = run h "_platform.ai.list_results" moodArgs

                Expect.equal
                    (errorOf refused)
                    (Some "UserDenied")
                    "a fourth discriminator for a fourth remedy — not PermissionDenied, which the caller does hold, and not UnqueryableModule, which an operator would have to fix"

                Expect.stringContains
                    refused
                    "do not retry"
                    "the refusal is written for the model, which otherwise re-plans the same read"

                Expect.equal (errorOf (run h "_platform.ai.list_results" moodArgs)) (Some "UserDenied") "still refused"

                Expect.equal
                    (promptCount h)
                    1
                    "and NOT asked again — a user who said no once is not asked per turn while the model re-plans"

            testCase "AlwaysAsk ignores a standing decision and asks every time"
            <| fun _ ->
                let h = buildHarness AlwaysAsk moodPerms [ "MoodJournal" ] []
                h.AnswerWith.Value <- Some AllowForConversation

                run h "_platform.ai.list_results" moodArgs |> ignore
                run h "_platform.ai.list_results" moodArgs |> ignore

                Expect.equal (promptCount h) 2 "the mode whose whole point is that no allowance stands"

            testCase "an unanswered prompt refuses the read rather than hanging the turn"
            <| fun _ ->
                // `AnswerWith = None` takes the abandon path — the same
                // resolution the shared suspended-dispatch budget reaches
                // when nobody clicks. The property under test is that the
                // turn ends with a refusal it can explain, not a hang.
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []

                let refused = run h "_platform.ai.list_results" moodArgs

                Expect.equal (errorOf refused) (Some "UserDenied") "an unanswered prompt is not an authorised read"
                Expect.equal (promptCount h) 1 "and it was asked"

            testCase "the consent wait rides the one suspended-dispatch budget"
            <| fun _ ->
                // The client-resident tool round trip and the consent round
                // trip are the same wait; two constants would be two budgets
                // to keep in step. The value is asserted here so a silent
                // divergence in either direction is visible.
                Expect.equal
                    AIConsentDispatch.SuspendedDispatchTimeoutMs
                    90_000
                    "the shared server-side budget for a dispatch suspended on the browser"

            testCase "query_entity asks for the producing module"
            <| fun _ ->
                let h =
                    buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] [ "MoodJournal", "Mood" ]

                h.AnswerWith.Value <- Some AllowForConversation

                Expect.equal
                    (errorOf (run h "_platform.ai.query_entity" """{"entityType":"Mood"}"""))
                    (Some "EntityStoreUnavailable")
                    "past the gate, refused by the absent entity store"

                match List.ofSeq h.Emitted with
                | [ AIConsentRequired(_, _, _, _, targetModule, queryKey, _) ] ->
                    Expect.equal targetModule "MoodJournal" "the producer whose data the read would return"
                    Expect.equal queryKey "Mood" "and the entity type it intends to read"
                | other -> failtestf "expected exactly one consent prompt, got %A" other

            testCase "an entity type the catalogue cannot attribute has no module to ask about"
            <| fun _ ->
                // The documented limit of the module-level grain, inherited
                // from 36.C: `EntityRegistration` carries no module
                // attribution, so an entity type with no catalogued producer
                // has nobody to name in a dialog. Passing it through is the
                // same decision 36.C made, for the same reason.
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []

                Expect.equal
                    (errorOf (run h "_platform.ai.query_entity" """{"entityType":"Untracked"}"""))
                    (Some "EntityStoreUnavailable")
                    "no producer to attribute ⇒ this gate has nothing to say"

                Expect.equal (promptCount h) 0 "and nothing to ask"
        ]

        testList "composition order — consent is INNERMOST" [

            testCase "PermissionDenied wins, and NO dialog is shown"
            <| fun _ ->
                // The leak this ordering prevents: a caller who may not read
                // the module must not learn it exists by being asked about
                // it. Asserted as a prompt count, because a refusal that
                // ALSO prompted would still return the right error.
                let h =
                    buildHarness RememberPerConversation [ "MoodJournal", [ Read ] ] [ "Payroll" ] []

                h.AnswerWith.Value <- Some AllowForConversation

                Expect.equal
                    (errorOf (run h "_platform.ai.list_results" """{"moduleName":"Payroll"}"""))
                    (Some "PermissionDenied")
                    "the caller-side answer, unchanged"

                Expect.equal (promptCount h) 0 "and the user was never shown a module they cannot read"

            testCase "UnqueryableModule wins, and NO dialog is shown"
            <| fun _ ->
                // The deployment has already said this module is not on the
                // AI surface. Asking the user to allow something the
                // deployment has refused would be a dialog whose only honest
                // answer changes nothing.
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []
                h.AnswerWith.Value <- Some AllowForConversation

                Expect.equal
                    (errorOf (run h "_platform.ai.list_results" """{"moduleName":"Payroll"}"""))
                    (Some "UnqueryableModule")
                    "the deployment-side answer, unchanged"

                Expect.equal (promptCount h) 0 "no dialog for a read the deployment has already refused"

            testCase "query_module refuses before the bus, so the module's handler never runs"
            <| fun _ ->
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []
                h.AnswerWith.Value <- Some Denied

                Expect.equal
                    (errorOf (run h "_platform.ai.query_module" """{"moduleName":"MoodJournal","queryKey":"summary"}"""))
                    (Some "UserDenied")
                    "the bus would have answered Ok — the refusal is ours, ahead of it"

            testCase "query_module lets an allowed module through to the bus"
            <| fun _ ->
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []
                h.AnswerWith.Value <- Some AllowForConversation

                Expect.isNone
                    (errorOf (run h "_platform.ai.query_module" """{"moduleName":"MoodJournal","queryKey":"summary"}"""))
                    "past consent and dispatched — the bus answered"
        ]

        testList "the /api/ai/consent endpoint" [

            testCase "a granted decision completes the read, persists, and audits"
            <| fun _ ->
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []
                h.AnswerWith.Value <- Some AllowForConversation

                run h "_platform.ai.list_results" moodArgs |> ignore

                match consentEvents h with
                | [ evt ] ->
                    Expect.equal evt.SourceModule "_platform.ai.consent" "one audit source for every consent decision"
                    Expect.equal evt.EventType "AIConsentGranted" "an allow is a grant"

                    use doc = JsonDocument.Parse evt.Payload
                    let root = doc.RootElement

                    Expect.equal (root.GetProperty("UserId").GetString()) "alice" "userId"

                    Expect.equal
                        (root.GetProperty("ConversationId").GetString())
                        (string h.ConversationId)
                        "conversationId"

                    Expect.equal (root.GetProperty("TargetModule").GetString()) "MoodJournal" "targetModule"

                    Expect.equal
                        (root.GetProperty("Decision").GetString())
                        "AllowForConversation"
                        "decision, as the stable token rather than an F# DU rendering"
                | other -> failtestf "expected exactly one consent audit row, got %A" other

            testCase "a denial audits as a denial"
            <| fun _ ->
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []
                h.AnswerWith.Value <- Some Denied

                run h "_platform.ai.list_results" moodArgs |> ignore

                match consentEvents h with
                | [ evt ] -> Expect.equal evt.EventType "AIConsentDenied" "a refusal is recorded as one"
                | other -> failtestf "expected exactly one consent audit row, got %A" other

            testCase "the conversation and the module come from the SERVER's record, not the body"
            <| fun _ ->
                // The POST carries a consent id and a decision token and
                // nothing else that matters — so there is nothing for a
                // client to lie about. This asserts the consequence: the
                // record lands against the conversation and module the
                // SUSPENDING TOOL registered.
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []
                h.AnswerWith.Value <- Some AllowForConversation

                run h "_platform.ai.list_results" moodArgs |> ignore

                Expect.equal
                    (AIConsentState.decisionFor "MoodJournal" (recordedState h))
                    (Some AllowForConversation)
                    "attributed to the module the tool was reading"

                Expect.isNone (AIConsentState.decisionFor "Payroll" (recordedState h)) "and to nothing else"

            testCase "an unreadable token fails CLOSED at the endpoint"
            <| fun _ ->
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []
                let consentId = Guid.NewGuid()

                let awaited =
                    h.Registry.RegisterPending(
                        consentId,
                        {
                            ConversationId = h.ConversationId
                            TargetModule = "MoodJournal"
                            Container = scope.Container
                            UserId = "alice"
                        }
                    )

                Expect.equal (postDecision h.Services h.Perms "alice" consentId "YesPlease") 200 "the POST is accepted"

                Expect.equal
                    (awaited |> Async.AwaitTask |> Async.RunSynchronously)
                    Denied
                    "a decision this build cannot interpret refuses the read rather than authorising it"

            testCase "a caller without Read on the target module is refused 403, and the read stays suspended"
            <| fun _ ->
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []
                let consentId = Guid.NewGuid()

                let awaited =
                    h.Registry.RegisterPending(
                        consentId,
                        {
                            ConversationId = h.ConversationId
                            TargetModule = "MoodJournal"
                            Container = scope.Container
                            UserId = "alice"
                        }
                    )

                // The symmetric half of the gate — the same shape Phase 36.A
                // added to `/api/ai/tool-result`, for the same reason.
                Expect.equal
                    (postDecision h.Services [ "Unrelated", [ Read ] ] "mallory" consentId "AllowForConversation")
                    403
                    "a caller who cannot read the module cannot answer for it"

                Expect.isFalse awaited.IsCompleted "the refused decision must not reach the suspended read"

                Expect.isSome
                    (h.Registry.PendingOf consentId)
                    "and the pending request stays registered — a refusal must not double as a cancellation lever"

                Expect.isEmpty (consentEvents h) "nothing was decided, so nothing is audited"

            testCase "an unknown consent id is a quiet 404"
            <| fun _ ->
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []

                Expect.equal
                    (postDecision h.Services h.Perms "alice" (Guid.NewGuid()) "AllowForConversation")
                    404
                    "a late click after the read timed out is not an error the user should see"

            testCase "a malformed body is a 400"
            <| fun _ ->
                let h = buildHarness RememberPerConversation moodPerms [ "MoodJournal" ] []

                let ctx = DefaultHttpContext()
                ctx.RequestServices <- h.Services
                ctx.Items["ToolUp.UserId"] <- box "alice"
                ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes "not json")
                ctx.Response.Body <- new MemoryStream()

                AIConsentHandler.consentDecisionHandler (fun c -> System.Threading.Tasks.Task.FromResult(Some c)) ctx
                |> Async.AwaitTask
                |> Async.RunSynchronously
                |> ignore

                Expect.equal ctx.Response.StatusCode 400 "an undeserialisable body"
        ]

        testList "six-rule portability audit on the new wire shapes" [

            testCase "every field of the AIConsentRequired SSE event is a primitive"
            <| fun _ ->
                // Rule 1 — identity by value. A live handle, a server type,
                // or a platform record here would make the event
                // unrenderable by a non-.NET client and would tie the SSE
                // contract to this assembly. Checked mechanically so a later
                // field addition has to make the same choice deliberately.
                let case =
                    FSharpType.GetUnionCases typeof<AIStreamEvent>
                    |> Array.tryFind (fun c -> c.Name = "AIConsentRequired")
                    |> Option.defaultWith (fun () -> failtest "AIStreamEvent has no AIConsentRequired case")

                for field in case.GetFields() do
                    Expect.isTrue
                        (field.PropertyType = typeof<Guid> || field.PropertyType = typeof<string>)
                        $"AIConsentRequired.{field.Name} is {field.PropertyType.FullName}; the SSE payload must stay FSharp-primitive-only"

            testCase "every field of the AIConsentDecisionRequest POST payload is a primitive"
            <| fun _ ->
                for field in FSharpType.GetRecordFields typeof<AIConsentDecisionRequest> do
                    Expect.isTrue
                        (field.PropertyType = typeof<Guid> || field.PropertyType = typeof<string>)
                        $"AIConsentDecisionRequest.{field.Name} is {field.PropertyType.FullName}; the POST contract must stay FSharp-primitive-only"

            testCase "the decision crosses the wire as a token, not as a DU"
            <| fun _ ->
                // Deliberate: the body is hand-written from the browser, so
                // a DU on the wire would bind the contract to one
                // serialiser's case encoding.
                let field =
                    FSharpType.GetRecordFields typeof<AIConsentDecisionRequest>
                    |> Array.find (fun f -> f.Name = "Decision")

                Expect.equal field.PropertyType typeof<string> "a token a non-F# client can produce"
        ]
    ]