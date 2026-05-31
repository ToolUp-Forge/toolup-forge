# Composition roots

The idiomatic ToolUp composition root collapses to one screen of substrate construction + a composition pipeline. SDK env-var-driven helpers (`*.fromEnv`) handle the repetitive env-var dispatch; the deployment names what it cares about in an overrides record and an optional `Wiring.fs` sidecar.

## The five-step pattern

Every ToolUp composition root follows the same five steps:

```fsharp
// 1. Logger from env (TOOLUP_LOG_LEVEL + TOOLUP_TRACE_CATEGORIES).
let logger = ConsoleLogger.fromEnv ()

// 2. Substrates from env (TOOLUP_SECRET_STORE, TOOLUP_BLOB_STORAGE,
//    TOOLUP_NOTIFICATION_CHANNEL, TOOLUP_AUTH_MODE). Each takes the
//    cloud companions the deployment has wired as a resolver list.
let secretStore = SecretStore.fromEnv logger Wiring.secretStoreResolvers
let blobStorage = BlobStorageEnv.fromEnv logger Wiring.blobStorageResolvers
let notifChannel, notifHealth, notifValidator =
    NotificationChannel.fromEnv logger Wiring.notificationResolvers
let authProvider = AuthProvider.fromEnv logger ToolUp.AuthProviders.OidcAuthProvider.fromConfig

// 3. ServerConfig from env + curated overrides.
let config =
    ServerConfig.fromEnv logger {
        ServerConfigOverrides.referenceApp with
            PublicPath = Some "public"
            SlowRequestThresholdOverrides = Some Wiring.slowRequestOverrides
    }

// 4. Application-specific wiring (algorithm singletons, AI provider
//    descriptors / builders / platform bundle, per-module API factories,
//    system-prompt composition). Lives in a sibling Wiring.fs file.
let aiProviderFactory = Wiring.aiProviderFactory secretStore Wiring.aiConfigStore blobStorage

// 5. Composition pipeline (RAGServerApp / AIServerApp / ServerApp).
[<EntryPoint>]
let main _ =
    RAGServerApp.create aiProviderFactory Wiring.aiConfigStore (EmbeddingProviderEnv.create blobStorage)
    |> RAGServerApp.withConfig config
    |> RAGServerApp.withAuth authProvider
    |> RAGServerApp.withLogger logger
    |> RAGServerApp.withStorage blobStorage
    |> RAGServerApp.withNotifications notifChannel
    |> RAGServerApp.addModules Wiring.allModules
    |> RAGServerApp.run
```

That's typically 25–50 lines. The hand-written reference composition root pre-11.G was ~980 lines.

## What goes in `Wiring.fs`

A sibling file alongside `Server.fs` carrying the deployment-specific constructions that don't belong in the SDK helpers:

- **Algorithm provider singletons** — stateless app-domain implementations (`MathNetElasticityEstimator`, `LevenbergMarquardtCurveFitter`, etc.). Created once, shared across requests.
- **Cloud-companion resolver lists** — `secretStoreResolvers`, `blobStorageResolvers`, `notificationResolvers`. Naming the cloud companions the deployment ships.
- **Per-module API factories** — the `xxxApi (ctx: HttpContext) : XxxApi` constructions threading scope into module-domain routines.
- **AI provider descriptors + builders + platform bundle** — Claude/OpenAI/etc. descriptors, `AIProviderBuilder` records, the `AIPlatformProvider` bundle. `aiProviderFactory` constructed from these via `DefaultAIProviderFactory.create … PlatformOnly (Some bundle)`.
- **`allModules` list** — every `ServerModule.create … |> ServerModule.withGuardedApi …` chain.
- **System-prompt composition** — `Prompt.compose [...]` for the AI assistant's standing context.
- **`slowRequestOverrides`** — per-route `Map<string, TimeSpan>` for the deployment's known-slow paths.

Keeping these in `Wiring.fs` means `Server.fs` reads as a manifest: "what substrates does this app use, and how do they compose?" without the construction noise.

## Client-side pattern

```fsharp
// 1. AG Grid Enterprise + Clerk read once from the SDK's bundle-constants helper.
let gridModules = AgGridEnterprise.gridModuleConfig BundleConstants.agGridLicense
AgGridEnterprise.registerCharts ()

let authUI =
#if DEBUG
    NoAuthUI
#else
    ClerkAuthUI { PublishableKey = BundleConstants.clerkPublishableKey }
#endif

// 2. ClientConfig from bundle constants + curated overrides.
let config =
    ClientConfigDefaults.fromBundleConstants {
        ClientConfigOverrides.referenceApp with
            AppName = Some "MyApp"
            Mode = Some Individual
            GridModules = Some gridModules
            AuthUI = Some authUI
            Handlers = Some Wiring.handlers
    }

// 3. Module registration list (consumer's modules).
let modules = Wiring.allModules

// 4. Run.
Client.run config modules
```

Three SDK helpers — `BundleConstants`, `ClientConfigDefaults.fromBundleConstants`, `Client.run` — plus a `Wiring.fs` sibling for handlers and modules.

## When to use the helpers vs roll your own

**Use the helpers when:** the deployment honours the standard `TOOLUP_*` env var contract documented in [`surfaces.md`](surfaces.md) + each substrate's doc.

**Roll your own when:** the deployment has a non-standard env-var scheme (different prefix, custom dispatch), needs synchronous bootstrap-time validation the helpers don't perform, or composes substrates that don't fit the resolver-list shape (e.g. multi-storage layering). Helpers are additive — you can call them for the dimensions that fit the standard pattern and hand-roll the dimensions that don't.

## See also

- [`surfaces.md`](surfaces.md) — the Subject / `SurfaceProfile` / `SurfaceRequirement` model and the `TOOLUP_PLATFORM_SURFACES` env-var contract.
- [`../migrations/11g-fromenv-helpers.md`](../migrations/11g-fromenv-helpers.md) — full before/after diffs for the reference consumer migration.
- [`../../samples/MinimalApp/`](../../samples/MinimalApp/) — runnable Anonymous-mode sample showing the pattern end-to-end.
