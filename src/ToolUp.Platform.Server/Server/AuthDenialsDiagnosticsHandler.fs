// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AuthDenialsDiagnosticsHandler

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open Giraffe
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── Phase 120 — /dev/auth-denials rollup ────────────────────────────
//
// Read-side companion to `IAuthAuditHook`: a rolling 60-minute rollup of
// `AuthorizationDenied` audit rows by route / requirement / scope, so an
// operator can spot an enumeration probe or a surface-config regression at
// a glance. Generalises the Phase 47 AI-denial rollup pattern to the whole
// auth surface.
//
// **Mounted only when `EnableDevEndpoints = true`** (the same gate as
// `/dev/inspect`; `BuildRouteHandlers` appends this route inside
// `devDiagnosticsRoutes`). Absent from the routing table otherwise — a
// clean 404, no runtime cost (GP 13).
//
// **Caller-scope only (GP 4).** The rollup reads the caller's resolved
// scope via `IAuditLog.GetAuditTrail(scope, …)`, which is structurally
// single-scope — no cross-team denial leakage. Scope-less (anonymous)
// denials are written under `_platform`; an operator acting in `_platform`
// scope sees those, an operator in a team scope sees that team's denials.

[<Literal>]
let private WindowMinutes = 60

[<Literal>]
let private RecentReasonsCap = 20

let private jsonOptions =
    let o = FableConverters.create ()
    o.WriteIndented <- true
    o

/// One grouped count row. `Count` is the sum of `DedupCount` across the
/// group's audit rows — so a coalesced probing burst contributes its true
/// total, not the number of (deduped) rows.
type private GroupCount = { key: string; count: int }

type private RecentDenial = {
    route: string
    requirement: string
    subjectKind: string
    verdict: string
    reason: string
    scopeId: string option
    occurredAt: DateTimeOffset
}

type private AuthDenialsRollup = {
    generatedAt: DateTime
    scopeId: string
    windowMinutes: int
    /// Sum of `DedupCount` across every row in the window — the true denial
    /// count including coalesced bursts.
    totalDenials: int
    /// Number of (deduped) audit rows actually read.
    rowCount: int
    byRoute: GroupCount list
    byRequirement: GroupCount list
    byScope: GroupCount list
    recentReasons: RecentDenial list
}

/// Resolve the caller's `AccessContext` — populated by
/// `ScopeResolutionMiddleware` on `/dev/*` paths. Falls back to an
/// anonymous context (which scopes the read to `_platform`) when absent,
/// e.g. a test bypassing the middleware.
let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ -> AccessContext.unrestricted (AnonymousSession "anonymous")

/// Caller scope to read denials from: the configured per-scope container
/// id (team / user / claim) when present, else `_platform` for anonymous
/// callers (where scope-less denials are written).
let private callerScopeId (accessContext: AccessContext) : string =
    match AccessContext.configScope accessContext with
    | Some scope -> scope.ScopeId
    | None -> "_platform"

let private groupBySum (selector: AuthorizationDeniedPayload -> string) (rows: AuthorizationDeniedPayload list) =
    rows
    |> List.groupBy selector
    |> List.map (fun (key, items) -> {
        key = key
        count = items |> List.sumBy _.DedupCount
    })
    |> List.sortByDescending _.count

/// Build the rollup for the caller's scope over the trailing window.
let private buildRollup (ctx: HttpContext) : Async<AuthDenialsRollup> = async {
    let accessContext = resolveAccessContext ctx
    let scopeId = callerScopeId accessContext
    let now = DateTime.UtcNow
    let windowStart = now.AddMinutes(float -WindowMinutes)

    // Flush the default hook's suppressed-burst tail so the rollup reflects
    // accurate counts without waiting for the next probe to roll the window.
    match ctx.RequestServices.GetService(typeof<IAuthAuditHook>) with
    | :? AuthAuditHook.AuthAuditHook as hook -> do! hook.FlushPending()
    | _ -> ()

    let rows =
        match ctx.RequestServices.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as auditLog -> async {
            let! events = auditLog.GetAuditTrail(scopeId, Some(windowStart, now), Some "AuthorizationDenied")

            return
                events
                |> List.choose (function
                    | AuthorizationDenied p -> Some p
                    | _ -> None)
          }
        | _ -> async { return [] }

    let! denials = rows

    let recent =
        denials
        |> List.sortByDescending _.OccurredAt
        |> List.truncate RecentReasonsCap
        |> List.map (fun p -> {
            route = p.Route
            requirement = p.Requirement
            subjectKind = p.SubjectKind
            verdict = p.Verdict
            reason = p.Reason
            scopeId = p.ScopeId
            occurredAt = p.OccurredAt
        })

    return {
        generatedAt = now
        scopeId = scopeId
        windowMinutes = WindowMinutes
        totalDenials = denials |> List.sumBy _.DedupCount
        rowCount = denials.Length
        byRoute = denials |> groupBySum _.Route
        byRequirement = denials |> groupBySum _.Requirement
        byScope = denials |> groupBySum (fun p -> p.ScopeId |> Option.defaultValue "_platform")
        recentReasons = recent
    }
}

let private rollupHandler: HttpHandler =
    fun next ctx -> task {
        let! rollup = buildRollup ctx
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.Headers["Cache-Control"] <- "no-store"
        do! ctx.Response.WriteAsync(JsonSerializer.Serialize(rollup, jsonOptions))
        return! next ctx
    }

/// `/dev/auth-denials` route. Mounted by `BuildRouteHandlers` inside the
/// `EnableDevEndpoints`-gated `devDiagnosticsRoutes` list.
let route: HttpHandler = Giraffe.Routing.route "/dev/auth-denials" >=> rollupHandler