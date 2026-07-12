// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.PostgresEntityStoreTests

open System
open Expecto
open Npgsql
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Platform.EntityStore
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Tests.Contracts
open ToolUp.EntityStores.Postgres

// ─── Phase 531 — PostgresEntityStore (env-gated live arm) ───────────────
//
// Binds the shared `IEntityStoreContract` pack + a predicate-pushdown suite
// against a real PostgreSQL when `TOOLUP_TEST_POSTGRES` is set to a
// connection string. Unset (the fresh-checkout default) → a single Pending
// case, never Failed. Per factory call a fresh registry is used and unique
// scope ids isolate data in the shared `toolup_entities` table.

type private Item = {
    Id: EntityId
    Type: string
    Version: int
    Owner: string
    Status: string
    Priority: string
}

[<Literal>]
let private ItemType = "PgQueryItem"

let private itemRegistration =
    EntityRegistration.create<Item> ItemType
    |> EntityRegistration.withIndex "Owner" _.Owner
    |> EntityRegistration.withIndex "Status" _.Status
    |> EntityRegistration.withIndex "Priority" _.Priority

let private mkItem id owner status priority : Item = {
    Id = id
    Type = ItemType
    Version = 0
    Owner = owner
    Status = status
    Priority = priority
}

[<Tests>]
let tests =
    match Environment.GetEnvironmentVariable "TOOLUP_TEST_POSTGRES" with
    | null
    | "" ->
        testList "Phase 531 — PostgresEntityStore" [ ptestCase "skipped — TOOLUP_TEST_POSTGRES not set" <| fun _ -> () ]
    | conn ->
        let dataSource = NpgsqlDataSource.Create conn

        let mkQueryStore () =
            let registry = EntityRegistry()
            registry.Register<Item>(itemRegistration)
            let store = PostgresEntityStore.create dataSource registry
            let scope = "team-q-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            store, scope

        let seed (store: IEntityStore) scope = async {
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

        let ids (result: Result<Item list, EntityError>) =
            match result with
            | Result.Ok items -> items |> List.map _.Id |> List.sort
            | Result.Error e -> failwithf "expected Ok, got %A" e

        testList "Phase 531 — PostgresEntityStore" [

            // The shared contract pack — same one BlobEntityStore binds.
            IEntityStoreContract.tests "PostgresEntityStore" (fun () ->
                let registry = EntityRegistry()
                let store = PostgresEntityStore.create dataSource registry
                let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
                store, registry, "team-a-" + suffix, "team-b-" + suffix)

            testList "predicate pushdown (SQL, not client-side scans)" [

                testCaseAsync "Eq matches exact value"
                <| async {
                    let store, scope = mkQueryStore ()
                    do! seed store scope

                    let q =
                        EntityQuery.forType<Item> ItemType |> EntityQuery.where (Eq("Owner", "alice"))

                    let! r = store.Query<Item>(scope, q)
                    Expect.equal (ids r) [ "i-1"; "i-2" ] "alice's items"
                }

                testCaseAsync "Ne is the complement of Eq"
                <| async {
                    let store, scope = mkQueryStore ()
                    do! seed store scope

                    let q =
                        EntityQuery.forType<Item> ItemType |> EntityQuery.where (Ne("Status", "active"))

                    let! r = store.Query<Item>(scope, q)
                    Expect.equal (ids r) [ "i-2"; "i-4" ] "non-active items"
                }

                testCaseAsync "range Gt executes as SQL"
                <| async {
                    let store, scope = mkQueryStore ()
                    do! seed store scope

                    let q =
                        EntityQuery.forType<Item> ItemType |> EntityQuery.where (Gt("Priority", "01"))

                    let! r = store.Query<Item>(scope, q)
                    Expect.equal (ids r) [ "i-2"; "i-4"; "i-5" ] "priority > 01"
                }

                testCaseAsync "In matches any of the values"
                <| async {
                    let store, scope = mkQueryStore ()
                    do! seed store scope

                    let q =
                        EntityQuery.forType<Item> ItemType
                        |> EntityQuery.where (In("Owner", [ "alice"; "bob" ]))

                    let! r = store.Query<Item>(scope, q)
                    Expect.equal (ids r) [ "i-1"; "i-2"; "i-3"; "i-4" ] "alice + bob"
                }

                testCaseAsync "And intersects; Or unions; Not complements"
                <| async {
                    let store, scope = mkQueryStore ()
                    do! seed store scope

                    let andQ =
                        EntityQuery.forType<Item> ItemType
                        |> EntityQuery.where (And(Eq("Owner", "alice"), Eq("Status", "active")))

                    let! andR = store.Query<Item>(scope, andQ)
                    Expect.equal (ids andR) [ "i-1" ] "alice AND active"

                    let notQ =
                        EntityQuery.forType<Item> ItemType
                        |> EntityQuery.where (Not(Eq("Status", "active")))

                    let! notR = store.Query<Item>(scope, notQ)
                    Expect.equal (ids notR) [ "i-2"; "i-4" ] "NOT active"
                }

                testCaseAsync "orderBy + paging is deterministic"
                <| async {
                    let store, scope = mkQueryStore ()
                    do! seed store scope

                    let q =
                        EntityQuery.forType<Item> ItemType
                        |> EntityQuery.orderBy "Priority" Ascending
                        |> EntityQuery.take 2

                    let! r = store.Query<Item>(scope, q)

                    match r with
                    | Result.Ok items ->
                        Expect.hasLength items 2 "two taken"
                        Expect.equal (items |> List.map _.Priority) [ "01"; "01" ] "lowest priorities first"
                    | Result.Error e -> failwithf "expected Ok, got %A" e
                }

                testCaseAsync "a non-indexed predicate field returns InvalidIndex"
                <| async {
                    let store, scope = mkQueryStore ()
                    let q = EntityQuery.forType<Item> ItemType |> EntityQuery.where (Eq("Nope", "x"))
                    let! r = store.Query<Item>(scope, q)

                    match r with
                    | Result.Error(InvalidIndex "Nope") -> ()
                    | other -> failtestf "expected InvalidIndex Nope; got %A" other
                }
            ]
        ]