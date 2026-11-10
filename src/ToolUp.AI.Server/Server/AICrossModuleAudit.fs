// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.AICrossModuleAudit

open System
open System.Diagnostics
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.AI.AIAuditEvents
open ToolUp.AI.AIToolRegistry
open ToolUp.Platform.Compose

/// The write side of the cross-module read trail.
///
/// **What it records and why it is not optional.** Four gates stand in
/// front of the `_platform.ai.*` family, and between them they record
/// every REFUSAL: an RBAC denial, an
/// attempt on inert authority, a consent decision. None of them records
/// a read that was ALLOWED. So the question a reviewer actually asks —
/// "what did the agent read out of modules the user never named" — had
/// no answer at all, and the question a user asks after clicking
/// "allow" — "what did it then do with that" — had none either. This
/// emits one row per invocation, allowed or refused, so both halves are
/// answerable from one stream.
///
/// **It is a DECORATOR over the whole executor, not a call inside it.**
/// The acceptance is "N audit entries, one per `_platform.ai.*`
/// invocation", and the six executors have between four and eight early
/// returns each (invalid arguments, permission, grant, opt-in, consent,
/// store-absent, not-found, success). An emission threaded through those
/// by hand is a list that silently loses a row every time a branch is
/// added — exactly the drift the audit codec registry exists to prevent
/// on the other side of the same trail. Wrapping the executor makes
/// "once per invocation, on every outcome path" hold by construction:
/// there is one return, and one raise, and both are covered here.
///
/// **Best-effort, never blocking, never throwing.** Same contract as
/// `IAuditLog.Record` and as every other audit write in this tier: the
/// control is the GATE, which has already run by the time this does, and
/// a wedged backend must never turn a tool call into a failed turn.
/// A deployment with no `IAuditLog` registered pays one failed
/// `GetService` per invocation (GP 13).

// ─── Per-request items ───────────────────────────────────────────────

/// The consumer compatibility carry's key for the module the conversation
/// was active in (`ToolContext.ItemsKeys.ActiveModule`).
module ItemsKeys =
    /// `string` — the module the conversation was active in for this turn.
    [<Literal>]
    let ActiveModule = ToolContext.ItemsKeys.ActiveModule

// ─── What a tool was reaching for ────────────────────────────────────

/// The target a single `_platform.ai.*` invocation was reaching for,
/// described by the tool itself.
///
/// Each tool knows its own argument shape, so the description is
/// supplied at the registration site rather than re-parsed here — a
/// second parser for six argument shapes would drift from the six that
/// already exist, and would drift silently.
type ReadTarget = {
    /// Every module this read is ABOUT. Empty for the two enumeration
    /// tools, which read no module's data; one entry for the three that
    /// name a module; and however many the data catalogue attributes an
    /// entity type to for `query_entity` — including none, which is the
    /// attribution hole the opt-in and consent gates both record.
    Modules: string list
    /// The read's discriminator within the target, when it has one.
    QueryKey: string option
}

module ReadTarget =
    /// The two enumeration tools: no module read, no discriminator.
    let enumeration: ReadTarget = { Modules = []; QueryKey = None }

    /// The three tools that name a module outright, plus the two that
    /// also carry a discriminator within it.
    let ofModule (moduleName: string option) (queryKey: string option) : ReadTarget = {
        Modules = moduleName |> Option.toList
        QueryKey = queryKey
    }

// ─── Outcome classification ──────────────────────────────────────────

/// The success token. A string rather than a DU for the reason the
/// payload's own docstring gives: `Outcome` mirrors the `error`
/// vocabulary the tools render for a MODEL, and a closed union here
/// could only fall behind it.
[<Literal>]
let OkOutcome = "ok"

/// The outcome recorded when the executor itself threw. Distinct from
/// every rendered refusal: those are the tool family working, this is
/// the tool family failing.
[<Literal>]
let ExceptionOutcome = "Exception"

/// The `error` discriminator of content a tool produced — how a gate
/// inside the tool (permission, opt-in, consent, store, not-found) still
/// reports its refusal.
///
/// Reads the FIELD rather than substring-matching, so a success payload
/// whose content happens to contain the word `PermissionDenied` — an
/// entity row, a module's own prose — is not recorded as a refusal.
/// An unparseable result reads as success, because every refusal this
/// family renders is well-formed JSON by construction and a malformed
/// body is a bug in a tool, not evidence that a read was blocked.
let private producedOutcome (content: string) : string =
    if String.IsNullOrWhiteSpace content then
        OkOutcome
    else
        try
            use doc = JsonDocument.Parse content

            if doc.RootElement.ValueKind <> JsonValueKind.Object then
                OkOutcome
            else
                match doc.RootElement.TryGetProperty "error" with
                | true, v when v.ValueKind = JsonValueKind.String ->
                    let token = v.GetString()
                    if String.IsNullOrWhiteSpace token then OkOutcome else token
                | _ -> OkOutcome
        with _ ->
            OkOutcome

/// The outcome token one invocation records: the fault's or refusal's own
/// discriminator, read from the value; for produced content, its `error`
/// field.
let outcomeOf (outcome: ToolOutcome) : string =
    match outcome with
    | ToolOutcome.Produced content -> producedOutcome content
    | ToolOutcome.Faulted fault -> ToolFault.kind fault
    | ToolOutcome.Refused _ -> "Denied"

// ─── Emission ────────────────────────────────────────────────────────

