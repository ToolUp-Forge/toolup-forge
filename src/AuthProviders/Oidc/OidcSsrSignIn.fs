// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 987 — interactive sign-in for server-rendered pages.
///
/// A browser that opens a link to an audience-gated SSR page (from email,
/// chat, a bookmark) arrives with no credential: the SPA's sign-in flow
/// never ran, and the SPA's session cookie is `SameSite=Strict`, so a
/// cross-site navigation would not carry it anyway. This module is the
/// server-side half that turns that `401` into a sign-in:
///
///   1. `GET <SignInPath>?returnUrl=/reports/q3` — mints `state`, a nonce
///      and a PKCE verifier, seals them (with the return URL) into a
///      short-lived DataProtection cookie, and redirects to the IdP's
///      authorization endpoint (authorization-code flow, `S256` PKCE).
///   2. `GET <CallbackPath>?code=…&state=…` — checks `state` against the
///      sealed cookie, exchanges the code at the token endpoint, validates
///      the resulting token through the registered `IAuthProvider` (so the
///      token is held to exactly the rules every later request applies,
///      claim mapping and admission gate included), checks the nonce, sets
///      the session cookie the provider reads — `HttpOnly`,
///      `SameSite=Lax`, `Secure` on HTTPS — and redirects to the return
///      URL.
///
/// `register` also puts an `InteractiveSignIn` in DI, which is what makes
/// the page handler redirect a credential-less browser here instead of
/// answering `401`.
///
/// **Why `SameSite=Lax`.** The session must survive the top-level
/// navigation that brings a reader in from another site — that is the whole
/// point — and `Strict` cookies are withheld from exactly that navigation.
/// `Lax` still withholds the cookie from cross-site sub-requests and
/// cross-site `POST`s; mutating `/api` calls stay behind the CSRF
/// middleware as before.
///
/// **Fail-closed paths.** No DataProtection → the sign-in route refuses
/// (500) rather than issuing an unsealed state cookie. A missing, expired
/// or mismatched state, an IdP `error`, a failed exchange, a token the
/// provider refuses, or a nonce mismatch all end in an error page and set
/// no session cookie. A return URL that is not a local path is replaced by
/// `/` (no open redirect).
module ToolUp.AuthProviders.OidcSsrSignIn

open System
open System.Collections.Generic
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Giraffe
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth

/// Which token from the code exchange becomes the session credential. It
/// must be one the registered `IAuthProvider` validates: an `IdToken` is
/// addressed to the client (`aud` = client id), an `AccessToken` to the
/// API audience the provider is configured for.
[<RequireQualifiedAccess>]
type SsrSessionToken =
    | IdToken
    | AccessToken

/// Configuration of the server-side sign-in. Build it with
/// `OidcSsrSignIn.defaults` and a copy-and-update expression.
type OidcSsrSignInConfig = {
    /// The IdP's authorization endpoint (`authorization_endpoint` in its
    /// discovery document).
    AuthorizationEndpoint: string
    /// The IdP's token endpoint (`token_endpoint`).
    TokenEndpoint: string
    /// The OIDC client (application) id.
    ClientId: string
    /// The client secret, for a confidential client (`client_secret_post`).
    /// `None` for a public client, which PKCE alone protects.
    ClientSecret: string option
    /// The absolute callback URL registered at the IdP — the public URL of
    /// `CallbackPath` on this deployment.
    RedirectUri: string
    /// Scopes requested. `openid` is required for an id_token.
    Scopes: string list
    /// Which token becomes the session credential.
    SessionToken: SsrSessionToken
    /// Local route that starts the sign-in (the `InteractiveSignIn` path).
    SignInPath: string
    /// Local route the IdP redirects back to.
    CallbackPath: string
    /// Name of the session cookie — the one the provider's `TokenLocation`
    /// reads (`BearerOrCookie <name>` / `Cookie <name>`). Defaults to the
    /// SDK's `toolup-auth-token`.
    CookieName: string
    /// How long a started sign-in may take before its state expires.
    StateLifetime: TimeSpan
}

/// Name of the short-lived cookie carrying the sealed sign-in state.
[<Literal>]
let StateCookieName = "toolup-ssr-signin"

[<Literal>]
let private ProtectorPurpose = "ToolUp.AuthProviders.OidcSsrSignIn.v1"

