# ToolUp.Platform Technical Guide — 01. Architecture & Composition

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> ← Prev: _(none)_ · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 2. Multi-Tenancy, Teams & Access Control →](02-multi-tenancy-and-access.md)

---

## The Problem with the Standard Three-Project Layout

A standard F# full-stack application has three projects: Shared, Server, and Client. This works well for a single application, but breaks down when you want to host multiple independent analytical modules within a single platform:

- **Shared becomes a dumping ground.** Every module's DTOs, API contracts, and validation types end up in one project. Removing a module means surgically extracting its types from Shared without breaking others.

- **Server.fs grows into a monolith.** All route handlers, all data access, all business logic — one project, one compile order. Adding a module means editing files that every other module also touches.

- **Client Index.fs hardcodes the world.** The Elmish program, the shell layout, and every module's init/update/view are wired together in one file. The module list is a literal F# list expression.

- **You can't distribute the platform without distributing every module.** There is no boundary between "platform infrastructure" and "module-specific code."

ToolUp.Platform solves all of these by restructuring where code lives and how it enters compilation.

## The Core Insight: Props-Based Code Injection

> **Status note (2026-05-08):** Phase 11.B follow-up moved the SDK and its companions onto `<Compile>` + `<ProjectReference>` consumption — the SDK ships as `ToolUp.Platform.{Core,Server,Client,Build}.dll` and each cross-tier SDK companion (`ToolUp.AI`, `ToolUp.RAG`, `ToolUp.KnowledgeBase`, `ToolUp.Forms`, `ToolUp.Scheduling`) ships as a Core/Server[/Client] DLL trio. The `.Server.props` / `.Client.props` files at SDK and companion sites have been emptied to markers. **Modules under `src/Modules/` deliberately keep the source-injection pattern described in this section** — they are App-tier code, not SDK companions, do not get NuGet-packaged, and the single-fsproj + `.Client.props` convention is preserved on purpose. The mechanism described below remains the live shape for module client code injection into `ToolupApp-Client.fsproj`; the SDK and its companions instead consume each other via `<ProjectReference>` (and post-Phase-11.C.2, `<PackageReference>`).

The central technical mechanism is **MSBuild `.props` file injection**. This exploits a property of F# compilation: **compile order is determined by the order items appear in the `.fsproj`, and `<Import>` directives insert items at the position they appear.**

A standard full-stack Client `.fsproj` lists every `.fs` file explicitly:

```xml
<Compile Include="Module1Model.fs" />
<Compile Include="Module1View.fs" />
<Compile Include="Module2Model.fs" />
<Compile Include="Module2View.fs" />
<Compile Include="Index.fs" />
<Compile Include="App.fs" />
```

Adding a module means editing this file. Removing a module means editing this file. The file is the coupling point.

ToolUp.Platform replaces this with:

```xml
<Import Project="..\ToolUp.Platform\ToolUp.Platform.Client.props" />
<Import Project="..\Modules\Module1\Module1.Client.props" />
<Import Project="..\Modules\Module2\Module2.Client.props" />
<ItemGroup>
    <Compile Include="Client.fs" />
</ItemGroup>
```

Each `.Client.props` file contains `<Compile>` items pointing to its own source files:

```xml
<Project>
  <ItemGroup>
    <Compile Visible="false" Include="$(MSBuildThisFileDirectory)SharedTypes.fs" />
    <Compile Visible="false" Include="$(MSBuildThisFileDirectory)ClientModel.fs" />
    <Compile Visible="false" Include="$(MSBuildThisFileDirectory)ClientView.fs" />
  </ItemGroup>
</Project>
```

MSBuild resolves `$(MSBuildThisFileDirectory)` to the directory containing the `.props` file — so the source files are physically in the module directory but compiled as part of the Client project. `Visible="false"` hides them from Solution Explorer so the Client project shows only `Client.fs`.

**Why this matters for Fable:** Fable compiles the entire Client `.fsproj` into JavaScript. All `.fs` files — whether locally defined or injected via props — end up in the same Fable compilation unit. This avoids a specific Fable codegen pitfall: anonymous records (`{| Foo = 1 |}`) are emitted as type-name-mangled JS classes scoped to the assembly that declares them, so the *same* literal constructed in two separately-Fable-compiled assemblies produces structurally incompatible JS types. By compiling SDK + module client code as one unit, every shared anonymous record, UIToolkit helper, and Elmish wrapper resolves to the same emitted type. See the `[<Erase>]` types section below for the related boundary issue affecting `inline` members on erased types.

**Why this matters for F# compile order:** The `<Import>` position determines where the injected files sit in the compile order. SDK props come first (providing UIToolkit, the shell framework), then module props (each module's SharedTypes -> ClientModel -> ClientView), then `Client.fs` last. This ensures every file can reference everything above it.

The same mechanism works for the server via `ToolUp.Platform.Server.props`, injecting infrastructure files (auth providers, storage, event store, team management, scope resolution, compose function) into the Server project.

### Server props: the target injection pattern

The client `.Client.props` uses simple `Visible="false"` on static `<Compile>` items — this works because the client uses `Microsoft.NET.Sdk`.

The server `.Server.props` requires a different approach because `Microsoft.NET.Sdk.Web` ignores both `Visible="false"` and `InProject="false"` on static compile items. Instead it uses a private item group + target injection pattern:

```xml
<Project>
  <ItemGroup>
    <_ToolUpPlatformServerSources Include="$(MSBuildThisFileDirectory)InMemoryEventStore.fs" />
    <_ToolUpPlatformServerSources Include="$(MSBuildThisFileDirectory)Auth\HeaderAuthProvider.fs" />
    <!-- ... more files ... -->
    <_ToolUpPlatformServerSources Include="$(MSBuildThisFileDirectory)SDK.Server.fs" />
  </ItemGroup>

  <Target Name="InjectToolUpPlatformServerSources" BeforeTargets="CoreCompile">
    <ItemGroup>
      <_ExistingCompile Include="@(Compile)" />
      <Compile Remove="@(Compile)" />
      <Compile Include="@(_ToolUpPlatformServerSources)">
        <Visible>false</Visible>
        <InProject>false</InProject>
      </Compile>
      <Compile Include="@(_ExistingCompile)" />
    </ItemGroup>
  </Target>
</Project>
```

1. Files are listed as `<_ToolUpPlatformServerSources>` (underscore-prefixed items are invisible to CPS/Solution Explorer)
2. A `<Target BeforeTargets="CoreCompile">` injects them into `<Compile>` with `<Visible>false</Visible>` and `<InProject>false</InProject>` metadata
3. The target reorders to preserve F# compile order: removes existing `Compile` items, adds SDK sources first, then re-adds the project's own files

This pattern must not be applied to `.Client.props` — the client `.fsproj` imports many `.props` files in a specific dependency order, and the reorder trick would break that sequence.

## Type Erasure for Module Composition

In standard Elmish, the top-level program has a single `Model` and `Msg` type. With multiple modules, each having its own `Model` and `Msg`, you need a way to compose them into a single Elmish program.

The SDK solves this with a two-stage approach:

**Stage 1: Typed construction.** Each module builds a `ClientModule<'Model, 'Msg>` record with its strongly-typed init, update, and view:

```fsharp skip=fragment
// In a module's ClientView.fs
let register () : ToolUp.Platform.ErasedModule =
    ToolUp.Platform.ClientModule.register {
        Definition = {
            Id = "MyModule"          // stable permission key — matches `makePermissionGuardedApi` in Server.fs
            Name = "My Module"       // display name shown in sidebar
            Pages = [ ... ]
        }
        Init = MyModel.init
        Update = MyModel.update
        View = view
        NeedsData = Some(fun has -> has "MyDataType")
        DataTypes = [ myDataTypeDisplay ]
        ProvidesProcessedData = None
    }
```

`ModuleDefinition` separates identity (`Id`) from presentation (`Name`). `Id` is the stable string used as a Map key for module state, the sidebar filter key against `GetAccessibleModules`, the `AIMessageRequest.ActiveModule` payload, and — for modules exposed by the app server — the `makePermissionGuardedApi` / `AccessContext` permission key. Convention: PascalCase, no spaces (e.g. "SkuAnalysis"). `Name` is free-form human-readable text shown in the sidebar and page header. SDK-built-in modules (FileManager, TeamManager) and companion-provided modules (AI assistant, AI settings) use reserved `Id` prefixes (`_sdk.*`, `_ai.*`) so they can never collide with app module Ids.

**Stage 2: Type erasure.** `ClientModule.register` erases the generic types into `ErasedModule`, where `Model` becomes `obj` and `Msg` becomes `obj`:

```fsharp
let register (m: ClientModule<'Model, 'Msg>) : ErasedModule = {
    Definition = m.Definition
    Init =
        fun () ->
            let model, cmd = m.Init()
            box model, Cmd.map box cmd
    Update =
        fun msg state ->
            let typedMsg = unbox<'Msg> msg
            let typedModel = unbox<'Model> state
            let newModel, cmd = m.Update typedMsg typedModel
            box newModel, Cmd.map box cmd
    View =
        fun state dispatch ->
            let typedModel = unbox<'Model> state
            let typedDispatch msg = dispatch (box msg)
            m.View typedModel typedDispatch
    NeedsData = m.NeedsData
    DataTypes = m.DataTypes
    ProvidesProcessedData =
        m.ProvidesProcessedData
        |> Option.map (fun f -> fun state -> f (unbox<'Model> state))
}
```

All `box`/`unbox` calls are contained within this single function. Module code never sees type erasure. The shell Elmish program works with `ErasedModule list`, routing messages to the active module's update function and rendering its view.

**Why not use a DU for messages?** A discriminated union like `type Msg = Module1Msg of Module1.Msg | Module2Msg of Module2.Msg` would couple the shell to every module at compile time. The type-erased approach means the shell only knows about `ErasedModule` — it never names a module.

## The Shell MVU

The SDK provides a complete Elmish program in `SDK.Client.fs` that manages module switching:

```fsharp
type private Model = {
    ActiveModuleName: string
    ModuleStates: Map<string, obj>   // Each module's state, keyed by name
}

type private Msg =
    | ModuleMsg of obj               // Forwarded to the active module
    | ModuleSelected of string       // User clicked a different module in the sidebar
```

`ModuleStates` is a `Map<string, obj>` — each module's state is stored as a boxed value keyed by its name. When the user switches modules, the previous module's state is preserved in the map. When they switch back, the state is restored without re-initialising.

