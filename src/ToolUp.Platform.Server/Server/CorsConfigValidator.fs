module ToolUp.Platform.CorsConfigValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Gap audit pass-2 #6 — CORS AllowCredentials × wildcard refusal ─
//
// CORS spec § 7.2 forbids `Access-Control-Allow-Credentials: true`
// with `Access-Control-Allow-Origin: *` — it's structurally invalid.
// Browsers refuse to send cookies / `Authorization` headers, so the
// auth-cookie path documented for cross-origin SPAs breaks and the
// operator chases application-layer bugs.
//
// No escape hatch — wildcard + credentials is never the right
// answer for production. Operators wanting cross-origin auth name
// the specific origins explicitly (`Origins = ["https://app.foo.com"]`).
//
// Phase 462 — the check itself is the pure `credentialsWildcardConflict`
// below, shared by two call sites: this preflight validator (which
// reports it alongside every other validator outcome) and
// `ComposeRuntimeServices.assertCorsCredentialsCompatible`, which
// raises BEFORE the CORS policy is registered. Preflight runs at the
// compose tail, so until 462 the composition root had already
// registered the policy and silently dropped credentials with only a
// `Warn` to show for it.

/// Phase 462 — the pure credentials-plus-wildcard check. `Some message`
/// is the operator-facing refusal text; `None` means the configuration
/// is structurally legal (including `Cors = None`, i.e. CORS unused).
let credentialsWildcardConflict (config: ServerConfig) : string option =
    match config.Cors with
    | None -> None
    | Some cors ->
        if cors.AllowCredentials && cors.Origins |> List.contains "*" then
            Some
                "ServerConfig.Cors has AllowCredentials = true AND Origins contains \"*\". The CORS spec § 7.2 forbids this combination — browsers refuse to send credentials (cookies, Authorization headers) when the origin is a wildcard, so cross-origin auth flows break. Startup is refused before any CORS policy is registered. Replace the wildcard with an explicit list of origins, e.g. Origins = [\"https://app.example.com\"; \"https://staging.example.com\"]. If you genuinely need wildcard origins (no cross-origin auth required), set AllowCredentials = false."
        else
            None

/// Gap audit pass-2 #6 — refuse the
/// `AllowCredentials=true + Origins = "*"` combination at preflight.
type CorsConfigValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    // Wildcard-origins + AllowCredentials is a cross-origin auth
    // misconfiguration — security-class, so it runs even under SkipPreflight (GP 4).
    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "cors-config"
        member _.Timeout = timeout

        member _.Validate() = async {
            match credentialsWildcardConflict config with
            | Some message -> return Error message
            | None -> return Ok
        }