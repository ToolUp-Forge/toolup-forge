// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.LdapConfig

open System

// ─── LDAP / Active Directory auth-provider config ────────────────────
//
// Declarative configuration for the LDAP `IAuthProvider`. Unlike the
// OIDC / GitHub providers — which validate a bearer minted elsewhere —
// this provider proves identity by binding to the directory as the user
// (a proof-of-possession of the password against the authoritative
// store). It is aimed at regulated / on-premise / air-gapped
// deployments whose identity store is Active Directory or a generic
// LDAP directory and which cannot reach an external OIDC issuer.
//
// **Secrets never live here.** The service-account *bind DN* is not
// secret (it is a directory path) and rides the config; the service
// account *password* is read from `ISecretStore("_platform",
// "auth/ldap/bind")` at construction, per the companion-authoring rule
// (substrate deps arrive through `create`, never env-var reads).

/// How the client transport to the directory is secured. LDAPS is the
/// default — the bind carries the service-account password and (on the
/// user-bind leg) the end-user's password in the clear at the LDAP
/// layer, so an unencrypted channel exposes both to a network observer.
type LdapChannelBinding =
    /// Implicit TLS on the LDAPS port (default 636). The default.
    | Ldaps
    /// Plain LDAP (default 389) upgraded to TLS via the StartTLS
    /// extended operation before any bind.
    | StartTls
    /// Unencrypted plain LDAP. **Refused at construction unless the
    /// `TOOLUP_LDAP_ALLOW_PLAINTEXT` opt-in env var is set** — a
    /// plaintext bind puts credentials on the wire in the clear.
    | Plaintext

/// Server-certificate validation posture for LDAPS / StartTLS.
type LdapCertificateValidation =
    /// Full chain + (optionally) pinned-thumbprint validation. The
    /// default — a MITM presenting an untrusted cert is rejected.
    | Strict of pinnedThumbprint: string option
    /// Accept any server certificate. **Dev/test only** — defeats the
    /// channel's MITM protection. The config validator flags this.
    | AllowUntrusted

/// Which directory attributes the provider reads off a user entry.
/// Defaults target Active Directory; a generic LDAP / OpenLDAP
/// directory overrides `UserId` (`entryUUID`), `LoginAttribute`
/// (`uid`), and `MemberOf` as needed.
type LdapUserSchema = {
    /// Attribute matched against the presented username in the search
    /// filter. AD: `sAMAccountName` (default) or `userPrincipalName`;
    /// generic LDAP: `uid` or `mail`.
    LoginAttribute: string
    /// Attribute yielding the stable, never-reused account id that
    /// becomes `AuthenticatedUser.UserId`. AD: `objectGUID` (default);
    /// generic LDAP: `entryUUID`. Falls back to `LoginAttribute` when
    /// absent.
    UserIdAttribute: string
    /// Human display-name attribute. Default `displayName` (AD); `cn`
    /// is the common generic-LDAP choice.
    DisplayNameAttribute: string
    /// Email attribute. Default `mail`.
    EmailAttribute: string
    /// Group-membership attribute carrying the DNs of the groups the
    /// user is a *direct* member of. Default `memberOf`.
    MemberOfAttribute: string
    /// The `objectClass` a user entry must carry, used in the search
    /// filter. AD: `user` (default); generic LDAP: `inetOrgPerson`.
    UserObjectClass: string
}

module LdapUserSchema =
    /// Active Directory defaults.
    let activeDirectory = {
        LoginAttribute = "sAMAccountName"
        UserIdAttribute = "objectGUID"
        DisplayNameAttribute = "displayName"
        EmailAttribute = "mail"
        MemberOfAttribute = "memberOf"
        UserObjectClass = "user"
    }

    /// Generic RFC-4519 / OpenLDAP `inetOrgPerson` defaults.
    let openLdap = {
        LoginAttribute = "uid"
        UserIdAttribute = "entryUUID"
        DisplayNameAttribute = "cn"
        EmailAttribute = "mail"
        MemberOfAttribute = "memberOf"
        UserObjectClass = "inetOrgPerson"
    }