/// Build the payload. Pure and total — no clock, no DI, no store — so
/// the whole shape is assertable from a test without a live turn.
let payloadOf
    (conversationId: Guid option)
    (userId: string)
    (activeModule: string option)
    (toolName: string)
    (target: ReadTarget)
    (latencyMs: float)
    (resultBytes: int)
    (outcome: string)
    : CrossModuleReadPayload =
    {
        ConversationId = conversationId
        UserId = UserId userId
        SourceConvActiveModule = activeModule
        // `Some m` exactly when the read resolved to ONE module. See the
        // payload's own docstring: a grouping key that synthesised a
        // join of several module names, or invented one where the
        // catalogue attributed none, would be a key an operator cannot
        // take at face value.
        TargetModule =
            match target.Modules with
            | [ single ] -> Some single
            | _ -> None
        TargetModules = target.Modules
        ToolName = toolName
        // The stable id of the component that performed the
        // read. The TOOL is that component and `ComponentId.forTool` is
        // its declared slot, so this needs nothing resolved at run time.
        // The target MODULE's id is deliberately not carried; see the
        // "Cross-module observability" section of the AI technical guide
        // for why it is not reachable from a tool executor.
        ComponentId = ComponentId.value (ComponentId.forTool toolName)
        QueryKey = target.QueryKey
        LatencyMs = latencyMs
        ResultBytes = resultBytes
        Allowed = outcome = OkOutcome
        Outcome = outcome
    }

let private record (context: ToolContext) (payload: CrossModuleReadPayload) : Async<unit> = async {
    match context.Request.RequestServices.GetService(typeof<IAuditLog>) with
    | :? IAuditLog as auditLog ->
        do!
            auditLog.Record(
                AuditScope.shard context.Scope.ScopeId,
                ToolUp.Platform.Config.Background.requestActor "AICrossModule" context.Request,
                AIAuditEvent.event (CrossModuleRead payload)
            )
            |> AuditRow.bestEffort
    | _ -> return ()
}

/// Run one invocation of `toolName` reaching for `target`, and record one
/// `CrossModuleRead` row whatever its outcome: the outcome's own token, a
/// fault raised as `ToolHelpers.ToolFaulted` as that fault, any other raise
/// as `ExceptionOutcome`. The audit is an observation; a raise is re-raised
/// exactly as it was.
let private observe
    (context: ToolContext)
    (toolName: string)
    (target: ReadTarget)
    (run: Async<ToolOutcome>)
    : Async<ToolOutcome> =
    async {
        let stopwatch = Stopwatch.StartNew()
        let! outcome = Async.Catch run
        stopwatch.Stop()

        let observed =
            match outcome with
            | Choice1Of2 value -> Some value
            | Choice2Of2(ToolHelpers.ToolFaulted fault) -> Some(ToolOutcome.Faulted fault)
            | Choice2Of2 _ -> None

        let rendered, outcomeToken =
            match observed with
            | Some value -> ToolOutcome.toModel value, outcomeOf value
            | None -> "", ExceptionOutcome

        do!
            record
                context
                (payloadOf
                    (context.Channel |> Option.map _.ConversationId)
                    context.Access.UserId
                    context.ActiveModule
                    toolName
                    target
                    stopwatch.Elapsed.TotalMilliseconds
                    (Encoding.UTF8.GetByteCount rendered)
                    outcomeToken)

        match outcome with
        | Choice1Of2 value -> return value
        | Choice2Of2 ex -> return raise ex
    }

/// `describe`, guarded: it runs BEFORE the executor so a refused read still
/// records what it was reaching for, and it must never take out the call it
/// only observes.
let private targetOf (describe: unit -> Async<ReadTarget>) : Async<ReadTarget> = async {
    try
        return! describe ()
    with _ ->
        return ReadTarget.enumeration
}

/// Wrap one `_platform.ai.*` typed executor so every invocation of it lands
/// one `CrossModuleRead` row. A decorator over the whole executor rather than
/// a call inside it: there is one return and one raise, both covered here,
/// so no early return can lose a row.
let auditedTyped
    (toolName: string)
    (describe: ToolContext -> ToolArgs -> Async<ReadTarget>)
    (execute: ToolContext -> ToolArgs -> Async<ToolOutcome>)
    : ToolContext -> ToolArgs -> Async<ToolOutcome> =
    fun context args -> async {
        let! target = targetOf (fun () -> describe context args)
        return! observe context toolName target (execute context args)
    }

/// `auditedTyped` over the string contract, for an executor written against
/// `HttpContext -> string -> Async<string>`.
let audited
    (toolName: string)
    (describe: HttpContext -> string -> Async<ReadTarget>)
    (execute: HttpContext -> string -> Async<string>)
    : HttpContext -> string -> Async<string> =
    fun ctx argsJson -> async {
        let context = ToolContext.ofRequest ctx
        let! target = targetOf (fun () -> describe ctx argsJson)

        let! outcome =
            observe
                context
                toolName
                target
                (async {
                    let! text = execute ctx argsJson
                    return ToolOutcome.Produced text
                })

        return ToolOutcome.toModel outcome
    }

/// `createTypedTool`, with the audit decorator applied: the registration
/// shape the built-in reach family uses, so a tool cannot join it without
/// declaring what it reads.
let createAuditedTool
    (def: AIToolDefinition)
    (describe: ToolContext -> ToolArgs -> Async<ReadTarget>)
    (execute: ToolContext -> ToolArgs -> Async<ToolOutcome>)
    : RegisteredTool =
    createTypedTool def (auditedTyped def.Name describe execute)