/// Defaults: `openid profile email`, the id_token as the session
/// credential, `/auth/sign-in` and `/auth/callback`, the SDK session cookie
/// name, a ten-minute state lifetime, and a public client.
let defaults
    (authorizationEndpoint: string)
    (tokenEndpoint: string)
    (clientId: string)
    (redirectUri: string)
    : OidcSsrSignInConfig =
    {
        AuthorizationEndpoint = authorizationEndpoint
        TokenEndpoint = tokenEndpoint
        ClientId = clientId
        ClientSecret = None
        RedirectUri = redirectUri
        Scopes = [ "openid"; "profile"; "email" ]
        SessionToken = SsrSessionToken.IdToken
        SignInPath = "/auth/sign-in"
        CallbackPath = "/auth/callback"
        CookieName = AuthSession.CookieName
        StateLifetime = TimeSpan.FromMinutes 10.0
    }

/// The `InteractiveSignIn` the page handler reads for this configuration.
let interactiveSignIn (config: OidcSsrSignInConfig) : InteractiveSignIn = { SignInPath = config.SignInPath }

/// Register the `InteractiveSignIn` this configuration implies, so gated
/// pages redirect a credential-less browser to `SignInPath`. Mount
/// `routes` as well; one without the other is either a dead redirect or an
/// unused route.
let register (services: IServiceCollection) (config: OidcSsrSignInConfig) : IServiceCollection =
    services.AddSingleton<InteractiveSignIn>(interactiveSignIn config)

// ─── Helpers ─────────────────────────────────────────────────────────

let private b64url (bytes: byte[]) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private randomToken () =
    b64url (RandomNumberGenerator.GetBytes 32)

/// RFC 7636 `S256`: BASE64URL(SHA256(ASCII(verifier))).
let codeChallenge (verifier: string) =
    b64url (SHA256.HashData(Encoding.ASCII.GetBytes verifier))

let private fixedTimeEquals (a: string) (b: string) =
    CryptographicOperations.FixedTimeEquals(
        ReadOnlySpan<byte>(Encoding.UTF8.GetBytes a),
        ReadOnlySpan<byte>(Encoding.UTF8.GetBytes b)
    )

let private protector (ctx: HttpContext) : ITimeLimitedDataProtector option =
    match ctx.RequestServices.GetService<IDataProtectionProvider>() with
    | null -> None
    | p -> Some((p.CreateProtector ProtectorPurpose).ToTimeLimitedDataProtector())

let private stateCookieOptions (config: OidcSsrSignInConfig) (ctx: HttpContext) =
    let opts = CookieOptions()
    opts.HttpOnly <- true
    // Lax, not Strict: the IdP's redirect back to the callback is a
    // cross-site top-level navigation, which a Strict cookie would miss.
    opts.SameSite <- SameSiteMode.Lax
    opts.Secure <- ctx.Request.IsHttps
    opts.Path <- config.CallbackPath
    opts.MaxAge <- Nullable config.StateLifetime
    opts

let private sessionCookieOptions (ctx: HttpContext) =
    let opts = CookieOptions()
    opts.HttpOnly <- true
    opts.SameSite <- SameSiteMode.Lax
    opts.Secure <- ctx.Request.IsHttps
    opts.Path <- "/"
    opts

let private writePage (ctx: HttpContext) (status: int) (message: string) : Task<HttpContext option> =
    ctx.Response.StatusCode <- status
    ctx.Response.ContentType <- "text/plain; charset=utf-8"
    ctx.Response.Headers["Cache-Control"] <- "no-store"
    ctx.Response.Headers["X-Robots-Tag"] <- "noindex"

    task {
        do! ctx.Response.WriteAsync message
        return Some ctx
    }

let private redirect (ctx: HttpContext) (location: string) : Task<HttpContext option> =
    ctx.Response.StatusCode <- 302
    ctx.Response.Headers["Location"] <- location
    ctx.Response.Headers["Cache-Control"] <- "no-store"
    Task.FromResult(Some ctx)

/// The sealed sign-in state: what the callback must find to finish.
type private SignInState = {
    State: string
    Verifier: string
    Nonce: string
    ReturnUrl: string
}