/// Full LDAP auth-provider configuration.
type LdapConfig = {
    /// Directory host (FQDN preferred — the TLS cert's subject must
    /// match it under `Strict` validation).
    Host: string
    /// Directory port. Defaults derive from `ChannelBinding` when built
    /// via `LdapConfig.defaults` (636 for LDAPS, 389 otherwise).
    Port: int
    /// Transport security. `Ldaps` by default.
    ChannelBinding: LdapChannelBinding
    /// Server-certificate validation posture. `Strict None` by default.
    CertificateValidation: LdapCertificateValidation
    /// DN the provider binds as to *search* for the user (before the
    /// user-password bind). Not secret — a directory path. The matching
    /// password is read from `ISecretStore`. Empty ⇒ anonymous search
    /// bind (only works on directories that permit it).
    ServiceBindDn: string
    /// `ISecretStore` key (under the `_platform` scope) holding the
    /// service-account bind password. Default `auth/ldap/bind`.
    BindPasswordSecretKey: string
    /// Subtree under which user entries are searched, e.g.
    /// `OU=Users,DC=example,DC=com`. Required — an empty base is a
    /// misconfiguration the preflight validator rejects.
    SearchBase: string
    /// Attribute schema. `LdapUserSchema.activeDirectory` by default.
    Schema: LdapUserSchema
    /// When `true`, expand nested group membership via the AD
    /// in-chain matching rule (`LDAP_MATCHING_RULE_IN_CHAIN`,
    /// `1.2.840.113556.1.4.1941`) in addition to the direct `memberOf`
    /// set. Generic LDAP directories without the matching rule fall
    /// back to direct membership. Default `true`.
    NestedGroupResolution: bool
    /// TTL (seconds) for the validated-identity cache, keyed by a
    /// SHA-256 of the presented credentials. A busy client then costs
    /// the directory one bind per TTL window rather than one per
    /// request. `0` disables the cache. Default 300 (5 min).
    CacheTtlSeconds: int
    /// Per-operation directory timeout (seconds). Default 10.
    TimeoutSeconds: int
}

