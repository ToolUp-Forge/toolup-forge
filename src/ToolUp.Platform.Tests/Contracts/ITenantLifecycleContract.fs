module ToolUp.Platform.Tests.Contracts.ITenantLifecycleContract

open System
open System.IO
open Expecto
open Microsoft.Extensions.Caching.Memory
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.EncryptionTypes
open ToolUp.Platform.Secrets
open ToolUp.Platform.TeamManagement

// ─── Phase 54 — ITenantLifecycle contract pack ───────────────────────
//
// The conformance bar for the four first-party hooks: each must
// `Skipped` (never `Failed`, never throw) when its substrate is absent
// from the resolving `IServiceProvider`. This is the graceful-degrade
// guarantee that lets an offboard on a minimal deployment run clean —
// `EncryptionKeyLifecycle` under no resolver, `JobSchedulerLifecycle`
// under no scheduler, etc. all report `Skipped`, so a `DeprovisionTenant`
// on such a deployment returns a summary of four `Skipped` outcomes
// rather than four failures.

/// `IServiceProvider` that resolves nothing — models a minimal
/// deployment where none of the hooks' substrates are registered.
let private emptyProvider: IServiceProvider =
    { new IServiceProvider with
        member _.GetService(_serviceType) = null
    }

let private isSkipped (result: LifecycleHookResult) =
    match result with
    | LifecycleHookResult.Skipped _ -> true
    | _ -> false

let private isCompleted (result: LifecycleHookResult) =
    match result with
    | LifecycleHookResult.Completed -> true
    | _ -> false

// ─── Phase 54g — provisioning-hook test doubles ──────────────────────
//
// The three Phase 54g provision hooks do real work when their substrate
// IS present, so the conformance bar extends beyond "Skipped when absent":
// each must seed/bootstrap/mint on `OnProvisioned`, be idempotent on
// re-provision, and (for the key hook) be the symmetric partner of the
// offboard crypto-shred.

/// `IServiceProvider` resolving a fixed `(type → instance)` table; any
/// type not in the table resolves to `null` — the MS-DI "service absent"
/// contract the hooks degrade against.
let private providerOf (services: (Type * obj) list) : IServiceProvider =
    let table = dict services

    { new IServiceProvider with
        member _.GetService(t) =
            match table.TryGetValue t with
            | true, v -> v
            | false, _ -> null
    }

/// The `StorageScope` the provision hooks build internally (container ==
/// the lifecycle scopeId), reproduced here so assertions read/write the
/// same blob path the hook seeded.
let private lifecycleScope (scopeId: string) : StorageScope = {
    ScopeId = scopeId
    Container = scopeId
    Persist = true
}

let private newTempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-54g-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private newConfigStore () : IConfigStore =
    ConfigStore.create (InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage)

let private newTeamStore () : ITeamStore =
    TeamManagement.TeamStore(
        InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage,
        NotificationChannel.NoOpNotificationChannel()
    )
    :> ITeamStore

let private newKeyResolver () : PerScopeKeyResolver.PerScopeKeyResolver =
    PerScopeKeyResolver.create
        (FileSecretStore.FileSecretStore(baseDir = newTempDir ()) :> ISecretStore)
        (new MemoryCache(MemoryCacheOptions()) :> IMemoryCache)
        None

/// Minimal `_platform` schema for the config-seed idempotency probe — a
/// single `currencySymbol` field, so the test can pre-write a custom
/// value and assert the hook does not overwrite it on re-provision.
/// (The SDK-shipped schema the hook actually seeds against is internal to
/// the server assembly; this stand-in only needs to validate the probe
/// value.)
let private platformProbeSchema: ModuleConfigSchema = {
    Fields = [
        {
            Key = "currencySymbol"
            DisplayName = "Currency symbol"
            Description = None
            Kind = ConfigFieldKind.String(Some 4)
            Required = false
            DefaultJson = "\"£\""
        }
    ]
}

