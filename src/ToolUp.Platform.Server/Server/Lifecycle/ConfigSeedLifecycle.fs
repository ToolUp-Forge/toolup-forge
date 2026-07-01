module ToolUp.Platform.ConfigSeedLifecycle

open System
open System.Text.Json
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
// **Default-source note.** The base `OnProvisioned` surface carries only
// `scopeId` + `actorUserId`, so "deployment defaults" on that path are the
// SDK-shipped schema `DefaultJson` values. Phase 305 threads the
// deploy-plane `ProvisioningRequest` through the optional
// `ITenantLifecycleProvisionContext` surface: `OnProvisionedWith` overlays
// per-request values onto those defaults before the write. The one mapping
// today is the app-name — on a *team* scope a fresh `_platform` doc's
// `appName` branding field defaults to the request's `DisplayName`, so the
// team is labelled by its display name from the first request rather than
// falling back to the deployment-wide default. Absent a request (the base
// path), a blank `DisplayName`, or a non-team scope, the seed is
// byte-identical to the schema-defaults write (GP 11).
//
// **Scope/symmetry note.** The seeded documents live at
// `config/{scopeId}/{moduleKey}.json` (the config store keys its blob
// path on `StorageScope.Container`, which the lifecycle `scopeId` —
// `team-{id}` / `user-{id}` — already is). That matches the path the
// Phase 9h `ConfigStoreErasureHandler` clears on offboard, so a scope's
// seeded config sits under the same prefix the offboard erasure sweeps.
//
// **Phase 306 — offboard teardown (provision/offboard symmetry).**
// `OnDeprovisioned` is now the symmetric counterpart of the seed: it clears
// exactly the module keys `runSeed` seeds (the `seededModuleKeys`
// manifest), so what provisioning creates, offboarding removes. It clears
// ONLY the seeded keys — a config document another subsystem or the admin
// UI authored under a different module key is never touched (the manifest
// is the "config-owner tag"). This teardown lives in the offboard hook, NOT
// in the subject-keyed `ConfigStoreErasureHandler`, deliberately: a
// per-subject DSR must NOT delete the scope's shared default-valued
// `_platform` docs (the scope lives on, and those docs name no subject),
// whereas a full offboard must remove them. `IConfigStore.Clear` is
// idempotent, so a re-run — the Phase 54b resumable re-dispatch, or a
// double offboard — is a clean no-op.

/// A fully-resolved seed target: the module key, the schema to validate
/// the write against (with any override-only field schemas spliced in),
/// and the concrete values to write (schema defaults overlaid with the
/// per-request overrides). Building the values up-front keeps `seedModule`
/// a pure "write if absent" step.
type private SeedTarget = {
    ModuleKey: string
    Schema: ModuleConfigSchema
    Values: Map<string, string>
}

/// The reserved `_platform` module keys this hook seeds, paired with the
/// SDK-shipped schema each is validated + defaulted against.
let private baseSeedTargets: (string * ModuleConfigSchema) list = [
    ConfigKeys.PlatformModuleKey, PlatformSchema.sdkDefaultPlatformSchema.Schema
    ConfigKeys.NotificationPrefsModuleKey, PlatformSchema.sdkNotificationPrefsSchema.Schema
]

/// The reserved `_platform` module keys this hook seeds — the provisioning
/// manifest the Phase 306 offboard teardown clears. Deriving it from
/// `baseSeedTargets` keeps seed + teardown from drifting: a key added to
/// the seed is automatically torn down. Only these keys are cleared on
/// offboard, so a document under any other module key is never removed.
let private seededModuleKeys: string list = baseSeedTargets |> List.map fst

/// The SDK branding `appName` field — used both to clamp the seeded
/// display name to the field's declared max length and to splice the field
/// into the `_platform` seed schema when it is being overridden (it lives
/// in `sdkBrandingFields`, not the default `_platform` schema, so a naked
/// `SetRaw` of an `appName` value would be rejected as an unknown field).
let private appNameField =
    PlatformSchema.sdkBrandingFields
    |> List.find (fun f -> f.Key = ConfigKeys.BrandingKeys.AppName)

/// Clamp a candidate string value to a field's declared max length so a
/// long `DisplayName` can't trip the config store's per-field length
/// validation and fail the whole seed. Non-string kinds pass through.
let private clampToKind (kind: ConfigFieldKind) (value: string) : string =
    match kind with
    | ConfigFieldKind.String(Some maxLen) when value.Length > maxLen -> value.Substring(0, maxLen)
    | _ -> value

/// Per-deployment `_platform` seed overrides derived from the provisioning
/// request. Currently the app-name only: on a *team* scope, `appName`
/// defaults to the request's `DisplayName` (clamped to the field's length).
/// Empty on the base path (no request / blank display name / non-team
/// scope), so the seed stays byte-identical to the schema-defaults write.
let private platformOverrides (context: TenantProvisioningContext) : Map<string, string> =
    let isTeamScope = context.ScopeId.StartsWith("team-", StringComparison.Ordinal)

    match context.Request with
    | Some req when isTeamScope && not (String.IsNullOrWhiteSpace req.DisplayName) ->
        let appName = clampToKind appNameField.Kind req.DisplayName
        Map.ofList [ ConfigKeys.BrandingKeys.AppName, JsonSerializer.Serialize appName ]
    | _ -> Map.empty

