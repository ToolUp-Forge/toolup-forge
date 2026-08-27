// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.GoogleOAuthFlow

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Google OAuth 2.0 credential-flow companion ──────────────────────
//
// `IOAuthCredentialFlow` implementation for Google's Authorization Code
// with offline-access flow — the credential half of the Google Analytics
// 4 connector (`GoogleAnalyticsDataSource.fs`) and, unchanged, of any
// other Google API a deployment wants to read on the user's behalf: the
// only per-API difference is the `Scopes` list in the config.
//
// **`access_type=offline` and `prompt=consent` are both mandatory, and
// omitting either fails silently.** Without `access_type=offline` Google
// issues an access token and no refresh token; without
// `prompt=consent` it issues a refresh token only on the user's FIRST
// consent and returns none on every subsequent authorization of the
// same client. The second is the trap: the flow works during
// development, then a re-connect months later returns a token response
// with no `refresh_token`, and the substrate — which requires one —
// fails at a point far from the cause. Both parameters are therefore
// unconditional here rather than configurable.
//
// **PKCE is declared even though this is a confidential client.** Google
// accepts `code_challenge` / `code_verifier` on the web-server flow, and
// the substrate stashes the verifier in its own state entry, so an
// intercepted authorization code is useless to anyone who does not also
// hold the verifier. Defence in depth: the client secret already
// protects the exchange, and this protects it a second way at no cost.
//
// The two halves are symmetrically OPTIONAL rather than required. When
// the substrate supplies a challenge the authorize URL carries it and
// the exchange carries the matching verifier; when it supplies neither,
// both are omitted and Google accepts the exchange on the client secret
// alone. Because both halves read the same substrate-supplied option,
// they cannot end up mismatched — which is the only way a
// half-configured PKCE exchange fails, and it fails at the token
// endpoint with a message about the code rather than about the verifier.
//
// **Refresh tokens do not rotate.** Unlike GitHub, Google returns the
// same refresh token across refreshes (the token response usually omits
// `refresh_token` entirely), so there is nothing to persist back on
// refresh. If a response ever DOES carry a changed refresh token this
// flow writes it back to the substrate's slot, on the same reasoning the
// GitHub companion does — a rotation nobody handled would present as an
// `invalid_grant` some days later.
//
// GP 1 — no Google client library on this path: BCL `HttpClient` +
// `System.Text.Json` only. The GA4 data path (`GoogleAnalyticsDataSource`)
// is where the vendor SDK lives, and it lives in the same companion
// package, never in `ToolUp.Platform.*`.
// Phase 9c rule 4 — stateless across calls: `ISecretStore` + `HttpClient`
// arrive via `create`; all per-call state rides `OAuthFlowContext`.

/// Configuration for the Google flow. Endpoints default to Google's
/// published production values; `Scopes` drives both the admin-UI
/// descriptor and the authorize URL's `scope` parameter.
type GoogleOAuthFlowConfig = {
    /// Flow discriminator — the `{flowName}` URL segment, the state-store
    /// cursor prefix, and the secret-key prefix. Kebab-case; must be
    /// stable (renaming strands stored refresh tokens). Default
    /// `"google-analytics"`.
    FlowName: string
    /// Human-readable provider name for the admin-UI plugin.
    DisplayName: string
    /// Upstream OAuth scopes requested at consent + surfaced in the
    /// admin UI so the operator sees what they are consenting to.
    Scopes: string list
    /// Optional setup-docs URL for the admin-UI "Setup help" link.
    HelpUrl: string option
    /// Google's authorization endpoint.
    AuthorizeBaseUrl: string
    /// Google's token endpoint (code exchange + refresh).
    TokenBaseUrl: string
    /// Google's token-revocation endpoint.
    RevokeBaseUrl: string
}

