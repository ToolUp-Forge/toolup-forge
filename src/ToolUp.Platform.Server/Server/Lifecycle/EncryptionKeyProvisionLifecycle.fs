module ToolUp.Platform.EncryptionKeyProvisionLifecycle

open System
open ToolUp.Platform
open ToolUp.Platform.EncryptionTypes
open ToolUp.Platform.BlobEncryption

// ─── Phase 54g — first-party encryption-key provision lifecycle hook ─
//
// On `OnProvisioned`, pre-creates the scope's per-scope encryption key so
// the tenant's first blob write isn't a cold-start key mint. The mirror
// of `EncryptionKeyLifecycle`'s `OnDeprovisioned` crypto-shred: provision
// mints the key, offboard destroys it (the genuinely symmetric pair).
// Resolves the active `IBlobEncryptionKeyResolver` from DI per call
// (stateless between invocations, GP 12 rule 4):
//   * `PerScopeKeyResolver` — resolve (and thereby auto-create + persist)
//     the scope's key. `ResolveKey` loads an existing key without
//     re-minting, so re-provisioning is idempotent — no duplicate key.
//   * any other resolver (`SingleKeyResolver`, KMS, custom) — `Skipped`,
//     mirroring the offboard hook: a platform-wide / externally-managed
//     key is not minted per scope.
//   * no resolver registered — `Skipped` (encryption-at-rest not enabled).
//
// `OnDeprovisioned` is a no-op `Skipped`: destroying the key on offboard
// is `EncryptionKeyLifecycle`'s job, not this provision-only hook's.

/// The first-party encryption-key provision hook, as a value. A
/// platform-wide / KMS key is not minted per scope, so any resolver but the
/// per-scope one counts as absent here.
let hook: TenantHook<PerScopeKeyResolver.PerScopeKeyResolver> = {
    Name = "encryption-key-provision"
    Requires = "a PerScopeKeyResolver (per-scope encryption at rest)"
    Resolve =
        fun services ->
            match services.GetService(typeof<IBlobEncryptionKeyResolver>) with
            | :? PerScopeKeyResolver.PerScopeKeyResolver as perScope -> Some perScope
            | _ -> None
    OnProvisioned =
        Some(fun perScope context -> async {
            let scopeId = context.ScopeId

            // The resolver keys on `StorageScope.ScopeId`; the offboard
            // `DestroyKey` keys on the raw lifecycle `scopeId`, so mint
            // under the same id for a symmetric provision/offboard pair.
            let scope: StorageScope = {
                ScopeId = scopeId
                Container = scopeId
                Persist = true
            }

            try
                // ResolveKey auto-creates + persists on first call and
                // loads the existing key on subsequent calls (idempotent).
                let! _key = (perScope :> IBlobEncryptionKeyResolver).ResolveKey scope
                return LifecycleHookResult.Completed
            with ex ->
                return
                    LifecycleHookResult.Failed(
                        sprintf "failed to pre-create per-scope key for %s: %s" scopeId ex.Message
                    )
        })
    // The per-scope key is crypto-shredded by the encryption-key offboard
    // hook, not this provision hook.
    OnDeprovisioned = None
    Preview = None
}

/// Construct the first-party encryption-key provision lifecycle hook. The
/// hook resolves the active `IBlobEncryptionKeyResolver` from `services`
/// on every call, so it picks up whatever resolver the deployment composed.
let create (services: IServiceProvider) : ITenantLifecycle = TenantHook.asLifecycle hook services