# ToolUp.Hosts.AzureFunctions

Azure Functions Worker host adapter for `ToolUp.Platform`. Bridges Azure Functions HTTP triggers through `IServerHost.Invoke` so a compose-built ToolUp deployment runs on Azure Functions Consumption / Premium plans without changes to its handler code.

## Status

Phase 16 reference adapter. Demonstrates the `IServerHost` seam end-to-end against Azure Functions Worker. AWS Lambda and Google Cloud Functions adapters follow the same pattern.

## Install

```xml
<PackageReference Include="ToolUp.Hosts.AzureFunctions" />
<PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" />
```

The consumer's `.fsproj` should target `net10.0` and use the Functions Worker SDK so the project produces a Functions deployment artefact (`host.json`, `local.settings.json`, etc.).

## Usage

### 1. Compose the SDK against the serverless shape

```fsharp skip=fragment
open ToolUp.Platform

let serverHost: IServerHost =
    ServerApp.empty
    |> ServerApp.withConfig {
        ServerConfig.defaults with
            Surfaces = Surfaces.anonymous
            ServerlessHost = ServerlessHost
            JobScheduler = NoJobScheduler
            Webhooks = NoWebhooks
            Notifications = NoNotificationsExplicit
            AuditLog = NoAuditLog
            UsageMetering = NoUsageMetering
            HealthStateTracking = false
    }
    |> ServerApp.addModule (myModule.register ())
    // The Functions worker drives `Invoke` per request; do NOT call
    // `ServerApp.run` (which would call `RunBlocking`).
    |> composeWithoutRunning
```

`composeWithoutRunning` is **your own** one-liner, not an SDK member: `ServerApp` ships no compose-only entry point, because `ServerApp.run` composes and then calls `RunBlocking()`. Write it over `ToolUp.Platform.Server.compose`, passing the positional argument list, and return the `IServerHost` without starting the blocking host.

### 2. Register `IServerHost` in the Functions Worker host

```fsharp skip=fragment
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

let host =
    Host
        .CreateDefaultBuilder()
        .ConfigureFunctionsWorkerDefaults()
        .ConfigureServices(fun services ->
            services.AddSingleton<IServerHost>(serverHost) |> ignore)
        .Build()

// Start the SDK host once at cold-start so any IHostedService that's
// still registered (the SDK gates everything off under
// ServerlessHost = ServerlessHost, but ASP.NET Core's internal services
// — logger factory, config root, options — still need StartAsync to run).
serverHost.Host.StartAsync(System.Threading.CancellationToken.None).Wait()

host.Run()
```

### 3. Write one catchall HTTP-trigger function

```fsharp
open Microsoft.Azure.Functions.Worker
open Microsoft.Azure.Functions.Worker.Http
open ToolUp.Hosts.AzureFunctions
open ToolUp.Platform

type CatchallFunction(host: IServerHost) =
    [<Function("Catchall")>]
    member _.Run
        ([<HttpTrigger(AuthorizationLevel.Anonymous,
                       "get", "post", "put", "delete", "patch", "options",
                       Route = "{*path}")>]
         req: HttpRequestData)
        =
        AzureFunctionHost.bridge (host, req)
```

The `{*path}` catchall forwards every request URL through `IServerHost.Invoke`. The compose-registered Giraffe router, scope resolver, auth enforcement, and module handlers all run unmodified.

## Translation contract

`AzureFunctionHost.bridge` is responsible for:

| Direction | What gets translated |
|---|---|
| `HttpRequestData` → `HttpContext` (request side) | Method, Scheme, Host, Path, QueryString, PathBase, Headers (multi-value), Body stream |
| `HttpContext` (response side) → `HttpResponseData` | StatusCode, Headers (multi-value), Body stream |

Cookies round-trip through the standard `Cookie` and `Set-Cookie` headers. The Functions runtime serialises the populated `HttpResponseData` back to the cloud invocation response.

`HttpContext.RequestServices` is populated from `IServerHost.App.Services` so SDK middleware that resolves DI services per-request (logger lookup, metrics sink, `IConfigStore`, etc.) gets the same singletons DI hands out to long-running Kestrel hosts.

## Limitations

- **No streaming responses.** The bridge buffers the full response body in a `MemoryStream` before flushing to `HttpResponseData`. SSE endpoints (`/api/notifications`, AI streaming) won't work — pair `ServerlessHost = ServerlessHost` with `Notifications = NoNotificationsExplicit` and gate AI-streaming routes off.
- **No WebSocket support.** Functions HTTP triggers don't expose the WebSocket upgrade handshake.
- **No long-running connections.** Azure Functions enforces a per-invocation timeout (10 min default on Premium, 5 min on Consumption). Long-running compute should pair with a sibling worker silo.
- **Single-instance cache locality.** If the SDK is configured with an in-memory store (`InMemoryEventStore`, `InMemoryRateLimitStore`), each Functions instance has its own cache. Distributed deployments need cloud-backed substrate (`Azure.Storage.Blobs` for `IBlobStorage`, Redis or Azure Table Storage for rate-limit state, etc.).

## Six-rule portability audit

This package is an adapter, not a substrate interface. It does not implement any of the six portability rules directly — it consumes `IServerHost`, which is itself purely an in-process composition seam and exempt from cross-shard portability (the SDK runs in one Functions instance per invocation; there's no horizontal-scale guarantee to honour).

## License

Apache-2.0. See `LICENSE` at the repo root.