module GoogleOAuthFlowConfig =
    [<Literal>]
    let DefaultFlowName = "google-analytics"

    [<Literal>]
    let DefaultAuthorizeBaseUrl = "https://accounts.google.com/o/oauth2/v2/auth"

    [<Literal>]
    let DefaultTokenBaseUrl = "https://oauth2.googleapis.com/token"

    [<Literal>]
    let DefaultRevokeBaseUrl = "https://oauth2.googleapis.com/revoke"

    /// The read-only Google Analytics scope. Requesting it puts the
    /// OAuth consent screen through Google's sensitive-scope
    /// verification before the app can leave testing mode — see the
    /// companion README.
    [<Literal>]
    let AnalyticsReadonlyScope = "https://www.googleapis.com/auth/analytics.readonly"

    /// The GA4 connector's flow: read-only Analytics, standard
    /// endpoints, standard names.
    let analyticsReadonly: GoogleOAuthFlowConfig = {
        FlowName = DefaultFlowName
        DisplayName = "Google Analytics"
        Scopes = [ AnalyticsReadonlyScope ]
        HelpUrl = Some "https://developers.google.com/analytics/devguides/reporting/data/v1"
        AuthorizeBaseUrl = DefaultAuthorizeBaseUrl
        TokenBaseUrl = DefaultTokenBaseUrl
        RevokeBaseUrl = DefaultRevokeBaseUrl
    }

    /// Build a config for any other Google API — you supply the flow
    /// name, display name and scopes; the endpoints are Google's and do
    /// not vary per API.
    let create (flowName: string) (displayName: string) (scopes: string list) : GoogleOAuthFlowConfig = {
        FlowName = flowName
        DisplayName = displayName
        Scopes = scopes
        HelpUrl = Some "https://developers.google.com/identity/protocols/oauth2/web-server"
        AuthorizeBaseUrl = DefaultAuthorizeBaseUrl
        TokenBaseUrl = DefaultTokenBaseUrl
        RevokeBaseUrl = DefaultRevokeBaseUrl
    }

// ─── Secret-key conventions (per the IOAuthCredentialFlow contract) ──

/// `ISecretStore` key holding the OAuth client id for one data source.
let clientIdKey (flowName: string) (dataSourceId: DataSourceId) =
    sprintf "%s-client-id-%s" flowName dataSourceId

/// `ISecretStore` key holding the OAuth client secret for one data
/// source.
let clientSecretKey (flowName: string) (dataSourceId: DataSourceId) =
    sprintf "%s-client-secret-%s" flowName dataSourceId

/// `ISecretStore` key the OAuth substrate persists the refresh token
/// under after a successful callback. Exposed because the connector's
/// refresh-descriptor wiring has to name the same key — a descriptor
/// pointed at a key nothing writes refreshes nothing, and does so
/// quietly.
let refreshTokenKey (flowName: string) (dataSourceId: DataSourceId) =
    sprintf "%s-refresh-%s" flowName dataSourceId

// ─── Token endpoint I/O ──────────────────────────────────────────────

/// Google's parsed token-endpoint success body. `RefreshToken` is
/// present on the code exchange and normally absent on refresh.
type private TokenGrant = {
    AccessToken: string
    RefreshToken: string option
    ExpiresInSeconds: int option
    IdToken: string option
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

/// Read `{flowName}-client-id-{ds}` + `{flowName}-client-secret-{ds}`
/// from the secret store. Missing either ⇒ `ClientCredentialMissing`
/// naming the key the operator has to fill.
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

/// POST a form to Google's token endpoint and parse the grant. Google
/// signals failure with a non-2xx status AND an `{ "error": … }` body,
/// so the body is inspected regardless of status — a 4xx whose body
/// names `invalid_grant` is the case the substrate must see as
/// `ProviderRejected`, not as a transport failure.
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

            let! resp = httpClient.SendAsync req |> Async.AwaitTask
            let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

            try
                use doc = JsonDocument.Parse body
                let root = doc.RootElement

                match tryGetString "error" root with
                | Some err ->
                    let desc = tryGetString "error_description" root |> Option.defaultValue err
                    // Keep the provider's own code in the message —
                    // `invalid_grant` tells an operator "re-consent"
                    // faster than any translation of it would.
                    return Error(ProviderRejected(sprintf "%s: %s" err desc))
                | None ->
                    match tryGetString "access_token" root with
                    | Some accessToken ->
                        return
                            Ok {
                                AccessToken = accessToken
                                RefreshToken = tryGetString "refresh_token" root
                                ExpiresInSeconds = tryGetInt "expires_in" root
                                IdToken = tryGetString "id_token" root
                            }
                    | None ->
                        return Error(OAuthFlowFailed "Google token response carried neither access_token nor error")
            with :? JsonException ->
                return Error(OAuthFlowFailed "Google token response was not valid JSON")
        with
        | :? HttpRequestException as ex -> return Error(NetworkError ex.Message)
        | :? TaskCanceledException -> return Error(NetworkError "request to the Google token endpoint timed out")
    }

