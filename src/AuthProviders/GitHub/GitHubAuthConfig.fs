// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.GitHubAuthConfig

open System
open ToolUp.Platform

// ─── GitHub auth-provider config + builders ──────────────────────────
//
// GitHub is NOT an OIDC issuer for the OAuth-App flow — it mints an
// *opaque* access token (`gho_…`), never a JWT / JWKS-verifiable
// id_token. So the generic `OidcAuthProvider` cannot validate a GitHub
// bearer: there is no local signature to check. Instead the provider
// establishes identity by calling GitHub's authoritative REST API
// (`GET {ApiBaseUrl}/user`) with the presented bearer — a token GitHub
// did not mint fails that call, so the identity is proven remotely
// rather than trusted from a spoofable header.
//
// This module carries only the *validator* config (how to read + verify
// an inbound bearer). The browser sign-in leg (authorize-URL + the
// server-side code→token exchange, which needs the OAuth-App client
// secret) lives in `GitHubOAuth.fs` — GitHub OAuth Apps have no PKCE, so
// the exchange must run server-side, unlike the OIDC client package.

/// Declarative configuration for the GitHub `IAuthProvider`. Only
/// `AllowedOrgs` shapes *who* is admitted; everything else has a
/// well-defined default via `GitHubAuthConfig.defaults`.
type GitHubAuthConfig = {
    /// GitHub REST API base URL. `https://api.github.com` for github.com;
    /// `https://<host>/api/v3` for a GitHub Enterprise Server install.
    /// Trailing slashes are trimmed by the provider. Must be https
    /// (enforced at construction) — the bearer rides these calls.
    ApiBaseUrl: string
    /// Where the provider extracts the bearer from the request. Reuses
    /// the SDK's `TokenLocation` so a GitHub deployment can pick the same
    /// Bearer-header / cookie / Bearer-or-cookie transport as an OIDC one
    /// (`BearerOrCookie` is the SSE-compatible choice).
    TokenLocation: TokenLocation
    /// Org allow-list. **Empty ⇒ any authenticated GitHub user is
    /// admitted** (identity-only gate). Non-empty ⇒ the user must be an
    /// *active* member of at least one listed org, checked via
    /// `GET /user/memberships/orgs/{org}` (needs the token's `read:org`
    /// scope). Membership is re-checked whenever the identity cache
    /// misses, not on every request.
    AllowedOrgs: string list
    /// TTL (seconds) for the per-token validated-identity cache. Each
    /// successful validation is cached keyed by a SHA-256 of the token,
    /// so a busy client costs GitHub one API round-trip per TTL window
    /// rather than one per request (GitHub's authenticated rate limit is
    /// 5000/hr/token). **Trade-off:** a token revoked at GitHub is still
    /// honoured for up to TTL. `0` disables the cache — every request
    /// validates live (correct-but-chatty; only safe under low traffic).
    /// Default 300 (5 min).
    CacheTtlSeconds: int
    /// When `true`, and `GET /user` returned no public email, the
    /// provider makes a second best-effort call to `GET /user/emails`
    /// (needs `user:email` scope) to resolve the user's primary verified
    /// address. Off by default — it costs an extra call + a wider scope,
    /// and many deployments don't need the email. A 403 (scope absent)
    /// degrades silently to `Email = None`.
    FetchPrimaryEmail: bool
    /// `User-Agent` sent on every GitHub API call. GitHub *rejects*
    /// requests with no User-Agent, so this is required by the wire
    /// contract; defaults to `"ToolUp-Platform-GitHubAuth"`. Set it to
    /// your app/org name per GitHub's API etiquette guidance.
    UserAgent: string
}

module GitHubAuthConfig =
    /// Canonical public GitHub REST API base.
    [<Literal>]
    let DefaultApiBaseUrl = "https://api.github.com"

    [<Literal>]
    let DefaultUserAgent = "ToolUp-Platform-GitHubAuth"

    [<Literal>]
    let DefaultCacheTtlSeconds = 300

    /// Sensible defaults — public github.com, bearer-header transport, no
    /// org restriction, 5-minute identity cache, no extra email call.
    /// Callers override the fields they care about with `{ defaults with … }`.
    let defaults: GitHubAuthConfig = {
        ApiBaseUrl = DefaultApiBaseUrl
        TokenLocation = BearerHeader
        AllowedOrgs = []
        CacheTtlSeconds = DefaultCacheTtlSeconds
        FetchPrimaryEmail = false
        UserAgent = DefaultUserAgent
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

    /// Split a comma / whitespace separated org list, trimming blanks.
    let private parseOrgs (raw: string) : string list =
        raw.Split([| ','; ';'; ' '; '\t'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
        |> Array.filter (fun s -> s <> "")
        |> Array.toList

    /// Read a `GitHubAuthConfig` from the standard `TOOLUP_GITHUB_*`
    /// environment variables, layered over `defaults`. Every var is
    /// optional — a deployment can enable GitHub auth with no env config
    /// at all and get the identity-only, any-GitHub-user posture.
    ///
    ///   - `TOOLUP_GITHUB_API_BASE_URL`      — override for GHES.
    ///   - `TOOLUP_GITHUB_ALLOWED_ORGS`      — comma-separated org allow-list.
    ///   - `TOOLUP_GITHUB_CACHE_TTL_SECONDS` — identity-cache TTL; a
    ///                                          set-but-invalid value fails
    ///                                          fast rather than silently
    ///                                          reverting to the default.
    ///   - `TOOLUP_GITHUB_FETCH_PRIMARY_EMAIL` — truthy enables the
    ///                                          `/user/emails` fallback.
    ///   - `TOOLUP_GITHUB_USER_AGENT`        — override the API User-Agent.
    let fromEnv () : GitHubAuthConfig = {
        ApiBaseUrl =
            envVar ConfigKeys.Names.githubApiBaseUrl
            |> Option.defaultValue defaults.ApiBaseUrl
        TokenLocation = defaults.TokenLocation
        AllowedOrgs =
            envVar ConfigKeys.Names.githubAllowedOrgs
            |> Option.map parseOrgs
            |> Option.defaultValue []
        CacheTtlSeconds =
            // A SET-but-unparseable / negative value previously would
            // silently fall back to the default — a typo'd TTL becomes a
            // security-relevant setting nobody knew was ignored. Fail
            // fast; leaving it unset still uses the 300s default.
            envVar ConfigKeys.Names.githubCacheTtlSeconds
            |> Option.map (fun s ->
                match Int32.TryParse(s.Trim()) with
                | true, n when n >= 0 -> n
                | _ ->
                    failwithf
                        "TOOLUP_GITHUB_CACHE_TTL_SECONDS = '%s' is not a valid TTL. Expected a non-negative integer (seconds); 0 disables the cache, leave unset for the %ds default."
                        s
                        DefaultCacheTtlSeconds)
            |> Option.defaultValue defaults.CacheTtlSeconds
        FetchPrimaryEmail =
            envVar ConfigKeys.Names.githubFetchPrimaryEmail
            |> Option.map parseBool
            |> Option.defaultValue defaults.FetchPrimaryEmail
        UserAgent =
            envVar ConfigKeys.Names.githubUserAgent
            |> Option.defaultValue defaults.UserAgent
    }