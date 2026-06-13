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