After every update cycle, the shell calls `computeProcessedData` — a pure aggregation over every module that exposes `ProvidesProcessedData` — and stores the result in `Model.ProcessedData`. The shell `view` wraps the active module in `ProcessedDataContext.Context.Provider model.ProcessedData`, so any module view `[<ReactComponent>]` that calls `ProcessedData.forType` sees the up-to-date list via `React.useContext`. This is how the shell distributes shared state without modules mutating globals directly — no module writes to a shared registry; only the shell publishes, only views consume.

The view function builds the sidebar from `ModuleDefinition` metadata — module names, icons, and data availability. No module name is hardcoded in the SDK.

`SDK.Client.run` constructs the Elmish program, handles data manager injection, wires HMR in Debug mode, and starts the React root:

```fsharp
let run (config: ClientConfig) (modules: ErasedModule list) =

    UserSession.configure config.Mode

    let allDataTypeDisplays = modules |> List.collect (fun m -> m.DataTypes)

    let allModules =
        match config.DataManager with
        | NoDataManager -> modules
        | DefaultDataManager -> FileManagerUI.create allDataTypeDisplays None :: modules
        | ConfiguredDataManager dmConfig -> FileManagerUI.create allDataTypeDisplays (Some dmConfig) :: modules
        | ExternalDataManager custom -> custom :: modules

    let program =
        Program.mkProgram (init config allModules) (update allModules) (viewWithSignIn config allModules)
#if DEBUG
        |> Program.withConsoleTrace
#endif

    program |> Program.withReactSynchronous "elmish-app" |> Program.run
```

