// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open System
open System.Security.Cryptography
open System.Text
open Microsoft.AspNetCore.Http

// ─── AI surface trust model (Phase 6g.F) ─────────────────────────
//
// `AIMessageRequest.Surface` is a CLIENT-SUPPLIED field driving
// `FullPageOnly` tool gating (see AITypes.fs + TECHNICAL_GUIDE
// §"Surface determination & trust model"). By default the server takes
// it at face value; a deployment that wants defence-in-depth opts into
// deriving the authoritative surface from a signed server-issued
// capability cookie instead, so a client that lies about its surface is
// demoted server-side.

/// How the server decides a chat turn's authoritative `AISurface`.
///
/// `TrustClient` (default) takes `AIMessageRequest.Surface` at face
/// value — byte-for-byte the pre-6g.F behaviour, so a deployment that
/// doesn't need defence-in-depth pays nothing (GP 11 / GP 13).
///
/// `DeriveFromCookie signingKey` ignores the request field for the
/// authoritative surface and derives it from a short-lived HMAC-signed
/// capability cookie the server issues when the user navigates into a
/// full-page AI module. A client asserting `FullPage` without a
/// corroborating cookie is demoted to `SidePanel`. `signingKey` is the
/// HMAC secret; the mode carries it inline so the security logic stays a
/// pure function with no DI / secret-store plumbing.
type AISurfaceDerivationMode =
    | TrustClient
    | DeriveFromCookie of signingKey: byte[]

