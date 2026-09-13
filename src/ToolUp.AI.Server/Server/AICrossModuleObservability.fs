// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.AICrossModuleObservability

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open Giraffe
open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Phase 36.E — `/dev/ai-cross-module` rolling-window rollup ───────
//
// The read side of `AICrossModuleAudit`. Every `_platform.ai.*`
// invocation writes one `CrossModuleRead` row; this answers the two
// questions those rows exist for, over the shared 60-minute window:
//
//   * **Which modules is the agent actually reaching into, and how
//     often** — grouped by `(targetModule, toolName)`, because "the
//     agent read Payroll" and "the agent read Payroll through
//     get_latest_result" are different operator facts.
//   * **Where is it being refused** — a denial RATE per group, plus the
//     refusal vocabulary broken out, so a module whose opt-in was never
//     declared looks different from one whose users keep saying no.
//
// It is the third rollup on this shape (`/dev/ai-fastpath`,
// `/dev/ai-latency`, `/dev/ai-allowlist` are the others) and the first
// to use the extracted `AIDiagnosticsWindow` arithmetic rather than its
// own copy — see that module's header for why the count had already been
// reached.
//
// **Read path: `IAuditLog`, not a ring buffer.** The rows are typed
// audit events under `_platform.audit` (they have to be — see
// `CrossModuleReadPayload`'s docstring), and `GetAuditTrail` takes the
// window and the event type natively, so the bounded read is one call.
// An in-memory ring was the alternative and is worse on both axes that
// matter: it would be a second source of truth for data the store
// already holds, and it would lose everything on restart — which is the
// moment an operator is most likely to be reading this.
//
// **Activation gate:** `ServerConfig.EnableDevEndpoints = true`, applied
// by `composeAI`. No `#if DEBUG` half — this tier stopped carrying
// compile-time gates when it went OSS-bound, exactly as
// `/dev/ai-allowlist` records.
//
// **Caller-scope only (GP 4).** `IAuditLog.GetAuditTrail` takes the
// caller's resolved scope, so another team's cross-module reads are
// structurally unreachable rather than filtered out afterwards.

/// The wire `EventType` discriminator the trail is persisted under —
/// equal to `AuditEvent.eventTypeName (CrossModuleRead _)`, named here so
/// the read side and any operator query cut on one literal.
[<Literal>]
let EventType = "CrossModuleRead"

/// The grouping key for a read that resolved to no single module —
/// `query_entity` against an entity type the data catalogue attributes
/// to no producer or to several, and the two enumeration tools, which
/// read no module's data at all. A label rather than a null key, for the
/// reason `/dev/ai-allowlist` gives: a null group key collapses into
/// whatever the serialiser does with it.
[<Literal>]
let UnattributedTargetLabel = "(unattributed)"

/// Cap on the recent-reads tail. The list answers "what has it been
/// doing"; a thousand rows answers nothing.
[<Literal>]
let private RecentCap = 25

let private indentedJsonOptions =
    let o = FableConverters.create ()
    o.WriteIndented <- true
    o

// ─── Wire shape ─────────────────────────────────────────────────────
//
// Hand-shaped DTO of primitives + lists, matching `/dev/ai-latency`: the
// report is read by a human or a dashboard, not round-tripped by Fable.

/// How many invocations ended in one outcome token.
type OutcomeCount = {
    /// The outcome token, in the tool family's own vocabulary — `"ok"`,
    /// or the `error` discriminator a refusal rendered to the model.
    Outcome: string
    /// How many invocations carried it.
    Count: int
}

