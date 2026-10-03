module ToolUp.Platform.Tests.InProcess.BlobEntityStoreTests

open System
open System.IO
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EntityStore
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.SecondaryIndex
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 973 — the blob store's half: the vacuum, the snapshot, the refs ───
//
// The contract pack pins the ANSWER every store owes (`staleRefTests`). What
// only a blob-indexed store has is checked here, by reading the refs on
// disk: a lookup that met a stale ref counts it and reclaims it, a delete
// takes its refs with it, and `VacuumIndex` reclaims what a refused removal
// left behind.

let private staleActor = EntityPrincipal.ofPrincipal "tester-973"

[<Literal>]
let private EntityTypeName = "TestEntity"

let private entity (id: EntityId) (owner: string) : IEntityStoreContract.TestEntity = {
    Id = id
    Type = EntityTypeName
    Version = 0
    Owner = owner
    Status = "active"
}

/// A blob entity store over in-memory storage whose index-ref (`*.ref`)
/// deletes are refused while `Refusing` is set; `Inner` reads what is on
/// disk past the double.
type private StaleRig = {
    Store: BlobEntityStore
    Inner: IBlobStorage
    Refusing: bool ref
    Scope: string
}

let private staleRig () =
    let inner = InMemoryBlobStorage() :> IBlobStorage
    let refusing = ref false

    let blob =
        DataObjectStoreTests.DeleteRefusingBlobStorage(inner, (fun name -> refusing.Value && name.EndsWith ".ref"))
        :> IBlobStorage

    let registry = EntityRegistry()
    registry.Register<IEntityStoreContract.TestEntity>(IEntityStoreContract.testEntityRegistration)

    {
        Store = BlobEntityStore(DataObjectStore(blob), blob, registry, None)
        Inner = inner
        Refusing = refusing
        Scope = "team-973-" + Guid.NewGuid().ToString("N").Substring(0, 8)
    }

let private entities (rig: StaleRig) = rig.Store :> IEntityStore

let private saveAs (rig: StaleRig) (id: EntityId) (owner: string) = async {
    match! (entities rig).Save(rig.Scope, staleActor, entity id owner) with
    | Result.Ok _ -> ()
    | Result.Error e -> failwithf "Save failed: %A" e
}

/// The ids `FindByIndex(Owner, owner)` answers with.
let private findOwner (rig: StaleRig) (owner: string) = async {
    match! (entities rig).FindByIndex<IEntityStoreContract.TestEntity>(rig.Scope, EntityTypeName, "Owner", owner) with
    | Result.Ok refs -> return refs |> List.map _.Id |> List.sort
    | Result.Error e -> return failwithf "FindByIndex failed: %A" e
}

/// The `.ref` blobs on disk under one index key (the segment as written).
let private refsOnDisk (rig: StaleRig) (indexName: string) (segment: string) = async {
    let! names = rig.Inner.List(rig.Scope, $"entities/_indexes/{EntityTypeName}/{indexName}/{segment}/")
    return names.Length
}

let private ownerDrift (rig: StaleRig) =
    rig.Store.IndexDriftSnapshot rig.Scope
    |> List.filter (fun e -> e.IndexName = $"{EntityTypeName}/Owner")
    |> List.map (fun e -> e.SampleSize, e.ConsistentEntries, e.OrphanedIndexEntries, e.UnindexedCanonicals)