/// Signed capability-cookie machinery + the pure surface resolver.
/// The token format mirrors `ToolUp.Stripe.TierToken.Token`
/// (`{claim}.{expUnix}.{sigBase64Url}`, HMAC-SHA256) — zero extra
/// dependencies, same guarantee for the single-issuer case.
module AISurfaceCapability =
    /// Name of the signed full-page capability cookie.
    [<Literal>]
    let CookieName = "toolup-ai-surface"

    /// The one capability the cookie currently asserts. A constant so a
    /// future second capability extends the claim vocabulary without
    /// changing the cookie name.
    [<Literal>]
    let FullPageClaim = "fullpage"

    /// Default capability-cookie lifetime (seconds). Short-lived by
    /// design: the cookie is re-issued on each full-page navigation, so
    /// a stale one simply demotes to `SidePanel` rather than granting
    /// forever.
    [<Literal>]
    let DefaultLifetimeSeconds = 3600

    // ─── Signed token ────────────────────────────────────────────

    let private base64UrlEncode (bytes: byte[]) : string =
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

    let private hmac (secret: byte[]) (payload: string) : byte[] =
        use h = new HMACSHA256(secret)
        h.ComputeHash(Encoding.UTF8.GetBytes payload)

    /// Mint a signed capability token: `{claim}.{expUnix}.{sig}` where
    /// `sig = base64url(HMAC-SHA256("{claim}.{expUnix}", signingKey))`.
    let mintToken (claim: string) (lifetimeSeconds: int) (now: DateTimeOffset) (signingKey: byte[]) : string =
        let exp = now.ToUnixTimeSeconds() + int64 (max 1 lifetimeSeconds)
        let payload = sprintf "%s.%d" claim exp
        let sig' = hmac signingKey payload |> base64UrlEncode
        sprintf "%s.%s" payload sig'

    /// Validate a token, returning its claim string on success. Any
    /// parse / signature / expiry failure returns `None` (the caller
    /// demotes to `SidePanel`). An empty signing key returns `None` so a
    /// mis-wired config never silently grants. Signature comparison is
    /// constant-time.
    let validateToken (now: DateTimeOffset) (signingKey: byte[]) (token: string) : string option =
        if String.IsNullOrEmpty token || signingKey.Length = 0 then
            None
        else
            let parts = token.Split('.')

            if parts.Length <> 3 then
                None
            else
                let claim = parts[0]
                let expStr = parts[1]
                let providedSig = parts[2]
                let payload = sprintf "%s.%s" claim expStr
                let expected = hmac signingKey payload |> base64UrlEncode

                let sigOk =
                    CryptographicOperations.FixedTimeEquals(
                        ReadOnlySpan<byte>(Encoding.UTF8.GetBytes providedSig),
                        ReadOnlySpan<byte>(Encoding.UTF8.GetBytes expected)
                    )

                if not sigOk then
                    None
                else
                    match Int64.TryParse expStr with
                    | true, exp when exp > now.ToUnixTimeSeconds() -> Some claim
                    | _ -> None

    // ─── Pure surface resolver (the security-meaningful core) ─────

    /// Resolve the authoritative surface for a turn.
    ///
    /// Returns the effective `AISurface` plus an optional Warn message
    /// fired when the client-claimed surface disagrees with the
    /// server-derived one (the tell of a tampered / stale request). The
    /// caller logs the Warn; the returned surface is what the agent loop
    /// uses.
    ///
    /// `TrustClient` is the identity on `claimedSurface` and never
    /// reports a mismatch — behaviour is byte-for-byte the pre-6g.F path.
    let resolveSurface
        (mode: AISurfaceDerivationMode)
        (now: DateTimeOffset)
        (cookieValue: string option)
        (claimedSurface: AISurface)
        : AISurface * string option =
        match mode with
        | TrustClient -> claimedSurface, None
        | DeriveFromCookie signingKey ->
            let derived =
                match cookieValue |> Option.bind (validateToken now signingKey) with
                | Some claim when claim = FullPageClaim -> FullPage
                | _ -> SidePanel

            let warning =
                if derived <> claimedSurface then
                    Some(
                        sprintf
                            "AISurfaceDerivation=DeriveFromCookie: client claimed surface %A but the server-derived surface is %A; using %A (the request field is not trusted in this mode)."
                            claimedSurface
                            derived
                            derived
                    )
                else
                    None

            derived, warning

    // ─── HttpContext adapters ─────────────────────────────────────

    /// Read the raw capability cookie off a request, if present.
    let readCookie (ctx: HttpContext) : string option =
        match ctx.Request.Cookies.TryGetValue CookieName with
        | true, v when not (String.IsNullOrEmpty v) -> Some v
        | _ -> None

    /// Resolve the effective surface directly from an `HttpContext` +
    /// mode — a thin adapter over `resolveSurface` + `readCookie`.
    let resolveSurfaceFromRequest
        (mode: AISurfaceDerivationMode)
        (now: DateTimeOffset)
        (ctx: HttpContext)
        (claimedSurface: AISurface)
        : AISurface * string option =
        resolveSurface mode now (readCookie ctx) claimedSurface

    /// Issue the signed full-page capability cookie on a response.
    /// `HttpOnly` + `Secure` + `SameSite=Strict` — not readable by JS,
    /// not sent cross-site. A consumer calls this from the server-side
    /// handler that runs when the user navigates into a full-page AI
    /// module (the navigation trigger is a consumer concern; forge
    /// supplies the substrate).
    let issueFullPageCookie
        (lifetimeSeconds: int)
        (now: DateTimeOffset)
        (signingKey: byte[])
        (ctx: HttpContext)
        : unit =
        let token = mintToken FullPageClaim lifetimeSeconds now signingKey
        let opts = CookieOptions()
        opts.HttpOnly <- true
        opts.Secure <- true
        opts.SameSite <- SameSiteMode.Strict
        opts.Path <- "/"
        opts.MaxAge <- TimeSpan.FromSeconds(float (max 1 lifetimeSeconds))
        ctx.Response.Cookies.Append(CookieName, token, opts)

    /// Clear the capability cookie (e.g. on navigation away from the
    /// full-page module, or on sign-out).
    let clearCookie (ctx: HttpContext) : unit = ctx.Response.Cookies.Delete CookieName