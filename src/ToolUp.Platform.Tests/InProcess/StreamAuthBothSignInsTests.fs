module ToolUp.Platform.Tests.InProcess.StreamAuthBothSignInsTests

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.NotificationChannel
open ToolUp.AuthProviders

// ─── Phase 1047 — the event streams authenticate whichever sign-in the user took ───
//
// Two writers share one cookie name, `toolup-auth-token`: the server-rendered
// sign-in (`OidcSsrSignIn`, `HttpOnly`) and the SPA (`UserSession.setAuthToken`,
// from script on the default `ClientCookieAndLocalStorage` path). A browser
// never lets script replace an `HttpOnly` cookie of the same name and path
// (RFC 6265bis, storage model), so once the server has signed a reader in, the
// SPA's own write is dropped without a word. `EventSource` carries cookies and
// nothing else, so the streams ride the server's token while REST rides the
// SPA's bearer header — until the server's token expires, when every stream
// answers 401 and REST stays green.
//
// The browser is the part a .NET test cannot run, so it is modelled here by
// `BrowserJar` — one origin, `Path=/`, and exactly the rules that make the
// defect: server `Set-Cookie` always lands (`Max-Age` / `Expires` honoured),
// a script write never replaces a live `HttpOnly` cookie, and
// `document.cookie` never shows one. The SPA side is the SDK's own decision,
// `UserSession.authCookieCarries`, driving the same request the client sends.
// Everything server-side is real: the OIDC provider against a mock issuer,
// `ScopeResolutionMiddleware`, `SseScopeResolution` under `CookieRequired`
// (what `/api/notifications` and `/api/ai/events` run), the sign-in routes and
// `POST` / `DELETE /api/auth/session`.

let private silentLogger: ILogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// The server sign-in's tokens live this long, so a test can outlive one.
let private serverTokenLifetime = TimeSpan.FromSeconds 4.0

/// One issuer for the section. Its `/token` endpoint mints the server
/// sign-in's id_token at `serverTokenLifetime`; the SPA's tokens are minted
/// directly with a long lifetime.
let private issuer =
    lazy
        (MockOidcServer.start {
            MockOidcServer.MockOidcConfig.defaults with
                TokenLifetime = serverTokenLifetime
        })

let private reader = "zoe"

/// A token the SPA holds: the same reader, long-lived.
let private spaToken () =
    issuer.Force().MintTokenWithLifetime(reader, TimeSpan.FromMinutes 30.0)

let private provider () : IAuthProvider =
    let s = issuer.Force()

    OidcAuthProvider.fromConfigWith (new HttpClient()) None {
        Issuer = Some s.IssuerUrl
        Audience = None
        KeySource = JwksDiscovery s.IssuerUrl
        TokenLocation = BearerOrCookie AuthSession.CookieName
        // No grace: an expired token is refused the moment it expires.
        ClockSkewSeconds = Some 0L
        AcceptedAlgorithms = None
        PreferOidWhenPresent = None
        ClaimMapping = None
    }

let private signInConfig () =
    let s = issuer.Force()

    OidcSsrSignIn.defaults
        $"{s.IssuerUrl}/authorize"
        $"{s.IssuerUrl}/token"
        "stream-auth-client"
        "http://localhost/auth/callback"

/// The stream gate `/api/notifications` and `/api/ai/events` share, on its
/// own: the scope the middleware resolved, or the `CookieRequired` 401.
let private streamProbe: HttpHandler =
    fun _ ctx -> task {
        match SseScopeResolution.resolve CookieRequired ctx with
        | SseScopeResolution.SseScope scope ->
            ctx.Response.StatusCode <- 200
            do! ctx.Response.WriteAsync scope
        | SseScopeResolution.SseUnauthenticated -> do! SseScopeResolution.refuseUnauthenticated ctx

        return Some ctx
    }

/// A REST call: the identity the middleware resolved, or 401.
let private restProbe: HttpHandler =
    fun _ ctx -> task {
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) when not (String.IsNullOrEmpty id) && id <> "anonymous" ->
            ctx.Response.StatusCode <- 200
            do! ctx.Response.WriteAsync id
        | _ -> ctx.Response.StatusCode <- 401

        return Some ctx
    }

