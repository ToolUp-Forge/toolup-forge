// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 830 - the Google Calendar bridge's credential half: the
/// Authorization Code flow on the OAuth substrate, and the per-call
/// bearer-token resolution the bridge reads through the refresh substrate.
module ToolUp.Calendar.GoogleCalendarOAuth

open System
open System.Globalization
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.ProviderOAuthFlow
open ToolUp.Scheduling.ICalendarBridge

// ─── Phase 830 — Google Calendar credentials ────────────────────────
//
// Two halves, both on shipped substrate and neither with a sign-in UI of
// its own:
//
//   * **Acquisition** — `create` builds the `IOAuthCredentialFlow` the
//     OAuth substrate drives (`ServerApp.withOAuthFlow`). A calendar
//     credential is a CONNECTION in the substrate's data-source sense:
//     the operator enters the Google Cloud OAuth client id + secret and
//     consents through the per-`Kind` credential form registry
//     (`DataSourceCredentialUIRegistry`), and the substrate persists the
//     refresh token under `{flowName}-refresh-{connectionId}` in the
//     connection's scope — the same key `refreshTokenKey` names here.
//   * **Use** — `GoogleCalendarTokenSource` resolves a bearer token per
//     bridge call. With the refresh substrate composed it reads the
//     cached access token the substrate keeps warm and asks the
//     substrate (`IOAuthTokenRefresher.RefreshNow`) when it is missing,
//     expired or refused; without it, it mints one from the refresh
//     token on every call. Either way every secret is read per call, so
//     a rotated refresh token or client secret is picked up on the next
//     call with no restart.
//
// **`access_type=offline` and `prompt=consent` are unconditional**, for
// the reason the Google Analytics flow gives: without the first Google
// issues no refresh token at all, and without the second it issues one
// only on the account's FIRST consent — a re-connect months later then
// fails far from its cause.
//
// **Why this flow is not the Google Analytics one reused.** That flow is
// BCL-only too, but it ships inside the Google Analytics package, which
// carries the Google client libraries; referencing it would pull a
// vendor SDK into a companion that deliberately has none. The token
// traffic goes through the substrate's own `OAuthTokenPost` seam
// instead, so a test drives the whole flow with no socket.

/// Default flow name — the `/api/oauth/{flowName}/*` route segment, the
/// state-store prefix and the secret-key prefix. Stable: renaming it
/// strands every stored refresh token.
[<Literal>]
let DefaultFlowName = "google-calendar"

/// Read / write events on calendars the user can reach — everything
/// `Push`, `Pull` and a watch channel need.
[<Literal>]
let EventsScope = "https://www.googleapis.com/auth/calendar.events"

/// Read the user's calendar list — the health probe's call, and the
/// bridge's own link check (`calendarList.get`; `calendars.get` is not
/// authorised under `calendar.events`).
[<Literal>]
let CalendarListReadonlyScope =
    "https://www.googleapis.com/auth/calendar.calendarlist.readonly"

/// Google's authorization endpoint.
[<Literal>]
let DefaultAuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth"

/// Google's token endpoint (code exchange and refresh).
[<Literal>]
let DefaultTokenEndpoint = "https://oauth2.googleapis.com/token"

/// Google's token-revocation endpoint.
[<Literal>]
let DefaultRevokeEndpoint = "https://oauth2.googleapis.com/revoke"

/// Configuration for the Google Calendar OAuth flow. Carries no secret.
type GoogleCalendarOAuthConfig = {
    /// `IOAuthCredentialFlow.Name` and the secret-key prefix.
    FlowName: string
    /// Human-readable provider name for the credential form.
    DisplayName: string
    /// Scopes requested at consent.
    Scopes: string list
    /// Optional setup-help link for the credential form.
    HelpUrl: string option
    /// Authorization endpoint.
    AuthorizeEndpoint: string
    /// Token endpoint — also the refresh descriptor's `TokenEndpoint`.
    TokenEndpoint: string
    /// Revocation endpoint.
    RevokeEndpoint: string
}

