// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SessionRevocation

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory
open ToolUp.Platform
open ToolUp.Platform.Auth

// ─── Phase 528 — SessionRevocationMiddleware ─────────────────────────
//
// Refuses a revoked session before dispatch, and records / touches a
// live one on the way through. Registered ONLY when
// `ServerConfig.SessionRegistry` selects a backend, so the default
// deployment does not pay even a delegate hop (GP 13); the mode check in
// `InvokeAsync` is a second, defensive line for a consumer that
// registers the middleware by hand.
//
// **Pipeline position.** Immediately after `SurfaceEnforcementMiddleware`
// — the same reasoning `ModuleVisibilityRouteMiddleware` and the
// anonymous-session migration trigger use. Two consequences, both wanted:
// a request that never cleared authentication is 401'd by the gate above
// rather than reaching a revocation decision, and the response codes stay
// honest (an anonymous caller sees "not authenticated", not "your session
// was revoked", which would be a claim about a session they do not have).
// It must be after `ScopeResolutionMiddleware` besides, since the
// `Subject` and `StorageScope` it reads are that middleware's stamps.
//
// ─── The cache, and the staleness bound it defines ───────────────────
//
// One `IMemoryCache` entry per (scope, session) holding the last verdict,
// for `SessionRegistryOptions.RevocationCacheSeconds`. That window IS the
// revocation window, which is why it is a named config knob rather than a
// constant: an operator who needs a revoke to bite within a second sets it
// to zero and pays a store read per authenticated request; one who does
// not keeps the read off the hot path. Documented on the field itself, so
// it is a stated bound rather than a surprise (the phase's ask).
//
// The cache is **one-sided**. A `Revoked` verdict is entered with a long
// expiry, because revocation is terminal — a session that has been cut
// off never comes back, so there is no correctness reason to re-ask, and
// re-asking would put a store read on exactly the requests an attacker
// controls the rate of. An `Active` verdict expires after the window.
// So the only staleness that can exist is a session honoured slightly
// after it was revoked; there is no window in which a revoked session is
// honoured *again*.
//
// The cache is per-instance, so on a multi-instance deployment a
// revocation reaches instance B within B's own window. See the
// multi-instance caveat on `ISessionRegistry` for the two remedies.

[<Literal>]
let private SubjectItemsKey = "ToolUp.Subject"

[<Literal>]
let private StorageScopeItemsKey = "ToolUp.StorageScope"

/// Stamped on `HttpContext.Items` when a session id was derived for the
/// request, so a downstream handler (`SessionApiHandler`) can tell the
/// caller which of their listed sessions is the one they are sitting at
/// without re-deriving it — and, more to the point, without the
/// credential having to travel any further down the pipeline.
[<Literal>]
let CurrentSessionItemsKey = "ToolUp.SessionId"

/// What the last check concluded about a session.
type private Verdict =
    | Active
    | Revoked

/// Read the presented bearer credential. Only the `Bearer` scheme is
/// recognised: it is the one shape whose value is a token the caller
/// holds, and the derivation needs exactly that. `Basic` and friends are
/// deliberately unmatched — hashing a username:password pair into a
/// session id would derive a "session" that survives every sign-in, which
/// is the opposite of what a revocation list is for.
let private bearerCredential (ctx: HttpContext) : string option =
    match ctx.Request.Headers.TryGetValue "Authorization" with
    | true, values when values.Count > 0 ->
        let raw = string values[0]

        if raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
            let token = raw.Substring(7).Trim()
            if token = "" then None else Some token
        else
            None
    | _ -> None

let private subjectOf (ctx: HttpContext) : Subject option =
    match ctx.Items.TryGetValue SubjectItemsKey with
    | true, (:? Subject as s) -> Some s
    | _ -> None

let private scopeOf (ctx: HttpContext) : StorageScope option =
    match ctx.Items.TryGetValue StorageScopeItemsKey with
    | true, (:? StorageScope as s) -> Some s
    | _ -> None

let private serviceOf<'T> (ctx: HttpContext) : 'T option =
    match ctx.RequestServices.GetService typeof<'T> with
    | :? 'T as s -> Some s
    | _ -> None

/// Provider label recorded on the session. The `IAuthProvider`
/// interface carries no `Name`, so the composed implementation's type
/// name is the honest answer — it distinguishes an OIDC sign-in from a
/// header-auth one, which is what the session list needs, and it cannot
/// drift from what is actually composed.
let private providerLabel (ctx: HttpContext) : string =
    match ctx.RequestServices.GetService typeof<IAuthProvider> with
    | null -> "unknown"
    | provider -> provider.GetType().Name

let private cacheKey (scopeId: string) (sessionId: string) =
    "session:verdict:" + scopeId + ":" + sessionId

