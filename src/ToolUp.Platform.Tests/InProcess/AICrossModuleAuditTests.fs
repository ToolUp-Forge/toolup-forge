// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AICrossModuleAuditTests

// ─── Phase 36.E — cross-module audit + observability ─────────────────
//
// Phases 36.A / 730 / 36.C / 36.D record every REFUSAL of a
// `_platform.ai.*` read and nothing at all about one that succeeded.
// This is the trail that answers both halves, and what this pack proves
// is the four things about it that are easy to get wrong:
//
//   1. **ONE row per invocation, on every outcome path.** Asserted by
//      COUNTING rows across a sequence of calls, never by checking that
//      a row exists: an emission that fired twice on the success path,
//      or not at all behind an early refusal, passes a presence check
//      and fails this. The refusal cases are enumerated one per gate —
//      permission, opt-in, consent — because each leaves the executor
//      through a different return.
//
//   2. **A consent-denied read still records the ATTEMPT**, with
//      `Allowed = false`. This is the acceptance criterion that makes
//      the trail worth having: the read that did not happen is the one
//      a reviewer is looking for. Its positive twin is the same context
//      with the user answering yes, so a decorator that recorded
//      everything as denied fails one half rather than passing both.
//
//   3. **The Phase 9g replicator carries the new type with NO
//      sink-code change** — which is why the row is a typed
//      `AuditEvent` case rather than an AI-tier `ModuleEvent` under its
//      own `SourceModule`. `AuditReplicator.shouldReplicate` admits
//      `_platform.audit` only, so the natural-looking precedent would
//      have produced a row no external sink ever sees. Asserted through
//      the shipped write path and then through each sink that has a
//      test pack, using each sink's own projection rather than a
//      re-implementation of it.
//
//   4. **The rollup's arithmetic.** `computeReport` is pure and total,
//      so the grouping, the percentiles and the denial rate are
//      exercised against a synthetic burst rather than inspected in a
//      running deployment.
//
// **Non-vacuity.** Every negative has a positive twin against the same
// harness shape, differing only in the permission, the opt-in or the
// decision. The row COUNT is asserted alongside the row CONTENT
// throughout, so a decorator that stopped emitting would fail loudly
// rather than leave an assertion about an empty list passing.

open System
open System.Collections.Concurrent
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Http
open Microsoft.FSharp.Reflection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts
open ToolUp.AI
open DataManagementTypes

// ─── Fixtures ────────────────────────────────────────────────────────

let private Read = ModulePermission.Read

let private scope = {
    ScopeId = "t1"
    Container = "team-t1"
    Persist = true
}

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// The 36.C / 36.D catalogue fixture: `(moduleName, typeId)` pairs, which
/// is all `query_entity`'s producer attribution needs.
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

/// The shipped bus's RBAC behaviour and nothing else, so `query_module`
/// has a reachable success path.
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

// ─── Harness ─────────────────────────────────────────────────────────

type private Harness = {
    Ctx: HttpContext
    AuditLog: IAuditLog
    Events: IEventStore
    ConversationId: Guid
}

