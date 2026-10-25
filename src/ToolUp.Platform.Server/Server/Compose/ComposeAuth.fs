module ToolUp.Platform.ComposeAuth

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage

// ─── compose phase: auth provider + Platform Admin store ─────────────
//
// `IAuthProvider` resolution + Platform Admin store + Platform Runtime
// Config store. Extracted from `compose` for the per-concern subdivision
// (Phase 15e follow-up). Takes the exact substrate values the inline
// definition captured and returns the same shape. Zero behaviour change.

/// Phase 1051 — a platform store's boot-time bootstrap threw, so the boot
/// is refused rather than started without it.
///
/// A degraded entry is for a fault at RUNTIME, which a running deployment
/// can recover from; a store that cannot be read at boot would start the
/// deployment with no platform admin, a stub runtime-config store or no
/// bootstrap team, and nothing would say so but one log line. `store`
/// names what failed and `remedy` what the operator does about it; `cause`
/// is the exception the store raised. Typed so a host can tell this
/// refusal from any other startup fault.
exception BootstrapRefused of store: string * remedy: string * cause: exn with
    override this.Message =
        sprintf
            "[%s] bootstrap failed at boot, so the boot is refused rather than started without it: %s. %s"
            this.store
            this.cause.Message
            this.remedy

/// Phase 1051 — carries a `BootstrapRefused` to the startup preflight as an
/// `Error` row, which refuses the boot.
///
/// The refusal rides the preflight rather than being raised where the
/// bootstrap ran, because the bootstrap runs BEFORE the preflight: a
/// storage that is down altogether fails the bootstrap read too, and
/// raising there would replace the storage validator's diagnosis with
/// this one. Through the preflight both rows are in the one refusal, the
/// storage validator's first. Structural class, so `SkipPreflight` cannot
/// skip it: a deployment that started anyway would be exactly the silent
/// start this refuses.
type BootstrapFaultValidator(fault: exn) =
    /// The `BootstrapRefused` this row reports.
    member _.Fault = fault

    interface ConfigValidation.IStructuralClassValidator

    interface ConfigValidation.IConfigValidator with
        member _.Name =
            match fault with
            | BootstrapRefused(store, _, _) -> sprintf "bootstrap-fault (%s)" store
            | _ -> "bootstrap-fault"

        member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout
        member _.Validate() = async { return ConfigValidation.ValidationResult.Error fault.Message }

/// Phase 1051 — record a bootstrap fault: log it at `Error` and register the
/// `BootstrapFaultValidator` that refuses the boot at preflight.
let refuseAtPreflight
    (services: IServiceCollection)
    (resolvedLogger: ILogger)
    (store: string)
    (remedy: string)
    (cause: exn)
    : unit =
    let fault = BootstrapRefused(store, remedy, cause)
    resolvedLogger.Error(fault.Message, Some cause)

    services.AddSingleton<ConfigValidation.IConfigValidator>(
        BootstrapFaultValidator(fault) :> ConfigValidation.IConfigValidator
    )
    |> ignore

/// Resolve the auth provider, defaulting to `HeaderAuthProvider` when
/// the app didn't supply one. `HeaderAuthProvider` is spoofable; safe
/// only because the security-class `HeaderAuthProviderModeValidator`
/// refuses startup in any auth-requiring Mode, and `SkipPreflight`
/// cannot bypass it.
let resolveAuthProvider (authProvider: IAuthProvider option) : IAuthProvider =
    authProvider |> Option.defaultValue (HeaderAuthProvider.HeaderAuthProvider())

