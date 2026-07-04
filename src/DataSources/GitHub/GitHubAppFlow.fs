// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.GitHubAppFlow

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── GitHub App OAuth credential-flow companion ──────────────────────
//
// `IOAuthCredentialFlow` implementation for the GitHub *App* user-to-
// server OAuth flow — the connector-credential story (persist a
// long-lived credential so the deployment can call the GitHub API on the
// user's behalf), distinct from the `ToolUp.AuthProviders.GitHub`
// sign-in companion (which validates an inbound bearer for login).
//
// **Why GitHub Apps, not classic OAuth Apps.** The substrate is built
// around a *required* refresh token (`OAuthCredentials.RefreshToken`) and
// `RefreshAccessToken` minting short-lived access tokens from it — the
// OAuth "offline access" shape. Classic GitHub OAuth Apps issue a single
// non-expiring access token and no refresh token, which doesn't fit. A
// GitHub App (or an OAuth App with "Expire user authorization tokens"
// enabled) issues `access_token` (~8h) + `refresh_token` (~6 months),
// the canonical fit. The degenerate classic-OAuth-App path is handled
// (access token stored as the refresh token) but GitHub Apps are the
// supported target.
//
// **Refresh-token rotation.** GitHub *rotates* the refresh token on every
// refresh — the response carries a fresh `refresh_token` and the old one
// is invalidated. The substrate's `RefreshAccessToken` returns only an
// access token, so this flow persists the rotated refresh token back to
// its own `{flowName}-refresh-{dataSourceId}` secret slot as a side
// effect, keeping the stored credential valid across refreshes. Without
// this the second refresh would fail `invalid_grant`.
//
// GP 1 — no `Octokit`: BCL `HttpClient` + `System.Text.Json` only.
// Phase 9c rule 4 — stateless across calls: `ISecretStore` + `HttpClient`
// arrive via `create`; all per-call state rides `OAuthFlowContext`.

/// Configuration for the GitHub App flow. Endpoints default to
/// github.com; override them (and `ApiBaseUrl`) for GitHub Enterprise
/// Server. `Scopes` populates the admin-UI descriptor + the authorize
/// URL's `scope` parameter.
type GitHubAppFlowConfig = {
    /// Flow discriminator — the `{flowName}` URL segment + the secret-key
    /// prefix. Kebab-case; must be stable (renaming strands stored
    /// refresh tokens). Default `"github"`.
    FlowName: string
    /// Human-readable provider name for the admin-UI plugin.
    DisplayName: string
    /// Upstream OAuth scopes requested at consent + surfaced in the admin
    /// UI. GitHub Apps largely derive permissions from the app
    /// registration, but the `scope` parameter is still honoured and
    /// documents intent.
    Scopes: string list
    /// Optional setup-docs URL for the admin-UI "Setup help" link.
    HelpUrl: string option
    /// GitHub authorize endpoint. github.com default; GHES equivalent
    /// (`https://<host>/login/oauth/authorize`) otherwise.
    AuthorizeBaseUrl: string
    /// GitHub token endpoint (code exchange + refresh).
    TokenBaseUrl: string
    /// GitHub REST API base — used only by `Revoke` (grant deletion).
    ApiBaseUrl: string
}

module GitHubAppFlowConfig =
    [<Literal>]
    let DefaultFlowName = "github"

    [<Literal>]
    let DefaultAuthorizeBaseUrl = "https://github.com/login/oauth/authorize"

    [<Literal>]
    let DefaultTokenBaseUrl = "https://github.com/login/oauth/access_token"

    [<Literal>]
    let DefaultApiBaseUrl = "https://api.github.com"

    /// Build a config for the common github.com case — you supply the
    /// scopes; endpoints + names default to the standard values.
    let create (scopes: string list) : GitHubAppFlowConfig = {
        FlowName = DefaultFlowName
        DisplayName = "GitHub"
        Scopes = scopes
        HelpUrl = Some "https://docs.github.com/en/apps/creating-github-apps"
        AuthorizeBaseUrl = DefaultAuthorizeBaseUrl
        TokenBaseUrl = DefaultTokenBaseUrl
        ApiBaseUrl = DefaultApiBaseUrl
    }

// ─── Secret-key conventions (per the IOAuthCredentialFlow contract) ──

let private clientIdKey (flowName: string) (dataSourceId: DataSourceId) =
    sprintf "%s-client-id-%s" flowName dataSourceId

let private clientSecretKey (flowName: string) (dataSourceId: DataSourceId) =
    sprintf "%s-client-secret-%s" flowName dataSourceId

/// The substrate's own refresh-token slot — this flow writes GitHub's
/// rotated refresh token back here on every `RefreshAccessToken`.
let private refreshKey (flowName: string) (dataSourceId: DataSourceId) =
    sprintf "%s-refresh-%s" flowName dataSourceId

// ─── Token endpoint I/O ──────────────────────────────────────────────