/// Wire the substrate the way `composeAI` wires it, plus the `IAuditLog`
/// the decorator writes through.
///
/// `consentMode` defaults to `TrustEverything` for the cases that are not
/// about consent — the 36.D gate then short-circuits before any I/O, so
/// those cases assert the decorator rather than the dialog.
let private buildHarness
    (consentMode: AIConsentMode)
    (answerWith: AllowDecision option)
    (perms: (string * ModulePermission list) list)
    (queryable: string list)
    (registrations: (string * string) list)
    (activeModule: string option)
    : Harness =
    let services = ServiceCollection()

    services.AddSingleton<ModuleAIExposureRegistry>(queryableOf queryable) |> ignore

    services.AddSingleton<IDataCatalog>(StubCatalog registrations :> IDataCatalog)
    |> ignore

    services.AddSingleton<IModuleQueryBus>(StubQueryBus() :> IModuleQueryBus)
    |> ignore

    let registry = AIConsentDispatch.AIConsentRegistry()
    services.AddSingleton<AIConsentDispatch.AIConsentRegistry>(registry) |> ignore
    services.AddSingleton<AIConsentMode>(consentMode) |> ignore

    let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
    services.AddSingleton<IBlobStorage>(storage) |> ignore

    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    services.AddSingleton<IEventStore>(events) |> ignore

    let auditLog = AuditLog.EventStoreAuditLog(events, silentLogger) :> IAuditLog
    services.AddSingleton<IAuditLog>(auditLog) |> ignore

    let provider = services.BuildServiceProvider() :> IServiceProvider
    let conversationId = Guid.NewGuid()

    // The simulated browser, answering synchronously from inside the
    // emitter. `requireConsent` registers the pending entry BEFORE it
    // emits, so the decision is already settled when the tool's
    // `Task.WhenAny` runs — deterministic, and off the 90-second budget.
    let emit (evt: AIStreamEvent) =
        match evt, answerWith with
        | AIConsentRequired(_, consentId, _, _, _, _, _), Some decision ->
            registry.TryComplete(consentId, decision) |> ignore
        | AIConsentRequired(_, consentId, _, _, _, _, _), None -> registry.TryAbandon consentId |> ignore
        | _ -> ()

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Items["ToolUp.UserId"] <- box "alice"
    ctx.Items["ToolUp.StorageScope"] <- box scope
    ctx.Items["ToolUp.ModulePermissions"] <- box (Map.ofList perms)
    ctx.Items[AIConsentDispatch.ItemsKeys.ConversationId] <- box conversationId
    ctx.Items[AIConsentDispatch.ItemsKeys.TaskId] <- box (Guid.NewGuid())

    ctx.Items[AIConsentDispatch.ItemsKeys.StreamEmitter] <- box ({ Emit = emit }: AIConsentDispatch.StreamEmitter)

    activeModule
    |> Option.iter (fun m -> ctx.Items[AICrossModuleAudit.ItemsKeys.ActiveModule] <- box m)

    {
        Ctx = ctx
        AuditLog = auditLog
        Events = events
        ConversationId = conversationId
    }

/// The default harness: trusted deployment, both modules readable and
/// queryable, one entity type per module.
let private trusting () =
    buildHarness
        TrustEverything
        None
        [ "MoodJournal", [ Read ]; "Payroll", [ Read ] ]
        [ "MoodJournal"; "Payroll" ]
        [ "MoodJournal", "Mood"; "Payroll", "Payslip" ]
        (Some "Dashboard")

let private run (h: Harness) (name: string) (args: string) =
    (toolNamed name).Execute h.Ctx args |> Async.RunSynchronously

/// Every `CrossModuleRead` row recorded in the caller's scope, read back
/// through the shipped `IAuditLog` query path — the same path
/// `/dev/ai-cross-module` uses, so a row this cannot see is a row the
/// endpoint cannot show either.
let private rows (h: Harness) : CrossModuleReadPayload list =
    h.AuditLog.GetAuditTrail(scope.ScopeId, None, Some AICrossModuleObservability.EventType)
    |> Async.RunSynchronously
    |> List.choose (function
        | CrossModuleRead p -> Some p
        | _ -> None)

let private soleRow (h: Harness) : CrossModuleReadPayload =
    match rows h with
    | [ single ] -> single
    | other -> failtestf "expected exactly one CrossModuleRead row, got %d" (List.length other)

let private moodArgs = """{"moduleName":"MoodJournal"}"""

// ─── Synthetic rows for the pure rollup ──────────────────────────────

let private syntheticRow (target: string option) (tool: string) (latencyMs: float) (outcome: string) = {
    ConversationId = Some(Guid.Parse "11111111-1111-1111-1111-111111111111")
    UserId = "alice"
    SourceConvActiveModule = Some "Dashboard"
    TargetModule = target
    TargetModules = Option.toList target
    ToolName = tool
    ComponentId = ComponentId.value (ComponentId.forTool tool)
    QueryKey = None
    LatencyMs = latencyMs
    ResultBytes = 100
    Allowed = outcome = AICrossModuleAudit.OkOutcome
    Outcome = outcome
}

// ─── Sink harnesses (per-sink 9g coverage) ───────────────────────────