`viewWithSignIn` runs `view` and pipes the result through `AuthUIProvider.gate config.AuthUI config.Mode`. The gate is a pass-through in Anonymous mode and when `ClientConfig.AuthUI = NoAuthUI` (the default) — it only wraps when a companion-backed sign-in UI has been selected. See the [Sign-in UI companions](03-authentication-secrets-and-encryption.md#sign-in-ui-companions) section (Chapter 3) for the delegate-registry pattern.

## Server Composition

Apps compose their server through a layered record-based fluent API. Each layer adds more infrastructure while re-using the layer below; each layer's `run` function drives the actual compose call internally. This lets an app opt in to only the layers it needs without changing compose's signature.

- `ServerApp` — core record; adds modules (via `ServerModule`), config, auth, logger, storage, notifications, extensions
- `AIServerApp` — wraps a `ServerApp.Base`; adds AI provider factory, AI config store, AI tools, module AI contexts
- `RAGServerApp` — wraps an `AIServerApp.AI`; adds an embedding provider

```fsharp skip=fragment
// No AI:
ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withAuth authProvider
|> ServerApp.withStorage blobStorage
|> ServerApp.addModules modules
|> ServerApp.run

// With AI:
AIServerApp.empty
|> AIServerApp.withBase (ServerApp.empty |> ... |> ServerApp.addModules modules)
|> AIServerApp.withAIFactory aiProviderFactory
|> AIServerApp.withAIConfigStore aiConfigStore
|> AIServerApp.withAITools AITools.allTools
|> AIServerApp.run

// With RAG:
RAGServerApp.empty
|> RAGServerApp.withAI (AIServerApp.empty |> ...)
|> RAGServerApp.withEmbeddingProvider embeddingProvider
|> RAGServerApp.run
```

A `ServerModule` record collects everything one module contributes to the server:

```fsharp skip=fragment
ServerModule.create "SkuAnalysis"           // Name = RBAC key for makePermissionGuardedApi
|> ServerModule.withGuardedApi apiFactory   // HttpContext -> 'T, wrapped in makePermissionGuardedApi
|> ServerModule.withDataTypes [ salesDataType ]
|> ServerModule.withVectorisation [ embeddingHandler ]
|> ServerModule.withConfig configSchema
```

`ServerApp.addModules` flattens the `ServerModule` list into the handler list, data-type list, vectorisation-handler list, and config-schema list that the underlying `SDK.Server.compose` consumes. Under the record pipeline, `compose` still has the same signature — but callers don't interact with it directly. Its responsibilities (auto-inject the five platform APIs — `PlatformInfoApi`, `TeamApi`, `PermissionApi`, `AccessibilityApi`, `DataCatalogApi` — plus FileManagementApi, ConfigApi, `/api/notifications`; register DI services; apply middleware) are unchanged.

```fsharp skip=fragment
let compose
    (handlers: HttpHandler list)
    (dataTypes: DataType list)
    (config: ServerConfig)
    (authProvider: IAuthProvider option)
    (extensions: ComposeExtensions)
    (logger: ILogger option)
    (blobStorage: IBlobStorage option)
    (notificationChannel: INotificationChannel option)
    (queryHandlers: (string * ModuleQueryHandler) list)
    : unit =
    ...
```

Companions wrap `compose` rather than modifying it. `ToolUp.AI.AICompose.composeWithAI` (Phase 1b) builds `ComposeExtensions` carrying the AI handlers + DI registrations and forwards to `compose`; `AIServerApp.run` calls `composeWithAI` with the collected state. `ToolUp.RAG.RAGCompose.composeWithRAG` (Phase 14) wraps `composeWithAI` in turn; `RAGServerApp.run` calls `composeWithRAG`. Apps without AI use `ServerApp.run`, which forwards to `compose` with `ComposeExtensions.empty`.

`ServerConfig` itself remains AI- and RAG-ignorant — the extensions record and the layered records are the only coupling points, and new companions can layer in without touching core.

Compose does the following:

1. **Auto-injects the five platform-API handlers** — `PlatformInfoApi` (mode + auth posture), `TeamApi` (team CRUD), `PermissionApi` (RBAC, Owner/Admin gated), `AccessibilityApi` (sidebar filter), `DataCatalogApi` (data-type enumeration); always present
2. **Auto-injects the FileManagement handler** — only when `dataTypes` is non-empty
3. **Auto-injects the ConfigHandler** — reserved `_platform` key always surfaced
4. **Auto-injects the FeatureFlag and ModuleQueryBus handlers** — always present (short-circuit on empty config)
5. **Auto-injects the `/api/notifications` SSE route** — Phase 6a, shared by all companions
6. **Merges extension handlers** — AI / RAG companions contribute via `ComposeExtensions.Handlers`
7. **Builds the ASP.NET Core app** via `WebApplication.CreateBuilder` with all handlers combined via Giraffe's `choose` and mounted via `app.UseGiraffe`
8. **Registers all DI services** — core services + `ComposeExtensions.ServiceConfig` additions, based on the platform mode (see below)
9. **Validates `ILogger` registration at startup** via `app.Services.GetRequiredService<ILogger>()` — see [`logApiError` and ILogger validation](#logapierror-and-ilogger-validation) below

### Post-SAFE composition pipeline

The DI registration and middleware setup are spelled out directly against the raw ASP.NET Core APIs — no Saturn DSL. The composition reads top-to-bottom so it can be skimmed without learning a wrapper:

```fsharp skip=fragment
let builder = WebApplication.CreateBuilder()
builder.WebHost.UseUrls($"http://0.0.0.0:{serverPort}") |> ignore

let services = builder.Services
services.AddMemoryCache() |> ignore
services.AddDistributedMemoryCache() |> ignore     // AddSession depends on this
services.AddResponseCompression() |> ignore

services
    .AddSingleton<ILogger>(resolvedLogger)
    .AddSingleton<DataType list>(dataTypes)
    .AddSingleton<IBlobStorage>(resolvedBlobStorage)
    .AddSingleton<IEventStore>(eventStore)
    .AddSingleton<IAuthProvider>(auth)
    .AddSingleton<ISecretStore>(secretStore)
    .AddSingleton<SSEConnectionManager>(sseConnectionManager)
    .AddSingleton<INotificationChannel>(resolvedNotificationChannel)
    .AddSingleton<INarrativeStore>(narrativeStore)
    .AddSingleton<IPermissionStore>(PermissionStore(resolvedBlobStorage))
    .AddSingleton<IConfigStore>(ConfigStore.create resolvedBlobStorage)
    .AddSingleton<IFeatureFlagStore>(featureFlagStore)
    .AddSingleton<FlagEvaluator>(flagEvaluator)
    .AddSingleton<IModuleQueryBus>(moduleQueryBus)
|> ignore

// Team-mode-only: shared TeamStore instance reused by TeamScopeResolver
match teamStoreOpt with
| Some ts -> services.AddSingleton<ITeamStore>(ts :> ITeamStore) |> ignore
| None -> ()

// Companion DI registrations (AI, RAG, future distributed task companions)
match extensions.ServiceConfig with
| Some cfg -> cfg services |> ignore
| None -> ()

let scopeResolver: IStorageScopeResolver =
    match config.Mode with
    | Anonymous -> AnonymousScopeResolver()
    | AuthenticatedEphemeral -> AuthenticatedEphemeralScopeResolver()
    | Individual -> AuthenticatedScopeResolver()
    | Team -> TeamScopeResolver(teamStoreOpt.Value, MemoryCache(MemoryCacheOptions()))

services
    .AddSingleton<IStorageScopeResolver>(scopeResolver)
    .AddScoped<AccessContext>(fun sp -> ...)       // Per-request, reads from HttpContext.Items
    .AddHttpContextAccessor()
    .AddSession()
|> ignore

let app = builder.Build()
app.Services.GetRequiredService<ILogger>() |> ignore  // Startup validation
```

The middleware pipeline runs in this order (each step is conditional only where noted):

```fsharp skip=fragment
// Optional — production behind a TLS-terminating load balancer
if config.TrustForwardedHeaders then app.UseForwardedHeaders(opts) |> ignore
if config.RequireHttps then app.UseHttpsRedirection() |> ignore

app.UseResponseCompression() |> ignore
// Static files — behaviour controlled by config.StaticPathBehaviour
if Directory.Exists publicPath then app.UseStaticFiles(...)
else match config.StaticPathBehaviour with
     | Warn -> resolvedLogger.Warn "..."
     | RequireExist -> failwith "..."
     | SkipSilent -> ()

app.UseSession() |> ignore
app.UseMiddleware<ScopeResolutionMiddleware>() |> ignore
app.UseMiddleware<AuthEnforcementMiddleware>(config) |> ignore
app.UseMiddleware<RemotingBodyNormalizationMiddleware>() |> ignore

app.UseGiraffe router
app.Run()
```

### Production deployment knobs

Three `ServerConfig` fields exist because Saturn's defaults used to provide them implicitly; the post-SAFE pipeline makes them explicit so deployments can opt in:

| Field | Default | Purpose | Env var (reference app) |
|-------|---------|---------|-------------------------|
| `RequireHttps` | `false` | Register `app.UseHttpsRedirection()` ahead of scope resolution. Set `true` in production. | `TOOLUP_REQUIRE_HTTPS` |
| `TrustForwardedHeaders` | `true` (Phase 16d) | Register `app.UseForwardedHeaders(...)` honouring `X-Forwarded-Proto` / `X-Forwarded-For`. Default-on since Phase 16d — containerised / serverless deploys are almost always behind a TLS-terminating ingress (Cloud Run, ALB, App Service Front Door, AKS Ingress, function gateway), and without forwarded-headers trust the SDK misreports client IPs in audit logs and rejects HTTPS redirects. Set `TOOLUP_TRUST_FORWARDED_HEADERS=0` (or `=false` / `=no` / `=off`) on a direct-bind dev shell with no proxy hop. Unrecognised values crash startup. **Phase 325:** on an auth-requiring surface this must be paired with a `TrustedProxyCidrs` allowlist (`TOOLUP_TRUSTED_PROXY_CIDRS`) or the `TOOLUP_ACCEPT_FORWARDED_HEADERS_FROM_ANY_PROXY=1` escape hatch — see the row below. | `TOOLUP_TRUST_FORWARDED_HEADERS` |
| `TrustedProxyCidrs` | `[]` (Phase 325) | When non-empty, populates `ForwardedHeadersOptions.KnownIPNetworks` so `X-Forwarded-*` are trusted only from in-range peers (comma-separated CIDRs, e.g. `10.0.0.0/8,192.168.1.0/24`; IPv6 supported; a malformed entry fails loud at startup). Empty + `TrustForwardedHeaders = true` + no escape hatch is a preflight **Error in auth-requiring modes** (Anonymous keeps a Warning). | `TOOLUP_TRUSTED_PROXY_CIDRS` |
| `StaticPathBehaviour` | `Warn` | What to do when `PublicPath` doesn't exist on disk at startup. `Warn` (dev — Vite serves assets), `RequireExist` (production — fail loudly on missing artefact), `SkipSilent` (pure API deployments). | `TOOLUP_STATIC_PATH_BEHAVIOUR` (`warn|require|skip`) |

`TrustForwardedHeaders` on its own clears `KnownIPNetworks` and `KnownProxies` so headers from any peer are honoured — the default known-network limits (`127.0.0.1`, `::1`) are too narrow for typical cloud load balancers. Since Phase 325 an **auth-requiring** deployment must narrow that trust: set `TrustedProxyCidrs` (env `TOOLUP_TRUSTED_PROXY_CIDRS`) to the terminator's network(s) so only in-range peers are honoured, or attest a single header-stripping proxy fronts every request with `AcceptForwardedHeadersFromAnyProxy` (env `TOOLUP_ACCEPT_FORWARDED_HEADERS_FROM_ANY_PROXY=1`) — the unscoped trust-any-peer posture is otherwise a preflight **Error** on any non-Anonymous surface. Anonymous-only deployments keep the trust-any-peer default (a Warning, not an Error). Deployments with bespoke needs can still register a custom `ForwardedHeadersOptions` post-`compose`. See [`docs/migrations/325-forwarded-headers-cidr-trust.md`](../../../docs/migrations/325-forwarded-headers-cidr-trust.md).

### `logApiError` and ILogger validation

`logApiError` is the shared diagnostic path used by both `makeApi` and `makePermissionGuardedApi`. It resolves `ILogger` via `GetRequiredService<ILogger>()` per request — there is no `eprintfn` fallback. `compose` validates the registration once at startup (`app.Services.GetRequiredService<ILogger>() |> ignore`) so a missing `ILogger` registration crashes loudly at startup rather than dumping stack traces to a possibly-discarded stderr stream during the first request. This trades a silent-failure mode (where ToolUp.Remoting handler errors vanished into request-scoped exceptions in hosted scenarios with no stderr capture) for a single loud startup throw.

Each module's `Server.fs` exposes pure processing functions. The app's composition root assembles them into `HttpContext -> 'T` API factories, wraps each in `makeApi` (open access) or `makePermissionGuardedApi` (RBAC-gated), and attaches them to a `ServerModule` record via `ServerModule.withGuardedApi`. Crucially, API factory construction lives in the composition root — not in module `Server.fs` — because module projects only see shared types, not server-injected infrastructure like `FileManagement.getFileContents` or `makePermissionGuardedApi`.

Infrastructure services (auth, blob storage, event store, scope resolver) are registered in DI by compose and available to module server code via `HttpContext.RequestServices`.

### Composition seam (Phase 1f)

Apps extend the ASP.NET Core middleware chain through three opt-in surfaces, each landing at a documented pipeline position. None of the seams require touching `compose`:

**`ServerConfig.SecurityHeaders : Map<string, string>`** — every entry is stamped onto every response by `SecurityHeadersMiddleware`, registered ahead of every other middleware so headers go out on `200`, `401`, `429`, `404`, and any pre-middleware short-circuit. Per-route overrides keep working: a handler that sets a same-name header before `OnStarting` runs wins (the middleware uses `ContainsKey` to avoid clobbering). Common keys: `Content-Security-Policy`, `Strict-Transport-Security`, `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy`. Default is `Map.empty`.

**`ServerConfig.Cors : CorsConfig option`** — typed allowlist mapping to `Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicyBuilder`. `compose` calls `services.AddCors(...)` and `app.UseCors()` only when this is `Some`. Helpers:

```fsharp
// Wide-open dev / public API
{ ServerConfig.defaults with Cors = Some CorsConfig.permissive }

// Allowlist + credentials (the typical SaaS shape)
{ ServerConfig.defaults with Cors = Some (CorsConfig.forOrigins [ "https://app.example.com" ]) }
```

`AllowCredentials = true` cannot combine with wildcard origins (browser policy); `compose` logs a warning and falls back to non-credentialed mode. For per-route or dynamic-origin policies, use `withPreMiddleware` and register the policy by hand.

**`ServerApp.withPreMiddleware` / `withPostMiddleware`** — accumulate `IApplicationBuilder -> IApplicationBuilder` thunks at the **pre** position (after CORS / security headers, BEFORE `ScopeResolutionMiddleware` / `AuthEnforcementMiddleware`) and the **post** position (AFTER `app.UseGiraffe(router devRoutes)`, before `app.Run()`). Pre is for IP allowlists, custom auth-precondition rejection, request-shape sanitisation; post is for fallback handlers, custom 404 pages, debug-only routes that shouldn't be in the Giraffe surface. Thunks apply in registration order.

#### Pipeline position

```
ForwardedHeaders         ← if config.TrustForwardedHeaders
HttpsRedirection         ← if config.RequireHttps
SecurityHeadersMiddleware ← Phase 1f (always registered; no-op when SecurityHeaders is empty)
CspMiddleware            ← Phase 9j (always registered; no-op when SecurityHardening = NoSecurityHardening)
UseCors                  ← if config.Cors.IsSome
ResponseCompression
StaticFiles              ← when PublicPath exists
Session
CsrfMiddleware           ← Phase 9j (always registered; no-op when SecurityHardening = NoSecurityHardening)
[withPreMiddleware]      ← Phase 1f pre seam (before scope resolution)
ScopeResolutionMiddleware
AuthEnforcementMiddleware
RequestTimingMiddleware
RateLimiter              ← if config.RateLimit.IsSome
RemotingBodyNormalizationMiddleware
MapHealthChecks
UseGiraffe(router)
[withPostMiddleware]     ← Phase 1f post seam (after Giraffe)
Run
```

#### Worked example: CSP via `SecurityHeaders`

```fsharp
let private securityHeaders =
    Map.ofList [
        "Content-Security-Policy",
            "default-src 'self'; \
             script-src 'self' 'unsafe-inline' https://js.stripe.com; \
             connect-src 'self' https://api.example.com; \
             frame-ancestors 'none'"
        "Strict-Transport-Security", "max-age=31536000; includeSubDomains"
        "X-Frame-Options", "DENY"
        "Referrer-Policy", "strict-origin-when-cross-origin"
        "Permissions-Policy", "camera=(), microphone=(), geolocation=()"
    ]

let private config = {
    ServerConfig.defaults with
        Mode = Team
        SecurityHeaders = securityHeaders
        Cors = Some (CorsConfig.forOrigins [ "https://app.example.com" ])
}

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.addModules modules
|> ServerApp.run
```

#### Phase 9j — security hardening (`ICspContributor` + CSRF)

`ServerConfig.SecurityHeaders` (above) is the *static* path — a hand-maintained header map. Phase 9j adds a *companion-aware* path that generates the CSP correct-by-construction and adds a CSRF guard. Both compose: an already-present `Content-Security-Policy` header (from the `SecurityHeaders` map or a per-route handler) always wins — `CspMiddleware` only sets the header when absent.

**Opt-in (GP 13).** `ServerConfig.SecurityHardening` defaults to `NoSecurityHardening`: `CspMiddleware` / `CsrfMiddleware` no-op and `/api/csrf-token` is not mounted, so a stock deployment is byte-for-byte unchanged. Opt in with `ServerApp.withSecurityHardening DefaultSecurityHardening` (or `StrictSecurityHardening`).

**`ICspContributor`.** Every subsystem/companion that needs a non-`'self'` origin declares it; `SecurityHardening.aggregate` walks the `IServiceCollection` at compose time (same instance-descriptor contract as `IHealthCheck` / `IConfigValidator`) and folds them into one header. First-party defaults auto-registered when hardening is on: the OIDC issuer (from `TOOLUP_OIDC_ISSUER`, inert if unset) and the AI provider hosts (`api.anthropic.com`, `api.openai.com`) → `connect-src`. A CDN-delivered grid host is opt-in via `ServerApp.withCspContributor (AgGridCdnCspContributor())`. Companions register their own with `services.AddSingleton<ICspContributor>(...)` through the `ComposeExtensions.ServiceConfig` hook.

```fsharp skip=fragment
type StripeCspContributor() =
    interface ICspContributor with
        member _.RequiredSources =
            [ ScriptSrc "https://js.stripe.com"; FrameSrc "https://hooks.stripe.com" ]

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withSecurityHardening DefaultSecurityHardening
|> ServerApp.withCspContributor (StripeCspContributor())
|> ServerApp.addModules modules
|> ServerApp.run
```

**What Default vs Strict produce.** Default: `script-src 'self'` (the client is Vite-bundled same-origin — no `'unsafe-inline'` for scripts at any level), `style-src 'self' 'unsafe-inline'` (Feliz dynamic styles + Tailwind), plus `connect-src` / `img-src 'self' data: blob:` / `font-src 'self' data:`, `frame-ancestors 'none'`, `form-action 'self'`, `base-uri 'self'`, and `frame-src 'none'` unless a contributor adds a frame origin. **Strict** additionally drops `'unsafe-inline'` from `style-src` (the deployment must serve nonce-driven tags) and adds `object-src 'none'` + `upgrade-insecure-requests`.

**CSRF token lifecycle.** A 256-bit base64url token is minted per ASP.NET session and surfaced by `GET /api/csrf-token` (`{"Token":"..."}`). The SDK client shell pre-fetches it once at startup (`CsrfClient.prefetch`, called from `SDK.Client.program`) and `UserSession.withRequestHeaders` attaches it as `X-CSRF-Token` to *every* ToolUp.Remoting call — no per-call-site change. `CsrfMiddleware` fixed-time-compares the header to the session token on state-changing (`POST`/`PUT`/`PATCH`/`DELETE`) `/api/*` requests and returns `403 {"error":"csrf_validation_failed"}` on mismatch. Exemptions: the token endpoint, `PeerRoutePrefixes` (the bearer IS the auth), and `AnonymousRoutePrefixes` (share-token-gated public writes have no session). The cross-origin attacker can ride the session cookie but cannot read the token (CORS protects the GET response) nor guess it.

**SameSite cookie (defence-in-depth).** Alongside the JSON the endpoint sets a `XSRF-TOKEN` cookie with `Path=/; SameSite=Strict` (`Secure` on HTTPS). This is *not* the primary check — the session-bound synchroniser token is — it is a second, independent reason a cross-site forged request fails.

#### Worked example: per-team IP allowlist via `withPreMiddleware`

The thunk runs before scope resolution, but `AccessContext.TeamId` is not yet populated — the resolver runs later. Use the request's resolved user (set by `ScopeResolutionMiddleware` when present) only when you also register an earlier copy of the resolver, or read team metadata from a header / token claim that's available before scope resolution. The simpler shape is to gate on `Request.Headers["X-Team-Slug"]` (set by an upstream proxy) or on the resolved user's tenant id from the auth provider:

```fsharp
let private allowlistMiddleware (allowlist: Map<string, string list>) =
    fun (app: IApplicationBuilder) ->
        app.Use(fun (ctx: HttpContext) (next: Func<Task>) ->
            task {
                let teamSlug =
                    match ctx.Request.Headers.TryGetValue "X-Team-Slug" with
                    | true, vs when vs.Count > 0 -> vs.[0]
                    | _ -> ""

                let remoteIp = ctx.Connection.RemoteIpAddress |> string

                let allowed =
                    match Map.tryFind teamSlug allowlist with
                    | None -> true                        // team not gated
                    | Some allowed -> List.contains remoteIp allowed

                if allowed then
                    do! next.Invoke()
                else
                    ctx.Response.StatusCode <- 403
                    do! ctx.Response.WriteAsync($"team {teamSlug} blocks {remoteIp}")
            } :> Task)

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withPreMiddleware (allowlistMiddleware Allowlists.byTeam)
|> ServerApp.addModules modules
|> ServerApp.run
```

For an allowlist that needs the resolved `AccessContext.TeamId`, register the gate as Giraffe handler middleware inside the router (so `ScopeResolutionMiddleware` has already run) rather than at the pre seam.

#### Diagnostics

`/dev/inspect` (Phase 9a, debug + `EnableDevEndpoints = true`) exposes a "Composition seam" panel listing pre/post hook counts, configured `SecurityHeaders` keys (values redacted), CORS active flag, and registered notification consumers. Use it to confirm what the consumer wired without trawling logs or service descriptors.

### Composition audit (Phase 1g)

Phase 1g (lightweight composition profile) audits every always-on registration in `compose`, `composeWithAI`, and `composeWithRAG` against Guiding Principle 13 ("the lightweight default is the SDK's primary product"). Each row classifies the registration as `Always` (truly required for the SDK to function), `OptIn` (gates on a `ServerConfig` mode field), or `DebugOnly` (compiled out in Release).

| Registration | Class | Gating field | File:line |
|--------------|-------|--------------|-----------|
| `ILogger` | Always | — | `SDK.Server.fs:1146` |
| `IBlobStorage` | Always | constructor arg | `SDK.Server.fs:1148` |
| `IDataObjectStore` | Always | — | `SDK.Server.fs:1149` |
| `IDataCatalog` | Always | — | `SDK.Server.fs:1150` |
| `IEventStore` (inner) | OptIn | `config.EventStore` | `SDK.Server.fs:973–979` |
| `IAuthProvider` | Always | constructor arg | `SDK.Server.fs:1153` |
| `ISecretStore` | Always | constructor arg | `SDK.Server.fs:1154` |
| `IStorageScopeResolver` | OptIn (surface-driven) | `config.Surfaces` | `SDK.Server.fs:1373` |
| `ITeamStore` | OptIn | team surface declared in `config.Surfaces` | `SDK.Server.fs:1295–1307` |
| `INotificationChannel` + `SSEConnectionManager` + `/api/notifications` | OptIn | `config.Notifications` (auto-detected) | `SDK.Server.fs:820–831, 1155–1156` |
| `IAuditLog` | OptIn | `config.AuditLog` | `SDK.Server.fs:1152` |
| `IWebhookRegistry` + `IWebhookDeliveryLog` + `IWebhookDispatcher` | OptIn | `config.Webhooks` | `SDK.Server.fs:996–1009, 1158–1163` |
| `WebhookDispatcher` `BackgroundService` | OptIn | `config.Webhooks` | `SDK.Server.fs:1161–1163` |
| `HookedEventStore` decorator wrapping `IEventStore` | OptIn | `config.Webhooks` | `SDK.Server.fs:1015–1016` |
| `IResultStore` | OptIn | `config.ResultStore` | `SDK.Server.fs:1124–1143, 1170` |
| `ILineageStore` | OptIn | `config.Lineage` | `SDK.Server.fs:1110–1113, 1176` |
| `IJobStore` + `IJobScheduler` + `InProcessJobScheduler` `BackgroundService` | OptIn | `config.JobScheduler` | `SDK.Server.fs:1186–1210` |
| `IDataSourceConfigStore` + `IDataIngestor` | OptIn | `config.DataIngestion` | `SDK.Server.fs:1218–1251` |
| `RateLimiter` middleware | OptIn | `config.RateLimit` | `SDK.Server.fs:1273–1277` |
| `ScopeResolutionMiddleware` | Always | — | pipeline |
| `AuthEnforcementMiddleware` | Always (surface-aware) | `config.Surfaces` | pipeline |
| `RequestTimingMiddleware` | OptIn | `config.SlowRequestThreshold` | pipeline |
| `RemotingBodyNormalizationMiddleware` | Always | — | pipeline |
| `platformInfoApiHandler` / `teamApiHandler` / `permissionApiHandler` / `accessibilityApiHandler` / `dataCatalogApiHandler` | Always (surface trims for anonymous subjects) | `config.Surfaces` | router |
| `configHandler` / `featureFlagHandler` / `moduleQueryBusHandler` | Always | — | router |
| `webhookHandler` | OptIn | `config.Webhooks` | router |
| `fileManagementHandler` | OptIn | non-empty `dataTypes` | router |
| `jobApiHandler` | OptIn | `config.JobScheduler` | router |
| `dataIngestionApiHandler` | OptIn | `config.DataIngestion` | router |
| `/dev/inspect` | OptIn | `config.EnableDevEndpoints` | `SDK.Server.fs` (runtime-only since Phase 11.B; was previously `#if DEBUG`-gated too) |
| `IAIProviderFactory` + AI handlers (companion) | OptIn (companion) | `composeWithAI` invocation | `AICompose.fs:89–94` |
| `IVectorStore` + RAG `BackgroundService`s (companion) | OptIn (companion) | `composeWithRAG` invocation | `RAGCompose.fs:360–483` |

**Resolution rules:**

1. `Notifications = NotificationsAuto` (the default) flips to `InMemoryNotifications` whenever any of the following is true:
   - `config.JobScheduler <> NoJobScheduler` (background jobs publish dead-letter notifications)
   - `config.Mode = MultiTeam` (membership-change events feed the client team-switch reset path)
   - `composeWithAI` or `composeWithRAG` is wrapping `compose` (each adds itself to `extensions.NotificationConsumers`)
   - Otherwise `NoNotifications` — the SSE endpoint and `InMemoryNotificationChannel` are not registered.
2. `AuditLog = NoAuditLog` (the default) replaces `EventStoreAuditLog` with `NoOpAuditLog`. Emission callsites (`ScopeResolutionMiddleware`, the platform-API handlers — `TeamApi` / `PermissionApi` — and `fileManagementApi`) keep their unconditional `IAuditLog.Record` calls — the no-op swallows them, so callsites stay clean and future emission additions don't need a mode check. This deviates from the original "interface unregistered" framing in favour of single-touchpoint gating.
3. `Webhooks = NoWebhooks` (the default) skips every webhook registration: `WebhookDispatcher` `BackgroundService`, `HookedEventStore` decorator (the inner `IEventStore` is registered directly), `IWebhookRegistry` / `IWebhookDeliveryLog` / `IWebhookDispatcher` DI services, and `webhookHandler` from the router.

The minimum-viable shape — `ServerApp.empty |> ServerApp.run` with all defaults — registers zero `BackgroundService`s beyond ASP.NET Core's own infrastructure, mounts no SSE endpoint, and has a `NoOpAuditLog` swallowing all audit emissions. Verified in CI by `src/ToolUp.Platform.Tests/InProcess/MinimumViableShapeTests.fs`.


---

> ← Prev: _(none)_ · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 2. Multi-Tenancy, Teams & Access Control →](02-multi-tenancy-and-access.md)
