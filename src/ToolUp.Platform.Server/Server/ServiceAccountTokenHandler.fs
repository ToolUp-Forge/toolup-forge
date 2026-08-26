// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ServiceAccountTokenHandler

open System
open System.Text
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open ToolUp.Platform
open ToolUp.Platform.Usage

// ─── ServiceAccountTokenMiddleware (Phase 527) ───────────────────────
//
// Turns an `Authorization: Bearer tusa_...` header into a resolved
// machine principal, and stashes it on `HttpContext.Items` in the two
// shapes the existing request pipeline already reads:
//
//   * `ShareTokenAuth.ShareTokenClaimItemsKey` ← a synthesised
//     `ShareTokenClaim`, so the shipped `ISubjectResolver` four-step
//     algorithm resolves the request to `ClaimBearer claim`;
//   * `ServiceAccountPermissionsItemsKey` ← the account's effective
//     module-permission map, which the `AccessContext` DI factory
//     prefers over the team-derived map when present.
//
// **Why a synthesised claim rather than a fifth `Subject` case.** The
// phase's own dependency line settles it — a service-account principal
// is a new claim-bearer SHAPE, not a new platform mode — and the
// engineering follows: `Subject` is matched exhaustively in dozens of
// places across three tiers, so a fifth case is a breaking change to
// every one of them, while a claim costs nothing and inherits the whole
// claim-bearer apparatus already in place. `AccessContext.configScope`
// derives the scope from `claim.ScopeId`, so a token issued under team A
// cannot address team B's storage even if a handler forgets to check
// (GP 4). `SurfaceEnforcementMiddleware` already refuses
// `ClaimBearerKind` on routes whose `SurfaceRequirement` does not admit
// it, so a deployment that has not opted claim-bearers into a surface
// does not silently gain a machine-callable one.
//
// A handler that needs to tell a machine caller from a share-link
// bearer matches `claim.ResourceKind = ServiceAccountTypes.ClaimResourceKind`.
//
// **The permission overlay is the load-bearing half, and it is not
// symmetric with the claim.** `AccessContext.canAccessModule` treats an
// EMPTY `ModulePermissions` map as UNRESTRICTED (opt-in RBAC — the
// default for a team that never configured permissions). For a human
// that is a reasonable floor; for a machine credential it is the
// opposite of what anyone means. So the overlay is refused empty at
// three independent points — the store rejects an account created or
// updated with an empty declared set, `ValidateToken` refuses a token
// whose effective set computes empty, and this middleware treats a
// present-but-empty overlay as a 401 rather than writing it. Three
// checks for one invariant is not redundancy here: the invariant is
// "an empty map never reaches `AccessContext`", and each layer can be
// reached without the others (a hand-composed store, a directly-called
// validator, a future second caller of this middleware).
//
// **Pass-through when no service-account token is present.** A request
// with no `Authorization` header, a non-Bearer scheme, or a Bearer token
// that does not carry the `tusa_` prefix continues untouched — the
// prefix is what makes coexistence with OIDC / JWT bearer auth on the
// same header possible without either scheme having to know about the
// other. The middleware itself is registered only when
// `ServerConfig.ServiceAccounts = EnabledServiceAccounts`, so a
// deployment that has not opted in has no middleware at all (GP 13).
//
// **The SCIM seam (Phase 530).** `src/AuthProviders/Scim/README.md`
// names this credential as the intended successor to its dedicated
// long-lived `SCIM_BEARER_TOKEN`: "same gate, a credential with an
// owner, an expiry and a revocation path". The pieces that seam needs
// are all present here — a `Bearer` credential on the standard header,
// constant-time comparison, fail-closed at every branch, and a
// per-request re-read so revocation bites immediately. Wiring SCIM onto
// it is deliberately NOT done in this phase: the SCIM gate currently
// resolves its token from `ISecretStore` and stamps the actor `_scim`,
// and moving it means deciding how a service account's declared
// module-permission set maps onto SCIM's team-membership authority —
// a design question, not a plumbing one.

/// `HttpContext.Items` key carrying the effective module-permission map
/// of a validated service-account token. Read by the `AccessContext` DI
/// factory in `ComposeScopeResolver`, which prefers it over the
/// team-derived `ToolUp.ModulePermissions` map.
///
/// A separate key rather than writing `ToolUp.ModulePermissions`
/// directly, because the two are written at different points in the
/// pipeline: this middleware runs BEFORE scope resolution (the claim has
/// to exist for the subject to resolve), and `ScopeResolutionMiddleware`
/// would then overwrite whatever this had written. Distinct keys make
/// the precedence explicit and readable at the point it is decided,
/// instead of implicit in middleware ordering.
[<Literal>]
let ServiceAccountPermissionsItemsKey = "ToolUp.ServiceAccountPermissions"

/// `HttpContext.Items` key carrying the resolved
/// `ServiceAccountPrincipal`. Handlers that want the account id or
/// display name for their own attribution read this rather than
/// re-deriving it from the claim.
[<Literal>]
let ServiceAccountPrincipalItemsKey = "ToolUp.ServiceAccountPrincipal"

