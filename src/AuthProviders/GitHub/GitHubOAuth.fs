// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.GitHubOAuth

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open ToolUp.Platform.Secrets

// ─── GitHub sign-in leg (server-side authorize + code exchange) ──────
//
// The OIDC companion splits into a server validator + a *browser* client
// package because OIDC public clients do Authorization Code + PKCE, so no
// secret ever touches the server. GitHub OAuth Apps have no PKCE — the
// code→token exchange authenticates with the app's `client_secret`, which
// must stay server-side. So the GitHub sign-in leg lives here, in the
// server companion, not in a Fable client package:
//
//   1. `buildAuthorizeUrl` (pure) — the full-page redirect the sign-in
//      button points at. The consumer generates + persists a CSRF
//      `state` and passes it in.
//   2. `exchangeCode` — the callback handler calls this with the returned
//      `code`; it reads the client secret from `ISecretStore` (never an
//      env var — companion secret-handling convention) and POSTs the
//      exchange, yielding the opaque access token the browser then
//      presents to `GitHubAuthProvider` on every subsequent request.
//
// The CSRF `state` round-trip, the callback route, and session issuance
// are the consumer's to wire (they belong to the app's composition root,
// not this companion) — the README shows the shape.

/// OAuth-App configuration for the sign-in leg. `ClientId` is public;
/// the matching secret is read from `ISecretStore` by `ClientSecretKey`
/// so it never appears in config, env, or logs.
type GitHubOAuthAppConfig = {
    /// OAuth App / GitHub App client id (public — safe in the authorize
    /// URL).
    ClientId: string
    /// Secret-store key (under the reserved `_platform` scope) holding
    /// the OAuth App client secret. The exchange reads it per call, so a
    /// rotated secret flows through without reconstructing anything.
    ClientSecretKey: string
    /// OAuth scopes requested at consent. `read:user` for identity;
    /// add `user:email` to enable the provider's primary-email fallback,
    /// `read:org` to enable org allow-listing.
    Scopes: string list
    /// Whether the authorize URL allows a not-yet-registered visitor to
    /// sign up during the flow (`allow_signup`). Defaults on in GitHub;
    /// set `false` to restrict to existing accounts.
    AllowSignup: bool
    /// GitHub authorize endpoint. `https://github.com/login/oauth/authorize`
    /// for github.com; `https://<host>/login/oauth/authorize` for GHES.
    AuthorizeBaseUrl: string
    /// GitHub token endpoint. `https://github.com/login/oauth/access_token`
    /// for github.com; the GHES equivalent otherwise.
    TokenBaseUrl: string
}

module GitHubOAuthAppConfig =
    [<Literal>]
    let DefaultAuthorizeBaseUrl = "https://github.com/login/oauth/authorize"

    [<Literal>]
    let DefaultTokenBaseUrl = "https://github.com/login/oauth/access_token"

    [<Literal>]
    let DefaultClientSecretKey = "github-client-secret"

    /// Build a config for the common github.com case — you supply the
    /// client id + the scopes; endpoints, secret key, and sign-up default
    /// to the standard values.
    let create (clientId: string) (scopes: string list) : GitHubOAuthAppConfig = {
        ClientId = clientId
        ClientSecretKey = DefaultClientSecretKey
        Scopes = scopes
        AllowSignup = true
        AuthorizeBaseUrl = DefaultAuthorizeBaseUrl
        TokenBaseUrl = DefaultTokenBaseUrl
    }

/// The credential minted by a successful code exchange. `AccessToken` is
/// the opaque bearer the browser presents to `GitHubAuthProvider`.
/// `Scope` is GitHub's echo of the granted scopes (may differ from the
/// requested set if the user pared them down).
type GitHubTokenResponse = {
    AccessToken: string
    Scope: string
    TokenType: string
}

/// The reserved cross-tenant secret scope (per `ISecretStore`). The
/// OAuth App client secret is platform-level, not per-user/per-team.
[<Literal>]
let private platformScope = "_platform"

let private urlEncode (value: string) : string = Uri.EscapeDataString value