let private vacuumTests =
    let indexOver (storage: IBlobStorage) =
        BlobIndex.create<string, string> storage "c" "idx" id id Some

    testList "Phase 973 — BlobIndex.Vacuum" [

        testCaseAsync "Vacuum drops the refs isStale names, keeps the rest, and a second run removes nothing"
        <| async {
            let index = indexOver (InMemoryBlobStorage())
            do! index.Add "k" "a" None
            do! index.Add "k" "b" None
            do! index.Add "k" "c" None

            let! removed = index.Vacuum "k" (fun v -> async { return v <> "b" })
            Expect.equal removed 2 "two stale refs removed"

            let! left = index.Lookup "k"
            Expect.equal (left |> List.map fst) [ "b" ] "the live ref stays"

            let! again = index.Vacuum "k" (fun v -> async { return v <> "b" })
            Expect.equal again 0 "idempotent: a clean key loses nothing"
        }

        testCaseAsync "a refused delete is not counted, and the ref stays for the next pass"
        <| async {
            let inner = InMemoryBlobStorage()

            let index =
                indexOver (DataObjectStoreTests.DeleteRefusingBlobStorage(inner, (fun n -> n.EndsWith "/a.ref")))

            do! index.Add "k" "a" None
            do! index.Add "k" "b" None

            let! removed = index.Vacuum "k" (fun _ -> async { return true })
            Expect.equal removed 1 "only the delete that happened is counted"

            let! left = index.Lookup "k"
            Expect.equal (left |> List.map fst) [ "a" ] "the refused ref is still listed"
        }

        testCaseAsync "a ref a writer re-asserts between the check and the delete is put back, payload and all"
        <| async {
            let index = indexOver (InMemoryBlobStorage())
            let payload = [| 9uy; 7uy; 3uy |]
            do! index.Add "k" "a" (Some payload)

            // Stale when first asked; backed again when asked after the delete
            // — the window a concurrent save re-indexing "a" under "k" opens.
            let asked = ref 0

            let! removed =
                index.Vacuum "k" (fun _ -> async {
                    asked.Value <- asked.Value + 1
                    return asked.Value = 1
                })

            Expect.equal removed 0 "a ref put back is not counted as removed"
            Expect.equal asked.Value 2 "the value is judged again after its delete"

            let! left = index.Lookup "k"
            Expect.equal left [ "a", Some payload ] "the ref is back with the payload it had"
        }
    ]