let private sealState (p: ITimeLimitedDataProtector) (lifetime: TimeSpan) (s: SignInState) =
    let json =
        JsonSerializer.Serialize(dict [ "s", s.State; "v", s.Verifier; "n", s.Nonce; "r", s.ReturnUrl ])

    p.Protect(json, DateTimeOffset.UtcNow.Add lifetime)

let private unsealState (p: ITimeLimitedDataProtector) (sealedValue: string) : SignInState option =
    try
        let json = p.Unprotect sealedValue
        let d = JsonSerializer.Deserialize<Dictionary<string, string>> json

        Some {
            State = d["s"]
            Verifier = d["v"]
            Nonce = d["n"]
            ReturnUrl = d["r"]
        }
    with _ ->
        // Tampered, expired, or sealed under another key ring: no state.
        None

let private authorizeUrl (config: OidcSsrSignInConfig) (s: SignInState) =
    let q (k: string) (v: string) = $"{k}={Uri.EscapeDataString v}"

    let query =
        [
            q "response_type" "code"
            q "client_id" config.ClientId
            q "redirect_uri" config.RedirectUri
            q "scope" (String.Join(" ", config.Scopes))
            q "state" s.State
            q "nonce" s.Nonce
            q "code_challenge" (codeChallenge s.Verifier)
            q "code_challenge_method" "S256"
        ]
        |> String.concat "&"

    let sep =
        if config.AuthorizationEndpoint.Contains "?" then
            "&"
        else
            "?"

    $"{config.AuthorizationEndpoint}{sep}{query}"

/// The `nonce` claim of an already-validated JWT, if any.
let private nonceOf (token: string) : string option =
    try
        let parts = token.Split '.'
        let payload = parts[1].Replace('-', '+').Replace('_', '/')
        let padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')
        use doc = JsonDocument.Parse(Convert.FromBase64String padded)

        match doc.RootElement.TryGetProperty "nonce" with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None
    with _ ->
        None

/// Validate `token` through the registered provider, presenting it the way
/// a later request will: as a bearer header AND as the session cookie, so a
/// provider reading either location sees it. A separate request context —
/// the callback request itself is not modified.
let private validateWithProvider (ctx: HttpContext) (cookieName: string) (token: string) = async {
    let probe = DefaultHttpContext()
    probe.RequestServices <- ctx.RequestServices
    probe.Request.Headers["Authorization"] <- $"Bearer {token}"
    probe.Request.Headers["Cookie"] <- $"{cookieName}={token}"

    match ctx.RequestServices.GetService(typeof<IAuthProvider>) with
    | :? IAuthProvider as provider -> return! provider.ValidateRequest(RequestContextBuilder.ofHttpContext probe)
    | _ -> return Error "no IAuthProvider is registered"
}

// ─── Routes ──────────────────────────────────────────────────────────

let private signIn (config: OidcSsrSignInConfig) : HttpHandler =
    fun _next ctx ->
        match protector ctx with
        | None ->
            writePage
                ctx
                500
                "Sign-in is unavailable: ASP.NET Core DataProtection is not composed, so the sign-in state cannot be sealed."
        | Some p ->
            let requested =
                match ctx.Request.Query.TryGetValue InteractiveSignIn.ReturnUrlParameter with
                | true, v when v.Count > 0 -> string v[0]
                | _ -> "/"

            let state = {
                State = randomToken ()
                Verifier = randomToken ()
                Nonce = randomToken ()
                ReturnUrl =
                    if InteractiveSignIn.isLocalReturnUrl requested then
                        requested
                    else
                        "/"
            }

            ctx.Response.Cookies.Append(
                StateCookieName,
                sealState p config.StateLifetime state,
                stateCookieOptions config ctx
            )

            redirect ctx (authorizeUrl config state)