let private startHost () : Async<IHost> = async {
    let config = {
        ServerConfig.defaults with
            Surfaces = [ SurfaceProfile.individual ]
    }

    let signIn = signInConfig ()
    let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
    let blob = ToolUp.Platform.Testing.Fakes.standardBlobStorage ()

    let permissionStore =
        PermissionStore.PermissionStore(blob) :> PermissionStore.IPermissionStore

    let host =
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost
                    .UseTestServer()
                    .ConfigureServices(fun services ->
                        services.AddGiraffe() |> ignore
                        services.AddDataProtection() |> ignore
                        services.AddSingleton<ILogger>(silentLogger) |> ignore
                        services.AddSingleton<IAuthProvider>(provider ()) |> ignore
                        OidcSsrSignIn.register services signIn |> ignore

                        services.AddSingleton<PermissionStore.IPermissionStore>(permissionStore)
                        |> ignore

                        ComposeScopeResolver.registerScopeResolution
                            services
                            config
                            None
                            notifications
                            silentLogger
                            []
                            []
                            []
                            None)
                    .Configure(fun (app: IApplicationBuilder) ->
                        app.UseMiddleware<Middleware.ScopeResolutionMiddleware>(config) |> ignore

                        app.UseGiraffe(
                            choose [
                                OidcSsrSignIn.routes (new HttpClient()) signIn
                                route "/api/notifications" >=> streamProbe
                                route "/api/rest" >=> restProbe
                            ]
                        ))
                |> ignore)
            .Build()

    do! host.StartAsync() |> Async.AwaitTask
    return host
}

// ─── The browser, modelled ───────────────────────────────────────────

type private JarCookie = {
    Value: string
    HttpOnly: bool
    SetCookie: string
    ExpiresAt: DateTimeOffset option
}

/// One origin's cookies at `Path=/`. Cookies scoped to another path (the
/// sign-in's state cookie, `Path=/auth/callback`) are carried by hand.
type private BrowserJar() =
    let cookies = Dictionary<string, JarCookie>()

    let live (c: JarCookie) =
        c.ExpiresAt |> Option.forall (fun at -> DateTimeOffset.UtcNow < at)

    let liveCookies () =
        cookies |> Seq.filter (fun kv -> live kv.Value) |> Seq.toList

    /// Apply every `Set-Cookie` of a response, as the browser does.
    member _.FromServer(response: HttpResponseMessage) =
        match response.Headers.TryGetValues "Set-Cookie" with
        | true, values ->
            for header in values do
                let parts = header.Split ';' |> Array.map _.Trim()
                let eq = parts[0].IndexOf '='
                let name = parts[0].Substring(0, eq)
                let value = parts[0].Substring(eq + 1)
                let attributes = parts |> Array.skip 1

                let attribute (key: string) =
                    attributes
                    |> Array.tryPick (fun p ->
                        if p.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) then
                            Some(p.Substring(key.Length + 1))
                        else
                            None)

                let flag (key: string) =
                    attributes
                    |> Array.exists (fun p -> p.Equals(key, StringComparison.OrdinalIgnoreCase))

                if attribute "path" |> Option.defaultValue "/" = "/" then
                    let expiresAt =
                        match attribute "max-age" with
                        | Some seconds -> Some(DateTimeOffset.UtcNow.AddSeconds(float (int64 seconds)))
                        | None -> attribute "expires" |> Option.map DateTimeOffset.Parse

                    if expiresAt |> Option.exists (fun at -> at <= DateTimeOffset.UtcNow) then
                        cookies.Remove name |> ignore
                    else
                        cookies[name] <- {
                            Value = value
                            HttpOnly = flag "httponly"
                            SetCookie = header
                            ExpiresAt = expiresAt
                        }
        | _ -> ()

    /// `document.cookie = "<name>=<value>"` — or, with `delete`, the
    /// `Max-Age=0` form. A live `HttpOnly` cookie of that name is never
    /// replaced or removed from script: the write is silently dropped.
    member _.FromScript(name: string, value: string, delete: bool) =
        match cookies.TryGetValue name with
        | true, existing when existing.HttpOnly && live existing -> ()
        | _ ->
            if delete then
                cookies.Remove name |> ignore
            else
                cookies[name] <- {
                    Value = value
                    HttpOnly = false
                    SetCookie = ""
                    ExpiresAt = None
                }

    /// What script reads from `document.cookie`: no `HttpOnly` cookie.
    member _.DocumentCookie =
        liveCookies ()
        |> List.filter (fun kv -> not kv.Value.HttpOnly)
        |> List.map (fun kv -> $"{kv.Key}={kv.Value.Value}")
        |> String.concat "; "

    /// The `Cookie` header a same-origin request carries.
    member _.Header =
        liveCookies ()
        |> List.map (fun kv -> $"{kv.Key}={kv.Value.Value}")
        |> String.concat "; "

    member _.TryGet(name: string) =
        liveCookies () |> List.tryFind (fun kv -> kv.Key = name) |> Option.map _.Value

