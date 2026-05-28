module ToolUp.Platform.ShareTokenAuth

open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open ToolUp.Platform

// ─── ShareTokenAuthMiddleware — Phase 66 Stream A.4 ──────────────────
//
// Extracts a share-token from the inbound request (`?token=` query
// parameter or `X-Share-Token` header), validates it through
// `IShareTokenStore.Validate`, and stashes the resolved
// `ShareTokenClaim` on `HttpContext.Items[ShareTokenClaimItemsKey]`
// for downstream consumption by `ScopeResolutionMiddleware` (Phase 66
// Stream A.6 wiring) → `ISubjectResolver` (Stream A.3) → handler.
//
// **No-op pass-through when no token is present** — the share-token
// path is opt-in for every request. Tokens are only validated when
// the request carries one, and even then the middleware never
// short-circuits the pipeline on Ok: every successful validation
// continues to `next.Invoke(ctx)`. The `SurfaceEnforcementMiddleware`
// (Stream A.5) is the layer that rejects claim-bearer subjects on
// routes whose `SurfaceRequirement` does not admit `ClaimBearerKind`.
//
// **MarkUsed is not called here** — the handler invokes
// `IShareTokenStore.MarkUsed` after the consuming operation succeeds.
// Matching today's Forms semantics (`PublicFormApiHandler`): a token
// that validates but whose downstream operation fails does not burn
// a use-slot.
//
// **Additive deployment (Stream A.4 only):** authored as a new file
// alongside the existing pipeline. Stream A.6 inserts this middleware
// into the pipeline ahead of `ScopeResolutionMiddleware` so
// `SubjectResolutionRequest.Claim` can be populated from
// `HttpContext.Items[ShareTokenClaimItemsKey]` at request-time.

[<Literal>]
let ShareTokenClaimItemsKey = "ToolUp.ShareTokenClaim"

[<Literal>]
let QueryParamName = "token"

[<Literal>]
let HeaderName = "X-Share-Token"

/// Read the first non-empty share-token candidate from the request.
/// Header beats query — both `?token=A&` *and* `X-Share-Token: B` is
/// treated as `B` (header is the more secret-friendly transport
/// because it doesn't appear in access logs or `Referer:` headers).
/// `None` when neither is present.
let internal tryReadToken (ctx: HttpContext) : string option =
    let headerValue =
        match ctx.Request.Headers.TryGetValue HeaderName with
        | true, values when values.Count > 0 ->
            let raw = string values[0]
            if raw <> "" then Some raw else None
        | _ -> None

    match headerValue with
    | Some _ -> headerValue
    | None ->
        match ctx.Request.Query.TryGetValue QueryParamName with
        | true, values when values.Count > 0 ->
            let raw = string values[0]
            if raw <> "" then Some raw else None
        | _ -> None

let private writeUnauthorized
    (ctx: HttpContext)
    (errorCode: string)
    (description: string)
    : System.Threading.Tasks.Task =
    task {
        ctx.Response.StatusCode <- 401
        ctx.Response.ContentType <- "application/json"

        // `WWW-Authenticate` per RFC 6750 — the scheme name is
        // `ShareToken` (parallels `Bearer`) so a client can distinguish
        // the share-token failure surface from a bearer-token-auth
        // failure on the same response. `error` and `error_description`
        // are encoded as quoted-string parameters.
        let header =
            sprintf "ShareToken error=\"%s\", error_description=\"%s\"" errorCode description

        ctx.Response.Headers["WWW-Authenticate"] <- StringValues header

        let body =
            sprintf "{\"error\":\"%s\",\"status\":401,\"error_description\":\"%s\"}" errorCode description

        do! ctx.Response.WriteAsync body
    }

/// Map `ShareTokenError` → (machine-readable `error` code, human-
/// readable `error_description`). `InvalidSignature` is collapsed
/// into `not_found` per the design's "indistinguishable to consumers"
/// rule (avoids leaking which tokens exist).
let internal classifyError (err: ShareTokenError) : string * string =
    match err with
    | ShareTokenError.Malformed -> "malformed_token", "Token string is not in the expected format."
    | ShareTokenError.InvalidSignature -> "not_found", "Token is invalid or has been revoked."
    | ShareTokenError.NotFound -> "not_found", "Token is invalid or has been revoked."
    | ShareTokenError.Expired -> "expired_token", "Token has expired."
    | ShareTokenError.RevokedToken -> "revoked_token", "Token has been revoked."
    | ShareTokenError.UseLimitExceeded -> "use_limit_exceeded", "Token's use limit has been exhausted."
    | ShareTokenError.RateLimited -> "rate_limited", "Token's rate limit has been exceeded."
    | ShareTokenError.StorageFailed _ -> "storage_error", "Token validation could not complete."

/// ASP.NET Core middleware that validates an inbound share-token and
/// stashes the resolved `ShareTokenClaim` on `HttpContext.Items`. See
/// the module preamble for the pass-through / MarkUsed / additive-
/// deployment contracts.
type ShareTokenAuthMiddleware(next: RequestDelegate) =
    member _.InvokeAsync(ctx: HttpContext) =
        task {
            match tryReadToken ctx with
            | None ->
                // No token present — pure pass-through. The
                // overwhelming majority of requests in a mixed-mode
                // deployment land here.
                do! next.Invoke(ctx)
            | Some token ->
                let store =
                    match ctx.RequestServices.GetService(typeof<IShareTokenStore>) with
                    | :? IShareTokenStore as s -> Some s
                    | _ -> None

                match store with
                | None ->
                    // Deployment does not have a share-token store
                    // wired (no `ClaimBearer _` in `Surfaces`). A
                    // request bearing a token in this shape is a
                    // misconfiguration on the caller's side, not a
                    // protocol-level token failure — surface as 401
                    // with the same shape so the operator's logs
                    // record the attempt.
                    do!
                        writeUnauthorized
                            ctx
                            "share_token_not_supported"
                            "This deployment does not accept share tokens."
                | Some s ->
                    let! result = s.Validate token

                    match result with
                    | Ok claim ->
                        ctx.Items[ShareTokenClaimItemsKey] <- box claim
                        do! next.Invoke(ctx)
                    | Error err ->
                        let code, description = classifyError err
                        do! writeUnauthorized ctx code description
        }
        :> System.Threading.Tasks.Task