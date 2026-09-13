// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ToolApprovalTests

// ─── Phase 503 — human-in-the-loop tool approval ─────────────────
//
// The acceptance has five claims, and each is asserted here against the
// real agent loop rather than against the gate in isolation:
//
//   1. A tool the policy holds SUSPENDS: the prompt is emitted and the
//      tool's own body has not run at the moment it is emitted.
//   2. Approving RESUMES it end to end — the body runs, its result
//      reaches the model.
//   3. Rejecting ABORTS the turn with no side effect: the body never
//      runs, the model is told it was denied, and the refusal reaches
//      the existing denial stream Phase 47's rollup reads.
//   4. An unanswered prompt aborts on the budget, with the same
//      no-side-effect property as an explicit rejection.
//   5. No policy composed ⇒ the turn is unchanged. Asserted by running
//      the same scenario twice — once with nothing registered, once with
//      a policy that requires nothing — and comparing the event streams.
//
// Two further claims the phase rests on are asserted because nothing
// else would catch them breaking: an action an OUTER gate refuses never
// reaches a prompt (the ordering the whole gate stack depends on), and
// the gate covers SERVER-resident tools (which is the point — the
// actions this phase exists for are server-resident, and the
// client-resident allowlist seam the shard proposed extending could not
// have reached them).

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI

// ─── Test-only fakes ──────────────────────────────────────────────────

/// A policy driven by a predicate over the tool name, so each case
/// states its own rule inline.
type private PredicatePolicy(requires: string -> bool) =
    interface IToolApprovalPolicy with
        member _.Requires(toolName, _sourceModule, _argsJson, _activeModule, _activePage) =
            if requires toolName then
                ApprovalRequired {
                    Summary = $"Approve running '{toolName}'?"
                    Detail = "This is a test-only policy."
                }
            else
                ApprovalNotRequired

/// A policy that raises. Documented as forbidden; asserted here because
/// "must not throw" without a test is a comment, and the consequence of
/// a defective policy has to be a HOLD rather than a run.
type private ThrowingPolicy() =
    interface IToolApprovalPolicy with
        member _.Requires(_toolName, _sourceModule, _argsJson, _activeModule, _activePage) =
            failwith "test-only defective policy"

/// Deny-all allowlist, to prove the ordering: a client-resident call the
/// allowlist refuses must never reach an approval prompt.
type private DenyAllAuthorizer() =
    interface IClientToolAuthorizer with
        member _.Authorize(_toolName, _argsJson, _activeModule, _activePage) = Deny "deny-all (test-only authorizer)"

/// Minimal `IAIProvider` that emits one tool call on the first
/// `SendMessage` and ends the conversation on the second.
type private CannedToolCallProvider(toolNameToCall: string, argsJson: string) =
    let mutable callCount = 0

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "test-canned"
            Model = "test-canned-model"
        }

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            callCount <- callCount + 1

            if callCount = 1 then
                return
                    Ok {
                        Content = ""
                        ToolCalls = [
                            {
                                Id = "00000000-0000-0000-0000-0000000000ff"
                                Name = toolNameToCall
                                Arguments = argsJson
                            }
                        ]
                        StopReason = "tool_use"
                        Usage = None
                    }
            else
                return
                    Ok {
                        Content = "done"
                        ToolCalls = []
                        StopReason = "end_turn"
                        Usage = None
                    }
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

// ─── Harness ─────────────────────────────────────────────────────────

let private ToolName = "_test.consequential.write"
let private SourceModule = "test-module"
let private ArgsJson = """{"target":"everything"}"""

/// Build a registry holding one tool at `location`, with a flag that
/// records whether its body ever ran. The flag is the no-side-effect
/// assertion: a refused invocation must leave it false.
let private buildToolRegistry (location: AIToolLocation) (ran: bool ref) =
    let registry = AIToolRegistry.AIToolRegistry()

    let definition: AIToolDefinition = {
        Name = ToolName
        Description = "Test-only tool standing in for a consequential action"
        Parameters = []
        SourceModule = SourceModule
        EmitsActions = None
        Location = location
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
    }

    let executor _ctx _argsJson = async {
        ran.Value <- true
        return """{"ok":true}"""
    }

    registry.RegisterAll [ AIToolRegistry.createTool definition executor ]
    registry