/// Send `request` with the jar's cookies (plus `extra`, a cookie scoped to
/// another path), and apply the response's `Set-Cookie`s to the jar.
let private sendWith (extra: string option) (client: HttpClient) (jar: BrowserJar) (request: HttpRequestMessage) = async {
    match jar.Header :: Option.toList extra |> List.filter ((<>) "") with
    | [] -> ()
    | cookies -> request.Headers.Add("Cookie", String.concat "; " cookies)

    let! response = client.SendAsync request |> Async.AwaitTask
    jar.FromServer response
    return response
}

let private send client jar request = sendWith None client jar request

/// The `exp` of a JWT.
let private expiryOf (token: string) =
    let payload = (token.Split '.').[1].Replace('-', '+').Replace('_', '/')
    let padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')
    use doc = JsonDocument.Parse(Convert.FromBase64String(padded))
    DateTimeOffset.FromUnixTimeSeconds(doc.RootElement.GetProperty("exp").GetInt64())

/// Wait until `token` has expired, with a margin for the whole-second `exp`.
let private outlive (token: string) = async {
    let wait = expiryOf token - DateTimeOffset.UtcNow + TimeSpan.FromSeconds 1.2

    if wait > TimeSpan.Zero then
        do! Async.Sleep wait
}

/// The reader opens a gated page cold and signs in through the server
/// (authorization code + PKCE). Returns the callback response.
let private serverSignIn (client: HttpClient) (jar: BrowserJar) (scheme: string) = async {
    let s = issuer.Force()
    s.SetSignInIdentity(reader, [])
    let at (path: string) = Uri($"{scheme}://localhost{path}")

    use! start = send client jar (new HttpRequestMessage(HttpMethod.Get, at "/auth/sign-in?returnUrl=%2F"))

    let stateCookie =
        match start.Headers.TryGetValues "Set-Cookie" with
        | true, values ->
            values
            |> Seq.find (fun v -> v.StartsWith(OidcSsrSignIn.StateCookieName + "="))
            |> fun v -> (v.Split ';').[0]
        | _ -> failtest "the sign-in sets its state cookie"

    use idp = new HttpClient(new HttpClientHandler(AllowAutoRedirect = false))
    use! fromIdp = idp.GetAsync(string start.Headers.Location) |> Async.AwaitTask
    let callback = Uri(string fromIdp.Headers.Location)

    let! landed =
        sendWith (Some stateCookie) client jar (new HttpRequestMessage(HttpMethod.Get, at callback.PathAndQuery))

    Expect.equal (int landed.StatusCode) 302 "the server sign-in completes"
    return landed
}

/// The SDK client storing a token on the default path — `setAuthToken`:
/// write the cookie from script and, where the write did not land (a
/// server-written `HttpOnly` cookie holds the name), send the token to
/// `POST /api/auth/session` so the server replaces it. Returns whether it
/// had to.
let private spaStoresToken (client: HttpClient) (jar: BrowserJar) (token: string) = async {
    jar.FromScript(AuthSession.CookieName, token, false)

    if UserSession.authCookieCarries jar.DocumentCookie token then
        return false
    else
        let request = new HttpRequestMessage(HttpMethod.Post, AuthSession.Path)
        request.Headers.Add("Authorization", "Bearer " + token)
        use! _ = send client jar request
        return true
}

/// The SDK client before Phase 1047: the script write, and nothing else.
let private spaStoresTokenPre1047 (jar: BrowserJar) (token: string) =
    jar.FromScript(AuthSession.CookieName, token, false)

/// The SDK client's request path — `getAuthToken` — on the default path:
/// a held token missing from `document.cookie` is written again (no
/// network: if a server-written cookie still holds the name, the write is
/// dropped and that cookie stays).
let private spaReadsToken (jar: BrowserJar) (token: string) =
    if not (UserSession.authCookieCarries jar.DocumentCookie token) then
        jar.FromScript(AuthSession.CookieName, token, false)

