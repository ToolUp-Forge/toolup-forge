// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 831 - the Microsoft Graph calendar bridge's settings, its
/// delegated OAuth flow, and the per-call access-token resolution.
module ToolUp.Calendar.MicrosoftGraphOAuth

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Scheduling.ICalendarBridge

// ─── Phase 831 — Microsoft Graph delegated OAuth ────────────────────
//
// The credential half of the Microsoft Graph calendar bridge. Nothing
// here is bespoke plumbing: the sign-in round-trip is the Phase 10e
// authorization-code substrate (`/api/oauth/{flow}/authorize` and
// `/callback`, PKCE-aware), and refresh is the Phase 10h
// `IOAuthTokenRefresher`. This file contributes the three things those
// substrates cannot know:
//
//   * the Microsoft identity platform's v2.0 endpoints and scopes —
//     `IOAuthCredentialFlow` below;
//   * which connection a calendar link authorises through —
//     `connectionId`, the link's user under a fixed prefix;
//   * how a bridge call turns that connection into a bearer token —
//     `accessToken`, which reads the refresher's cached token from
//     `ISecretStore` PER CALL and asks the refresher for a new one when
//     it is missing or about to expire.
//
// **How a user connects.** A deployment adds a data source of Kind
// `MicrosoftGraphCalendar` whose id is `connectionId userId`. The
// built-in data-ingestion admin derives the flow name from the Kind
// (`MicrosoftGraphCalendar` ↔ `microsoft-graph-calendar`), so its
// generic Connect / Disconnect buttons drive this flow with no
// companion UI; the substrate persists the refresh token under
// `{flow}-refresh-{connectionId}` in the caller's scope — the key this
// file reads back.
//
// **The client secret is a secret, the client id is not.** The Entra
// application id is configuration (`TOOLUP_MSGRAPH_CALENDAR_CLIENT_ID`,
// public by the OAuth spec); the client secret is read from
// `ISecretStore` under `SecretKeys.ClientSecret` in the link's own scope
// per call, so rotating it needs no restart. That is also the key the
// refresh descriptor names, so the scheduled refresh and the sign-in
// exchange authenticate identically.
//
// **Refresh tokens rotate.** The Microsoft identity platform returns a
// new refresh token on every refresh. The refresher persists the
// rotated token before it caches the access token; `RefreshAccessToken`
// below does the same on the substrate's own path, so a rotation is
// picked up by the next call whichever path minted it.
//
// **Revocation.** The v2.0 endpoint has no per-token revocation
// endpoint (revoking a user's sessions is a directory operation, not
// the app's to make), so `Revoke` answers `RevocationUnsupported` after
// unregistering the refresh descriptor; the substrate then deletes the
// local refresh token, which is what disconnecting means here.
//
// GP 1 — no Microsoft identity or Graph SDK on this path: BCL
// `HttpClient` and `System.Text.Json` only.