let private buildHttpContext
    (eventStore: IEventStore)
    (policy: IToolApprovalPolicy option)
    (authorizer: IClientToolAuthorizer option)
    (approvalRegistry: ToolApprovalDispatch.ToolApprovalRegistry option)
    : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<IEventStore>(eventStore) |> ignore

    match policy with
    | Some p -> services.AddSingleton<IToolApprovalPolicy>(p) |> ignore
    | None -> ()

    match authorizer with
    | Some a -> services.AddSingleton<IClientToolAuthorizer>(a) |> ignore
    | None -> ()

    match approvalRegistry with
    | Some r -> services.AddSingleton<ToolApprovalDispatch.ToolApprovalRegistry>(r) |> ignore
    | None -> ()

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx :> HttpContext

/// Run one turn, capturing every SSE event. `onApproval` is invoked with
/// the approval id the moment a prompt is emitted, so a case can answer
/// it from inside the emission the way the browser answers it from
/// inside the stream.
let private runTurn
    (ctx: HttpContext)
    (registry: AIToolRegistry.AIToolRegistry)
    (onApproval: Guid -> unit)
    : Async<AIStreamEvent list> =
    async {
        let events = ResizeArray<AIStreamEvent>()

        let onEvent (evt: AIStreamEvent) =
            lock events (fun () -> events.Add evt)

            match evt with
            | ToolApprovalRequired(_, approvalId, _, _, _, _, _, _) -> onApproval approvalId
            | _ -> ()

        let! _ =
            AIAgentEngine.runAgentLoop
                (CannedToolCallProvider(ToolName, ArgsJson) :> IAIProvider)
                registry
                (ClientToolDispatch.ClientToolDispatchRegistry())
                ctx
                (Guid.NewGuid())
                (Guid.NewGuid())
                AISurface.FullPage
                (Some "SalesAnalysis")
                (Some "/dashboard")
                CancellationToken.None
                [ AIProviderMessage.text "user" "delete everything please" ]
                None
                onEvent

        return lock events (fun () -> events.ToArray() |> Array.toList)
    }

let private approvalPrompts (events: AIStreamEvent list) =
    events
    |> List.choose (function
        | ToolApprovalRequired(_, approvalId, _, toolName, sourceModule, summary, detail, preview) ->
            Some(approvalId, toolName, sourceModule, summary, detail, preview)
        | _ -> None)

let private toolResults (events: AIStreamEvent list) =
    events
    |> List.choose (function
        | ToolCallCompleted(_, _, content) -> Some content
        | _ -> None)

/// Event kinds only — the comparison axis for the GP 11 claim. Guids
/// differ between runs by construction, so the assertion is over the
/// SHAPE of the stream plus the tool-result payloads, which is what a
/// deployment observes.
let private eventKinds (events: AIStreamEvent list) =
    events
    |> List.map (function
        | MessageDelta _ -> "MessageDelta"
        | MessageComplete _ -> "MessageComplete"
        | ToolCallStarted _ -> "ToolCallStarted"
        | ToolCallCompleted _ -> "ToolCallCompleted"
        | StreamError _ -> "StreamError"
        | StreamCancelled _ -> "StreamCancelled"
        | ClientToolInvoke _ -> "ClientToolInvoke"
        | AnswerVerified _ -> "AnswerVerified"
        | AIConsentRequired _ -> "AIConsentRequired"
        | ToolApprovalRequired _ -> "ToolApprovalRequired")

let private auditRows (store: IEventStore) (source: string) = async {
    let! rows = store.ReadBySource("anonymous", source)
    return rows |> Seq.toList
}

let private noOpLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

// ─── Tests ───────────────────────────────────────────────────────────