let tests =
    testList "ITenantLifecycle — first-party hook contract" [

        testCaseAsync "EncryptionKeyLifecycle skips when no IBlobEncryptionKeyResolver is registered"
        <| async {
            let hook = EncryptionKeyLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("team-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without an encryption resolver"
        }

        testCaseAsync "MembershipCacheLifecycle skips when no TeamScopeResolver is registered"
        <| async {
            let hook = MembershipCacheLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("team-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without a team scope resolver"
        }

        testCaseAsync "JobSchedulerLifecycle skips when no IJobScheduler is registered"
        <| async {
            let hook = JobSchedulerLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("team-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without a scheduler"
        }

        testCaseAsync "DataSubjectRequestLifecycle skips when no IErasureHandler is registered"
        <| async {
            let hook = DataSubjectRequestLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("user-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without erasure handlers"
        }

        // ─── Phase 54d — domain / companion offboard hooks ───────────

        testCaseAsync "ConversationStoreLifecycle skips when no IConversationStore is registered"
        <| async {
            let hook = ConversationStoreLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("team-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without a conversation store"
        }

        testCaseAsync "KnowledgeBaseLifecycle skips when no IBlobStorage is registered"
        <| async {
            let hook = ToolUp.KnowledgeBase.Server.KnowledgeBaseLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("team-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without a blob store (KB substrate uncomposed)"
        }

        testCaseAsync "RagVectorStoreLifecycle skips when no IVectorStore is registered"
        <| async {
            let hook = ToolUp.RAG.RagVectorStoreLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("team-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without a vector store"
        }

        testCaseAsync "every first-party hook is a no-op Skipped on OnProvisioned"
        <| async {
            let hooks = [
                EncryptionKeyLifecycle.create emptyProvider
                MembershipCacheLifecycle.create emptyProvider
                JobSchedulerLifecycle.create emptyProvider
                DataSubjectRequestLifecycle.create emptyProvider
                ConversationStoreLifecycle.create emptyProvider
                ToolUp.KnowledgeBase.Server.KnowledgeBaseLifecycle.create emptyProvider
                ToolUp.RAG.RagVectorStoreLifecycle.create emptyProvider
            ]

            for hook in hooks do
                let! result = hook.OnProvisioned("team-x", "admin")
                Expect.isTrue (isSkipped result) (sprintf "%s provisioning is a no-op skip" hook.Name)
        }

        testCaseAsync "first-party hook names are distinct (no aggregation collision)"
        <| async {
            let names =
                [
                    EncryptionKeyLifecycle.create emptyProvider
                    MembershipCacheLifecycle.create emptyProvider
                    JobSchedulerLifecycle.create emptyProvider
                    DataSubjectRequestLifecycle.create emptyProvider
                    ConversationStoreLifecycle.create emptyProvider
                    ToolUp.KnowledgeBase.Server.KnowledgeBaseLifecycle.create emptyProvider
                    ToolUp.RAG.RagVectorStoreLifecycle.create emptyProvider
                ]
                |> List.map (fun h -> h.Name)

            Expect.equal (List.distinct names |> List.length) names.Length "all hook names are unique"
        }

        // ─── Phase 54b — resumable offboard ledger contract ──────────
        //
        // ILifecycleLedger conformance (against the blob-backed default
        // over the in-memory blob double). Resumability + retry *through
        // the aggregator sweep* are exercised in
        // TenantLifecycleAggregatorTests (`runResumable`); these cases pin
        // the ledger seam those callbacks ride on.

        testCaseAsync "ledger records a hook then GetCompleted reads it back"
        <| async {
            let ledger =
                BlobBackedLifecycleLedger.create (
                    InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
                )

            let! before = ledger.GetCompleted("team-x", Deprovisioning)
            Expect.isTrue (Set.isEmpty before) "a fresh ledger is empty"

            do! ledger.Record("team-x", Deprovisioning, "encryption-key", LedgerDisposition.Completed)
            do! ledger.Record("team-x", Deprovisioning, "data-erasure", LedgerDisposition.Skipped)

            let! after = ledger.GetCompleted("team-x", Deprovisioning)
            Expect.equal after (Set.ofList [ "encryption-key"; "data-erasure" ]) "both dispositions recorded as done"
        }

        testCaseAsync "ledger Record is idempotent — recording the same hook twice is one entry"
        <| async {
            let ledger =
                BlobBackedLifecycleLedger.create (
                    InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
                )

            do! ledger.Record("team-x", Deprovisioning, "job-scheduler", LedgerDisposition.Completed)
            do! ledger.Record("team-x", Deprovisioning, "job-scheduler", LedgerDisposition.Completed)

            let! completed = ledger.GetCompleted("team-x", Deprovisioning)
            Expect.equal completed (Set.ofList [ "job-scheduler" ]) "duplicate record collapses to one entry"
        }

        testCaseAsync "ledger keys are isolated per (scope, phase)"
        <| async {
            let ledger =
                BlobBackedLifecycleLedger.create (
                    InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
                )

            do! ledger.Record("team-a", Deprovisioning, "h", LedgerDisposition.Completed)

            let! otherScope = ledger.GetCompleted("team-b", Deprovisioning)
            let! otherPhase = ledger.GetCompleted("team-a", Provisioning)
            Expect.isTrue (Set.isEmpty otherScope) "a different scope sees nothing"
            Expect.isTrue (Set.isEmpty otherPhase) "a different phase sees nothing"
        }

        testCaseAsync "ledger Clear resets the run so a re-offboard starts fresh"
        <| async {
            let ledger =
                BlobBackedLifecycleLedger.create (
                    InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
                )

            do! ledger.Record("team-x", Deprovisioning, "h1", LedgerDisposition.Completed)
            do! ledger.Record("team-x", Deprovisioning, "h2", LedgerDisposition.Completed)
            do! ledger.Clear("team-x", Deprovisioning)

            let! afterClear = ledger.GetCompleted("team-x", Deprovisioning)
            Expect.isTrue (Set.isEmpty afterClear) "Clear removes every recorded hook for the run"
        }

        // ─── Phase 54g — provisioning hooks that do real work ────────

        testCaseAsync "Phase 54g hooks skip on OnProvisioned when their substrate is absent"
        <| async {
            let hooks = [
                ConfigSeedLifecycle.create emptyProvider
                OwnerTeamBootstrapLifecycle.create emptyProvider
                EncryptionKeyProvisionLifecycle.create emptyProvider
            ]

            for hook in hooks do
                let! result = hook.OnProvisioned("team-x", "admin")
                Expect.isTrue (isSkipped result) (sprintf "%s skips without its substrate" hook.Name)
        }

        testCaseAsync "Phase 54g hooks are a no-op Skipped on OnDeprovisioned (provision-only)"
        <| async {
            let hooks = [
                ConfigSeedLifecycle.create emptyProvider
                OwnerTeamBootstrapLifecycle.create emptyProvider
                EncryptionKeyProvisionLifecycle.create emptyProvider
            ]

            for hook in hooks do
                let! result = hook.OnDeprovisioned("team-x", "admin")
                Expect.isTrue (isSkipped result) (sprintf "%s offboard is a no-op skip" hook.Name)
        }

        testCaseAsync "ConfigSeedLifecycle seeds _platform + notification_prefs defaults on provision"
        <| async {
            let store = newConfigStore ()
            let provider = providerOf [ typeof<IConfigStore>, box store ]
            let hook = ConfigSeedLifecycle.create provider

            let! result = hook.OnProvisioned("team-seed", "admin")
            Expect.isTrue (isCompleted result) "config seed completes when IConfigStore is present"

            let scope = lifecycleScope "team-seed"
            let! platformRaw = store.GetRaw(scope, ConfigKeys.PlatformModuleKey)
            Expect.isTrue (Map.containsKey "currencySymbol" platformRaw) "platform defaults (currencySymbol) seeded"

            let! prefsRaw = store.GetRaw(scope, ConfigKeys.NotificationPrefsModuleKey)

            Expect.isTrue
                (Map.containsKey ConfigKeys.NotificationPrefsKeys.EmailEnabled prefsRaw)
                "notification-prefs defaults seeded"
        }

        testCaseAsync "ConfigSeedLifecycle is idempotent — re-provision does not overwrite existing config"
        <| async {
            let store = newConfigStore ()
            let provider = providerOf [ typeof<IConfigStore>, box store ]
            let hook = ConfigSeedLifecycle.create provider
            let scope = lifecycleScope "team-idem"

            // Pre-seed a custom currency so a re-seed would be observable.
            let! pre =
                store.SetRaw(
                    scope,
                    ConfigKeys.PlatformModuleKey,
                    Map.ofList [ "currencySymbol", "\"$\"" ],
                    platformProbeSchema
                )

            Expect.isOk pre "pre-seed write succeeds"

            let! result = hook.OnProvisioned("team-idem", "admin")
            Expect.isTrue (isCompleted result) "re-provision completes"

            let! raw = store.GetRaw(scope, ConfigKeys.PlatformModuleKey)

            Expect.equal
                (Map.tryFind "currencySymbol" raw)
                (Some "\"$\"")
                "existing _platform config left intact (not re-seeded to the default)"
        }

        testCaseAsync "OwnerTeamBootstrapLifecycle creates the owner team + owner membership on provision"
        <| async {
            let store = newTeamStore ()
            let provider = providerOf [ typeof<ITeamStore>, box store ]
            let hook = OwnerTeamBootstrapLifecycle.create provider

            let! result = hook.OnProvisioned("team-acme", "owner-1")
            Expect.isTrue (isCompleted result) "owner-team bootstrap completes when ITeamStore is present"

            let! team = store.GetTeam "acme"
            Expect.isSome team "the owner team row was created"

            let! role = store.GetMemberRole("acme", "owner-1")
            Expect.equal role (Some Owner) "the provisioning actor is attached as Owner"
        }

        testCaseAsync "OwnerTeamBootstrapLifecycle is idempotent — re-provision adds no duplicate team / owner"
        <| async {
            let store = newTeamStore ()
            let provider = providerOf [ typeof<ITeamStore>, box store ]
            let hook = OwnerTeamBootstrapLifecycle.create provider

            let! first = hook.OnProvisioned("team-acme", "owner-1")
            Expect.isTrue (isCompleted first) "first provision completes"

            let! second = hook.OnProvisioned("team-acme", "owner-1")
            Expect.isTrue (isCompleted second) "re-provision completes (no 'already a member' error)"

            let! members = store.GetTeamMembers "acme"
            Expect.equal members.Length 1 "re-provision produced no duplicate membership"
        }

        testCaseAsync "OwnerTeamBootstrapLifecycle skips a non-team scope"
        <| async {
            let store = newTeamStore ()
            let provider = providerOf [ typeof<ITeamStore>, box store ]
            let hook = OwnerTeamBootstrapLifecycle.create provider

            let! result = hook.OnProvisioned("user-solo", "owner-1")
            Expect.isTrue (isSkipped result) "a user scope has no owner team to bootstrap"
        }

        testCaseAsync "EncryptionKeyProvisionLifecycle pre-creates the per-scope key on provision"
        <| async {
            let resolver = newKeyResolver ()
            let provider = providerOf [ typeof<IBlobEncryptionKeyResolver>, box resolver ]
            let hook = EncryptionKeyProvisionLifecycle.create provider

            let! result = hook.OnProvisioned("team-keyed", "admin")
            Expect.isTrue (isCompleted result) "key provision completes under a PerScopeKeyResolver"

            // The key now resolves by id — proof it was minted + persisted.
            let! key = (resolver :> IBlobEncryptionKeyResolver).ResolveKey(lifecycleScope "team-keyed")
            let! byId = (resolver :> IBlobEncryptionKeyResolver).ResolveKeyById key.KeyId
            Expect.isOk byId "the pre-created key resolves by id"
        }

        testCaseAsync "EncryptionKeyProvisionLifecycle is idempotent — re-provision keeps the same key"
        <| async {
            let resolver = newKeyResolver ()
            let provider = providerOf [ typeof<IBlobEncryptionKeyResolver>, box resolver ]
            let hook = EncryptionKeyProvisionLifecycle.create provider

            let! first = hook.OnProvisioned("team-keyed", "admin")
            Expect.isTrue (isCompleted first) "first provision completes"

            let! keyBefore = (resolver :> IBlobEncryptionKeyResolver).ResolveKey(lifecycleScope "team-keyed")

            let! second = hook.OnProvisioned("team-keyed", "admin")
            Expect.isTrue (isCompleted second) "re-provision completes"

            let! keyAfter = (resolver :> IBlobEncryptionKeyResolver).ResolveKey(lifecycleScope "team-keyed")
            Expect.equal keyAfter.KeyId keyBefore.KeyId "re-provision did not re-mint a different key"
        }

        testCaseAsync "EncryptionKeyProvisionLifecycle skips under a non-PerScope resolver"
        <| async {
            let single =
                SingleKeyResolver.create (FileSecretStore.FileSecretStore(baseDir = newTempDir ()) :> ISecretStore)

            let provider = providerOf [ typeof<IBlobEncryptionKeyResolver>, box single ]
            let hook = EncryptionKeyProvisionLifecycle.create provider

            let! result = hook.OnProvisioned("team-keyed", "admin")
            Expect.isTrue (isSkipped result) "a platform-wide / single key is not minted per scope"
        }

        // Phase 54g — provision/offboard symmetry: the per-scope key the
        // provision hook mints is exactly what the offboard hook
        // crypto-shreds. Provision → key resolves; offboard → KeyDestroyed.
        testCaseAsync "provision mints the per-scope key and offboard crypto-shreds it (symmetry)"
        <| async {
            let resolver = newKeyResolver ()
            let provider = providerOf [ typeof<IBlobEncryptionKeyResolver>, box resolver ]
            let provisionHook = EncryptionKeyProvisionLifecycle.create provider
            let offboardHook = EncryptionKeyLifecycle.create provider

            let! provisioned = provisionHook.OnProvisioned("team-sym", "admin")
            Expect.isTrue (isCompleted provisioned) "provision mints the key"

            let! key = (resolver :> IBlobEncryptionKeyResolver).ResolveKey(lifecycleScope "team-sym")
            let! present = (resolver :> IBlobEncryptionKeyResolver).ResolveKeyById key.KeyId
            Expect.isOk present "the key is present after provision"

            let! offboarded = offboardHook.OnDeprovisioned("team-sym", "admin")
            Expect.isTrue (isCompleted offboarded) "offboard crypto-shreds the key"

            let! afterShred = (resolver :> IBlobEncryptionKeyResolver).ResolveKeyById key.KeyId

            match afterShred with
            | Error(KeyDestroyed _) -> ()
            | other -> failwithf "expected KeyDestroyed after offboard, got %A" other
        }
    ]