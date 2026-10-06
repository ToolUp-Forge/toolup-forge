module ToolUp.Platform.Tests.InProcess.OidcSignInSpaPrecedenceTests

// Phase 992 — interactive sign-in's routes are reachable whenever a client
// bundle ships.
//
// Phase 988 found that `SpaFallbackMiddleware` answered every extensionless
// non-`/api` GET with `PublicPath/index.html` whenever a client bundle was
// present, and fixed it with `SpaFallbackPrecedence.RouterFirst`, which
// PublicRendering's composer registers. The interactive SSR sign-in serves
// extensionless GETs of its own (`/auth/sign-in`, `/auth/callback`), so a
// deployment composing it WITHOUT PublicRendering had both routes shadowed
// by the shell. These cases host exactly that composition — a shipped
// bundle, interactive sign-in, no PublicRendering — through the SDK's own
// terminal, `ServerApp.run`, on a loopback port, so the pipeline order under
// test is the real one.

open System
open System.IO
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth

let private shellMarker = "SPA-SHELL-992"

let private writeFile (path: string) (contents: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, contents)

let private mkDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-signin-spa-tests", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

/// Captures the host's lifetime once it has started, so the test can stop
/// the host `ServerApp.run` blocks on.
type private LifetimeCapture
    (
        lifetime: Microsoft.Extensions.Hosting.IHostApplicationLifetime,
        sink: Threading.Tasks.TaskCompletionSource<Microsoft.Extensions.Hosting.IHostApplicationLifetime>
    ) =
    interface Microsoft.Extensions.Hosting.IHostedService with
        member _.StartAsync _ =
            lifetime.ApplicationStarted.Register(fun () -> sink.TrySetResult lifetime |> ignore)
            |> ignore

            Threading.Tasks.Task.CompletedTask

        member _.StopAsync _ = Threading.Tasks.Task.CompletedTask

let private freePort () =
    let listener = Net.Sockets.TcpListener(Net.IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> Net.IPEndPoint).Port
    listener.Stop()
    port

let private quietLogger: ILogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private issuer =
    lazy (MockOidcServer.start MockOidcServer.MockOidcConfig.defaults)

let private signInConfig (port: int) =
    let s = issuer.Force()

    ToolUp.AuthProviders.OidcSsrSignIn.defaults
        $"{s.IssuerUrl}/authorize"
        $"{s.IssuerUrl}/token"
        "spa-precedence-client"
        $"http://127.0.0.1:{port}/auth/callback"

/// The composition under test: an identity provider, a shipped client
/// bundle, the interactive SSR sign-in — and no PublicRendering.
let private signInOnlyApp (port: int) (publicDir: string) (extensions: ComposeExtensions) =
    let s = issuer.Force()

    let config = {
        ServerConfig.defaults with
            Port = port
            PublicPath = publicDir
            TrustForwardedHeaders = false
            SseAuthMode = CookieRequired
            AcceptPlaintextSecretsWhenAuthRequired = true
            AcceptSameSiteOnlyCsrfWhenAuthRequired = true
    }

    let auth =
        ToolUp.AuthProviders.OidcAuthProvider.fromConfigWith (new Net.Http.HttpClient()) None {
            Issuer = Some s.IssuerUrl
            Audience = None
            KeySource = JwksDiscovery s.IssuerUrl
            TokenLocation = BearerOrCookie AuthSession.CookieName
            ClockSkewSeconds = None
            AcceptedAlgorithms = None
            PreferOidWhenPresent = None
            ClaimMapping = None
        }

    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withExtensions extensions
    |> ServerApp.withStorage (ToolUp.Platform.Testing.Fakes.standardBlobStorage ())
    |> ServerApp.withLogger quietLogger
    |> ServerApp.withAuth auth
    |> ToolUp.AuthProviders.OidcSsrSignIn.withInteractiveSignIn (new Net.Http.HttpClient()) (signInConfig port)

