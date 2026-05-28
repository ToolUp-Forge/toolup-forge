// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Security.Cryptography
open System.Text
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.DataProtection
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.SurfaceEnforcement

// ─── Phase 9j — CSRF / cross-origin POST guard ───────────────────────
//
// **Stateless signed double-submit.** `GET /api/csrf-token` mints a
// random nonce sealed by ASP.NET DataProtection with a bounded
// lifetime; the sealed string is the token. It is returned in the JSON
// body AND set as a `SameSite=Strict` `XSRF-TOKEN` cookie. The client
// shell echoes it in the `X-CSRF-Token` header on every state-changing
// (`POST`/`PUT`/`PATCH`/`DELETE`) `/api/*` request. Validation requires
// that the header (a) fixed-time-equals the cookie (double-submit) AND
// (b) is a currently-valid DataProtection seal (proves THIS platform
// minted it, unexpired). **No ASP.NET session is involved.**
//
// Why stateless: the previous design bound the token to `ctx.Session`,
// which made it silently single-instance — a token minted on one
// replica failed on another, and every restart invalidated every
// token (403 storm with no diagnostic). The DataProtection key ring is
// persisted through the resolved `IBlobStorage` with a stable
// application name (see `SDK.Server` / `BlobXmlRepository`), so the
// seal verifies on every instance and survives restarts with no
// session store and no sticky load balancer.
//
// Why it still closes the CSRF gap: a cross-origin attacker can force
// the victim's browser to issue a state-changing request, but (1) the
// `SameSite=Strict` cookie is not sent cross-site, (2) the attacker
// cannot read the token (the `/api/csrf-token` response is
// same-origin-read-protected) to populate the header, and (3) even a
// fabricated token fails the DataProtection unseal. All three are
// independent.
//
// Exemptions: the token endpoint itself; routes whose
// `SurfaceRequirement.AcceptedSubjects` admits `AnonymousKind`
// (anonymous flows have no logged-in session to carry the token)
// or `ClaimBearerKind` (share-token flows are HMAC-gated already;
// requiring a session-bound CSRF nonce on top breaks the cookie-
// less embed flow). Phase 66 Stream A.6 — peer-bearer routes flow
// through the same `SurfaceRequirement` lens via the bridge
// registry built in `SDK.Server.fs`. `NoSecurityHardening`
// (default) makes the whole middleware a passthrough and the route
// is not mounted, so a stock deployment is unchanged (GP 13).

