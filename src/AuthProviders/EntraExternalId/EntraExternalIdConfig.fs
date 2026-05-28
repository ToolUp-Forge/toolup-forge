// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.EntraExternalIdConfig

open System

// ─── Entra External ID config + builders ─────────────────────────────
//
// Opinionated wrapper around `AuthConfig` for the Microsoft Entra
// External ID (customer-facing CIAM) identity service. The generic OIDC
// provider works against External ID with raw config, but two pieces of
// External-ID-specific knowledge are easy to get wrong:
//
//   1. **Issuer URL shape.** The CIAM tenant publishes its v2.0
//      metadata at `https://<tenant>.ciamlogin.com/<tenant>/v2.0` (or
//      `https://<customDomain>/<tenant>/v2.0` when a custom domain is
//      configured). The v1.0 endpoint exists but rejects the bound
//      audience format used by modern app registrations.
//   2. **User-flow policies.** Sign-up and sign-in are separately
//      configurable as `signUpPolicyId` / `signInPolicyId`. The browser
//      companion routes the user to one or the other based on which
//      affordance they clicked.
//
// `fromEnv` mirrors the SDK-wide Phase 11.G convention (`TOOLUP_*` env
// vars, all-uppercase, underscore-separated). Returning `None` when the
// required tenant + audience are absent is intentional — a deployment
// that doesn't use External ID must not be punished for importing the
// companion's referenced assembly.

/// Declarative configuration for the Entra External ID auth provider.
/// `tenant` and `audience` are required; everything else has a
/// well-defined default. Constructed values flow into the generic
/// `OidcAuthProvider` via `EntraExternalIdAuthProvider.create`.
type EntraExternalIdConfig = {
    /// External ID tenant identifier (the short name used in the
    /// `<tenant>.ciamlogin.com` default host, also embedded in the
    /// issuer path). Required.
    Tenant: string
    /// Optional custom-domain override (e.g. `login.example.com`). When
    /// set, replaces `<tenant>.ciamlogin.com` in the constructed
    /// issuer URL. The tenant id is still embedded in the path
    /// segment because External ID's v2.0 metadata structure requires
    /// it.
    CustomDomain: string option
    /// App-registration client id (`aud` claim on issued tokens).
    /// Required — without it the inner provider would not validate
    /// the audience claim, which would leave tokens issued for other
    /// applications in the same tenant accepted by this deployment.
    Audience: string
    /// Clock-skew tolerance (seconds) applied to `exp`/`nbf` claims.
    /// `None` -> SDK default (60s — standard practice). Propagated to
    /// the inner `OidcAuthProvider` via `AuthConfig.ClockSkewSeconds`
    /// (Cluster B4); cross-region setups can override. Raise
    /// cautiously since wider tolerance widens the replay window for
    /// stolen tokens.
    ClockSkewSeconds: int option
    /// User-flow policy id used for the sign-up affordance. When set,
    /// the browser companion surfaces a "Sign up" button alongside
    /// "Sign in" and routes the user through this policy.
    SignUpPolicyId: string option
    /// User-flow policy id used for the sign-in affordance. When set,
    /// the browser companion's "Sign in" button routes through this
    /// policy explicitly; absent, the default authorize endpoint is
    /// used.
    SignInPolicyId: string option
}

module EntraExternalIdConfig =
    let private envVar name =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> None
        | value -> Some value

    /// Build the constructed issuer URL from a tenant id + optional
    /// custom domain. Always produces the v2.0 path — the v1.0 endpoint
    /// exists but rejects the bound audience format used by current
    /// app registrations, which results in `InvalidAudience` errors
    /// that are hard to diagnose from logs alone.
    let issuerUrl (tenant: string) (customDomain: string option) : string =
        let host =
            customDomain |> Option.defaultWith (fun () -> sprintf "%s.ciamlogin.com" tenant)

        sprintf "https://%s/%s/v2.0" host tenant

    /// Build the authorize-endpoint URL for a specific user-flow
    /// policy. Used by the browser companion to route the sign-up
    /// affordance through the configured policy. Format mirrors the
    /// External ID `oauth2/v2.0/authorize?p=<policyId>` shape.
    let authorizeUrlForPolicy (config: EntraExternalIdConfig) (policyId: string) : string =
        let issuer = issuerUrl config.Tenant config.CustomDomain
        sprintf "%s/oauth2/v2.0/authorize?p=%s" issuer policyId

    /// Read configuration from the standard `TOOLUP_ENTRA_EXTERNAL_ID_*`
    /// environment variables. Returns `None` when either the tenant or
    /// audience is unset — a deployment that doesn't use External ID
    /// is expected to leave both unset and skip wiring the provider.
    /// The custom-domain / policy / clock-skew vars are optional and
    /// default to `None` / 60 when absent.
    let fromEnv () : EntraExternalIdConfig option =
        match envVar "TOOLUP_ENTRA_EXTERNAL_ID_TENANT", envVar "TOOLUP_ENTRA_EXTERNAL_ID_AUDIENCE" with
        | Some tenant, Some audience ->
            Some {
                Tenant = tenant
                CustomDomain = envVar "TOOLUP_ENTRA_EXTERNAL_ID_CUSTOM_DOMAIN"
                Audience = audience
                ClockSkewSeconds =
                    envVar "TOOLUP_ENTRA_EXTERNAL_ID_CLOCK_SKEW_SECONDS"
                    |> Option.bind (fun s ->
                        match Int32.TryParse s with
                        | true, n -> Some n
                        | _ -> None)
                SignUpPolicyId = envVar "TOOLUP_ENTRA_EXTERNAL_ID_SIGN_UP_POLICY"
                SignInPolicyId = envVar "TOOLUP_ENTRA_EXTERNAL_ID_SIGN_IN_POLICY"
            }
        | _ -> None