/// Host the composition and hand `body` a non-redirecting client on its
/// port; the host is stopped, and the process-wide culture `compose` sets
/// is restored, whatever `body` does.
let private withHost (body: Net.Http.HttpClient -> Async<unit>) = async {
    let publicDir = mkDir ()
    writeFile (Path.Combine(publicDir, "index.html")) ("<!doctype html><html><body>" + shellMarker + "</body></html>")
    writeFile (Path.Combine(publicDir, "main.js")) "console.log('bundle-992')"
    let port = freePort ()

    let started =
        Threading.Tasks.TaskCompletionSource<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(
            Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    let capture: ComposeExtensions = {
        ComposeExtensions.empty with
            ServiceConfig =
                Some(fun services ->
                    services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(fun sp ->
                        LifetimeCapture(
                            sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(),
                            started
                        )
                        :> Microsoft.Extensions.Hosting.IHostedService)
                    |> ignore

                    services)
    }

    let app = signInOnlyApp port publicDir capture
    let culture = Globalization.CultureInfo.DefaultThreadCurrentCulture
    let uiCulture = Globalization.CultureInfo.DefaultThreadCurrentUICulture

    let running =
        Threading.Tasks.Task.Factory.StartNew(
            (fun () -> ServerApp.run app),
            Threading.Tasks.TaskCreationOptions.LongRunning
        )

    try
        let! first =
            Threading.Tasks.Task.WhenAny(
                started.Task :> Threading.Tasks.Task,
                running :> Threading.Tasks.Task,
                Threading.Tasks.Task.Delay(TimeSpan.FromSeconds 60.0)
            )
            |> Async.AwaitTask

        if not (obj.ReferenceEquals(first, started.Task)) then
            if running.IsFaulted then
                raise running.Exception.InnerException

            failtest "the composed host did not start"

        use handler =
            new Net.Http.HttpClientHandler(AllowAutoRedirect = false, UseCookies = false)

        use client =
            new Net.Http.HttpClient(handler, BaseAddress = Uri $"http://127.0.0.1:{port}")

        do! body client
    finally
        if started.Task.IsCompletedSuccessfully then
            started.Task.Result.StopApplication()
            running.Wait(TimeSpan.FromSeconds 30.0) |> ignore

        Globalization.CultureInfo.DefaultThreadCurrentCulture <- culture
        Globalization.CultureInfo.DefaultThreadCurrentUICulture <- uiCulture
}

/// A browser navigation: (status, body, Location).
let private navigate (client: Net.Http.HttpClient) (path: string) = async {
    use request = new Net.Http.HttpRequestMessage(Net.Http.HttpMethod.Get, path)
    request.Headers.Add("Accept", "text/html")
    use! response = client.SendAsync request |> Async.AwaitTask
    let! text = response.Content.ReadAsStringAsync() |> Async.AwaitTask

    let location =
        match response.Headers.Location with
        | null -> None
        | l -> Some(string l)

    return int response.StatusCode, text, location
}

let tests =
    testList "Phase 992 — interactive sign-in's routes precede the SPA shell" [

        testCase "register arms router-first precedence, idempotently beside another composer"
        <| fun _ ->
            let services = ServiceCollection() :> IServiceCollection
            let config = signInConfig 5000

            let registrations () =
                services
                |> Seq.filter (fun d -> d.ServiceType = typeof<SpaFallbackPrecedence>)
                |> Seq.length

            ToolUp.AuthProviders.OidcSsrSignIn.register services config |> ignore

            Expect.equal (registrations ()) 1 "registering interactive sign-in registers a precedence"

            do
                use provider = services.BuildServiceProvider()

                Expect.equal
                    (provider.GetRequiredService<SpaFallbackPrecedence>())
                    RouterFirst
                    "and it is router-first: registering interactive sign-in is sufficient"

            // PublicRendering's composer arms the same precedence; composing
            // both leaves one registration, not two.
            SpaFallbackPrecedence.useRouterFirst services |> ignore
            ToolUp.AuthProviders.OidcSsrSignIn.register services config |> ignore
            Expect.equal (registrations ()) 1 "composing both layers leaves exactly one registration"

        testCaseAsync
            "with a client bundle shipped and no PublicRendering, the sign-in routes are answered by their handlers"
        <| withHost (fun client -> async {
            let s = issuer.Force()

            let! signIn, signInBody, signInLocation = navigate client "/auth/sign-in?returnUrl=%2Freports"
            let! callback, callbackBody, _ = navigate client "/auth/callback?code=abc&state=xyz"
            let! deep, deepBody, _ = navigate client "/workspace/anything"
            let! asset, assetBody, _ = navigate client "/main.js"

            Expect.equal signIn 302 "GET /auth/sign-in is answered by the sign-in handler"
            Expect.isFalse (signInBody.Contains shellMarker) "not by the SPA shell"

            Expect.isTrue
                (signInLocation
                 |> Option.exists (fun l -> l.StartsWith(s.IssuerUrl + "/authorize?")))
                "and redirects the browser to the issuer"

            // No state cookie was sealed for this browser, so the handler's
            // fail-closed path answers; the shell would have answered 200.
            Expect.equal callback 400 "GET /auth/callback is answered by the callback handler"
            Expect.stringContains callbackBody "was not started here" "with its own refusal"
            Expect.isFalse (callbackBody.Contains shellMarker) "not by the SPA shell"

            // The SPA keeps the paths nothing on the server answers.
            Expect.equal deep 200 "an unrouted SPA deep link is served"
            Expect.stringContains deepBody shellMarker "with the client shell"
            Expect.equal asset 200 "a bundle asset is served"
            Expect.stringContains assetBody "bundle-992" "from PublicPath"
        })
    ]