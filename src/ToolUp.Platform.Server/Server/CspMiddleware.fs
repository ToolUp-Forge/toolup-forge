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

type CspMiddleware(next: RequestDelegate, policy: SecurityHardening.ResolvedCspPolicy) =
    member _.InvokeAsync(ctx: HttpContext) = task {
        if policy.Header <> "" then
            ctx.Response.OnStarting(fun () ->
                if not (ctx.Response.Headers.ContainsKey "Content-Security-Policy") then
                    ctx.Response.Headers["Content-Security-Policy"] <-
                        Microsoft.Extensions.Primitives.StringValues policy.Header

                System.Threading.Tasks.Task.CompletedTask)

        do! next.Invoke(ctx)
    }