/// Fallback access-token lifetime (seconds) when the response omits
/// `expires_in`. Google's is one hour and it is always reported; this
/// only guards a malformed response from producing a
/// `DateTime.UtcNow` expiry that reads as already-expired.
[<Literal>]
let private defaultExpirySeconds = 3600

// ─── Construction ────────────────────────────────────────────────────

/// Build the Google `IOAuthCredentialFlow`.
///
/// `secretStore` supplies the per-data-source client id / secret;
/// `httpClient` carries the token + revocation calls.
///
/// `refresher` is the Phase 10h token-refresh substrate, when the
/// deployment composed one. It is used for exactly one thing here:
/// `Revoke` unregisters the data source's refresh descriptor before the
/// substrate's Disconnect path deletes the refresh-token secret. Without
/// it the descriptor survives the disconnect and its scheduled job keeps
/// firing against a key that no longer holds a token — a permanent
/// failure the operator sees as a dead-lettered refresh for a connector
/// they believe they removed. `None` for a deployment with no refresher
/// composed; the flow is otherwise identical (GP 13).
let create
    (httpClient: HttpClient)
    (secretStore: ISecretStore)
    (refresher: IOAuthTokenRefresher option)
    (config: GoogleOAuthFlowConfig)
    : IOAuthCredentialFlow =
    let flowName = config.FlowName

    { new IOAuthCredentialFlow with
        member _.Name = flowName

        member _.Descriptor = {
            DisplayName = config.DisplayName
            Scopes = config.Scopes
            HelpUrl = config.HelpUrl
        }

        // Google accepts PKCE on the web-server flow. See the module
        // note — this is defence in depth over the client secret, not a
        // substitute for it.
        member _.SupportsPkce = true

        member _.BuildAuthorizeUrl(ctx, state, redirectUri, pkce) = async {
            let! clientId = secretStore.GetSecret(ctx.ScopeId, clientIdKey flowName ctx.DataSourceId)

            match clientId with
            | None -> return Error(ClientCredentialMissing(clientIdKey flowName ctx.DataSourceId))
            | Some id ->
                let scope = config.Scopes |> String.concat " "

                // PKCE parameters are appended when the substrate
                // supplied a challenge and omitted when it did not.
                // Google's web-server flow accepts both, so a caller
                // that does not stash a verifier still gets a working
                // authorize URL rather than an error — and, because the
                // token exchange mirrors this optionality, the pair can
                // never end up mismatched.
                let pkceParams =
                    match pkce with
                    | Some challenge -> [
                        "code_challenge", challenge.Challenge
                        "code_challenge_method", challenge.Method
                      ]
                    | None -> []

                let query =
                    [
                        "client_id", id
                        "redirect_uri", redirectUri
                        "response_type", "code"
                        "scope", scope
                        "state", state
                        // Both mandatory — see the module note.
                        "access_type", "offline"
                        "prompt", "consent"
                    ]
                    @ pkceParams
                    |> List.map (fun (k, v) -> sprintf "%s=%s" k (urlEncode v))
                    |> String.concat "&"

                return Ok(sprintf "%s?%s" config.AuthorizeBaseUrl query)
        }

        member _.ExchangeCode(ctx, code, redirectUri, codeVerifier) = async {
            match! readClientCredentials secretStore flowName ctx with
            | Error e -> return Error e
            | Ok(clientId, clientSecret) ->
                // Mirrors `BuildAuthorizeUrl`: the verifier is sent when
                // the substrate stashed one and omitted when it did not.
                // The two must agree, and they do because both read the
                // same substrate-supplied option.
                let verifierField =
                    match codeVerifier with
                    | Some verifier -> [ "code_verifier", verifier ]
                    | None -> []

                let! grant =
                    postToken
                        httpClient
                        config.TokenBaseUrl
                        ([
                            "client_id", clientId
                            "client_secret", clientSecret
                            "code", code
                            // Must match the authorize step byte for
                            // byte — Google compares for equality, not
                            // equivalence.
                            "redirect_uri", redirectUri
                            "grant_type", "authorization_code"
                         ]
                         @ verifierField)

                match grant with
                | Error e -> return Error e
                | Ok g ->
                    match g.RefreshToken with
                    | None ->
                        // The `prompt=consent` case the module note
                        // describes. Naming it precisely here is the
                        // difference between a five-minute fix and an
                        // afternoon.
                        return
                            Error(
                                OAuthFlowFailed
                                    "Google returned no refresh_token — the authorize request must carry access_type=offline and prompt=consent, and the account must not have an existing grant that suppresses re-issue"
                            )
                    | Some refreshToken ->
                        let expiresIn = g.ExpiresInSeconds |> Option.defaultValue defaultExpirySeconds

                        return
                            Ok {
                                RefreshToken = refreshToken
                                AccessToken = Some g.AccessToken
                                ExpiresAt = Some(DateTime.UtcNow.AddSeconds(float expiresIn))
                                IdToken = g.IdToken
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
                    // Google does not rotate; handle it anyway (see the
                    // module note). Best-effort — a write failure does
                    // not invalidate the access token we just minted.
                    match g.RefreshToken with
                    | Some rotated when rotated <> refreshToken ->
                        let! _ = secretStore.SetSecret(ctx.ScopeId, refreshTokenKey flowName ctx.DataSourceId, rotated)
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
            // Phase 10h adoption, revoke half. The substrate's Disconnect
            // path calls `Revoke` and THEN deletes the refresh-token
            // secret, so unregistering here satisfies the ordering the
            // descriptor contract needs: the scheduled job is cancelled
            // while its token is still readable, never after.
            //
            // Unconditional, and ahead of the network call: a Google
            // outage must not leave a live descriptor behind for a
            // connector the operator has disconnected.
            match refresher with
            | Some r -> do! r.UnregisterDescriptor(flowName, ctx.DataSourceId)
            | None -> ()

            try
                // Google's revocation endpoint takes the token as a
                // form field and authenticates nothing else — revoking
                // a refresh token invalidates every access token minted
                // from it.
                use content = new FormUrlEncodedContent(dict [ "token", refreshToken ])
                use req = new HttpRequestMessage(HttpMethod.Post, config.RevokeBaseUrl)
                req.Content <- content
                req.Headers.TryAddWithoutValidation("Accept", "application/json") |> ignore

                let! resp = httpClient.SendAsync req |> Async.AwaitTask

                if resp.IsSuccessStatusCode then
                    return Ok()
                else
                    let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

                    // Google answers 400 `invalid_token` for a token it
                    // has already invalidated. From the caller's side
                    // that is the desired end state, not a failure.
                    if body.Contains "invalid_token" then
                        return Ok()
                    else
                        return Error(ProviderRejected(sprintf "revocation returned HTTP %d" (int resp.StatusCode)))
            with
            | :? HttpRequestException as ex -> return Error(NetworkError ex.Message)
            | :? TaskCanceledException ->
                return Error(NetworkError "request to the Google revocation endpoint timed out")
        }
    }