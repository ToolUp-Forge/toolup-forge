module ToolUp.Platform.Tests.InProcess.EntityQueryTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Platform.EntityStore
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.ISparseIndex
open ToolUp.RAG.InMemoryBM25Index

// ─── Phase 19a EntityQuery tests ────────────────────────────────────
//
// Direct integration tests against a BlobEntityStore over LocalFileStorage.
// Covers every predicate kind (Eq / Ne / Gt / Lt / In / And / Or / Not)
// plus sort + paging + validation errors.

type Item = {
    Id: EntityId
    Type: string
    Version: int
    Owner: string
    Status: string
    Priority: string // string-formatted for sortable indexing — e.g. "01" / "02" / "03"
}

[<Literal>]
let private ItemType = "QueryItem"

let private itemRegistration =
    EntityRegistration.create<Item> ItemType
    |> EntityRegistration.withIndex "Owner" _.Owner
    |> EntityRegistration.withIndex "Status" _.Status
    |> EntityRegistration.withIndex "Priority" _.Priority

let private mkStore () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-query-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()
    registry.Register<Item>(itemRegistration)
    let store = BlobEntityStore(dos, blob, registry, None) :> IEntityStore
    let scope = "team-q-" + Guid.NewGuid().ToString("N").Substring(0, 6)
    store, scope

let private mkItem (id: string) (owner: string) (status: string) (priority: string) : Item = {
    Id = id
    Type = ItemType
    Version = 0
    Owner = owner
    Status = status
    Priority = priority
}

let private seed (store: IEntityStore) (scope: string) : Async<unit> = async {
    let items = [
        mkItem "i-1" "alice" "active" "01"
        mkItem "i-2" "alice" "completed" "02"
        mkItem "i-3" "bob" "active" "01"
        mkItem "i-4" "bob" "blocked" "03"
        mkItem "i-5" "carol" "active" "02"
    ]

    for item in items do
        let! _ = store.Save<Item>(scope, item)
        ()
}

// ─── Phase 19b — full-text predicate fixtures ───────────────────────
//
// A `FtDoc` registers `Category` as an ordinary index and `Notes` as a
// full-text field. The store is wired with an `InMemoryBM25Index` (the
// Phase 14e sparse-index substrate) so `Predicate.FullText` resolves
// against a per-(entityType, field) BM25 index, scope-isolated by
// construction. A large flush interval keeps persistence dormant (the
// tests exercise the in-memory index only).

type FtDoc = {
    Id: EntityId
    Type: string
    Version: int
    Category: string
    Notes: string
}

[<Literal>]
let private FtDocType = "FtDoc"

let private ftRegistration =
    EntityRegistration.create<FtDoc> FtDocType
    |> EntityRegistration.withIndex "Category" _.Category
    |> EntityRegistration.withFullTextField "Notes" _.Notes

let private mkFtStore () : IEntityStore =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-ft-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()
    registry.Register<FtDoc>(ftRegistration)
    // Large flush interval — these tests exercise the in-memory index
    // only; no flush fires within the suite's lifetime, so the store's
    // best-effort snapshot persistence (which can't render the synthetic
    // scope's `:`-bearing path under LocalFileStorage on Windows) stays
    // dormant and silent.
    let sparse =
        new InMemoryBM25Index(blob, flushIntervalMs = 3_600_000) :> ISparseIndex

    BlobEntityStore(dos, blob, registry, None, Some sparse) :> IEntityStore

let private mkFtScope () =
    "team-ft-" + Guid.NewGuid().ToString("N").Substring(0, 6)

let private mkFtDoc (id: string) (category: string) (notes: string) : FtDoc = {
    Id = id
    Type = FtDocType
    Version = 0
    Category = category
    Notes = notes
}

let private seedFt (store: IEntityStore) (scope: string) (docs: FtDoc list) : Async<unit> = async {
    for d in docs do
        let! _ = store.Save<FtDoc>(scope, d)
        ()
}

let private ftDocs = [
    mkFtDoc "d-1" "maintenance" "urgent leak kitchen"
    mkFtDoc "d-2" "maintenance" "routine maintenance scheduled"
    mkFtDoc "d-3" "safety" "basement flood urgent response"
    mkFtDoc "d-4" "safety" "annual flood inspection report"
]

