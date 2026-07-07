// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.LdapAuthProvider

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.Metrics
open ToolUp.Platform.Secrets
open ToolUp.AuthProviders.LdapConfig
open ToolUp.AuthProviders.LdapDirectory
open ToolUp.AuthProviders.LdapGroupMapper

// ─── LDAP / Active Directory auth-provider companion ─────────────────
//
// Server-side `IAuthProvider` that proves identity by binding to the
// directory as the user — a proof-of-possession of the password against
// the authoritative store, not the trust of a spoofable header. Aimed
// at regulated / on-premise / air-gapped deployments whose identity
// store is Active Directory or generic LDAP and which cannot reach an
// external OIDC issuer.
//
// Flow per authentication:
//   1. Extract HTTP Basic credentials from the request.
//   2. Reject an empty password outright — a bind with a DN and an
//      empty password is an *unauthenticated bind* that succeeds
//      anonymously on many directories (a classic LDAP auth bypass).
//   3. Service-bind + search for the user by the configured login
//      attribute (the username is RFC-4515-escaped — LDAP-injection
//      safe). Exactly one match is required.
//   4. Bind as the user's DN with the presented password — the
//      authoritative password check.
//   5. Resolve group membership (direct + optional nested expansion)
//      and map to ToolUp roles via the `ldap.json` policy.
//   6. Build the `AuthenticatedUser`; cache the validated identity.
//
// All directory access goes through the `ILdapConnectionFactory` seam,
// so the whole pipeline is exercised in-process against an in-memory
// fake with no live LDAP server (GP 1 — the vendor dependency stays in
// `LdapConnection.fs`).

// ─── LDAP-specific metric names (shared `toolup.auth.validate.*`) ────

let private ldapTags = Map.ofList [ AuthMetrics.ProviderTag, "ldap" ]

[<Literal>]
let private ValidateUserNotFound = "toolup.auth.validate.user_not_found_total"

[<Literal>]
let private ValidateInvalidCredentials =
    "toolup.auth.validate.invalid_credentials_total"

[<Literal>]
let private ValidateAmbiguous = "toolup.auth.validate.ambiguous_user_total"

[<Literal>]
let private ValidateUpstreamError = "toolup.auth.validate.upstream_error_total"

[<Literal>]
let private ValidateCacheHit = "toolup.auth.validate.cache_hit_total"

// ─── Credential extraction (HTTP Basic) ──────────────────────────────

let private extractBasicCredentials (ctx: HttpContext) : (string * string) option =
    match ctx.Request.Headers.TryGetValue "Authorization" with
    | true, values when values.Count > 0 ->
        let value = string values[0]

        if value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) then
            try
                let decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value.Substring 6))
                let idx = decoded.IndexOf ':'

                if idx < 0 then
                    None
                else
                    Some(decoded.Substring(0, idx), decoded.Substring(idx + 1))
            with _ ->
                None
        else
            None
    | _ -> None

// ─── Identity cache ──────────────────────────────────────────────────
//
// Keyed by a SHA-256 of `username:password` — the raw credentials are
// never used as a dictionary key. Populated only on full success, so a
// cached hit is safe to admit. A password revoked at the directory is
// still honoured for up to the TTL (the standard cache trade-off,
// documented on `CacheTtlSeconds`).

let private hashCredentials (username: string) (password: string) : string =
    SHA256.HashData(Encoding.UTF8.GetBytes(username + ":" + password))
    |> Convert.ToHexString

// ─── No-op logger fallback ───────────────────────────────────────────

let private noOpLogger: ILogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

// ─── Group resolution ────────────────────────────────────────────────

/// The user's full group set: the direct `memberOf` DNs plus, when
/// nested resolution is on, the transitive nested groups from an AD
/// in-chain matching-rule search. A failed nested search degrades to
/// the direct set (roles are enrichment — never fail auth over a group
/// query) and is logged.
let private resolveGroups
    (connection: ILdapConnection)
    (config: LdapConfig)
    (log: ILogger)
    (userDn: string)
    (userEntry: LdapEntry)
    : Async<string list> =
    async {
        let directGroups = LdapEntry.values config.Schema.MemberOfAttribute userEntry

        if not config.NestedGroupResolution then
            return directGroups
        else
            let nestedFilter =
                sprintf "(member:%s:=%s)" InChainMatchingRuleOid (escapeFilterValue userDn)

            let! nestedResult =
                connection.Search {
                    BaseDn = config.SearchBase
                    Filter = nestedFilter
                    Scope = Subtree
                    Attributes = [ "distinguishedName" ]
                    SizeLimit = 0
                }

            match nestedResult with
            | Ok entries ->
                let nestedDns = entries |> List.map _.Dn
                // Union direct + nested, order-stable, case-insensitive.
                let seen =
                    System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

                let ordered = ResizeArray<string>()

                for g in List.append directGroups nestedDns do
                    if not (String.IsNullOrWhiteSpace g) && seen.Add g then
                        ordered.Add g

                return List.ofSeq ordered
            | Error msg ->
                log.Debug $"LDAP nested-group search failed, using direct membership only: {msg}"
                return directGroups
    }

