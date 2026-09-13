// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.ToolApprovalDispatch

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.AI

/// Phase 503 — the human-in-the-loop approval gate.
///
/// **What this is the fifth of, and why it is not a sixth mechanism.**
/// A tool call already passes four gates before it runs: the caller's
/// per-module `Read` permission (36.A), the liveness of the authority
/// behind it (730), the module's declared AI-queryability (36.C, inside
/// the `_platform.ai.*` tools) and — for a client-resident invocation —
/// the deployment's static allowlist (46). All four are answered by the
/// deployment ahead of time, and all four can only say yes or no. This
/// one asks the person sitting in front of the conversation, about the
/// invocation in front of them, with its arguments on screen.
///
/// The round trip is Phase 6g.A's suspend/resume, generalised by
/// [`SuspendedPrompt`](SuspendedPrompt.fs) when this became its third
/// caller: register, emit on the stream the user is already watching,
/// park on a `TaskCompletionSource`, and resume when
/// `/api/ai/tool-approval` completes it — or refuse when the one shared
/// 90 s budget elapses. The turn state the shard called
/// `AwaitingApproval` IS that parked continuation; there is no separate
/// loop state to hold, and inventing one would be the second mechanism
/// this file exists to avoid.
///
/// **Where it sits, and why the placement is the point.** At the agent
/// loop's dispatch site, AFTER the RBAC / grant / allowlist arms and
/// BEFORE the server-vs-client routing. After, so nothing an outer gate
/// refused can reach a prompt — a dialog for an action the deployment
/// already forbids would leak what it exposes and train the user to
/// click through. Before the routing, so it covers BOTH locations: the
/// actions this phase exists for — writes, deletions, spend, outbound
/// calls — are server-resident, and the allowlist seam it might
/// otherwise have ridden is client-resident only.

// ─── Audit ───────────────────────────────────────────────────────

/// The audit source every approval request and decision is recorded
/// under. One source, four event types, for the reason Phase 36.A gave
/// the RBAC denial its own stream: an operator asking "what has the
/// assistant been held for, and what did people say" reads one stream and
/// gets every answer.
///
/// A REFUSAL additionally writes the existing
/// `_platform.ai.tool_allowlist_denial` row from the agent loop, so it
/// reaches Phase 47's `/dev/ai-allowlist` rollup, the
/// `IAIDenialRollupProbe` admin panel and the sustained-denial rate
/// monitor. That is deliberately not a parallel denial family: the
/// refusal rides the denial stream the estate already has, and this
/// stream carries the DECISION record the acceptance asks for.
[<Literal>]
let ApprovalAuditSource = "_platform.ai.tool_approval"

/// A held invocation was put in front of the user.
[<Literal>]
let ApprovalRequestedEvent = "ToolApprovalRequested"

/// The user approved it.
[<Literal>]
let ApprovalGrantedEvent = "ToolApprovalGranted"

/// The user refused it.
[<Literal>]
let ApprovalRejectedEvent = "ToolApprovalRejected"

/// Nobody answered inside the shared budget, so it was refused.
[<Literal>]
let ApprovalExpiredEvent = "ToolApprovalExpired"

let private jsonOptions = FableConverters.create ()

// ─── The pending record ──────────────────────────────────────────

/// One held invocation, as the server recorded it when it suspended.
///
/// Recorded BEFORE the SSE event is emitted, so the decision POST can be
/// authorised and attributed from this rather than from the client's
/// body — the same reason Phase 36.A records a pending tool call's
/// `SourceModule`.
type PendingApproval = {
    /// The conversation the held turn belongs to. Audit attribution.
    ConversationId: Guid
    /// The tool the model asked for, by its canonical registry name.
    ToolName: string
    /// The tool's declaring module. The axis the decision POST
    /// re-authorises on, exactly as `/api/ai/tool-result` re-authorises a
    /// client-resident completion.
    SourceModule: string
    /// `sha256:`-prefixed digest of the exact argument JSON the user was
    /// shown. Correlates the decision with the invocation without copying
    /// the arguments into a second place.
    ArgumentsDigest: string
    /// The capped, control-stripped rendering of those arguments that the
    /// dialog showed. Audited so the record says what the user actually
    /// read, not merely what was asked.
    ArgumentsPreview: string
    /// The scope the audit rows are written under.
    ScopeId: string
    /// The user the gate resolved when it suspended. Audit attribution.
    UserId: string
}