/// Phase 4b — Platform Admin store. Registered unconditionally so
/// `ScopeResolutionMiddleware` can resolve `AccessContext.PlatformRole`
/// per request via `IsPlatformAdmin`, and admin-gated handlers
/// (`PlatformAdminApiHandler`, the encryption-destroy endpoint) can
/// resolve the store directly. Default impl persists a flat user-id list
/// to `_platform/platform-admins.json`; emits audit on every mutation
/// via the already-resolved `auditLog` instance.
///
/// One-shot bootstrap from `TOOLUP_INITIAL_PLATFORM_ADMIN`. No-op when
/// the env var is unset / empty OR when the admin list is already
/// populated. Synchronous wait keeps the first request from racing the
/// bootstrap; a failure the helper reports (a refused dev fallback) is
/// logged at Error by the helper and does not abort startup. A storage
/// fault that THROWS refuses the boot at preflight with `BootstrapRefused`
/// (Phase 1051) — it used to be logged and the boot continued with no
/// admin.
///
/// Returns the constructed store so subsequent registrations
/// (`PlatformRuntimeConfig`, dev-diagnostics contributor) can capture
/// the same instance DI hands out.
let registerPlatformAdminStore
    (services: IServiceCollection)
    (config: ServerConfig)
    (resolvedBlobStorage: IBlobStorage)
    (auditLog: IAuditLog)
    (resolvedLogger: ILogger)
    : IPlatformAdminStore =

    let platformAdminStore: IPlatformAdminStore =
        PlatformAdminStore.BlobBackedPlatformAdminStore(resolvedBlobStorage, auditLog) :> _

    services.AddSingleton<IPlatformAdminStore>(platformAdminStore) |> ignore

    try
        PlatformAdminStore.bootstrap
            resolvedLogger
            config.AutoBootstrapDevAdmin
            (DeploymentConfig.requiresAnyAuth config)
            platformAdminStore
        |> Async.RunSynchronously
    with ex ->
        // Phase 1051 — refuse the boot (at preflight): a deployment whose
        // admin store cannot be read at boot would otherwise start with no
        // platform admin and no refusal.
        refuseAtPreflight
            services
            resolvedLogger
            "PlatformAdmin"
            "The platform-admin store (_platform/platform-admins.json in the configured blob storage) could not be read or written; restore access to the storage, then restart."
            ex

    platformAdminStore

/// Phase 4b deferred follow-up — runtime PlatformKnowledgeBase toggle.
/// The store loads any persisted override from blob at startup so the
/// in-memory cell starts in the right state; if no override exists,
/// `ServerConfig.PlatformKnowledgeBase` is the boot-time default.
/// Subsequent admin `Set` calls update both the blob and the cell
/// atomically. `RetrievalPipeline.authorisedScopes` reads via
/// `Snapshot()` per request — no I/O on the hot path.
let registerPlatformRuntimeConfig
    (services: IServiceCollection)
    (config: ServerConfig)
    (resolvedBlobStorage: IBlobStorage)
    (resolvedLogger: ILogger)
    : IPlatformRuntimeConfigStore =

    // Phase 1051 — a load that throws refuses the boot at preflight. The
    // stub below only carries the composition to that refusal: on its own
    // it used to START the deployment, its reads returning the static
    // config and its `Set` returning `Error`, so a persisted override was
    // silently ignored.
    let platformRuntimeConfig: IPlatformRuntimeConfigStore =
        try
            PlatformRuntimeConfigStore.create resolvedBlobStorage config.PlatformKnowledgeBase
            |> Async.RunSynchronously
        with ex ->
            refuseAtPreflight
                services
                resolvedLogger
                "PlatformRuntimeConfig"
                "The platform runtime-config store (its persisted override in the configured blob storage) could not be read; restore access to the storage, then restart."
                ex

            { new IPlatformRuntimeConfigStore with
                member _.Snapshot() = config.PlatformKnowledgeBase
                member _.GetPlatformKnowledgeBase() = async { return config.PlatformKnowledgeBase }
                member _.SetPlatformKnowledgeBase _ = async { return Error "runtime config store unavailable" }
            }

    services.AddSingleton<IPlatformRuntimeConfigStore>(platformRuntimeConfig)
    |> ignore

    platformRuntimeConfig

/// Phase 4b dev-convenience contributor — surfaces current admin list +
/// runtime PlatformKnowledgeBase state in `/dev/inspect` so operators
/// don't have to inspect blob files by hand to debug bootstrap /
/// role-resolution issues.
let registerPlatformAdminDevDiagnostics
    (services: IServiceCollection)
    (platformAdminStore: IPlatformAdminStore)
    (platformRuntimeConfig: IPlatformRuntimeConfigStore)
    : unit =
    services.AddSingleton<IDevDiagnosticsContributor>(
        PlatformAdminDevDiagnostics.PlatformAdminContributor(platformAdminStore, platformRuntimeConfig)
        :> IDevDiagnosticsContributor
    )
    |> ignore