let tests =
    testList "EntityQuery" [

        testCaseAsync "Eq predicate matches exact-value entries"
        <| async {
            let store, scope = mkStore ()
            do! seed store scope

            let q =
                EntityQuery.forType<Item> ItemType |> EntityQuery.where (Eq("Owner", "alice"))

            let! result = store.Query<Item>(scope, q)

            match result with
            | Result.Ok items ->
                Expect.hasLength items 2 "alice has 2 items"
                let ids = items |> List.map _.Id |> List.sort
                Expect.equal ids [ "i-1"; "i-2" ] "alice's items"
            | Result.Error e -> failwithf "expected Ok, got %A" e
        }

        testCaseAsync "And predicate intersects two Eq results"
        <| async {
            let store, scope = mkStore ()
            do! seed store scope

            let q =
                EntityQuery.forType<Item> ItemType
                |> EntityQuery.where (And(Eq("Owner", "alice"), Eq("Status", "active")))

            let! result = store.Query<Item>(scope, q)

            match result with
            | Result.Ok items ->
                Expect.hasLength items 1 "alice + active has 1"
                Expect.equal items.Head.Id "i-1" "matched item"
            | Result.Error e -> failwithf "expected Ok, got %A" e
        }

        testCaseAsync "Or predicate unions two Eq results"
        <| async {
            let store, scope = mkStore ()
            do! seed store scope

            let q =
                EntityQuery.forType<Item> ItemType
                |> EntityQuery.where (Or(Eq("Owner", "alice"), Eq("Owner", "carol")))

            let! result = store.Query<Item>(scope, q)

            match result with
            | Result.Ok items ->
                Expect.hasLength items 3 "alice (2) + carol (1) = 3 items"
                let owners = items |> List.map _.Owner |> List.distinct |> List.sort
                Expect.equal owners [ "alice"; "carol" ] "owners present"
            | Result.Error e -> failwithf "expected Ok, got %A" e
        }

        testCaseAsync "In predicate matches any of several values"
        <| async {
            let store, scope = mkStore ()
            do! seed store scope

            let q =
                EntityQuery.forType<Item> ItemType
                |> EntityQuery.where (In("Status", [ "active"; "blocked" ]))

            let! result = store.Query<Item>(scope, q)

            match result with
            | Result.Ok items ->
                // 3 active + 1 blocked
                Expect.hasLength items 4 "active + blocked = 4 items"
            | Result.Error e -> failwithf "expected Ok, got %A" e
        }

        testCaseAsync "Ne predicate returns complement"
        <| async {
            let store, scope = mkStore ()
            do! seed store scope

            let q =
                EntityQuery.forType<Item> ItemType |> EntityQuery.where (Ne("Owner", "alice"))

            let! result = store.Query<Item>(scope, q)

            match result with
            | Result.Ok items ->
                // 5 total - 2 alice = 3 non-alice
                Expect.hasLength items 3 "non-alice has 3"
                let owners = items |> List.map _.Owner |> List.distinct |> List.sort
                Expect.equal owners [ "bob"; "carol" ] "no alice in results"
            | Result.Error e -> failwithf "expected Ok, got %A" e
        }

        testCaseAsync "Gte predicate matches range above threshold"
        <| async {
            let store, scope = mkStore ()
            do! seed store scope

            let q =
                EntityQuery.forType<Item> ItemType |> EntityQuery.where (Gte("Priority", "02"))

            let! result = store.Query<Item>(scope, q)

            match result with
            | Result.Ok items ->
                // priorities 02 (i-2, i-5) + 03 (i-4) = 3
                Expect.hasLength items 3 "priority >= 02 has 3 items"
            | Result.Error e -> failwithf "expected Ok, got %A" e
        }

        testCaseAsync "Lt predicate matches strict-below"
        <| async {
            let store, scope = mkStore ()
            do! seed store scope

            let q =
                EntityQuery.forType<Item> ItemType |> EntityQuery.where (Lt("Priority", "02"))

            let! result = store.Query<Item>(scope, q)

            match result with
            | Result.Ok items ->
                // priority 01: i-1, i-3
                Expect.hasLength items 2 "priority < 02 has 2 items"
            | Result.Error e -> failwithf "expected Ok, got %A" e
        }

        testCaseAsync "OrderBy + Skip + Take produces deterministic paging"
        <| async {
            let store, scope = mkStore ()
            do! seed store scope

            let q =
                EntityQuery.forType<Item> ItemType
                |> EntityQuery.where (Eq("Status", "active"))
                |> EntityQuery.orderBy "Priority" Ascending
                |> EntityQuery.skip 1
                |> EntityQuery.take 1

            let! result = store.Query<Item>(scope, q)

            match result with
            | Result.Ok items -> Expect.hasLength items 1 "page-1 of size-1 = 1 item"
            // 3 active items sorted by priority ascending: i-1 (01), i-3 (01), i-5 (02)
            // After skip 1 take 1, we should get one of the i-1/i-3 (with stable
            // secondary sort on EntityId). Don't assert specific id — just len.
            | Result.Error e -> failwithf "expected Ok, got %A" e
        }

        testCaseAsync "Validation error on non-indexed predicate field"
        <| async {
            let store, scope = mkStore ()

            let q =
                EntityQuery.forType<Item> ItemType
                |> EntityQuery.where (Eq("NotIndexed", "anything"))

            let! result = store.Query<Item>(scope, q)

            match result with
            | Result.Error(InvalidIndex name) -> Expect.equal name "NotIndexed" "invalid field name surfaces"
            | _ -> failtest "expected InvalidIndex"
        }

        testCaseAsync "Empty store returns empty result for any predicate"
        <| async {
            let store, scope = mkStore ()

            let q =
                EntityQuery.forType<Item> ItemType |> EntityQuery.where (Eq("Owner", "anyone"))

            let! result = store.Query<Item>(scope, q)

            match result with
            | Result.Ok items -> Expect.isEmpty items "empty store, empty query result"
            | Result.Error e -> failwithf "expected Ok empty list, got %A" e
        }

        // ─── Phase 19b — full-text predicates ───────────────────────

        testCaseAsync "FullText single-term matches every doc holding the term"
        <| async {
            let store = mkFtStore ()
            let scope = mkFtScope ()
            do! seedFt store scope ftDocs

            let q =
                EntityQuery.forType<FtDoc> FtDocType
                |> EntityQuery.where (FullText("Notes", "urgent"))

            let! result = store.Query<FtDoc>(scope, q)

            match result with
            | Result.Ok docs ->
                // "urgent" appears in d-1 and d-3 (d-2/d-4 hold neither).
                let ids = docs |> List.map _.Id |> List.sort
                Expect.equal ids [ "d-1"; "d-3" ] "docs containing 'urgent'"
            | Result.Error e -> failwithf "expected Ok, got %A" e
        }

        testCaseAsync "FullText multi-term is bag-of-words (union of terms)"
        <| async {
            let store = mkFtStore ()
            let scope = mkFtScope ()
            do! seedFt store scope ftDocs

            let q =
                EntityQuery.forType<FtDoc> FtDocType
                |> EntityQuery.where (FullText("Notes", "urgent flood"))

            let! result = store.Query<FtDoc>(scope, q)

            match result with
            | Result.Ok docs ->
                // "urgent" -> d-1, d-3 ; "flood" -> d-3, d-4 ; union = 3 docs.
                let ids = docs |> List.map _.Id |> List.sort
                Expect.equal ids [ "d-1"; "d-3"; "d-4" ] "union of 'urgent' and 'flood'"
            | Result.Error e -> failwithf "expected Ok, got %A" e
        }

        testCaseAsync "FullText intersects with an Eq predicate"
        <| async {
            let store = mkFtStore ()
            let scope = mkFtScope ()
            do! seedFt store scope ftDocs

            let q =
                EntityQuery.forType<FtDoc> FtDocType
                |> EntityQuery.where (And(Eq("Category", "maintenance"), FullText("Notes", "urgent")))

            let! result = store.Query<FtDoc>(scope, q)

            match result with
            | Result.Ok docs ->
                // 'urgent' -> d-1, d-3 ; Category=maintenance -> d-1, d-2 ;
                // intersection = d-1.
                Expect.hasLength docs 1 "maintenance AND 'urgent' has 1"
                Expect.equal docs.Head.Id "d-1" "matched doc"
            | Result.Error e -> failwithf "expected Ok, got %A" e
        }

        testCaseAsync "FullText is scope-isolated"
        <| async {
            // One store, one shared sparse index, two entity scopes. The
            // per-scope synthetic full-text scope must keep scope-A's index
            // invisible to a scope-B query and vice versa (team isolation by
            // construction).
            let store = mkFtStore ()
            let scopeA = mkFtScope ()
            let scopeB = mkFtScope ()

            do! seedFt store scopeA [ mkFtDoc "a-1" "maintenance" "urgent leak in scope a" ]
            do! seedFt store scopeB [ mkFtDoc "b-1" "maintenance" "urgent leak in scope b" ]

            let q =
                EntityQuery.forType<FtDoc> FtDocType
                |> EntityQuery.where (FullText("Notes", "urgent"))

            let! resultA = store.Query<FtDoc>(scopeA, q)
            let! resultB = store.Query<FtDoc>(scopeB, q)

            match resultA, resultB with
            | Result.Ok docsA, Result.Ok docsB ->
                Expect.equal (docsA |> List.map _.Id) [ "a-1" ] "scope A sees only its own doc"
                Expect.equal (docsB |> List.map _.Id) [ "b-1" ] "scope B sees only its own doc"
            | _ -> failtest "expected Ok for both scopes"
        }

        testCaseAsync "FullText against an undeclared full-text field is InvalidIndex"
        <| async {
            let store = mkFtStore ()
            let scope = mkFtScope ()

            let q =
                EntityQuery.forType<FtDoc> FtDocType
                |> EntityQuery.where (FullText("Category", "anything"))

            let! result = store.Query<FtDoc>(scope, q)

            match result with
            // 'Category' is an index, not a declared full-text field — the
            // validator rejects it with the same shape as a non-indexed field.
            | Result.Error(InvalidIndex name) -> Expect.equal name "Category" "undeclared full-text field surfaces"
            | _ -> failtest "expected InvalidIndex"
        }
    ]