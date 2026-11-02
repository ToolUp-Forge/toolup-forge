module ToolUp.Platform.Compose.EncryptionKeyLifecycle

open System
open ToolUp.Platform
open ToolUp.Platform.EncryptionTypes
open ToolUp.Platform.Infra.BlobEncryption
open ToolUp.Platform.Config

// ─── Phase 54 — first-party encryption-key lifecycle hook ────────────
//
// Wraps the Phase 22 `PerScopeKeyResolver.DestroyKey` crypto-shred on
// `OnDeprovisioned`. Resolves the active `IBlobEncryptionKeyResolver`
// from DI per call (stateless between invocations, GP 12 rule 4):
//   * `PerScopeKeyResolver` — destroy the scope's key. Every blob the
//     scope ever wrote becomes undecryptable, which is the strongest
//     offboard guarantee the SDK offers (the ciphertext can remain at
//     rest indefinitely yet is cryptographically inert).
//   * any other resolver (`SingleKeyResolver`, KMS, custom) — `Skipped`,
//     because destroying a platform-wide / externally-managed key on a
//     single-tenant offboard would shred every other tenant's data.
//   * no resolver registered — `Skipped` (encryption not enabled).
//
// `OnProvisioned` is a no-op `Skipped`: per-scope keys are minted lazily
// on the scope's first encrypt, so provisioning has nothing to do.

/// The per-scope key resolver, when the deployment composed one: a
/// platform-wide / KMS key must never be destroyed on a single-tenant
/// offboard, so any other resolver counts as absent here.
let private perScopeResolver (services: IServiceProvider) =
    match services.GetService(typeof<IBlobEncryptionKeyResolver>) with
    | :? PerScopeKeyResolver.PerScopeKeyResolver as perScope -> Some perScope
    | _ -> None

/// The first-party encryption-key offboard hook, as a value.
let hook: TenantHook<PerScopeKeyResolver.PerScopeKeyResolver> = {
    Name = "encryption-key"
    Requires = "a PerScopeKeyResolver (per-scope encryption at rest)"
    Resolve = perScopeResolver
    OnProvisioned = None
    OnDeprovisioned =
        Some(fun perScope scopeId actorUserId -> async {
            match! perScope.DestroyKey(scopeId, actorUserId) with
            | Ok() -> return LifecycleHookResult.Completed
            | Error err -> return LifecycleHookResult.Failed(KeyResolutionError.message err)
        })
    // Mutation-free preview: whether the scope's key WOULD be
    // crypto-shredded, without calling DestroyKey.
    Preview =
        Some(fun _ scopeId _ -> async {
            return 1, sprintf "the scope's encryption key would be crypto-shredded (scope %s)" scopeId
        })
}

/// Construct the first-party encryption-key lifecycle hook. The hook
/// resolves the active `IBlobEncryptionKeyResolver` from `services` on
/// every call, so it picks up whatever resolver the deployment composed.
let create (services: IServiceProvider) : ITenantLifecycle = TenantHook.asLifecycle hook services