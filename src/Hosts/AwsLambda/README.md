# ToolUp.Hosts.AwsLambda

AWS Lambda host adapter for `ToolUp.Platform`. Bridges Lambda invocations (API Gateway REST v1, HTTP API v2, ALB, Lambda Function URLs) through `IServerHost.Invoke` so a compose-built ToolUp deployment runs on Lambda without changes to its handler code.

## Status

Phase 16 host-adapter companion. Structural twin of [`ToolUp.Hosts.AzureFunctions`](../AzureFunctions/README.md); same `IServerHost.Invoke` seam, different event-shape translation.

## Install

```xml
<PackageReference Include="ToolUp.Hosts.AwsLambda" />
<PackageReference Include="Amazon.Lambda.RuntimeSupport" />
<PackageReference Include="Amazon.Lambda.Serialization.SystemTextJson" />
```

The consumer's `.fsproj` targets `net10.0` and produces a self-contained Lambda deployment artefact (the `dotnet lambda package` tool or a `dotnet publish` + ZIP step).

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
    // Lambda drives `Invoke` per request; do NOT call
    // `ServerApp.run` (which would call `RunBlocking`).
    |> composeWithoutRunning
```

`composeWithoutRunning` is **your own** one-liner, not an SDK member: `ServerApp` ships no compose-only entry point, because `ServerApp.run` composes and then calls `RunBlocking()`. Write it over `ToolUp.Platform.Server.compose`, passing the positional argument list, and return the `IServerHost` without starting the blocking host.

### 2. Start the host once at cold-start

```fsharp skip=fragment
do serverHost.Host.StartAsync(System.Threading.CancellationToken.None).Wait()
```

ASP.NET Core's internal services (logger factory, config root, options) need a started host. Under `ServerlessHost = ServerlessHost` every `IHostedService` registration is gated off, so `StartAsync` is effectively a no-op — the call still has to happen for the framework internals.

### 3. Write one handler per event source

Pick the bridge matching the integration's payload format. Most new deployments use **HTTP API v2** (or Lambda Function URLs, which share the v2 payload).

#### HTTP API v2 / Lambda Function URLs (recommended)

```fsharp skip=fragment
open Amazon.Lambda.Core
open Amazon.Lambda.APIGatewayEvents
open Amazon.Lambda.RuntimeSupport
open Amazon.Lambda.Serialization.SystemTextJson
open ToolUp.Hosts.AwsLambda

let handler (req: APIGatewayHttpApiV2ProxyRequest) (_ctx: ILambdaContext) =
    AwsLambdaHost.bridgeV2 (serverHost, req)

[<EntryPoint>]
let main _ =
    LambdaBootstrapBuilder
        .Create(handler, DefaultLambdaJsonSerializer())
        .Build()
        .RunAsync()
        .Wait()
    0
```

#### REST API v1

```fsharp skip=fragment
let handler (req: APIGatewayProxyRequest) (_ctx: ILambdaContext) =
    AwsLambdaHost.bridgeV1 (serverHost, req)
```

#### Application Load Balancer

```fsharp skip=fragment
let handler (req: ApplicationLoadBalancerRequest) (_ctx: ILambdaContext) =
    AwsLambdaHost.bridgeAlb (serverHost, req)
```

## Translation contract

`AwsLambdaHost.bridgeV2` / `bridgeV1` / `bridgeAlb` are responsible for:

| Direction | What gets translated |
|---|---|
| Lambda event → `HttpContext` (request side) | Method, Scheme (always `https`), Host, Path, QueryString, Headers, Cookies (v2 dedicated array), Body (base64-decoded when `IsBase64Encoded`) |
| `HttpContext` (response side) → Lambda response | StatusCode, Headers (comma-joined for v2; single + MultiValueHeaders for v1/ALB), Cookies (v2 dedicated array; `Set-Cookie` headers for v1/ALB), Body (text when Content-Type is text/JSON/XML; base64 otherwise) |

`HttpContext.RequestServices` is populated from `IServerHost.App.Services` so SDK middleware that resolves DI services per-request (logger lookup, metrics sink, `IConfigStore`, etc.) gets the same singletons DI hands out to long-running Kestrel hosts.

### Body encoding heuristic

Response bodies are returned as UTF-8 text when the `Content-Type` is:
- `text/*`
- `application/json`, `application/xml`, `application/javascript`
- `application/x-www-form-urlencoded`
- any `*+json` or `*+xml` MIME type

Other content types (binary uploads, images, downloads) are base64-encoded with `IsBase64Encoded = true`. API Gateway / ALB / Function URLs all honour this flag.

## Limitations

- **No streaming responses.** The bridge buffers the full response body in a `MemoryStream` before flushing to the Lambda response. SSE endpoints (`/api/notifications`, AI streaming) won't work over the standard request/response Lambda shape — pair `ServerlessHost = ServerlessHost` with `Notifications = NoNotificationsExplicit` and gate AI-streaming routes off. (Lambda Function URLs with `RESPONSE_STREAM` invoke mode and the `Amazon.Lambda.AspNetCoreServer.Hosting` streaming integration support a separate code-path for this; not covered by this companion's `bridge*` API.)
- **No WebSocket support.** API Gateway has a dedicated WebSocket API (not the HTTP API used here); it requires a different integration shape.
- **Per-invocation timeout.** Lambda functions max at 15 minutes. Long-running compute should pair with a sibling worker silo.
- **Single-instance cache locality.** If the SDK is configured with an in-memory store (`InMemoryEventStore`, `InMemoryRateLimitStore`), each Lambda execution environment has its own cache. Distributed deployments need cloud-backed substrate (`Amazon.S3` for `IBlobStorage`, DynamoDB or ElastiCache Redis for rate-limit state, etc.).
- **Cold-start cost.** First invocation of a cold execution environment pays the .NET 10 JIT + SDK composition cost. Lambda SnapStart (.NET on Lambda) and Provisioned Concurrency mitigate this; see cold-start mitigation notes in the SDK Phase 16 follow-up docs.

## Choosing between bridges

| Use | When |
|---|---|
| `bridgeV2` | API Gateway HTTP API (the modern default) or Lambda Function URLs |
| `bridgeV1` | API Gateway REST API (legacy / per-route-customisation deployments) |
| `bridgeAlb` | Application Load Balancer fronting Lambda (typically internal VPC traffic) |

If unsure, **start with HTTP API v2** — lower cost, lower latency, simpler payload, supported by Function URLs as well.

## Six-rule portability audit

This package is an adapter, not a substrate interface. It does not implement any of the six portability rules directly — it consumes `IServerHost`, which is itself purely an in-process composition seam and exempt from cross-shard portability (the SDK runs in one Lambda execution environment per invocation; there's no horizontal-scale guarantee to honour).

## License

Apache-2.0. See `LICENSE` at the repo root.
