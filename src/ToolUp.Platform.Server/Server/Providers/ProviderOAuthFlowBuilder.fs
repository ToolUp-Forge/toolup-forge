// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ProviderOAuthFlow

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Phase 43.B — the shared Authorization Code provider flow ─────
//
// Every vendor's provider-OAuth flow is the same RFC 6749
// Authorization Code + PKCE exchange over different endpoint URLs and
// scopes. This builder is that flow once; a vendor companion supplies
// a `ProviderOAuthFlowConfig` and gets an `IProviderOAuthFlow` back.
//
// **Why the generic half lives in `Platform.Server` and not in each
// companion.** GP 1 keeps VENDOR dependencies out of the SDK core —
// and there is no vendor dependency here: this is BCL `HttpClient`
// against a URL the caller names. What GP 1 would be violated by is
// the opposite arrangement, where `ToolUp.Platform` referenced
// `ToolUp.AIProviders.Claude`. It does not; the reference points the
// other way, and the companion file is a config record.
//
// **The network sits behind one seam.** All outbound token traffic
// goes through an `OAuthTokenPost` function, so a contract pack drives
// the whole flow — authorize URL, code exchange, refresh, revoke — with
// no socket and no vendor account. That is deliberate per the phase's
// design guardrail: contract packs are the conformance bar, not live
// -network tests.

/// Static description of one vendor's Authorization Code endpoints.
type ProviderOAuthFlowConfig = {
    /// `IOAuthCredentialFlow.Name` — the URL segment and the secret-key
    /// prefix. Kebab-case, stable for the deployment's lifetime.
    FlowName: string
    /// Human-readable name for the "Connect …" button.
    DisplayName: string
    /// `AIProviderDescriptor.Id` the minted `ProviderEntry` names.
    ProviderId: string
    /// Default `ProviderEntry.Label` for a one-click connect.
    DefaultEntryLabel: string
    /// Optional model to stamp on the minted entry.
    DefaultModel: string option
    /// Upstream authorization endpoint (the consent page).
    AuthorizeEndpoint: string
    /// Upstream token endpoint (code exchange + refresh).
    TokenEndpoint: string
    /// Optional revocation endpoint. `None` makes `Revoke` return
    /// `RevocationUnsupported`, which the substrate handles by deleting
    /// the local token only.
    RevokeEndpoint: string option
    /// Scopes requested at consent.
    Scopes: string list
    /// Optional setup-help link for the admin UI.
    HelpUrl: string option
    /// Whether the upstream accepts PKCE (RFC 7636).
    SupportsPkce: bool
}

/// The single outbound-HTTP seam: POST a form to `url`, return the raw
/// response body. Implementations must not throw — transport failure
/// is `Error (NetworkError …)`.
type OAuthTokenPost = string -> (string * string) list -> Async<Result<string, OAuthError>>

/// `ISecretStore` key holding the OAuth `client_id` for a flow.
/// Deployment-level rather than per-entry: a provider OAuth app is
/// registered once with the vendor, and every user of the deployment
/// consents against the same `client_id`. (The Phase 10e data-source
/// flows key theirs per data source because each connector instance
/// can point at a different upstream project.)
let clientIdKey (flowName: string) = $"{flowName}-client-id"

/// `ISecretStore` key holding the OAuth `client_secret`.
let clientSecretKey (flowName: string) = $"{flowName}-client-secret"

/// Fallback access-token lifetime when the token response omits
/// `expires_in`. Guards a malformed response from producing an expiry
/// that reads as already-elapsed and re-refreshes on every tick.
[<Literal>]
let DefaultExpirySeconds = 3600

