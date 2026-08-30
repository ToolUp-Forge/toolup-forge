module ToolUp.Platform.OidcClaimMappingAdvisoryValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── OIDC claim mapping — startup advisory ──────────────────────────
//
// `TOOLUP_OIDC_USER_ID_CLAIM` / `TOOLUP_OIDC_TENANT_ID_CLAIM` change
// WHICH claim becomes `AuthenticatedUser.UserId` / `TenantId`. That is
// the single most consequential thing a deployment can change about its
// identity model without changing its IdP: `UserId` is the key for RBAC
// entries, `TOOLUP_INITIAL_PLATFORM_ADMIN` matching, audit records, and
// (through the storage-scope resolver) the container every scoped read
// and write lands in. Turning the mapping on renames every one of those
// for every existing user, and turning it off renames them back.
//
// So the mapping is announced at startup rather than being inferable
// only from the env block. The shape mirrors the `ValidateIdToken`
// opt-out advisory on the client-side coherence validator: a `Warning`,
// not an `Error` — the configuration is legitimate and is exactly what
// the seam exists to support. What is NOT acceptable is it being
// invisible, because the failure it causes when set by mistake (or
// carried into an environment it was not meant for) presents as "every
// existing user is suddenly a new user with no data", which reads as a
// storage fault rather than an auth-config one.
//
// Two further conditions are worth a line each, and both are silent
// today:
//
//   • The mapping is set while `TOOLUP_AUTH_MODE` is not `oidc`. Nothing
//     reads it — `AuthProvider.fromEnv` only builds an `AuthConfig` on
//     the oidc branch — so the operator has configured something that
//     does nothing. That is a typo-class finding, not a hazard.
//   • `TOOLUP_OIDC_USER_ID_CLAIM` is set alongside an issuer for which
//     `fromEnv` would have auto-enabled `PreferOidWhenPresent`. Both
//     target `UserId`; the explicit mapping wins, and it is fail-closed
//     where the auto-enabled flag falls back. Same resolved identity for
//     a token that carries `oid`, different behaviour for one that does
//     not — worth saying out loud.
//
// Structural class, not security class. It reaches no external
// dependency and completes in microseconds, so `SkipPreflight` — the
// emergency lever for probes whose dependency may be down — is not what
// it should be bypassed by. It never returns `Error`, so running under
// `SkipPreflight` can only ever produce a log line, never a refusal.

[<Literal>]
let private AuthModeEnvVar = ConfigKeys.Names.authMode

[<Literal>]
let private OidcIssuerEnvVar = ConfigKeys.Names.oidcIssuer

[<Literal>]
let private UserIdClaimEnvVar = ConfigKeys.Names.oidcUserIdClaim

[<Literal>]
let private TenantIdClaimEnvVar = ConfigKeys.Names.oidcTenantIdClaim

/// Normalise a resolved value the way `claimMappingFromEnv` does, so the
/// advisory and the reader agree about what counts as "set". Reading
/// through the seam matters for the same reason it matters there: a
/// manifest-declared value must reach the advisory, or the advisory stays
/// quiet on precisely the deployments that most need the line.
///
/// Note each variable is applied to `ConfigResolution.tryValue` at its
/// own call site below rather than inside a `read key` helper — the
/// manifest-bindability conformance test resolves the seam call's
/// argument, and a key read through a wrapper is invisible to it.
let private nonBlank =
    Option.map (fun (value: string) -> value.Trim())
    >> Option.filter (String.IsNullOrWhiteSpace >> not)

/// Microsoft Entra issuers, for which `AuthProvider.fromEnv` auto-enables
/// `AuthConfig.PreferOidWhenPresent`. Mirrors `preferOidFromIssuer`; kept
/// permissive on the host suffix for the same reason.
let private isEntraIssuer (issuer: string) =
    let host =
        try
            (Uri issuer).Host.ToLowerInvariant()
        with _ ->
            ""

    host.EndsWith "login.microsoftonline.com" || host.EndsWith "ciamlogin.com"

/// Startup advisory naming any configured OIDC claim mapping, so a
/// changed identity source is visible in the preflight summary rather
/// than only in the environment block.
type OidcClaimMappingAdvisoryValidator(?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    // In-process, dependency-free, microseconds — the structural-class
    // cost test, met exactly.
    interface IStructuralClassValidator

    interface IConfigValidator with
        member _.Name = "oidc-claim-mapping-advisory"
        member _.Timeout = timeout

        member _.Validate() = async {
            let userIdClaim = ConfigResolution.tryValue UserIdClaimEnvVar |> nonBlank
            let tenantIdClaim = ConfigResolution.tryValue TenantIdClaimEnvVar |> nonBlank

            match userIdClaim, tenantIdClaim with
            | None, None -> return Ok
            | _ ->
                let authMode =
                    ConfigResolution.tryValue AuthModeEnvVar
                    |> Option.map (fun v -> v.Trim().ToLowerInvariant())

                let configured =
                    [
                        userIdClaim
                        |> Option.map (sprintf "%s=%s (-> AuthenticatedUser.UserId)" UserIdClaimEnvVar)
                        tenantIdClaim
                        |> Option.map (sprintf "%s=%s (-> AuthenticatedUser.TenantId)" TenantIdClaimEnvVar)
                    ]
                    |> List.choose id
                    |> String.concat "; "

                if authMode <> Some "oidc" then
                    return
                        Warning(
                            sprintf
                                "%s is set but %s is not 'oidc', so nothing reads it — the OIDC claim mapping is only built on the oidc branch of AuthProvider.fromEnv. Either set %s=oidc, or unset the claim-mapping variable so the deployment's configuration says what it does."
                                configured
                                AuthModeEnvVar
                                AuthModeEnvVar
                        )
                else
                    let preferOidCollision =
                        userIdClaim.IsSome
                        && (ConfigResolution.tryValue OidcIssuerEnvVar
                            |> nonBlank
                            |> Option.map isEntraIssuer
                            |> Option.defaultValue false)

                    let collisionNote =
                        if preferOidCollision then
                            sprintf
                                " Note the configured issuer is a Microsoft Entra endpoint, for which AuthProvider.fromEnv also auto-enables AuthConfig.PreferOidWhenPresent; the explicit mapping wins. They agree on a token carrying `oid`, and differ on one that does not — the mapping rejects, the auto-enabled flag would have fallen back to `sub`."
                        else
                            ""

                    return
                        Warning(
                            sprintf
                                "OIDC claim mapping is active: %s. Tokens are still fully validated first (signature, iss, aud, exp); the mapping is applied afterwards and is FAIL-CLOSED — a validated token that does not carry a named claim as a usable string is rejected rather than falling back to `sub`. This changes the identity key used by RBAC entries, %s matching, audit records and storage scopes, so an existing deployment turning it on will see previously-known users as new ones. This line is informational; the configuration is supported.%s"
                                configured
                                ConfigKeys.Names.initialPlatformAdmin
                                collisionNote
                        )
        }