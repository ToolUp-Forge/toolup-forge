module ToolUp.Platform.ConfigSeedLifecycle

open System
open ToolUp.Platform

// ─── Phase 54g — first-party config-seed lifecycle hook ──────────────
//
// On `OnProvisioned`, seeds the new scope's reserved-`_platform` config
// documents with the SDK-shipped schema defaults so a fresh tenant has a
// concrete settings document from the first request rather than relying
// on the lazy schema-default overlay `IConfigStore.GetEffective` applies.
// Seeds two module keys (the deployment-default surface the admin UI
// edits):
//   * `_platform`                  — the platform-defaults schema
//                                    (`currencySymbol`, …).
//   * `_platform.notification_prefs` — the safe-quiet notification kill
//                                    switches (every `*.enabled` defaults
//                                    to `false`).
// Resolves the active `IConfigStore` from DI per call (stateless between
// invocations, GP 12 rule 4):
//   * `IConfigStore` present — seed each module key whose document does
//     not yet exist; an already-seeded key is a no-op (idempotent —
//     re-provisioning produces no duplicate or overwritten config).
//   * no store registered — `Skipped` (config substrate not composed).
//
// **Default-source note.** The `OnProvisioned` hook surface carries only
// `scopeId` + `actorUserId` (not the deploy-plane `ProvisioningRequest`),
// so "deployment defaults" here are the SDK-shipped schema `DefaultJson`
// values, not per-request overrides. Threading a `ProvisioningRequest`
// through the hook surface is a follow-on (it would change the
// `ITenantLifecycle` contract, out of this phase's scope).
//
// **Scope/symmetry note.** The seeded documents live at
// `config/{scopeId}/{moduleKey}.json` (the config store keys its blob
// path on `StorageScope.Container`, which the lifecycle `scopeId` —
// `team-{id}` / `user-{id}` — already is). That matches the path the
// Phase 9h `ConfigStoreErasureHandler` clears on offboard, so a scope's
// seeded config sits under the same prefix the offboard erasure sweeps.
//
// `OnDeprovisioned` is a no-op `Skipped`: tearing config down on offboard
// is the existing erasure path's job (`DataSubjectRequestLifecycle` →
// `ConfigStoreErasureHandler`), not this provision-only hook's.

/// The reserved `_platform` module keys this hook seeds, paired with the
/// SDK-shipped schema each is validated + defaulted against.
let private seedTargets: (string * ModuleConfigSchema) list = [
    ConfigKeys.PlatformModuleKey, PlatformSchema.sdkDefaultPlatformSchema.Schema
    ConfigKeys.NotificationPrefsModuleKey, PlatformSchema.sdkNotificationPrefsSchema.Schema
]

/// Seed one module key's document with its schema defaults, but only if
/// no document exists yet. Returns `Ok true` when a fresh document was
/// written, `Ok false` when one already existed (idempotent skip), and
/// `Error` when the write failed.
let private seedModule
    (configStore: IConfigStore)
    (scope: StorageScope)
    (moduleKey: string)
    (schema: ModuleConfigSchema)
    : Async<Result<bool, string>> =
    async {
        let! existing = configStore.GetRaw(scope, moduleKey)

        if not (Map.isEmpty existing) then
            return Ok false
        else
            let defaults =
                schema.Fields |> List.map (fun f -> f.Key, f.DefaultJson) |> Map.ofList

            match! configStore.SetRaw(scope, moduleKey, defaults, schema) with
            | Ok() -> return Ok true
            | Error e -> return Error e
    }

type ConfigSeedLifecycle(services: IServiceProvider) =
    interface ITenantLifecycle with
        member _.Name = "config-seed"

        member _.OnProvisioned(scopeId, _actorUserId) = async {
            match services.GetService(typeof<IConfigStore>) with
            | :? IConfigStore as configStore ->
                let scope: StorageScope = {
                    ScopeId = scopeId
                    Container = scopeId
                    Persist = true
                }

                let errors = ResizeArray<string>()

                for moduleKey, schema in seedTargets do
                    match! seedModule configStore scope moduleKey schema with
                    | Ok _ -> ()
                    | Error e -> errors.Add(sprintf "%s: %s" moduleKey e)

                if errors.Count > 0 then
                    return LifecycleHookResult.Failed(String.Join("; ", errors))
                else
                    return LifecycleHookResult.Completed
            | _ -> return LifecycleHookResult.Skipped "no IConfigStore registered (config substrate not composed)"
        }

        member _.OnDeprovisioned(_scopeId, _actorUserId) = async {
            return
                LifecycleHookResult.Skipped
                    "no offboard action — seeded config is torn down by the erasure path, not this provision hook"
        }

/// Construct the first-party config-seed lifecycle hook. Resolves the
/// active `IConfigStore` from `services` on every call.
let create (services: IServiceProvider) : ITenantLifecycle =
    ConfigSeedLifecycle(services) :> ITenantLifecycle