/// One `(targetModule, toolName)` bucket. The pair is the grouping key
/// rather than the module alone because "the agent read Payroll" and
/// "the agent read Payroll through `get_latest_result`" are different
/// operator facts.
type CrossModuleGroup = {
    /// The module read, or `UnattributedTargetLabel` when the read
    /// resolved to no single one.
    TargetModule: string
    /// The `_platform.ai.*` tool that read it.
    ToolName: string
    /// Invocations in this group over the window.
    Count: int
    /// Of those, how many reached the data.
    AllowedCount: int
    /// Of those, how many were refused — by any of the four gates, or by
    /// an absent store.
    DeniedCount: int
    /// Refusals over invocations in this group, `0.0` when none.
    DenialRate: float
    /// Median whole-invocation latency, gates included (see
    /// `CrossModuleReadPayload.LatencyMs` for why that is the
    /// measurement).
    P50LatencyMs: float
    /// 95th-percentile whole-invocation latency, nearest-rank.
    P95LatencyMs: float
    /// 99th-percentile whole-invocation latency, nearest-rank.
    P99LatencyMs: float
    /// Bytes returned by the ALLOWED reads in this group. A refusal
    /// renders a small body, so summing every row would flatter a group
    /// that is mostly being refused.
    AllowedResultBytes: int
    /// The refusal vocabulary, biggest first. Empty when nothing in this
    /// group was refused.
    Denials: OutcomeCount list
}

/// One row of the recent tail. No timestamp: `IAuditLog.GetAuditTrail`
/// returns decoded `AuditEvent`s and the envelope's `OccurredAt` does not
/// survive that projection. The list is in the order the store returned
/// (reverse-chronological, per the `IAuditLog` contract), which is what
/// "recent" means here — rather than duplicating a clock into the payload
/// so that two timestamps could disagree.
type RecentRead = {
    /// The `_platform.ai.*` tool invoked.
    ToolName: string
    /// The module read, or `UnattributedTargetLabel`.
    TargetModule: string
    /// The module the conversation was active in, or `""` when the user
    /// was on no module's page.
    SourceConvActiveModule: string
    /// The read's discriminator within the target, or `""` when it had
    /// none.
    QueryKey: string
    /// The outcome token — `"ok"`, or the refusal's own discriminator.
    Outcome: string
    /// Did the read reach the data.
    Allowed: bool
    /// Whole-invocation wall clock, gates included.
    LatencyMs: float
    /// Size of the rendered result in bytes, refusals included.
    ResultBytes: int
}

/// The `/dev/ai-cross-module` payload.
type CrossModuleReport = {
    /// When the report was built.
    GeneratedAt: DateTime
    /// The caller's resolved scope — the only scope the trail was read
    /// from (GP 4).
    ScopeId: string
    /// The rolling window, in minutes. Shared with the three sibling
    /// AI-tier `/dev/*` rollups so the four are comparable.
    WindowMinutes: int
    /// Every `_platform.ai.*` invocation in the window.
    TotalReadsInWindow: int
    /// Of those, how many reached the data.
    AllowedInWindow: int
    /// Of those, how many were refused.
    DeniedInWindow: int
    /// Refusals over invocations across the whole window.
    DenialRate: float
    /// Distinct modules reached in the window, excluding the
    /// unattributed bucket.
    DistinctTargetModules: int
    /// One entry per `(targetModule, toolName)` pair, busiest first.
    ByTargetAndTool: CrossModuleGroup list
    /// The whole window's outcome vocabulary, biggest first.
    ByOutcome: OutcomeCount list
    /// A capped tail of the most recent reads, for eyeballing what the
    /// agent has just been doing.
    RecentReads: RecentRead list
}

// ─── Rollup (pure) ──────────────────────────────────────────────────

/// Grouping label for a payload's target module.
let targetLabel (payload: CrossModuleReadPayload) : string =
    match payload.TargetModule with
    | Some m when not (String.IsNullOrWhiteSpace m) -> m
    | _ -> UnattributedTargetLabel

let private optLabel (value: string option) =
    match value with
    | Some v when not (String.IsNullOrWhiteSpace v) -> v
    | _ -> ""

let private rate (numerator: int) (denominator: int) =
    if denominator = 0 then
        0.0
    else
        float numerator / float denominator

let private countByOutcome (rows: CrossModuleReadPayload list) =
    rows
    |> List.groupBy _.Outcome
    |> List.map (fun (outcome, items) -> {
        Outcome = outcome
        Count = items.Length
    })
    |> List.sortByDescending _.Count

