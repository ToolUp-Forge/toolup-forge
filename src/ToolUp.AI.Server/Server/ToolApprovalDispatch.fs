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
open ToolUp.Platform.Infra
open ToolUp.Platform.Compose

/// The human-in-the-loop approval gate.
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
/// The round trip is the suspend/resume, generalised by
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
/// under. One source, four event types, for the reason the RBAC re-check gave
/// the RBAC denial its own stream: an operator asking "what has the
/// assistant been held for, and what did people say" reads one stream and
/// gets every answer.
///
/// A REFUSAL additionally writes the existing
/// `_platform.ai.tool_allowlist_denial` row from the agent loop, so it
/// reaches the `/dev/ai-allowlist` rollup, the
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

let private jsonOptions = PlatformJson.options

// ─── The pending record ──────────────────────────────────────────

/// One held invocation, as the server recorded it when it suspended.
///
/// Recorded BEFORE the SSE event is emitted, so the decision POST can be
/// authorised and attributed from this rather than from the client's
/// body — the same reason the RBAC re-check records a pending tool call's
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
    UserId: UserId
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

/// The standing refusal text, written for the model.
let private userRejectedMessage (toolName: string) =
    sprintf
        "The user was asked to approve this '%s' call and refused it. This is the user's own decision about a consequential action, not a permission and not a deployment setting — do not retry it, do not route the same action through another tool, and do not treat it as PermissionDenied. Tell the user plainly that you did not perform it, and ask what they would like instead."
        toolName

let private unansweredMessage (toolName: string) (budgetMs: int) =
    sprintf
        "The approval prompt for this '%s' call was not answered within %d seconds, so it did NOT run and nothing was changed. Do not retry it. Say plainly that the action is still outstanding and that it needs the user's confirmation."
        toolName
        (budgetMs / 1000)

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
/// a failed turn. Mirrors the shape of the agent loop's denial audit
/// (`AIAgentEngine.Loop.run`) rather than inventing a second one.
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
                do! EventStore.writeOrRaise store evt
            with ex ->
                logger.Warn
                    $"[ToolApproval] audit write failed ({eventType}, tool={pending.ToolName}, conversation={pending.ConversationId}): {ex.Message}. Record dropped; the decision still applied."
    }

// ─── The gate ────────────────────────────────────────────────────

/// The class-keyed hold: `ApprovalRequired` when `policy` names a class `def`
/// declares, with a prompt naming the classes; `ApprovalNotRequired`
/// otherwise. Pure.
let requirementByClass (policy: AIToolRegistry.ToolPolicy) (def: AIToolDefinition) : ToolApprovalRequirement =
    match AIToolRegistry.ToolGate.approvalClasses policy def with
    | [] -> ApprovalNotRequired
    | classes ->
        let named = classes |> List.map ToolEffectClass.label |> String.concat ", "
        let effects = ToolEffectDeclaration.describe (AIToolEffects.declaredOf def)

        ApprovalRequired {
            Summary = $"Approve running '{def.Name}'?"
            Detail =
                $"This tool declares effects of a class this deployment holds for your decision ({named}): {effects}. It runs only if you approve."
        }

/// What the deployment requires of a call: the class-keyed hold first, else
/// whatever the composed `IToolApprovalPolicy` answers. A policy that raises
/// holds the invocation — a policy that cannot answer must not be read as a
/// yes.
let requirementFor
    (policy: AIToolRegistry.ToolPolicy)
    (logger: ILogger)
    (activeModule: string option)
    (activePage: string option)
    (call: AIToolRegistry.GateCall)
    : ToolApprovalRequirement =
    let def = call.Tool

    match requirementByClass policy def, resolvePolicy call.Request with
    | ApprovalRequired _ as held, _ -> held
    | ApprovalNotRequired, None -> ApprovalNotRequired
    | ApprovalNotRequired, Some approvals ->
        try
            approvals.Requires(def.Name, def.SourceModule, call.Arguments, activeModule, activePage)
        with ex ->
            logger.Warn
                $"[ToolApproval] IToolApprovalPolicy.Requires raised for tool '{def.Name}': {ex.Message}. Treating the invocation as approval-requiring — a policy that cannot answer must not be read as a yes."

            ApprovalRequired {
                Summary = $"Approve running '{def.Name}'?"
                Detail =
                    "The approval policy could not be evaluated for this action, so it is being held for your decision."
            }

