module ToolUp.AI.AIConsentDispatch

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.AI

/// Phase 36.D — the per-conversation cross-module read consent gate.
///
/// **What this is the fourth of.** A `_platform.ai.*` read already passes
/// three gates before it reaches any module's data: the caller's per-module
/// `Read` permission (36.A), the liveness of the authority behind that
/// permission (730), and the module's own declared AI-queryability (36.C).
/// All three are answered by the DEPLOYMENT, at compose time or by an
/// administrator. None of them asks the person sitting in front of the
/// conversation whether the agent may go and read their data out of a
/// module they did not name in their prompt. This gate does, and it is
/// INNERMOST for a reason the 36.C ordering rationale already establishes:
/// a caller who fails an outer gate must be refused before any dialog is
/// shown, or the dialog itself leaks which modules a deployment exposed.
///
/// **The mechanism is the Phase 6g.A client-resident round trip, reused.**
/// A server-resident tool suspends on a `TaskCompletionSource`, the browser
/// is asked over the existing SSE stream, and a POST to `/api/ai/consent`
/// completes it — structurally the same suspend/resume `ClientToolDispatch`
/// performs for a client-resident tool, on the same per-process registry
/// shape, under the SAME timeout (see `SuspendedDispatchTimeoutMs` below).
/// It is a second CALLER of one mechanism, not a second mechanism.
///
/// Per-process singleton, with the same distributed caveat
/// `ClientToolDispatchRegistry` carries: a deployment running several silos
/// behind a load balancer needs SSE/POST affinity, or a shared registry,
/// for the decision POST to find the suspended tool.

// ─── The one suspended-dispatch budget ───────────────────────────

/// How long a suspended dispatch waits for the browser before it gives up.
///
/// **One declaration, two callers.** `AIAgentEngine`'s client-resident tool
/// dispatch reads this, and so does the consent await below. They are the
/// same wait — a server thread parked on a `TaskCompletionSource` that only
/// a browser can complete — and a second constant would be a second
/// mechanism to keep in step, which is exactly what a never-answered
/// consent prompt must not introduce: the turn has to fail cleanly on the
/// budget the deployment already has, not on a new one.
///
/// 90 s: generously longer than the client's own 30 s watchdog on the tool
/// path, so the client-side failure wins the race and the user sees the
/// specific cause rather than a generic server abort.
[<Literal>]
let SuspendedDispatchTimeoutMs = 90_000

// ─── Per-request items the gate reads ────────────────────────────

/// `HttpContext.Items` keys the agent loop stamps so a SERVER-resident tool
/// executor can reach the two things its signature does not carry.
///
/// A tool executor is `HttpContext -> argsJson -> Async<string>`: it has no
/// conversation and no way to emit on the SSE stream, because until this
/// phase no server-resident tool needed either. Widening that signature
/// would retype every tool in the SDK and every tool a consumer has
/// written, to serve one gate; stamping the items is the same stringly-keyed
/// per-request carry the loop already uses for `ToolUp.StorageScope` /
/// `ToolUp.ModulePermissions` (and which `reconstructAccessContext`
/// documents as the RBAC-correct path on the background flow).
///
/// Named through constants so the stamping end and the reading end move
/// together — the discipline Phase 730 established for the grant stamps.
module ItemsKeys =
    /// `Guid` — the conversation the current turn belongs to.
    [<Literal>]
    let ConversationId = "ToolUp.AI.ConversationId"

    /// `Guid` — the `AITask` the current turn belongs to. Rides the SSE
    /// event for parity with `ClientToolInvoke`; the registry keys on the
    /// consent id alone.
    [<Literal>]
    let TaskId = "ToolUp.AI.TaskId"

    /// `StreamEmitter` — the agent loop's own emitter, so a tool can put
    /// an event on the stream the user is already watching.
    [<Literal>]
    let StreamEmitter = "ToolUp.AI.StreamEmitter"

/// The agent loop's SSE emitter, wrapped in a nominal type.
///
/// A bare `AIStreamEvent -> unit` would box as an `FSharpFunc` and be
/// recovered by a type test over a function type — legal, but a shape
/// nothing else in the items dictionary uses and one that silently
/// matches any same-arity function. A one-field record is unambiguous at
/// the read site and gives the stamp a name to grep for.
type StreamEmitter = { Emit: AIStreamEvent -> unit }