/// Real-network `OAuthTokenPost` over a caller-owned `HttpClient`.
/// The body is read and returned regardless of status code: an OAuth
/// error arrives as a 4xx WITH a JSON body naming `invalid_grant`, and
/// treating that as a transport failure would hide the one diagnostic
/// the operator needs.
let httpPost (client: HttpClient) : OAuthTokenPost =
    fun url fields -> async {
        try
            use content = new FormUrlEncodedContent(dict fields)
            use request = new HttpRequestMessage(HttpMethod.Post, url)
            request.Content <- content
            request.Headers.TryAddWithoutValidation("Accept", "application/json") |> ignore

            let! response = client.SendAsync request |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            return Ok body
        with
        | :? HttpRequestException as ex -> return Error(NetworkError ex.Message)
        | :? TaskCanceledException -> return Error(NetworkError $"request to {url} timed out")
    }

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

/// Parsed token-endpoint success body.
type private TokenGrant = {
    AccessToken: string
    RefreshToken: string option
    ExpiresInSeconds: int option
    IdToken: string option
}

/// Parse a token-endpoint body into a grant or a typed error. An
/// `error` member wins over an `access_token` member, so a provider
/// that returns both (some do, on a partially-failed refresh) is read
/// as the failure it is.
let private parseGrant (body: string) : Result<TokenGrant, OAuthError> =
    try
        use doc = JsonDocument.Parse body
        let root = doc.RootElement

        match tryGetString "error" root with
        | Some err ->
            let description = tryGetString "error_description" root |> Option.defaultValue err
            Error(ProviderRejected $"{err}: {description}")
        | None ->
            match tryGetString "access_token" root with
            | Some accessToken ->
                Ok {
                    AccessToken = accessToken
                    RefreshToken = tryGetString "refresh_token" root
                    ExpiresInSeconds = tryGetInt "expires_in" root
                    IdToken = tryGetString "id_token" root
                }
            | None -> Error(OAuthFlowFailed "token response carried neither access_token nor error")
    with :? JsonException ->
        Error(OAuthFlowFailed "token response was not valid JSON")