/// The approval gate (g5), on a budget of `budgetMs`: hold the invocation
/// for the user's decision when the deployment requires it, and admit it
/// otherwise. The held call is registered BEFORE `ToolApprovalRequired` is
/// emitted on `channel`, so a decision on a fast round trip finds it; an
/// unanswered prompt is a refusal, and so is a requirement with no channel
/// or registry to ask through — the deployment declared this invocation
/// consequential enough to need a person. The budget is a parameter because
/// the elapsed arm is the one a test cannot afford at the production value
/// (`SuspendedPrompt.SuspendedDispatchTimeoutMs`, what the loop passes).
let approve
    (budgetMs: int)
    (policy: AIToolRegistry.ToolPolicy)
    (logger: ILogger)
    (channel: PromptChannel option)
    (activeModule: string option)
    (activePage: string option)
    : AIToolRegistry.GateCheck =
    fun call -> async {
        let toolName = call.Tool.Name

        let refuse reason =
            Error(ToolRefusal.ApprovalRefused(toolName, reason))

        match requirementFor policy logger activeModule activePage call with
        | ApprovalNotRequired -> return Ok()
        | ApprovalRequired prompt ->
            match channel, call.Request.RequestServices.GetService typeof<ToolApprovalRegistry> with
            | Some channel, (:? ToolApprovalRegistry as registry) ->
                let store =
                    match call.Request.RequestServices.GetService typeof<IEventStore> with
                    | :? IEventStore as s -> Some s
                    | _ -> None

                let approvalId = Guid.NewGuid()
                let preview = AIConsentDispatch.redactPayloadPreview call.Arguments

                let pending: PendingApproval = {
                    ConversationId = channel.ConversationId
                    ToolName = toolName
                    SourceModule = call.Tool.SourceModule
                    ArgumentsDigest = argumentsDigest call.Arguments
                    ArgumentsPreview = preview
                    ScopeId = (AIRequest.scopeOf call.Access).ScopeId
                    UserId = UserId call.Access.UserId
                }

                let awaited = registry.RegisterPending(approvalId, pending)

                do! writeApprovalAudit store logger ApprovalRequestedEvent pending

                channel.Emit(
                    ToolApprovalRequired(
                        channel.TaskId,
                        approvalId,
                        channel.ConversationId,
                        toolName,
                        call.Tool.SourceModule,
                        prompt.Summary,
                        prompt.Detail,
                        preview
                    )
                )

                let! answered =
                    SuspendedPrompt.awaitDecisionWithin budgetMs awaited (fun () ->
                        registry.TryAbandon approvalId |> ignore)

                match answered with
                | Some Approved -> return Ok()
                // The POST handler wrote the rejection row: it is the party
                // that knows a person pressed a button.
                | Some Rejected -> return refuse (userRejectedMessage toolName)
                | None ->
                    do! writeApprovalAudit store logger ApprovalExpiredEvent pending

                    logger.Warn
                        $"[ToolApproval] no decision for tool '{toolName}' (approvalId={approvalId}, conversation={channel.ConversationId}) within {budgetMs / 1000}s; the invocation was refused and did not run."

                    return refuse (unansweredMessage toolName budgetMs)
            | _ ->
                logger.Warn
                    $"[ToolApproval] tool '{toolName}' requires approval but this context has no way to ask (no conversation, emitter or registry). Refused rather than run."

                return refuse (noPromptChannelMessage toolName)
    }