/// GitHub's parsed token-endpoint success body (code-exchange + refresh
/// share the shape). `RefreshToken` is present when the app issues
/// expiring tokens; `ExpiresInSeconds` is the access-token lifetime.
type private TokenGrant = {
    AccessToken: string
    RefreshToken: string option
    ExpiresInSeconds: int option
}

let private urlEncode (value: string) : string = Uri.EscapeDataString value

let private tryGetString (name: string) (el: JsonElement) : string option =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

let private tryGetInt (name: string) (el: JsonElement) : int option =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Number ->
        match v.TryGetInt32() with
        | true, n -> Some n
        | _ -> None
    | _ -> None

/// Read `{flowName}-client-id-{ds}` + `{flowName}-client-secret-{ds}` from
/// the secret store. Missing either ⇒ `ClientCredentialMissing`.
let private readClientCredentials
    (secretStore: ISecretStore)
    (flowName: string)
    (ctx: OAuthFlowContext)
    : Async<Result<string * string, OAuthError>> =
    async {
        let! clientId = secretStore.GetSecret(ctx.ScopeId, clientIdKey flowName ctx.DataSourceId)
        let! clientSecret = secretStore.GetSecret(ctx.ScopeId, clientSecretKey flowName ctx.DataSourceId)

        match clientId, clientSecret with
        | Some id, Some secret -> return Ok(id, secret)
        | None, _ -> return Error(ClientCredentialMissing(clientIdKey flowName ctx.DataSourceId))
        | _, None -> return Error(ClientCredentialMissing(clientSecretKey flowName ctx.DataSourceId))
    }

/// POST a form to the GitHub token endpoint (`Accept: application/json`)
/// and parse the grant. GitHub returns HTTP 200 with an `{ "error": … }`
/// body on failure, so the body is inspected regardless of status.
let private postToken
    (httpClient: HttpClient)
    (tokenUrl: string)
    (fields: (string * string) list)
    : Async<Result<TokenGrant, OAuthError>> =
    async {
        try
            use content = new FormUrlEncodedContent(dict fields)
            use req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
            req.Content <- content
            req.Headers.TryAddWithoutValidation("Accept", "application/json") |> ignore

            req.Headers.TryAddWithoutValidation("User-Agent", "ToolUp-Platform-GitHubAppFlow")
            |> ignore

            let! resp = httpClient.SendAsync req |> Async.AwaitTask
            let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

            try
                use doc = JsonDocument.Parse body
                let root = doc.RootElement

                match tryGetString "error" root with
                | Some err ->
                    let desc = tryGetString "error_description" root |> Option.defaultValue err
                    return Error(ProviderRejected desc)
                | None ->
                    match tryGetString "access_token" root with
                    | Some accessToken ->
                        return
                            Ok {
                                AccessToken = accessToken
                                RefreshToken = tryGetString "refresh_token" root
                                ExpiresInSeconds = tryGetInt "expires_in" root
                            }
                    | None ->
                        return Error(OAuthFlowFailed "GitHub token response carried neither access_token nor error")
            with :? JsonException ->
                return Error(OAuthFlowFailed "GitHub token response was not valid JSON")
        with
        | :? HttpRequestException as ex -> return Error(NetworkError ex.Message)
        | :? TaskCanceledException -> return Error(NetworkError "request to the GitHub token endpoint timed out")
    }

/// Default access-token lifetime (seconds) when the response omits
/// `expires_in`. GitHub App user tokens are ~8h; this fallback only
/// applies to a non-expiring classic-OAuth-App token, where any future
/// instant satisfies the substrate's "refresh a few minutes before
/// expiry" contract without forcing a needless refresh loop.
[<Literal>]
let private defaultExpirySeconds = 28800

// ─── Construction ────────────────────────────────────────────────────