/// The default configuration and its variations.
module GoogleCalendarOAuthConfig =
    /// Google's production endpoints, the events scope plus the
    /// read-only calendar-list scope the health probe uses.
    let defaults: GoogleCalendarOAuthConfig = {
        FlowName = DefaultFlowName
        DisplayName = "Google Calendar"
        Scopes = [ EventsScope; CalendarListReadonlyScope ]
        HelpUrl = Some "https://developers.google.com/calendar/api/guides/auth"
        AuthorizeEndpoint = DefaultAuthorizeEndpoint
        TokenEndpoint = DefaultTokenEndpoint
        RevokeEndpoint = DefaultRevokeEndpoint
    }

// ─── Secret-key conventions ─────────────────────────────────────────

/// `ISecretStore` key holding the OAuth client id for one connection —
/// the Phase 10e data-source convention, so a credential form that
/// writes Google client credentials for a data source writes these.
let clientIdKey (flowName: string) (connectionId: string) : string =
    sprintf "%s-client-id-%s" flowName connectionId

/// `ISecretStore` key holding the OAuth client secret for one connection.
let clientSecretKey (flowName: string) (connectionId: string) : string =
    sprintf "%s-client-secret-%s" flowName connectionId

/// `ISecretStore` key the OAuth substrate persists the refresh token
/// under after a successful callback — byte-identical to the
/// substrate's own derivation, which is what makes the two agree.
let refreshTokenKey (flowName: string) (connectionId: string) : string =
    sprintf "%s-refresh-%s" flowName connectionId

/// The refresh descriptor for one connection, pinned to its scope.
/// Registered by the bridge the first time it resolves a token for the
/// connection, so a scheduled job keeps the access token warm from then
/// on.
let descriptor
    (config: GoogleCalendarOAuthConfig)
    (scopeId: string)
    (connectionId: string)
    (clientId: string)
    : OAuthRefreshDescriptor =
    OAuthRefreshDescriptor.withDefaults
        config.FlowName
        connectionId
        scopeId
        config.TokenEndpoint
        clientId
        (clientSecretKey config.FlowName connectionId)
        (refreshTokenKey config.FlowName connectionId)

// ─── Token-endpoint parsing ─────────────────────────────────────────

/// A parsed token-endpoint success body.
type private Grant = {
    AccessToken: string
    RefreshToken: string option
    ExpiresInSeconds: int option
    IdToken: string option
}

let private tryString (name: string) (el: JsonElement) =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

let private tryInt (name: string) (el: JsonElement) =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Number ->
        match v.TryGetInt32() with
        | true, n -> Some n
        | _ -> None
    | _ -> None

/// Google signals failure with an `{ "error": … }` body, whatever the
/// status; `invalid_grant` is kept verbatim in the message because it
/// tells an operator "re-consent" faster than any translation would.
let private parseGrant (body: string) : Result<Grant, OAuthError> =
    try
        use doc = JsonDocument.Parse body
        let root = doc.RootElement

        match tryString "error" root with
        | Some err ->
            let description = tryString "error_description" root |> Option.defaultValue err
            Error(ProviderRejected(sprintf "%s: %s" err description))
        | None ->
            match tryString "access_token" root with
            | Some token ->
                Ok {
                    AccessToken = token
                    RefreshToken = tryString "refresh_token" root
                    ExpiresInSeconds = tryInt "expires_in" root
                    IdToken = tryString "id_token" root
                }
            | None -> Error(OAuthFlowFailed "Google token response carried neither access_token nor error")
    with :? JsonException ->
        Error(OAuthFlowFailed "Google token response was not valid JSON")

let private readClientCredentials
    (secretStore: ISecretStore)
    (flowName: string)
    (scopeId: string)
    (connectionId: string)
    =
    async {
        let! clientId = secretStore.GetSecret(scopeId, clientIdKey flowName connectionId)
        let! clientSecret = secretStore.GetSecret(scopeId, clientSecretKey flowName connectionId)

        match clientId, clientSecret with
        | Some id, Some secret when id <> "" && secret <> "" -> return Ok(id, secret)
        | Some id, _ when id <> "" -> return Error(ClientCredentialMissing(clientSecretKey flowName connectionId))
        | _ -> return Error(ClientCredentialMissing(clientIdKey flowName connectionId))
    }

