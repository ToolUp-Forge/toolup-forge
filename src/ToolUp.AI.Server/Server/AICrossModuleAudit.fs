// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.AICrossModuleAudit

open System
open System.Diagnostics
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.AI.AIToolRegistry

/// Phase 36.E — the write side of the cross-module read trail.
///
/// **What it records and why it is not optional.** Phases 36.A / 730 /
/// 36.C / 36.D built four gates in front of the `_platform.ai.*` family,
/// and between them they record every REFUSAL: an RBAC denial, an
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

/// `HttpContext.Items` keys this module READS. Named through constants
/// so the stamping end and the reading end move together, exactly as
/// `AIConsentDispatch.ItemsKeys` does for the consent gate.
module ItemsKeys =
    /// `string` — the module the conversation was active in for this
    /// turn (`AIMessageRequest.ActiveModule`), stamped onto the agent
    /// loop's background context by `AIAssistantHandler`. Absent when the
    /// user was on no module's page, which is an ordinary state.
    [<Literal>]
    let ActiveModule = "ToolUp.AI.ActiveModule"

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
    /// attribution hole Phases 36.C and 36.D both record.
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

/// Read the `error` discriminator out of a rendered tool result.
///
/// Reads the FIELD rather than substring-matching, so a success payload
/// whose content happens to contain the word `PermissionDenied` — an
/// entity row, a module's own prose — is not recorded as a refusal.
/// An unparseable result reads as success, because every refusal this
/// family renders is well-formed JSON by construction and a malformed
/// body is a bug in a tool, not evidence that a read was blocked.
let outcomeOf (rendered: string) : string =
    if String.IsNullOrWhiteSpace rendered then
        OkOutcome
    else
        try
            use doc = JsonDocument.Parse rendered

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

// ─── Context reads ───────────────────────────────────────────────────

let private userIdOf (ctx: HttpContext) : string =
    match ctx.Items.TryGetValue "ToolUp.UserId" with
    | true, (:? string as id) -> id
    | _ -> "anonymous"

/// The scope the row is written under — the same structural tenant
/// boundary every other read in this family uses (GP 4), so a team's
/// cross-module trail is readable by that team and structurally
/// unreachable from another.
let private scopeIdOf (ctx: HttpContext) : string =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> s.ScopeId
    | _ -> userIdOf ctx

let private conversationIdOf (ctx: HttpContext) : Guid option =
    match ctx.Items.TryGetValue AIConsentDispatch.ItemsKeys.ConversationId with
    | true, (:? Guid as id) -> Some id
    | _ -> None

let private activeModuleOf (ctx: HttpContext) : string option =
    match ctx.Items.TryGetValue ItemsKeys.ActiveModule with
    | true, (:? string as m) when not (String.IsNullOrWhiteSpace m) -> Some m
    | _ -> None

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
        UserId = userId
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
        // Phase 283 — the stable id of the component that performed the
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

let private record (ctx: HttpContext) (payload: CrossModuleReadPayload) : Async<unit> = async {
    match ctx.RequestServices.GetService(typeof<IAuditLog>) with
    | :? IAuditLog as auditLog ->
        try
            do! auditLog.Record(scopeIdOf ctx, CrossModuleRead payload)
        with _ ->
            // `IAuditLog.Record` already logs and swallows its own
            // failures; this catches a substrate that throws OUT of it.
            // A dropped row must never cost the turn.
            ()
    | _ -> return ()
}

/// Wrap one `_platform.ai.*` executor so every invocation of it lands one
/// `CrossModuleRead` row.
///
/// `describe` runs BEFORE the executor, so a refused read still records
/// what it was reaching for. It is passed the same `argsJson` the tool
/// gets and may consult DI (`query_entity` resolves its producers through
/// the data catalogue); it must never throw — a `describe` that failed
/// would otherwise take out the tool call it is only observing — so its
/// result is guarded here rather than trusted.
let audited
    (toolName: string)
    (describe: HttpContext -> string -> Async<ReadTarget>)
    (execute: HttpContext -> string -> Async<string>)
    : HttpContext -> string -> Async<string> =
    fun ctx argsJson -> async {
        let! target = async {
            try
                return! describe ctx argsJson
            with _ ->
                return ReadTarget.enumeration
        }

        let conversationId = conversationIdOf ctx
        let userId = userIdOf ctx
        let activeModule = activeModuleOf ctx
        let stopwatch = Stopwatch.StartNew()

        let! outcome = Async.Catch(execute ctx argsJson)
        stopwatch.Stop()

        let rendered, outcomeToken =
            match outcome with
            | Choice1Of2 result -> result, outcomeOf result
            | Choice2Of2 _ -> "", ExceptionOutcome

        do!
            record
                ctx
                (payloadOf
                    conversationId
                    userId
                    activeModule
                    toolName
                    target
                    stopwatch.Elapsed.TotalMilliseconds
                    (Encoding.UTF8.GetByteCount rendered)
                    outcomeToken)

        // The audit is an observation; it never changes what the caller
        // sees. A throwing executor throws exactly as it did before.
        match outcome with
        | Choice1Of2 result -> return result
        | Choice2Of2 ex -> return raise ex
    }

/// `createTool`, with the audit decorator applied. The registration
/// shape the six built-ins use, so a seventh cannot be added without
/// declaring what it reads.
let createAuditedTool
    (def: AIToolDefinition)
    (describe: HttpContext -> string -> Async<ReadTarget>)
    (execute: HttpContext -> string -> Async<string>)
    : RegisteredTool =
    createTool def (audited def.Name describe execute)