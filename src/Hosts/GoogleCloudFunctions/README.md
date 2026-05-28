# ToolUp.Hosts.GoogleCloudFunctions

Google Cloud Functions host adapter for `ToolUp.Platform`. Bridges Functions Framework HTTP invocations through `IServerHost.Invoke` so a compose-built ToolUp deployment runs on Google Cloud Functions (2nd gen / Cloud Run functions) without changes to its handler code.

## Status

Phase 16 host-adapter companion. Structural twin of [`ToolUp.Hosts.AzureFunctions`](../AzureFunctions/README.md) and [`ToolUp.Hosts.AwsLambda`](../AwsLambda/README.md); same `IServerHost.Invoke` seam, simpler bridge — the Functions Framework already hands the consumer an `HttpContext`, so no request/response translation is required.

## Install

```xml
<PackageReference Include="ToolUp.Hosts.GoogleCloudFunctions" />
<PackageReference Include="Google.Cloud.Functions.Hosting" />
```

`Google.Cloud.Functions.Framework` arrives transitively through `ToolUp.Hosts.GoogleCloudFunctions`. The `Hosting` package carries the entry-point startup helpers (`FunctionsStartup`, `--target` parsing); add it as a top-level reference so the consumer's `Program.cs` / equivalent F# entry compiles.

The consumer's `.fsproj` targets `net10.0` and produces a deployable GCF artefact — typically a `dotnet publish` ZIP for 2nd gen functions, or a container image for Cloud Run functions.

## Usage

### 1. Compose the SDK against the serverless shape

```fsharp
open ToolUp.Platform

let serverHost: IServerHost =
    ServerApp.empty
    |> ServerApp.withConfig {
        ServerConfig.defaults with
            Mode = Anonymous
            ServerlessHost = ServerlessHost
            JobScheduler = NoJobScheduler
            Webhooks = NoWebhooks
            Notifications = NoNotificationsExplicit
            AuditLog = NoAuditLog
            UsageMetering = NoUsageMetering
            HealthStateTracking = false
    }
    |> ServerApp.addModule (myModule.register ())
    // The Functions Framework drives `Invoke` per request; do NOT call
    // `ServerApp.run` (which would call `RunBlocking`).
    |> ServerApp.composeOnly
```

`composeOnly` is the low-level entry point that returns the `IServerHost` without calling `RunBlocking()`. (If not yet available in your SDK version, the same shape is reachable by calling `ToolUp.Platform.Server.compose` directly with the positional argument list.)

### 2. Register `IServerHost` in the Functions Framework host

```fsharp
open Google.Cloud.Functions.Hosting
open Microsoft.Extensions.DependencyInjection

type Startup() =
    inherit FunctionsStartup()
    override _.ConfigureServices(_context, services) =
        services.AddSingleton<IServerHost>(serverHost) |> ignore
```

Start the SDK host once at cold-start so any non-Kestrel `IHostedService` registrations fire. Under `ServerlessHost = ServerlessHost` every `IHostedService` is gated off, but ASP.NET Core's internal services (logger factory, config root, options) still need `StartAsync` to run.

```fsharp
do serverHost.Host.StartAsync(System.Threading.CancellationToken.None).Wait()
```

### 3a. Use the built-in `ServerHostFunction` (recommended)

The adapter ships a default `IHttpFunction` that forwards every invocation to `IServerHost.Invoke`. Subclass it in your project so the Functions Framework can auto-discover it (single-`IHttpFunction`-in-assembly auto-detection), and attach your `Startup`:

```fsharp
open ToolUp.Hosts.GoogleCloudFunctions

[<FunctionsStartup(typeof<Startup>)>]
type Function(host: IServerHost) =
    inherit ServerHostFunction(host)
```

The framework resolves the constructor argument from DI; the `Startup` above registered `IServerHost` as a singleton, so `ServerHostFunction` is instantiated automatically per cold-start. Deploy with `FUNCTION_TARGET=YourAssembly.Function` (or `dotnet run --target YourAssembly.Function`).