// ─── User mapping ────────────────────────────────────────────────────

/// Derive a safe `AuthenticatedUser.UserId`. Prefer the configured
/// stable-id attribute (AD `objectGUID`); fall back to the login name.
/// Both flow through `IdentitySanitiser` before they can reach a
/// storage-scope path. Returns `Error` when neither yields a safe id
/// rather than silently degrading a validated user to `anonymous`.
let private deriveUserId (config: LdapConfig) (username: string) (entry: LdapEntry) : Result<string, string> =
    let candidates =
        [ LdapEntry.firstValue config.Schema.UserIdAttribute entry; Some username ]
        |> List.choose id
        |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))

    candidates
    |> List.tryPick (fun c ->
        match IdentitySanitiser.sanitiseScopeId c with
        | Result.Ok safe -> Some safe
        | Result.Error _ -> None)
    |> function
        | Some safe -> Result.Ok safe
        | None -> Result.Error "could not derive a filesystem-safe user id from the directory entry"

// ─── Validation pipeline ─────────────────────────────────────────────

let private validate
    (factory: ILdapConnectionFactory)
    (config: LdapConfig)
    (groupMap: GroupRoleMap)
    (cache: ConcurrentDictionary<string, DateTimeOffset * AuthenticatedUser>)
    (metrics: IMetricsSink)
    (log: ILogger)
    (ctx: HttpContext)
    : Async<Result<AuthenticatedUser, string>> =
    let incr (counter: string) : unit = metrics.Increment(counter, ldapTags)

    async {
        match extractBasicCredentials ctx with
        | None ->
            incr AuthMetrics.ValidateNoToken
            return Error "no LDAP credentials in request"
        | Some(username, password) ->
            // Reject an empty username or password before any bind. An
            // empty password would trigger an unauthenticated bind that
            // succeeds anonymously on many directories — a definitive
            // auth bypass, not a directory quirk to tolerate.
            if String.IsNullOrWhiteSpace username || password = "" then
                incr ValidateInvalidCredentials
                return Error "LDAP credentials must carry a non-empty username and password"
            else
                let cacheKey = hashCredentials username password

                let cached =
                    if config.CacheTtlSeconds > 0 then
                        match cache.TryGetValue cacheKey with
                        | true, (expiry, user) when expiry > DateTimeOffset.UtcNow -> Some user
                        | _ -> None
                    else
                        None

                match cached with
                | Some user ->
                    incr ValidateCacheHit
                    return Ok user
                | None ->
                    match! factory.OpenServiceBound() with
                    | Error msg ->
                        incr ValidateUpstreamError
                        return Error(sprintf "could not reach the directory: %s" msg)
                    | Ok connection ->
                        use connection = connection

                        let filter =
                            sprintf
                                "(&(objectClass=%s)(%s=%s))"
                                config.Schema.UserObjectClass
                                config.Schema.LoginAttribute
                                (escapeFilterValue username)

                        let! searchResult =
                            connection.Search {
                                BaseDn = config.SearchBase
                                Filter = filter
                                Scope = Subtree
                                Attributes = [
                                    config.Schema.UserIdAttribute
                                    config.Schema.DisplayNameAttribute
                                    config.Schema.EmailAttribute
                                    config.Schema.MemberOfAttribute
                                ]
                                SizeLimit = 2
                            }

                        match searchResult with
                        | Error msg ->
                            incr ValidateUpstreamError
                            return Error(sprintf "directory search failed: %s" msg)
                        | Ok [] ->
                            incr ValidateUserNotFound
                            return Error "no directory user matches the presented username"
                        | Ok(_ :: _ :: _) ->
                            // More than one match — an ambiguous login
                            // attribute. Fail closed rather than guess.
                            incr ValidateAmbiguous
                            return Error "the presented username matches more than one directory entry"
                        | Ok [ userEntry ] ->
                            let userDn = userEntry.Dn

                            match! factory.VerifyCredentials(userDn, password) with
                            | Error msg ->
                                incr ValidateUpstreamError
                                return Error(sprintf "could not verify credentials: %s" msg)
                            | Ok false ->
                                incr ValidateInvalidCredentials
                                return Error "the directory rejected the presented credentials"
                            | Ok true ->
                                match deriveUserId config username userEntry with
                                | Result.Error reason ->
                                    incr ValidateUpstreamError
                                    return Error reason
                                | Result.Ok userId ->
                                    let! groups = resolveGroups connection config log userDn userEntry
                                    let roles = GroupRoleMap.resolveRoles groupMap groups

                                    let displayName =
                                        LdapEntry.firstValue config.Schema.DisplayNameAttribute userEntry
                                        |> Option.filter (fun s -> not (String.IsNullOrWhiteSpace s))
                                        |> Option.defaultValue username

                                    let email =
                                        LdapEntry.firstValue config.Schema.EmailAttribute userEntry
                                        |> Option.filter (fun s -> not (String.IsNullOrWhiteSpace s))

                                    let user = {
                                        UserId = userId
                                        DisplayName = displayName
                                        Email = email
                                        TenantId = None
                                        Roles = roles
                                    }

                                    if config.CacheTtlSeconds > 0 then
                                        let expiry = DateTimeOffset.UtcNow.AddSeconds(float config.CacheTtlSeconds)

                                        cache[cacheKey] <- (expiry, user)

                                    incr AuthMetrics.ValidateSuccess
                                    return Ok user
    }