/// Per-process registry of tool invocations held awaiting a user's
/// approval decision.
///
/// A domain-named facade over the one suspended-dispatch mechanism
/// (`SuspendedPrompt.PendingPromptRegistry`) rather than a third copy of
/// it: the members below are the vocabulary this round trip is discussed
/// in, and the plumbing under them is shared with the consent round trip
/// so a fix to one is a fix to both.
type ToolApprovalRegistry() =
    let inner =
        SuspendedPrompt.PendingPromptRegistry<ToolApprovalDecision, PendingApproval>()

    /// Register a held invocation. Returns the Task the gate awaits —
    /// completed by `/api/ai/tool-approval`, or abandoned by the gate's
    /// own timeout path.
    member _.RegisterPending(approvalId: Guid, request: PendingApproval) =
        inner.RegisterPending(approvalId, request)

    /// What was recorded for an approval id, or `None` when the id is
    /// unknown / stale / already answered. Read-only, so the handler can
    /// authorise before it completes.
    member _.PendingOf(approvalId: Guid) : PendingApproval option = inner.PendingOf approvalId

    /// Complete a held invocation with the user's decision.
    member _.TryComplete(approvalId: Guid, decision: ToolApprovalDecision) : bool =
        inner.TryComplete(approvalId, decision)

    /// Abandon a held invocation on the gate's own timeout path. Resolves
    /// as `Rejected`: an unanswered prompt is not an error to recover
    /// from, it is an action the user did not authorise, and it must have
    /// exactly the effect an explicit refusal has.
    member _.TryAbandon(approvalId: Guid) : bool = inner.TryAbandon(approvalId, Rejected)

// ─── Outcome ─────────────────────────────────────────────────────

/// Outcome of the approval gate at the dispatch site.
type ApprovalOutcome =
    /// Run the invocation — no policy composed, no requirement declared
    /// for it, or a user who approved it.
    | ApprovalGranted
    /// Do not run it. `reason` is prose written for the MODEL: it has to
    /// understand that the refusal is the user's and that re-planning the
    /// same action will not help.
    | ApprovalRefused of reason: string

// ─── Helpers ─────────────────────────────────────────────────────

/// `sha256:`-prefixed lowercase-hex digest of an argument blob.
///
/// The audit carries the digest as well as the capped preview because the
/// two answer different questions: the preview says what the user read,
/// the digest says — exactly, and at any length — which invocation it
/// was, so a decision row can be matched to a tool-call row without
/// storing the arguments twice.
let argumentsDigest (argsJson: string) : string =
    let raw = if isNull argsJson then "" else argsJson
    let bytes = SHA256.HashData(Encoding.UTF8.GetBytes raw)
    "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant()

/// The deployment's approval policy, read from DI.
///
/// Absent — which is every deployment that has not composed one — means
/// nothing is ever held, and the gate returns before any other lookup,
/// any hashing and any I/O. That single failed `GetService` per tool call
/// is the whole cost of this phase to a deployment that does not use it
/// (GP 13).
let resolvePolicy (ctx: HttpContext) : IToolApprovalPolicy option =
    match ctx.RequestServices.GetService typeof<IToolApprovalPolicy> with
    | :? IToolApprovalPolicy as policy -> Some policy
    | _ -> None

let private guidItem (ctx: HttpContext) (key: string) : Guid option =
    match ctx.Items.TryGetValue key with
    | true, (:? Guid as value) -> Some value
    | _ -> None

let private emitterOf (ctx: HttpContext) : AIConsentDispatch.StreamEmitter option =
    match ctx.Items.TryGetValue AIConsentDispatch.ItemsKeys.StreamEmitter with
    | true, (:? AIConsentDispatch.StreamEmitter as emitter) -> Some emitter
    | _ -> None