module LdapConfig =
    [<Literal>]
    let DefaultBindPasswordSecretKey = "auth/ldap/bind"

    [<Literal>]
    let DefaultCacheTtlSeconds = 300

    [<Literal>]
    let DefaultTimeoutSeconds = 10

    [<Literal>]
    let LdapsPort = 636

    [<Literal>]
    let LdapPort = 389

    /// The reserved `_platform` secret scope the bind password lives
    /// under — mirrors the `ISecretStore` scope conventions.
    [<Literal>]
    let SecretScope = "_platform"

    /// Default port for a channel binding.
    let defaultPortFor =
        function
        | Ldaps -> LdapsPort
        | StartTls
        | Plaintext -> LdapPort

    /// AD-shaped defaults over a host. LDAPS on 636, strict cert
    /// validation, nested-group resolution on, 5-minute identity cache.
    /// Callers override fields with `{ defaults host with … }`.
    let defaults (host: string) : LdapConfig = {
        Host = host
        Port = LdapsPort
        ChannelBinding = Ldaps
        CertificateValidation = Strict None
        ServiceBindDn = ""
        BindPasswordSecretKey = DefaultBindPasswordSecretKey
        SearchBase = ""
        Schema = LdapUserSchema.activeDirectory
        NestedGroupResolution = true
        CacheTtlSeconds = DefaultCacheTtlSeconds
        TimeoutSeconds = DefaultTimeoutSeconds
    }

    let private envVar name =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> None
        | value -> Some value

    let private parseBool (raw: string) : bool =
        match raw.Trim().ToLowerInvariant() with
        | "1"
        | "true"
        | "yes"
        | "on"
        | "enabled" -> true
        | _ -> false

    /// Is LDAP auth switched on for this deployment? Gates every
    /// `tryFromEnv` factory (provider, health check, validator) so a
    /// deployment that does not use LDAP — and has not set the flag —
    /// pays nothing and registers nothing (GP 13).
    let enabledFromEnv () : bool =
        match envVar "TOOLUP_LDAP_AUTH" with
        | Some raw -> parseBool raw
        | None -> false

    /// Has the operator explicitly acknowledged an unencrypted bind?
    /// A `Plaintext` channel binding is refused at construction unless
    /// this is set — a plaintext bind puts credentials on the wire in
    /// the clear, so it must never be reachable by omission.
    let plaintextAllowedFromEnv () : bool =
        match envVar "TOOLUP_LDAP_ALLOW_PLAINTEXT" with
        | Some raw -> parseBool raw
        | None -> false

    let private parseChannelBinding (raw: string) : LdapChannelBinding =
        match raw.Trim().ToLowerInvariant() with
        | "starttls"
        | "start-tls" -> StartTls
        | "plaintext"
        | "plain"
        | "none" -> Plaintext
        | _ -> Ldaps

    /// Read an `LdapConfig` from the `TOOLUP_LDAP_*` environment,
    /// layered over `defaults`. Only `TOOLUP_LDAP_HOST` and
    /// `TOOLUP_LDAP_SEARCH_BASE` are load-bearing; everything else has a
    /// well-defined default.
    ///
    ///   - `TOOLUP_LDAP_HOST`               — directory host (required).
    ///   - `TOOLUP_LDAP_PORT`               — override the derived port.
    ///   - `TOOLUP_LDAP_CHANNEL`            — `ldaps` (default) / `starttls` / `plaintext`.
    ///   - `TOOLUP_LDAP_ALLOW_UNTRUSTED_CERT` — truthy ⇒ `AllowUntrusted` (dev).
    ///   - `TOOLUP_LDAP_CERT_THUMBPRINT`    — pin the server cert (Strict).
    ///   - `TOOLUP_LDAP_BIND_DN`            — service-account search bind DN.
    ///   - `TOOLUP_LDAP_BIND_SECRET_KEY`    — override the bind-password secret key.
    ///   - `TOOLUP_LDAP_SEARCH_BASE`        — user search subtree (required).
    ///   - `TOOLUP_LDAP_LOGIN_ATTR`         — login attribute (`sAMAccountName`).
    ///   - `TOOLUP_LDAP_USER_ID_ATTR`       — stable id attribute (`objectGUID`).
    ///   - `TOOLUP_LDAP_DISPLAY_ATTR`       — display-name attribute.
    ///   - `TOOLUP_LDAP_EMAIL_ATTR`         — email attribute.
    ///   - `TOOLUP_LDAP_MEMBEROF_ATTR`      — group-membership attribute.
    ///   - `TOOLUP_LDAP_USER_OBJECTCLASS`   — user objectClass.
    ///   - `TOOLUP_LDAP_NESTED_GROUPS`      — truthy (default) enables nested resolution.
    ///   - `TOOLUP_LDAP_CACHE_TTL_SECONDS`  — identity-cache TTL.
    ///   - `TOOLUP_LDAP_TIMEOUT_SECONDS`    — per-op timeout.
    let fromEnv () : LdapConfig =
        let host = envVar "TOOLUP_LDAP_HOST" |> Option.defaultValue ""

        let channel =
            envVar "TOOLUP_LDAP_CHANNEL"
            |> Option.map parseChannelBinding
            |> Option.defaultValue Ldaps

        let certValidation =
            if
                envVar "TOOLUP_LDAP_ALLOW_UNTRUSTED_CERT"
                |> Option.map parseBool
                |> Option.defaultValue false
            then
                AllowUntrusted
            else
                Strict(envVar "TOOLUP_LDAP_CERT_THUMBPRINT")

        let baseDefaults = defaults host

        let schema = {
            LoginAttribute =
                envVar "TOOLUP_LDAP_LOGIN_ATTR"
                |> Option.defaultValue baseDefaults.Schema.LoginAttribute
            UserIdAttribute =
                envVar "TOOLUP_LDAP_USER_ID_ATTR"
                |> Option.defaultValue baseDefaults.Schema.UserIdAttribute
            DisplayNameAttribute =
                envVar "TOOLUP_LDAP_DISPLAY_ATTR"
                |> Option.defaultValue baseDefaults.Schema.DisplayNameAttribute
            EmailAttribute =
                envVar "TOOLUP_LDAP_EMAIL_ATTR"
                |> Option.defaultValue baseDefaults.Schema.EmailAttribute
            MemberOfAttribute =
                envVar "TOOLUP_LDAP_MEMBEROF_ATTR"
                |> Option.defaultValue baseDefaults.Schema.MemberOfAttribute
            UserObjectClass =
                envVar "TOOLUP_LDAP_USER_OBJECTCLASS"
                |> Option.defaultValue baseDefaults.Schema.UserObjectClass
        }

        let intVar name fallback =
            envVar name
            |> Option.map (fun s ->
                match Int32.TryParse(s.Trim()) with
                | true, n when n >= 0 -> n
                | _ -> failwithf "%s = '%s' is not a valid non-negative integer." name s)
            |> Option.defaultValue fallback

        {
            baseDefaults with
                Port = intVar "TOOLUP_LDAP_PORT" (defaultPortFor channel)
                ChannelBinding = channel
                CertificateValidation = certValidation
                ServiceBindDn = envVar "TOOLUP_LDAP_BIND_DN" |> Option.defaultValue ""
                BindPasswordSecretKey =
                    envVar "TOOLUP_LDAP_BIND_SECRET_KEY"
                    |> Option.defaultValue DefaultBindPasswordSecretKey
                SearchBase = envVar "TOOLUP_LDAP_SEARCH_BASE" |> Option.defaultValue ""
                Schema = schema
                NestedGroupResolution =
                    envVar "TOOLUP_LDAP_NESTED_GROUPS"
                    |> Option.map parseBool
                    |> Option.defaultValue true
                CacheTtlSeconds = intVar "TOOLUP_LDAP_CACHE_TTL_SECONDS" DefaultCacheTtlSeconds
                TimeoutSeconds = intVar "TOOLUP_LDAP_TIMEOUT_SECONDS" DefaultTimeoutSeconds
        }