/// ASP.NET Core middleware refusing revoked sessions and recording live
/// ones. Path-scoped to `/api/*` — the surface a credential is actually
/// spent on. Static assets and the SPA shell carry no credential worth
/// recording, and gating them would make a revoked user unable to load
/// the page that tells them why.
type SessionRevocationMiddleware(next: RequestDelegate, config: ServerConfig) =

    let optionsOpt = SessionRegistryMode.options config.SessionRegistry

    /// A revoked verdict is held far longer than an active one — see the
    /// module header. Bounded rather than infinite so a long-lived
    /// process cannot accumulate entries for sessions nobody will ever
    /// present again.
    let revokedCacheLifetime = TimeSpan.FromHours 1.0

    member _.InvokeAsync(ctx: HttpContext) = task {
        match optionsOpt with
        | None ->
            // Defensive: the pipeline does not register this
            // middleware under `NoSessionRegistry`, so reaching here
            // means a consumer wired it by hand. Pass through
            // byte-for-byte rather than half-enabling a substrate
            // whose store is not composed (GP 11).
            do! next.Invoke ctx
        | Some options ->
            let isApi = ctx.Request.Path.StartsWithSegments(PathString "/api")

            let derived =
                if not isApi then
                    None
                else
                    match subjectOf ctx, scopeOf ctx with
                    | Some subject, Some scope ->
                        SessionRegistry.SessionIdentity.ofSubject subject (bearerCredential ctx)
                        |> Option.map (fun sid -> subject, scope, sid)
                    | _ -> None

            match derived, serviceOf<ISessionRegistry> ctx with
            | None, _
            | _, None ->
                // No derivable session (an unauthenticated request, a
                // header-auth deployment presenting no bearer token, a
                // share-token claim with its own revocation path), or
                // no registry resolved. Nothing to check and nothing
                // to record.
                do! next.Invoke ctx
            | Some(subject, scope, sessionId), Some registry ->
                ctx.Items[CurrentSessionItemsKey] <- box sessionId

                let cache = serviceOf<IMemoryCache> ctx
                let key = cacheKey scope.ScopeId sessionId

                let cached =
                    match cache with
                    | Some c ->
                        match c.TryGetValue key with
                        | true, (:? Verdict as v) -> Some v
                        | _ -> None
                    | None -> None

                let! verdict =
                    match cached with
                    | Some v -> System.Threading.Tasks.Task.FromResult v
                    | None -> task {
                        let! revoked = Async.StartImmediateAsTask(registry.IsRevoked(scope.ScopeId, sessionId))

                        if revoked then
                            return Revoked
                        else
                            // Cache miss on a live session is also
                            // where the record is created and
                            // touched, so the store is consulted
                            // once per session per window rather
                            // than once per request. `Record` is
                            // idempotent and non-clobbering, so
                            // running it on every miss is safe.
                            let now = DateTimeOffset.UtcNow

                            let userAgent =
                                match ctx.Request.Headers.TryGetValue "User-Agent" with
                                | true, values when values.Count > 0 -> Some(string values[0])
                                | _ -> None

                            let record: SessionRecord = {
                                SessionId = sessionId
                                UserId = AccessContext.unrestricted(subject).UserId
                                ScopeId = scope.ScopeId
                                DeviceDescriptor = SessionApi.deviceDescriptorOf userAgent
                                AuthProvider =
                                    match subject with
                                    | AnonymousSession _ -> "anonymous"
                                    | _ -> providerLabel ctx
                                CreatedAt = now
                                LastSeenAt = now
                                Status = ActiveSession
                                RevokedAt = None
                                RevokedBy = None
                            }

                            let! _ = Async.StartImmediateAsTask(registry.Record record)
                            let! _ = Async.StartImmediateAsTask(registry.Touch(scope.ScopeId, sessionId, now))
                            return Active
                      }

                match cache, cached with
                | Some c, None ->
                    let lifetime =
                        match verdict with
                        | Revoked -> revokedCacheLifetime
                        | Active -> TimeSpan.FromSeconds(float (max 0 options.RevocationCacheSeconds))

                    // A zero window means "no cache" — the operator
                    // asked for a store read per request, so writing a
                    // zero-lifetime entry (which `IMemoryCache` treats
                    // as immediately expired but still allocates)
                    // would be pure cost.
                    if lifetime > TimeSpan.Zero then
                        let entryOpts = MemoryCacheEntryOptions()
                        entryOpts.AbsoluteExpirationRelativeToNow <- Nullable lifetime
                        c.Set(key, verdict, entryOpts) |> ignore
                | _ -> ()

                match verdict with
                | Active -> do! next.Invoke ctx
                | Revoked ->
                    ctx.Response.StatusCode <- 401
                    ctx.Response.ContentType <- "application/json"

                    do!
                        ctx.Response.WriteAsync
                            "{\"error\":\"session_revoked\",\"status\":401,\"hint\":\"This session was signed out. Sign in again to continue.\"}"
    }