let private storeTests =
    testList "Phase 973 — BlobEntityStore stale refs" [

        testCaseAsync
            "a stale ref met at lookup is counted in the drift snapshot and reclaimed; a second lookup finds the folder clean"
        <| async {
            let rig = staleRig ()
            rig.Refusing.Value <- true
            do! saveAs rig "e-1" "alice"
            do! saveAs rig "e-1" "bob"
            rig.Refusing.Value <- false

            let! before = refsOnDisk rig "Owner" "alice"
            Expect.equal before 1 "the refused removal left the alice ref on disk"

            let! answer = findOwner rig "alice"
            Expect.equal answer [] "the head carries bob"
            Expect.equal (ownerDrift rig) [ 1, 0, 1, 0 ] "one ref read, and it was stale"

            let! after = refsOnDisk rig "Owner" "alice"
            Expect.equal after 0 "the lookup reclaimed the stale ref it met"

            let! again = findOwner rig "alice"
            Expect.equal again [] "still no answer"
            Expect.equal (ownerDrift rig) [ 1, 0, 1, 0 ] "the second lookup read no ref at all: the folder is clean"

            let! underBob = findOwner rig "bob"
            Expect.equal underBob [ "e-1" ] "the live ref answers"
            Expect.equal (ownerDrift rig) [ 2, 1, 1, 0 ] "a consistent read is counted beside the stale one"

            Expect.isEmpty (rig.Store.IndexDriftSnapshot "another-scope") "the snapshot is the caller's scope only"
        }

        testCaseAsync "a lookup whose reclaim is refused still answers correctly, and the next lookup reclaims the ref"
        <| async {
            let rig = staleRig ()
            rig.Refusing.Value <- true
            do! saveAs rig "e-2" "alice"
            do! saveAs rig "e-2" "bob"

            let! refused = findOwner rig "alice"
            Expect.equal refused [] "the answer does not depend on the reclaim"

            let! stillThere = refsOnDisk rig "Owner" "alice"
            Expect.equal stillThere 1 "the refused reclaim left the ref"

            rig.Refusing.Value <- false
            let! healed = findOwner rig "alice"
            Expect.equal healed [] "still no answer"
            Expect.equal (ownerDrift rig) [ 2, 0, 2, 0 ] "each lookup counted the stale ref it met"

            let! gone = refsOnDisk rig "Owner" "alice"
            Expect.equal gone 0 "the second lookup reclaimed it"
        }

        testCaseAsync "Delete and DeleteIfVersion leave no ref behind in any declared index"
        <| async {
            let rig = staleRig ()
            do! saveAs rig "e-3" "alice"
            do! saveAs rig "e-4" "alice"

            match! (entities rig).Delete(rig.Scope, staleActor, EntityTypeName, "e-3") with
            | Result.Ok() -> ()
            | Result.Error e -> failwithf "Delete failed: %A" e

            match! (entities rig).DeleteIfVersion(rig.Scope, staleActor, EntityTypeName, "e-4", 1) with
            | Result.Ok() -> ()
            | Result.Error e -> failwithf "DeleteIfVersion failed: %A" e

            let! owner = refsOnDisk rig "Owner" "alice"
            Expect.equal owner 0 "no Owner ref outlives its entity"

            let! status = refsOnDisk rig "Status" "active"
            Expect.equal status 0 "no Status ref outlives its entity"
        }

        testCaseAsync "a refused DeleteIfVersion conflict removes no ref"
        <| async {
            let rig = staleRig ()
            do! saveAs rig "e-5" "alice"

            match! (entities rig).DeleteIfVersion(rig.Scope, staleActor, EntityTypeName, "e-5", 7) with
            | Result.Error(VersionConflict _) -> ()
            | other -> failwithf "expected VersionConflict, got %A" other

            let! owner = refsOnDisk rig "Owner" "alice"
            Expect.equal owner 1 "the entity is still there, and so is its ref"

            let! answer = findOwner rig "alice"
            Expect.equal answer [ "e-5" ] "and it still answers"
        }

        testCaseAsync
            "on a refusing store VacuumIndex reclaims a deleted entity's ref, and a second run removes nothing"
        <| async {
            let rig = staleRig ()
            rig.Refusing.Value <- true
            do! saveAs rig "e-6" "alice"

            match! (entities rig).Delete(rig.Scope, staleActor, EntityTypeName, "e-6") with
            | Result.Ok() -> ()
            | Result.Error e -> failwithf "Delete failed: %A" e

            let! left = refsOnDisk rig "Owner" "alice"
            Expect.equal left 1 "the refused removal left the ref"

            rig.Refusing.Value <- false
            let! vacuumed = rig.Store.VacuumIndex(rig.Scope, EntityTypeName, "Owner")
            Expect.equal vacuumed (Result.Ok 1) "the vacuum reclaimed it"

            let! gone = refsOnDisk rig "Owner" "alice"
            Expect.equal gone 0 "nothing left on disk"

            let! again = rig.Store.VacuumIndex(rig.Scope, EntityTypeName, "Owner")
            Expect.equal again (Result.Ok 0) "idempotent"
        }

        testCaseAsync "VacuumIndex keeps live refs and reaches a key the path encoding escaped"
        <| async {
            let rig = staleRig ()
            let compound = "x|y z"
            rig.Refusing.Value <- true
            do! saveAs rig "e-7" compound
            do! saveAs rig "e-7" "w"
            rig.Refusing.Value <- false
            do! saveAs rig "e-8" compound

            let segment = BlobIndex.pathSafeSegment compound
            let! before = refsOnDisk rig "Owner" segment
            Expect.equal before 2 "one stale and one live ref under the escaped key"

            let! vacuumed = rig.Store.VacuumIndex(rig.Scope, EntityTypeName, "Owner")
            Expect.equal vacuumed (Result.Ok 1) "only the stale ref went"

            let! answer = findOwner rig compound
            Expect.equal answer [ "e-8" ] "the live entity still answers"
            Expect.equal (ownerDrift rig) [ 1, 1, 0, 0 ] "the lookup met no stale ref: the vacuum got there first"
        }

        testCaseAsync "VacuumIndex refuses an unknown entity type and an undeclared index"
        <| async {
            let rig = staleRig ()
            let! unknownType = rig.Store.VacuumIndex(rig.Scope, "NoSuchType", "Owner")
            Expect.equal unknownType (Result.Error(UnknownEntityType "NoSuchType")) "unknown type"

            let! unknownIndex = rig.Store.VacuumIndex(rig.Scope, EntityTypeName, "NoSuchIndex")
            Expect.equal unknownIndex (Result.Error(InvalidIndex "NoSuchIndex")) "undeclared index"
        }

        testCaseAsync "the composed entity store's drift reaches /dev/inspect through the inspector seam"
        <| async {
            let inner = InMemoryBlobStorage() :> IBlobStorage
            let refusing = ref true

            let blob =
                DataObjectStoreTests.DeleteRefusingBlobStorage(
                    inner,
                    (fun name -> refusing.Value && name.EndsWith ".ref")
                )
                :> IBlobStorage

            let services = ServiceCollection()
            services.AddSingleton<IBlobStorage>(blob) |> ignore

            services.AddSingleton<IDataObjectStore>(DataObjectStore(blob) :> IDataObjectStore)
            |> ignore

            ComposeStores.registerEntityStore
                services
                {
                    ServerConfig.defaults with
                        EntityStore = EnabledEntityStore
                }
                [
                    fun registry ->
                        registry.Register<IEntityStoreContract.TestEntity>(IEntityStoreContract.testEntityRegistration)
                ]

            use sp = services.BuildServiceProvider()
            let store = sp.GetRequiredService<IEntityStore>()
            let scope = "team-973-inspect"

            for owner in [ "alice"; "bob" ] do
                match! store.Save(scope, staleActor, entity "e-9" owner) with
                | Result.Ok _ -> ()
                | Result.Error e -> failwithf "Save failed: %A" e

            match! store.FindByIndex<IEntityStoreContract.TestEntity>(scope, EntityTypeName, "Owner", "alice") with
            | Result.Ok refs -> Expect.isEmpty refs "no answer under alice"
            | Result.Error e -> failwithf "FindByIndex failed: %A" e

            match DevDiagnosticsHandler.indexInspectors [] sp with
            | [ inspector ] ->
                let! entries = inspector scope

                Expect.equal
                    (entries
                     |> List.map (fun e -> e.StoreName, e.IndexName, e.SampleSize, e.OrphanedIndexEntries))
                    [ "entities", $"{EntityTypeName}/Owner", 1, 1 ]
                    "the stale ref the lookup met is on the page"
            | other -> failtestf "expected the entity store's one inspector, got %d" other.Length
        }
    ]