/// Build the GitHub App `IOAuthCredentialFlow`. `secretStore` supplies
/// the per-data-source client id/secret (and receives the rotated refresh
/// token); `httpClient` carries the token-endpoint + revocation calls.
let create (httpClient: HttpClient) (secretStore: ISecretStore) (config: GitHubAppFlowConfig) : IOAuthCredentialFlow =
    let flowName = config.FlowName

    { new IOAuthCredentialFlow with
        member _.Name = flowName

        member _.Descriptor = {
            DisplayName = config.DisplayName
            Scopes = config.Scopes
            HelpUrl = config.HelpUrl
        }

        // GitHub Apps / OAuth Apps do not support PKCE — the substrate
        // passes `None` at both ends and the wire output carries no PKCE
        // parameters.
        member _.SupportsPkce = false

        member _.BuildAuthorizeUrl(ctx, state, redirectUri, _pkce) = async {
            let! clientId = secretStore.GetSecret(ctx.ScopeId, clientIdKey flowName ctx.DataSourceId)

            match clientId with
            | None -> return Error(ClientCredentialMissing(clientIdKey flowName ctx.DataSourceId))
            | Some id ->
                let scope = config.Scopes |> String.concat " "

                let query =
                    [ "client_id", id; "redirect_uri", redirectUri; "state", state; "scope", scope ]
                    |> List.map (fun (k, v) -> sprintf "%s=%s" k (urlEncode v))
                    |> String.concat "&"

                return Ok(sprintf "%s?%s" config.AuthorizeBaseUrl query)
        }

        member _.ExchangeCode(ctx, code, redirectUri, _codeVerifier) = async {
            match! readClientCredentials secretStore flowName ctx with
            | Error e -> return Error e
            | Ok(clientId, clientSecret) ->
                let! grant =
                    postToken httpClient config.TokenBaseUrl [
                        "client_id", clientId
                        "client_secret", clientSecret
                        "code", code
                        "redirect_uri", redirectUri
                        "grant_type", "authorization_code"
                    ]

                match grant with
                | Error e -> return Error e
                | Ok g ->
                    // Prefer the issued refresh token (GitHub App / expiring
                    // OAuth App). If absent (classic non-expiring OAuth App),
                    // store the access token as the refresh token — the
                    // documented degenerate path; `RefreshAccessToken` then
                    // returns it verbatim.
                    let refreshToken = g.RefreshToken |> Option.defaultValue g.AccessToken
                    let expiresIn = g.ExpiresInSeconds |> Option.defaultValue defaultExpirySeconds

                    return
                        Ok {
                            RefreshToken = refreshToken
                            AccessToken = Some g.AccessToken
                            ExpiresAt = Some(DateTime.UtcNow.AddSeconds(float expiresIn))
                            IdToken = None
                        }
        }

        member _.RefreshAccessToken(ctx, refreshToken) = async {
            match! readClientCredentials secretStore flowName ctx with
            | Error e -> return Error e
            | Ok(clientId, clientSecret) ->
                let! grant =
                    postToken httpClient config.TokenBaseUrl [
                        "client_id", clientId
                        "client_secret", clientSecret
                        "refresh_token", refreshToken
                        "grant_type", "refresh_token"
                    ]

                match grant with
                | Error e -> return Error e
                | Ok g ->
                    // GitHub rotates the refresh token on every refresh.
                    // Persist the new one back to the substrate's slot so
                    // the next refresh doesn't fail `invalid_grant`. Best-
                    // effort: a write failure is swallowed (the access token
                    // is still valid now) rather than failing the refresh.
                    match g.RefreshToken with
                    | Some rotated when rotated <> refreshToken ->
                        let! _ = secretStore.SetSecret(ctx.ScopeId, refreshKey flowName ctx.DataSourceId, rotated)
                        ()
                    | _ -> ()

                    let expiresIn = g.ExpiresInSeconds |> Option.defaultValue defaultExpirySeconds

                    return
                        Ok {
                            Token = g.AccessToken
                            ExpiresAt = DateTime.UtcNow.AddSeconds(float expiresIn)
                        }
        }

        member _.Revoke(ctx, refreshToken) = async {
            // GitHub has no refresh-token-specific revoke endpoint. Mint a
            // short-lived access token from the refresh token, then delete
            // the whole OAuth grant (`DELETE /applications/{client_id}/grant`,
            // client Basic auth) — revoking every token for this user +
            // app. Best-effort: any failure returns a typed error and the
            // substrate deletes the local secret regardless. Never throws.
            match! readClientCredentials secretStore flowName ctx with
            | Error e -> return Error e
            | Ok(clientId, clientSecret) ->
                let! grant =
                    postToken httpClient config.TokenBaseUrl [
                        "client_id", clientId
                        "client_secret", clientSecret
                        "refresh_token", refreshToken
                        "grant_type", "refresh_token"
                    ]

                match grant with
                | Error(ProviderRejected _) ->
                    // Refresh token already invalid ⇒ the grant is already
                    // gone. Treat as a successful revocation.
                    return Ok()
                | Error e -> return Error e
                | Ok g ->
                    try
                        let url =
                            config.ApiBaseUrl.TrimEnd('/')
                            + "/applications/"
                            + urlEncode clientId
                            + "/grant"

                        use req = new HttpRequestMessage(HttpMethod.Delete, url)

                        let basic =
                            Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + clientSecret))

                        req.Headers.TryAddWithoutValidation("Authorization", "Basic " + basic) |> ignore

                        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json")
                        |> ignore

                        req.Headers.TryAddWithoutValidation("User-Agent", "ToolUp-Platform-GitHubAppFlow")
                        |> ignore

                        req.Content <- new StringContent(sprintf """{"access_token":"%s"}""" g.AccessToken)

                        let! resp = httpClient.SendAsync req |> Async.AwaitTask

                        // 204 No Content on success; 404 ⇒ grant already
                        // absent — both are "revoked" from our perspective.
                        if resp.IsSuccessStatusCode || resp.StatusCode = HttpStatusCode.NotFound then
                            return Ok()
                        else
                            return
                                Error(
                                    ProviderRejected(sprintf "grant revocation returned HTTP %d" (int resp.StatusCode))
                                )
                    with
                    | :? HttpRequestException as ex -> return Error(NetworkError ex.Message)
                    | :? TaskCanceledException ->
                        return Error(NetworkError "request to the GitHub revocation endpoint timed out")
        }
    }