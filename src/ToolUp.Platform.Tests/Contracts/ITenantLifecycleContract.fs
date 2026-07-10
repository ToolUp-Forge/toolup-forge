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

/// Phase 305 — build a `ProvisioningRequest` for the request-aware
/// provisioning tests.
let private requestWith (owner: string) (displayName: string) : ProvisioningRequest = {
    Slug = "acme"
    OwnerUserId = owner
    Region = "eu-west"
    Tier = "standard"
    DisplayName = displayName
}

/// Phase 305 — cast a first-party hook (constructed via `create`, typed as
/// `ITenantLifecycle`) to its optional request-aware surface. The runtime
/// object implements the interface, so the downcast succeeds.
let private asProvisionContext (hook: ITenantLifecycle) : ITenantLifecycleProvisionContext =
    hook :?> ITenantLifecycleProvisionContext

let private ctxOf
    (scopeId: string)
    (actorUserId: string)
    (request: ProvisioningRequest option)
    : TenantProvisioningContext =
    {
        ScopeId = scopeId
        ActorUserId = actorUserId
        Request = request
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

        testCaseAsync "UserMembershipTeardownLifecycle skips when no ITeamStore is registered"
        <| async {
            let hook = UserMembershipTeardownLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("user-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without a team store"
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
                UserMembershipTeardownLifecycle.create emptyProvider
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
                    UserMembershipTeardownLifecycle.create emptyProvider
                    ToolUp.KnowledgeBase.Server.KnowledgeBaseLifecycle.create emptyProvider
                    ToolUp.RAG.RagVectorStoreLifecycle.create emptyProvider
                ]
                |> List.map _.Name

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

        testCaseAsync "Phase 54g hooks skip on OnDeprovisioned when their substrate is absent"
        <| async {
            // EncryptionKeyProvisionLifecycle is genuinely provision-only
            // (offboard shred is EncryptionKeyLifecycle's job). ConfigSeed +
            // OwnerTeamBootstrap gained Phase 306 offboard teardown, but with
            // no substrate composed they still degrade to Skipped — the
            // graceful-degrade bar every offboard hook must clear.
            let hooks = [
                ConfigSeedLifecycle.create emptyProvider
                OwnerTeamBootstrapLifecycle.create emptyProvider
                EncryptionKeyProvisionLifecycle.create emptyProvider
            ]

            for hook in hooks do
                let! result = hook.OnDeprovisioned("team-x", "admin")
                Expect.isTrue (isSkipped result) (sprintf "%s offboard skips without its substrate" hook.Name)
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

        // ─── Phase 305 — request-aware provisioning (OnProvisionedWith) ──

        testCaseAsync "OwnerTeamBootstrap OnProvisionedWith attaches the request's OwnerUserId (≠ acting admin)"
        <| async {
            let store = newTeamStore ()
            let provider = providerOf [ typeof<ITeamStore>, box store ]
            let hook = asProvisionContext (OwnerTeamBootstrapLifecycle.create provider)

            // The operator ("op-admin") provisions on the customer's behalf;
            // the request names "customer-9" as the owner.
            let ctx =
                ctxOf "team-behalf" "op-admin" (Some(requestWith "customer-9" "Behalf Co"))

            let! result = hook.OnProvisionedWith ctx
            Expect.isTrue (isCompleted result) "request-aware bootstrap completes"

            let! ownerRole = store.GetMemberRole("behalf", "customer-9")
            Expect.equal ownerRole (Some Owner) "the request's OwnerUserId is the team Owner"

            let! actorRole = store.GetMemberRole("behalf", "op-admin")
            Expect.equal actorRole None "the acting admin is NOT attached as owner"
        }

        testCaseAsync "OwnerTeamBootstrap OnProvisionedWith falls back to the actor when the request owner is blank"
        <| async {
            let store = newTeamStore ()
            let provider = providerOf [ typeof<ITeamStore>, box store ]
            let hook = asProvisionContext (OwnerTeamBootstrapLifecycle.create provider)

            // Blank request OwnerUserId → the acting admin owns (byte-
            // identical to the base OnProvisioned self-provision case).
            let ctx = ctxOf "team-fallback" "self-admin" (Some(requestWith "   " "Fallback Co"))
            let! result = hook.OnProvisionedWith ctx
            Expect.isTrue (isCompleted result) "bootstrap completes on the fallback path"

            let! role = store.GetMemberRole("fallback", "self-admin")
            Expect.equal role (Some Owner) "the acting admin is the owner when the request carries no owner"
        }

        testCaseAsync "OwnerTeamBootstrap OnProvisionedWith with Request = None matches the base OnProvisioned"
        <| async {
            let store = newTeamStore ()
            let provider = providerOf [ typeof<ITeamStore>, box store ]
            let hook = asProvisionContext (OwnerTeamBootstrapLifecycle.create provider)

            let! result = hook.OnProvisionedWith(ctxOf "team-none" "actor-1" None)
            Expect.isTrue (isCompleted result) "no-request path completes"

            let! role = store.GetMemberRole("none", "actor-1")
            Expect.equal role (Some Owner) "with no request the actor is the owner (legacy behaviour)"
        }

        testCaseAsync "ConfigSeed OnProvisionedWith seeds _platform appName from the request DisplayName (team scope)"
        <| async {
            let store = newConfigStore ()
            let provider = providerOf [ typeof<IConfigStore>, box store ]
            let hook = asProvisionContext (ConfigSeedLifecycle.create provider)

            let ctx = ctxOf "team-brand" "admin" (Some(requestWith "owner-1" "Acme Corp"))
            let! result = hook.OnProvisionedWith ctx
            Expect.isTrue (isCompleted result) "request-aware seed completes"

            let scope = lifecycleScope "team-brand"
            let! raw = store.GetRaw(scope, ConfigKeys.PlatformModuleKey)

            Expect.equal
                (Map.tryFind ConfigKeys.BrandingKeys.AppName raw)
                (Some "\"Acme Corp\"")
                "the team app-name defaults to the request's DisplayName"

            Expect.isTrue (Map.containsKey "currencySymbol" raw) "the schema-default fields are still seeded"
        }

        testCaseAsync "ConfigSeed base OnProvisioned seeds NO appName — legacy path is byte-identical"
        <| async {
            let store = newConfigStore ()
            let provider = providerOf [ typeof<IConfigStore>, box store ]
            let hook = ConfigSeedLifecycle.create provider

            let! result = hook.OnProvisioned("team-plain", "admin")
            Expect.isTrue (isCompleted result) "base seed completes"

            let scope = lifecycleScope "team-plain"
            let! raw = store.GetRaw(scope, ConfigKeys.PlatformModuleKey)

            Expect.isFalse
                (Map.containsKey ConfigKeys.BrandingKeys.AppName raw)
                "the base path seeds only schema defaults — no appName override"
        }

        testCaseAsync "ConfigSeed OnProvisionedWith does not seed appName for a non-team (user) scope"
        <| async {
            let store = newConfigStore ()
            let provider = providerOf [ typeof<IConfigStore>, box store ]
            let hook = asProvisionContext (ConfigSeedLifecycle.create provider)

            let ctx = ctxOf "user-solo" "admin" (Some(requestWith "owner-1" "Solo User"))
            let! result = hook.OnProvisionedWith ctx
            Expect.isTrue (isCompleted result) "user-scope seed completes"

            let scope = lifecycleScope "user-solo"
            let! raw = store.GetRaw(scope, ConfigKeys.PlatformModuleKey)

            Expect.isFalse
                (Map.containsKey ConfigKeys.BrandingKeys.AppName raw)
                "app-name branding is team-scoped — a user scope gets no appName seed"
        }

        // ─── Phase 306 — offboard teardown of seeded config + owner team ─
        //
        // Provision/offboard symmetry: what the Phase 54g provision hooks
        // create (seeded `_platform` config docs + the bootstrapped owner
        // team), the Phase 306 offboard counterparts remove. Idempotent (a
        // second offboard is a clean no-op) and scope-precise (only the
        // provisioning-seeded keys are cleared).

        testCaseAsync "ConfigSeed offboard clears the seeded _platform config docs"
        <| async {
            let store = newConfigStore ()
            let provider = providerOf [ typeof<IConfigStore>, box store ]
            let hook = ConfigSeedLifecycle.create provider
            let scope = lifecycleScope "team-teardown"

            let! seeded = hook.OnProvisioned("team-teardown", "admin")
            Expect.isTrue (isCompleted seeded) "provision seeds the config docs"

            let! platformBefore = store.GetRaw(scope, ConfigKeys.PlatformModuleKey)
            let! prefsBefore = store.GetRaw(scope, ConfigKeys.NotificationPrefsModuleKey)
            Expect.isFalse (Map.isEmpty platformBefore) "the _platform doc exists after provision"
            Expect.isFalse (Map.isEmpty prefsBefore) "the notification_prefs doc exists after provision"

            let! torndown = hook.OnDeprovisioned("team-teardown", "admin")
            Expect.isTrue (isCompleted torndown) "offboard teardown completes"

            let! platformAfter = store.GetRaw(scope, ConfigKeys.PlatformModuleKey)
            let! prefsAfter = store.GetRaw(scope, ConfigKeys.NotificationPrefsModuleKey)
            Expect.isTrue (Map.isEmpty platformAfter) "the seeded _platform doc is gone after offboard"
            Expect.isTrue (Map.isEmpty prefsAfter) "the seeded notification_prefs doc is gone after offboard"
        }

        testCaseAsync "ConfigSeed offboard is idempotent — a second teardown is a clean no-op"
        <| async {
            let store = newConfigStore ()
            let provider = providerOf [ typeof<IConfigStore>, box store ]
            let hook = ConfigSeedLifecycle.create provider

            let! _ = hook.OnProvisioned("team-idem-off", "admin")
            let! first = hook.OnDeprovisioned("team-idem-off", "admin")
            Expect.isTrue (isCompleted first) "first offboard completes"

            // Re-run over an already-torn-down scope (the resumable
            // re-dispatch) — Clear is idempotent, so this is a no-op.
            let! second = hook.OnDeprovisioned("team-idem-off", "admin")
            Expect.isTrue (isCompleted second) "a second offboard over a torn-down scope still completes"

            let scope = lifecycleScope "team-idem-off"
            let! platformAfter = store.GetRaw(scope, ConfigKeys.PlatformModuleKey)
            Expect.isTrue (Map.isEmpty platformAfter) "the doc stays gone after the second teardown"
        }

        testCaseAsync "ConfigSeed offboard clears ONLY the seeded keys — a foreign config doc survives"
        <| async {
            let store = newConfigStore ()
            let provider = providerOf [ typeof<IConfigStore>, box store ]
            let hook = ConfigSeedLifecycle.create provider
            let scope = lifecycleScope "team-foreign"

            let! _ = hook.OnProvisioned("team-foreign", "admin")

            // A document under a module key this hook never seeds — a domain
            // module's own settings, or an admin-authored doc. It must
            // survive offboard teardown (only the seed manifest is cleared).
            let! wrote =
                store.SetRaw(scope, "domain.custom", Map.ofList [ "currencySymbol", "\"$\"" ], platformProbeSchema)

            Expect.isOk wrote "the foreign doc write succeeds"

            let! torndown = hook.OnDeprovisioned("team-foreign", "admin")
            Expect.isTrue (isCompleted torndown) "offboard teardown completes"

            let! seededAfter = store.GetRaw(scope, ConfigKeys.PlatformModuleKey)
            Expect.isTrue (Map.isEmpty seededAfter) "the seeded _platform doc is cleared"

            let! foreignAfter = store.GetRaw(scope, "domain.custom")

            Expect.equal
                (Map.tryFind "currencySymbol" foreignAfter)
                (Some "\"$\"")
                "a non-seeded module doc is left intact — teardown is manifest-scoped"
        }

        testCaseAsync "OwnerTeamBootstrap offboard deletes the bootstrapped owner-team row"
        <| async {
            let store = newTeamStore ()
            let provider = providerOf [ typeof<ITeamStore>, box store ]
            let hook = OwnerTeamBootstrapLifecycle.create provider

            let! created = hook.OnProvisioned("team-del", "owner-1")
            Expect.isTrue (isCompleted created) "provision creates the owner team"

            let! before = store.GetTeam "del"
            Expect.isSome before "the owner team exists after provision"

            let! torndown = hook.OnDeprovisioned("team-del", "owner-1")
            Expect.isTrue (isCompleted torndown) "offboard teardown completes"

            let! after = store.GetTeam "del"
            Expect.isNone after "the owner-team row is gone after offboard"
        }

        testCaseAsync "OwnerTeamBootstrap offboard is idempotent — a second teardown is a clean no-op"
        <| async {
            let store = newTeamStore ()
            let provider = providerOf [ typeof<ITeamStore>, box store ]
            let hook = OwnerTeamBootstrapLifecycle.create provider

            let! _ = hook.OnProvisioned("team-del2", "owner-1")

            let! first = hook.OnDeprovisioned("team-del2", "owner-1")
            Expect.isTrue (isCompleted first) "first offboard deletes the team"

            // The team row is already gone — the GetTeam guard makes this a
            // no-op Completed rather than a DeleteTeam error.
            let! second = hook.OnDeprovisioned("team-del2", "owner-1")
            Expect.isTrue (isCompleted second) "a second offboard over a deleted team still completes"
        }

        testCaseAsync "OwnerTeamBootstrap offboard skips a non-team scope"
        <| async {
            let store = newTeamStore ()
            let provider = providerOf [ typeof<ITeamStore>, box store ]
            let hook = OwnerTeamBootstrapLifecycle.create provider

            let! result = hook.OnDeprovisioned("user-solo", "owner-1")
            Expect.isTrue (isSkipped result) "a user scope has no owner team to tear down"
        }

        // Full provision→offboard symmetry across both hooks at once: what
        // provisioning creates (seeded config + owner team), offboarding
        // removes — the same shape as the Phase 54g encryption-key symmetry.
        testCaseAsync "provision seeds config + owner team; offboard removes both (symmetry)"
        <| async {
            let configStore = newConfigStore ()
            let teamStore = newTeamStore ()

            let provider =
                providerOf [ typeof<IConfigStore>, box configStore; typeof<ITeamStore>, box teamStore ]

            let configHook = ConfigSeedLifecycle.create provider
            let teamHook = OwnerTeamBootstrapLifecycle.create provider
            let scope = lifecycleScope "team-sym306"

            let! seeded = configHook.OnProvisioned("team-sym306", "owner-1")
            let! bootstrapped = teamHook.OnProvisioned("team-sym306", "owner-1")
            Expect.isTrue (isCompleted seeded) "config seeded"
            Expect.isTrue (isCompleted bootstrapped) "owner team bootstrapped"

            let! platformBefore = configStore.GetRaw(scope, ConfigKeys.PlatformModuleKey)
            let! teamBefore = teamStore.GetTeam "sym306"
            Expect.isFalse (Map.isEmpty platformBefore) "seeded config present after provision"
            Expect.isSome teamBefore "owner team present after provision"

            let! configOff = configHook.OnDeprovisioned("team-sym306", "owner-1")
            let! teamOff = teamHook.OnDeprovisioned("team-sym306", "owner-1")
            Expect.isTrue (isCompleted configOff) "config teardown completes"
            Expect.isTrue (isCompleted teamOff) "team teardown completes"

            let! platformAfter = configStore.GetRaw(scope, ConfigKeys.PlatformModuleKey)
            let! teamAfter = teamStore.GetTeam "sym306"
            Expect.isTrue (Map.isEmpty platformAfter) "seeded config removed on offboard"
            Expect.isNone teamAfter "owner team removed on offboard"
        }

        // Phase 306 task 3 — wired into the exportThenDeprovision path: the
        // teardown runs strictly AFTER the export bundle is captured, so the
        // export still sees the seeded config + owner team before they are
        // removed. The runExport callback snapshots the stores at capture
        // time; the assertion is that snapshot saw the data present, and the
        // post-bundle stores show it gone.
        testCaseAsync "exportThenDeprovision captures seeded config + owner team BEFORE the teardown removes them"
        <| async {
            let configStore = newConfigStore ()
            let teamStore = newTeamStore ()

            let provider =
                providerOf [ typeof<IConfigStore>, box configStore; typeof<ITeamStore>, box teamStore ]

            let configHook = ConfigSeedLifecycle.create provider
            let teamHook = OwnerTeamBootstrapLifecycle.create provider
            let scope = lifecycleScope "team-export306"

            let! _ = configHook.OnProvisioned("team-export306", "owner-1")
            let! _ = teamHook.OnProvisioned("team-export306", "owner-1")

            let noAudit (_scopeId: string) (_event: AuditEvent) = async { return () }

            // Snapshot the stores at export-capture time — this proves the
            // export ran before teardown (fail-closed ordering: export first).
            let configAtCapture = ref false
            let teamAtCapture = ref false

            let runExport () = async {
                let! platformRaw = configStore.GetRaw(scope, ConfigKeys.PlatformModuleKey)
                let! team = teamStore.GetTeam "export306"
                configAtCapture.Value <- not (Map.isEmpty platformRaw)
                teamAtCapture.Value <- Option.isSome team

                return
                    Ok {
                        Container = "_platform"
                        BlobPath = "tenant-export/team-export306/export.json"
                        ContentHash = "abc"
                        SegmentCount = 1
                    }
            }

            let! result =
                TenantLifecycleAggregator.exportThenDeprovision
                    noAudit
                    runExport
                    [ configHook; teamHook ]
                    "team-export306"
                    "owner-1"

            match result with
            | Ok _ -> ()
            | Error e -> failtestf "expected Ok; got %s" e

            Expect.isTrue configAtCapture.Value "the export saw the seeded config present (captured before teardown)"
            Expect.isTrue teamAtCapture.Value "the export saw the owner team present (captured before teardown)"

            let! platformAfter = configStore.GetRaw(scope, ConfigKeys.PlatformModuleKey)
            let! teamAfter = teamStore.GetTeam "export306"
            Expect.isTrue (Map.isEmpty platformAfter) "the seeded config was torn down after the export"
            Expect.isNone teamAfter "the owner team was torn down after the export"
        }
    ]