// ─── Pending-consent registry ────────────────────────────────────

/// One suspended consent request. Recorded at the tool site BEFORE the SSE
/// event is emitted, so the decision POST can be authorised and attributed
/// from the server's own record rather than from the client's body — the
/// same reason Phase 36.A records a pending tool call's `SourceModule`.
type PendingConsent = {
    ConversationId: Guid
    TargetModule: string
    /// The scope whose blob container holds this conversation's consent
    /// record. Carried so the POST handler writes where the tool will
    /// read, without re-deriving a scope from a different request.
    Container: string
    /// The user the tool resolved when it suspended. Audit attribution.
    UserId: string
}

/// Per-process registry of suspended cross-module reads awaiting a user's
/// consent decision. Mirrors `ClientToolDispatchRegistry`.
type AIConsentRegistry() =
    let pending =
        ConcurrentDictionary<Guid, TaskCompletionSource<AllowDecision> * PendingConsent>()

    /// Register a suspended read. Returns the Task the tool awaits —
    /// completed by `/api/ai/consent`, or aborted by the caller's own
    /// timeout path.
    member _.RegisterPending(consentId: Guid, request: PendingConsent) : Task<AllowDecision> =
        let tcs =
            TaskCompletionSource<AllowDecision>(TaskCreationOptions.RunContinuationsAsynchronously)

        pending[consentId] <- (tcs, request)
        tcs.Task

    /// The request recorded for a consent id, or `None` when the id is
    /// unknown / stale / already answered. Read-only: it does NOT remove
    /// the entry, so the handler can authorise first and complete second
    /// (the ordering `/api/ai/tool-result` has to get right for the same
    /// reason).
    member _.PendingOf(consentId: Guid) : PendingConsent option =
        match pending.TryGetValue consentId with
        | true, (_, request) -> Some request
        | false, _ -> None

    /// Complete a suspended read with the user's decision. `false` when no
    /// matching request was pending — a late POST after the tool already
    /// timed out, a double-click, or a replayed body.
    member _.TryComplete(consentId: Guid, decision: AllowDecision) : bool =
        match pending.TryRemove consentId with
        | true, (tcs, _) -> tcs.TrySetResult decision
        | false, _ -> false

    /// Abandon a suspended read (the tool's own timeout path). Resolves as
    /// `Denied` rather than raising: an unanswered prompt is not an error
    /// to recover from, it is a read the user did not authorise, and the
    /// model should be told the same thing it would be told by an explicit
    /// refusal. The turn then fails cleanly on the one shared budget.
    member _.TryAbandon(consentId: Guid) : bool =
        match pending.TryRemove consentId with
        | true, (tcs, _) -> tcs.TrySetResult Denied
        | false, _ -> false

// ─── Persistence — the fourth sibling conversation blob ──────────

let private jsonOptions = FableConverters.create ()

/// `ai-conversations/{id}.consent.json`, beside the conversation's own
/// `.json` / `.history.json` / `.meta.json` siblings.
///
/// **The shard named `AIConversationState.CrossModuleAllowlist`; no such
/// type exists.** The per-conversation server state IS this family of
/// sibling blobs, so "extend the conversation state" is a fourth sibling:
/// same container, same naming, same `FableConverters` round trip. Keeping
/// it separate from the UI blob is the same argument `ConversationMeta`
/// makes — the chat panel's history read should not pull a record it never
/// renders, and a consent write should not rewrite the message list.
let consentBlobName (conversationId: Guid) =
    $"ai-conversations/{conversationId}.consent.json"

/// Read a conversation's consent record. Absent (a conversation nobody has
/// been asked about yet) and unparseable both read as empty — the safe
/// direction, since an empty record grants nothing and merely prompts.
let loadState (storage: IBlobStorage) (container: string) (conversationId: Guid) : Async<AIConsentState> = async {
    let! result = storage.Download(container, consentBlobName conversationId)

    match result with
    | Ok bytes ->
        try
            let state =
                JsonSerializer.Deserialize<AIConsentState>(Encoding.UTF8.GetString bytes, jsonOptions)

            return
                if isNull (box state.CrossModuleAllowlist) then
                    AIConsentState.empty
                else
                    state
        with _ ->
            return AIConsentState.empty
    | Error _ -> return AIConsentState.empty
}