/// The SDK client signing out on the default path — `clearAuthToken`.
let private spaSignsOut (client: HttpClient) (jar: BrowserJar) (heldToken: string) = async {
    let serverHeld = not (UserSession.authCookieCarries jar.DocumentCookie heldToken)
    jar.FromScript(AuthSession.CookieName, "", true)

    if serverHeld then
        use! _ = send client jar (new HttpRequestMessage(HttpMethod.Delete, AuthSession.Path))
        ()
}

/// An `EventSource` handshake: the jar's cookies and no other credential.
/// The scope the stream registered under, or `None` when refused.
let private stream (client: HttpClient) (jar: BrowserJar) = async {
    use! response = send client jar (new HttpRequestMessage(HttpMethod.Get, "/api/notifications"))
    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
    return if int response.StatusCode = 200 then Some body else None
}

/// A REST call: the SPA's bearer header plus the jar's cookies.
let private rest (client: HttpClient) (jar: BrowserJar) (token: string) = async {
    let request = new HttpRequestMessage(HttpMethod.Get, "/api/rest")
    request.Headers.Add("Authorization", "Bearer " + token)
    use! response = send client jar request
    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
    return if int response.StatusCode = 200 then Some body else None
}

let private withHost (body: HttpClient -> Async<unit>) = async {
    use! host = startHost ()
    use client = host.GetTestClient()

    try
        do! body client
    finally
        host.StopAsync() |> Async.AwaitTask |> Async.RunSynchronously
}

