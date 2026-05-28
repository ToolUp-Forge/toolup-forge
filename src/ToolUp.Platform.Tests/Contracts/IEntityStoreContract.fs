module ToolUp.Platform.Tests.Contracts.IEntityStoreContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore

// ─── Phase 19 IEntityStore contract tests ───────────────────────────
//
// Parametrised contract pack — any IEntityStore implementation binds
// to it. The factory returns `(store, registry, scopeA, scopeB)` so
// tests can exercise scope-isolation paths against a single store
// instance.

type TestEntity = {
    Id: EntityId
    Type: string
    Version: int
    Owner: string
    Status: string
}

type OtherEntity = {
    Id: EntityId
    Type: string
    Version: int
    Note: string
}

[<Literal>]
let private TestEntityType = "TestEntity"

[<Literal>]
let private OtherEntityType = "OtherEntity"

let testEntityRegistration =
    EntityRegistration.create<TestEntity> TestEntityType
    |> EntityRegistration.withIndex "Owner" _.Owner
    |> EntityRegistration.withIndex "Status" _.Status

let otherEntityRegistration = EntityRegistration.create<OtherEntity> OtherEntityType

let private mkEntity (id: EntityId) (owner: string) (status: string) : TestEntity = {
    Id = id
    Type = TestEntityType
    Version = 0 // overridden by store
    Owner = owner
    Status = status
}