/// Build the GitHub authorize URL the sign-in button redirects to. Pure
/// — no I/O. `state` is the caller's CSRF token (generate a fresh random
/// value, persist it, and reject the callback if it doesn't echo back).
/// `redirectUri` must match the callback registered on the OAuth App and
/// the one later passed to `exchangeCode` (GitHub validates equality).
let buildAuthorizeUrl (config: GitHubOAuthAppConfig) (redirectUri: string) (state: string) : string =
    let scope = config.Scopes |> String.concat " "

    let query =
        [
            "client_id", config.ClientId
            "redirect_uri", redirectUri
            "scope", scope
            "state", state
            "allow_signup", (if config.AllowSignup then "true" else "false")
        ]
        |> List.map (fun (k, v) -> sprintf "%s=%s" k (urlEncode v))
        |> String.concat "&"

    sprintf "%s?%s" config.AuthorizeBaseUrl query

/// Exchange an authorization `code` for an access token. Reads the client
/// secret from `ISecretStore` (reserved `_platform` scope), POSTs the
/// exchange with `Accept: application/json`, and returns the opaque
/// bearer. GitHub returns HTTP 200 with an `{ "error": … }` body on a
/// bad/expired code, so the JSON is inspected for `error` regardless of
/// status. Error strings carry no secret material.
let exchangeCode
    (httpClient: HttpClient)
    (secretStore: ISecretStore)
    (config: GitHubOAuthAppConfig)
    (code: string)
    (redirectUri: string)
    : Async<Result<GitHubTokenResponse, string>> =
    async {
        let! secret = secretStore.GetSecret(platformScope, config.ClientSecretKey)

        match secret with
        | None ->
            return
                Error(
                    sprintf
                        "GitHub OAuth client secret not found in the secret store under key '%s' (scope %s)."
                        config.ClientSecretKey
                        platformScope
                )
        | Some clientSecret ->
            try
                let form =
                    new FormUrlEncodedContent(
                        dict [
                            "client_id", config.ClientId
                            "client_secret", clientSecret
                            "code", code
                            "redirect_uri", redirectUri
                        ]
                    )

                use req = new HttpRequestMessage(HttpMethod.Post, config.TokenBaseUrl)
                req.Content <- form
                req.Headers.TryAddWithoutValidation("Accept", "application/json") |> ignore

                req.Headers.TryAddWithoutValidation("User-Agent", "ToolUp-Platform-GitHubAuth")
                |> ignore

                let! resp = httpClient.SendAsync req |> Async.AwaitTask
                let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

                try
                    use doc = JsonDocument.Parse body
                    let root = doc.RootElement

                    match root.TryGetProperty "error" with
                    | true, errEl when errEl.ValueKind = JsonValueKind.String ->
                        // GitHub reports code-exchange failures in the body
                        // (bad_verification_code, redirect_uri_mismatch, …)
                        // — surfaced verbatim; operators read GitHub error
                        // codes faster than translated text.
                        let desc =
                            match root.TryGetProperty "error_description" with
                            | true, d when d.ValueKind = JsonValueKind.String -> d.GetString()
                            | _ -> errEl.GetString()

                        return Error(sprintf "GitHub rejected the code exchange: %s" desc)
                    | _ ->
                        match root.TryGetProperty "access_token" with
                        | true, tokEl when tokEl.ValueKind = JsonValueKind.String ->
                            let getStr (name: string) =
                                match root.TryGetProperty name with
                                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                                | _ -> ""

                            return
                                Ok {
                                    AccessToken = tokEl.GetString()
                                    Scope = getStr "scope"
                                    TokenType = getStr "token_type"
                                }
                        | _ -> return Error "GitHub code-exchange response carried neither access_token nor error"
                with :? JsonException ->
                    return Error "GitHub code-exchange response was not valid JSON"
            with
            | :? HttpRequestException as ex ->
                return Error(sprintf "could not reach the GitHub token endpoint: %s" ex.Message)
            | :? TaskCanceledException -> return Error "request to the GitHub token endpoint timed out"
    }