# Phase 11.G — `fromEnv` helpers (consumer migration)

**What changes.** SDK adds env-var-driven construction helpers for every common composition-root substrate dimension (logger, secret store, blob storage, notification channel, auth provider, server config), plus a client-side bundle-constants helper. Reference consumers shrink composition roots from ~1000 lines of env-var dispatch to ~30 lines of substrate construction.

**Env-var contract.** Every `TOOLUP_*` env var honoured by the pre-11.G hand-written reference composition root is honoured by the new helpers with byte-for-byte identical fallback messages. Startup-log diff before/after migration is empty across all five `PlatformMode` paths.

**Scope.** Forge SDK ships the helpers + a runnable sample (`samples/MinimalApp/`) + this doc. Consumer adoption is opt-in per deployment.

## Helper surface (verbatim signatures)

| Helper | Module | Replaces (lines in the reference consumer's Server.fs pre-11.G) |
|---|---|---|
| `ConsoleLogger.fromEnv ()` | `ToolUp.Platform.ConsoleLogger` | 80–105 (`resolvedLogLevel` + `resolvedTraceCategories` + `let private logger`) |
| `ConsoleLogger.envSettings ()` | `ToolUp.Platform.ConsoleLogger` | (helper to mirror level + categories into `ServerConfig`) |
| `SecretStore.fromEnv logger resolvers` | `ToolUp.Platform.SecretStore` | 174–211 (entire `TOOLUP_SECRET_STORE` dispatch) |
| `BlobStorageEnv.fromEnv logger resolvers` | `ToolUp.Platform.BlobStorageEnv` | 215–242 (entire `TOOLUP_BLOB_STORAGE` dispatch) |
| `NotificationChannel.fromEnv logger resolvers` | `ToolUp.Platform.NotificationChannel` | 256–297 (entire `TOOLUP_NOTIFICATION_CHANNEL` dispatch + triple) |
| `AuthProvider.fromEnv logger oidcBuilder` | `ToolUp.Platform.AuthProvider` | 132–162 (entire `TOOLUP_AUTH_MODE` dispatch) |
| `ServerConfig.fromEnv logger overrides` | `ToolUp.Platform.ServerConfig` | 704–875 (entire `ServerConfig.defaults with …` block — 170 lines) |
| `HealthCheckDefaults.fromEnv builders secret notifProbe` | `ToolUp.Platform.HealthCheckDefaults` | 939–944 (`withHealthCheck` chain) |
| `ConfigValidators.fromEnv oidcResolver notifValidator` | `ToolUp.Platform.ConfigValidators` | 950–956 (`withConfigValidator` chain) |
| `BundleConstants.{moduleFilter,agGridLicense,clerkPublishableKey}` | `ToolUp.Platform.BundleConstants` | client-side 19–42 (three `[<Emit>]` lets) |
| `ClientConfigDefaults.fromBundleConstants overrides` | `ToolUp.Platform.ClientConfigDefaults` | client-side 118–159 (entire `ClientConfig.defaults with …` block) |

### Naming caveats (Core-module collision)

Three helpers carry suffixed module names because the obvious name was already taken by an interface module in `ToolUp.Platform.Core` (and F# doesn't merge top-level modules across assemblies):

- `BlobStorageEnv.fromEnv` (not `BlobStorage.fromEnv`) — Core's `module BlobStorage` carries `BlobMetadata` + `IBlobStorage`.
- `HealthCheckDefaults.fromEnv` (not `HealthChecks.fromEnv`) — Core's `module HealthChecks` carries `IHealthCheck`.

`ConsoleLogger.fromEnv` / `NotificationChannel.fromEnv` / `ServerConfig.fromEnv` augment their existing Server-side / Core-side modules directly — no suffix needed.

## Substrate-cleanliness: cloud-companion resolvers

`SecretStore.fromEnv`, `BlobStorageEnv.fromEnv`, and `NotificationChannel.fromEnv` take **resolver lists** so `ToolUp.Platform.Server` doesn't take hard `<PackageReference>` deps on every cloud companion. The consumer threads in one entry per companion the deployment has wired:

```fsharp
let secretStore =
    SecretStore.fromEnv logger [
        { Name = "azure-key-vault";     Resolve = ToolUp.Secrets.AzureKeyVault.fromEnv }
        { Name = "aws-secrets-manager"; Resolve = ToolUp.Secrets.AwsSecretsManager.fromEnv }
        { Name = "vault";               Resolve = ToolUp.Secrets.HashiCorpVault.fromEnv }
    ]

let blobStorage =
    BlobStorageEnv.fromEnv logger [
        { Name = "azure"; Resolve = fun () -> ToolUp.Storage.AzureBlobStorage.fromEnv None }
        { Name = "s3";    Resolve = ToolUp.Storage.AwsS3Storage.fromEnv }
        { Name = "gcs";   Resolve = ToolUp.Storage.GoogleCloudStorage.fromEnv }
    ]

let notificationChannel, notifChannelHealth, notifChannelValidator =
    NotificationChannel.fromEnv logger [
        { Name = "redis"
          ConnectionEnvVar = "TOOLUP_REDIS_CONNECTION"
          Resolve =
            fun lg connStr ->
                let mux, channel =
                    ToolUp.Platform.NotificationChannels.Redis.RedisNotificationChannel.connect connStr (Some lg)

                Some(
                    channel,
                    Some(ToolUp.Platform.NotificationChannels.RedisHealth.RedisNotificationChannelHealth.create mux),
                    Some(ToolUp.Platform.NotificationChannels.RedisValidator.create mux)
                ) }
    ]

let authProvider =
    AuthProvider.fromEnv logger ToolUp.AuthProviders.OidcAuthProvider.fromConfig
```

Consumers that don't ship a given cloud companion simply omit the corresponding resolver — `TOOLUP_SECRET_STORE=azure-key-vault` in a consumer without the `ToolUp.Secrets.AzureKeyVault` reference falls back to encrypted-local with a Warn, same posture as today.

**Embedding-provider parity (Phase 671, post-11.G).** `EmbeddingProviderEnv.fromEnv logger resolvers fallback` (`ToolUp.Platform.EmbeddingProviderEnv`) completes the set for RAG deployments, on the same resolver-list shape as the three above; the companions supply `LocalEmbeddingProvider.fromEnv` and `OpenAIEmbeddingProvider.fromEnv`, and the `TOOLUP_EMBEDDING_MODEL` / `_DIMENSIONS` / `_BATCH_SIZE` cluster tunes the selected one. **It has no SDK-side default to replace** — an embedding provider is a companion, so what it preserves is the consumer's own explicit `RAGServerApp.create … embedder` argument, threaded in as the third parameter. That is why its unset arm returns the fallback and logs *nothing*, where its three siblings announce the default they construct: announcing here would break the empty-startup-log-diff contract at the top of this doc for every deployment that adopted the helper and changed no configuration. The API key is not part of the cluster and will not be — it resolves through `ISecretStore` at embed time.

**Auth-dispatch hardening (2026-06-12, post-11.G).** `AuthProvider.fromEnv` / `fromEnvMetered` no longer Warn-and-fall-back on explicit misconfiguration: `TOOLUP_AUTH_MODE=oidc` without `TOOLUP_OIDC_ISSUER`, and any unrecognised `TOOLUP_AUTH_MODE` value, refuse startup via `invalidOp`. Only a genuinely-unset `TOOLUP_AUTH_MODE` keeps the dev `HeaderAuthProvider` fallback (GP 11 — the unset-everything dev posture is unchanged). The "byte-for-byte identical fallback messages" contract stated at the top of this doc therefore no longer covers the two explicit-misconfiguration arms of the auth dispatch — those now fail the boot deliberately.

## `ServerConfigOverrides`

Curated record carrying the small set of fields that need consumer-specific values on top of `ServerConfig.fromEnv`'s env-var-derived baseline:

```fsharp
{ ServerConfigOverrides.referenceApp with
    PublicPath = Some "public"
    SlowRequestThresholdOverrides = Some slowRequestOverrides
    EnableDevEndpoints =
#if DEBUG
        Some true
#else
        Some false
#endif
    AutoBootstrapDevAdmin =
#if DEBUG
        Some "dev-admin"
#else
        None
#endif }
```

`ServerConfigOverrides.referenceApp` pre-bundles `Webhooks = EnabledWebhooks`, `AuditLog = EnabledAuditLog`, `SecurityHardening = DefaultSecurityHardening`. Consumers not in the reference posture use `ServerConfigOverrides.empty` and add their own overrides selectively.

## Before / after — reference consumer `Server.fs`

### Before (lines 43–298 of pre-11.G `Server.fs`)

```fsharp
// ~250 lines of:
//   - envVar helper
//   - resolvedLogLevel / resolvedTraceCategories parsing
//   - let private logger = ConsoleLogger(...)
//   - secretsMasterKey extraction
//   - `do match TOOLUP_PLATFORM_MODE with ...` warning block
//   - authProvider dispatch on TOOLUP_AUTH_MODE
//   - secretStore dispatch on TOOLUP_SECRET_STORE
//   - blobStorage dispatch on TOOLUP_BLOB_STORAGE
//   - notificationChannel triple build from TOOLUP_NOTIFICATION_CHANNEL
```

### After

```fsharp
let logger = ConsoleLogger.fromEnv ()

let secretStore =
    SecretStore.fromEnv logger [
        { Name = "azure-key-vault";     Resolve = ToolUp.Secrets.AzureKeyVault.fromEnv }
        { Name = "aws-secrets-manager"; Resolve = ToolUp.Secrets.AwsSecretsManager.fromEnv }
        { Name = "vault";               Resolve = ToolUp.Secrets.HashiCorpVault.fromEnv }
    ]

let blobStorage =
    BlobStorageEnv.fromEnv logger [
        { Name = "azure"; Resolve = fun () -> ToolUp.Storage.AzureBlobStorage.fromEnv None }
        { Name = "s3";    Resolve = ToolUp.Storage.AwsS3Storage.fromEnv }
        { Name = "gcs";   Resolve = ToolUp.Storage.GoogleCloudStorage.fromEnv }
    ]

let notificationChannel, notifHealth, notifValidator =
    NotificationChannel.fromEnv logger [ Wiring.redisResolver ]

let authProvider = AuthProvider.fromEnv logger ToolUp.AuthProviders.OidcAuthProvider.fromConfig
```

`Wiring.redisResolver` lives in a sibling `Wiring.fs` file alongside other per-deployment construction (algorithm provider singletons, AI provider descriptors / builders / platform bundle, system-prompt composition, per-module API factories). See [`docs/platform/composition-roots.md`](../platform/composition-roots.md) for the recommended layout.

### Before (lines 704–875 of pre-11.G `Server.fs`)

```fsharp
let private config = {
    ServerConfig.defaults with
        PublicPath = "public"
        Mode = platformMode ()
        ModuleFilter = envVar "TOOLUP_MODULE"
        RequireHttps = envFlag "TOOLUP_REQUIRE_HTTPS"
        // … 165 more lines of field-by-field env reads + validators
}
```

### After

```fsharp
let config =
    ServerConfig.fromEnv logger {
        ServerConfigOverrides.referenceApp with
            PublicPath = Some "public"
            SlowRequestThresholdOverrides = Some Wiring.slowRequestOverrides
            EnableDevEndpoints =
#if DEBUG
                Some true
#else
                Some false
#endif
            AutoBootstrapDevAdmin =
#if DEBUG
                Some "dev-admin"
#else
                None
#endif
    }
```

## Client-side migration — reference consumer's `Client.fs`

### Before (lines 19–159 of pre-11.G `Client.fs`)

```fsharp
[<Emit("__TOOLUP_MODULE__")>]
let private toolupModuleFilter: string = jsNative

[<Emit("__AG_GRID_LICENSE__")>]
let private agGridLicenseKey: string = jsNative

[<Emit("__CLERK_PUBLISHABLE_KEY__")>]
let private clerkPublishableKey: string = jsNative

let private gridModules = AgGridEnterprise.gridModuleConfig agGridLicenseKey
AgGridEnterprise.registerCharts ()

let private aiMode = ConfiguredAIAssistant { ... }
let private authUI = #if DEBUG NoAuthUI #else ClerkAuthUI { ... } #endif
let private handlers = { ... }
// … 30 more lines DEBUG-gated console-trace / dev-admin flags
let private config = { ClientConfig.defaults with … }   // ~40 lines
```

### After

```fsharp
let private gridModules = AgGridEnterprise.gridModuleConfig BundleConstants.agGridLicense
AgGridEnterprise.registerCharts ()

let private authUI =
#if DEBUG
    NoAuthUI
#else
    if System.String.IsNullOrEmpty BundleConstants.clerkPublishableKey then
        failwith
            "CLERK_PUBLISHABLE_KEY was empty when bundling this Release build. Set it in CI (see DEPLOYMENT_CI.md) and rebuild."

    ClerkAuthUI { PublishableKey = BundleConstants.clerkPublishableKey }
#endif

let private config =
    ClientConfigDefaults.fromBundleConstants {
        ClientConfigOverrides.referenceApp with
            AppName = Some "ToolUp"
            AppLogo = Some "favicon.png"
            Mode = Some Individual
            GridModules = Some gridModules
            AuthUI = Some authUI
            Handlers =
                Some {
                    ClientHandlerRegistry.empty with
                        AuthUIHandlers =
#if DEBUG
                            []
#else
                            [ ToolUp.AuthProviders.ClerkRegister.handler ]
#endif
                        NarrativeCommitHandler = Some KnowledgeBaseView.narrativeCommitHandler
                }
            PublicEntryDispatchers = Some Wiring.publicEntryDispatchers
            EnableElmishConsoleTrace =
#if DEBUG
                Some true
#else
                Some false
#endif
            ShowDebugOnlyModules =
#if DEBUG
                Some true
#else
                Some false
#endif
            DevDefaultUserId =
#if DEBUG
                Some "dev-admin"
#else
                None
#endif
    }

AIClientConfig.run aiMode config modules
```

## Fable `[<Emit>]` propagation (open question, verify with sample)

`BundleConstants.fs` reads the three Vite-injected constants via `[<Emit>]` declarations *inside* `ToolUp.Platform.Client`. For consumer projects that take `ToolUp.Platform.Client` via `<ProjectReference>` (in-tree samples + templates) or via the Fable source-in-nupkg path (every NuGet consumer), Vite's `define` substitution should reach the emitted JS regardless of which source file the `[<Emit>]` originated in — Vite substitutes textually against the produced JS, and Fable emits one JS file per F# file (cross-project or not).

**Verify empirically:** the `samples/MinimalApp/` build proves this. If the sample sees `__TOOLUP_MODULE__` as a literal at runtime (not the empty string from the typeof-guard, not the value from Vite's `define`), consumers should use `ClientConfigDefaults.fromBundleConstantValues` and pass values from their own `[<Emit>]` declarations.

## Verification steps (consumer adoption PR)

1. **Startup-log diff.** Boot the app pre- and post-migration with the same env var values. Diff the first 200 lines of startup output. Should be empty across all five `PlatformMode` paths (Anonymous, AuthenticatedEphemeral, Individual, Team, MultiTeam).
2. **Validator chain.** Phase 6m mode-validators (`HeaderAuthProviderModeValidator`, `EncryptedSecretStoreModeValidator`, `InProcessJobSchedulerModeValidator`, `RateLimitModeValidator`, `SseAuthModeValidator`) still fire on the same env-var settings — no regression.
3. **`dotnet build` clean** on the consumer's sln after the migration.
4. **Fable JS** — `dotnet fable -o output` from the client project + spot-check that the Vite-`define` values substitute into the bundled JS for `__TOOLUP_MODULE__` etc.
5. **Line count.** `Server.fs` ≤ 50 executable lines, `Client.fs` ≤ 30. Algorithm singletons + per-module API factories + system-prompt composition move to a new sibling `Wiring.fs` file.

## Rollback

Each helper is a strict superset of the pre-11.G hand-written code. Revert the consumer PR if the migration causes any regression; the SDK helpers stay shipped (additive — they don't change the hand-written code path's behaviour).