// ─── The credential flow ────────────────────────────────────────────

/// Build the Google Calendar `IOAuthCredentialFlow`. Register it with
/// `ServerApp.withOAuthFlow`; the substrate's authorize / callback
/// routes then drive it for any connection whose credential form names
/// `config.FlowName`.
///
/// `post` is the token-endpoint transport — `ProviderOAuthFlow.httpPost`
/// over an egress-policy-wrapped client in production. `refresher` is
/// used for exactly one thing: `Revoke` unregisters the connection's
/// refresh descriptor before the substrate deletes its refresh token, so
/// no scheduled refresh keeps firing at a key that holds nothing.
let create
    (post: OAuthTokenPost)
    (secretStore: ISecretStore)
    (refresher: IOAuthTokenRefresher option)
    (config: GoogleCalendarOAuthConfig)
    : IOAuthCredentialFlow =
    let flowName = config.FlowName

    { new IOAuthCredentialFlow with
        member _.Name = flowName

        member _.Descriptor = {
            DisplayName = config.DisplayName
            Scopes = config.Scopes
            HelpUrl = config.HelpUrl
        }

        // Google accepts PKCE on the web-server flow: defence in depth
        // over the client secret, so an intercepted code is useless
        // without the verifier the substrate stashed.
        member _.SupportsPkce = true

        member _.BuildAuthorizeUrl(ctx, state, redirectUri, pkce) = async {
            match! secretStore.GetSecret(ctx.ScopeId, clientIdKey flowName ctx.DataSourceId) with
            | None
            | Some "" -> return Error(ClientCredentialMissing(clientIdKey flowName ctx.DataSourceId))
            | Some clientId ->
                let pkceParams =
                    match pkce with
                    | Some challenge -> [
                        "code_challenge", challenge.Challenge
                        "code_challenge_method", challenge.Method
                      ]
                    | None -> []

                let query =
                    [
                        "client_id", clientId
                        "redirect_uri", redirectUri
                        "response_type", "code"
                        "scope", String.concat " " config.Scopes
                        "state", state
                        "access_type", "offline"
                        "prompt", "consent"
                    ]
                    @ pkceParams
                    |> List.map (fun (k, v) -> sprintf "%s=%s" (Uri.EscapeDataString k) (Uri.EscapeDataString v))
                    |> String.concat "&"

                return Ok(sprintf "%s?%s" config.AuthorizeEndpoint query)
        }

        member _.ExchangeCode(ctx, code, redirectUri, codeVerifier) = async {
            match! readClientCredentials secretStore flowName ctx.ScopeId ctx.DataSourceId with
            | Error e -> return Error e
            | Ok(clientId, clientSecret) ->
                let verifier =
                    match codeVerifier with
                    | Some v -> [ "code_verifier", v ]
                    | None -> []

                let! response =
                    post
                        config.TokenEndpoint
                        ([
                            "client_id", clientId
                            "client_secret", clientSecret
                            "code", code
                            "redirect_uri", redirectUri
                            "grant_type", "authorization_code"
                         ]
                         @ verifier)

                match response |> Result.bind parseGrant with
                | Error e -> return Error e
                | Ok grant ->
                    match grant.RefreshToken with
                    | None ->
                        return
                            Error(
                                OAuthFlowFailed
                                    "Google returned no refresh_token — the authorize request must carry access_type=offline and prompt=consent"
                            )
                    | Some refreshToken ->
                        let expiresIn = grant.ExpiresInSeconds |> Option.defaultValue DefaultExpirySeconds

                        return
                            Ok {
                                RefreshToken = refreshToken
                                AccessToken = Some grant.AccessToken
                                ExpiresAt = Some(DateTime.UtcNow.AddSeconds(float expiresIn))
                                IdToken = grant.IdToken
                            }
        }

        member _.RefreshAccessToken(ctx, refreshToken) = async {
            match! readClientCredentials secretStore flowName ctx.ScopeId ctx.DataSourceId with
            | Error e -> return Error e
            | Ok(clientId, clientSecret) ->
                let! response =
                    post config.TokenEndpoint [
                        "client_id", clientId
                        "client_secret", clientSecret
                        "refresh_token", refreshToken
                        "grant_type", "refresh_token"
                    ]

                match response |> Result.bind parseGrant with
                | Error e -> return Error e
                | Ok grant ->
                    // Google does not rotate refresh tokens today; if a
                    // response ever carries a changed one, persist it so
                    // the rotation cannot surface later as invalid_grant.
                    match grant.RefreshToken with
                    | Some rotated when rotated <> refreshToken ->
                        let! _ = secretStore.SetSecret(ctx.ScopeId, refreshTokenKey flowName ctx.DataSourceId, rotated)

                        ()
                    | _ -> ()

                    let expiresIn = grant.ExpiresInSeconds |> Option.defaultValue DefaultExpirySeconds

                    return
                        Ok {
                            Token = grant.AccessToken
                            ExpiresAt = DateTime.UtcNow.AddSeconds(float expiresIn)
                        }
        }

        member _.Revoke(ctx, refreshToken) = async {
            // Unconditional and ahead of the network call: an outage at
            // Google must not leave a live descriptor behind for a
            // connection the operator has disconnected.
            match refresher with
            | Some r -> do! r.UnregisterDescriptor(flowName, ctx.DataSourceId)
            | None -> ()

            match! post config.RevokeEndpoint [ "token", refreshToken ] with
            | Error e -> return Error e
            | Ok body ->
                // Google answers `invalid_token` for a token it already
                // invalidated — the end state the caller wanted.
                if String.IsNullOrWhiteSpace body || not (body.Contains "\"error\"") then
                    return Ok()
                elif body.Contains "invalid_token" then
                    return Ok()
                else
                    return Error(ProviderRejected(sprintf "revocation refused: %s" body))
        }
    }

