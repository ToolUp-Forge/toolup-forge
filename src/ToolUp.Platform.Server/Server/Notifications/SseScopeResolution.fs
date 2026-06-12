// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SseScopeResolution

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Phase 117 — shared SSE scope resolution ─────────────────────────
//
// One scope-resolution path for every SSE endpoint (`/api/notifications`
// here in ToolUp.Platform.Server; `/api/ai/events` in ToolUp.AI.Server).
// The two handlers previously carried hand-copied variants of this logic
// and drifted: the AI handler gained the `SseAuthMode` gate
// (`CookieRequired` → 401, no client-trusted fallback) while the
// notification handler kept honouring `?userId=` unconditionally — so
// under the documented `AcceptQueryParamSseAuthWhenAuthRequired` escape
// hatch an unauthenticated caller could subscribe to an arbitrary user's
// live notification stream. Extracting the seam makes the next drift
// impossible rather than unlikely. (GP 4 — caller-scope-only delivery.)

/// Outcome of resolving the subscriber scope for an SSE connect.
type SseScopeResult =
    /// A scope was resolved; register the connection under it.
    | SseScope of scopeId: string
    /// `CookieRequired` and no middleware-resolved identity. The
    /// connect must be refused (`refuseUnauthenticated` below) —
    /// trusting a client-supplied `userId` here would let any caller
    /// subscribe as an arbitrary user and receive their stream.
    | SseUnauthenticated

/// DenialCode stamped on the `SurfaceDenied` audit row when a validated
/// principal connects with a mismatching `?userId=`. The *param* is what
/// was denied — the connection itself proceeds under the principal.
[<Literal>]
let mismatchDenialCode = "sse_userid_principal_mismatch"

/// Emit the principal/param-mismatch audit row + Warn. Best-effort and
/// fire-and-forget — observability must not stall or fail the connect.
/// Mirrors `SurfaceEnforcementMiddleware.emitDenialAudit`: a
/// `SurfaceDenied` row on scope `_platform` with a distinct
/// `DenialCode`. Phase 120's generic authz-denial hook will consume
/// this event as one of its inputs; a dedicated `AuditEvent` case can
/// land there if the mismatch row needs richer structure.
let private emitMismatchAudit (ctx: HttpContext) (principalId: string) (assertedId: string) : unit =
    try
        match ctx.RequestServices.GetService(typeof<ILogger>) with
        | :? ILogger as logger ->
            logger.Warn(
                sprintf
                    "SSE connect on %s carried ?userId=%s but the validated principal is %s — param ignored, principal wins. Repeated mismatches from one source are probing."
                    (string ctx.Request.Path)
                    assertedId
                    principalId
            )
        | _ -> ()

        match ctx.RequestServices.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as auditLog ->
            // Subject kind/id vocabulary matches SurfaceEnforcementMiddleware
            // so operator dashboards aggregate both through one query shape.
            // `"ToolUp.Subject"` is set by the scope-resolution middleware
            // (same key RateLimiting reads); when absent — defensive, a
            // resolved principal implies a resolved subject — fall back to
            // the principal id with kind "user".
            let subjectKind, subjectId =
                match ctx.Items.TryGetValue "ToolUp.Subject" with
                | true, (:? Subject as subject) ->
                    let kind =
                        match Subject.kind subject with
                        | AnonymousKind -> "anonymous"
                        | UserKind -> "user"
                        | TeamMemberKind -> "team"
                        | ClaimBearerKind -> "claim"

                    kind, Some principalId
                | _ -> "user", Some principalId

            auditLog.Record(
                "_platform",
                SurfaceDenied {
                    Method = ctx.Request.Method
                    Path = string ctx.Request.Path
                    SubjectKind = subjectKind
                    SubjectId = subjectId
                    DenialCode = mismatchDenialCode
                    Hint = Some(sprintf "asserted=%s" assertedId)
                    CorrelationId = ToolUp.Remoting.Server.CallContext.correlationId ()
                    OccurredAt = DateTimeOffset.UtcNow
                }
            )
            |> Async.Start
        | _ -> ()
    with _ ->
        ()

/// Resolve the subscriber scope for an SSE connect.
///
/// The middleware-resolved identity (`ctx.Items["ToolUp.UserId"]`,
/// populated by `ScopeResolutionMiddleware`) always wins. The guard
/// excludes `"anonymous"` so EventSource connections (which cannot set
/// custom headers) fall through to the `?userId=` query param in
/// Anonymous-mode deployments — the middleware writes `"anonymous"`
/// when no header is present, which would otherwise register the
/// connection under one shared scope and drop notifications destined
/// for the per-tab session id.
///
/// When a validated principal is present and `?userId=` mismatches it,
/// the param is ignored *and* audited (`SurfaceDenied` +
/// `sse_userid_principal_mismatch`) — probing must be visible (GP 6).
///
/// The `?userId=` / `X-User-Id` fallback is honoured ONLY under
/// `QueryParamFallback` (the documented dev / Anonymous path; preflight
/// `SseAuthModeValidator` refuses authenticated mode + fallback unless
/// explicitly opted in). Under `CookieRequired` a missing /
/// `"anonymous"` resolved identity yields `SseUnauthenticated` → the
/// caller refuses with 401.
let resolve (sseAuthMode: SseAuthMode) (ctx: HttpContext) : SseScopeResult =
    let queryUserId =
        match ctx.Request.Query.TryGetValue "userId" with
        | true, values when values.Count > 0 -> Some(string values[0])
        | _ -> None

    match ctx.Items.TryGetValue "ToolUp.UserId" with
    | true, (:? string as id) when not (String.IsNullOrEmpty id) && id <> "anonymous" ->
        match queryUserId with
        | Some asserted when asserted <> id -> emitMismatchAudit ctx id asserted
        | _ -> ()

        SseScope id
    | _ ->
        match sseAuthMode with
        | CookieRequired -> SseUnauthenticated
        | QueryParamFallback ->
            match queryUserId with
            | Some asserted -> SseScope asserted
            | None ->
                match ctx.Request.Headers.TryGetValue "X-User-Id" with
                | true, values when values.Count > 0 -> SseScope(string values[0])
                | _ -> SseScope "anonymous"

/// Write the `CookieRequired`-mode 401 refusal. Shared so the two SSE
/// endpoints answer an unauthenticated connect byte-for-byte the same.
/// Plain text — JSON would mislead clients into parsing it as an SSE
/// event.
let refuseUnauthenticated (ctx: HttpContext) : Task =
    ctx.Response.StatusCode <- 401
    ctx.Response.ContentType <- "text/plain; charset=utf-8"

    ctx.Response.WriteAsync(
        "SSE connection requires an authenticated session (SseAuthMode = CookieRequired). No middleware-resolved identity was present and the query-param fallback is disabled in this mode."
    )