let tests (name: string) (factory: unit -> IEntityStore * EntityStore.EntityRegistry * string * string) =
    let setup () =
        let store, registry, scopeA, scopeB = factory ()
        // Both registrations are required for the type-isolation test.
        registry.Register<TestEntity>(testEntityRegistration)
        registry.Register<OtherEntity>(otherEntityRegistration)
        store, scopeA, scopeB

    testList $"{name} — IEntityStore contract" [

        testCaseAsync "Save then Get returns the entity with assigned Version 1"
        <| async {
            let store, scopeA, _ = setup ()
            let entity = mkEntity "e-1" "alice" "active"

            let! saveResult = store.Save<TestEntity>(scopeA, entity)
            Expect.isOk saveResult "save succeeds"

            match saveResult with
            | Result.Ok ref -> Expect.equal ref.Version 1 "first save gets Version 1"
            | _ -> ()

            let! getResult = store.Get<TestEntity>(scopeA, TestEntityType, "e-1")

            match getResult with
            | Result.Ok loaded ->
                Expect.equal loaded.Id "e-1" "Id preserved"
                Expect.equal loaded.Type TestEntityType "Type preserved"
                Expect.equal loaded.Version 1 "Version reflects first-save assignment"
                Expect.equal loaded.Owner "alice" "user field preserved"
                Expect.equal loaded.Status "active" "user field preserved"
            | Result.Error e -> failwithf "expected Ok, got %A" e
        }

        testCaseAsync "Save twice bumps the version; ListVersions returns both"
        <| async {
            let store, scopeA, _ = setup ()
            let v1 = mkEntity "e-2" "alice" "active"

            let! _ = store.Save<TestEntity>(scopeA, v1)
            let v2 = { v1 with Status = "completed" }
            let! saveResult = store.Save<TestEntity>(scopeA, v2)

            match saveResult with
            | Result.Ok ref -> Expect.equal ref.Version 2 "second save gets Version 2"
            | Result.Error e -> failwithf "expected Ok, got %A" e

            let! versions = store.ListVersions<TestEntity>(scopeA, TestEntityType, "e-2")
            Expect.hasLength versions 2 "two versions present"
            Expect.equal (versions |> List.map _.Version) [ 1; 2 ] "versions ascending"
        }

        testCaseAsync "Get of non-existent entity returns NotFound"
        <| async {
            let store, scopeA, _ = setup ()
            let! result = store.Get<TestEntity>(scopeA, TestEntityType, "missing")

            match result with
            | Result.Error(NotFound(t, id)) ->
                Expect.equal t TestEntityType "type preserved"
                Expect.equal id "missing" "id preserved"
            | _ -> failtest "expected NotFound"
        }

        testCaseAsync "Delete makes subsequent Get return NotFound"
        <| async {
            let store, scopeA, _ = setup ()
            let entity = mkEntity "e-3" "alice" "active"
            let! _ = store.Save<TestEntity>(scopeA, entity)

            let! delete = store.Delete(scopeA, TestEntityType, "e-3")
            Expect.isOk delete "delete succeeds"

            let! getResult = store.Get<TestEntity>(scopeA, TestEntityType, "e-3")

            match getResult with
            | Result.Error(NotFound _) -> ()
            | _ -> failtest "expected NotFound after delete"
        }

        testCaseAsync "Delete is idempotent"
        <| async {
            let store, scopeA, _ = setup ()
            let! result = store.Delete(scopeA, TestEntityType, "never-existed")
            Expect.isOk result "deleting a non-existent entity returns Ok"
        }

        testCaseAsync "FindByIndex returns entities matching the indexed value"
        <| async {
            let store, scopeA, _ = setup ()
            let! _ = store.Save<TestEntity>(scopeA, mkEntity "e-4" "alice" "active")
            let! _ = store.Save<TestEntity>(scopeA, mkEntity "e-5" "alice" "completed")
            let! _ = store.Save<TestEntity>(scopeA, mkEntity "e-6" "bob" "active")

            let! aliceResult = store.FindByIndex<TestEntity>(scopeA, TestEntityType, "Owner", "alice")

            match aliceResult with
            | Result.Ok refs ->
                Expect.hasLength refs 2 "alice has 2 entities"
                let ids = refs |> List.map _.Id |> List.sort
                Expect.equal ids [ "e-4"; "e-5" ] "alice's entities"
            | Result.Error e -> failwithf "expected Ok, got %A" e

            let! bobResult = store.FindByIndex<TestEntity>(scopeA, TestEntityType, "Owner", "bob")

            match bobResult with
            | Result.Ok refs -> Expect.hasLength refs 1 "bob has 1 entity"
            | Result.Error e -> failwithf "expected Ok, got %A" e
        }

        testCaseAsync "FindByIndex with non-existent index returns InvalidIndex"
        <| async {
            let store, scopeA, _ = setup ()

            let! result = store.FindByIndex<TestEntity>(scopeA, TestEntityType, "NotAnIndex", "any")

            match result with
            | Result.Error(InvalidIndex name) -> Expect.equal name "NotAnIndex" "index name preserved"
            | _ -> failtest "expected InvalidIndex"
        }

        testCaseAsync "Scope isolation — entities in scope A are invisible from scope B"
        <| async {
            let store, scopeA, scopeB = setup ()
            let entity = mkEntity "shared-id" "alice" "active"
            let! _ = store.Save<TestEntity>(scopeA, entity)

            // Same id, different scope — scope B should see nothing.
            let! getB = store.Get<TestEntity>(scopeB, TestEntityType, "shared-id")

            match getB with
            | Result.Error(NotFound _) -> ()
            | _ -> failtest "expected NotFound in scope B"

            // Save in scope B with the same id — should be a fresh entity.
            let! _ = store.Save<TestEntity>(scopeB, { entity with Owner = "bob" })

            // Scope A's entity should be unchanged.
            let! getA = store.Get<TestEntity>(scopeA, TestEntityType, "shared-id")

            match getA with
            | Result.Ok loaded -> Expect.equal loaded.Owner "alice" "scope A's entity unchanged"
            | Result.Error e -> failwithf "expected Ok in scope A, got %A" e
        }

        testCaseAsync "Type isolation — different types in same scope don't collide"
        <| async {
            let store, scopeA, _ = setup ()

            let testE: TestEntity = {
                Id = "shared-id"
                Type = TestEntityType
                Version = 0
                Owner = "alice"
                Status = "active"
            }

            let otherE: OtherEntity = {
                Id = "shared-id"
                Type = OtherEntityType
                Version = 0
                Note = "from-other-type"
            }

            let! _ = store.Save<TestEntity>(scopeA, testE)
            let! _ = store.Save<OtherEntity>(scopeA, otherE)

            let! getTest = store.Get<TestEntity>(scopeA, TestEntityType, "shared-id")
            let! getOther = store.Get<OtherEntity>(scopeA, OtherEntityType, "shared-id")

            match getTest with
            | Result.Ok t -> Expect.equal t.Owner "alice" "TestEntity intact"
            | Result.Error e -> failwithf "expected Ok TestEntity, got %A" e

            match getOther with
            | Result.Ok o -> Expect.equal o.Note "from-other-type" "OtherEntity intact"
            | Result.Error e -> failwithf "expected Ok OtherEntity, got %A" e
        }

        testCaseAsync "Count returns the number of entities of a type"
        <| async {
            let store, scopeA, _ = setup ()

            let! initial = store.Count(scopeA, TestEntityType)
            Expect.equal initial 0 "fresh store has zero entities"

            let! _ = store.Save<TestEntity>(scopeA, mkEntity "c-1" "alice" "active")
            let! _ = store.Save<TestEntity>(scopeA, mkEntity "c-2" "bob" "active")

            let! after = store.Count(scopeA, TestEntityType)
            Expect.equal after 2 "two entities counted"
        }

        testCaseAsync "ListAll paginates deterministically"
        <| async {
            let store, scopeA, _ = setup ()

            for i in 0..4 do
                let! _ = store.Save<TestEntity>(scopeA, mkEntity (sprintf "p-%d" i) "alice" "active")
                ()

            let! page1 = store.ListAll<TestEntity>(scopeA, TestEntityType, 0, 2)
            let! page2 = store.ListAll<TestEntity>(scopeA, TestEntityType, 2, 2)
            let! page3 = store.ListAll<TestEntity>(scopeA, TestEntityType, 4, 2)

            Expect.hasLength page1 2 "page 1 has 2"
            Expect.hasLength page2 2 "page 2 has 2"
            Expect.hasLength page3 1 "page 3 has 1 (last)"

            // Combined results must cover all 5 entities exactly once.
            let allIds = (page1 @ page2 @ page3) |> List.map _.Id |> List.sort
            Expect.equal allIds [ "p-0"; "p-1"; "p-2"; "p-3"; "p-4" ] "complete coverage"
        }

        testCaseAsync "Save with unregistered entity type returns UnknownEntityType"
        <| async {
            let store, scopeA, _ = setup ()

            let weird: TestEntity = {
                Id = "u-1"
                Type = "Unregistered"
                Version = 0
                Owner = "alice"
                Status = "active"
            }

            let! result = store.Save<TestEntity>(scopeA, weird)

            match result with
            | Result.Error(UnknownEntityType t) -> Expect.equal t "Unregistered" "type preserved"
            | _ -> failtest "expected UnknownEntityType"
        }
    ]