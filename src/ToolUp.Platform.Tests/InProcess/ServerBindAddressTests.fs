module ToolUp.Platform.Tests.InProcess.ServerBindAddressTests

// ─── Phase 1000 — a forge server binds the address it is configured to ──
//
// Until 0.26.0 the host builder hard-coded `UseUrls("http://0.0.0.0:<port>")`,
// so every forge server listened on every interface and `ASPNETCORE_URLS` had
// no effect. These cases pin the replacement:
//
//   * the precedence — Kestrel endpoints > the `urls` host setting
//     (`ASPNETCORE_URLS`) > `SERVER_BIND_ADDRESS` > `ServerConfig.BindAddress`
//     > loopback `127.0.0.1` — as a pure resolution, and on a REAL listener
//     for the loopback default, a configured Kestrel endpoint and the `urls`
//     setting (every server these cases start binds 127.0.0.1: the
//     all-interfaces case is asserted on the resolved plan, never bound, so
//     the pack raises no firewall prompt and exposes nothing);
//   * the preflight — the DEFAULT loopback under a deployed signal is an
//     Error naming `SERVER_BIND_ADDRESS=0.0.0.0`, an EXPLICIT loopback there
//     is a Warning, and a local run is Ok — and that compose registers it.
//
// Env vars are process-global, so the list is `testSequenced` and every case
// restores what it touches.

open System
open System.IO
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ServerBinding
open ToolUp.Platform.TransientFault

// ─── Helpers ─────────────────────────────────────────────────────────