/// Connection settings for the Microsoft Graph calendar bridge. Carries
/// no secret — the client secret and the webhook client-state secret
/// come from `ISecretStore` — so the record is safe to log.
type MicrosoftGraphCalendarSettings = {
    /// Entra application (client) id. Public by the OAuth spec.
    ClientId: string
    /// Tenant segment of the sign-in authority: `common` (work, school
    /// and personal accounts), `organizations`, `consumers`, a tenant
    /// id, or a verified domain.
    Tenant: string
    /// Sign-in base, e.g. `https://login.microsoftonline.com`. National
    /// clouds use their own (`https://login.microsoftonline.us`).
    LoginEndpoint: string
    /// Graph base, e.g. `https://graph.microsoft.com`. National clouds
    /// use their own (`https://graph.microsoft.us`).
    GraphEndpoint: string
    /// When set, replaces BOTH `LoginEndpoint` and `GraphEndpoint` for
    /// every request — the convention the other HTTP companions use for
    /// a test double standing in for the provider. Scopes keep naming
    /// the real Graph resource.
    EndpointOverride: string option
    /// Public HTTPS URL Graph delivers change notifications to — where
    /// the deployment mounts `MicrosoftGraphSubscriptions.handler`.
    /// `None` — the bridge
    /// declares itself polling-only and opens no subscriptions.
    NotificationUrl: string option
    /// How long a subscription is requested for. Graph caps calendar
    /// subscriptions at just under three days; two leaves the renewal
    /// job a full day of slack. Default two days.
    SubscriptionLifetime: TimeSpan
    /// How far forward a pull's window reaches. Default 90 days.
    PullWindowDays: int
    /// How far BACK a pull's window reaches when the caller supplies no
    /// `since`. Default one day.
    PullLookbackDays: int
    /// How long a stored delta cursor is followed before the bridge
    /// starts a fresh delta round over a re-anchored window. Bounds how
    /// long a change the sync engine failed to apply stays unobserved,
    /// and keeps the window moving forward. Default one day.
    FullResyncInterval: TimeSpan
}

/// `ISecretStore` key names the bridge reads, all in the link's own
/// scope. Not `TOOLUP_*` variables: they are secret names, and
/// `EnvironmentSecretStore` maps them to the scoped environment
/// spelling itself.
module SecretKeys =
    /// The Entra application's client secret.
    [<Literal>]
    let ClientSecret = "MSGRAPH_CALENDAR_CLIENT_SECRET"

    /// The key the per-subscription `clientState` values are derived
    /// from (HMAC-SHA256). Required only when `NotificationUrl` is set.
    [<Literal>]
    let WebhookSecret = "MSGRAPH_CALENDAR_WEBHOOK_SECRET"

/// Defaults, the environment reader and endpoint resolution for
/// `MicrosoftGraphCalendarSettings`.
module MicrosoftGraphCalendarSettings =
    /// Settings with no application configured — the shape a
    /// deployment overrides field by field.
    let defaults: MicrosoftGraphCalendarSettings = {
        ClientId = ""
        Tenant = "common"
        LoginEndpoint = "https://login.microsoftonline.com"
        GraphEndpoint = "https://graph.microsoft.com"
        EndpointOverride = None
        NotificationUrl = None
        SubscriptionLifetime = TimeSpan.FromDays 2.0
        PullWindowDays = 90
        PullLookbackDays = 1
        FullResyncInterval = TimeSpan.FromDays 1.0
    }

    /// Read settings from the environment:
    ///   TOOLUP_MSGRAPH_CALENDAR_CLIENT_ID        — required
    ///   TOOLUP_MSGRAPH_CALENDAR_TENANT           — optional, default `common`
    ///   TOOLUP_MSGRAPH_CALENDAR_NOTIFICATION_URL — optional; unset = polling only
    ///   TOOLUP_MSGRAPH_CALENDAR_ENDPOINT         — optional override
    /// The window, lifetime and national-cloud knobs are record fields
    /// only: a deployment that needs to change them is already
    /// constructing the record.
    let fromEnv () : MicrosoftGraphCalendarSettings =
        let read name =
            match Environment.GetEnvironmentVariable(name: string) with
            | null
            | "" -> None
            | v -> Some v

        let clientId =
            match read ConfigKeys.Names.msGraphCalendarClientId with
            | Some v -> v
            | None ->
                failwithf
                    "Microsoft Graph calendar bridge: env var %s is required"
                    ConfigKeys.Names.msGraphCalendarClientId

        {
            defaults with
                ClientId = clientId
                Tenant =
                    read ConfigKeys.Names.msGraphCalendarTenant
                    |> Option.defaultValue defaults.Tenant
                NotificationUrl = read ConfigKeys.Names.msGraphCalendarNotificationUrl
                EndpointOverride = read ConfigKeys.Names.msGraphCalendarEndpoint
        }

    /// The Graph base every API request is issued against, without a
    /// trailing slash.
    let graphBase (settings: MicrosoftGraphCalendarSettings) : string =
        (settings.EndpointOverride |> Option.defaultValue settings.GraphEndpoint).TrimEnd '/'

    /// The sign-in base, without a trailing slash.
    let loginBase (settings: MicrosoftGraphCalendarSettings) : string =
        (settings.EndpointOverride |> Option.defaultValue settings.LoginEndpoint).TrimEnd '/'

    /// The v2.0 authorization endpoint for the configured tenant.
    let authorizeEndpoint (settings: MicrosoftGraphCalendarSettings) : string =
        sprintf "%s/%s/oauth2/v2.0/authorize" (loginBase settings) settings.Tenant

    /// The v2.0 token endpoint for the configured tenant — the one the
    /// sign-in exchange and the refresh descriptor both name.
    let tokenEndpoint (settings: MicrosoftGraphCalendarSettings) : string =
        sprintf "%s/%s/oauth2/v2.0/token" (loginBase settings) settings.Tenant

    /// The delegated scopes requested at consent. `offline_access` is
    /// what makes the platform issue a refresh token at all; the Graph
    /// permission is named by its full resource URI so a national-cloud
    /// deployment consents against its own Graph.
    let scopes (settings: MicrosoftGraphCalendarSettings) : string list = [
        "offline_access"
        sprintf "%s/Calendars.ReadWrite" (settings.GraphEndpoint.TrimEnd '/')
    ]

