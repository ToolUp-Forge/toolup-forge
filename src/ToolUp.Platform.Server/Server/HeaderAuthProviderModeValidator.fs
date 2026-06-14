module ToolUp.Platform.HeaderAuthProviderModeValidator

open System
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.ConfigValidation

// ─── Phase 6l.A / Wave 19 — unverified auth provider in auth modes ───
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
// requiring `Mode` with an *unverified* provider. The escape hatch is
// `ServerConfig.AcceptHeaderAuthWhenAuthRequired = true`, intended
// for behind-mTLS deployments where a verified-identity proxy strips
// any incoming `X-User-Id` and re-injects the value it has cryptograph-
// ically verified itself. Default `false` — explicit opt-in only.
//
// Wave 19 (Auth-core audit, Mode-gating Finding 3) — the gate now reads
// the provider's `IAuthProvider.IsCryptographicallyVerified` capability
// rather than `GetType() = typeof<HeaderAuthProvider>`. The exact-type
// check let a consumer subclass / wrapper / hand-rolled header-trusting
// provider evade the gate; capability is fail-closed — any provider that
// does not affirmatively report verification is refused in an auth mode.

/// Phase 6l.A / Wave 19 — config validator that refuses an *unverified*
/// auth provider (`IAuthProvider.IsCryptographicallyVerified = false`) in
/// authenticated platform modes unless the operator has explicitly opted
/// in via `ServerConfig.AcceptHeaderAuthWhenAuthRequired`. Reads the
/// capability, not the concrete type, so wrappers/subclasses can't evade
/// it (fail-closed by capability).
type HeaderAuthProviderModeValidator(config: ServerConfig, authProvider: IAuthProvider, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "header-auth-mode"
        member _.Timeout = timeout

        member _.Validate() =
            ConfigValidator.gatedAuthValidation
                config
                (fun () ->
                    not authProvider.IsCryptographicallyVerified
                    && not config.AcceptHeaderAuthWhenAuthRequired)
                (fun () ->
                    Error(
                        sprintf
                            "ServerConfig.Surfaces = %s requires a verified auth provider, but the registered provider (%s) reports IAuthProvider.IsCryptographicallyVerified = false — it establishes identity without cryptographic proof (e.g. HeaderAuthProvider trusts the X-User-Id header), so any caller can spoof any user id, breaking per-tenant data isolation. Configure a verifying provider — OIDC (TOOLUP_AUTH_MODE=oidc + TOOLUP_OIDC_ISSUER=<your-issuer>) or StaticJwt — or set ServerConfig.AcceptHeaderAuthWhenAuthRequired = true if your deployment is behind an mTLS proxy that strips and re-injects the header. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            (DeploymentConfig.surfacesLabel config)
                            (authProvider.GetType().Name)
                    ))