/// Bind the IEntityStore contract pack to BlobEntityStore over a
/// LocalFileStorage substrate. Each factory call creates a fresh
/// temp directory + DataObjectStore + EntityRegistry so tests don't
/// share filesystem state.
let tests =
    let factory () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "toolup-entity-test-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory tempDir |> ignore
        let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
        let dos = DataObjectStore(blob) :> IDataObjectStore
        let registry = EntityRegistry()
        // Audit log stays None — tests focus on storage behaviour;
        // audit emission is exercised separately when wired up.
        let store = BlobEntityStore(dos, blob, registry, None) :> IEntityStore
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, registry, "team-a-" + suffix, "team-b-" + suffix

    /// Phase 806 — the same substrate, composed WITH the audit log the
    /// actor pack hands it.
    let auditedFactory (auditLog: IAuditLog) =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "toolup-entity-audit-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory tempDir |> ignore
        let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
        let dos = DataObjectStore(blob) :> IDataObjectStore
        let registry = EntityRegistry()
        let store = BlobEntityStore(dos, blob, registry, Some auditLog) :> IEntityStore
        store, registry, "team-audit-" + Guid.NewGuid().ToString("N").Substring(0, 8)

    /// Phase 973 — the same substrate behind a double that refuses the
    /// removal of an index ref (`*.ref`) while the pack's switch says so: the
    /// state a refused `BlobIndex.Remove` leaves behind.
    let refusingFactory (refuseIndexRemoval: unit -> bool) =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "toolup-entity-stale-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory tempDir |> ignore
        let inner = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage

        let blob =
            DataObjectStoreTests.DeleteRefusingBlobStorage(
                inner,
                fun name -> refuseIndexRemoval () && name.EndsWith ".ref"
            )
            :> IBlobStorage

        let dos = DataObjectStore(blob) :> IDataObjectStore
        let registry = EntityRegistry()
        let store = BlobEntityStore(dos, blob, registry, None) :> IEntityStore
        store, registry, "team-stale-" + Guid.NewGuid().ToString("N").Substring(0, 8)

    testList "BlobEntityStore" [
        IEntityStoreContract.tests "BlobEntityStore (blob-backed)" factory
        IEntityStoreContract.auditTests "BlobEntityStore (blob-backed)" auditedFactory
        IEntityStoreContract.staleRefTests "BlobEntityStore (blob-backed)" refusingFactory
        vacuumTests
        storeTests
    ]