let private pipelineTests =
    testList "the composed pipeline, with a browser's cookie rules" [
        testCaseAsync
            "reproduction — before Phase 1047 the SPA's write is dropped, and once the server token expires the stream is refused while REST is accepted"
        <| withHost (fun client -> async {
            let jar = BrowserJar()
            let! _ = serverSignIn client jar "http"
            let serverToken = (jar.TryGet AuthSession.CookieName |> Option.get).Value
            let spa = spaToken ()

            spaStoresTokenPre1047 jar spa

            Expect.equal
                (jar.TryGet AuthSession.CookieName |> Option.map _.Value)
                (Some serverToken)
                "the script write is dropped: the cookie still holds the server's token"

            let! before = stream client jar
            Expect.equal before (Some reader) "the stream rides the server's token while it is valid"

            do! outlive serverToken

            let! streamAfter = stream client jar
            let! restAfter = rest client jar spa
            Expect.isNone streamAfter "the stream is refused once the server's token has expired"
            Expect.equal restAfter (Some reader) "while REST, on the SPA's bearer header, is accepted"
        })

        testCaseAsync
            "server sign-in, then the SPA's: the SPA's token replaces the server's cookie and the stream outlives the server token"
        <| withHost (fun client -> async {
            let jar = BrowserJar()
            let! _ = serverSignIn client jar "http"
            let serverToken = (jar.TryGet AuthSession.CookieName |> Option.get).Value
            let spa = spaToken ()

            let! reflected = spaStoresToken client jar spa
            Expect.isTrue reflected "the dropped script write is detected and sent through the server"

            let cookie =
                jar.TryGet AuthSession.CookieName
                |> Option.defaultWith (fun () -> failtest "the SPA's token is in the cookie")

            Expect.equal cookie.Value spa "the server replaced its own cookie with the SPA's token: the latest wins"
            Expect.isTrue cookie.HttpOnly "and wrote it HttpOnly"

            Expect.stringContains (cookie.SetCookie.ToLowerInvariant()) "samesite=strict" "and SameSite=Strict"

            do! outlive serverToken

            let! streamAfter = stream client jar
            let! restAfter = rest client jar spa
            Expect.equal streamAfter (Some reader) "the stream stays authenticated past the server token's expiry"
            Expect.equal restAfter (Some reader) "as REST does"
        })

        testCaseAsync "the server sign-in's cookie expires with its token, and an absent or expired token is refused"
        <| withHost (fun client -> async {
            let jar = BrowserJar()
            let! landed = serverSignIn client jar "https"

            let setCookie =
                landed.Headers.GetValues "Set-Cookie"
                |> Seq.find (fun v -> v.StartsWith(AuthSession.CookieName + "="))
                |> _.ToLowerInvariant()

            Expect.stringContains setCookie "httponly" "the session cookie is HttpOnly"
            Expect.stringContains setCookie "secure" "Secure over HTTPS"
            Expect.stringContains setCookie "samesite=lax" "and SameSite=Lax"

            let maxAge =
                setCookie.Split ';'
                |> Array.map _.Trim()
                |> Array.tryPick (fun p ->
                    if p.StartsWith "max-age=" then
                        Some(int (p.Substring 8))
                    else
                        None)
                |> Option.defaultWith (fun () -> failtest "the session cookie carries a Max-Age")

            Expect.isTrue
                (maxAge > 0 && float maxAge <= serverTokenLifetime.TotalSeconds)
                $"its Max-Age ({maxAge}s) is the token's remaining life, no longer"

            let serverToken = (jar.TryGet AuthSession.CookieName |> Option.get).Value
            do! outlive serverToken

            Expect.isNone (jar.TryGet AuthSession.CookieName) "the browser has dropped the expired session cookie"
            let! absent = stream client jar
            Expect.isNone absent "a stream with no token is refused"

            let expired = BrowserJar()
            expired.FromScript(AuthSession.CookieName, serverToken, false)
            let! withExpired = stream client expired
            Expect.isNone withExpired "a stream presenting the expired token is refused"
        })

        testCaseAsync
            "the SPA's sign-in, then the server's in another tab: once the server token expires the SPA's next request restores its cookie"
        <| withHost (fun client -> async {
            let jar = BrowserJar()
            let spa = spaToken ()
            let! reflected = spaStoresToken client jar spa
            Expect.isFalse reflected "with no server cookie the script write lands and nothing is sent"

            let! _ = serverSignIn client jar "http"
            let serverToken = (jar.TryGet AuthSession.CookieName |> Option.get).Value
            Expect.notEqual serverToken spa "the server sign-in replaced the SPA's cookie"

            do! outlive serverToken
            spaReadsToken jar spa

            let! streamAfter = stream client jar
            Expect.equal streamAfter (Some reader) "the stream reconnects on the SPA's token"
        })

        testCaseAsync "the SPA alone keeps its script-written cookie and needs no server round trip"
        <| withHost (fun client -> async {
            let jar = BrowserJar()
            let spa = spaToken ()
            let! reflected = spaStoresToken client jar spa
            Expect.isFalse reflected "no server round trip"
            Expect.isFalse (jar.TryGet AuthSession.CookieName |> Option.get).HttpOnly "the script's own cookie"
            let! streamed = stream client jar
            Expect.equal streamed (Some reader) "the stream authenticates from it"
        })

        testCaseAsync "signing out of the SPA clears a server-held cookie, so the stream is refused afterwards"
        <| withHost (fun client -> async {
            let jar = BrowserJar()
            let! _ = serverSignIn client jar "http"
            let spa = spaToken ()
            let! _ = spaStoresToken client jar spa
            let! before = stream client jar
            Expect.equal before (Some reader) "signed in"

            do! spaSignsOut client jar spa

            Expect.isNone (jar.TryGet AuthSession.CookieName) "no auth cookie survives the sign-out"
            let! after = stream client jar
            Expect.isNone after "and the stream is refused"
        })

        testCaseAsync "the server-side cookie write validates what it is given and never takes a token from the URL"
        <| withHost (fun client -> async {
            let post (token: string option) (path: string) = async {
                let request =
                    new HttpRequestMessage(HttpMethod.Post, Uri($"https://localhost{path}"))

                token
                |> Option.iter (fun t -> request.Headers.Add("Authorization", "Bearer " + t))

                use! response = client.SendAsync request |> Async.AwaitTask

                let setCookie =
                    match response.Headers.TryGetValues "Set-Cookie" with
                    | true, values -> values |> Seq.tryFind (fun v -> v.StartsWith(AuthSession.CookieName + "="))
                    | _ -> None

                return int response.StatusCode, setCookie
            }

            let expired = issuer.Force().MintExpiredToken()
            let! noBearer = post None AuthSession.Path
            let! garbage = post (Some "not.a.token") AuthSession.Path
            let! stale = post (Some expired) AuthSession.Path
            let! inQuery = post None $"{AuthSession.Path}?token={spaToken ()}"
            let! valid = post (Some(spaToken ())) AuthSession.Path

            Expect.equal noBearer (400, None) "no bearer: refused, no cookie"
            Expect.equal garbage (401, None) "a token that does not validate: refused, no cookie"
            Expect.equal stale (401, None) "an expired token: refused, no cookie"
            Expect.equal inQuery (400, None) "a token in the query string is not read"

            let status, setCookie = valid
            Expect.equal status 204 "a valid token is written"

            let written =
                setCookie
                |> Option.defaultWith (fun () -> failtest "the cookie is set")
                |> _.ToLowerInvariant()

            Expect.stringContains written "httponly" "HttpOnly"
            Expect.stringContains written "secure" "Secure over HTTPS"
            Expect.stringContains written "samesite=strict" "SameSite=Strict"
        })
    ]

