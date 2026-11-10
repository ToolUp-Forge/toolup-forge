module ToolUp.Platform.Tests.InProcess.ConsentStateStoreTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra.DataObjectStore
open ToolUp.Platform.Infra.EntityStore
open ToolUp.Platform.Infra.IEntityStore
open ToolUp.Platform.Audit.Consent.ConsentStateStore
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Infra
open ToolUp.Platform.Testing

// ─── Phase 159 — IConsentStateStore in-process bindings ──────────────
//
// Binds the portable `IConsentStateStoreContract` pack against both
// shipped implementations:
//   * `InMemoryConsentStateStore` — dev / test default.
//   * `EntityBackedConsentStateStore` over a real `BlobEntityStore`
//     (LocalFileStorage temp dir) — the production durable path.
//
// Plus a restart-durability test the portable pack cannot express: a
// second store instance constructed over the SAME on-disk substrate
// reads back a prior instance's write — proving the acceptance
// criterion "a subject's consent persists across restart".

let private freshScopes () =
    let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
    "team-a-" + suffix, "team-b-" + suffix

// ─── In-memory binding ───────────────────────────────────────────────

let tests =
    let factory () =
        let store = InMemoryConsentStateStore() :> IConsentStateStore
        let scopeA, scopeB = freshScopes ()
        store, scopeA, scopeB

    IConsentStateStoreContract.tests "InMemoryConsentStateStore" factory

// ─── Entity-backed binding (durable, production path) ────────────────

/// Build a `BlobEntityStore` over a fresh temp dir with the
/// `ConsentRecord` entity type registered. Returns the store + the
/// temp dir so a restart test can reopen the same substrate.
let private buildEntityStore (tempDir: string) : IEntityStore =
    let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()
    registry.Register ConsentRecord.registration
    BlobEntityStore(dos, blob, registry, NoOpAuditLog()) :> IEntityStore

let entityBackedTests =
    // The contract's factory has no teardown hook, so each directory
    // outlives its case; the family name attributes it in the census.
    let factory () =
        let tempDir = TempDir.create "consent-test"
        let entityStore = buildEntityStore tempDir.Path
        let store = EntityBackedConsentStateStore(entityStore) :> IConsentStateStore
        let scopeA, scopeB = freshScopes ()
        store, scopeA, scopeB

    IConsentStateStoreContract.tests "EntityBackedConsentStateStore (blob-backed)" factory

// ─── Restart durability ──────────────────────────────────────────────

let restartPersistenceTests =
    testList "EntityBackedConsentStateStore — durability across restart" [
        testCaseAsync "a subject's consent survives a fresh store instance over the same substrate"
        <| async {
            use tempDir = TempDir.create "consent-restart"
            let scope = "team-restart"
            let at = DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero)

            // First "process": write the decision, then drop the store.
            let store1 =
                EntityBackedConsentStateStore(buildEntityStore tempDir.Path) :> IConsentStateStore

            let input =
                ConsentRecord.create
                    "subject-restart"
                    (Set.ofList [ Analytics; Marketing ])
                    Set.empty
                    at
                    BannerInteraction

            let! _ = store1.Upsert(scope, input)

            // Second "process": brand-new store + registry over the
            // SAME on-disk substrate. No in-memory state carries over.
            let store2 =
                EntityBackedConsentStateStore(buildEntityStore tempDir.Path) :> IConsentStateStore

            let! fetched = store2.Get(scope, "subject-restart")

            match fetched with
            | Some r ->
                Expect.isTrue (Set.contains Analytics r.Granted) "Analytics survives restart"
                Expect.isTrue (Set.contains Marketing r.Granted) "Marketing survives restart"
                Expect.equal r.Subject "subject-restart" "subject survives restart"
            | None -> failtest "consent decision did not survive a restart"
        }
    ]