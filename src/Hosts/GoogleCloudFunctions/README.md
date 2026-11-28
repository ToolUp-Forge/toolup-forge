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

```fsharp skip=fragment
open ToolUp.Platform
open ToolUp.Platform.Compose

let config = {
    ServerConfig.defaults with
        Surfaces = Surfaces.anonymous
        ServerlessHost = ServerlessHost
        JobScheduler = NoJobScheduler
        Webhooks = FeatureMode.Disabled
        Notifications = NoNotificationsExplicit
        AuditLog = FeatureMode.Disabled
        UsageMetering = FeatureMode.Disabled
        Observability = {
            ServerConfig.defaults.Observability with
                HealthStateTracking = false
        }
}

// The Functions Framework drives `Invoke` per request; do NOT call
// `ServerApp.run` (which would call `RunBlocking`).
let plan = Server.CompositionPlan.ofConfig config
let serverHost: IServerHost = Server.compose { plan with Handlers = myHandlers }
```

`ServerApp` ships no compose-only entry point: `ServerApp.run` composes and then calls `RunBlocking()`, and the `CompositionPlan` it builds on the way is not returned. The one call that returns an `IServerHost` without running it is `ToolUp.Platform.Compose.Server.compose`, which takes one `CompositionPlan` record. `Server.CompositionPlan.ofConfig` gives a plan that composes the configuration and nothing else; set `Handlers` to the deployment's Giraffe handlers, and any other registration field the deployment needs, on that record. A plan composed this way gets nothing `ServerApp.run` adds around `compose`, such as the composition-inspector route.

### 2. Register `IServerHost` in the Functions Framework host

```fsharp skip=fragment
open Google.Cloud.Functions.Hosting
open Microsoft.Extensions.DependencyInjection

type Startup() =
    inherit FunctionsStartup()
    override _.ConfigureServices(_context, services) =
        services.AddSingleton<IServerHost>(serverHost) |> ignore
```

Start the SDK host once at cold-start so any non-Kestrel `IHostedService` registrations fire. Under `ServerlessHost = ServerlessHost` every `IHostedService` is gated off, but ASP.NET Core's internal services (logger factory, config root, options) still need `StartAsync` to run.

```fsharp skip=fragment
do serverHost.Host.StartAsync(System.Threading.CancellationToken.None).Wait()
```

### 3a. Use the built-in `ServerHostFunction` (recommended)

The adapter ships a default `IHttpFunction` that forwards every invocation to `IServerHost.Invoke`. Subclass it in your project so the Functions Framework can auto-discover it (single-`IHttpFunction`-in-assembly auto-detection), and attach your `Startup`:

```fsharp skip=fragment
open ToolUp.Hosts.GoogleCloudFunctions

[<FunctionsStartup(typeof<Startup>)>]
type Function(host: IServerHost) =
    inherit ServerHostFunction(host)
```

The framework resolves the constructor argument from DI; the `Startup` above registered `IServerHost` as a singleton, so the function is instantiated automatically per cold-start.

The Functions Framework looks the target up in the function's own assembly (the one its generated entry point starts), by the type's full CLR name. `FUNCTION_TARGET` (or `dotnet run --target …`) therefore names your subclass, never `ServerHostFunction`, which lives in this package's assembly. A type `Function` in namespace `MyFunctions` is `MyFunctions.Function`; a type declared inside an F# module `Functions` is a nested type, `Functions+Function`. When the assembly holds exactly one function type, the framework finds it with no target set.

### 3b. Or write your own `IHttpFunction` and call `bridge` directly

Useful when you need additional per-invocation logic (request-scoped logging, custom auth pre-checks) before forwarding to the SDK pipeline.

```fsharp skip=fragment
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