[<Literal>]
let AuthorizationHeader = "Authorization"

[<Literal>]
let BearerScheme = "Bearer "

/// Extract a service-account token from `Authorization: Bearer <token>`.
/// `None` for a missing header, a non-Bearer scheme, or a Bearer token
/// that is not one of ours (no `tusa_` prefix) — all three are ordinary
/// pass-throughs, not failures.
let internal tryReadToken (ctx: HttpContext) : string option =
    match ctx.Request.Headers.TryGetValue AuthorizationHeader with
    | true, values when values.Count > 0 ->
        let raw = string values[0]

        if raw.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase) then
            let candidate = raw.Substring(BearerScheme.Length).Trim()

            if candidate.StartsWith(ServiceAccountTypes.TokenPrefix, StringComparison.Ordinal) then
                Some candidate
            else
                None
        else
            None
    | _ -> None

/// Map `ServiceAccountError` → (machine-readable `error` code,
/// human-readable `error_description`).
///
/// Every token-existence case collapses to one observable answer
/// (`invalid_token` / "Token is invalid, expired or revoked") so the
/// endpoint is not an oracle: a caller cannot learn whether an id
/// exists, whether it is expired rather than revoked, or whether the
/// owning account has been disabled. The distinctions survive in the
/// server-side classification for logs, which is where they are useful
/// and harmless.
///
/// `Malformed` is deliberately NOT collapsed — it says only "this is not
/// shaped like one of our tokens", which the caller already knows, and
/// keeping it distinct is what lets an operator tell a client bug from a
/// credential problem.
let internal classifyError (err: ServiceAccountError) : string * string =
    match err with
    | ServiceAccountError.Malformed -> "malformed_token", "Token string is not in the expected format."
    | ServiceAccountError.NotFound
    | ServiceAccountError.InvalidSecret
    | ServiceAccountError.Expired
    | ServiceAccountError.RevokedToken
    | ServiceAccountError.AccountDisabled
    | ServiceAccountError.AccountNotFound
    | ServiceAccountError.ScopeMismatch -> "invalid_token", "Token is invalid, expired or revoked."
    | ServiceAccountError.NoPermissionsDeclared ->
        "insufficient_scope", "The service account has no effective permissions."
    | ServiceAccountError.StorageFailed _ -> "storage_error", "Token validation could not complete."

let private writeUnauthorized (ctx: HttpContext) (errorCode: string) (description: string) : Threading.Tasks.Task = task {
    ctx.Response.StatusCode <- 401
    ctx.Response.ContentType <- "application/json"

    // RFC 6750 — the scheme name is `Bearer`, because that is the
    // scheme the caller actually presented; the `error` parameter
    // carries the discrimination.
    let header =
        sprintf "Bearer error=\"%s\", error_description=\"%s\"" errorCode description

    ctx.Response.Headers["WWW-Authenticate"] <- StringValues header

    let body =
        sprintf "{\"error\":\"%s\",\"status\":401,\"error_description\":\"%s\"}" errorCode description

    do! ctx.Response.WriteAsync body
}

/// Phase 120 — emit a uniform `AuthorizationDenied` row via
/// `IAuthAuditHook` alongside the 401, so an enumeration of guessed
/// tokens shows up in the `/dev/auth-denials` rollup keyed by route.
/// Best-effort; a hook failure never affects the response.
let private emitDenial (ctx: HttpContext) (errorCode: string) (description: string) : unit =
    try
        match ctx.RequestServices.GetService(typeof<IAuthAuditHook>) with
        | :? IAuthAuditHook as hook ->
            hook.RecordDenial {
                Route = sprintf "%s %s" ctx.Request.Method (string ctx.Request.Path)
                // A rejected token resolves to no principal — attribute
                // the denial to an anonymous probe. There is no PII to
                // leak and no scope to name.
                Subject = AnonymousSession "anonymous"
                Requirement = ShareTokenDenialRequirement
                Verdict = errorCode
                Reason = description
                ScopeId = None
                CorrelationId = ToolUp.Remoting.Server.CallContext.correlationId ()
            }
            |> Async.Start
        | _ -> ()
    with _ ->
        ()