/// The OAuth flow name — the `{flowName}` URL segment of the Phase 10e
/// substrate, the refresh-token key prefix, and the refresh
/// descriptor's `Provider`. A wire value: renaming it strands every
/// stored refresh token.
[<Literal>]
let FlowName = "microsoft-graph-calendar"

/// The data-source Kind a user's connection is created under. The
/// data-ingestion admin kebab-cases it to `FlowName`, which is what
/// wires its generic Connect button to this flow.
[<Literal>]
let DataSourceKind = "MicrosoftGraphCalendar"

/// The data-source id — the OAuth correlation id — a user's calendar
/// connection lives under: the link's `UserId` behind a fixed prefix,
/// so the same user can hold a connection per provider in one scope
/// without the ids colliding.
let connectionId (userId: string) : string = "msgraph-calendar-" + userId

/// The `ISecretStore` key the Phase 10e substrate persists the refresh
/// token under for one connection (`{flow}-refresh-{id}`).
let refreshTokenKey (connection: string) : string =
    sprintf "%s-refresh-%s" FlowName connection

/// The Phase 10h refresh descriptor for one connection in one scope.
let refreshDescriptor
    (settings: MicrosoftGraphCalendarSettings)
    (scopeId: string)
    (connection: string)
    : OAuthRefreshDescriptor =
    OAuthRefreshDescriptor.withDefaults
        FlowName
        connection
        scopeId
        (MicrosoftGraphCalendarSettings.tokenEndpoint settings)
        settings.ClientId
        SecretKeys.ClientSecret
        (refreshTokenKey connection)

// ─── Token endpoint I/O ─────────────────────────────────────────────

type private TokenGrant = {
    AccessToken: string
    RefreshToken: string option
    ExpiresInSeconds: int option
    IdToken: string option
}

let private tryString (name: string) (el: JsonElement) : string option =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

let private tryInt (name: string) (el: JsonElement) : int option =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Number ->
        match v.TryGetInt32() with
        | true, n -> Some n
        | _ -> None
    | _ -> None

/// Fallback access-token lifetime when the response omits `expires_in`.
[<Literal>]
let private DefaultExpirySeconds = 3600