let saveState (storage: IBlobStorage) (container: string) (conversationId: Guid) (state: AIConsentState) : Async<unit> = async {
    let bytes = JsonSerializer.Serialize(state, jsonOptions) |> Encoding.UTF8.GetBytes

    let! _ = storage.Upload(container, consentBlobName conversationId, bytes)
    return ()
}

/// Record one decision into a conversation's consent record.
///
/// Read-modify-write on a per-conversation blob. Two tool calls in one
/// parallel batch could in principle interleave here and lose one
/// another's decision; the cost of that is a re-prompt, never a spurious
/// allowance, because the value that can be lost is the WRITE and the
/// absence of a record always prompts. A stronger primitive (conditional
/// upload) exists on `IBlobStorage` but is not universally implemented
/// across the shipped backends, and paying for it to avoid an occasional
/// extra dialog would be the wrong trade.
let recordDecision
    (storage: IBlobStorage)
    (container: string)
    (conversationId: Guid)
    (targetModule: string)
    (decision: AllowDecision)
    : Async<unit> =
    async {
        let! state = loadState storage container conversationId
        do! saveState storage container conversationId (AIConsentState.record targetModule decision state)
    }

// ─── The gate ────────────────────────────────────────────────────

/// Outcome of the consent gate at one reach site.
type ConsentOutcome =
    /// The read may proceed — trusted deployment, a standing
    /// `AllowForConversation`, or a decision the user just made.
    | ConsentGranted
    /// The read must not happen. `reason` is prose written for the MODEL:
    /// it has to understand that re-planning around a different tool will
    /// not help, and that the remedy belongs to the user, not to it.
    | ConsentRefused of reason: string

[<Literal>]
let private PayloadPreviewCharCap = 240

/// A short, capped rendering of the model's own arguments for the dialog.
///
/// This is the model's REQUEST, never the module's data — the read has not
/// happened yet, so there is nothing of the user's to leak here. It is
/// capped anyway: an argument blob is model-authored text of unbounded
/// length, and a dialog is not a place to render one.
let redactPayloadPreview (raw: string) : string =
    if String.IsNullOrWhiteSpace raw then
        ""
    else
        let collapsed = raw.Replace('\r', ' ').Replace('\n', ' ').Trim()

        if collapsed.Length <= PayloadPreviewCharCap then
            collapsed
        else
            collapsed.Substring(0, PayloadPreviewCharCap) + "…"

/// The deployment's declared consent mode, read from DI.
///
/// Absent — a deployment that composed AI before this phase, or a test
/// context that never registered one — reads as the default
/// `RememberPerConversation`, which is the phase's deliberate GP 11
/// inversion. Not `TrustEverything`: an absent declaration must never be
/// the thing that turns the gate off.
let resolveMode (ctx: HttpContext) : AIConsentMode =
    match ctx.RequestServices.GetService typeof<AIConsentMode> with
    | :? AIConsentMode as mode -> mode
    | _ -> RememberPerConversation

let private guidItem (ctx: HttpContext) (key: string) : Guid option =
    match ctx.Items.TryGetValue key with
    | true, (:? Guid as value) -> Some value
    | _ -> None

let private emitterOf (ctx: HttpContext) : StreamEmitter option =
    match ctx.Items.TryGetValue ItemsKeys.StreamEmitter with
    | true, (:? StreamEmitter as emitter) -> Some emitter
    | _ -> None

let private containerOf (ctx: HttpContext) : string option =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as scope) -> Some scope.Container
    | _ -> None

let private userIdOf (ctx: HttpContext) : string =
    match ctx.Items.TryGetValue "ToolUp.UserId" with
    | true, (:? string as id) -> id
    | _ -> "anonymous"

/// The standing refusal text, written for the model: the remedy is the
/// user's, so re-planning the same read through another tool is wasted
/// turns, and it is NOT a permission the deployment can grant.
let private userDeniedMessage (moduleName: string) =
    sprintf
        "The user did not allow this conversation to read from module '%s'. This is the user's own decision, not a permission or a deployment setting — do not retry the same read, and do not treat it as PermissionDenied. Answer from what you already have, say plainly that you could not read '%s', or ask the user whether they want to allow it."
        moduleName
        moduleName