/// Build an `IProviderOAuthFlow` from a vendor config.
///
/// `post` is the outbound seam (`httpPost client` in production, a stub
/// in tests). `secretStore` supplies the deployment's `client_id` /
/// `client_secret` at the caller's scope.
let create (post: OAuthTokenPost) (secretStore: ISecretStore) (config: ProviderOAuthFlowConfig) : IProviderOAuthFlow =
    let flowName = config.FlowName

    let readClientCredentials (scopeId: string) : Async<Result<string * string, OAuthError>> = async {
        let! id = secretStore.GetSecret(scopeId, clientIdKey flowName)
        let! secret = secretStore.GetSecret(scopeId, clientSecretKey flowName)

        match id, secret with
        | Some i, Some s -> return Ok(i, s)
        | None, _ -> return Error(ClientCredentialMissing(clientIdKey flowName))
        | _, None -> return Error(ClientCredentialMissing(clientSecretKey flowName))
    }

    let exchange (scopeId: string) (fields: (string * string) list) : Async<Result<TokenGrant, OAuthError>> = async {
        let! credentials = readClientCredentials scopeId

        match credentials with
        | Error e -> return Error e
        | Ok(clientId, clientSecret) ->
            let form = fields @ [ "client_id", clientId; "client_secret", clientSecret ]
            let! body = post config.TokenEndpoint form

            match body with
            | Error e -> return Error e
            | Ok raw -> return parseGrant raw
    }

    { new IProviderOAuthFlow with
        member _.Name = flowName

        member _.Descriptor = {
            DisplayName = config.DisplayName
            Scopes = config.Scopes
            HelpUrl = config.HelpUrl
        }

        member _.SupportsPkce = config.SupportsPkce

        member _.ProviderId = config.ProviderId
        member _.DefaultEntryLabel = config.DefaultEntryLabel
        member _.DefaultModel = config.DefaultModel

        member _.BuildAuthorizeUrl(ctx, state, redirectUri, pkce) = async {
            let! clientId = secretStore.GetSecret(ctx.ScopeId, clientIdKey flowName)

            match clientId with
            | None -> return Error(ClientCredentialMissing(clientIdKey flowName))
            | Some id ->
                let scopes = config.Scopes |> String.concat " "

                let baseParams = [
                    "response_type", "code"
                    "client_id", id
                    "redirect_uri", redirectUri
                    "state", state
                    "scope", scopes
                ]

                // The substrate never hands a PKCE challenge to a flow
                // that did not declare `SupportsPkce`, and a declaring
                // flow is never handed `None` — so appending only when
                // `Some` keeps a non-PKCE flow's URL byte-identical to
                // the pre-PKCE substrate's (GP 11).
                let pkceParams =
                    match pkce with
                    | Some challenge -> [
                        "code_challenge", challenge.Challenge
                        "code_challenge_method", challenge.Method
                      ]
                    | None -> []

                let query =
                    baseParams @ pkceParams
                    |> List.map (fun (k, v) -> $"{Uri.EscapeDataString k}={Uri.EscapeDataString v}")
                    |> String.concat "&"

                let separator = if config.AuthorizeEndpoint.Contains "?" then "&" else "?"
                return Ok $"{config.AuthorizeEndpoint}{separator}{query}"
        }

        member _.ExchangeCode(ctx, code, redirectUri, codeVerifier) = async {
            let verifierField =
                match codeVerifier with
                | Some v -> [ "code_verifier", v ]
                | None -> []

            let! grant =
                exchange
                    ctx.ScopeId
                    ([
                        "grant_type", "authorization_code"
                        "code", code
                        "redirect_uri", redirectUri
                     ]
                     @ verifierField)

            match grant with
            | Error e -> return Error e
            | Ok g ->
                match g.RefreshToken with
                | None ->
                    // No refresh token means the connection cannot
                    // survive the access token's lifetime. Failing here
                    // is the honest outcome: binding an entry that
                    // stops working in an hour, with no way to recover
                    // it without re-consent, would present as a
                    // successful connect.
                    return
                        Error(
                            OAuthFlowFailed
                                $"{config.DisplayName} returned no refresh_token — the consent request must ask for offline access."
                        )
                | Some refresh ->
                    let seconds = g.ExpiresInSeconds |> Option.defaultValue DefaultExpirySeconds

                    return
                        Ok {
                            RefreshToken = refresh
                            AccessToken = Some g.AccessToken
                            ExpiresAt = Some(DateTime.UtcNow.AddSeconds(float seconds))
                            IdToken = g.IdToken
                        }
        }

        member _.RefreshAccessToken(ctx, refreshToken) = async {
            let! grant = exchange ctx.ScopeId [ "grant_type", "refresh_token"; "refresh_token", refreshToken ]

            match grant with
            | Error e -> return Error e
            | Ok g ->
                let seconds = g.ExpiresInSeconds |> Option.defaultValue DefaultExpirySeconds

                return
                    Ok {
                        Token = g.AccessToken
                        ExpiresAt = DateTime.UtcNow.AddSeconds(float seconds)
                    }
        }

        member _.Revoke(ctx, refreshToken) = async {
            match config.RevokeEndpoint with
            | None -> return Error RevocationUnsupported
            | Some endpoint ->
                let! credentials = readClientCredentials ctx.ScopeId

                match credentials with
                | Error e -> return Error e
                | Ok(clientId, clientSecret) ->
                    let! body =
                        post endpoint [
                            "token", refreshToken
                            "token_type_hint", "refresh_token"
                            "client_id", clientId
                            "client_secret", clientSecret
                        ]

                    match body with
                    | Error e -> return Error e
                    | Ok raw ->
                        // A revocation endpoint answers 200 with an
                        // empty body on success. Only a body that
                        // explicitly names an error is a failure —
                        // treating "not JSON" as failure would report
                        // every successful revocation as one.
                        if String.IsNullOrWhiteSpace raw then
                            return Ok()
                        else
                            try
                                use doc = JsonDocument.Parse raw

                                match tryGetString "error" doc.RootElement with
                                | Some err -> return Error(ProviderRejected err)
                                | None -> return Ok()
                            with :? JsonException ->
                                return Ok()
        }
    }