/// POST a form to the v2.0 token endpoint. The platform signals failure
/// with a 4xx AND an `{ "error": … }` body, so the body is read whatever
/// the status: `invalid_grant` must reach the substrate as
/// `ProviderRejected`, not as a transport failure.
let private postToken
    (httpClient: HttpClient)
    (tokenUrl: string)
    (fields: (string * string) list)
    : Async<Result<TokenGrant, OAuthError>> =
    async {
        try
            use content = new FormUrlEncodedContent(dict fields)
            use request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
            request.Content <- content
            request.Headers.TryAddWithoutValidation("Accept", "application/json") |> ignore

            let! response = httpClient.SendAsync request |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

            try
                use doc = JsonDocument.Parse body
                let root = doc.RootElement

                match tryString "error" root with
                | Some code ->
                    let description = tryString "error_description" root |> Option.defaultValue code
                    return Error(ProviderRejected(sprintf "%s: %s" code description))
                | None ->
                    match tryString "access_token" root with
                    | Some access ->
                        return
                            Ok {
                                AccessToken = access
                                RefreshToken = tryString "refresh_token" root
                                ExpiresInSeconds = tryInt "expires_in" root
                                IdToken = tryString "id_token" root
                            }
                    | None ->
                        return Error(OAuthFlowFailed "Microsoft token response carried neither access_token nor error")
            with :? JsonException ->
                return Error(OAuthFlowFailed "Microsoft token response was not valid JSON")
        with
        | :? HttpRequestException as ex -> return Error(NetworkError ex.Message)
        | :? TaskCanceledException -> return Error(NetworkError "request to the Microsoft token endpoint timed out")
    }

let private readClientSecret (secretStore: ISecretStore) (scopeId: string) = async {
    match! secretStore.GetSecret(scopeId, SecretKeys.ClientSecret) with
    | None
    | Some "" -> return Error(ClientCredentialMissing SecretKeys.ClientSecret)
    | Some secret -> return Ok secret
}

// ─── The flow ───────────────────────────────────────────────────────