/// Phase 9d attribution — one `api.requests` usage record per admitted
/// service-account request, so machine traffic is visible PER ACCOUNT
/// rather than smeared across the owning team's human usage.
///
/// `ScopeId` is the account's scope (the axis usage rolls up on) and the
/// account id rides `Metadata`, matching the shape
/// `AIProviderUsageMiddleware` and `FileManagement` already emit — the
/// admin usage dashboard therefore needs no change to show it.
///
/// Fire-and-forget and fully optional: a deployment with no `IUsageLog`
/// composed pays nothing (GP 13), and a metering failure never affects
/// the request.
let private meterRequest (ctx: HttpContext) (principal: ServiceAccountPrincipal) : unit =
    try
        match ctx.RequestServices.GetService(typeof<IUsageLog>) with
        | :? IUsageLog as usageLog ->
            usageLog.Record {
                RecordId = Guid.NewGuid()
                ScopeId = principal.ScopeId
                ResourceKind = ResourceKinds.apiRequests
                Quantity = 1M
                Unit = "requests"
                Origin = None
                Metadata =
                    Map.ofList [
                        "serviceAccountId", principal.AccountId
                        "serviceAccountName", principal.DisplayName
                        "tokenId", principal.TokenId
                    ]
                Timestamp = DateTime.UtcNow
            }
            |> Async.Start
        | _ -> ()
    with _ ->
        ()

/// Build the `ShareTokenClaim` a validated principal presents to the
/// subject resolver.
///
/// `AttributedHandle` is the account id, which makes
/// `AccessContext.UserId` the account id too (the resolver prefers
/// `AttributedHandle` over `IssuedBy`) — so every audit row, log line
/// and metric a machine call produces attributes to the SERVICE ACCOUNT
/// rather than to the human who happened to mint the credential. That is
/// the whole point of a first-class machine principal, and getting it
/// wrong here would silently re-introduce the impersonation this phase
/// exists to remove.
///
/// `UseLimit = None` — a service-account token is a long-lived
/// credential bounded by expiry and revocation, not by a use count; and
/// `UsedCount` is never incremented because nothing calls `MarkUsed` on
/// a synthesised claim. `RateLimit = None` leaves the claim-bearer rate
/// gate off by default; the platform's ordinary rate limiting still
/// applies.
let internal claimFor (principal: ServiceAccountPrincipal) : ShareTokenClaim = {
    TokenId = principal.TokenId
    ScopeId = principal.ScopeId
    ResourceKind = ServiceAccountTypes.ClaimResourceKind
    ResourceId = principal.AccountId
    AttributedHandle = Some principal.AccountId
    IssuedBy = principal.AccountId
    IssuedAt = DateTimeOffset.UtcNow
    ExpiresAt = principal.ExpiresAt
    UseLimit = None
    UsedCount = 0
    Revoked = false
    RateLimit = None
}

/// ASP.NET Core middleware validating an inbound service-account bearer
/// token. Registered ahead of `ScopeResolutionMiddleware` so the
/// synthesised claim is in `HttpContext.Items` before the subject is
/// resolved. See the module preamble for the pass-through, empty-map and
/// SCIM-seam contracts.
type ServiceAccountTokenMiddleware(next: RequestDelegate) =
    member _.InvokeAsync(ctx: HttpContext) =
        task {
            match tryReadToken ctx with
            | None ->
                // No service-account credential — pure pass-through. Every
                // human request and every other bearer scheme lands here.
                do! next.Invoke(ctx)
            | Some token ->
                // The credential is in a header rather than a URL, so it
                // does not ride `Referer`; `no-store` still matters because
                // a machine-facing response is as cacheable as any other and
                // the response may carry scope-bound data.
                if not (ctx.Response.Headers.ContainsKey "Cache-Control") then
                    ctx.Response.Headers["Cache-Control"] <- StringValues "no-store"

                let store =
                    match ctx.RequestServices.GetService(typeof<IServiceAccountStore>) with
                    | :? IServiceAccountStore as s -> Some s
                    | _ -> None

                match store with
                | None ->
                    // The middleware is only registered when the mode is
                    // enabled, so reaching here means the store failed to
                    // register — a deployment defect. Fail closed and say
                    // so, rather than passing the request through as
                    // anonymous and letting it succeed with the wrong
                    // authority.
                    emitDenial
                        ctx
                        "service_accounts_not_supported"
                        "This deployment does not accept service-account tokens."

                    do!
                        writeUnauthorized
                            ctx
                            "service_accounts_not_supported"
                            "This deployment does not accept service-account tokens."
                | Some s ->
                    let! result = s.ValidateToken token

                    match result with
                    | Error err ->
                        let code, description = classifyError err
                        emitDenial ctx code description
                        do! writeUnauthorized ctx code description
                    | Ok principal when principal.Permissions.IsEmpty ->
                        // Belt to the store's braces — see the preamble.
                        // An empty map downstream means UNRESTRICTED, so
                        // it is refused here even though `ValidateToken`
                        // already promises never to return one.
                        let code, description = classifyError ServiceAccountError.NoPermissionsDeclared
                        emitDenial ctx code description
                        do! writeUnauthorized ctx code description
                    | Ok principal ->
                        ctx.Items[ShareTokenAuth.ShareTokenClaimItemsKey] <- box (claimFor principal)
                        ctx.Items[ServiceAccountPermissionsItemsKey] <- box principal.Permissions
                        ctx.Items[ServiceAccountPrincipalItemsKey] <- box principal
                        meterRequest ctx principal
                        do! next.Invoke(ctx)
        }
        :> Threading.Tasks.Task