/// Always-200, body-recording transport. The shape both vendor sink test
/// packs already use; duplicated rather than shared because those are
/// `private` to their own modules.
type private RecordingHandler() =
    inherit HttpMessageHandler()

    let bodies = ConcurrentQueue<string>()

    member _.RecordedBodies: string list = bodies |> List.ofSeq

    override _.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> = task {
        let! body =
            if isNull request.Content then
                Task.FromResult ""
            else
                request.Content.ReadAsStringAsync()

        bodies.Enqueue body
        return new HttpResponseMessage(HttpStatusCode.OK)
    }

type private FakeSecretStore() =
    interface ISecretStore with
        member _.GetSecret(_scope, _key) = async { return Some "test-token" }
        member _.SetSecret(_scope, _key, _value) = async { return Ok() }
        member _.DeleteSecret(_scope, _key) = async { return Ok() }
        member _.ListKeys _ = async { return [] }

let private gunzip (bytes: byte[]) : string =
    use input = new MemoryStream(bytes)
    use gz = new GZipStream(input, CompressionMode.Decompress)
    use reader = new StreamReader(gz, Encoding.UTF8)
    reader.ReadToEnd()

/// One envelope carrying a `CrossModuleRead`, for the sink assertions.
let private sinkEnvelope: AuditEnvelope =
    AuditEnvelope.fromScopeId
        "team-sinks"
        (DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc))
        (CrossModuleRead(syntheticRow (Some "Payroll") "_platform.ai.list_results" 12.0 AICrossModuleAudit.OkOutcome))

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 36.E — cross-module audit + observability" [

        testList "emission — one row per invocation, every outcome path" [

            testCase "a successful cross-module read records exactly one allowed row"
            <| fun _ ->
                let h = trusting ()

                let result =
                    run h "_platform.ai.query_module" """{"moduleName":"MoodJournal","queryKey":"latest"}"""

                Expect.stringContains result "\"ok\"" "the read reached the bus"

                let row = soleRow h
                Expect.isTrue row.Allowed "an unrefused read is allowed"
                Expect.equal row.Outcome AICrossModuleAudit.OkOutcome "outcome token"
                Expect.equal row.TargetModule (Some "MoodJournal") "the module it read"
                Expect.equal row.ToolName "_platform.ai.query_module" "the tool that read it"
                Expect.equal row.QueryKey (Some "latest") "the discriminator within the target"
                Expect.isGreaterThan row.ResultBytes 0 "the size of what came back"

            testCase "N invocations produce N rows — counted, not merely present"
            <| fun _ ->
                let h = trusting ()

                run h "_platform.ai.list_accessible_modules" "{}" |> ignore
                run h "_platform.ai.list_data_types" "{}" |> ignore

                run h "_platform.ai.query_module" """{"moduleName":"MoodJournal","queryKey":"latest"}"""
                |> ignore

                run h "_platform.ai.list_results" moodArgs |> ignore

                // Four calls, four rows. A decorator that double-emitted on
                // the success path, or skipped the enumeration tools,
                // fails here and would pass any presence check.
                Expect.hasLength (rows h) 4 "one row per _platform.ai.* invocation"

            testCase "the two enumeration tools record a read of no module"
            <| fun _ ->
                let h = trusting ()
                run h "_platform.ai.list_accessible_modules" "{}" |> ignore

                let row = soleRow h
                Expect.equal row.TargetModule None "an enumeration reads no module's data"
                Expect.isEmpty row.TargetModules "and names none"
                Expect.isTrue row.Allowed "it is not a refusal either"

            testCase "a permission-denied read still records the attempt"
            <| fun _ ->
                // `list_results` pre-checks the caller's own permission and
                // returns before touching any store.
                let h =
                    buildHarness TrustEverything None [ "Payroll", [ Read ] ] [ "MoodJournal" ] [] None

                let result = run h "_platform.ai.list_results" moodArgs
                Expect.stringContains result "PermissionDenied" "refused"

                let row = soleRow h
                Expect.isFalse row.Allowed "a refusal is not allowed"
                Expect.equal row.Outcome "PermissionDenied" "which gate spoke"
                Expect.equal row.TargetModule (Some "MoodJournal") "what it was reaching for"

            testCase "an opt-in refusal still records the attempt"
            <| fun _ ->
                let h = buildHarness TrustEverything None [ "MoodJournal", [ Read ] ] [] [] None

                let result = run h "_platform.ai.list_results" moodArgs
                Expect.stringContains result "UnqueryableModule" "refused by the 36.C gate"

                let row = soleRow h
                Expect.isFalse row.Allowed "a refusal is not allowed"
                Expect.equal row.Outcome "UnqueryableModule" "which gate spoke"

            testCase "invalid arguments record a row with no target"
            <| fun _ ->
                let h = trusting ()
                run h "_platform.ai.list_results" "{}" |> ignore

                let row = soleRow h
                Expect.equal row.Outcome "InvalidArguments" "the tool's own discriminator"
                Expect.equal row.TargetModule None "a call that named no module reached none"

            testCase "the row carries the conversation and the active module"
            <| fun _ ->
                let h = trusting ()
                run h "_platform.ai.list_accessible_modules" "{}" |> ignore

                let row = soleRow h
                Expect.equal row.ConversationId (Some h.ConversationId) "the conversation it belonged to"
                Expect.equal row.SourceConvActiveModule (Some "Dashboard") "the 'from' half of cross-module"
                Expect.equal row.UserId "alice" "the caller"

            testCase "no active module stamped reads as None, not as a guess"
            <| fun _ ->
                let h =
                    buildHarness TrustEverything None [ "MoodJournal", [ Read ] ] [ "MoodJournal" ] [] None

                run h "_platform.ai.list_accessible_modules" "{}" |> ignore
                Expect.equal (soleRow h).SourceConvActiveModule None "absent is absent"

            testCase "the Phase 283 component id on the row is the tool's slot"
            <| fun _ ->
                let h = trusting ()
                run h "_platform.ai.list_data_types" "{}" |> ignore

                Expect.equal
                    (soleRow h).ComponentId
                    (ComponentId.value (ComponentId.forTool "_platform.ai.list_data_types"))
                    "ComponentId.forTool, resolved from the tool's own declared name"
        ]

        testList "consent — a denied read still records the attempt (36.D composition)" [

            testCase "a consent-denied read emits CrossModuleRead with Allowed = false"
            <| fun _ ->
                let h =
                    buildHarness
                        RememberPerConversation
                        (Some Denied)
                        [ "MoodJournal", [ Read ] ]
                        [ "MoodJournal" ]
                        []
                        (Some "Dashboard")

                let result = run h "_platform.ai.list_results" moodArgs
                Expect.stringContains result "UserDenied" "the 36.D refusal"

                let row = soleRow h
                Expect.isFalse row.Allowed "the acceptance criterion: the attempt is captured"
                Expect.equal row.Outcome "UserDenied" "and named as the user's decision, not a permission"
                Expect.equal row.TargetModule (Some "MoodJournal") "naming what would have been read"

            testCase "the positive twin — the same read allowed records Allowed = true"
            <| fun _ ->
                // Same harness, same module, same gates; only the user's
                // answer differs. Without this, a decorator that recorded
                // every row as denied would pass the case above.
                let h =
                    buildHarness
                        RememberPerConversation
                        (Some AllowForConversation)
                        [ "MoodJournal", [ Read ] ]
                        [ "MoodJournal" ]
                        []
                        (Some "Dashboard")

                let result = run h "_platform.ai.list_results" moodArgs
                Expect.stringContains result "ResultStoreUnavailable" "past every gate, into an absent store"

                let row = soleRow h
                Expect.equal row.Outcome "ResultStoreUnavailable" "the downstream absence, not a refusal"
                Expect.isFalse row.Allowed "a store that answered nothing did not deliver a read"

            testCase "the consent decision itself is NOT duplicated onto this row"
            <| fun _ ->
                // 36.D owns the decision stream. This row records the READ.
                // Recording the decision twice, under two event types, is
                // exactly what the Refine note ruled out.
                let h =
                    buildHarness
                        RememberPerConversation
                        (Some Denied)
                        [ "MoodJournal", [ Read ] ]
                        [ "MoodJournal" ]
                        []
                        None

                run h "_platform.ai.list_results" moodArgs |> ignore

                let fields =
                    FSharpType.GetRecordFields typeof<CrossModuleReadPayload>
                    |> Array.map _.Name
                    |> Set.ofArray

                Expect.isFalse (fields.Contains "Decision") "no consent decision on the read row"
                Expect.isFalse (fields.Contains "ConsentId") "no consent correlation on the read row"
        ]

        testList "query_entity attribution — honest about zero and many" [

            testCase "one catalogued producer resolves to one target module"
            <| fun _ ->
                let h =
                    buildHarness
                        TrustEverything
                        None
                        [ "MoodJournal", [ Read ] ]
                        [ "MoodJournal" ]
                        [ "MoodJournal", "Mood" ]
                        None

                run h "_platform.ai.query_entity" """{"entityType":"Mood"}""" |> ignore

                let row = soleRow h
                Expect.equal row.TargetModule (Some "MoodJournal") "the single producer"
                Expect.equal row.TargetModules [ "MoodJournal" ] "and the set agrees"
                Expect.equal row.QueryKey (Some "Mood") "the entity type is the discriminator"

            testCase "several producers record the SET and no single grouping key"
            <| fun _ ->
                let h =
                    buildHarness
                        TrustEverything
                        None
                        [ "MoodJournal", [ Read ]; "Payroll", [ Read ] ]
                        [ "MoodJournal"; "Payroll" ]
                        [ "MoodJournal", "Shared"; "Payroll", "Shared" ]
                        None

                run h "_platform.ai.query_entity" """{"entityType":"Shared"}""" |> ignore

                let row = soleRow h
                Expect.equal row.TargetModule None "no single module to key on — and none is invented"
                Expect.equal row.TargetModules [ "MoodJournal"; "Payroll" ] "both producers named"

            testCase "no catalogued producer records none rather than a fabricated module"
            <| fun _ ->
                // The attribution hole Phases 36.C and 36.D both record: an
                // entity type the catalogue attributes to nobody passes both
                // gates with nothing to name. The row says so.
                let h =
                    buildHarness TrustEverything None [ "MoodJournal", [ Read ] ] [ "MoodJournal" ] [] None

                run h "_platform.ai.query_entity" """{"entityType":"Orphan"}""" |> ignore

                let row = soleRow h
                Expect.equal row.TargetModule None "unattributed"
                Expect.isEmpty row.TargetModules "and honestly empty"

            testCase "an opt-in refusal on query_entity still names the producers it would have read"
            <| fun _ ->
                // Deliberately UNFILTERED by the queryability gate: filtering
                // here would record an opt-in refusal as a read of nothing,
                // which is the one rendering that makes the refusal
                // unauditable.
                let h =
                    buildHarness TrustEverything None [ "Payroll", [ Read ] ] [] [ "Payroll", "Payslip" ] None

                let result = run h "_platform.ai.query_entity" """{"entityType":"Payslip"}"""
                Expect.stringContains result "UnqueryableModule" "refused"

                let row = soleRow h
                Expect.isFalse row.Allowed "refused"
                Expect.equal row.TargetModules [ "Payroll" ] "the module it would have reached"
        ]

        testList "rollup — /dev/ai-cross-module arithmetic" [

            testCase "groups by (targetModule, toolName) with percentiles and a denial rate"
            <| fun _ ->
                let ok = AICrossModuleAudit.OkOutcome

                let rows = [
                    syntheticRow (Some "Payroll") "_platform.ai.list_results" 10.0 ok
                    syntheticRow (Some "Payroll") "_platform.ai.list_results" 20.0 ok
                    syntheticRow (Some "Payroll") "_platform.ai.list_results" 30.0 ok
                    syntheticRow (Some "Payroll") "_platform.ai.list_results" 40.0 "UserDenied"
                    // Same module, DIFFERENT tool — a separate group, because
                    // "read Payroll" and "read Payroll through this tool" are
                    // different operator facts.
                    syntheticRow (Some "Payroll") "_platform.ai.get_latest_result" 5.0 ok
                ]

                let report =
                    AICrossModuleObservability.computeReport "t1" DateTime.UtcNow (TimeSpan.FromMinutes 60.0) rows

                Expect.equal report.TotalReadsInWindow 5 "every row counted"
                Expect.equal report.DeniedInWindow 1 "one refusal"
                Expect.floatClose Accuracy.high report.DenialRate 0.2 "1 in 5"
                Expect.equal report.DistinctTargetModules 1 "one module, two tools"
                Expect.hasLength report.ByTargetAndTool 2 "one group per (module, tool) pair"

                let group =
                    report.ByTargetAndTool
                    |> List.find (fun g -> g.ToolName = "_platform.ai.list_results")

                Expect.equal group.Count 4 "group size"
                Expect.equal group.AllowedCount 3 "three allowed"
                Expect.equal group.DeniedCount 1 "one refused"
                Expect.floatClose Accuracy.high group.DenialRate 0.25 "1 in 4"
                Expect.floatClose Accuracy.high group.P50LatencyMs 20.0 "nearest-rank p50 of 10/20/30/40"
                Expect.floatClose Accuracy.high group.P95LatencyMs 40.0 "p95"
                Expect.floatClose Accuracy.high group.P99LatencyMs 40.0 "p99"
                Expect.equal group.AllowedResultBytes 300 "bytes from the ALLOWED reads only"
                Expect.equal group.Denials [ { Outcome = "UserDenied"; Count = 1 } ] "the refusal vocabulary"

            testCase "an unattributed read gets a label, never a null grouping key"
            <| fun _ ->
                let rows = [
                    syntheticRow None "_platform.ai.query_entity" 1.0 AICrossModuleAudit.OkOutcome
                ]

                let report =
                    AICrossModuleObservability.computeReport "t1" DateTime.UtcNow (TimeSpan.FromMinutes 60.0) rows

                Expect.equal
                    (report.ByTargetAndTool |> List.map _.TargetModule)
                    [ AICrossModuleObservability.UnattributedTargetLabel ]
                    "labelled, so the axis has no null key"

                Expect.equal report.DistinctTargetModules 0 "and it is not counted as a module reached"

            testCase "an empty window reports zeroes rather than dividing by none"
            <| fun _ ->
                let report =
                    AICrossModuleObservability.computeReport "t1" DateTime.UtcNow (TimeSpan.FromMinutes 60.0) []

                Expect.equal report.TotalReadsInWindow 0 "nothing read"
                Expect.equal report.DenialRate 0.0 "no denial rate over no reads"
                Expect.isEmpty report.ByTargetAndTool "no groups"

            testCase "the rollup reads the trail the decorator wrote, end to end"
            <| fun _ ->
                // The two halves meet: `reportFor` over the same `IAuditLog`
                // the tools wrote through. A decorator writing under a
                // different scope or event type would leave this at zero
                // while every emission assertion above still passed.
                let h = trusting ()
                run h "_platform.ai.list_results" moodArgs |> ignore
                run h "_platform.ai.list_accessible_modules" "{}" |> ignore

                let report =
                    AICrossModuleObservability.reportFor (Some h.AuditLog) scope.ScopeId
                    |> Async.RunSynchronously

                Expect.equal report.TotalReadsInWindow 2 "both invocations visible to the endpoint"
                Expect.equal report.WindowMinutes 60 "the shared rolling window"
        ]

        testList "Phase 9g replication — the new type reaches sinks with no sink-code change" [

            testCase "the row is written under _platform.audit and the replicator admits it"
            <| fun _ ->
                let h = trusting ()
                run h "_platform.ai.list_accessible_modules" "{}" |> ignore

                let written =
                    h.Events.ReadBySource(scope.ScopeId, AuditSourceModule.value)
                    |> Async.RunSynchronously
                    |> List.filter (fun e -> e.EventType = AICrossModuleObservability.EventType)

                match written with
                | [ evt ] ->
                    // The whole reason the row is a typed union case: an
                    // AI-tier `SourceModule` would fail this predicate and
                    // no external sink would ever see the event.
                    Expect.isTrue (AuditReplicator.shouldReplicate evt) "the replicator queues it"
                | other -> failtestf "expected one persisted audit event, got %d" (List.length other)

            testCase "it round-trips through the shipped codec registry"
            <| fun _ ->
                let h = trusting ()

                run h "_platform.ai.query_module" """{"moduleName":"Payroll","queryKey":"latest"}"""
                |> ignore

                // Decoded by `GetAuditTrail`, i.e. by the same registry the
                // replicator's batch decode uses. A missing codec row would
                // land this as `AuditEventDecodeFailed` instead.
                let row = soleRow h
                Expect.equal row.TargetModule (Some "Payroll") "fields survived the round trip"
                Expect.equal row.ToolName "_platform.ai.query_module" "including the tool"

            testCase "S3Archive writes it into the archive with no sink change"
            <| fun _ ->
                let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage

                let settings: ToolUp.Platform.AuditSinks.S3Archive.S3ArchiveSettings = {
                    Container = "audit-archive"
                    PathPrefix = None
                }

                let sink = ToolUp.Platform.AuditSinks.S3Archive.create "test-s3" settings storage

                match sink.Deliver [ sinkEnvelope ] |> Async.RunSynchronously with
                | Error msg -> failtestf "S3Archive refused the batch: %s" msg
                | Ok() ->
                    let blobs = storage.List("audit-archive", "") |> Async.RunSynchronously

                    match blobs with
                    | [ name ] ->
                        match storage.Download("audit-archive", name) |> Async.RunSynchronously with
                        | Error msg -> failtestf "archive blob unreadable: %s" msg
                        | Ok bytes ->
                            Expect.stringContains
                                (gunzip bytes)
                                AICrossModuleObservability.EventType
                                "the archived line carries the new discriminator"
                    | other -> failtestf "expected one archive blob, got %d" (List.length other)

            testCase "SplunkHec POSTs it with no sink change"
            <| fun _ ->
                let handler = new RecordingHandler()
                use httpClient = new HttpClient(handler)

                let settings: ToolUp.Platform.AuditSinks.SplunkHec.SplunkHecSettings = {
                    EndpointUrl = "https://localhost.invalid/services/collector/event"
                    Sourcetype = "toolup_audit"
                    Index = None
                    Host = None
                }

                let sink =
                    ToolUp.Platform.AuditSinks.SplunkHec.create
                        "test-splunk"
                        settings
                        (FakeSecretStore() :> ISecretStore)
                        "splunk_token"
                        httpClient

                match sink.Deliver [ sinkEnvelope ] |> Async.RunSynchronously with
                | Error msg -> failtestf "SplunkHec refused the batch: %s" msg
                | Ok() ->
                    let body = handler.RecordedBodies |> String.concat "\n"

                    Expect.stringContains
                        body
                        AICrossModuleObservability.EventType
                        "the HEC payload carries the new discriminator"

            testCase "DatadogLogs POSTs it with no sink change"
            <| fun _ ->
                let handler = new RecordingHandler()
                use httpClient = new HttpClient(handler)

                let settings: ToolUp.Platform.AuditSinks.DatadogLogs.DatadogLogsSettings = {
                    EndpointUrl = "https://localhost.invalid/api/v2/logs"
                    Service = "toolup"
                    Env = "test"
                    DdSource = "toolup_audit"
                    Host = None
                }

                let sink =
                    ToolUp.Platform.AuditSinks.DatadogLogs.create
                        "test-datadog"
                        settings
                        (FakeSecretStore() :> ISecretStore)
                        "datadog_key"
                        httpClient

                match sink.Deliver [ sinkEnvelope ] |> Async.RunSynchronously with
                | Error msg -> failtestf "DatadogLogs refused the batch: %s" msg
                | Ok() ->
                    let body = handler.RecordedBodies |> String.concat "\n"

                    Expect.stringContains
                        body
                        AICrossModuleObservability.EventType
                        "the Logs payload carries the new discriminator"

            testCase "the Phase 658 chained ledger records it with no sink change"
            <| fun _ ->
                let record = ToolUp.Platform.AuditSinks.ChainedLedger.recordOfEnvelope sinkEnvelope

                Expect.equal record.EventType AICrossModuleObservability.EventType "the ledger record's discriminator"

                Expect.isFalse (String.IsNullOrWhiteSpace record.Payload) "with a canonicalised payload to chain"

            testCase "the CEF formatter renders it rather than dropping it"
            <| fun _ ->
                let line =
                    ToolUp.Platform.AuditSinks.CefFormat.renderLine
                        ToolUp.Platform.AuditSinks.CefFormat.CefDeviceIdentity.defaults
                        sinkEnvelope

                Expect.stringContains line AICrossModuleObservability.EventType "the CEF `cat` field"

                Expect.isLessThanOrEqual
                    (Encoding.UTF8.GetByteCount line)
                    ToolUp.Platform.AuditSinks.CefFormat.MaxCefLineBytes
                    "within the CEF byte budget"
        ]

        testList "six-rule portability audit" [

            testCase "every field of CrossModuleReadPayload is a value (rule 1)"
            <| fun _ ->
                // A live handle, a server type or a platform record here
                // would make the row unrenderable by a non-.NET sink and
                // would tie the wire shape to this assembly. Checked
                // mechanically so a later field addition has to make the
                // same choice deliberately.
                let rec isValueShaped (t: Type) =
                    t = typeof<string>
                    || t = typeof<bool>
                    || t = typeof<int>
                    || t = typeof<int64>
                    || t = typeof<float>
                    || t = typeof<Guid>
                    || t = typeof<DateTime>
                    || (t.IsGenericType && t.GetGenericArguments() |> Array.forall isValueShaped)

                for field in FSharpType.GetRecordFields typeof<CrossModuleReadPayload> do
                    Expect.isTrue
                        (isValueShaped field.PropertyType)
                        $"CrossModuleReadPayload.{field.Name} is {field.PropertyType.FullName}; the audit row must stay identity-by-value"

            testCase "the emission seam is async at its boundary (rule 2)"
            <| fun _ ->
                // `audited` returns the executor's own
                // `HttpContext -> string -> Async<string>` shape, so the
                // audit write is awaited on the same async chain rather
                // than fired into a `unit`-returning side channel whose
                // completion nothing can observe.
                let decorated =
                    AICrossModuleAudit.audited
                        "_platform.ai.probe"
                        (fun _ _ -> async.Return AICrossModuleAudit.ReadTarget.enumeration)
                        (fun _ _ -> async.Return "{}")

                let ctx = DefaultHttpContext()
                ctx.RequestServices <- ServiceCollection().BuildServiceProvider()

                // No IAuditLog registered: the decorator must be a no-op
                // rather than a failure (GP 13).
                Expect.equal (decorated ctx "{}" |> Async.RunSynchronously) "{}" "result passes through untouched"

            testCase "the decorator never swallows nor invents a failure"
            <| fun _ ->
                let decorated =
                    AICrossModuleAudit.audited
                        "_platform.ai.probe"
                        (fun _ _ -> async.Return AICrossModuleAudit.ReadTarget.enumeration)
                        (fun _ _ -> async { return failwith "executor blew up" })

                let ctx = DefaultHttpContext()
                ctx.RequestServices <- ServiceCollection().BuildServiceProvider()

                Expect.throws
                    (fun () -> decorated ctx "{}" |> Async.RunSynchronously |> ignore)
                    "a throwing executor throws exactly as it did before the decorator"

            testCase "a describe that throws costs the row its target, never the call"
            <| fun _ ->
                let decorated =
                    AICrossModuleAudit.audited
                        "_platform.ai.probe"
                        (fun _ _ -> async { return failwith "describe blew up" })
                        (fun _ _ -> async.Return """{"fine":true}""")

                let ctx = DefaultHttpContext()
                ctx.RequestServices <- ServiceCollection().BuildServiceProvider()

                Expect.equal
                    (decorated ctx "{}" |> Async.RunSynchronously)
                    """{"fine":true}"""
                    "the observation never takes out the thing observed"
        ]

        testList "outcome classification" [

            testCase "the error FIELD decides, not a substring of the body"
            <| fun _ ->
                // A module's own data can contain the word PermissionDenied.
                // Recording that read as a refusal would be worse than not
                // recording it at all.
                Expect.equal
                    (AICrossModuleAudit.outcomeOf """{"content":"the PermissionDenied error means ..."}""")
                    AICrossModuleAudit.OkOutcome
                    "prose mentioning a refusal is not a refusal"

                Expect.equal
                    (AICrossModuleAudit.outcomeOf """{"error":"PermissionDenied","targetModule":"Payroll"}""")
                    "PermissionDenied"
                    "the field is"

            testCase "an empty or unparseable body reads as success, not as a refusal"
            <| fun _ ->
                for body in [ ""; "   "; "not json at all"; "[1,2,3]" ] do
                    Expect.equal
                        (AICrossModuleAudit.outcomeOf body)
                        AICrossModuleAudit.OkOutcome
                        $"'{body}' is a defect in a tool, not evidence a read was blocked"
        ]
    ]