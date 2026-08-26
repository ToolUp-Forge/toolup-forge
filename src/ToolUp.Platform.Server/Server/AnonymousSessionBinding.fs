// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AnonymousSessionBinding

open System
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection

// ─── Phase 135 — anonymous-session ownership binding ────────────────
//
// The anonymous session id is a client-generated GUID the browser sends
// in `X-User-Id` (and which also rides the SSE `?userId=` query string)
// — it is NOT secret. Without a server-issued, browser-bound proof of
// ownership, the anonymous→authenticated migration trusts that
// self-asserted id: a signed-in attacker sending `X-User-Id: <victim's
// anonymous GUID>` with their own bearer token would have the victim's
// anonymous-session data migrated into the attacker's account
// (horizontal data theft).
//
// This module issues a server-minted, HttpOnly, DataProtection-sealed
// cookie that binds the anonymous session id to the browser. The
// migration only fires when the cookie cryptographically proves THIS
// server issued the binding for THIS session id — so possessing the
// (non-secret) id alone is insufficient.
//
// DataProtection (not a hand-rolled HMAC) is used deliberately: it is
// already the SDK's sealing primitive (CSRF tokens), its key ring is
// persisted to `IBlobStorage` in `SDK.Server` so the seal verifies on
// any instance and survives restarts, and `Unprotect` authenticates the
// seal (AEAD) in constant time over the MAC.
//
// ─── Phase 337 — the id is SERVER-ISSUED, not merely bound ───────────
//
// Phase 135 closed the migration leg but left the *scope-selection* leg
// open, and trust-on-first-use is why: binding whatever id an unbound
// browser asserts hands an attacker a binding for the victim's id on
// request one. `DefaultSubjectResolver` was therefore still building
// `Subject.AnonymousSession` straight from the self-asserted `X-User-Id`
// — so a caller could address any anonymous session's storage scope by
// naming it.
//
// The fix inverts the direction of trust: the sealed cookie *carries*
// the session id (`boundSessionId`), and a client-supplied value can
// only ever ECHO it. A request with no valid binding does not get the
// id it asked for — it gets a fresh server-minted one (`issue`), and
// the cookie set on that response is what makes the NEXT request
// continuous. No client-supplied value ever selects a scope, so there
// is no first-use window to exploit (GP 4).

/// HttpOnly cookie carrying the binding token. Not JS-readable — the
/// browser presents it automatically on the post-sign-in request.
[<Literal>]
let CookieName = "toolup-anon-binding"

[<Literal>]
let private ProtectorPurpose = "ToolUp.AnonymousSessionBinding.v1"

/// Binding lifetime. Generous — an anonymous session may persist for
/// weeks before the user signs in and triggers migration.
let private BindingLifetime = TimeSpan.FromDays 30.0

let private protector (ctx: HttpContext) : ITimeLimitedDataProtector option =
    match ctx.RequestServices.GetService<IDataProtectionProvider>() with
    | null -> None
    | p -> Some((p.CreateProtector ProtectorPurpose).ToTimeLimitedDataProtector())

/// Mint a binding token sealing `sessionId`. A *valid* token in the
/// HttpOnly cookie proves the server issued it for this session — i.e.
/// the browser actually held the anonymous session, not merely that it
/// knows the (non-secret) id. `None` only when DataProtection is
/// unavailable (it is always registered by the ASP.NET host).
let mint (ctx: HttpContext) (sessionId: string) : string option =
    protector ctx |> Option.map (fun pr -> pr.Protect(sessionId, BindingLifetime))

/// Verify `cookieValue` is a currently-valid binding the server issued
/// for `claimedSessionId`. `Unprotect` both authenticates the seal and
/// recovers the bound id; equality with the claimed id closes the loop.
/// Any failure (tampered / expired / wrong purpose / mismatched id) →
/// `false`, fail-closed.
let verify (ctx: HttpContext) (cookieValue: string) (claimedSessionId: string) : bool =
    match protector ctx with
    | None -> false
    | Some pr ->
        try
            let bound = pr.Unprotect cookieValue
            String.Equals(bound, claimedSessionId, StringComparison.Ordinal)
        with _ ->
            false

/// Read the binding cookie value, if present and non-empty.
let readCookie (ctx: HttpContext) : string option =
    match ctx.Request.Cookies.TryGetValue CookieName with
    | true, v when not (String.IsNullOrEmpty v) -> Some v
    | _ -> None

/// Is the browser already validly bound to `sessionId`?
let isBoundTo (ctx: HttpContext) (sessionId: string) : bool =
    readCookie ctx |> Option.exists (fun c -> verify ctx c sessionId)

/// Set the HttpOnly binding cookie. `Secure` on https; `SameSite=Lax`
/// so it rides the top-level navigation that follows sign-in; `Path=/`.
let setCookie (ctx: HttpContext) (token: string) : unit =
    let opts = CookieOptions()
    opts.HttpOnly <- true
    opts.Secure <- ctx.Request.IsHttps
    opts.SameSite <- SameSiteMode.Lax
    opts.Path <- "/"
    opts.MaxAge <- Nullable BindingLifetime
    ctx.Response.Cookies.Append(CookieName, token, opts)

/// Phase 337 — the anonymous session id THIS server issued to THIS
/// browser, recovered from the sealed cookie. `Unprotect` authenticates
/// the seal before the payload is read, so a `Some` return is a
/// server-issued value by construction; a tampered, expired,
/// wrong-purpose or absent cookie is `None`.
///
/// This is the only sanctioned source of an anonymous session id for
/// scope selection. `X-User-Id` is a client-supplied echo and must
/// never be read in its place — that is the Phase 337 defect.
let boundSessionId (ctx: HttpContext) : string option =
    match protector ctx, readCookie ctx with
    | Some pr, Some cookieValue ->
        try
            match pr.Unprotect cookieValue with
            | null -> None
            | bound when bound = "" -> None
            | bound -> Some bound
        with _ ->
            None
    | _ -> None

/// Phase 337 — mint a FRESH server-issued anonymous session id, seal it
/// and set the binding cookie on the response. The returned id is
/// server-issued and therefore verified by construction; the caller may
/// use it to select a scope immediately, and the cookie makes the next
/// request from this browser continuous with this one.
///
/// Returns the id even when DataProtection is unavailable (no cookie is
/// then set): the request still needs *a* session id, and an
/// unrecoverable one is strictly safer than an attacker-chosen one — it
/// addresses a scope nobody else can name.
let issue (ctx: HttpContext) : string =
    let sessionId = Guid.NewGuid().ToString()

    match mint ctx sessionId with
    | Some token -> setCookie ctx token
    | None -> ()

    sessionId

/// Phase 337 — ensure the browser holds a valid binding for
/// `sessionId`, minting and setting the cookie when it does not. Called
/// once per request by `ScopeResolutionMiddleware` after the subject
/// resolves to `AnonymousSession`, so a freshly-minted session (and a
/// session recovered on the legacy fallback path) becomes continuous
/// from the next request onward. A no-op when the binding is already
/// present, so a steady-state anonymous request emits no `Set-Cookie`.
let ensureBound (ctx: HttpContext) (sessionId: string) : unit =
    if sessionId <> "" && not (isBoundTo ctx sessionId) then
        match mint ctx sessionId with
        | Some token -> setCookie ctx token
        | None -> ()