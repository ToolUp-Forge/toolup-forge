# ToolUp.Platform Technical Guide — 12. Hosting Models

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 11. AI Integration & Closing Notes](11-ai-integration-and-closing-notes.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 13. Deployment Shapes →](13-deployment-shapes.md)

---

`SDK.Server.compose` returns an `IServerHost` ([`IServerHost.fs`](../../ToolUp.Platform.Server/Server/IServerHost.fs)) that both the long-running Kestrel default and the three host-adapter companions (Azure Functions, AWS Lambda, Google Cloud Functions) drive through the same seam. Each adapter forwards cloud invocations into `IServerHost.Invoke ctx`; the configured Giraffe router, scope resolver, auth enforcement, and module handlers run unmodified.

This chapter is the reference for picking a host runtime, matching it against the `ServerConfig` shape the deployment needs, and producing the deployment artefact. The matrix below is the central lookup; the worked examples that follow give an end-to-end shape per cloud provider.

## When serverless is appropriate

- **Anonymous mode + no scheduler + no jobs + no webhooks + no real-time notifications.** The "stateless analytical tool" — every request is independent, all state in `IBlobStorage`, no `BackgroundService` work. Public-facing demo deployments fit naturally.
- **Authenticated stateless mode (Individual / Team) with no SSE, no scheduler.** Persistence works via `IBlobStorage` cloud companions; auth via OIDC server-to-server. KB ingestion offloaded to a separate worker (or skipped — read-only KB).
- **Serverless front-door + dedicated worker.** A serverless deployment handles request/response API; a separate Kestrel-hosted worker silo (`ProcessProfile = WorkerOnly`) runs the job scheduler, webhook dispatcher, RAG ingestion. Both halves share the same persistent `IBlobStorage` / `IEventStore` / `IRateLimitStore`; cross-silo coordination uses a distributed `INotificationChannel` (Redis). See the [hybrid worked example](#hybrid-serverless-front-door--kestrel-worker-silo) below for the substrate and env-var contract.

## When serverless is NOT appropriate

- **In-process scheduler / webhook dispatcher / SSE notifications** — all need a long-running process. A serverless deployment either disables them (`JobScheduler = NoJobScheduler` / `Webhooks = NoWebhooks` / `Notifications = NoNotificationsExplicit`) or pairs with a separate worker.
- **Heavy ingestion paths** (RAG embedding, large file extraction) — exceed typical serverless function timeouts. Pair with a worker.
- **Long-lived AI streaming (SSE)** — the three shipped adapters buffer response bodies before flushing, so SSE endpoints (`/api/notifications`, AI streaming) do not stream incrementally. Some serverless runtimes (Lambda Function URLs with `RESPONSE_STREAM` invoke mode; Cloud Run functions with `--no-cpu-throttling`) support streaming via dedicated handlers that bypass the bridge — out of scope for the shipped `bridge*` API.

## Host runtimes

| Runtime | Companion package | Driving mechanism |
|---|---|---|
| Kestrel (default) | _(none — built into `ToolUp.Platform.Server`)_ | `IServerHost.RunBlocking()` calls `app.Run()` |
| Azure Functions Worker | [`ToolUp.Hosts.AzureFunctions`](../../Hosts/AzureFunctions/) | `HttpTrigger` → `AzureFunctionHost.bridge` → `IServerHost.Invoke` |
| AWS Lambda | [`ToolUp.Hosts.AwsLambda`](../../Hosts/AwsLambda/) | Lambda event → `AwsLambdaHost.bridgeV2` / `bridgeV1` / `bridgeAlb` → `IServerHost.Invoke` |
| Google Cloud Functions | [`ToolUp.Hosts.GoogleCloudFunctions`](../../Hosts/GoogleCloudFunctions/) | Functions Framework `IHttpFunction.HandleAsync` → `GoogleCloudFunctionHost.bridge` → `IServerHost.Invoke` |

## Compatibility matrix

Cell vocabulary: ✅ supported · ⚠ supported with caveat · ❌ unsupported. The Kestrel column is today's default behaviour; the serverless columns assume the consumer adds the corresponding `ToolUp.Hosts.*` companion `PackageReference` and follows the worked example below.

### `ServerlessHostMode`

| `ServerConfig.ServerlessHost` | Kestrel | AzureFunctions | AwsLambda | GoogleCloudFunctions |
|---|---|---|---|---|
| `KestrelHost` (default) | ✅ | ❌ — Functions Worker provides its own HTTP listener; Kestrel binding would race | ❌ — Lambda has no port to bind; `app.Run()` blocks indefinitely | ❌ — Functions Framework provides its own HTTP listener |
| `ServerlessHost` | ⚠ — composes but nothing drives `Invoke`; useful only when a hosting integration outside this SDK supplies the listener | ✅ | ✅ | ✅ |

`ServerlessHost = ServerlessHost` short-circuits every background-subsystem `IHostedService` registration through [`ProcessProfileGate.shouldRegisterBackgroundService`](../../ToolUp.Platform.Server/Server/Compose/ProcessProfileGate.fs); the DI singletons (`IJobScheduler`, `IWebhookDispatcher`, `TransactionalDispatcher`, …) still register so admin routes resolve, but their run-loops never tick.

### `ProcessProfile`

| `ServerConfig.ProcessProfile` | Kestrel | AzureFunctions | AwsLambda | GoogleCloudFunctions |
|---|---|---|---|---|
| `AllInOne` (default) | ✅ | ✅ — only profile that makes sense; `ServerlessHost` already gates background subsystems off | ✅ — as above | ✅ — as above |
| `WebOnly` | ✅ — sibling `WorkerOnly` silo drains jobs | ⚠ — equivalent to `AllInOne` (background subsystems already gated off by `ServerlessHost`); set `AllInOne` for clarity | ⚠ — as above | ⚠ — as above |
| `WorkerOnly` | ⚠ — binds Kestrel port but mounts no HTTP handlers; sibling routing should not target the silo | ❌ — host adapter requires the HTTP pipeline `Invoke` drives | ❌ — as above | ❌ — as above |
| `DispatcherOnly` | ✅ — outbound transactional + webhook dispatchers run; no scheduler, no RAG ingestion | ❌ — `ServerlessHost` gates the dispatchers off; no driver | ❌ — as above | ❌ — as above |

A `WorkerOnly` silo is always a Kestrel-shape deployment paired with one of the serverless front-doors above; the serverless side never sets `WorkerOnly`. The future `Host.CreateApplicationBuilder()` refactor (which lets `WorkerOnly` bind no port at all) is tracked on the Phase 16a follow-up list; until then keep `ReplicaCount = 1` on the worker silo and avoid routing HTTP to it.

### Background subsystems

Every cell in the serverless columns reads ❌ — `ServerlessHost = ServerlessHost` gates the IHostedService registration off across the board (see [Phase 16 migration: what gets gated](../../../docs/migrations/16-serverless-host-iserverhost.md#what-gets-gated-under-serverlesshost--serverlesshost)). The matrix exists to show which `ServerConfig` value to set to avoid orphaning the substrate (events queued with no drain, audit entries with no replicator, usage rows with no flusher).

| Subsystem (`ServerConfig` field) | Kestrel | AzureFunctions / AwsLambda / GoogleCloudFunctions |
|---|---|---|
| `JobScheduler = InProcessJobScheduler` | ✅ | ❌ — set `NoJobScheduler`; schedule jobs at a sibling `WorkerOnly` silo |
| `JobScheduler = NoJobScheduler` | ✅ | ✅ — required for the serverless front-door |
| `Webhooks = EnabledWebhooks` | ✅ | ❌ — set `NoWebhooks`; deliver from a sibling silo |
| `Webhooks = NoWebhooks` | ✅ | ✅ |
| `AuditLog = EnabledAuditLog` | ✅ — `AuditReplicator` fans events to external sinks | ⚠ — `IAuditLog` writes still land in `IEventStore`, but the `AuditReplicator` `BackgroundService` is gated off, so external `IAuditSink`s never receive events from this silo. Set `AuditLog = NoAuditLog` for clarity, or pair with a sibling silo that runs the replicator |
| `UsageMetering = EnabledUsageMetering` | ✅ | ⚠ — `IUsageLog` writes still land, but the `UsageBatchFlusher` is gated off; rows accumulate without a flush. Set `NoUsageMetering`, or pair with a sibling silo |
| `HealthStateTracking = true` | ✅ | ❌ — `HealthStateTrackerService` is gated off; set `HealthStateTracking = false` |
| `OAuth` data-source connectors | ✅ — token refresher + state-cleanup run | ❌ — both gated off; pair with a sibling silo if any data source uses OAuth refresh |

### Notifications & SSE

| `ServerConfig.Notifications` | Kestrel | AzureFunctions / AwsLambda / GoogleCloudFunctions |
|---|---|---|
| `NotificationsAuto` (default) | ✅ — SSE delivers in real time | ❌ — adapters buffer the response body before flushing; SSE never streams |
| `InMemoryNotifications` | ✅ | ❌ — as above; also per-instance cache, no fan-out |
| `RedisNotifications _` | ✅ | ⚠ — substrate works (cross-instance fan-out is fine), but the client-facing `/api/notifications` SSE response is still buffered; useful only for server-side coordination (e.g. notifying a sibling worker silo of an event) |
| `NoNotificationsExplicit` | ✅ | ✅ — required for the serverless front-door; gate AI-streaming routes off too (`AIAssistantMode = NoAIAssistant`) |

### Platform mode

| `ServerConfig.Mode` | Kestrel | AzureFunctions / AwsLambda / GoogleCloudFunctions |
|---|---|---|
| `Anonymous` | ✅ | ✅ — the canonical "stateless analytical tool" fit |
| `AuthenticatedEphemeral` | ✅ | ⚠ — works; per-instance session state — pair with a cloud-backed `IBlobStorage` and `IRateLimitStore` for multi-instance scaling |
| `Individual` | ✅ | ⚠ — as above; auth via OIDC server-to-server, persistence via cloud `IBlobStorage` |
| `Team` | ✅ | ⚠ — as above; `ITeamStore` and `IPermissionStore` resolve against the same blob substrate |
| `MultiTeam` | ✅ | ⚠ — as above; the header team-switcher and `TeamSwitched` reset path work because the SDK middleware that handles them is HTTP-shape, not BackgroundService-shape |

Single-instance cache locality applies to every serverless deployment: if the SDK is configured with an in-memory store (`InMemoryEventStore`, `InMemoryRateLimitStore`), each function execution environment has its own cache. Multi-instance deployments require cloud-backed substrate — see the per-cloud worked examples below for typical pairings.

### Transport-level features

| Feature | Kestrel | AzureFunctions | AwsLambda | GoogleCloudFunctions |
|---|---|---|---|---|
| Streaming responses (SSE, chunked transfer) | ✅ | ❌ — bridge buffers in `MemoryStream` before flushing | ❌ — as above (Function URLs `RESPONSE_STREAM` exists but is outside the shipped `bridge*` API) | ❌ — as above (Cloud Run `--no-cpu-throttling` exists but is outside the shipped `bridge*` API) |
| WebSocket upgrade | ✅ | ❌ — Functions HTTP triggers do not expose the upgrade handshake | ❌ — HTTP API does not expose it; API Gateway WebSocket APIs use a different integration shape | ❌ — Functions Framework does not surface the upgrade |
| Per-invocation timeout | _(none)_ | 5 min (Consumption) / 10 min default (Premium) | 15 min max | 9 min (1st gen) / 60 min (2nd gen / Cloud Run functions) |
| Cold-start cost | _(none — process is warm)_ | First request after idle pays .NET 10 JIT + SDK composition cost | As above; SnapStart and Provisioned Concurrency mitigate | As above; `--min-instances ≥ 1` mitigates |

See the [Cold-start mitigation](#cold-start-mitigation) section below for the compiled-binary-size guidance, R2R-over-trimming rationale, lazy-DI pre-resolution pattern, and the provider-specific always-warm levers.

## Worked examples

The three worked examples below share a common composition root and differ only in host wiring and deployment manifest. Each example targets the canonical "stateless analytical tool" shape (Anonymous mode + no background subsystems + no SSE), which is the minimum viable serverless deployment per the matrix above.

### Common composition root

```fsharp
// Server/Composition.fs
module MyApp.Composition

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
    |> ServerApp.addModule (MyApp.Module.register ())
    // The host adapter drives `Invoke` per cloud invocation; do NOT
    // call `ServerApp.run` (which would call `RunBlocking`).
    |> ServerApp.composeOnly
```

`composeOnly` is the low-level entry point that returns the `IServerHost` without calling `RunBlocking()`. (If not yet available in your SDK version, the same shape is reachable by calling `ToolUp.Platform.Server.compose` directly with the positional argument list.)

After composition, every adapter calls `Host.StartAsync(CancellationToken.None).Wait()` once at cold-start so ASP.NET Core's internal services (logger factory, config root, options) are ready. Under `ServerlessHost = ServerlessHost` every `IHostedService` registration is gated off, so `StartAsync` is effectively a no-op — the call still has to happen for the framework internals.

### Azure Functions

**`PackageReference` items.**

```xml
<PackageReference Include="ToolUp.Hosts.AzureFunctions" />
<PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" />
```

The consumer's `.fsproj` targets `net10.0` and uses the Functions Worker SDK so the project produces a Functions deployment artefact (`host.json`, `local.settings.json`, etc.).

**Host wiring.**

```fsharp
// Program.fs
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

let host =
    Host
        .CreateDefaultBuilder()
        .ConfigureFunctionsWorkerDefaults()
        .ConfigureServices(fun services ->
            services.AddSingleton<IServerHost>(MyApp.Composition.serverHost) |> ignore)
        .Build()

MyApp.Composition.serverHost.Host.StartAsync(System.Threading.CancellationToken.None).Wait()
host.Run()
```

**Catchall HTTP-trigger function.**

```fsharp
// Functions/Catchall.fs
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

The `{*path}` catchall forwards every request URL through `IServerHost.Invoke`.

**`host.json` (minimum).**

```json
{
  "version": "2.0",
  "extensions": {
    "http": { "routePrefix": "" }
  }
}
```

`routePrefix: ""` removes the default `/api` prefix so SDK routes (`/api/...`, `/health`, `/ready`) reach the Giraffe router under their declared paths.

**`local.settings.json` (local-run only — do NOT deploy).**

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated"
  }
}
```

**Environment variables (set in the Function App's Application Settings).** Names only — populate values from the Function App's secret store.

| Variable | Purpose |
|---|---|
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` |
| `AzureWebJobsStorage` | Function-app metadata storage (separate from your `IBlobStorage` companion) |
| `TOOLUP_AZURE_BLOB_CONNECTION` | If using `ToolUp.Storage.Azure` for `IBlobStorage` |
| _(your `ISecretStore` companion's env contract)_ | E.g. `TOOLUP_AZURE_KEYVAULT_URI` for `ToolUp.Secrets.AzureKeyVault` |

**Deployment.**

```powershell
dotnet publish -c Release -o ./publish
Compress-Archive -Path ./publish/* -DestinationPath ./publish.zip -Force
az functionapp deployment source config-zip `
    --resource-group <rg> --name <function-app> --src ./publish.zip
```

Or via the Functions Core Tools:

```powershell
func azure functionapp publish <function-app> --dotnet-isolated
```

### AWS Lambda

**`PackageReference` items.**

```xml
<PackageReference Include="ToolUp.Hosts.AwsLambda" />
<PackageReference Include="Amazon.Lambda.RuntimeSupport" />
<PackageReference Include="Amazon.Lambda.Serialization.SystemTextJson" />
```

**Host wiring (HTTP API v2 / Lambda Function URLs).** Most new deployments use HTTP API v2 (or Lambda Function URLs, which share the v2 payload).

```fsharp
// Program.fs
open Amazon.Lambda.Core
open Amazon.Lambda.APIGatewayEvents
open Amazon.Lambda.RuntimeSupport
open Amazon.Lambda.Serialization.SystemTextJson
open ToolUp.Hosts.AwsLambda

do
    MyApp.Composition.serverHost.Host.StartAsync(
        System.Threading.CancellationToken.None
    ).Wait()

let handler (req: APIGatewayHttpApiV2ProxyRequest) (_ctx: ILambdaContext) =
    AwsLambdaHost.bridgeV2 (MyApp.Composition.serverHost, req)

[<EntryPoint>]
let main _ =
    LambdaBootstrapBuilder
        .Create(handler, DefaultLambdaJsonSerializer())
        .Build()
        .RunAsync()
        .Wait()
    0
```

Two other bridges ship for legacy integrations: `AwsLambdaHost.bridgeV1` (API Gateway REST) and `AwsLambdaHost.bridgeAlb` (Application Load Balancer). Start with `bridgeV2` — lower cost, lower latency, simpler payload.

**`aws-lambda-tools-defaults.json` (manifest read by the Lambda Tools CLI).**

```json
{
  "profile": "default",
  "region": "eu-west-2",
  "configuration": "Release",
  "framework": "net10.0",
  "function-runtime": "dotnet10",
  "function-memory-size": 512,
  "function-timeout": 30,
  "function-handler": "MyApp",
  "function-name": "MyApp",
  "function-architecture": "x86_64"
}
```

Lambda's .NET 10 runtime support follows the AWS Lambda runtime roadmap — confirm availability for your region before deploying.

**Environment variables.**

| Variable | Purpose |
|---|---|
| `AWS_LAMBDA_FUNCTION_NAME` | Set by Lambda runtime — read by some SDK companions for self-identification |
| _(your `ISecretStore` companion's env contract)_ | E.g. `TOOLUP_AWS_SECRETSMANAGER_REGION` |
| _(your `IBlobStorage` companion's env contract)_ | E.g. `TOOLUP_AWS_S3_BUCKET` for `ToolUp.Storage.AwsS3` |

IAM credentials for the function come from the Lambda execution role — do not pass `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` as environment variables.

**Deployment (Lambda Tools CLI).**

```powershell
dotnet tool install -g Amazon.Lambda.Tools
dotnet lambda deploy-function MyApp
```

Pair with API Gateway HTTP API or a Lambda Function URL — both terminate at the same `bridgeV2` handler. Function URLs are the lowest-friction option for a single-handler deployment:

```powershell
aws lambda create-function-url-config --function-name MyApp --auth-type NONE
```

### Google Cloud Functions

**`PackageReference` items.**

```xml
<PackageReference Include="ToolUp.Hosts.GoogleCloudFunctions" />
<PackageReference Include="Google.Cloud.Functions.Hosting" />
```

`Google.Cloud.Functions.Framework` arrives transitively through `ToolUp.Hosts.GoogleCloudFunctions`. The `Hosting` package carries the entry-point startup helpers (`FunctionsStartup`, `--target` parsing); add it as a top-level reference so the consumer's entry point compiles.

**Host wiring.**

```fsharp
// Startup.fs
open Google.Cloud.Functions.Hosting
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform

type Startup() =
    inherit FunctionsStartup()
    override _.ConfigureServices(_context, services) =
        services.AddSingleton<IServerHost>(MyApp.Composition.serverHost) |> ignore

do
    MyApp.Composition.serverHost.Host.StartAsync(
        System.Threading.CancellationToken.None
    ).Wait()
```

**Function class (recommended — built-in `ServerHostFunction`).** Subclass so the Functions Framework's single-`IHttpFunction`-in-assembly auto-detection finds it.

```fsharp
// Function.fs
open Google.Cloud.Functions.Hosting
open ToolUp.Hosts.GoogleCloudFunctions
open ToolUp.Platform

[<FunctionsStartup(typeof<MyApp.Startup>)>]
type Function(host: IServerHost) =
    inherit ServerHostFunction(host)
```

**Environment variables.**

| Variable | Purpose |
|---|---|
| `FUNCTION_TARGET` | Required — `MyApp.Function` (the fully-qualified type name of your `Function` class) |
| _(your `ISecretStore` companion's env contract)_ | E.g. `TOOLUP_GCP_SECRETMANAGER_PROJECT` |
| _(your `IBlobStorage` companion's env contract)_ | E.g. `TOOLUP_GCP_STORAGE_BUCKET` for `ToolUp.Storage.GoogleCloud` |

Service-account credentials come from the Cloud Functions execution identity (Application Default Credentials) — do not pass a service-account key as an environment variable in production.

**Deployment (2nd gen Cloud Functions — recommended).**

```powershell
dotnet publish -c Release -o ./publish
gcloud functions deploy myapp `
    --gen2 `
    --runtime=dotnet10 `
    --region=europe-west2 `
    --source=./publish `
    --entry-point=MyApp.Function `
    --trigger-http `
    --allow-unauthenticated `
    --min-instances=1
```

`--min-instances=1` keeps one warm to amortise cold-start; drop it for cost-tuned low-traffic deployments and accept the per-invocation latency. Cloud Run functions (the rebranded 2nd-gen surface) use identical packaging via `gcloud run deploy`. 1st gen Cloud Functions are legacy — new deployments should target 2nd gen.

GCF's .NET 10 runtime support follows the Cloud Functions runtime roadmap — confirm availability for your region before deploying.

### Hybrid: serverless front-door + Kestrel worker silo

The fourth worked example pairs any of the three serverless front-doors above with a long-running Kestrel worker silo (`ProcessProfile = WorkerOnly`). The front-door handles request/response API at low idle cost; the worker silo runs the background subsystems gated off by `ServerlessHost`: job scheduler, webhook dispatcher, audit replicator, usage flusher, RAG ingestion, OAuth refresh, health-state tracking. Both halves share the same persistent substrate; cross-silo coordination flows through a Redis-backed `INotificationChannel`.

**When this shape fits.** Workloads that need on-demand request/response handling AND any of: scheduled jobs, outbound webhooks, multi-stage ingestion, audit fan-out, usage metering, OAuth token refresh. The single-host options force either "pay for an always-on instance for tiny request loads" (Kestrel-only) or "lose every background capability" (serverless-only); the hybrid pays only for the silo that has work.

**Substrate contract.** Every substrate the two halves must observe identically:

| Substrate (`ServerConfig` field) | Front-door (`ServerlessHost`) | Worker (`KestrelHost` + `WorkerOnly`) | Coordination |
|---|---|---|---|
| `BlobStorage` | Cloud-backed (`ToolUp.Storage.Azure` / `.AwsS3` / `.GoogleCloud`) | Same companion, same connection | Connection-string env var identical in both deployments |
| `EventStore` | Same backing store as `BlobStorage` (the default) | Same | The worker's `AuditReplicator` + transactional dispatcher drain events the front-door writes |
| `RateLimitStore` | Redis-backed when multi-instance | Same Redis | Per-tenant counters need shared storage; per-instance `InMemoryRateLimitStore` defeats the rate envelope |
| `Notifications` | `RedisNotifications` | Same Redis | The worker publishes job-completion / ingestion events; the front-door subscribes for cache hints (browser SSE caveats below) |
| `TeamStore` / `PermissionStore` | Blob-backed (shared) | Same | Auth resolution reaches one source of truth; an in-memory team store on either silo cannot serve the other |
| `ConfigStore` | Blob-backed | Same | Operator-set config (rate limits, kill switches) — the worker's `ConfigStoreInvalidator` `BackgroundService` is gated off in serverless mode, so the front-door re-reads on every request |
| `SecretStore` | Provider-native (`AzureKeyVault` / `AwsSecretsManager` / `GcpSecretManager`) | Same | Both halves' managed identity is granted read on the same secret scope |
| `JobScheduler` | `NoJobScheduler` | `InProcessJobScheduler` | The front-door never schedules directly; it `INotificationChannel.Publish`es and the worker's job handler (subscribed via `IModuleQueryBus`) picks it up |
| `Webhooks` | `NoWebhooks` | `EnabledWebhooks` | Modules call `IWebhookEvents.publish` on either side; the dispatcher only runs on the worker, so front-door publishes queue for the worker to drain |
| `AuditLog` / `UsageMetering` / `HealthStateTracking` | `NoAuditLog` / `NoUsageMetering` / `false` | `EnabledAuditLog` / `EnabledUsageMetering` / `true` | Writes from both halves land in the shared `IEventStore`; only the worker silo replicates / flushes / tracks state |

**Front-door composition root.** Same shape as the Anonymous worked example above, with substrate fields pointing at cloud companions and (typically) `Mode = Team` or `Mode = Individual` rather than `Anonymous`.

```fsharp
// Server/FrontDoorComposition.fs
module MyApp.FrontDoorComposition

open ToolUp.Platform

let serverHost: IServerHost =
    ServerApp.empty
    |> ServerApp.withConfig {
        ServerConfig.defaults with
            Mode = Team
            ServerlessHost = ServerlessHost
            ProcessProfile = AllInOne
            BlobStorage = AzureBlobStorage(Env.required "TOOLUP_AZURE_BLOB_CONNECTION")
            RateLimitStore = RedisRateLimitStore(Env.required "TOOLUP_REDIS_CONNECTION")
            Notifications = RedisNotifications(Env.required "TOOLUP_REDIS_CONNECTION")
            SecretStore = AzureKeyVaultSecrets(Env.required "TOOLUP_AZURE_KEYVAULT_URI")
            JobScheduler = NoJobScheduler
            Webhooks = NoWebhooks
            AuditLog = NoAuditLog
            UsageMetering = NoUsageMetering
            HealthStateTracking = false
    }
    |> ServerApp.addModule (MyApp.Module.register ())
    |> ServerApp.composeOnly
```

**Worker silo composition root.** Same domain modules; opposite background-subsystem posture.

```fsharp
// Server/WorkerComposition.fs
module MyApp.WorkerComposition

open ToolUp.Platform

[<EntryPoint>]
let main _ =
    ServerApp.empty
    |> ServerApp.withConfig {
        ServerConfig.defaults with
            Mode = Team
            ServerlessHost = KestrelHost
            ProcessProfile = WorkerOnly
            BlobStorage = AzureBlobStorage(Env.required "TOOLUP_AZURE_BLOB_CONNECTION")
            RateLimitStore = RedisRateLimitStore(Env.required "TOOLUP_REDIS_CONNECTION")
            Notifications = RedisNotifications(Env.required "TOOLUP_REDIS_CONNECTION")
            SecretStore = AzureKeyVaultSecrets(Env.required "TOOLUP_AZURE_KEYVAULT_URI")
            JobScheduler = InProcessJobScheduler
            Webhooks = EnabledWebhooks
            AuditLog = EnabledAuditLog
            UsageMetering = EnabledUsageMetering
            HealthStateTracking = true
    }
    |> ServerApp.addModule (MyApp.Module.register ())
    |> ServerApp.run

    0
```

**Environment-variable contract.** Both halves consume the same env-var names with identical values; the cloud-provider deployments differ only in which sets the values:

| Variable | Front-door | Worker | Notes |
|---|---|---|---|
| `TOOLUP_AZURE_BLOB_CONNECTION` (or `TOOLUP_AWS_S3_BUCKET` / `TOOLUP_GCP_STORAGE_BUCKET`) | ✓ | ✓ | Identical values — both halves point at the same blob container |
| `TOOLUP_REDIS_CONNECTION` | ✓ | ✓ | Identical — same Redis instance for `RateLimitStore` + `Notifications` (separate keyspaces inside Redis) |
| `TOOLUP_AZURE_KEYVAULT_URI` (or `TOOLUP_AWS_SECRETSMANAGER_REGION` / `TOOLUP_GCP_SECRETMANAGER_PROJECT`) | ✓ | ✓ | Identical — same secret scope |
| OIDC issuer / audience / client-id (`TOOLUP_OIDC_*`) | ✓ | ✓ | Worker doesn't serve callbacks but reads claims from audit events |
| `ASPNETCORE_URLS` (or platform-set port binding) | _(host adapter sets it)_ | ✓ | Worker binds Kestrel on the silo |

**Deployment topology — one per cloud-provider.**

- **Azure**: Function App (Consumption or Premium) for the front-door + Container Apps job (or a `replicas = 1` Container App) for the worker. Same Resource Group, same managed-identity grant on the Key Vault + Blob containers.
- **AWS**: Lambda + API Gateway HTTP API (or Function URL) for the front-door + ECS Fargate service with `desiredCount = 1` for the worker. The Lambda execution role and the Fargate task role both granted S3 + Secrets Manager + ElastiCache permissions on the same resources.
- **GCP**: Cloud Run functions (2nd gen) for the front-door + Cloud Run (services, not functions) with `min-instances = 1` for the worker. Both run as the same service-account identity; shared Cloud Memorystore Redis instance accessed via VPC connector.

**Why `replicas = 1` on the worker silo.** Phase 9b's `InProcessJobScheduler` assumes a single drainer per due-job partition; two replicas double-fire scheduled jobs. The distributed `IJobScheduler` companions from Phase 9c lift this restriction by externalising the leader election; until that lands, pin the worker silo at one replica. The front-door is freely scalable — every `BackgroundService` is gated off, so there is no leader to elect.

**Browser SSE across the silo boundary.** The front-door's `/api/notifications` SSE endpoint is buffered (per the [transport-level matrix](#transport-level-features)), so a worker-published event does NOT stream to a browser-side `EventSource` connected to the front-door. Two patterns close the loop without changing host runtimes:

- The browser polls a worker-silo SSE endpoint directly (routed via a separate path on the load balancer); the worker's `RedisNotifications` substrate fans out per tenant.
- The browser polls a buffered `/api/notifications/poll` route on the front-door, which reads-and-clears the per-tenant Redis backlog. Added latency, single browser-target URL.

The first pattern is the lower-latency choice when the front-door's HTTP runtime can't stream; the second keeps the entire client wired to one origin.

## Cold-start mitigation

The phase-16 acceptance target is "< 2s cold start" for a minimal `Anonymous` SDK composition on Azure Functions Consumption. Hitting that on any of the three runtimes depends on a small set of levers turned at composition or deployment time. The list below is ordered cheapest-effort-first; apply the ones that materially help your workload.

**1. Framework-dependent publish.** Default (`<SelfContained>false</SelfContained>`) cuts publish-output size by 70–80 MB versus self-contained and lowers cold-start I/O cost accordingly. Self-contained is required only if your target runtime doesn't ship the .NET 10 framework yet — confirm runtime availability for each provider's region (see the per-cloud notes at the end of each worked example) before publishing self-contained.

**2. `ReadyToRun` over `PublishTrimmed`.** Trimming is unsafe for F# applications: ToolUp.Remoting's transport layer (the in-tree Fable.Remoting fork) uses reflection to materialise the API record on the server, and any trim pass removes handlers it can't statically prove are reachable. `<PublishReadyToRun>true</PublishReadyToRun>` is safe — it precompiles IL to native ahead of time, eliminating the JIT pass on the cold-start hot path without removing any code. Expect a 30–50% cold-start reduction on the SDK's stateless composition without breaking reflection.

```xml
<!-- In the consumer's Server fsproj -->
<PropertyGroup>
  <PublishReadyToRun>true</PublishReadyToRun>
  <SelfContained>false</SelfContained>
</PropertyGroup>
```

A pure-Anonymous-mode deployment that doesn't use ToolUp.Remoting on its API surface (Giraffe-shape HTTP handlers only) can opt into `<PublishTrimmed>true</PublishTrimmed>` at the consumer fsproj level. Document the choice explicitly so a future contributor doesn't add ToolUp.Remoting and discover the runtime breakage in production.

**3. Pre-resolve hot-path singletons.** ASP.NET Core's DI container resolves lazily; the first call to any service pays the resolution cost on the request-handling thread. The host adapters all call `IServerHost.Host.StartAsync` at cold start, which pre-resolves every `IHostedService` registration (gated off under `ServerlessHost = ServerlessHost`, so this is effectively a no-op for background subsystems) and every `Server`-tier substrate the composition root touched (`IBlobStorage`, `IConfigStore`, `IAuthProvider`, `IAIProvider`, …). The substrate companions you registered are warm by the time the first request lands.

What is NOT pre-resolved: module-level singletons registered through their own `services.AddSingleton<…>` that the composition root never touches. If a module's first request triggers an expensive type-load (opening an LLM tokeniser, parsing a large RAG knowledge-base index, deserialising a registered prompt corpus), pre-resolve it explicitly:

```fsharp
// In the consumer's Composition.fs, after the SDK compose
let host = ServerApp.empty |> ... |> ServerApp.composeOnly
host.Host.StartAsync(CancellationToken.None).Wait()
// Force any module hot-path singleton to materialise before first request
host.App.Services.GetRequiredService<MyModule.HotPath.IExpensiveClient>() |> ignore
```

The SDK ships this pattern for `AILoaderConfig`: every system prompt is rendered at boot and cached as a static string so the AI request hot path never expands a template. Extend the pattern to any hot-path computation you can move to boot.

**4. Provider-specific always-warm levers.** Each runtime carries its own warm-instance affordance, paid in dollars-per-month rather than per-invocation latency:

| Runtime | Lever | Effect on cold start |
|---|---|---|
| Azure Functions Consumption | _(none — no always-warm option)_ | First request after idle pays full cold start. Premium plan is the only path. |
| Azure Functions Premium | `alwaysOn = true` + pre-warmed instance count (`functionAppScaleLimit`) | Keeps N instances warm; per-instance cold start with R2R + framework-dependent typically ≈800ms–1.5s on the SDK's stateless composition |
| AWS Lambda | SnapStart (`SnapStart = ApplyOn = "PublishedVersions"`) | Snapshots the CLR after init, restores per-invocation; drops cold start to ≈200–400ms. .NET 10 availability follows the AWS Lambda runtime roadmap. |
| AWS Lambda | Provisioned Concurrency | Keeps N instances continuously warm; higher cost, lower latency than SnapStart |
| Google Cloud Functions (2nd gen) | `--min-instances=1` | Keeps one instance warm continuously; per-invocation latency drops to network RTT |

Without any of these levers, expect a cold start in the 1.2–2s range for the SDK's stateless composition (Azure Consumption / Lambda without SnapStart / GCF without `--min-instances`). With R2R + framework-dependent publish, that floor drops by 30–50%.

**5. Module footprint.** Every module the composition root registers contributes to JIT cost on first request. A serverless front-door that only needs a subset of the modules in your full Kestrel deployment can compose a slimmed module list — `ServerApp.addModule` is per-call, so the same domain code base targets multiple deployment shapes from different `Composition.fs` entry points. This pairs naturally with the hybrid worked example above: the front-door imports only the request-handling modules, the worker silo imports the full set.

**6. Measuring cold start.** None of the levers above is guaranteed to hit the < 2s target on every cloud / instance shape. Measure your actual deployment with `time curl https://your-app/health` from a cold (long-idle) state. `/health` is the lowest-overhead path through the request pipeline; if it doesn't hit your target, no business-logic endpoint will. Log the first-request latency on every deployment as part of release validation — cold-start drift is a regression class the CI suite cannot catch.

## Per-host packaging (FAKE)

The `ToolUp.Platform.Build` package ships three host-packaging helpers consumer FAKE pipelines wire to produce the deployable bundle each cloud runtime expects. Each function takes a `dotnet publish` output directory and emits a zip rooted at the publish-dir contents:

```fsharp
// Build.fs — consumer-side
open Fake.Core
open ToolUp.Platform        // brings HostPackaging into scope
open ToolUp.Platform.Build  // brings BuildConfig, registerTargets, etc.

Target.create "PublishFunctions" (fun _ ->
    let publishDir = "deploy/azure-functions"
    let zipPath = "deploy/azure-functions.zip"

    CreateProcess.fromRawCommand
        "dotnet"
        [ "publish"; "src/MyApp.Server/MyApp.Server.fsproj"; "-c"; "Release"; "-o"; publishDir ]
    |> CreateProcess.ensureExitCode
    |> Proc.run
    |> ignore

    HostPackaging.packAzureFunctions publishDir zipPath)
```

The same shape applies for `HostPackaging.packAwsLambda` and `HostPackaging.packGoogleCloudFunctions`. Each function:

- Verifies the publish directory exists and is non-empty before zipping (clear error message if `dotnet publish` was skipped or pointed at the wrong path).
- Replaces any existing zip at the output path.
- Rejects bundles smaller than 100 KB on the assumption a sub-100 KB bundle indicates a failed publish rather than a legitimately tiny app. Consumers shipping a deployment that genuinely lands below the threshold can inline the body against `System.IO.Compression.ZipFile.CreateFromDirectory` — the function bodies are short on purpose so the extension seam stays mechanical.

**Smoke test in the Verify chain.** The packaging functions throw on a bad bundle, so wiring them into a `Verify` target catches a "deploy will fail on the cloud runtime" regression at build time rather than at deploy time:

```fsharp
Target.create "Verify-Packaging" (fun _ ->
    HostPackaging.packAzureFunctions "deploy/azure-functions" "deploy/azure-functions.zip"
    HostPackaging.packAwsLambda "deploy/aws-lambda" "deploy/aws-lambda.zip"
    HostPackaging.packGoogleCloudFunctions "deploy/gcf" "deploy/gcf.zip")

"Bundle" ==> "Verify-Packaging" |> ignore
```

The `Bundle` dependency is required because `Verify-Packaging` assumes `dotnet publish` has populated each input directory; if your pipeline produces the publish output by a different target, chain accordingly.

**Why the three function names exist separately.** Today's shipped behaviour is identical — each writes a zip of `publishDir`. The names are reserved as the host-specific extension seam: when AWS Lambda adds first-class .NET 10 SnapStart support requiring a layered bundle format, or when Cloud Functions Gen 2 settles on a source-archive convention distinct from `gcloud functions deploy --source=`, that divergence lands behind these names without churning consumer wiring.

## Follow-ups (deferred from this phase task)

- **Phase 16a deployment-shapes section** — shipped as [chapter 13](13-deployment-shapes.md). The `ProcessProfile` matrix and the Kestrel-shape `WebOnly` / `WorkerOnly` / `DispatcherOnly` worked examples live there; the [Phase 16a migration doc](../../../docs/migrations/16a-process-profile-gating.md) carries the consumer-side migration diff.
