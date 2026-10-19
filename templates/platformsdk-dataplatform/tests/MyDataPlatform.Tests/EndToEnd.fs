module MyDataPlatform.Tests.EndToEnd

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.AuthProviders
open MyDataPlatform.Server
open MyDataPlatform.Tests.Scripted

// ─── The whole application, end to end, on a loopback port ────────
//
// The synthetic delivery is uploaded through the module's API, both fact
// tables are written, the daily report is generated with every number
// resolving to a fact, and the gated report page refuses an anonymous
// reader while serving one who holds access. Only the model and the
// embedder are replaced; everything else is the composition `Program.fs`
// runs.

/// The local run's identity: forge's dev-only header provider, with a
/// bootstrap team whose owner is the analyst.
[<Literal>]
let private Analyst = "analyst"

let private freePort () =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

/// Completes when the host has started, and stops it on request.
type private Lifetime(lifetime: IHostApplicationLifetime, started: TaskCompletionSource<IHostApplicationLifetime>) =
    interface IHostedService with
        member _.StartAsync _ =
            lifetime.ApplicationStarted.Register(fun () -> started.TrySetResult lifetime |> ignore)
            |> ignore

            Task.CompletedTask

        member _.StopAsync _ = Task.CompletedTask

let private withLifetime (started: TaskCompletionSource<IHostApplicationLifetime>) (app: ServerApp) =
    let register (s: IServiceCollection) =
        s.AddSingleton<IHostedService>(fun sp ->
            Lifetime(sp.GetRequiredService<IHostApplicationLifetime>(), started) :> IHostedService)
        |> ignore

        s

    let ext = app.Extensions

    {
        app with
            Extensions = {
                ext with
                    ServiceConfig =
                        match ext.ServiceConfig with
                        | None -> Some register
                        | Some first -> Some(first >> register)
            }
    }

/// Host the application in a temporary data root and hand `body` a client
/// on its port; the host is stopped whatever `body` does.
let private withHost (body: HttpClient -> unit) =
    let root =
        Path.Combine(Path.GetTempPath(), "dataplatform-e2e-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore

    let port = freePort ()

    // The local run's settings, exactly as `--local` applies them: one team
    // owned by the analyst, secrets encrypted under a fresh key, and the
    // dev-only acknowledgements a header identity needs to boot.
    for name, value in LocalRun.settings (sprintf "http://127.0.0.1:%d" port) do
        Environment.SetEnvironmentVariable(name, value)

    Environment.SetEnvironmentVariable("TOOLUP_AUTH_MODE", null)
    Environment.SetEnvironmentVariable("TOOLUP_INITIAL_TEAM_ID", "trading")
    Environment.SetEnvironmentVariable("TOOLUP_INITIAL_PLATFORM_ADMIN", Analyst)

    Environment.SetEnvironmentVariable(
        "TOOLUP_SECRETS_MASTER_KEY",
        Convert.ToBase64String(Security.Cryptography.RandomNumberGenerator.GetBytes 32)
    )

    ReferenceData.verify ()

    let logger = ConsoleLogger.fromEnv ()

    let config =
        Composition.configure
            {
                ServerConfig.fromEnv logger ServerConfigOverrides.empty with
                    Port = port
            }
            (Path.Combine(AppContext.BaseDirectory, "content"))

    let storage = Companions.blobStorage (Path.Combine(root, "blobs"))
    let secrets = Companions.secretStore (Path.Combine(root, "secrets"))
    let started = TaskCompletionSource<IHostApplicationLifetime>()

    let app =
        Composition.build {
            Config = config
            Storage = storage
            Secrets = secrets
            ProviderProfile = Companions.providerProfile storage
            AIProviderFactory = ScriptedFactory(ScriptedModel())
            Embedder = constantEmbedder
            Auth = AuthProvider.fromEnv logger OidcAuthProvider.fromConfig
            SignIn = None
        }
        |> withLifetime started

    let running =
        Task.Factory.StartNew((fun () -> ServerApp.run app), TaskCreationOptions.LongRunning)

    let first =
        Task.WhenAny(started.Task :> Task, running :> Task, Task.Delay(TimeSpan.FromSeconds 120.0))
        |> Async.AwaitTask
        |> Async.RunSynchronously

    if not (obj.ReferenceEquals(first, started.Task)) then
        if running.IsFaulted then
            raise running.Exception

        failtestf "the host did not start within 120 s (exited: %b)" running.IsCompleted

    use client =
        new HttpClient(
            new HttpClientHandler(AllowAutoRedirect = false, UseCookies = false),
            BaseAddress = Uri(sprintf "http://127.0.0.1:%d" port)
        )

    try
        body client
    finally
        started.Task.Result.StopApplication()
        running.Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private send (client: HttpClient) (request: HttpRequestMessage) (asUser: string option) =
    asUser |> Option.iter (fun user -> request.Headers.Add("X-User-Id", user))

    let response = client.Send request
    let text = response.Content.ReadAsStringAsync().Result
    response.StatusCode, text

let private page (client: HttpClient) (path: string) (asUser: string option) =
    let request = new HttpRequestMessage(HttpMethod.Get, path)
    request.Headers.Accept.ParseAdd "text/html"
    send client request asUser

let private upload (client: HttpClient) (fileName: string) (csv: string) =
    let request =
        new HttpRequestMessage(
            HttpMethod.Post,
            "/api/DailyTradeApi/Upload",
            Content =
                new StringContent(
                    JsonSerializer.Serialize [| {| FileName = fileName; Csv = csv |} |],
                    Encoding.UTF8,
                    "application/json"
                )
        )

    send client request (Some Analyst)

let private syntheticDelivery () =
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "daily-trade.synthetic.csv"))

