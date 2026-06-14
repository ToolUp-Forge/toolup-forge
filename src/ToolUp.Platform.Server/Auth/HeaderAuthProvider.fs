module HeaderAuthProvider

open Microsoft.AspNetCore.Http
open ToolUp.Platform.Auth

/// Header name for user identification (must match client-side UserSession.userIdHeader)
let userIdHeader = "X-User-Id"

let private extractUser (httpCtx: HttpContext) =
    // Phase 6l.H — sanitise the X-User-Id value before constructing
    // an AuthenticatedUser. A header value carrying `..`, `/`, `\`,
    // NUL, control characters, or a Windows reserved name is treated
    // as missing — falls back to "anonymous", which the auth-enforce-
    // ment middleware then rejects in any non-Anonymous mode. The
    // sanitiser is defence-in-depth on top of the Phase 6l.A refusal
    // of HeaderAuthProvider in auth modes (which makes this provider
    // off-limits for production identity in the first place).
    let userId =
        match httpCtx.Request.Headers.TryGetValue userIdHeader with
        | true, values when values.Count > 0 ->
            match IdentitySanitiser.sanitiseScopeId (string values[0]) with
            | Result.Ok value -> value
            | Result.Error _ -> "anonymous"
        | _ -> "anonymous"

    {
        UserId = userId
        DisplayName = userId
        Email = None
        TenantId = None
        Roles = []
    }

/// Authentication provider that extracts user identity from a request header.
/// This is the default provider for development and simple deployments.
/// `ValidateRequest` always succeeds — header-based auth has no validation.
type HeaderAuthProvider() =
    interface IAuthProvider with
        member _.GetUser(ctx: RequestContext) = async { return extractUser (RequestContext.value ctx :?> HttpContext) }

        member _.ValidateRequest(ctx: RequestContext) = async {
            return Ok(extractUser (RequestContext.value ctx :?> HttpContext))
        }

        // The whole point of this provider: it trusts the X-User-Id
        // header with no cryptographic proof. Reporting `false` is what
        // makes the auth-mode validator refuse to boot a production-shaped
        // deployment on it (unless the operator opts in via the
        // AcceptHeaderAuthWhenAuthRequired escape hatch behind an mTLS
        // proxy). NEVER flip this to `true`.
        member _.IsCryptographicallyVerified = false