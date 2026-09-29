// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.PostgresFactStoreTests

open System
open System.Text
open System.Text.Json
open Expecto
open Microsoft.Extensions.DependencyInjection
open Npgsql
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Facts
open ToolUp.FactStores.Postgres
open ToolUp.Platform.Tests.Contracts

// ─── Phase 888 — PostgresFactStore ───────────────────────────────────
//
// Two halves.
//
// **Always run (no database).** The companion's options and statement
// shapes (every statement binds the scope — GP 4), the blob store's
// multi-replica scale guard and its composition, the replacement seam
// (`FactsCompose.withFactStoreImplementation`), and the differential pack
// self-bound to two `BlobFactStore` read paths — so the pack itself is
// proven sound on every run, not only when a database is present.
//
// **Live (`TOOLUP_TEST_POSTGRES` = a connection string).** BOTH
// `IFactStore` contract packs bound to the companion, unmodified; the
// differential pack holding it to `BlobFactStore` over one seeded fact
// base; the audit shape; concurrent writers racing one lineage; the
// schema modes; the future-dated head; and a point read at 300,000
// subjects, whose touched-row count is read from the executed plan and
// recorded. Unset (the fresh-checkout default) → one Pending case, never
// Failed.

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draftFor (member': string) (metric: string) (inputHash: string) (value: decimal) : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ member' ]
    }
    Metric = MetricRef metric
    Value = Scalar value
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ inputHash ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Disclosure.Surfaceable
}

let private blobReference (registry: IMetricRegistry option) (clock: unit -> DateTime) : IFactStore * string =
    BlobFactStore.createWithIndex
        (InMemoryBlobStorage.InMemoryBlobStorage())
        (InMemoryEventStore.InMemoryEventStore())
        registry
        clock
        FactSurfaceOptions.disabled
        FactIndexOptions.disabled,
    newScope ()

let private blobIndexed (registry: IMetricRegistry option) (clock: unit -> DateTime) : IFactStore * string =
    BlobFactStore.createWithIndex
        (InMemoryBlobStorage.InMemoryBlobStorage())
        (InMemoryEventStore.InMemoryEventStore())
        registry
        clock
        FactSurfaceOptions.always
        FactIndexOptions.always,
    newScope ()

// ─── Always run ──────────────────────────────────────────────────────

let private composed (replicaCount: int) (knobs: ServerApp -> ServerApp) : ServiceCollection * ServiceProvider =
    let app =
        {
            ServerApp.empty with
                Config = {
                    ServerConfig.defaults with
                        FactStore = EnabledFactStore
                        ReplicaCount = replicaCount
                }
        }
        |> knobs

    let services = ServiceCollection()

    services.AddSingleton<IBlobStorage>(InMemoryBlobStorage.InMemoryBlobStorage())
    |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    services.AddSingleton<IScopeEnumerator>(ScopeEnumeration.ofScopes "test" [ "team-scale" ])
    |> ignore

    match app.Extensions.ServiceConfig with
    | Some cfg -> cfg services |> ignore
    | None -> ()

    services, services.BuildServiceProvider()

let private scaleValidators (sp: ServiceProvider) =
    sp.GetServices<ConfigValidation.IConfigValidator>()
    |> Seq.filter (fun v -> v.Name = "blob-fact-store-scale")
    |> List.ofSeq

/// Write `n` fact blobs the way `BlobFactStore` lays them out — the census
/// is what the guard counts, so the blobs need not be facts.
let private seedCensus (storage: IBlobStorage) (scope: string) (n: int) = async {
    for i in 1..n do
        let! _ = storage.Upload(scope, sprintf "%sfact-%06d.json" BlobFactStoreScale.FactsPrefix i, [| 0uy |])
        ()
}