let tests =
    testSequenced
    <| testList "the application, end to end" [
        testCase "a delivery becomes facts and a grounded report, served only to its readers"
        <| fun () ->
            withHost (fun client ->
                let status, body = upload client "daily-trade.synthetic.csv" (syntheticDelivery ())
                Expect.equal status HttpStatusCode.OK (sprintf "the upload is answered: %s" body)

                let result = JsonDocument.Parse(body).RootElement.GetProperty "Result"

                Expect.isTrue
                    (result.GetProperty("Accepted").GetBoolean())
                    (sprintf "the synthetic delivery is accepted: %s" body)

                let tables =
                    result.GetProperty("Tables").EnumerateArray()
                    |> Seq.map (fun t -> t.GetProperty("TableId").GetString(), t.GetProperty("Rows").GetInt32())
                    |> Map.ofSeq

                Expect.equal
                    tables
                    (Map [ "trade.channel", 12; "trade.site", 4 ])
                    "both fact tables are written, one row per subject per day"

                let narratives =
                    result.GetProperty("Narratives").EnumerateArray()
                    |> Seq.map _.GetString()
                    |> List.ofSeq

                Expect.exists
                    narratives
                    (fun n -> n.StartsWith "daily-trading-report: published")
                    (sprintf "the daily report is published with every figure grounded: %A" narratives)

                let anonymous, anonymousBody = page client "/insights/latest" None
                Expect.equal anonymous HttpStatusCode.Unauthorized "an anonymous reader is refused the report"

                Expect.isFalse (anonymousBody.Contains "Headline by site") "and is shown none of it"

                let reader, readerBody = page client "/insights/latest" (Some Analyst)
                Expect.equal reader HttpStatusCode.OK "a reader with access is served the report"
                Expect.stringContains readerBody "Headline by site" "the report's sections are rendered"
                Expect.stringContains readerBody "uk-web" "with the model's grounded paragraph")

        testCase "a malformed delivery is rejected whole, naming every problem"
        <| fun () ->
            withHost (fun client ->
                let csv =
                    "Date,Site,Channel,Orders,Shoppers,Revenue,Units\n2026-01-12,uk-web,direct,-4,10,10.00,3\n1999-01-01,uk-web,search,1,1,1.00,1\n"

                let status, body = upload client "broken.csv" csv
                Expect.equal status HttpStatusCode.OK "the upload is answered"

                let result = JsonDocument.Parse(body).RootElement.GetProperty "Result"
                Expect.isFalse (result.GetProperty("Accepted").GetBoolean()) "nothing is written"

                let problems =
                    result.GetProperty("Problems").EnumerateArray()
                    |> Seq.map _.GetString()
                    |> List.ofSeq

                Expect.equal problems.Length 2 (sprintf "both bad lines are named: %A" problems))
    ]