/// Resolve the concrete seed targets for a provisioning run. Each base
/// target's schema defaults are overlaid with its per-request overrides;
/// an override key that isn't already a schema field has its field schema
/// spliced in (from `sdkBrandingFields`) so `SetRaw`'s unknown-key
/// validation accepts the write.
let private resolveTargets (context: TenantProvisioningContext) : SeedTarget list =
    baseSeedTargets
    |> List.map (fun (moduleKey, schema) ->
        let overrides =
            if moduleKey = ConfigKeys.PlatformModuleKey then
                platformOverrides context
            else
                Map.empty

        let knownKeys = schema.Fields |> List.map _.Key |> Set.ofList

        let overrideOnlyFields =
            overrides
            |> Map.toList
            |> List.map fst
            |> List.filter (fun k -> not (Set.contains k knownKeys))
            |> List.choose (fun k -> PlatformSchema.sdkBrandingFields |> List.tryFind (fun f -> f.Key = k))

        let schema = {
            schema with
                Fields = schema.Fields @ overrideOnlyFields
        }

        let values =
            schema.Fields
            |> List.map (fun f -> f.Key, f.DefaultJson)
            |> Map.ofList
            |> fun defaults -> Map.fold (fun acc k v -> Map.add k v acc) defaults overrides

        {
            ModuleKey = moduleKey
            Schema = schema
            Values = values
        })

/// Seed one module key's document with its resolved values, but only if no
/// document exists yet. Returns `Ok true` when a fresh document was
/// written, `Ok false` when one already existed (idempotent skip), and
/// `Error` when the write failed.
let private seedModule
    (configStore: IConfigStore)
    (scope: StorageScope)
    (target: SeedTarget)
    : Async<Result<bool, string>> =
    async {
        let! existing = configStore.GetRaw(scope, target.ModuleKey)

        if not (Map.isEmpty existing) then
            return Ok false
        else
            match! configStore.SetRaw(scope, target.ModuleKey, target.Values, target.Schema) with
            | Ok() -> return Ok true
            | Error e -> return Error e
    }

/// Run the seed for a provisioning context — shared by the base
/// `OnProvisioned` (`Request = None`, schema defaults only) and the
/// Phase 305 `OnProvisionedWith` (per-request overrides applied).
let private runSeed (services: IServiceProvider) (context: TenantProvisioningContext) : Async<LifecycleHookResult> = async {
    match services.GetService(typeof<IConfigStore>) with
    | :? IConfigStore as configStore ->
        let scope: StorageScope = {
            ScopeId = context.ScopeId
            Container = context.ScopeId
            Persist = true
        }

        let errors = ResizeArray<string>()

        for target in resolveTargets context do
            match! seedModule configStore scope target with
            | Ok _ -> ()
            | Error e -> errors.Add(sprintf "%s: %s" target.ModuleKey e)

        if errors.Count > 0 then
            return LifecycleHookResult.Failed(String.Join("; ", errors))
        else
            return LifecycleHookResult.Completed
    | _ -> return LifecycleHookResult.Skipped "no IConfigStore registered (config substrate not composed)"
}

/// Phase 306 — tear down the provisioning-seeded `_platform` config
/// documents for `scopeId` on offboard: `Clear` each seeded module key.
/// The symmetric counterpart of `runSeed`, clearing exactly the keys it
/// seeds (never a document another subsystem owns). `IConfigStore.Clear` is
/// idempotent (a no-op when the document is already gone), so a re-run over
/// an already-torn-down scope clears nothing and still reports Completed.
let private runTeardown (services: IServiceProvider) (scopeId: string) : Async<LifecycleHookResult> = async {
    match services.GetService(typeof<IConfigStore>) with
    | :? IConfigStore as configStore ->
        let scope: StorageScope = {
            ScopeId = scopeId
            Container = scopeId
            Persist = true
        }

        for moduleKey in seededModuleKeys do
            do! configStore.Clear(scope, moduleKey)

        return LifecycleHookResult.Completed
    | _ -> return LifecycleHookResult.Skipped "no IConfigStore registered (config substrate not composed)"
}

type ConfigSeedLifecycle(services: IServiceProvider) =
    interface ITenantLifecycle with
        member _.Name = "config-seed"

        member _.OnProvisioned(scopeId, actorUserId) =
            runSeed services {
                ScopeId = scopeId
                ActorUserId = actorUserId
                Request = None
            }

        // Phase 306 — offboard teardown: remove the seeded `_platform` docs
        // this hook created at provisioning (symmetry with the seed).
        member _.OnDeprovisioned(scopeId, _actorUserId) = runTeardown services scopeId

    // Phase 305 — request-aware provisioning: overlay per-deployment values
    // (the team app-name from the request's DisplayName) onto the schema
    // defaults. Falls back byte-identically to OnProvisioned when no request
    // rides or it carries no per-deployment override.
    interface ITenantLifecycleProvisionContext with
        member _.OnProvisionedWith(context) = runSeed services context

/// Construct the first-party config-seed lifecycle hook. Resolves the
/// active `IConfigStore` from `services` on every call.
let create (services: IServiceProvider) : ITenantLifecycle =
    ConfigSeedLifecycle(services) :> ITenantLifecycle