/// Build the Microsoft Graph calendar `IOAuthCredentialFlow`, for
/// registration beside the Phase 10e substrate.
///
/// `refresher` is the Phase 10h substrate when the deployment composed
/// one; it is used by `Revoke` only, to unregister the connection's
/// descriptor before the substrate deletes its refresh token, so a
/// disconnected connection leaves no scheduled refresh behind.
let createFlow
    (httpClient: HttpClient)
    (secretStore: ISecretStore)
    (refresher: IOAuthTokenRefresher option)
    (settings: MicrosoftGraphCalendarSettings)
    : IOAuthCredentialFlow =
    let scope = MicrosoftGraphCalendarSettings.scopes settings |> String.concat " "
    let tokenUrl = MicrosoftGraphCalendarSettings.tokenEndpoint settings

    let clientIdOrMissing () =
        if String.IsNullOrWhiteSpace settings.ClientId then
            Error(ClientCredentialMissing ConfigKeys.Names.msGraphCalendarClientId)
        else
            Ok settings.ClientId

    { new IOAuthCredentialFlow with
        member _.Name = FlowName

        member _.Descriptor = {
            DisplayName = "Microsoft 365 calendar"
            Scopes = MicrosoftGraphCalendarSettings.scopes settings
            HelpUrl = Some "https://learn.microsoft.com/entra/identity-platform/quickstart-register-app"
        }

        // The v2.0 endpoint accepts PKCE on the web-server flow. It is
        // defence in depth over the client secret: an intercepted code
        // is useless without the verifier the substrate stashed.
        member _.SupportsPkce = true

        member _.BuildAuthorizeUrl(_ctx, state, redirectUri, pkce) = async {
            match clientIdOrMissing () with
            | Error e -> return Error e
            | Ok clientId ->
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
                        "response_type", "code"
                        "redirect_uri", redirectUri
                        "response_mode", "query"
                        "scope", scope
                        "state", state
                    ]
                    @ pkceParams
                    |> List.map (fun (k, v) -> sprintf "%s=%s" k (Uri.EscapeDataString v))
                    |> String.concat "&"

                return Ok(sprintf "%s?%s" (MicrosoftGraphCalendarSettings.authorizeEndpoint settings) query)
        }

        member _.ExchangeCode(ctx, code, redirectUri, codeVerifier) = async {
            match clientIdOrMissing () with
            | Error e -> return Error e
            | Ok clientId ->
                match! readClientSecret secretStore ctx.ScopeId with
                | Error e -> return Error e
                | Ok clientSecret ->
                    let verifier =
                        match codeVerifier with
                        | Some v -> [ "code_verifier", v ]
                        | None -> []

                    let! grant =
                        postToken
                            httpClient
                            tokenUrl
                            ([
                                "client_id", clientId
                                "client_secret", clientSecret
                                "code", code
                                // Compared for equality with the authorize
                                // step's value, byte for byte.
                                "redirect_uri", redirectUri
                                "grant_type", "authorization_code"
                                "scope", scope
                             ]
                             @ verifier)

                    match grant with
                    | Error e -> return Error e
                    | Ok g ->
                        match g.RefreshToken with
                        | None ->
                            return
                                Error(
                                    OAuthFlowFailed
                                        "Microsoft returned no refresh_token — the requested scopes must include offline_access"
                                )
                        | Some refreshToken ->
                            let expiresIn = g.ExpiresInSeconds |> Option.defaultValue DefaultExpirySeconds

                            return
                                Ok {
                                    RefreshToken = refreshToken
                                    AccessToken = Some g.AccessToken
                                    ExpiresAt = Some(DateTime.UtcNow.AddSeconds(float expiresIn))
                                    IdToken = g.IdToken
                                }
        }

        member _.RefreshAccessToken(ctx, refreshToken) = async {
            match clientIdOrMissing () with
            | Error e -> return Error e
            | Ok clientId ->
                match! readClientSecret secretStore ctx.ScopeId with
                | Error e -> return Error e
                | Ok clientSecret ->
                    let! grant =
                        postToken httpClient tokenUrl [
                            "client_id", clientId
                            "client_secret", clientSecret
                            "refresh_token", refreshToken
                            "grant_type", "refresh_token"
                            "scope", scope
                        ]

                    match grant with
                    | Error e -> return Error e
                    | Ok g ->
                        // The platform rotates on every refresh. Persist
                        // the new token under the substrate's own key
                        // before handing back the access token — an
                        // unpersisted rotation reads as `invalid_grant`
                        // on the next refresh.
                        match g.RefreshToken with
                        | Some rotated when rotated <> refreshToken ->
                            let! _ = secretStore.SetSecret(ctx.ScopeId, refreshTokenKey ctx.DataSourceId, rotated)

                            ()
                        | _ -> ()

                        let expiresIn = g.ExpiresInSeconds |> Option.defaultValue DefaultExpirySeconds

                        return
                            Ok {
                                Token = g.AccessToken
                                ExpiresAt = DateTime.UtcNow.AddSeconds(float expiresIn)
                            }
        }

        member _.Revoke(ctx, _refreshToken) = async {
            // Unregister first and unconditionally: the substrate deletes
            // the refresh token after this returns, and a descriptor left
            // behind would keep firing against a key that holds nothing.
            match refresher with
            | Some r -> do! r.UnregisterDescriptor(FlowName, ctx.DataSourceId)
            | None -> ()

            return Error RevocationUnsupported
        }
    }

// ─── Per-call access-token resolution ───────────────────────────────

/// How close to expiry a cached access token is still used. Inside this
/// margin the bridge asks the refresher for a new one rather than
/// starting a Graph call that could outlive its token.
let private ExpiryMargin = TimeSpan.FromMinutes 2.0

