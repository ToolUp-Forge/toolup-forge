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
///      `SameSite=Lax`, `Secure` on HTTPS, `Max-Age` the token's remaining
///      life (Phase 1047) — and redirects to the return URL.
///
/// The session cookie shares its name with the SPA's (`toolup-auth-token` by
/// default), which the event streams authenticate from. `routes` therefore
/// also mounts `POST` / `DELETE /api/auth/session`, through which the SPA
/// replaces a cookie it cannot overwrite from script (Phase 1047).
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
///
/// **The error page (Phase 1139).** Every failure answers with one small
/// HTML page: a plain-language reason drawn from a closed set (never IdP
/// text, a token or an exception message), a "Try again" link back to
/// `SignInPath` carrying the sealed, already-validated return URL, and either
/// the deployment's own stylesheet (`SignInPage.withStylesheet`) or minimal
/// inline styling. Status codes are unchanged.
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
    /// SDK's `toolup-auth-token`, which the SPA writes too; with that name
    /// `routes` also mounts `/api/auth/session` (Phase 1047).
    CookieName: string
    /// How long a started sign-in may take before its state expires.
    StateLifetime: TimeSpan
}

/// Phase 1139 — how the sign-in failure page looks. Kept apart from
/// `OidcSsrSignInConfig` so that record's shape (and every full-literal
/// construction of it) is untouched; opaque, so a later appearance setting
/// is an addition here and breaks no caller. Build it with
/// `SignInPage.defaults` and `SignInPage.withStylesheet`.
type SignInPage = private {
    StylesheetPath: string option
}

[<RequireQualifiedAccess>]
module SignInPage =
    /// No stylesheet: the failure page carries minimal inline styling.
    let defaults: SignInPage = { StylesheetPath = None }

    /// The page links `path` — a local, rooted path such as `/theme.css` —
    /// as its one stylesheet, so a failure takes the deployment's theme.
    /// Anything that is not a local path (an absolute URL, `//host`, a path
    /// with whitespace, a quote, an angle bracket, a backtick or a
    /// backslash) is refused with `ArgumentException`, at composition time,
    /// so a bad value stops the boot rather than shipping a broken or
    /// foreign `<link>`.
    let withStylesheet (path: string) (page: SignInPage) : SignInPage =
        let clean =
            InteractiveSignIn.isLocalReturnUrl path
            && not (path |> Seq.exists (fun c -> Char.IsWhiteSpace c || "\"'<>`\\".Contains c))

        if not clean then
            invalidArg (nameof path) "the sign-in stylesheet must be a local, rooted path such as /theme.css"

        { page with StylesheetPath = Some path }

    /// The stylesheet path, if one is set.
    let stylesheet (page: SignInPage) : string option = page.StylesheetPath

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
///
/// Phase 992 — it also arms `SpaFallbackPrecedence.RouterFirst`. The
/// sign-in and callback routes are extensionless GETs, which the SPA
/// fallback otherwise answers with the client shell whenever a bundle
/// ships, so a deployment composing interactive sign-in without
/// PublicRendering would never reach them. `useRouterFirst` is idempotent,
/// so composing PublicRendering as well leaves one registration.
let register (services: IServiceCollection) (config: OidcSsrSignInConfig) : IServiceCollection =
    services.AddSingleton<InteractiveSignIn>(interactiveSignIn config)
    |> SpaFallbackPrecedence.useRouterFirst

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

/// Phase 1047 — `lifetime` is the token's remaining life: the cookie must not
/// outlive the credential it carries, or the browser keeps presenting an
/// expired token under the name the SPA needs to write.
let private sessionCookieOptions (ctx: HttpContext) (lifetime: TimeSpan option) =
    let opts = CookieOptions()
    opts.HttpOnly <- true
    opts.SameSite <- SameSiteMode.Lax
    opts.Secure <- ctx.Request.IsHttps
    opts.Path <- "/"
    lifetime |> Option.iter (fun l -> opts.MaxAge <- Nullable l)
    opts

/// Phase 1139 — the closed set of reasons a sign-in can fail. The page says
/// one of these in fixed words; nothing from the IdP, a token or an exception
/// ever reaches it.
type private SignInFailure =
    | Unavailable
    | Expired
    | Declined
    | Mismatch
    | ExchangeFailed
    | NotIssuedHere
    | TokenExpired
    | NotAccepted