module Csrf =

    /// Header the client echoes the token in.
    [<Literal>]
    let HeaderName = "X-CSRF-Token"

    /// `SameSite=Strict` double-submit cookie name.
    [<Literal>]
    let CookieName = "XSRF-TOKEN"

    /// Route the client shell pre-fetches once at startup.
    [<Literal>]
    let TokenPath = "/api/csrf-token"

    [<Literal>]
    let private ProtectorPurpose = "ToolUp.Csrf.v1"

    /// Token validity window. Long enough to span a working session;
    /// short enough to bound replay of a leaked token. The client
    /// re-prefetches on load, so expiry is transparent.
    let private TokenLifetime = TimeSpan.FromHours 12.0

    /// 256-bit URL-safe random nonce (the sealed payload).
    let private randomNonce () =
        let bytes = RandomNumberGenerator.GetBytes 32

        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

    let private fixedTimeEquals (a: string) (b: string) =
        CryptographicOperations.FixedTimeEquals(
            ReadOnlySpan(Encoding.UTF8.GetBytes a),
            ReadOnlySpan(Encoding.UTF8.GetBytes b)
        )

    /// Time-limited protector from the app's DataProtection key ring
    /// (persisted to `IBlobStorage` in `SDK.Server`, so the seal
    /// verifies on any instance and survives restarts). `None` only if
    /// DataProtection is not registered (defensive — the ASP.NET host
    /// always registers it; `SDK.Server` additionally configures
    /// persistence + a stable application name).
    let private protector (ctx: HttpContext) : ITimeLimitedDataProtector option =
        match ctx.RequestServices.GetService<IDataProtectionProvider>() with
        | null -> None
        | p -> Some((p.CreateProtector ProtectorPurpose).ToTimeLimitedDataProtector())

    /// Mint a fresh stateless token: a random nonce sealed with a
    /// bounded lifetime. The sealed string is base64url — safe in the
    /// hand-built JSON body, the cookie value, and the header.
    let private mint (ctx: HttpContext) : string option =
        protector ctx
        |> Option.map (fun pr -> pr.Protect(randomNonce (), TokenLifetime))

    /// `GET /api/csrf-token` — returns `{"Token":"..."}` and sets the
    /// `SameSite=Strict` double-submit cookie. No session.
    let tokenRoute: HttpHandler =
        GET
        >=> route TokenPath
        >=> fun next ctx -> task {
            match mint ctx with
            | None ->
                // DataProtection unavailable — cannot issue a token.
                // Fail loud rather than hand out an unusable one.
                ctx.Response.StatusCode <- 503
                ctx.Response.ContentType <- "application/json; charset=utf-8"
                do! ctx.Response.WriteAsync """{"error":"csrf_unavailable"}"""
                return! next ctx
            | Some token ->
                let cookieOpts = CookieOptions()
                cookieOpts.Path <- "/"
                cookieOpts.SameSite <- SameSiteMode.Strict
                cookieOpts.Secure <- ctx.Request.IsHttps
                ctx.Response.Cookies.Append(CookieName, token, cookieOpts)

                ctx.Response.Headers["Cache-Control"] <- "no-store"
                ctx.Response.ContentType <- "application/json; charset=utf-8"
                do! ctx.Response.WriteAsync(sprintf "{\"Token\":\"%s\"}" token)
                return! next ctx
        }

    /// True when this request must carry a valid CSRF token. Phase
    /// 66 Stream A.6 — the carve-out is derived from
    /// `SurfaceRequirementRegistry`: routes whose admit set contains
    /// `AnonymousKind` or `ClaimBearerKind` skip CSRF (no session to
    /// bind the nonce against; share-token flows are HMAC-gated). The
    /// retiring `config.PeerRoutePrefixes` / `config.AnonymousRoutePrefixes`
    /// based carve-out is preserved via the bridge registry built in
    /// `SDK.Server.fs` — both prefix lists map to `SurfaceRequirement.public_`
    /// whose admit set contains both kinds, so this predicate keeps
    /// returning `true → exempt` for them.
    let requiresValidation (registry: SurfaceRequirementRegistry) (ctx: HttpContext) : bool =
        let m = ctx.Request.Method

        let stateChanging = m = "POST" || m = "PUT" || m = "PATCH" || m = "DELETE"

        let path =
            match ctx.Request.Path.Value with
            | null -> ""
            | p -> p

        let isApi = path.StartsWith("/api/", StringComparison.Ordinal)
        let isTokenRoute = path = TokenPath

        let admitsAnonOrClaim =
            let requirement = SurfaceRequirementRegistry.resolve registry m path
            let admits = requirement.AcceptedSubjects
            admits.Contains AnonymousKind || admits.Contains ClaimBearerKind

        stateChanging && isApi && not isTokenRoute && not admitsAnonOrClaim

    /// Stateless signed double-submit: the header must fixed-time-equal
    /// the `XSRF-TOKEN` cookie AND be a currently-valid DataProtection
    /// seal. No session lookup.
    let isTokenValid (ctx: HttpContext) : bool =
        let provided = ctx.Request.Headers[HeaderName].ToString()

        let cookied =
            match ctx.Request.Cookies.TryGetValue CookieName with
            | true, v -> v
            | _ -> ""

        not (String.IsNullOrEmpty provided)
        && not (String.IsNullOrEmpty cookied)
        && fixedTimeEquals provided cookied
        && (match protector ctx with
            | None -> false
            | Some pr ->
                try
                    pr.Unprotect provided |> ignore
                    true
                with _ ->
                    false)

type CsrfMiddleware(next: RequestDelegate, config: ServerConfig, registry: SurfaceRequirementRegistry) =
    member _.InvokeAsync(ctx: HttpContext) = task {
        if config.SecurityHardening = NoSecurityHardening then
            do! next.Invoke ctx
        elif Csrf.requiresValidation registry ctx then
            if Csrf.isTokenValid ctx then
                do! next.Invoke ctx
            else
                ctx.Response.StatusCode <- 403
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsync """{"error":"csrf_validation_failed"}"""
        else
            do! next.Invoke ctx
    }