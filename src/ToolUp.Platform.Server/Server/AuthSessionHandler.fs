// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open Giraffe
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Auth

// ─── Phase 133 — BFF-style server-set auth cookie ────────────────────
//
// Takes the primary JWT out of JS-reachable storage. Before this phase
// the client wrote the JWT to `localStorage` AND mirrored it into a
// `document.cookie` — neither of which can be `HttpOnly` (only a server
// `Set-Cookie` can), so a single injected script could read the bearer
// credential from either store and exfiltrate a usable
// `Authorization: Bearer` token.
//
// The reflection endpoint closes that gap. The client POSTs the JWT it
// just acquired (in the `Authorization` header) once; the server
// validates it through the registered `IAuthProvider` and, on success,
// reflects it into an `HttpOnly; Secure; SameSite=Strict; Path=/`
// cookie. The browser then sends that cookie automatically for SSE
// (`EventSource` — which cannot send custom headers) and same-origin
// XHR, and the token never re-enters JS-readable storage. An XSS can no
// longer dump a long-lived bearer from `localStorage` / `document.cookie`.
//
// **Why validate first, not blind-reflect.** Setting an attacker-chosen
// value as the caller's own cookie grants no privilege the caller did
// not already have (they could send any `Authorization` header
// directly), and the auth provider re-validates on every later request.
// But validating at reflect time gives a clean 401 instead of a cookie
// that silently 401s every subsequent call — and matches the SDK's
// fail-loud posture. The provider must therefore admit the bearer header
// on this call: the recommended `TokenLocation` is
// `BearerOrCookie "toolup-auth-token"`, which reads the header first
// (this endpoint) and the cookie afterwards (every later request + SSE).
//
// **CSRF.** `POST /api/auth/session` is a state-changing `/api/*` call,
// so under `DefaultSecurityHardening` the client request-guard attaches
// `X-CSRF-Token` and `CsrfMiddleware` validates it — same gate as every
// other mutating endpoint. Combined with `SameSite=Strict` on the issued
// cookie this fends off login-CSRF / session-fixation against the
// reflect endpoint.
//
// Mounted only when `ServerConfig.AuthCookieIssuance =
// EnabledAuthCookieIssuance`; the `NoAuthCookieIssuance` default leaves
// the route unmounted and an existing deployment byte-for-byte
// unchanged (GP 11).

module AuthSession =

    /// Cookie the reflection endpoint sets / clears. Matches the client
    /// `UserSession.authCookieName` and the conventional
    /// `TokenLocation.Cookie "toolup-auth-token"` / `BearerOrCookie`
    /// server-side read.
    [<Literal>]
    let CookieName = "toolup-auth-token"

    /// Route the client `UserSession` reflects its JWT through.
    [<Literal>]
    let Path = "/api/auth/session"

    /// Pull the raw token out of `Authorization: Bearer <token>`. The
    /// client sends it explicitly on the reflect POST (the request-guard
    /// would otherwise attach only the `X-User-Id` fallback on the
    /// server-cookie path, where no JS-readable bearer is held).
    let private bearerToken (ctx: HttpContext) : string option =
        match ctx.Request.Headers.TryGetValue "Authorization" with
        | true, values when values.Count > 0 ->
            let value = string values[0]

            if value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
                let token = value.Substring(7).Trim()
                if String.IsNullOrEmpty token then None else Some token
            else
                None
        | _ -> None

    let private cookieOptions (ctx: HttpContext) : CookieOptions =
        let opts = CookieOptions()
        opts.HttpOnly <- true
        opts.Path <- "/"
        opts.SameSite <- SameSiteMode.Strict
        // `Secure` only on HTTPS — browsers reject `Secure` cookies over
        // plain `http://localhost`, so a dev https-less run can still set
        // (and later clear) the cookie. Behind a TLS-terminating proxy,
        // ForwardedHeaders makes `IsHttps` reflect the original scheme.
        opts.Secure <- ctx.Request.IsHttps
        opts

    let private writeJson (ctx: HttpContext) (status: int) (body: string) = task {
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.Headers["Cache-Control"] <- "no-store"
        do! ctx.Response.WriteAsync body
    }

    /// `POST /api/auth/session` — validate the presented bearer token via
    /// the registered `IAuthProvider`, then reflect it into the HttpOnly
    /// cookie. 400 if no bearer is presented, 401 if it fails validation
    /// or resolves anonymous, 204 on success.
    let setRoute: HttpHandler =
        POST
        >=> route Path
        >=> fun next ctx -> task {
            match bearerToken ctx with
            | None ->
                do! writeJson ctx 400 """{"error":"missing_bearer_token"}"""
                return! next ctx
            | Some token ->
                let auth = ctx.RequestServices.GetService(typeof<IAuthProvider>) :?> IAuthProvider

                let! result = auth.ValidateRequest(RequestContextBuilder.ofHttpContext ctx)

                match result with
                | Ok user when not (AuthenticatedUser.isAnonymous user) ->
                    ctx.Response.Cookies.Append(CookieName, token, cookieOptions ctx)
                    ctx.Response.Headers["Cache-Control"] <- "no-store"
                    ctx.Response.StatusCode <- 204
                    return! next ctx
                | Ok _ ->
                    // Provider returned `Ok anonymous` (lenient header
                    // providers) — no real identity, so do not mint a
                    // session cookie.
                    do! writeJson ctx 401 """{"error":"token_validation_failed"}"""
                    return! next ctx
                | Error _ ->
                    do! writeJson ctx 401 """{"error":"token_validation_failed"}"""
                    return! next ctx
        }

    /// `DELETE /api/auth/session` — clear the HttpOnly cookie on sign-out
    /// (`Max-Age=0` via `Cookies.Delete`). Idempotent; always 204.
    let clearRoute: HttpHandler =
        DELETE
        >=> route Path
        >=> fun next ctx -> task {
            ctx.Response.Cookies.Delete(CookieName, cookieOptions ctx)
            ctx.Response.Headers["Cache-Control"] <- "no-store"
            ctx.Response.StatusCode <- 204
            return! next ctx
        }

    /// Both routes. Mounted by `BuildRouteHandlers` only when
    /// `ServerConfig.AuthCookieIssuance = EnabledAuthCookieIssuance`.
    let routes: HttpHandler list = [ setRoute; clearRoute ]