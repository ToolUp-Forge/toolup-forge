// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.PlatformAdminAuthorization

open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Phase 132 — admin structural backstop ───────────────────────────
//
// The raw Giraffe `_platform` admin handlers (`AdUnitConfigApi`,
// `PremiumUserApi`, `GrantPremiumApiHandler`) are NOT Fable.Remoting
// records, so the dispatcher's per-request authorisation classifier
// never sees them. Their only protection is a hand-written in-handler
// `AccessContext.canModifyPlatformConfig` check — so a future handler
// added under `/api/_platform/admin/*` (or the grant routes) that omits
// that one line fails *open*: reachable by any caller.
//
// This middleware is the fail-closed path-prefix backstop. It denies the
// admin path-prefixes for non-`PlatformAdmin` callers BEFORE the Giraffe
// terminal middleware dispatches to the handler, so the structural gate
// holds even when an individual handler forgets its in-line check. The
// in-handler checks remain (defence in depth) — this is additive (GP 11):
// it only ever *adds* a denial the handler would (correctly) also make.
//
// `ToolUp.PlatformRole` is stamped on `HttpContext.Items` by
// `ScopeResolutionMiddleware` (Phase 4b — resolved from
// `IPlatformAdminStore`), so this middleware MUST be registered AFTER it
// in the pipeline. It reads the same item the `ForgeAuthContext.HasRole`
// bridge and `AccessContext.canModifyPlatformConfig` read, so the three
// agree by construction.

[<Literal>]
let private AdminPrefix = "/api/_platform/admin"

[<Literal>]
let private TenantPrefix = "/api/_platform/tenants"

[<Literal>]
let private UsersPrefix = "/api/_platform/users"

/// Whether the request targets a Platform-Admin-gated surface that this
/// backstop enforces. Deliberately narrow:
///   * every method under `/api/_platform/admin/*` (AdUnit CRUD, premium-
///     user list, rate-limit event read) — all `canModifyPlatformConfig`-
///     gated;
///   * every method under `/api/_platform/tenants/*` — the Remoting tenant
///     lifecycle API, every method `[<RequiresRole "PlatformAdmin">]`
///     (already dispatcher-enforced; covered here as defence in depth);
///   * the grant/revoke writes `POST|DELETE /api/_platform/users/*/premium`.
///
/// Explicitly NOT covered (so the backstop never over-blocks a
/// legitimately-open or differently-gated surface):
///   * `GET /api/_platform/users/me/premium-status` — public by design
///     (anonymous → NotPremium); it ends `/premium-status`, not `/premium`,
///     and is a GET, so both guards below exclude it;
///   * `/api/_platform/encryption/*` — its handler has a role-OR-env-token
///     gate (the scripted-recovery path); a strict PlatformAdmin prefix
///     here would break that emergency access;
///   * `/api/_platform/ads/*`, `/api/_platform/consent` — public analytics
///     / consent sinks.
///
/// **Phase 336 — the premium discriminator is case- and trailing-slash-
/// insensitive, matching the prefix guards above.** `StartsWithSegments`
/// is `OrdinalIgnoreCase` by default, so before this phase the two halves
/// of the `UsersPrefix` arm disagreed: `/api/_platform/users/{id}/PREMIUM`
/// (or a trailing `/premium/`) satisfied the prefix and then failed the
/// case-sensitive `EndsWith "/premium"`, skipping the backstop entirely.
/// The method test had the same shape — `httpMethod = "POST"` is ordinal,
/// while Giraffe's `POST` combinator routes via
/// `HttpMethods.IsPost` (`OrdinalIgnoreCase`), so a lower-case `post`
/// reached the grant handler with the backstop silent.
///
/// Neither was a live end-to-end bypass: today's grant/revoke routes use
/// Giraffe `routef`, whose regex match IS case-sensitive, so `/PREMIUM`
/// 404s and the in-handler `canModifyPlatformConfig` check still holds
/// the method variant. That is exactly the point — this middleware exists
/// to hold when the *second* gate does not, so a backstop a casing trick
/// disables is a defect whether or not something else happens to catch it
/// today. A future handler mounted with `routeCif` or ASP.NET Core
/// endpoint routing (case-insensitive by default) closes the gap between
/// latent and live with no edit here.
///
/// Normalising is strictly more closed, never less: `/premium-status`
/// still does not end with `/premium` after the trailing-slash trim, so
/// the intentionally-open GET read stays open on both guards.
let internal requiresPlatformAdmin (httpMethod: string) (path: PathString) : bool =
    if path.StartsWithSegments(PathString AdminPrefix) then
        true
    elif path.StartsWithSegments(PathString TenantPrefix) then
        true
    elif path.StartsWithSegments(PathString UsersPrefix) then
        // Grant/revoke are the only mutating premium routes; the read
        // (`/premium-status`, GET) is intentionally open.
        let isMutating =
            System.String.Equals(httpMethod, "POST", System.StringComparison.OrdinalIgnoreCase)
            || System.String.Equals(httpMethod, "DELETE", System.StringComparison.OrdinalIgnoreCase)

        let pathValue = if path.HasValue then path.Value else ""

        // Trim EVERY trailing slash, not just one — `/premium//` is the
        // same request to a router that collapses them, and a guard that
        // handles exactly one variant of a repeatable character is the
        // same defect one character along.
        let trimmed = pathValue.TrimEnd '/'

        isMutating
        && trimmed.EndsWith("/premium", System.StringComparison.OrdinalIgnoreCase)
    else
        false

/// Read the server-resolved platform role stamped by
/// `ScopeResolutionMiddleware`. Mirrors the `ToolUp.PlatformRole` item
/// contract the `ForgeAuthContext.HasRole` bridge reads (Phase 132 core).
let internal isPlatformAdmin (ctx: HttpContext) : bool =
    match ctx.Items.TryGetValue "ToolUp.PlatformRole" with
    | true, (:? PlatformRole as role) -> role = PlatformRole.PlatformAdmin
    | _ -> false

/// Path-prefix authorisation backstop for the raw `_platform` admin
/// handlers. Registered after `ScopeResolutionMiddleware` and ahead of
/// the Giraffe router. No-ops (single `StartsWithSegments` check) for
/// every non-admin request, so registering unconditionally is cheap.
type PlatformAdminAuthorizationMiddleware(next: RequestDelegate) =
    member _.InvokeAsync(ctx: HttpContext) : Task =
        task {
            if
                requiresPlatformAdmin ctx.Request.Method ctx.Request.Path
                && not (isPlatformAdmin ctx)
            then
                // 403 matches the in-handler refusals (`writeError ctx 403
                // "platform admin role required"`) so the wire contract is
                // identical whether the backstop or the handler denies.
                ctx.Response.StatusCode <- 403
                ctx.Response.ContentType <- "application/json; charset=utf-8"
                do! ctx.Response.WriteAsync """{"error":"platform admin role required"}"""
            else
                do! next.Invoke ctx
        }
        :> Task