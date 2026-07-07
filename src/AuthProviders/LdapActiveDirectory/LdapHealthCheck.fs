// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.LdapHealthCheck

open System
open ToolUp.Platform.Secrets
open ToolUp.Platform.HealthChecks
open ToolUp.AuthProviders.LdapConfig
open ToolUp.AuthProviders.LdapDirectory

// ─── LDAP connectivity health probe (Phase 9k IHealthCheck) ─────────
//
// Readiness probe: can the deployment bind to the directory *and* does a
// probe search return at least one user? The two-part check is
// deliberate — a bind that succeeds against a directory whose search
// base is wrong (a trailing-comma typo, the wrong OU, a base under a
// different naming context) returns zero users silently, and every user
// sign-in then fails with "user not found" while `/ready` stays green.
// This probe surfaces that misconfiguration as `Degraded` — reachable
// but behaving abnormally — without tripping `/ready` to 503 (the
// directory *is* up; the config is wrong, an operator concern).

/// Probe implementation. Hidden behind `create` / `tryFromEnv` so the
/// module name doesn't shadow the type at call sites.
type private Impl(config: LdapConfig, factory: ILdapConnectionFactory, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IHealthCheck.defaultTimeout

    interface IHealthCheck with
        member _.Name = sprintf "ldap-auth (%s:%d)" config.Host config.Port
        member _.Kind = Readiness
        member _.Timeout = timeout

        member _.Check() = async {
            match! factory.OpenServiceBound() with
            | Error msg -> return Unhealthy(sprintf "LDAP bind to %s:%d failed: %s" config.Host config.Port msg)
            | Ok connection ->
                use connection = connection

                // Presence filter — any user entry under the base.
                let probeFilter = sprintf "(%s=*)" config.Schema.LoginAttribute

                let! result =
                    connection.Search {
                        BaseDn = config.SearchBase
                        Filter = probeFilter
                        Scope = Subtree
                        Attributes = [ config.Schema.LoginAttribute ]
                        SizeLimit = 1
                    }

                match result with
                | Error msg -> return Unhealthy(sprintf "LDAP probe search failed: %s" msg)
                | Ok(_ :: _) -> return Healthy
                | Ok [] ->
                    return
                        Degraded(
                            sprintf
                                "LDAP bind succeeded but a probe search under '%s' returned 0 users — check the search base and login attribute (%s); every sign-in will fail with 'user not found' until this is corrected"
                                config.SearchBase
                                config.Schema.LoginAttribute
                        )
        }

/// Construct a health probe over an explicit config + directory factory
/// (tests inject a fake factory).
let fromParts (config: LdapConfig) (factory: ILdapConnectionFactory) : IHealthCheck =
    Impl(config, factory) :> IHealthCheck

/// Construct a health probe wiring a real directory factory whose
/// service-bind password is read from `ISecretStore`.
let create (secretStore: ISecretStore) (config: LdapConfig) : IHealthCheck =
    let resolvePassword () =
        secretStore.GetSecret(LdapConfig.SecretScope, config.BindPasswordSecretKey)

    fromParts config (LdapConnection.create config resolvePassword)

/// Return a probe when LDAP auth is enabled (`TOOLUP_LDAP_AUTH` truthy),
/// reading the directory config from the `TOOLUP_LDAP_*` environment.
/// `None` when LDAP auth is not enabled — a deployment that doesn't use
/// it registers no probe (GP 13).
let tryFromEnv (secretStore: ISecretStore) : IHealthCheck option =
    if LdapConfig.enabledFromEnv () then
        Some(create secretStore (LdapConfig.fromEnv ()))
    else
        None