// ─── Per-call bearer tokens for the bridge ──────────────────────────

/// Map a substrate failure onto the bridge's closed error vocabulary.
/// A transport failure is retryable; every other failure needs an
/// operator (a missing client credential, a refused grant).
let toBridgeError (error: OAuthError) : BridgeError =
    match error with
    | NetworkError message -> Unreachable message
    | other -> AuthenticationFailed(OAuthError.toMessage other)

/// How much life a cached access token must have left to be used rather
/// than refreshed — a token that expires mid-request is a 401 nobody
/// wanted.
let AccessTokenSafetyMargin: TimeSpan = TimeSpan.FromMinutes 1.0

/// Resolves a bearer token for one connection, per call, never caching
/// anything in memory (portability rule 4).
///
/// With `refresher = Some _` the refresh substrate owns the token: the
/// cached access token under the descriptor's derived key is used while
/// it has `AccessTokenSafetyMargin` left, and `RefreshNow` is asked for a
/// new one otherwise — registering the connection's descriptor first
/// when the substrate does not yet know it. With `refresher = None` a
/// token is minted from the stored refresh token on every call, exactly
/// as the Google Analytics connector does without the substrate (GP 13).
type GoogleCalendarTokenSource
    (
        post: OAuthTokenPost,
        secretStore: ISecretStore,
        refresher: IOAuthTokenRefresher option,
        config: GoogleCalendarOAuthConfig,
        clock: unit -> DateTimeOffset
    ) =

    let flowName = config.FlowName

    let mint (scopeId: string) (connectionId: string) = async {
        match! readClientCredentials secretStore flowName scopeId connectionId with
        | Error e -> return Error(toBridgeError e)
        | Ok(clientId, clientSecret) ->
            match! secretStore.GetSecret(scopeId, refreshTokenKey flowName connectionId) with
            | None
            | Some "" ->
                return
                    Error(
                        AuthenticationFailed(
                            sprintf
                                "no '%s' refresh token in scope '%s' — connect the Google Calendar credential first"
                                (refreshTokenKey flowName connectionId)
                                scopeId
                        )
                    )
            | Some refreshToken ->
                let! response =
                    post config.TokenEndpoint [
                        "client_id", clientId
                        "client_secret", clientSecret
                        "refresh_token", refreshToken
                        "grant_type", "refresh_token"
                    ]

                match response |> Result.bind parseGrant with
                | Error e -> return Error(toBridgeError e)
                | Ok grant -> return Ok grant.AccessToken
    }

    let readCached (scopeId: string) (connectionId: string) = async {
        let key = refreshTokenKey flowName connectionId
        let! token = secretStore.GetSecret(scopeId, key + ".access")
        let! expiry = secretStore.GetSecret(scopeId, key + ".expires-at")

        match token, expiry with
        | Some t, Some e when t <> "" ->
            match DateTimeOffset.TryParse(e, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
            | true, expiresAt when expiresAt > clock () + AccessTokenSafetyMargin -> return Some t
            | _ -> return None
        | _ -> return None
    }

    let ensureDescriptor (r: IOAuthTokenRefresher) (scopeId: string) (connectionId: string) = async {
        match! r.GetDescriptor(flowName, connectionId) with
        | Some _ -> return Ok()
        | None ->
            match! readClientCredentials secretStore flowName scopeId connectionId with
            | Error e -> return Error(toBridgeError e)
            | Ok(clientId, _) ->
                do! r.RegisterDescriptor(descriptor config scopeId connectionId clientId)
                return Ok()
    }

    let refreshThroughSubstrate (r: IOAuthTokenRefresher) (scopeId: string) (connectionId: string) = async {
        match! ensureDescriptor r scopeId connectionId with
        | Error e -> return Error e
        | Ok() ->
            match! r.RefreshNow(flowName, connectionId) with
            | Refreshed _ ->
                let key = refreshTokenKey flowName connectionId

                match! secretStore.GetSecret(scopeId, key + ".access") with
                | Some token when token <> "" -> return Ok token
                | _ ->
                    return
                        Error(
                            AuthenticationFailed(
                                sprintf
                                    "the refresh substrate reported success but left no access token under '%s.access'"
                                    key
                            )
                        )
            | TokenInvalidatedByProvider ->
                return
                    Error(
                        AuthenticationFailed
                            "Google invalidated the refresh token — reconnect the Google Calendar credential"
                    )
            | TransientError reason -> return Error(Unreachable reason)
            | PermanentError reason -> return Error(AuthenticationFailed reason)
    }

    /// The production constructor — the wall clock.
    new(post: OAuthTokenPost, secretStore: ISecretStore, refresher: IOAuthTokenRefresher option, config) =
        GoogleCalendarTokenSource(post, secretStore, refresher, config, (fun () -> DateTimeOffset.UtcNow))

    /// The OAuth configuration this source resolves tokens under.
    member _.Config: GoogleCalendarOAuthConfig = config

    /// A bearer token for `connectionId` in `scopeId` — the cached one
    /// while it is fresh, a refreshed one otherwise.
    member _.AccessToken(scopeId: string, connectionId: string) : Async<Result<string, BridgeError>> = async {
        match refresher with
        | None -> return! mint scopeId connectionId
        | Some r ->
            match! readCached scopeId connectionId with
            | Some token -> return Ok token
            | None -> return! refreshThroughSubstrate r scopeId connectionId
    }

    /// A NEW bearer token, bypassing the cache — called once after Google
    /// refused the token `AccessToken` returned (a revoked or rotated
    /// grant the cache has not caught up with).
    member _.RefreshedAccessToken(scopeId: string, connectionId: string) : Async<Result<string, BridgeError>> = async {
        match refresher with
        | None -> return! mint scopeId connectionId
        | Some r -> return! refreshThroughSubstrate r scopeId connectionId
    }