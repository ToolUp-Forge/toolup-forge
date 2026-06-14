// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Phase 9j — CSP middleware ───────────────────────────────────────
//
// Stamps the compose-time-aggregated `Content-Security-Policy` header
// (resolved from every registered `ICspContributor` by
// `SecurityHardening.aggregate`, injected as the
// `ResolvedCspPolicy` DI singleton) onto every response.
//
// **No-op default.** `SecurityHardening.aggregate` returns
// `{ Header = "" }` for `NoSecurityHardening`, so the empty-header
// fast-path keeps the registration zero-cost — registering the
// middleware unconditionally is fine (same contract as
// `SecurityHeadersMiddleware`).
//
// **Per-route override.** Like `SecurityHeadersMiddleware`, a handler
// that already wrote a `Content-Security-Policy` header before
// `OnStarting` runs wins — the middleware only sets it when absent.
// This is also what lets the static `ServerConfig.SecurityHeaders`
// map's CSP (stamped by `SecurityHeadersMiddleware`, registered
// earlier in the pipeline) take precedence when a deployment uses
// both mechanisms.
//
// **Position.** Registered right after `SecurityHeadersMiddleware`,
// ahead of every short-circuiting middleware, so the header lands on
// every status code (including 401 / 403 / 429 / 404).
//
// ─── Phase 156 — per-request nonce source mode ───────────────────────
//
// When the resolved policy is in nonce mode (`NonceMode = true`,
// `SecurityHardening.CspSourceMode = NonceCsp`), the header carries the
// `SecurityHardening.noncePlaceholder` token inside its `script-src` /
// `style-src` `'nonce-…'` sources. This middleware, BEFORE invoking the
// rest of the pipeline (so the value is available while the response body
// renders), generates a cryptographically-random nonce, stashes it on
// `HttpContext.Items` for the layout to read via `Csp.requestNonce`, and
// substitutes it into the header it stamps at `OnStarting`.
//
// **Cache interaction (load-bearing).** A nonce baked into a response
// body must match the header that body is served with. When the response
// is a `304 Not Modified` (the browser is revalidating a body it already
// holds, whose stored CSP header carries the original nonce), this
// middleware must NOT stamp a fresh-nonce header — doing so would
// overwrite the cached header's nonce and mismatch the cached body. So in
// nonce mode the header is suppressed on a `304`. The companion hazard —
// a render-cache *hit* serving a stored 200 body with a fixed nonce — is
// outside this middleware's view (the cache lives in the page handler);
// `CspNonceCacheValidator` warns at startup when nonce mode is composed
// alongside a render cache, steering cacheable deployments to hash mode
// (whose header is byte-stable and needs none of this).

/// Phase 156 — per-request CSP nonce: generation + the layout accessor.
[<RequireQualifiedAccess>]
module Csp =
    /// `HttpContext.Items` key under which `CspMiddleware` stashes the
    /// per-request nonce in nonce mode.
    [<Literal>]
    let nonceItemKey = "ToolUp.CspNonce"

    /// A fresh CSP nonce — 16 cryptographically-random bytes, base64
    /// (the wire form a CSP `'nonce-…'` source and an HTML `nonce="…"`
    /// attribute both expect).
    let generateNonce () : string =
        let bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes 16
        System.Convert.ToBase64String bytes

    /// Layout accessor — the per-request CSP nonce stamped by
    /// `CspMiddleware` in nonce mode, for emitting inline
    /// `<script nonce="…">` / `<style nonce="…">` that execute under a
    /// policy with no `'unsafe-inline'`. `None` when nonce mode is not
    /// composed (static / hash mode, or no security hardening) — a layout
    /// then omits the attribute.
    let requestNonce (ctx: HttpContext) : string option =
        match ctx.Items.TryGetValue nonceItemKey with
        | true, (:? string as n) -> Some n
        | _ -> None

type CspMiddleware(next: RequestDelegate, policy: SecurityHardening.ResolvedCspPolicy) =
    member _.InvokeAsync(ctx: HttpContext) = task {
        if policy.Header <> "" then
            // Nonce mode: mint the per-request nonce up front (so the
            // layout can read it while rendering) and resolve the header
            // value it will be stamped with. Static / hash mode: the
            // header is byte-stable, stamped verbatim — no per-request
            // work (GP 13).
            let headerValue =
                if policy.NonceMode then
                    let nonce = Csp.generateNonce ()
                    ctx.Items[Csp.nonceItemKey] <- box nonce
                    policy.Header.Replace(SecurityHardening.noncePlaceholder, nonce)
                else
                    policy.Header

            ctx.Response.OnStarting(fun () ->
                // Nonce-mode 304 skip: a revalidated body keeps the header
                // (and nonce) the browser already cached; stamping a fresh
                // nonce here would mismatch that body. Static / hash mode
                // is byte-stable, so the header is always safe to stamp.
                let suppressForNonce304 = policy.NonceMode && ctx.Response.StatusCode = 304

                if
                    not suppressForNonce304
                    && not (ctx.Response.Headers.ContainsKey "Content-Security-Policy")
                then
                    ctx.Response.Headers["Content-Security-Policy"] <-
                        Microsoft.Extensions.Primitives.StringValues headerValue

                System.Threading.Tasks.Task.CompletedTask)

        do! next.Invoke(ctx)
    }