// ─── Construction ────────────────────────────────────────────────────

let private buildProvider
    (factory: ILdapConnectionFactory)
    (config: LdapConfig)
    (groupMap: GroupRoleMap)
    (metrics: IMetricsSink option)
    (logger: ILogger option)
    : IAuthProvider =
    let log = logger |> Option.defaultValue noOpLogger

    let sink =
        metrics |> Option.defaultWith (fun () -> NoOpMetricsSink() :> IMetricsSink)

    let cache = ConcurrentDictionary<string, DateTimeOffset * AuthenticatedUser>()

    { new IAuthProvider with
        member _.GetUser ctx = async {
            let httpCtx = RequestContext.value ctx :?> HttpContext
            let! result = validate factory config groupMap cache sink log httpCtx

            match result with
            | Ok user ->
                log.Debug $"LDAP auth ok: user={user.UserId}"
                return user
            | Error msg ->
                if msg = "no LDAP credentials in request" then
                    log.Debug "LDAP auth (lenient): no credentials in request → anonymous"
                else
                    log.Warn $"LDAP auth failed (lenient): {msg}"

                return AuthenticatedUser.anonymous
        }

        member _.ValidateRequest ctx = async {
            let httpCtx = RequestContext.value ctx :?> HttpContext
            let! result = validate factory config groupMap cache sink log httpCtx

            match result with
            | Ok user ->
                log.Debug $"LDAP validate ok: user={user.UserId}"
                return Ok user
            | Error msg ->
                log.Warn $"LDAP validate failed: {msg}"
                return Error msg
        }

        // Identity is proven by an authoritative bind to the directory —
        // a wrong password fails that bind — not by trusting a spoofable
        // request header. That is the same remote-authoritative class the
        // unverified-provider startup gate is written to admit, so `true`
        // is the honest answer.
        member _.IsCryptographicallyVerified = true
    }

// ─── Public entry points ─────────────────────────────────────────────

/// Build an LDAP `IAuthProvider` over an explicit directory factory +
/// mapping policy. Tests use this with an in-memory fake factory; it is
/// also the seam an advanced caller uses to inject a custom directory
/// adapter.
let fromParts
    (factory: ILdapConnectionFactory)
    (config: LdapConfig)
    (groupMap: GroupRoleMap)
    (logger: ILogger option)
    : IAuthProvider =
    buildProvider factory config groupMap None logger

/// Metrics-enabled variant of `fromParts`.
let fromPartsMetered
    (factory: ILdapConnectionFactory)
    (config: LdapConfig)
    (groupMap: GroupRoleMap)
    (metrics: IMetricsSink option)
    (logger: ILogger option)
    : IAuthProvider =
    buildProvider factory config groupMap metrics logger