/// Make sure the refresher knows the connection. Idempotent — the
/// in-process refresher re-schedules under a stable idempotency key —
/// and cheap, so every link-level call may make it: a restarted process
/// whose refresher has not recovered the descriptor yet gets it back on
/// the first bridge call rather than failing it.
let ensureRegistered
    (refresher: IOAuthTokenRefresher)
    (settings: MicrosoftGraphCalendarSettings)
    (scopeId: string)
    (userId: string)
    : Async<unit> =
    async {
        let connection = connectionId userId

        match! refresher.GetDescriptor(FlowName, connection) with
        | Some _ -> ()
        | None -> do! refresher.RegisterDescriptor(refreshDescriptor settings scopeId connection)
    }

let private refreshFailure (result: OAuthRefreshResult) : BridgeError =
    match result with
    | TokenInvalidatedByProvider ->
        AuthenticationFailed "Microsoft refused the stored refresh token — the user must reconnect the calendar"
    | TransientError reason -> Unreachable(sprintf "Microsoft token refresh failed transiently: %s" reason)
    | PermanentError reason -> AuthenticationFailed(sprintf "Microsoft token refresh failed: %s" reason)
    | Refreshed _ -> Unreachable "token refresh reported success but no access token was cached"

/// Ask the refresher for a new access token now and read it back. Used
/// when no usable cached token exists, and once after Graph answers
/// `401` to a token the cache still considered valid.
let forceRefresh
    (secretStore: ISecretStore)
    (refresher: IOAuthTokenRefresher)
    (settings: MicrosoftGraphCalendarSettings)
    (scopeId: string)
    (userId: string)
    : Async<Result<string, BridgeError>> =
    async {
        let connection = connectionId userId
        let descriptor = refreshDescriptor settings scopeId connection
        do! ensureRegistered refresher settings scopeId userId

        match! refresher.RefreshNow(FlowName, connection) with
        | Refreshed _ ->
            match! secretStore.GetSecret(scopeId, OAuthRefreshDescriptor.accessTokenKey descriptor) with
            | Some token when token <> "" -> return Ok token
            | _ -> return Error(refreshFailure (Refreshed DateTimeOffset.MinValue))
        | other -> return Error(refreshFailure other)
    }

/// The bearer token for one user's connection in one scope, resolved
/// per call (portability rule 4). Reads the refresher's cached access
/// token and its expiry from `ISecretStore`; a missing, unparseable or
/// nearly-expired one is refreshed through the refresher first. A
/// connection with no refresh token at all was never connected, which
/// is `AuthenticationFailed` naming the data source to connect.
let accessToken
    (secretStore: ISecretStore)
    (refresher: IOAuthTokenRefresher)
    (settings: MicrosoftGraphCalendarSettings)
    (now: unit -> DateTimeOffset)
    (scopeId: string)
    (userId: string)
    : Async<Result<string, BridgeError>> =
    async {
        let connection = connectionId userId
        let descriptor = refreshDescriptor settings scopeId connection

        match! secretStore.GetSecret(scopeId, refreshTokenKey connection) with
        | None
        | Some "" ->
            return
                Error(
                    AuthenticationFailed(
                        sprintf
                            "no Microsoft calendar connection '%s' in scope '%s' — connect a %s data source with that id"
                            connection
                            scopeId
                            DataSourceKind
                    )
                )
        | Some _ ->
            do! ensureRegistered refresher settings scopeId userId
            let! cached = secretStore.GetSecret(scopeId, OAuthRefreshDescriptor.accessTokenKey descriptor)
            let! expiry = secretStore.GetSecret(scopeId, OAuthRefreshDescriptor.accessExpiryKey descriptor)

            let usable =
                match cached, expiry with
                | Some token, Some stamp when token <> "" ->
                    match DateTimeOffset.TryParse stamp with
                    | true, expiresAt when expiresAt - ExpiryMargin > now () -> Some token
                    | _ -> None
                | _ -> None

            match usable with
            | Some token -> return Ok token
            | None -> return! forceRefresh secretStore refresher settings scopeId userId
    }