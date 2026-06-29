module ToolUp.Platform.EncryptionKeyLifecycle

open System
open ToolUp.Platform
open ToolUp.Platform.EncryptionTypes
open ToolUp.Platform.BlobEncryption

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

type EncryptionKeyLifecycle(services: IServiceProvider) =
    interface ITenantLifecycle with
        member _.Name = "encryption-key"

        member _.OnProvisioned(_scopeId, _actorUserId) = async {
            return
                LifecycleHookResult.Skipped "no provisioning action — per-scope keys are minted lazily on first encrypt"
        }

        member _.OnDeprovisioned(scopeId, actorUserId) = async {
            match services.GetService(typeof<IBlobEncryptionKeyResolver>) with
            | :? PerScopeKeyResolver.PerScopeKeyResolver as perScope ->
                let! result = perScope.DestroyKey(scopeId, actorUserId)

                match result with
                | Ok() -> return LifecycleHookResult.Completed
                | Error err -> return LifecycleHookResult.Failed(KeyResolutionError.message err)
            | null ->
                return
                    LifecycleHookResult.Skipped
                        "no IBlobEncryptionKeyResolver registered (encryption-at-rest not enabled)"
            | _ ->
                return
                    LifecycleHookResult.Skipped
                        "encryption resolver is not PerScopeKeyResolver — crypto-shred unavailable (a platform-wide / KMS key must not be destroyed on a single-tenant offboard)"
        }

    // Phase 54c — mutation-free preview: report whether the scope's key
    // WOULD be crypto-shredded, without calling DestroyKey. Count is 1
    // (the per-scope key) under an active PerScopeKeyResolver, else 0.
    interface ITenantLifecyclePreview with
        member _.OnDeprovisionPreview(scopeId, _actorUserId) = async {
            match services.GetService(typeof<IBlobEncryptionKeyResolver>) with
            | :? PerScopeKeyResolver.PerScopeKeyResolver ->
                return
                    LifecyclePreviewItem.affecting
                        "encryption-key"
                        1
                        (sprintf "the scope's encryption key would be crypto-shredded (scope %s)" scopeId)
            | null ->
                return
                    LifecyclePreviewItem.affecting
                        "encryption-key"
                        0
                        "encryption-at-rest not enabled — no key to destroy"
            | _ ->
                return
                    LifecyclePreviewItem.affecting
                        "encryption-key"
                        0
                        "resolver is not PerScopeKeyResolver — no per-scope key would be destroyed"
        }

/// Construct the first-party encryption-key lifecycle hook. The hook
/// resolves the active `IBlobEncryptionKeyResolver` from `services` on
/// every call, so it picks up whatever resolver the deployment composed.
let create (services: IServiceProvider) : ITenantLifecycle =
    EncryptionKeyLifecycle(services) :> ITenantLifecycle