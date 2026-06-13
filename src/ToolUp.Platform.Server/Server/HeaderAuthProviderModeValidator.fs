module ToolUp.Platform.HeaderAuthProviderModeValidator

open System
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.ConfigValidation

// ─── Phase 6l.A — HeaderAuthProvider in authenticated modes ──────────
//
// `HeaderAuthProvider` reads `X-User-Id` from request headers and trusts
// the value at face value — no signature, no JWT, no cryptographic
// proof. Documented as "dev only" but nothing structurally prevents a
// production deployment from running it: the SDK boots with whatever
// `IAuthProvider` is registered, and `HeaderAuthProvider` is the
// fallback when `TOOLUP_AUTH_MODE` is unset or misconfigured.
//
// Production failure mode: the deployment runs in `Individual` / `Team`
// / `MultiTeam` (or `AuthenticatedEphemeral`), the `AuthEnforcementMiddle-
// ware` checks `ctx.Items["ToolUp.User"]` for a non-anonymous user, the
// header-trust path lets any caller spoof any user id by setting
// `X-User-Id`, and per-tenant data (KB, conversations, team scopes) is
// keyed by the spoofed value.
//
// This validator refuses startup when a deployment combines an auth-
// requiring `Mode` with `HeaderAuthProvider`. The escape hatch is
// `ServerConfig.AcceptHeaderAuthWhenAuthRequired = true`, intended
// for behind-mTLS deployments where a verified-identity proxy strips
// any incoming `X-User-Id` and re-injects the value it has cryptograph-
// ically verified itself. Default `false` — explicit opt-in only.

/// Phase 6l.A — config validator that refuses `HeaderAuthProvider` in
/// authenticated platform modes unless the operator has explicitly
/// opted in via `ServerConfig.AcceptHeaderAuthWhenAuthRequired`.
type HeaderAuthProviderModeValidator(config: ServerConfig, authProvider: IAuthProvider, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "header-auth-mode"
        member _.Timeout = timeout

        member _.Validate() =
            ConfigValidator.gatedAuthValidation
                config
                (fun () ->
                    authProvider.GetType() = typeof<HeaderAuthProvider.HeaderAuthProvider>
                    && not config.AcceptHeaderAuthWhenAuthRequired)
                (fun () ->
                    Error(
                        sprintf
                            "ServerConfig.Surfaces = %s requires a verified auth provider, but HeaderAuthProvider is registered. HeaderAuthProvider trusts the X-User-Id header without cryptographic proof — any caller can spoof any user id, breaking per-tenant data isolation. Configure OIDC (TOOLUP_AUTH_MODE=oidc + TOOLUP_OIDC_ISSUER=<your-issuer>) or set ServerConfig.AcceptHeaderAuthWhenAuthRequired = true if your deployment is behind an mTLS proxy that strips and re-injects the header. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            (DeploymentConfig.surfacesLabel config)
                    ))