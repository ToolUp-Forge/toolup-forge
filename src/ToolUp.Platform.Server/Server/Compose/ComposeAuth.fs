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
/// bootstrap; bootstrap failures are logged at Error by the helper and
/// do not abort startup. The try/with guards against an `IBlobStorage`
/// glitch — admin assignment can be retried via the API once the
/// deployment is up.
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
        PlatformAdminStore.bootstrap resolvedLogger config.AutoBootstrapDevAdmin platformAdminStore
        |> Async.RunSynchronously
    with ex ->
        resolvedLogger.Error(sprintf "[PlatformAdmin] Bootstrap aborted: %s" ex.Message, Some ex)

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

    let platformRuntimeConfig: IPlatformRuntimeConfigStore =
        try
            PlatformRuntimeConfigStore.create resolvedBlobStorage config.PlatformKnowledgeBase
            |> Async.RunSynchronously
        with ex ->
            resolvedLogger.Error(
                sprintf "[PlatformRuntimeConfig] Init failed, falling back to ServerConfig default: %s" ex.Message,
                Some ex
            )

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