let private failureDetail (failure: SignInFailure) : int * string * string =
    match failure with
    | Unavailable -> 500, "unavailable", "Sign-in is unavailable on this site right now."
    | Expired -> 400, "expired", "This sign-in has expired or was not started here."
    | Declined -> 401, "declined", "The identity provider did not complete the sign-in."
    | Mismatch -> 400, "mismatch", "The sign-in response did not match the sign-in that was started."
    | ExchangeFailed -> 502, "exchange-failed", "The identity provider could not finish signing you in."
    | NotIssuedHere -> 401, "not-issued-here", "The sign-in could not be verified for this visit."
    | TokenExpired -> 401, "token-expired", "The sign-in had already expired when it arrived."
    | NotAccepted -> 401, "not-accepted", "Your account is not accepted by this site."

let private minimalStyle =
    "body{font-family:system-ui,sans-serif;margin:0;display:grid;min-height:100vh;place-items:center}"
    + "main{max-width:32rem;padding:2rem}a{font-weight:600}"

/// The one renderer for every failure path. `returnUrl` is already validated
/// local (it comes from the sealed state, or is `/`); the try-again link
/// re-enters `SignInPath` with it, except when sign-in itself is unavailable.
let private writeFailure
    (page: SignInPage)
    (config: OidcSsrSignInConfig)
    (ctx: HttpContext)
    (failure: SignInFailure)
    (returnUrl: string)
    : Task<HttpContext option> =
    let status, code, reason = failureDetail failure
    let enc (s: string) = System.Net.WebUtility.HtmlEncode s

    let head =
        match page.StylesheetPath with
        | Some href -> $"<link rel=\"stylesheet\" href=\"{enc href}\">"
        | None -> $"<style>{minimalStyle}</style>"

    let retry =
        match failure with
        | Unavailable -> ""
        | _ ->
            let href = InteractiveSignIn.redirectFor (interactiveSignIn config) returnUrl
            $"<p><a class=\"signin-failure-retry\" href=\"{enc href}\">Try again</a></p>"

    let html =
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
        + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
        + "<meta name=\"robots\" content=\"noindex\"><title>Sign-in problem</title>"
        + head
        + $"</head><body><main class=\"signin-failure\" data-reason=\"{code}\"><h1>Sign-in problem</h1>"
        + $"<p class=\"signin-failure-reason\">{enc reason}</p>{retry}</main></body></html>"

    ctx.Response.StatusCode <- status
    ctx.Response.ContentType <- "text/html; charset=utf-8"
    ctx.Response.Headers["Cache-Control"] <- "no-store"
    ctx.Response.Headers["X-Robots-Tag"] <- "noindex"

    task {
        do! ctx.Response.WriteAsync html
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

/// The claim `name` of an already-validated JWT, read by `read`, if any.
let private claimOf (token: string) (name: string) (read: JsonElement -> 'a option) : 'a option =
    try
        let parts = token.Split '.'
        let payload = parts[1].Replace('-', '+').Replace('_', '/')
        let padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')
        use doc = JsonDocument.Parse(Convert.FromBase64String padded)

        match doc.RootElement.TryGetProperty name with
        | true, v -> read v
        | _ -> None
    with _ ->
        None

/// The `nonce` claim of an already-validated JWT, if any.
let private nonceOf (token: string) : string option =
    claimOf token "nonce" (fun v ->
        if v.ValueKind = JsonValueKind.String then
            Some(v.GetString())
        else
            None)

/// Phase 1047 — the `exp` claim of an already-validated JWT, if any.
let private expiryOf (token: string) : DateTimeOffset option =
    claimOf token "exp" (fun v ->
        match v.ValueKind with
        | JsonValueKind.Number ->
            match v.TryGetInt64() with
            | true, seconds -> Some(DateTimeOffset.FromUnixTimeSeconds seconds)
            | _ -> None
        | _ -> None)

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

let private signIn (page: SignInPage) (config: OidcSsrSignInConfig) : HttpHandler =
    fun _next ctx ->
        match protector ctx with
        | None -> writeFailure page config ctx Unavailable "/"
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

let private callback (page: SignInPage) (http: HttpClient) (config: OidcSsrSignInConfig) : HttpHandler =
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
        | None -> return! writeFailure page config ctx Expired "/"
        | Some state ->
            match query "error", query "state", query "code" with
            | Some _, _, _ -> return! writeFailure page config ctx Declined state.ReturnUrl
            | None, Some returned, Some code when fixedTimeEquals returned state.State ->
                match! exchangeCode http config code state.Verifier with
                | Error reason -> return! writeFailure page config ctx ExchangeFailed state.ReturnUrl
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

                        // Phase 1047 — the cookie lives exactly as long as the
                        // token: whole seconds, since `Max-Age` is. A token
                        // with no `exp` keeps a browser-session cookie.
                        let lifetime =
                            expiryOf token
                            |> Option.map (fun exp ->
                                TimeSpan.FromSeconds(floor (exp - DateTimeOffset.UtcNow).TotalSeconds))

                        if not nonceOk then
                            return! writeFailure page config ctx NotIssuedHere state.ReturnUrl
                        elif lifetime |> Option.exists (fun l -> l <= TimeSpan.Zero) then
                            // Accepted inside the provider's clock skew but
                            // already past `exp`: a cookie the browser drops at
                            // once would only send the reader round again.
                            return! writeFailure page config ctx TokenExpired state.ReturnUrl
                        else
                            ctx.Response.Cookies.Append(config.CookieName, token, sessionCookieOptions ctx lifetime)
                            return! redirect ctx state.ReturnUrl
                    | _ ->
                        // Signed in at the IdP but not accepted here (admission
                        // gate, audience, claim mapping). An error page, never a
                        // redirect back to the page — that would loop.
                        return! writeFailure page config ctx NotAccepted state.ReturnUrl
            | _ -> return! writeFailure page config ctx Mismatch state.ReturnUrl
    }

/// The sign-in and callback routes. `http` performs the code exchange;
/// production callers pass a long-lived client.
///
/// Phase 1047 — when the session cookie is the SDK's `toolup-auth-token`,
/// also `POST` / `DELETE /api/auth/session` (`AuthSession.routes`). The SPA
/// writes that cookie from script, and a browser silently drops a script
/// write over the `HttpOnly` cookie this sign-in sets, so the SPA's only way
/// to replace it is through the server. The route validates the bearer
/// token it is sent before writing it `HttpOnly`, and sits behind the CSRF
/// middleware like every mutating `/api` call. Mounting it twice (beside
/// `AuthCookieIssuance = EnabledAuthCookieIssuance`) is harmless: the
/// handlers are the same and the first match answers.
let routesWithPage (page: SignInPage) (http: HttpClient) (config: OidcSsrSignInConfig) : HttpHandler =
    choose [
        GET >=> route config.SignInPath >=> signIn page config
        GET >=> route config.CallbackPath >=> callback page http config
        if config.CookieName = AuthSession.CookieName then
            yield! AuthSession.routes
    ]

/// `routesWithPage` with the default failure page (Phase 1139: minimal
/// inline styling).
let routes (http: HttpClient) (config: OidcSsrSignInConfig) : HttpHandler =
    routesWithPage SignInPage.defaults http config

/// Compose the interactive sign-in onto a `ServerApp` with `page` as the failure page: the two routes are
/// appended to the router through `ComposeExtensions.Handlers` and the
/// `InteractiveSignIn` registration through its `ServiceConfig`, the seam
/// every companion contributes through. Apply it BEFORE
/// `PublicRenderingCompose.withPublicRendering`, so the routes precede the
/// public-page catch-all.
/// **Adds** the sign-in routes, and their registrations through `ServerApp.addServiceConfig`.
let withInteractiveSignInPage
    (page: SignInPage)
    (http: HttpClient)
    (config: OidcSsrSignInConfig)
    (app: ServerApp)
    : ServerApp =
    let registerSignIn (s: IServiceCollection) = register s config

    {
        app with
            Extensions = {
                app.Extensions with
                    Handlers = app.Extensions.Handlers @ [ routesWithPage page http config ]
            }
    }
    |> ServerApp.addServiceConfig registerSignIn

/// `withInteractiveSignInPage` with the default failure page.
/// **Adds** what `withInteractiveSignInPage` adds.
let withInteractiveSignIn (http: HttpClient) (config: OidcSsrSignInConfig) (app: ServerApp) : ServerApp =
    withInteractiveSignInPage SignInPage.defaults http config app