/// The standing refusal text, written for the model.
let private userRejectedMessage (toolName: string) =
    sprintf
        "The user was asked to approve this '%s' call and refused it. This is the user's own decision about a consequential action, not a permission and not a deployment setting — do not retry it, do not route the same action through another tool, and do not treat it as PermissionDenied. Tell the user plainly that you did not perform it, and ask what they would like instead."
        toolName

let private unansweredMessage (toolName: string) =
    sprintf
        "The approval prompt for this '%s' call was not answered within %d seconds, so it did NOT run and nothing was changed. Do not retry it. Say plainly that the action is still outstanding and that it needs the user's confirmation."
        toolName
        (SuspendedPrompt.SuspendedDispatchTimeoutMs / 1000)

/// The refusal used when a policy has declared an invocation
/// approval-requiring and there is no live turn to ask on.
///
/// **This gate fails CLOSED where the consent gate abstains**, and the
/// asymmetry is deliberate. 36.D's consent gate abstains without a live
/// turn because three deployment-side gates have already decided the read
/// is permitted and consent is a courtesy on top of them. Here the
/// deployment has affirmatively declared THIS invocation consequential
/// enough to need a person; running it because there is no one to ask
/// would be precisely the outcome the declaration exists to prevent.
let private noPromptChannelMessage (toolName: string) =
    sprintf
        "The '%s' call requires a person's approval and there is no interactive session to ask in, so it did not run. This is a deployment configuration matter, not something to retry or work around."
        toolName

// ─── Audit writes ────────────────────────────────────────────────

/// Write one approval-trail row, best-effort.
///
/// Best-effort by the same rule the loop's denial audit follows: the
/// control is the DECISION, which has already been applied by the time
/// this runs, and a wedged event store must never turn a held action into
/// a failed turn. Mirrors `AIAgentEngine.writeToolDenialAudit`'s shape
/// rather than inventing a second one.
let writeApprovalAudit
    (store: IEventStore option)
    (logger: ILogger)
    (eventType: string)
    (pending: PendingApproval)
    : Async<unit> =
    async {
        match store with
        | None -> return ()
        | Some store ->
            let payload = {|
                UserId = pending.UserId
                ConversationId = pending.ConversationId
                ToolName = pending.ToolName
                SourceModule = pending.SourceModule
                ArgumentsDigest = pending.ArgumentsDigest
                ArgumentsPreview = pending.ArgumentsPreview
            |}

            let evt: ModuleEvent = {
                Id = Guid.NewGuid()
                OccurredAt = DateTime.UtcNow
                ScopeId = pending.ScopeId
                SourceModule = ApprovalAuditSource
                EventType = eventType
                Payload = JsonSerializer.Serialize(payload, jsonOptions)
            }

            try
                do! store.Write evt
            with ex ->
                logger.Warn
                    $"[ToolApproval] audit write failed ({eventType}, tool={pending.ToolName}, conversation={pending.ConversationId}): {ex.Message}. Record dropped; the decision still applied."
    }

// ─── The gate ────────────────────────────────────────────────────