let offlineTests =
    testList "Phase 888 — PostgresFactStore (no database)" [

        testCase "the default options validate, and an unsafe table name is refused by name"
        <| fun _ ->
            Expect.isEmpty (PostgresFactStoreOptions.validate PostgresFactStoreOptions.defaults) "defaults are valid"

            let problems =
                PostgresFactStoreOptions.validate {
                    PostgresFactStoreOptions.defaults with
                        Table = "facts; DROP TABLE x"
                        MaxWriteAttempts = 0
                        CommandTimeoutSeconds = -1
                }

            Expect.hasLength problems 3 "three problems, each named"

            Expect.isFalse
                (PostgresFactStoreOptions.isSafeIdentifier (
                    String('a', PostgresFactStoreOptions.MaxTableNameLength + 1)
                ))
                "a name whose index names PostgreSQL would truncate is refused"

        testCase "every row statement binds the scope (GP 4)"
        <| fun _ ->
            let statements = Sql.scopeBoundStatements "toolup_facts"
            Expect.isGreaterThanOrEqual (List.length statements) 8 "every read and write shape is enumerated"

            for statement in statements do
                Expect.stringContains statement "scope = @scope" "the statement binds the scope"

            Expect.stringStarts
                (Sql.copyIn "toolup_facts")
                "COPY toolup_facts (scope,"
                "the COPY writes the scope into every row"

        testCase "the scale verdict is silent at one replica and below the warning threshold"
        <| fun _ ->
            Expect.equal
                (BlobFactStoreScale.verdict 1 10 20 [ "a", 1_000_000 ])
                ConfigValidation.ValidationResult.Ok
                "one replica is never refused"

            Expect.equal
                (BlobFactStoreScale.verdict 3 10 20 [ "a", 10; "b", 4 ])
                ConfigValidation.ValidationResult.Ok
                "at the threshold"

            Expect.equal (BlobFactStoreScale.verdict 3 10 20 []) ConfigValidation.ValidationResult.Ok "no scopes"

        testCase "the scale verdict warns first, then refuses, naming the largest scope and the companion"
        <| fun _ ->
            match BlobFactStoreScale.verdict 2 10 20 [ "small", 3; "big", 15 ] with
            | ConfigValidation.ValidationResult.Warning message ->
                Expect.stringContains message "'big'" "the largest scope is named"
                Expect.stringContains message BlobFactStoreScale.Remedy "the remedy is named"
            | other -> failtestf "expected a warning, got %A" other

            match BlobFactStoreScale.verdict 2 10 20 [ "small", 3; "big", 21 ] with
            | ConfigValidation.ValidationResult.Error message ->
                Expect.stringContains message "'big'" "the largest scope is named"
                Expect.stringContains message BlobFactStoreScale.Remedy "the remedy is named"
            | other -> failtestf "expected a refusal, got %A" other

        testCaseAsync "the guard counts the blob census, and stands down for a replacement store"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            do! seedCensus storage "team-a" 12
            do! seedCensus storage "team-b" 3
            let scopes () = async { return [ "team-a"; "team-b" ] }

            let validate (v: ConfigValidation.IConfigValidator) = v.Validate()

            let! refused = validate (BlobFactStoreScaleValidator(2, storage, scopes, true, 5, 10))

            match refused with
            | ConfigValidation.ValidationResult.Error message ->
                Expect.stringContains message "holds 12 facts" "the census count is reported"
            | other -> failtestf "expected a refusal, got %A" other

            let! warned = validate (BlobFactStoreScaleValidator(2, storage, scopes, true, 5, 20))

            match warned with
            | ConfigValidation.ValidationResult.Warning _ -> ()
            | other -> failtestf "expected a warning, got %A" other

            let! replaced = validate (BlobFactStoreScaleValidator(2, storage, scopes, false, 5, 10))

            Expect.equal
                replaced
                ConfigValidation.ValidationResult.Ok
                "a replacement store is not the blob store's problem"

            let! single = validate (BlobFactStoreScaleValidator(1, storage, scopes, true, 5, 10))
            Expect.equal single ConfigValidation.ValidationResult.Ok "one replica"

            let! stated = validate (BlobFactStoreScaleValidator(2, storage, scopes, true))
            Expect.equal stated ConfigValidation.ValidationResult.Ok "twelve facts is far below the stated thresholds"
        }

        testCase "withFactStore registers the guard only when more than one replica is configured"
        <| fun _ ->
            let _, single = composed 1 FactsCompose.withFactStore
            Expect.isEmpty (scaleValidators single) "one replica composes exactly as before (GP 11)"

            let _, many = composed 3 FactsCompose.withFactStore
            Expect.hasLength (scaleValidators many) 1 "three replicas compose the guard"

        testCaseAsync "withFactStoreImplementation replaces the store every fact registration resolves"
        <| async {
            let replacement, _ = blobIndexed None (fun () -> DateTime.UtcNow)

            let services, sp =
                composed
                    3
                    (FactsCompose.withFactStore
                     >> FactsCompose.withFactStoreImplementation "test" (fun _ -> replacement))

            Expect.isTrue
                (obj.ReferenceEquals(sp.GetRequiredService<IFactStore>(), replacement))
                "the composed IFactStore is the replacement"

            Expect.hasLength
                (services
                 |> Seq.filter (fun d -> d.ServiceType = typeof<IFactStore>)
                 |> List.ofSeq)
                1
                "the blob registration is gone, not merely shadowed"

            // The guard is still registered (three replicas) and stands down.
            let storage = sp.GetRequiredService<IBlobStorage>()
            do! seedCensus storage "team-scale" 10

            match scaleValidators sp with
            | [ guard ] ->
                let! verdict = guard.Validate()
                Expect.equal verdict ConfigValidation.ValidationResult.Ok "the replacement is not the blob store"
            | other -> failtestf "expected one guard, got %d" other.Length
        }

        testCase "withFactStoreImplementation under NoFactStore changes nothing"
        <| fun _ ->
            let app = {
                ServerApp.empty with
                    Config = {
                        ServerConfig.defaults with
                            FactStore = NoFactStore
                    }
            }

            let after =
                FactsCompose.withFactStoreImplementation "test" (fun _ -> failwith "never built") app

            Expect.isTrue
                (obj.ReferenceEquals(app.Extensions.ServiceConfig, after.Extensions.ServiceConfig))
                "no registration"

        // The differential pack, self-bound: the enumerating blob path held
        // to the indexed-and-surfaced one. Proves the pack on every run.
        IFactStoreContract.differentialTests "BlobFactStore (index + surface)" blobReference blobIndexed
    ]

