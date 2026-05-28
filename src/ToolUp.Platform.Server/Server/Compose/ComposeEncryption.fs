module ToolUp.Platform.ComposeEncryption

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption

// ─── compose phase: encryption decorator + DI ────────────────────────
//
// Phase 22 — envelope encryption substrate. Wraps the configured
// `IBlobStorage` with `EncryptedBlobStorage` when a resolver is
// registered; every downstream consumer (DataObjectStore, EventStore,
// JobStore, WebhookRegistry, TeamStore, PermissionStore,
// NotificationAddressBook, etc.) receives the wrapped instance so
// encryption is transparent. `None` (the default) leaves the storage
// layer un-decorated.
//
// Extracted from `compose` for the per-concern subdivision (Phase 15e
// follow-up). Takes the exact substrate values the inline definition
// captured and returns the same shape. Zero behaviour change.

/// Apply the `EncryptedBlobStorage` decorator when an encryption-key
/// resolver was supplied. Returns the (possibly wrapped) blob storage
/// that every downstream consumer (DataObjectStore, EventStore, ...)
/// receives via DI.
let applyEncryptionDecorator
    (innerBlobStorage: IBlobStorage)
    (encryptionKeyResolver: IBlobEncryptionKeyResolver option)
    : IBlobStorage =
    match encryptionKeyResolver with
    | Some resolver -> EncryptedBlobStorage.EncryptedBlobStorage(innerBlobStorage, resolver) :> IBlobStorage
    | None -> innerBlobStorage

/// Register the encryption resolver as a DI singleton when the
/// deployment opted into envelope encryption. `EncryptionAdminHandler`
/// resolves it per-request to dispatch the destroy-scope-key endpoint
/// on `PerScopeKeyResolver`. Apps that didn't call
/// `ServerApp.withEncryptedBlobStorage` skip this registration entirely.
let registerEncryptionResolver
    (services: IServiceCollection)
    (encryptionKeyResolver: IBlobEncryptionKeyResolver option)
    : unit =
    match encryptionKeyResolver with
    | Some r -> services.AddSingleton<IBlobEncryptionKeyResolver>(r) |> ignore
    | None -> ()

/// Gap audit #1 — wire `PerScopeKeyResolver` to the cross-process
/// notification channel for multi-instance cache coherence. After
/// wiring, `DestroyKey` on any silo publishes a
/// `KeyDestroyedNotificationKey` `CustomNotification` on the platform-
/// reserved scope; other silos' subscribed handlers evict their local
/// caches synchronously. Distributed channels (Redis pub/sub) bridge the
/// publish across silos; the in-process default doesn't
/// (`PerScopeKeyResolverDistributedValidator` warns about the
/// combination at preflight). Best-effort: `WireToChannel` runs
/// synchronously here, but a network glitch on subscribe shouldn't
/// crash compose — the resolver still functions single-instance.
let wirePerScopeResolverToNotificationChannel
    (encryptionKeyResolver: IBlobEncryptionKeyResolver option)
    (resolvedNotificationChannel: INotificationChannel)
    : unit =
    match encryptionKeyResolver with
    | Some(:? PerScopeKeyResolver.PerScopeKeyResolver as perScope) ->
        try
            perScope.WireToChannel resolvedNotificationChannel |> Async.RunSynchronously
        with _ ->
            ()
    | _ -> ()