/// Hold a tool invocation for the user's approval, if the deployment's
/// policy says it needs one.
///
/// Call it at the agent loop's dispatch site, after the RBAC / grant /
/// allowlist arms and before the tool runs. It resolves the policy, asks
/// it about THIS invocation, and — only when the answer is
/// `ApprovalRequired` — suspends, emits `ToolApprovalRequired` on the
/// stream the user is watching, and waits on the one shared budget.
///
/// With no policy composed it does one failed `GetService` and returns
/// `ApprovalGranted`, so the turn is byte-for-byte what it was before
/// this phase (GP 11).
let requireApproval
    (ctx: HttpContext)
    (logger: ILogger)
    (toolName: string)
    (sourceModule: string)
    (argsJson: string)
    (activeModule: string option)
    (activePage: string option)
    : Async<ApprovalOutcome> =
    async {
        match resolvePolicy ctx with
        | None -> return ApprovalGranted
        | Some policy ->
            // A policy that raises must not take the turn down with it.
            // It is documented as non-throwing; a defective one is
            // treated the way a defective authorizer's malformed input
            // is — as a reason to hold, never as a reason to run.
            let requirement =
                try
                    policy.Requires(toolName, sourceModule, argsJson, activeModule, activePage)
                with ex ->
                    logger.Warn
                        $"[ToolApproval] IToolApprovalPolicy.Requires raised for tool '{toolName}': {ex.Message}. Treating the invocation as approval-requiring — a policy that cannot answer must not be read as a yes."

                    ApprovalRequired {
                        Summary = $"Approve running '{toolName}'?"
                        Detail =
                            "The approval policy could not be evaluated for this action, so it is being held for your decision."
                    }

            match requirement with
            | ApprovalNotRequired -> return ApprovalGranted
            | ApprovalRequired prompt ->
                let registry =
                    match ctx.RequestServices.GetService typeof<ToolApprovalRegistry> with
                    | :? ToolApprovalRegistry as r -> Some r
                    | _ -> None

                match guidItem ctx AIConsentDispatch.ItemsKeys.ConversationId, emitterOf ctx, registry with
                | Some conversationId, Some emitter, Some registry ->
                    let store =
                        match ctx.RequestServices.GetService typeof<IEventStore> with
                        | :? IEventStore as s -> Some s
                        | _ -> None

                    let access = AIToolRegistry.reconstructAccessContext ctx

                    let scopeId =
                        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
                        | true, (:? StorageScope as s) -> s.ScopeId
                        | _ -> access.UserId

                    let approvalId = Guid.NewGuid()

                    let taskId =
                        guidItem ctx AIConsentDispatch.ItemsKeys.TaskId
                        |> Option.defaultValue Guid.Empty

                    // The same capped, control-stripped rendering the
                    // consent dialog uses. These are the MODEL's own
                    // arguments, not the module's data — nothing has run
                    // yet — but they are model-authored text of unbounded
                    // length, and a dialog is not a place to render one.
                    let preview = AIConsentDispatch.redactPayloadPreview argsJson

                    let pending: PendingApproval = {
                        ConversationId = conversationId
                        ToolName = toolName
                        SourceModule = sourceModule
                        ArgumentsDigest = argumentsDigest argsJson
                        ArgumentsPreview = preview
                        ScopeId = scopeId
                        UserId = access.UserId
                    }

                    // Register BEFORE emitting, so a decision that arrives
                    // on a fast local round trip cannot find an
                    // unregistered id.
                    let awaited = registry.RegisterPending(approvalId, pending)

                    do! writeApprovalAudit store logger ApprovalRequestedEvent pending

                    emitter.Emit(
                        ToolApprovalRequired(
                            taskId,
                            approvalId,
                            conversationId,
                            toolName,
                            sourceModule,
                            prompt.Summary,
                            prompt.Detail,
                            preview
                        )
                    )

                    let! answered =
                        SuspendedPrompt.awaitDecision awaited (fun () -> registry.TryAbandon approvalId |> ignore)

                    match answered with
                    | Some Approved -> return ApprovalGranted
                    | Some Rejected ->
                        // The POST handler wrote the rejection row: it is
                        // the party that knows a person pressed a button.
                        return ApprovalRefused(userRejectedMessage toolName)
                    | None ->
                        do! writeApprovalAudit store logger ApprovalExpiredEvent pending

                        logger.Warn
                            $"[ToolApproval] no decision for tool '{toolName}' (approvalId={approvalId}, conversation={conversationId}) within {SuspendedPrompt.SuspendedDispatchTimeoutMs / 1000}s; the invocation was refused and did not run."

                        return ApprovalRefused(unansweredMessage toolName)
                | _ ->
                    // No conversation stamped, no emitter, or no registry
                    // composed: there is nowhere to ask. See
                    // `noPromptChannelMessage` for why this refuses where
                    // the consent gate abstains.
                    logger.Warn
                        $"[ToolApproval] tool '{toolName}' requires approval but this context has no way to ask (no conversation, emitter or registry). Refused rather than run."

                    return ApprovalRefused(noPromptChannelMessage toolName)
    }