// ─── Live ────────────────────────────────────────────────────────────

[<Literal>]
let private TestTable = "toolup_facts_test"

let private liveOptions = {
    PostgresFactStoreOptions.defaults with
        Table = TestTable
}

/// The rows a plan touched: every scan node's rows returned plus the rows
/// its filter or recheck discarded, times its loops — and whether any node
/// was a sequential scan.
let private touchedRows (planJson: string) : int64 * bool =
    use doc = JsonDocument.Parse planJson
    let mutable touched = 0L
    let mutable sequential = false

    let number (node: JsonElement) (name: string) =
        match node.TryGetProperty name with
        | true, v -> v.GetInt64()
        | _ -> 0L

    let rec walk (node: JsonElement) =
        let nodeType = node.GetProperty("Node Type").GetString()

        if nodeType.Contains "Scan" then
            if nodeType = "Seq Scan" then
                sequential <- true

            let loops = max 1L (number node "Actual Loops")

            touched <-
                touched
                + loops
                  * (number node "Actual Rows"
                     + number node "Rows Removed by Filter"
                     + number node "Rows Removed by Index Recheck")

        match node.TryGetProperty "Plans" with
        | true, plans ->
            for child in plans.EnumerateArray() do
                walk child
        | _ -> ()

    for entry in doc.RootElement.EnumerateArray() do
        walk (entry.GetProperty "Plan")

    touched, sequential

[<Literal>]
let private ScaleSubjects = 300_000