### 3b. Or write your own `IHttpFunction` and call `bridge` directly

Useful when you need additional per-invocation logic (request-scoped logging, custom auth pre-checks) before forwarding to the SDK pipeline.

```fsharp
open Google.Cloud.Functions.Framework
open ToolUp.Hosts.GoogleCloudFunctions

type Function(host: IServerHost) =
    interface IHttpFunction with
        member _.HandleAsync(ctx: HttpContext) =
            // ... pre-checks ...
            GoogleCloudFunctionHost.bridge (host, ctx)
```

## Translation contract

`GoogleCloudFunctionHost.bridge` is responsible for:

| Direction | What gets translated |
|---|---|
| GCF-provided `HttpContext` → SDK `Invoke` | The framework already provides a fully-populated `HttpContext` — method, URL, headers, body, response writer all owned by the GCF host. The bridge swaps `RequestServices` to `IServerHost.App.Services`, then invokes the SDK pipeline against the same `HttpContext`. |
| SDK `Invoke` completion → GCF response | The SDK pipeline writes the response on `HttpContext.Response`; the Functions Framework reads it back to the cloud after `HandleAsync` returns. No copy required. |

`HttpContext.RequestServices` is rebound to `IServerHost.App.Services` so SDK middleware that resolves DI services per-request (logger lookup, metrics sink, `IConfigStore`, etc.) gets the same singletons DI hands out to long-running Kestrel hosts. The original framework scope is not restored — the `HttpContext` is owned by the framework's per-invocation pipeline and disposed when `HandleAsync` returns.

## Limitations

- **No streaming responses.** 2nd gen Cloud Functions and Cloud Run functions both support arbitrary response bodies, but the underlying ToolUp SDK middleware-graph terminates the response on pipeline completion — SSE endpoints (`/api/notifications`, AI streaming) buffer their full response before flushing. Pair `ServerlessHost = ServerlessHost` with `Notifications = NoNotificationsExplicit` and gate AI-streaming routes off. Cloud Run functions with `--no-cpu-throttling` can support genuine streaming via a dedicated handler that bypasses the bridge.
- **No WebSocket support.** GCF does not surface the WebSocket upgrade handshake.
- **Per-invocation timeout.** 2nd gen functions max at 60 minutes; HTTP-triggered functions max at 60 minutes on Cloud Run functions, 9 minutes on 1st gen. Long-running compute should pair with a sibling worker silo.
- **Single-instance cache locality.** If the SDK is configured with an in-memory store (`InMemoryEventStore`, `InMemoryRateLimitStore`), each function instance has its own cache. Distributed deployments need cloud-backed substrate (`Google.Cloud.Storage.V1` for `IBlobStorage`, Memorystore Redis for rate-limit state, Firestore for `IEventStore`, etc.).
- **Cold-start cost.** First invocation of a cold instance pays the .NET 10 JIT + SDK composition cost. Set `--min-instances` ≥ 1 on the deployment to keep one warm, or accept the cold-start latency for low-traffic deployments.

## Choosing between 1st and 2nd gen / Cloud Run functions

- **2nd gen Cloud Functions** (built on Cloud Run): recommended default — same Functions Framework, longer timeouts, higher concurrency, more memory, Eventarc triggers.
- **Cloud Run functions**: the rebranded 2nd gen surface; identical packaging.
- **1st gen Cloud Functions**: legacy. Still works, but tighter timeout / memory limits; new deployments should target 2nd gen.

The adapter does not distinguish between the three — the Functions Framework runtime abstracts them all.

## Six-rule portability audit

This package is an adapter, not a substrate interface. It does not implement any of the six portability rules directly — it consumes `IServerHost`, which is itself purely an in-process composition seam and exempt from cross-shard portability (the SDK runs in one function instance per invocation; there's no horizontal-scale guarantee to honour).

## License

Apache-2.0. See `LICENSE` at the repo root.