let private exchangeCode (http: HttpClient) (config: OidcSsrSignInConfig) (code: string) (verifier: string) = task {
    let form =
        [
            "grant_type", "authorization_code"
            "code", code
            "redirect_uri", config.RedirectUri
            "client_id", config.ClientId
            "code_verifier", verifier
            yield!
                config.ClientSecret
                |> Option.map (fun secret -> "client_secret", secret)
                |> Option.toList
        ]
        |> List.map (fun (k, v) -> KeyValuePair(k, v))

    use content = new FormUrlEncodedContent(form)

    try
        use! response = http.PostAsync(config.TokenEndpoint, content)
        let! body = response.Content.ReadAsStringAsync()

        if not response.IsSuccessStatusCode then
            return Error $"the token endpoint answered {int response.StatusCode}"
        else
            use doc = JsonDocument.Parse body

            let field =
                match config.SessionToken with
                | SsrSessionToken.IdToken -> "id_token"
                | SsrSessionToken.AccessToken -> "access_token"

            match doc.RootElement.TryGetProperty field with
            | true, v when
                v.ValueKind = JsonValueKind.String
                && not (String.IsNullOrWhiteSpace(v.GetString()))
                ->
                return Ok(v.GetString())
            | _ -> return Error $"the token response carries no {field}"
    with ex ->
        return Error $"the token exchange failed ({ex.GetType().Name})"
}

let private callback (http: HttpClient) (config: OidcSsrSignInConfig) : HttpHandler =
    fun _next ctx -> task {
        let query (name: string) =
            match ctx.Request.Query.TryGetValue name with
            | true, v when v.Count > 0 && not (String.IsNullOrEmpty(string v[0])) -> Some(string v[0])
            | _ -> None

        let sealedState =
            match protector ctx, ctx.Request.Cookies.TryGetValue StateCookieName with
            | Some p, (true, value) -> unsealState p value
            | _ -> None

        // One use: the state cookie is cleared whatever the outcome.
        ctx.Response.Cookies.Delete(StateCookieName, stateCookieOptions config ctx)

        match sealedState with
        | None -> return! writePage ctx 400 "The sign-in has expired or was not started here. Open the page again."
        | Some state ->
            match query "error", query "state", query "code" with
            | Some _, _, _ -> return! writePage ctx 401 "The identity provider did not complete the sign-in."
            | None, Some returned, Some code when fixedTimeEquals returned state.State ->
                match! exchangeCode http config code state.Verifier with
                | Error reason -> return! writePage ctx 502 $"Sign-in failed: {reason}."
                | Ok token ->
                    match! validateWithProvider ctx config.CookieName token |> Async.StartAsTask with
                    | Ok user when not (AuthenticatedUser.isAnonymous user) ->
                        let nonceOk =
                            match config.SessionToken with
                            | SsrSessionToken.IdToken ->
                                nonceOf token |> Option.exists (fun n -> fixedTimeEquals n state.Nonce)
                            // An access token carries no nonce; its binding to
                            // this sign-in is the state + PKCE exchange.
                            | SsrSessionToken.AccessToken -> true

                        if not nonceOk then
                            return! writePage ctx 401 "Sign-in failed: the token was not issued for this sign-in."
                        else
                            ctx.Response.Cookies.Append(config.CookieName, token, sessionCookieOptions ctx)
                            return! redirect ctx state.ReturnUrl
                    | _ ->
                        // Signed in at the IdP but not accepted here (admission
                        // gate, audience, claim mapping). An error page, never a
                        // redirect back to the page — that would loop.
                        return! writePage ctx 401 "Your account is not accepted by this site."
            | _ -> return! writePage ctx 400 "The sign-in response did not match the sign-in that was started."
    }

/// The sign-in and callback routes. `http` performs the code exchange;
/// production callers pass a long-lived client.
let routes (http: HttpClient) (config: OidcSsrSignInConfig) : HttpHandler =
    choose [
        GET >=> route config.SignInPath >=> signIn config
        GET >=> route config.CallbackPath >=> callback http config
    ]

/// Compose the interactive sign-in onto a `ServerApp`: the two routes are
/// appended to the router through `ComposeExtensions.Handlers` and the
/// `InteractiveSignIn` registration through its `ServiceConfig`, the seam
/// every companion contributes through. Apply it BEFORE
/// `PublicRenderingCompose.withPublicRendering`, so the routes precede the
/// public-page catch-all.
let withInteractiveSignIn (http: HttpClient) (config: OidcSsrSignInConfig) (app: ServerApp) : ServerApp =
    let registerSignIn (s: IServiceCollection) = register s config

    {
        app with
            Extensions = {
                app.Extensions with
                    Handlers = app.Extensions.Handlers @ [ routes http config ]
                    ServiceConfig =
                        match app.Extensions.ServiceConfig with
                        | None -> Some registerSignIn
                        | Some baseFn -> Some(fun s -> registerSignIn (baseFn s))
            }
    }