/// Production constructor. Wires a real `System.DirectoryServices`
/// factory whose service-bind password is read from `ISecretStore`
/// (`_platform` / `config.BindPasswordSecretKey`) on every service bind,
/// so a rotated bind password flows through without a recompose.
/// Refuses a `Plaintext` channel binding unless the
/// `TOOLUP_LDAP_ALLOW_PLAINTEXT` opt-in is set — a plaintext bind puts
/// credentials on the wire in the clear.
let create
    (secretStore: ISecretStore)
    (config: LdapConfig)
    (groupMap: GroupRoleMap)
    (logger: ILogger option)
    : IAuthProvider =
    match config.ChannelBinding with
    | Plaintext when not (LdapConfig.plaintextAllowedFromEnv ()) ->
        invalidArg
            (nameof config)
            "LdapConfig.ChannelBinding = Plaintext puts credentials on the wire in the clear. Use Ldaps (default) or StartTls, or set TOOLUP_LDAP_ALLOW_PLAINTEXT to explicitly opt in."
    | _ -> ()

    let resolvePassword () =
        secretStore.GetSecret(LdapConfig.SecretScope, config.BindPasswordSecretKey)

    let factory = LdapConnection.create config resolvePassword
    buildProvider factory config groupMap None logger

/// Environment-driven factory. Gated on `TOOLUP_LDAP_AUTH` — returns
/// `None` when LDAP auth is not enabled so a deployment that does not
/// use it can fall back to another provider without a `try`/`with`
/// (GP 13). When enabled, layers `LdapConfig.fromEnv` over the defaults
/// and loads the `ldap.json` group-mapping policy from `ISecretStore`
/// (`_platform` / `auth/ldap.json`), tolerating an absent policy as the
/// empty map.
let fromEnv (secretStore: ISecretStore) (logger: ILogger option) : Async<IAuthProvider option> = async {
    if not (LdapConfig.enabledFromEnv ()) then
        return None
    else
        let config = LdapConfig.fromEnv ()

        let! mappingJson = secretStore.GetSecret(LdapConfig.SecretScope, "auth/ldap.json")

        let groupMap =
            match mappingJson with
            | Some json ->
                match GroupRoleMap.parse json with
                | Result.Ok m -> m
                | Result.Error reason ->
                    (logger |> Option.defaultValue noOpLogger).Warn
                        $"LDAP group-mapping policy could not be parsed, no roles will be mapped: {reason}"

                    GroupRoleMap.empty
            | None -> GroupRoleMap.empty

        return Some(create secretStore config groupMap logger)
}

// ─── Hybrid fallback (OIDC-first, LDAP-second) ───────────────────────
//
// A deployment with "AD-resident staff + OIDC-federated contractors"
// wires a chain: try the OIDC provider first, fall back to LDAP for a
// user the OIDC store does not know. Depends only on `IAuthProvider`
// (Core), so it composes any providers — the LDAP companion carries it
// because the phase scopes the hybrid case to LDAP.

/// Compose providers into a fallback chain. `ValidateRequest` returns
/// the first `Ok`; if every provider errors, the *last* error is
/// returned. `GetUser` returns the first non-anonymous user; else
/// anonymous. `IsCryptographicallyVerified` is the conjunction — the
/// composite is only as strong as its weakest admitting path, so a
/// header-trusting provider anywhere in the chain makes the whole chain
/// report `false` (and the startup gate treats it accordingly).
let chain (providers: IAuthProvider list) : IAuthProvider =
    match providers with
    | [] -> invalidArg (nameof providers) "chain requires at least one provider"
    | [ single ] -> single
    | _ ->
        { new IAuthProvider with
            member _.GetUser ctx = async {
                let rec go =
                    function
                    | [] -> async { return AuthenticatedUser.anonymous }
                    | (p: IAuthProvider) :: rest -> async {
                        let! user = p.GetUser ctx

                        if AuthenticatedUser.isAnonymous user then
                            return! go rest
                        else
                            return user
                      }

                return! go providers
            }

            member _.ValidateRequest ctx = async {
                let rec go lastError =
                    function
                    | [] -> async {
                        return Error(lastError |> Option.defaultValue "no auth provider accepted the request")
                      }
                    | (p: IAuthProvider) :: rest -> async {
                        match! p.ValidateRequest ctx with
                        | Ok user -> return Ok user
                        | Error e -> return! go (Some e) rest
                      }

                return! go None providers
            }

            member _.IsCryptographicallyVerified =
                providers |> List.forall _.IsCryptographicallyVerified
        }

/// Convenience for the canonical hybrid: OIDC (or any primary) first,
/// LDAP second.
let withFallback (primary: IAuthProvider) (ldap: IAuthProvider) : IAuthProvider = chain [ primary; ldap ]