let private decisionTests =
    testList "UserSession.authCookieCarries" [
        testCase "true only when document.cookie holds exactly this token under the SDK name"
        <| fun _ ->
            Expect.isTrue (UserSession.authCookieCarries "toolup-auth-token=abc" "abc") "alone"
            Expect.isTrue (UserSession.authCookieCarries "a=1; toolup-auth-token=abc; b=2" "abc") "among others"
            Expect.isFalse (UserSession.authCookieCarries "" "abc") "an empty document.cookie (an HttpOnly shadow)"
            Expect.isFalse (UserSession.authCookieCarries "toolup-auth-token=old" "abc") "another token"
            Expect.isFalse (UserSession.authCookieCarries "x-toolup-auth-token=abc" "abc") "a longer name"
            Expect.isFalse (UserSession.authCookieCarries "toolup-auth-token=abcd" "abc") "a longer value"
            Expect.isFalse (UserSession.authCookieCarries "toolup-auth-token=" "") "no token held"
    ]

// ─── The browser-only wiring, pinned by text ─────────────────────────
//
// `setAuthToken` / `getAuthToken` / `clearAuthToken` touch `localStorage`
// and `document.cookie`, which the .NET runner cannot reach; the behaviour
// above drives the decision they make, and these pins hold them to making it.

let private userSessionSource () =
    let assembly = System.Reflection.Assembly.GetExecutingAssembly().Location

    let root =
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName assembly, "..", "..", "..", "..", ".."))

    let path =
        Path.Combine(root, "src", "ToolUp.Platform.Client", "Client", "UserSession.fs")

    Expect.isTrue (File.Exists path) $"UserSession.fs at {path}"
    (File.ReadAllText path).Replace("\r\n", "\n")

/// The body of the top-level `let <name>` binding, up to the next one.
let private bindingBody (source: string) (name: string) =
    let start = source.IndexOf $"\nlet {name} "
    Expect.isGreaterThanOrEqual start 0 $"UserSession.{name} exists"
    let next = source.IndexOf("\nlet ", start + 1)

    if next < 0 then
        source.Substring start
    else
        source.Substring(start, next - start)

let private wiringTests =
    testList "UserSession wiring" [
        testCase "setAuthToken sends a dropped default-path write through the server"
        <| fun _ ->
            let body = bindingBody (userSessionSource ()) "setAuthToken"
            Expect.stringContains body "cookieMissing token" "setAuthToken checks its own cookie write"
            Expect.stringContains body "reflectTokenToServer token" "and reflects the token when it did not land"

        testCase "getAuthToken restores a missing default-path cookie without a network call"
        <| fun _ ->
            let body = bindingBody (userSessionSource ()) "getAuthToken"
            Expect.stringContains body "cookieMissing token" "getAuthToken checks the cookie"
            Expect.stringContains body "setAuthCookie" "and re-writes it from script"
            Expect.isFalse (body.Contains "reflectTokenToServer") "and never sends the token anywhere"

        testCase "clearAuthToken clears a cookie the script cannot see"
        <| fun _ ->
            let body = bindingBody (userSessionSource ()) "clearAuthToken"
            Expect.stringContains body "cookieMissing held" "clearAuthToken checks whether the server holds the cookie"
            Expect.stringContains body "clearServerCookie ()" "and asks the server to clear it"

        testCase "the cookie checks read document.cookie only where a document exists"
        <| fun _ ->
            let source = userSessionSource ()

            Expect.stringContains
                source
                "typeof document === 'undefined' ? null : document.cookie"
                "the one cookie read is guarded for a host with no DOM"

            Expect.isFalse
                (source.Contains "authCookieCarries Browser.Dom.document.cookie")
                "no check reads document.cookie unguarded (getAuthToken runs before every stream connect)"
    ]

let tests =
    testList "Phase 1047 — the event streams authenticate whichever sign-in the user took" [
        pipelineTests
        decisionTests
        wiringTests
    ]