let private withEnv (name: string) (value: string option) (body: unit -> 'a) : 'a =
    let saved = Environment.GetEnvironmentVariable name

    try
        Environment.SetEnvironmentVariable(name, Option.toObj value)
        body ()
    finally
        Environment.SetEnvironmentVariable(name, saved)

/// Every variable the listen plan or the deployed-posture signals read,
/// cleared, so a case states each one it depends on.
let private withCleanBindEnv (body: unit -> 'a) : 'a =
    withEnv "ASPNETCORE_URLS" None (fun () ->
        withEnv "DOTNET_URLS" None (fun () ->
            withEnv BindAddressEnvVar None (fun () ->
                withEnv "SERVER_PORT" None (fun () ->
                    withEnv "DOTNET_RUNNING_IN_CONTAINER" None (fun () ->
                        withEnv "KUBERNETES_SERVICE_HOST" None body)))))

let private configOf (pairs: (string * string) list) : IConfiguration =
    ConfigurationBuilder()
        .AddInMemoryCollection(pairs |> List.map (fun (k, v) -> Collections.Generic.KeyValuePair(k, v)))
        .Build()
    :> IConfiguration

let private noEnv (_: string) : string option = None

let private envOf (pairs: (string * string) list) (name: string) : string option =
    pairs |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

let private validate (plan: ListenPlan option) (signals: string list) =
    (ServerBindsConfiguredAddressValidator(plan, signals) :> ConfigValidation.IConfigValidator).Validate()
    |> Async.RunSynchronously

/// Build a web app from a fresh `WebApplicationBuilder`, let `configure`
/// stamp its configuration, apply the plan exactly as compose does, start
/// it, and return the plan and the addresses the server actually bound.
let private bindAndRead
    (configure: WebApplicationBuilder -> unit)
    (bindAddress: string option)
    : ListenPlan * string list =
    let b = WebApplication.CreateBuilder()
    configure b
    let plan = ComposeBootstrap.listenPlanFor b bindAddress "0"
    ComposeBootstrap.applyListenPlan b plan
    let app = b.Build()
    app.MapGet("/", Func<string>(fun () -> "ok")) |> ignore
    app.StartAsync().GetAwaiter().GetResult()

    try
        let addresses =
            app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
            |> List.ofSeq

        plan, addresses
    finally
        app.StopAsync().GetAwaiter().GetResult()
        (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()

let private allLoopback127 (addresses: string list) =
    not addresses.IsEmpty
    && addresses |> List.forall (fun a -> a.StartsWith "http://127.0.0.1:")

// ─── Compose: the preflight is registered and run ─────────────────────

type private CapturingLogger() =
    let lines = Collections.Concurrent.ConcurrentQueue<string>()
    member _.Lines = List.ofSeq lines

    interface ILogger with
        member _.Debug m = lines.Enqueue m
        member _.Info m = lines.Enqueue m
        member _.Warn m = lines.Enqueue m
        member _.Error(m, _) = lines.Enqueue m

/// Compose the default anonymous app exactly as `ServerApp.run` does, in a
/// scratch working directory, and return what the preflight said about the
/// listen address. Compose builds the host and never starts it, so nothing
/// binds a port.
let private composedVerdict (config: ServerConfig) : string * string list =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-1000-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    let saved = Directory.GetCurrentDirectory()
    let logger = CapturingLogger()

    try
        Directory.SetCurrentDirectory dir

        try
            ToolUp.Platform.Server.compose
                []
                []
                config
                None
                ComposeExtensions.empty
                (Some(logger :> ILogger))
                (Some(
                    ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage.InMemoryBlobStorage()
                    :> BlobStorage.IBlobStorage
                ))
                None
                []
                []
                []
                []
                []
                None
                []
                None
                []
                []
                None
                []
                None
                []
                []
                None
                None
                []
                []
                []
                []
                []
                NoResilience
                NoResilience
                None
            |> ignore
        with :? ConfigValidatorAggregator.ConfigPreflightFailedException ->
            ()
    finally
        Directory.SetCurrentDirectory saved

        try
            Directory.Delete(dir, true)
        with _ ->
            ()

    let prefix = "[preflight] server-bind-address: "

    match logger.Lines |> List.filter (fun l -> l.StartsWith prefix) with
    | [ line ] -> line.Substring(prefix.Length).Split(' ').[0], logger.Lines
    | [] -> failtest "the composed preflight never ran server-bind-address"
    | many -> failtestf "the composed preflight ran server-bind-address %d times" many.Length

[<Tests>]
let tests =
    testSequenced
    <| testList "Phase 1000 — a forge server binds the address it is configured to" [

        // ── The default ──────────────────────────────────────────────
        test "ServerConfig.defaults leaves the bind address unset (the loopback default)" {
            Expect.isNone ServerConfig.defaults.BindAddress "unset means 127.0.0.1"
            Expect.equal DefaultBindAddress "127.0.0.1" "the default is loopback"
        }

        // ── Parsing ──────────────────────────────────────────────────
        test "parseBindAddress accepts IP literals and localhost" {
            for raw, expected in
                [
                    "127.0.0.1", "127.0.0.1"
                    "0.0.0.0", "0.0.0.0"
                    "::", "::"
                    "[::1]", "::1"
                    " 10.1.2.3 ", "10.1.2.3"
                    "LocalHost", "localhost"
                ] do
                Expect.equal (parseBindAddress raw) (Ok expected) (sprintf "%s is a bind address" raw)
        }

        test "parseBindAddress refuses a bare integer, a hostname and an empty value, naming the setting" {
            for raw in [ "1"; "example.com"; ""; "0.0.0"; "*" ] do
                match parseBindAddress raw with
                | Ok v -> failtestf "%s must be refused, parsed as %s" raw v
                | Error reason ->
                    Expect.stringContains reason "SERVER_BIND_ADDRESS" "the refusal names the setting"
                    Expect.stringContains reason "0.0.0.0" "the refusal names the all-interfaces spelling"
        }

        test
            "configuredBindAddress: SERVER_BIND_ADDRESS wins over ServerConfig.BindAddress, which wins over the default" {
            let cfg = {
                ServerConfig.defaults with
                    BindAddress = Some "10.0.0.5"
            }

            Expect.equal (configuredBindAddress cfg (Some "0.0.0.0")) (Some "0.0.0.0") "env wins"
            Expect.equal (configuredBindAddress cfg None) (Some "10.0.0.5") "config when env is unset"
            Expect.equal (configuredBindAddress cfg (Some " ")) (Some "10.0.0.5") "a blank env is unset"
            Expect.isNone (configuredBindAddress ServerConfig.defaults None) "neither: the default"

            Expect.throws
                (fun () -> configuredBindAddress ServerConfig.defaults (Some "not-an-address") |> ignore)
                "an unparseable address fails loud"
        }

        // ── Precedence, resolved ─────────────────────────────────────
        test "resolve: nothing configured binds loopback 127.0.0.1 on the port" {
            let plan = resolve (configOf []) None "5000"
            Expect.equal plan.Source DefaultLoopback "default"
            Expect.equal plan.Urls [ "http://127.0.0.1:5000" ] "loopback on the port"
        }

        test "resolve: a configured all-interfaces address binds every interface (asserted, never bound)" {
            let v4 = resolve (configOf []) (Some "0.0.0.0") "5000"
            Expect.equal v4.Source ConfiguredBindAddress "configured"
            Expect.equal v4.Urls [ "http://0.0.0.0:5000" ] "all IPv4 interfaces"

            let v6 = resolve (configOf []) (Some "::") "8080"
            Expect.equal v6.Urls [ "http://[::]:8080" ] "an IPv6 literal is bracketed"
        }

        test "resolve: the urls host setting (ASPNETCORE_URLS) overrides forge's address" {
            let plan =
                resolve (configOf [ "urls", "http://+:5000;http://localhost:5001" ]) (Some "127.0.0.1") "9000"

            Expect.equal plan.Source HostUrls "urls wins over SERVER_BIND_ADDRESS"
            Expect.equal plan.Urls [ "http://+:5000"; "http://localhost:5001" ] "each url, split"
        }

        test "resolve: a Kestrel endpoint overrides the urls setting and forge's address" {
            let plan =
                resolve
                    (configOf [
                        "Kestrel:Endpoints:Http:Url", "http://127.0.0.1:7001"
                        "urls", "http://+:5000"
                    ])
                    (Some "0.0.0.0")
                    "9000"

            Expect.equal plan.Source KestrelEndpoints "Kestrel endpoints win"
            Expect.equal plan.Urls [ "http://127.0.0.1:7001" ] "the endpoint's url"
        }

        test "isLoopbackUrl tells loopback from every-interface hosts" {
            for url in
                [
                    "http://127.0.0.1:5000"
                    "http://localhost:5000"
                    "https://[::1]:443"
                    "http://127.0.0.2:1"
                ] do
                Expect.isTrue (isLoopbackUrl url) (sprintf "%s is loopback" url)

            for url in
                [
                    "http://0.0.0.0:5000"
                    "http://[::]:5000"
                    "http://+:5000"
                    "http://*:5000"
                    "http://app.internal:80"
                ] do
                Expect.isFalse (isLoopbackUrl url) (sprintf "%s is not loopback" url)
        }

        // ── Precedence, on a real listener (127.0.0.1 only) ──────────
        test "a real server with nothing configured listens on 127.0.0.1 only" {
            withCleanBindEnv (fun () ->
                let plan, addresses = bindAndRead ignore None
                Expect.equal plan.Source DefaultLoopback "default"
                Expect.isTrue (allLoopback127 addresses) (sprintf "bound %A" addresses))
        }

        test "a real server honours a configured Kestrel endpoint over SERVER_BIND_ADDRESS=0.0.0.0" {
            withCleanBindEnv (fun () ->
                let plan, addresses =
                    bindAndRead
                        (fun b -> b.Configuration["Kestrel:Endpoints:Http:Url"] <- "http://127.0.0.1:0")
                        (Some "0.0.0.0")

                Expect.equal plan.Source KestrelEndpoints "the endpoint decided"
                Expect.isTrue (allLoopback127 addresses) (sprintf "the endpoint bound, not 0.0.0.0: %A" addresses))
        }

        test "a real server honours ASPNETCORE_URLS over SERVER_BIND_ADDRESS=0.0.0.0" {
            withCleanBindEnv (fun () ->
                withEnv "ASPNETCORE_URLS" (Some "http://127.0.0.1:0") (fun () ->
                    let plan, addresses = bindAndRead ignore (Some "0.0.0.0")
                    Expect.equal plan.Source HostUrls "ASPNETCORE_URLS decided"

                    Expect.isTrue
                        (allLoopback127 addresses)
                        (sprintf "ASPNETCORE_URLS bound, not 0.0.0.0: %A" addresses)))
        }

        // ── Deployed posture ─────────────────────────────────────────
        test "deployedSignals recognises a container, a Kubernetes pod and a replica count; a local run has none" {
            Expect.isEmpty (deployedSignals ServerConfig.defaults noEnv) "local"

            let container =
                deployedSignals ServerConfig.defaults (envOf [ "DOTNET_RUNNING_IN_CONTAINER", "true" ])

            Expect.equal container.Length 1 "container"
            Expect.stringContains container.Head "DOTNET_RUNNING_IN_CONTAINER" "named"

            Expect.isEmpty
                (deployedSignals ServerConfig.defaults (envOf [ "DOTNET_RUNNING_IN_CONTAINER", "false" ]))
                "an explicit false is not a container"

            let pod =
                deployedSignals ServerConfig.defaults (envOf [ "KUBERNETES_SERVICE_HOST", "10.96.0.1" ])

            Expect.stringContains pod.Head "KUBERNETES_SERVICE_HOST" "pod"

            let replicas =
                deployedSignals
                    {
                        ServerConfig.defaults with
                            ReplicaCount = 3
                    }
                    noEnv

            Expect.stringContains replicas.Head "ReplicaCount = 3" "replicas"
        }

        // ── The preflight ────────────────────────────────────────────
        test
            "preflight: the DEFAULT loopback in a container is an Error naming the signal and SERVER_BIND_ADDRESS=0.0.0.0" {
            let plan = resolve (configOf []) None "5000"

            match validate (Some plan) [ "DOTNET_RUNNING_IN_CONTAINER=true (the process runs in a container)" ] with
            | ConfigValidation.ValidationResult.Error message ->
                Expect.stringContains message "SERVER_BIND_ADDRESS=0.0.0.0" "names the setting that fixes it"
                Expect.stringContains message "DOTNET_RUNNING_IN_CONTAINER" "names how it detected a deployment"
                Expect.stringContains message "http://127.0.0.1:5000" "names what it bound"
            | other -> failtestf "expected Error, got %A" other
        }

        test
            "preflight: an EXPLICIT loopback in a deployed posture is a Warning naming the sidecar case and the setting" {
            for plan in
                [
                    resolve (configOf []) (Some "127.0.0.1") "5000"
                    resolve (configOf [ "urls", "http://localhost:5000" ]) None "5000"
                    resolve (configOf [ "Kestrel:Endpoints:Http:Url", "http://127.0.0.1:5000" ]) None "5000"
                ] do
                match
                    validate (Some plan) [ "KUBERNETES_SERVICE_HOST is set (the process runs in a Kubernetes pod)" ]
                with
                | ConfigValidation.ValidationResult.Warning message ->
                    Expect.stringContains message "sidecar" "names the legitimate topology"
                    Expect.stringContains message "SERVER_BIND_ADDRESS=0.0.0.0" "names the setting to change"
                | other -> failtestf "%A: expected Warning, got %A" plan.Source other
        }

        test "preflight: all interfaces in a container, a local loopback run, and a worker are Ok" {
            let container = [ "DOTNET_RUNNING_IN_CONTAINER=true (the process runs in a container)" ]

            for plan, signals, label in
                [
                    Some(resolve (configOf []) (Some "0.0.0.0") "5000"), container, "configured 0.0.0.0 in a container"
                    Some(resolve (configOf [ "urls", "http://+:5000" ]) None "5000"),
                    container,
                    "ASPNETCORE_URLS=http://+:5000 in a container"
                    Some(resolve (configOf []) None "5000"), [], "the loopback default on a local run"
                    None, container, "a WorkerOnly process (no listener)"
                ] do
                Expect.equal (validate plan signals) ConfigValidation.ValidationResult.Ok label
        }

        test
            "compose registers the preflight: the loopback default in a container refuses, SERVER_BIND_ADDRESS=0.0.0.0 passes, a local run passes" {
            withCleanBindEnv (fun () ->
                let refused, _ =
                    withEnv "DOTNET_RUNNING_IN_CONTAINER" (Some "true") (fun () ->
                        composedVerdict ServerConfig.defaults)

                Expect.equal refused "Error" "the default loopback in a container refuses to start"

                let fixedByEnv, _ =
                    withEnv "DOTNET_RUNNING_IN_CONTAINER" (Some "true") (fun () ->
                        withEnv BindAddressEnvVar (Some "0.0.0.0") (fun () -> composedVerdict ServerConfig.defaults))

                Expect.equal fixedByEnv "Ok" "SERVER_BIND_ADDRESS=0.0.0.0 fixes it"

                let local, _ = composedVerdict ServerConfig.defaults
                Expect.equal local "Ok" "a local run is unaffected")
        }
    ]