let tests =
    testList "Phase 503 — human-in-the-loop tool approval" [

        testCaseAsync "a held invocation suspends: the prompt is emitted and the tool body has NOT run"
        <| async {
            let ran = ref false
            let observedRanAtPrompt = ref true
            let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let approvals = ToolApprovalDispatch.ToolApprovalRegistry()

            let ctx =
                buildHttpContext store (Some(PredicatePolicy(fun _ -> true))) None (Some approvals)

            let! events =
                runTurn ctx (buildToolRegistry ServerResident ran) (fun approvalId ->
                    // The moment the question reaches the stream, the
                    // action must not yet have happened. This is the
                    // whole claim of the phase; reading the flag here is
                    // the only place it can be observed.
                    observedRanAtPrompt.Value <- ran.Value
                    approvals.TryComplete(approvalId, Rejected) |> ignore)

            let prompts = approvalPrompts events
            Expect.hasLength prompts 1 "exactly one approval prompt for one held invocation"

            Expect.isFalse
                observedRanAtPrompt.Value
                "the tool body must NOT have run at the moment the approval prompt was emitted — a prompt raised after the action is not a gate"

            let (_, toolName, sourceModule, summary, _, preview) = prompts |> List.head
            Expect.equal toolName ToolName "the prompt names the tool"
            Expect.equal sourceModule SourceModule "the prompt names the declaring module"
            Expect.stringContains summary ToolName "the policy's own summary reaches the dialog"

            Expect.stringContains
                preview
                "everything"
                "the prompt carries the model's own arguments — the user is approving a call, not a tool name"
        }

        testCaseAsync "approving resumes the invocation end to end"
        <| async {
            let ran = ref false
            let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let approvals = ToolApprovalDispatch.ToolApprovalRegistry()

            let ctx =
                buildHttpContext store (Some(PredicatePolicy(fun _ -> true))) None (Some approvals)

            let! events =
                runTurn ctx (buildToolRegistry ServerResident ran) (fun approvalId ->
                    approvals.TryComplete(approvalId, Approved) |> ignore)

            Expect.hasLength (approvalPrompts events) 1 "one prompt"
            Expect.isTrue ran.Value "the tool body runs once the user approves"

            let results = toolResults events

            Expect.isTrue
                (results |> List.exists (fun r -> r.Contains "\"ok\""))
                "the tool's own result reaches the model after approval"

            let! granted = auditRows store ToolApprovalDispatch.ApprovalAuditSource

            let types = granted |> List.map _.EventType |> List.distinct |> List.sort

            Expect.equal
                types
                [
                    ToolApprovalDispatch.ApprovalGrantedEvent
                    ToolApprovalDispatch.ApprovalRequestedEvent
                ]
                "the request is audited when it is raised and the grant when it is answered"
        }

        testCaseAsync "rejecting aborts the turn with no side effect, and reaches the existing denial stream"
        <| async {
            let ran = ref false
            let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let approvals = ToolApprovalDispatch.ToolApprovalRegistry()

            let ctx =
                buildHttpContext store (Some(PredicatePolicy(fun _ -> true))) None (Some approvals)

            let! events =
                runTurn ctx (buildToolRegistry ServerResident ran) (fun approvalId ->
                    approvals.TryComplete(approvalId, Rejected) |> ignore)

            Expect.isFalse ran.Value "a rejected invocation never runs — that is the no-side-effect claim"

            Expect.isTrue
                (toolResults events |> List.exists (fun r -> r.Contains "was denied:"))
                "the model is told the call was denied, in the typed Denied rendering it already understands"

            // The refusal rides the EXISTING denial family rather than a
            // parallel one, so Phase 47's /dev/ai-allowlist rollup, the
            // IAIDenialRollupProbe panel and the sustained-denial rate
            // monitor all see it without a second reader.
            let! denials = auditRows store "_platform.ai.tool_allowlist_denial"

            Expect.isNonEmpty
                denials
                "a refused approval must appear in the denial stream the existing observability reads"

            Expect.isTrue
                (denials |> List.forall (fun e -> e.EventType = "ToolAllowlistDenied"))
                "on the existing event type — a parallel denial family would be invisible to the rollup"
        }

        testCaseAsync "an unanswered prompt aborts on the budget, with the same no-side-effect property"
        <| async {
            // The budget is a parameter precisely so this path can be
            // exercised: at the shared 90 s value it is the one arm of
            // the gate a suite cannot afford to run, and an untested
            // refusal path is the one that rots.
            let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let approvals = ToolApprovalDispatch.ToolApprovalRegistry()

            let ctx =
                buildHttpContext store (Some(PredicatePolicy(fun _ -> true))) None (Some approvals)

            ctx.Items[AIConsentDispatch.ItemsKeys.ConversationId] <- box (Guid.NewGuid())
            ctx.Items[AIConsentDispatch.ItemsKeys.TaskId] <- box (Guid.NewGuid())

            ctx.Items[AIConsentDispatch.ItemsKeys.StreamEmitter] <-
                box ({ Emit = ignore }: AIConsentDispatch.StreamEmitter)

            let! outcome =
                ToolApprovalDispatch.requireApprovalWithin
                    50
                    ctx
                    noOpLogger
                    ToolName
                    SourceModule
                    ArgsJson
                    (Some "SalesAnalysis")
                    (Some "/dashboard")

            match outcome with
            | ToolApprovalDispatch.ApprovalGranted ->
                failtest "an unanswered approval prompt must NOT grant — the whole gate turns on this"
            | ToolApprovalDispatch.ApprovalRefused reason ->
                Expect.stringContains reason "not answered" "the model is told nobody answered"
                Expect.stringContains reason "did NOT run" "and that the action did not happen"

            let! rows = auditRows store ToolApprovalDispatch.ApprovalAuditSource

            Expect.isTrue
                (rows
                 |> List.exists (fun e -> e.EventType = ToolApprovalDispatch.ApprovalExpiredEvent))
                "an expired prompt is audited — an unanswered consequential action is exactly the thing an operator asks about later"
        }

        testCaseAsync "no policy composed ⇒ the turn is unchanged (GP 11)"
        <| async {
            // The strongest comparison available from inside the tree:
            // run the same scenario with the gate structurally absent and
            // with a policy that requires nothing, and assert the two
            // observable streams are identical. A gate that is inert when
            // unused cannot tell those two apart, and neither can a
            // deployment.
            let ranA = ref false
            let storeA = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let ctxA = buildHttpContext storeA None None None
            let! eventsA = runTurn ctxA (buildToolRegistry ServerResident ranA) ignore

            let ranB = ref false
            let storeB = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let ctxB = buildHttpContext storeB (Some(PredicatePolicy(fun _ -> false))) None None

            let! eventsB = runTurn ctxB (buildToolRegistry ServerResident ranB) ignore

            Expect.isTrue ranA.Value "the tool runs when no policy is composed"
            Expect.isTrue ranB.Value "and when the policy requires nothing"

            Expect.isEmpty (approvalPrompts eventsA) "no prompt without a policy"
            Expect.isEmpty (approvalPrompts eventsB) "no prompt when the policy requires nothing"

            Expect.equal
                (eventKinds eventsB)
                (eventKinds eventsA)
                "the event stream is identical with and without a policy that requires nothing — the gate is inert when unused"

            Expect.equal (toolResults eventsB) (toolResults eventsA) "and so are the tool results the model sees"

            let! rowsA = auditRows storeA ToolApprovalDispatch.ApprovalAuditSource
            let! rowsB = auditRows storeB ToolApprovalDispatch.ApprovalAuditSource
            Expect.isEmpty rowsA "no audit rows without a policy"
            Expect.isEmpty rowsB "and none when nothing was held — there is no decision to record"
        }

        testCaseAsync "an action an OUTER gate refuses never reaches an approval prompt"
        <| async {
            // The ordering the whole stack depends on. A client-resident
            // call the static allowlist denies must be refused BEFORE the
            // approval gate is consulted — a dialog for an action the
            // deployment already forbids would leak what it exposes and
            // teach the user to click through.
            let ran = ref false
            let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let approvals = ToolApprovalDispatch.ToolApprovalRegistry()

            let ctx =
                buildHttpContext
                    store
                    (Some(PredicatePolicy(fun _ -> true)))
                    (Some(DenyAllAuthorizer() :> IClientToolAuthorizer))
                    (Some approvals)

            let! events = runTurn ctx (buildToolRegistry ClientResident ran) ignore

            Expect.isEmpty
                (approvalPrompts events)
                "the allowlist deny short-circuits before the approval gate — an outer refusal must never surface a prompt"

            Expect.isEmpty
                (events
                 |> List.filter (function
                     | ClientToolInvoke _ -> true
                     | _ -> false))
                "and no client-resident dispatch was emitted either"

            let! rows = auditRows store ToolApprovalDispatch.ApprovalAuditSource
            Expect.isEmpty rows "nothing was held, so nothing is recorded on the approval trail"
        }

        testCaseAsync "the gate covers SERVER-resident tools, which the client-resident allowlist seam could not"
        <| async {
            // Stated as its own case because it is the premise this phase
            // corrected. `IClientToolAuthorizer` is consulted only inside
            // the ClientResident arm, so extending its decision — what the
            // shard asked for — would have delivered approval for exactly
            // the tools that do not need it. The actions this phase exists
            // for (writes, deletions, spend, outbound calls) are
            // server-resident, and this asserts they are reachable.
            let ran = ref false
            let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let approvals = ToolApprovalDispatch.ToolApprovalRegistry()

            let ctx =
                buildHttpContext store (Some(PredicatePolicy(fun name -> name = ToolName))) None (Some approvals)

            let! events =
                runTurn ctx (buildToolRegistry ServerResident ran) (fun approvalId ->
                    approvals.TryComplete(approvalId, Rejected) |> ignore)

            Expect.hasLength (approvalPrompts events) 1 "a server-resident tool is held for approval"
            Expect.isFalse ran.Value "and refusing it stops it running"
        }

        testCaseAsync "a policy that requires approval with nowhere to ask REFUSES rather than running"
        <| async {
            // The fail-closed asymmetry against 36.D's consent gate,
            // which abstains in the same situation. Consent sits behind
            // three gates that have already permitted the read; here the
            // deployment has affirmatively declared THIS invocation
            // consequential, so running it because nobody is listening is
            // precisely the outcome the declaration forbids.
            let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let ctx = buildHttpContext store (Some(PredicatePolicy(fun _ -> true))) None None

            let! outcome = ToolApprovalDispatch.requireApproval ctx noOpLogger ToolName SourceModule ArgsJson None None

            match outcome with
            | ToolApprovalDispatch.ApprovalGranted ->
                failtest "with no way to ask, an approval-requiring invocation must be refused, never run"
            | ToolApprovalDispatch.ApprovalRefused reason ->
                Expect.stringContains reason "did not run" "the model is told the action did not happen"
        }

        testCaseAsync "a policy that raises HOLDS the invocation rather than letting it through"
        <| async {
            let ran = ref false
            let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let approvals = ToolApprovalDispatch.ToolApprovalRegistry()
            let ctx = buildHttpContext store (Some(ThrowingPolicy())) None (Some approvals)

            let! events =
                runTurn ctx (buildToolRegistry ServerResident ran) (fun approvalId ->
                    approvals.TryComplete(approvalId, Rejected) |> ignore)

            Expect.hasLength
                (approvalPrompts events)
                1
                "a policy that cannot answer must not be read as a yes — the invocation is held"

            Expect.isFalse ran.Value "and it does not run when the held prompt is refused"
        }

        testCase "the decision token vocabulary fails closed"
        <| fun () ->
            Expect.equal (ToolApprovalDecision.ofToken "Approved") Approved "the approval token round-trips"
            Expect.equal (ToolApprovalDecision.ofToken "Rejected") Rejected "and so does the rejection"

            Expect.equal
                (ToolApprovalDecision.ofToken "AllowForConversation")
                Rejected
                "a token from another vocabulary is a rejection, never an approval"

            Expect.equal (ToolApprovalDecision.ofToken null) Rejected "and so is a null body"

            Expect.equal
                (ToolApprovalDecision.ofToken (ToolApprovalDecision.toToken Approved))
                Approved
                "toToken and ofToken agree"

        testCase "the arguments digest is stable, and distinguishes arguments the preview would not"
        <| fun () ->
            let a = ToolApprovalDispatch.argumentsDigest """{"target":"everything"}"""
            let b = ToolApprovalDispatch.argumentsDigest """{"target":"everything"}"""
            let c = ToolApprovalDispatch.argumentsDigest """{"target":"one-row"}"""

            Expect.equal a b "the same arguments digest the same way"
            Expect.notEqual a c "different arguments do not"
            Expect.stringStarts a "sha256:" "the estate's one digest convention"
            Expect.equal (a.Length) (7 + 64) "sha256: plus 64 hex digits"

            Expect.equal
                (ToolApprovalDispatch.argumentsDigest null)
                (ToolApprovalDispatch.argumentsDigest "")
                "a null argument blob digests as the empty one rather than raising"

        testCaseAsync "the suspended-prompt mechanism abandons on its budget and resolves as the caller's fallback"
        <| async {
            // The shared primitive, asserted directly: 36.D's consent
            // await and this phase's approval hold are two callers of it,
            // so its race and its abandonment are worth pinning once
            // rather than twice.
            let registry = SuspendedPrompt.PendingPromptRegistry<ToolApprovalDecision, string>()

            let promptId = Guid.NewGuid()
            let awaited = registry.RegisterPending(promptId, "pending-record")

            Expect.equal
                (registry.PendingOf promptId)
                (Some "pending-record")
                "the record is readable without removing it"

            Expect.equal
                (registry.PendingOf promptId)
                (Some "pending-record")
                "twice — a read must not consume the entry"

            let! answered =
                SuspendedPrompt.awaitDecisionWithin 50 awaited (fun () ->
                    registry.TryAbandon(promptId, Rejected) |> ignore)

            Expect.equal answered None "an unanswered prompt reports no decision"
            Expect.equal (registry.PendingOf promptId) None "and the entry is gone — nothing is left parked forever"

            let second = Guid.NewGuid()
            let awaitedSecond = registry.RegisterPending(second, "another")
            Expect.isTrue (registry.TryComplete(second, Approved)) "a matching completion succeeds"
            let! decision = awaitedSecond |> Async.AwaitTask
            Expect.equal decision Approved "and delivers the decision to the parked caller"
            Expect.isFalse (registry.TryComplete(second, Approved)) "a replayed completion finds nothing"
        }
    ]