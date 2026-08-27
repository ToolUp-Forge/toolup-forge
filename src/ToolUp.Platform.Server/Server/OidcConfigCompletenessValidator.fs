module ToolUp.Platform.OidcConfigCompletenessValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Gap #4 — OIDC config completeness (cascading-error fix) ────────
//
// When `TOOLUP_AUTH_MODE=oidc` but `TOOLUP_OIDC_ISSUER` is unset, the
// consumer's composition root logs a Warn
// "TOOLUP_AUTH_MODE=oidc but TOOLUP_OIDC_ISSUER not set, falling back
// to HeaderAuthProvider" and returns `HeaderAuthProvider`. The
// HeaderAuthProviderModeValidator then refuses startup because
// HeaderAuthProvider isn't allowed in auth mode — but the error
// message names the wrong cause (HeaderAuthProvider) rather than the
// root cause (missing OIDC_ISSUER).
//
// Operator UX: two error messages for one root cause. Diagnosis time
// burns until the operator scrolls back to the Warn line that named
// the actual missing env var.
//
// This validator fires BEFORE HeaderAuthProviderModeValidator (by name
// ordering at registration; both run in parallel, both errors land in
// the preflight summary, but THIS validator's message names the env
// var directly so the operator sees the actionable line first when
// scanning the summary).
//
// Severity: Error. Same as HeaderAuthProviderModeValidator — refuses
// startup. No escape hatch — there's no legitimate
// TOOLUP_AUTH_MODE=oidc + TOOLUP_OIDC_ISSUER-unset configuration; if
// the operator wants HeaderAuthProvider, they should unset
// TOOLUP_AUTH_MODE entirely.

[<Literal>]
let private AuthModeEnvVar = ConfigKeys.Names.authMode

[<Literal>]
let private OidcIssuerEnvVar = ConfigKeys.Names.oidcIssuer

// Phase 698 — resolved through the Phase-696 `ConfigResolution` seam, so
// a manifest-declared value reaches the preflight that refuses on it.

/// Gap #4 — config validator that refuses startup when
/// `TOOLUP_AUTH_MODE=oidc` is set without `TOOLUP_OIDC_ISSUER`.
/// Surfaces the root cause directly so the operator doesn't need to
/// scroll back through Warn lines to diagnose.
type OidcConfigCompletenessValidator(?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    // An unset OIDC issuer falls back to an insecure default — an
    // unauthenticated-access hole; security-class, so it survives SkipPreflight.
    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "oidc-config-completeness"
        member _.Timeout = timeout

        member _.Validate() = async {
            let authMode =
                ConfigResolution.tryValue AuthModeEnvVar
                |> Option.map (fun v -> v.Trim().ToLowerInvariant())

            let oidcIssuer = ConfigResolution.tryValue OidcIssuerEnvVar |> Option.map _.Trim()

            match authMode, oidcIssuer with
            | Some "oidc", None ->
                return
                    Error(
                        "TOOLUP_AUTH_MODE=oidc requires TOOLUP_OIDC_ISSUER. `AuthProvider.fromEnv` refuses to start on this combination (Gap audit 2026-06-12 Auth G6); this validator names the root cause for composition roots with hand-written auth dispatch. Set TOOLUP_OIDC_ISSUER to your OIDC provider's discovery URL (examples: https://my.clerk.accounts.dev, https://login.microsoftonline.com/{tenantId}/v2.0, https://your-domain.auth0.com). Optionally set TOOLUP_OIDC_AUDIENCE to your specific app's audience for defence-in-depth. To use HeaderAuthProvider in dev, unset TOOLUP_AUTH_MODE entirely (and set TOOLUP_ACCEPT_HEADER_AUTH_IN_AUTH_MODE=1 if Mode != Anonymous)."
                    )
            | _ -> return Ok
        }