/// Build the report from already-read, already-decoded rows.
///
/// Pure and total — no clock, no DI, no store — so a synthetic burst
/// exercises the whole aggregation from a test, which is what makes the
/// "grouped by `(targetModule, toolName)` with p50/p95/p99 and denial
/// rate" acceptance assertable rather than merely inspectable.
///
/// The window filter is NOT applied here: `GetAuditTrail` applies it at
/// the store, so `rows` is already the window. Re-filtering would need a
/// second `OccurredAt` this payload deliberately does not carry — the
/// audit envelope owns the timestamp, and duplicating it into the
/// payload is how two fields that must agree stop agreeing.
let computeReport
    (scopeId: string)
    (now: DateTime)
    (window: TimeSpan)
    (rows: CrossModuleReadPayload list)
    : CrossModuleReport =
    let denied = rows |> List.filter (fun r -> not r.Allowed)

    {
        GeneratedAt = now
        ScopeId = scopeId
        WindowMinutes = int window.TotalMinutes
        TotalReadsInWindow = rows.Length
        AllowedInWindow = rows.Length - denied.Length
        DeniedInWindow = denied.Length
        DenialRate = rate denied.Length rows.Length
        DistinctTargetModules = rows |> List.choose _.TargetModule |> List.distinct |> List.length
        ByTargetAndTool =
            rows
            |> List.groupBy (fun r -> targetLabel r, r.ToolName)
            |> List.map (fun ((target, tool), bucket) ->
                let latencies = bucket |> List.map _.LatencyMs |> List.toArray
                let refused = bucket |> List.filter (fun r -> not r.Allowed)
                let allowed = bucket |> List.filter _.Allowed

                {
                    TargetModule = target
                    ToolName = tool
                    Count = bucket.Length
                    AllowedCount = allowed.Length
                    DeniedCount = refused.Length
                    DenialRate = rate refused.Length bucket.Length
                    P50LatencyMs = AIDiagnosticsWindow.percentile latencies 0.50
                    P95LatencyMs = AIDiagnosticsWindow.percentile latencies 0.95
                    P99LatencyMs = AIDiagnosticsWindow.percentile latencies 0.99
                    AllowedResultBytes = allowed |> List.sumBy _.ResultBytes
                    Denials = countByOutcome refused
                })
            |> List.sortByDescending _.Count
        ByOutcome = countByOutcome rows
        RecentReads =
            rows
            |> List.truncate RecentCap
            |> List.map (fun r -> {
                ToolName = r.ToolName
                TargetModule = targetLabel r
                SourceConvActiveModule = optLabel r.SourceConvActiveModule
                QueryKey = optLabel r.QueryKey
                Outcome = r.Outcome
                Allowed = r.Allowed
                LatencyMs = r.LatencyMs
                ResultBytes = r.ResultBytes
            })
    }

// ─── Store read ─────────────────────────────────────────────────────

/// Read + roll up the cross-module reads recorded for `scopeId`.
let reportFor (auditLogOpt: IAuditLog option) (scopeId: string) : Async<CrossModuleReport> = async {
    let now = DateTime.UtcNow
    let window = AIDiagnosticsWindow.rollingWindow
    let windowStart = now - window

    let! events =
        match auditLogOpt with
        | None -> async { return [] }
        | Some auditLog -> auditLog.GetAuditTrail(scopeId, Some(windowStart, now), Some EventType)

    let rows =
        events
        |> List.choose (function
            | CrossModuleRead payload -> Some payload
            | _ -> None)

    return computeReport scopeId now window rows
}

// ─── Route handler ──────────────────────────────────────────────────

let private resolveScopeId (ctx: HttpContext) : string =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> s.ScopeId
    | _ ->
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> id
        | _ -> "anonymous"

let private buildReport (ctx: HttpContext) : Async<CrossModuleReport> =
    let auditLogOpt =
        match ctx.RequestServices.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as log -> Some log
        | _ -> None

    reportFor auditLogOpt (resolveScopeId ctx)

/// JSON handler for `/dev/ai-cross-module`. `Cache-Control: no-store` so
/// a refresh always re-reads the trail.
let private crossModuleHandler: HttpHandler =
    fun next ctx -> task {
        let! report = buildReport ctx
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.Headers["Cache-Control"] <- "no-store"
        do! ctx.Response.WriteAsync(JsonSerializer.Serialize(report, indentedJsonOptions))
        return! next ctx
    }

/// Route for the cross-module read rollup. Mounted only when
/// `ServerConfig.EnableDevEndpoints = true` by `composeAI`.
let routes: HttpHandler list = [ route "/dev/ai-cross-module" >=> crossModuleHandler ]