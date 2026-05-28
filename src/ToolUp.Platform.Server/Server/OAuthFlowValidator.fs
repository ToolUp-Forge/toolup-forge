module ToolUp.Platform.OAuthFlowValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 9m / Phase 10b — OAuth redirect-base preflight ───────────
//
// Verifies that `TOOLUP_OAUTH_REDIRECT_BASE` is sane before the SDK
// accepts requests. The redirect-base URL is the deployment's public
// origin (e.g. `https://app.example.com`) — it forms the prefix of
// the `redirect_uri` the SDK sends to upstream OAuth providers. Wrong
// or missing values produce a confusing failure mode where the user
// completes consent at the provider only to be redirected to a URL
// that doesn't resolve, or worse, redirected to a localhost URL that
// the user-agent can't reach.
//
// **Three checked conditions:**
//   - `Error`: the env var is set but doesn't parse as an absolute
//     HTTP/HTTPS URI. Fail loud so the operator fixes the config.
//   - `Warning`: the env var is unset. The SDK falls back to per-
//     request `Scheme + Host` derivation; production deployments
//     behind a TLS-terminating proxy MUST trust forwarded headers
//     for this to produce HTTPS URLs. The warning is the lowest-
//     friction prompt to set the env var explicitly.
//   - `Warning`: the env var resolves to `localhost` / `127.0.0.1`
//     while `ServerConfig.Mode` is non-Anonymous (production-shaped).
//     Almost certainly a dev URL leaked into a production deploy.
//
// **Gating.** The validator only runs when at least one
// `IOAuthCredentialFlow` is registered with DI. Deployments that don't
// use OAuth flows skip the validator entirely (no env var pressure).

[<Literal>]
let private RedirectBaseEnvVar = "TOOLUP_OAUTH_REDIRECT_BASE"

let private readEnv () : string option =
    match Environment.GetEnvironmentVariable RedirectBaseEnvVar with
    | null
    | "" -> None
    | v -> Some(v.Trim())

let private isAbsoluteHttpUri (raw: string) : bool =
    match Uri.TryCreate(raw, UriKind.Absolute) with
    | true, uri -> uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps
    | _ -> false

let private isLocalhostHost (raw: string) : bool =
    match Uri.TryCreate(raw, UriKind.Absolute) with
    | true, uri ->
        let h = uri.Host

        h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || h = "127.0.0.1"
        || h = "::1"
    | _ -> false

/// Phase 9m / Phase 10b config validator. Registered only when an
/// `IOAuthCredentialFlow` is present in DI; the registration check
/// lives in compose so the validator constructor stays simple.
type OAuthFlowValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "oauth-flow-redirect-base"
        member _.Timeout = timeout

        member _.Validate() = async {
            match readEnv () with
            | None ->
                return
                    Warning(
                        sprintf
                            "%s is not set. The OAuth substrate will derive the redirect URI from each request's Scheme + Host, which is correct only when ServerConfig.TrustForwardedHeaders is enabled and a TLS-terminating proxy is supplying X-Forwarded-Proto. Set %s=https://your-deployment.example.com to pin the redirect URI explicitly — eliminates a class of misconfigurations where /authorize and /callback see different scheme/host pairs."
                            RedirectBaseEnvVar
                            RedirectBaseEnvVar
                    )
            | Some raw ->
                if not (isAbsoluteHttpUri raw) then
                    return
                        Error(
                            sprintf
                                "%s = %s is not a valid absolute HTTP/HTTPS URL. Expected a deployment origin like 'https://app.example.com' (no trailing path). The substrate uses this value verbatim as the prefix for OAuth redirect URIs; an invalid value would cause every /authorize redirect to land on an unreachable URL after upstream consent."
                                RedirectBaseEnvVar
                                raw
                        )
                elif isLocalhostHost raw && DeploymentConfig.requiresAnyAuth config then
                    return
                        Warning(
                            sprintf
                                "%s = %s resolves to localhost while ServerConfig.Surfaces requires authentication (%s). This is almost certainly a dev URL leaked into a production-shaped deployment — upstream providers will redirect users to localhost, which the user-agent cannot reach. Set %s to the deployment's public origin."
                                RedirectBaseEnvVar
                                raw
                                (DeploymentConfig.surfacesLabel config)
                                RedirectBaseEnvVar
                        )
                else
                    return Ok
        }