/// Ask the user, if they have not already answered, before reading from
/// `targetModule` in this conversation.
///
/// Call it at a reach site AFTER the caller-side gates and the
/// queryability gate — it is the innermost of the four, and an outer
/// refusal must never surface a dialog.
///
/// **What makes it cost nothing when unused (GP 13).** `TrustEverything`
/// short-circuits before any I/O. So does an agent-loop context with no
/// conversation stamped (a contract-pack call, a probe, any caller that is
/// not a live turn): there is no conversation to remember a decision
/// against and no stream to ask on, so the gate has nothing to say and
/// says nothing rather than failing closed on infrastructure the caller
/// never had.
let requireConsent
    (ctx: HttpContext)
    (toolName: string)
    (targetModule: string)
    (intendedQueryKey: string)
    (argsJson: string)
    : Async<ConsentOutcome> =
    async {
        let mode = resolveMode ctx

        match mode with
        | TrustEverything -> return ConsentGranted
        | AlwaysAsk
        | RememberPerConversation ->
            let registry =
                match ctx.RequestServices.GetService typeof<AIConsentRegistry> with
                | :? AIConsentRegistry as r -> Some r
                | _ -> None

            let storage =
                match ctx.RequestServices.GetService typeof<IBlobStorage> with
                | :? IBlobStorage as s -> Some s
                | _ -> None

            match guidItem ctx ItemsKeys.ConversationId, emitterOf ctx, registry, containerOf ctx, storage with
            | Some conversationId, Some emitter, Some registry, Some container, Some storage ->
                // A standing decision short-circuits the prompt — but only
                // in `RememberPerConversation`. `AlwaysAsk` neither reads
                // nor trusts the record: every read is its own decision.
                let! standing =
                    match mode with
                    | RememberPerConversation -> async {
                        let! state = loadState storage container conversationId
                        return AIConsentState.decisionFor targetModule state
                      }
                    | _ -> async.Return None

                match standing with
                | Some decision when AIConsentState.satisfies decision -> return ConsentGranted
                | Some Denied -> return ConsentRefused(userDeniedMessage targetModule)
                | _ ->
                    // Prompt. Register BEFORE emitting, so a decision that
                    // arrives on a fast local round trip cannot find an
                    // unregistered id.
                    let consentId = Guid.NewGuid()

                    let taskId = guidItem ctx ItemsKeys.TaskId |> Option.defaultValue Guid.Empty

                    let awaited =
                        registry.RegisterPending(
                            consentId,
                            {
                                ConversationId = conversationId
                                TargetModule = targetModule
                                Container = container
                                UserId = userIdOf ctx
                            }
                        )

                    emitter.Emit(
                        AIConsentRequired(
                            taskId,
                            consentId,
                            conversationId,
                            toolName,
                            targetModule,
                            intendedQueryKey,
                            redactPayloadPreview argsJson
                        )
                    )

                    let timeoutTask = Task.Delay SuspendedDispatchTimeoutMs
                    let! winner = Task.WhenAny(awaited :> Task, timeoutTask) |> Async.AwaitTask

                    if winner = (awaited :> Task) then
                        let! decision = awaited |> Async.AwaitTask

                        match decision with
                        | Denied -> return ConsentRefused(userDeniedMessage targetModule)
                        | AllowOnce
                        | AllowForConversation -> return ConsentGranted
                    else
                        registry.TryAbandon consentId |> ignore

                        return
                            ConsentRefused(
                                sprintf
                                    "The consent prompt for module '%s' was not answered within %d seconds, so the read did not happen. Do not retry it; continue without that data and say so."
                                    targetModule
                                    (SuspendedDispatchTimeoutMs / 1000)
                            )
            | _ ->
                // No live turn to ask on (no conversation stamped, no
                // emitter, no registry, or no blob storage to remember a
                // decision in). Nothing to prompt and nothing to persist,
                // so this gate abstains rather than refusing: the three
                // gates outside it have already run and are what protect
                // the read. A composed deployment always has all five —
                // the loop stamps the first three, `composeAI` registers
                // the fourth and core `compose` the fifth.
                return ConsentGranted
    }