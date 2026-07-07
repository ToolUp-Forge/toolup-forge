module ToolUp.Platform.CorsConfigValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Gap audit pass-2 #6 — CORS AllowCredentials × wildcard refusal ─
//
// The composition root in `SDK.Server.fs` detects the
// `AllowCredentials=true` + wildcard-origins combination and falls
// back to non-credentialed mode with a `Warn` log line. Operators
// missing the Warn line silently lose `AllowCredentials` semantics —
// browsers refuse to send cookies / `Authorization` headers, the
// auth-cookie path documented for cross-origin SPAs breaks, and the
// operator chases application-layer bugs.
//
// CORS spec § 7.2 forbids `Access-Control-Allow-Credentials: true`
// with `Access-Control-Allow-Origin: *` — it's structurally
// invalid. The Warn-and-continue posture is too lenient. Promote
// to an `Error`-level refusal so the operator has to fix the
// configuration before the deployment boots.
//
// No escape hatch — wildcard + credentials is never the right
// answer for production. Operators wanting cross-origin auth name
// the specific origins explicitly (`Origins = ["https://app.foo.com"]`).

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
            match config.Cors with
            | None -> return Ok
            | Some cors ->
                let hasWildcard = cors.Origins |> List.contains "*"

                if cors.AllowCredentials && hasWildcard then
                    return
                        Error(
                            "ServerConfig.Cors has AllowCredentials = true AND Origins contains \"*\". The CORS spec § 7.2 forbids this combination — browsers refuse to send credentials (cookies, Authorization headers) when the origin is a wildcard. The composition root falls back to non-credentialed mode at runtime, but that silently breaks cross-origin auth flows. Replace the wildcard with an explicit list of origins, e.g. Origins = [\"https://app.example.com\"; \"https://staging.example.com\"]. If you genuinely need wildcard origins (no cross-origin auth required), set AllowCredentials = false."
                        )
                else
                    return Ok
        }