let private liveTests (conn: string) =
    let dataSource = NpgsqlDataSource.Create conn

    let events () = InMemoryEventStore.InMemoryEventStore()

    let store (registry: IMetricRegistry option) (clock: unit -> DateTime) =
        PostgresFactStore.createWithDataSource dataSource liveOptions (events ()) registry clock

    let factory () : IFactStore * string * string =
        store None (fun () -> DateTime.UtcNow) :> IFactStore, newScope (), newScope ()

    let registryFactory (registry: IMetricRegistry) : IFactStore * string * string =
        store (Some registry) (fun () -> DateTime.UtcNow) :> IFactStore, newScope (), newScope ()

    let candidate (registry: IMetricRegistry option) (clock: unit -> DateTime) : IFactStore * string =
        store registry clock :> IFactStore, newScope ()

    let execute (sql: string) = async {
        use cmd = dataSource.CreateCommand sql
        let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
        ()
    }

    testList "Phase 888 — PostgresFactStore (live)" [

        // BOTH contract packs, unmodified — the same packs BlobFactStore binds.
        IFactStoreContract.tests "PostgresFactStore" factory
        IFactStoreContract.populationRegistryTests "PostgresFactStore" registryFactory

        // One seeded fact base, every query shape, value for value.
        IFactStoreContract.differentialTests "PostgresFactStore" blobReference candidate

        testList "PostgresFactStore specifics" [

            testCaseAsync "audit rows match BlobFactStore's shape: per fact on the scalar path, one per batch"
            <| async {
                let log = InMemoryEventStore.InMemoryEventStore()

                let s =
                    PostgresFactStore.createWithDataSource dataSource liveOptions log None (fun () -> DateTime.UtcNow)
                    :> IFactStore

                let scope = newScope ()

                let! _ = s.Assert(scope, draftFor "uk" "revenue" "h1" 1m)
                let! _ = s.Assert(scope, draftFor "uk" "revenue" "h2" 2m)
                let! _ = s.Assert(scope, draftFor "uk" "revenue" "h2" 2m)

                let! _ = s.AssertBatch(scope, [ draftFor "fr" "revenue" "f1" 3m; draftFor "de" "revenue" "d1" 4m ])

                let! rows = (log :> IEventStore).ReadBySource(scope, FactEvents.SourceModule)
                let kinds = rows |> List.map _.EventType |> List.countBy id |> Map.ofList

                Expect.equal (kinds |> Map.tryFind FactEvents.AssertedType) (Some 2) "one FactAsserted per scalar write"

                Expect.equal
                    (kinds |> Map.tryFind FactEvents.SupersededType)
                    (Some 1)
                    "one FactSuperseded for the supersession"

                Expect.equal
                    (kinds |> Map.tryFind FactEvents.BatchAssertedType)
                    (Some 1)
                    "one summarised row for the batch"

                Expect.equal rows.Length 4 "and the idempotent replay audited nothing"
            }

            testCaseAsync "concurrent writers racing one lineage leave one linear chain and one head"
            <| async {
                let scope = newScope ()

                let writers = [
                    for i in 1..8 ->
                        async {
                            let s = store None (fun () -> DateTime.UtcNow) :> IFactStore
                            return! s.Assert(scope, draftFor "uk" "revenue" (sprintf "race-%d" i) (decimal i))
                        }
                ]

                let! results = Async.Parallel writers

                for r in results do
                    match r with
                    | Ok _ -> ()
                    | Error e -> failtestf "a racing writer failed instead of re-deriving: %s" e

                let s = store None (fun () -> DateTime.UtcNow) :> IFactStore

                let! heads =
                    s.Query(
                        scope,
                        FactQuery.forSubjectMetric
                            {
                                Hierarchy = "geography"
                                Path = [ "uk" ]
                            }
                            (MetricRef "revenue")
                    )

                Expect.hasLength heads 1 "one current head"

                let! chain = s.QuerySupersessionChain(scope, heads.Head.FactId)
                Expect.hasLength chain 8 "every writer's fact is in the lineage"

                let edges =
                    chain |> List.pairwise |> List.map (fun (a, b) -> b.Supersedes = Some a.FactId)

                Expect.allEqual edges true "the chain is linear: each fact supersedes exactly its predecessor"
                Expect.isNone chain.Head.Supersedes "and it starts at a first fact"
            }

            testCaseAsync "a future-dated head is not yet visible; its predecessor is, from the same table"
            <| async {
                // The Phase 702 case: the head's transaction time is ahead
                // of the clock that reads it (a skewed or coarse clock).
                // There is no separate read model for that head to be
                // missing from, so the read simply answers "as of now".
                let scope = newScope ()
                let base' = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
                let mutable now = base'
                let s = store None (fun () -> now) :> IFactStore

                let! first = s.Assert(scope, draftFor "uk" "revenue" "f1" 1m)
                now <- base'.AddDays 10.0
                let! second = s.Assert(scope, draftFor "uk" "revenue" "f2" 2m)
                now <- base'.AddDays 1.0

                match first, second with
                | Ok f1, Ok f2 ->
                    Expect.equal f2.Supersedes (Some f1.FactId) "the second supersedes the first"

                    let! current = s.Query(scope, FactQuery.forSubjectMetric f1.Subject f1.Metric)

                    Expect.equal
                        (current |> List.map _.FactId)
                        [ f1.FactId ]
                        "at the clock's now the head is not yet visible, and its predecessor is"

                    let! population =
                        s.QueryPopulation(
                            scope,
                            {
                                PopulationQuery.create (MetricRef "revenue") "geography" with
                                    Ordering = Descending
                            }
                        )

                    match population with
                    | Ok p -> Expect.equal (p.Ranked |> List.map _.FactId) [ f1.FactId ] "the population read agrees"
                    | Error e -> failtestf "population refused: %s" e

                    let! atSecond =
                        s.Query(scope, FactQuery.forSubjectMetric f1.Subject f1.Metric |> FactQuery.asOf f2.AsOf)

                    Expect.equal (atSecond |> List.map _.FactId) [ f2.FactId ] "as of the second, the second"
                | other -> failtestf "seed failed: %A" other
            }

            testCase "VerifyOnly refuses a missing table by name, before any request"
            <| fun _ ->
                let ex =
                    Expect.throwsC
                        (fun () ->
                            PostgresFactStore.createWithDataSource
                                dataSource
                                {
                                    liveOptions with
                                        Table = "toolup_facts_absent"
                                        SchemaMode = VerifyOnly
                                }
                                (events ())
                                None
                                (fun () -> DateTime.UtcNow)
                            |> ignore)
                        id

                let message =
                    match ex with
                    | PostgresFactStoreException m -> m
                    | other -> other.Message

                Expect.stringContains message "toolup_facts_absent" "the missing table is named"

            testCaseAsync "a point read at 300,000 subjects touches a bounded number of rows (recorded)"
            <| async {
                let scope = "scale-" + Guid.NewGuid().ToString("N").Substring(0, 12)
                let s = store None (fun () -> DateTime.UtcNow)
                let facts = s :> IFactStore
                let chunk = 20_000

                for start in 0..chunk .. (ScaleSubjects - 1) do
                    let drafts = [
                        for i in start .. (min (start + chunk) ScaleSubjects) - 1 ->
                            draftFor (sprintf "s%06d" i) "load" (sprintf "h%06d" i) (decimal (i % 1000))
                    ]

                    let! r = facts.AssertBatch(scope, drafts)

                    match r with
                    | Ok receipt -> Expect.equal receipt.AssertedCount drafts.Length "the chunk was written"
                    | Error e -> failtestf "seed chunk at %d failed: %s" start e

                // A supersession inside the population, so the read has
                // history to skip.
                let target = {
                    Hierarchy = "geography"
                    Path = [ "s150000" ]
                }

                let! _ = facts.Assert(scope, draftFor "s150000" "load" "h150000-v2" 7m)
                do! execute (sprintf "ANALYZE %s" TestTable)

                let query = FactQuery.forSubjectMetric target (MetricRef "load")
                let! plan = s.ExplainQuery(scope, query)
                let touched, sequential = touchedRows plan
                let! answer = facts.Query(scope, query)

                printfn
                    "Phase 888 scale: %d subjects in one scope — a subject-and-metric point read touched %d rows (%s)"
                    ScaleSubjects
                    touched
                    (if sequential then
                         "a sequential scan appeared"
                     else
                         "index scans only")

                Expect.equal (answer |> List.map _.Value) [ Scalar 7m ] "the point read answers the current head"
                Expect.isFalse sequential "no sequential scan over the scope"
                // The lineage's two facts, visited by the heads branch and the
                // successor probe — a handful, whatever the scope's size.
                Expect.isLessThanOrEqual touched 10L "the rows touched are the lineage's, not the scope's"
            }
        ]
    ]

[<Tests>]
let tests =
    match Environment.GetEnvironmentVariable "TOOLUP_TEST_POSTGRES" with
    | null
    | "" ->
        testList "Phase 888 — PostgresFactStore (live)" [
            ptestCase "skipped — TOOLUP_TEST_POSTGRES not set" <| fun _ -> ()
        ]
    | conn -> liveTests conn