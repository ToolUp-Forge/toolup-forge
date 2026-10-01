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

// ─── Phase 941 — migrating a BlobFactStore into the table ────────────
//
// The source is a `BlobFactStore` seeded with supersession chains (three
// versions of most lineages), competing methods and a second metric —
// 620 facts, small on purpose. The live cases pin why the copy cannot go
// through `Assert` (red first), then show the raw import answering every
// AsOf read as the source does, resuming after an interruption to the same
// rows as an uninterrupted run, replaying a page as a no-op, and a
// verification that names each planted difference.

// The source's transaction-time clock: a minute per call, from 2026-08-01.
let private migrationClock () : unit -> DateTime =
    let current = ref (DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc))

    fun () ->
        let value = current.Value
        current.Value <- value.AddMinutes 1.0
        value

let private blobSource () : BlobFactStore * IBlobStorage =
    let storage = InMemoryBlobStorage.InMemoryBlobStorage()

    BlobFactStore(
        storage,
        InMemoryEventStore.InMemoryEventStore(),
        None,
        migrationClock (),
        FactSurfaceOptions.disabled,
        FactIndexOptions.disabled
    ),
    storage :> IBlobStorage

let private migrationSeed (store: IFactStore) (scope: string) = async {
    for round in 1..3 do
        let drafts = [
            for i in 1..200 do
                if round < 3 || i % 2 = 0 then
                    draftFor (sprintf "m%03d" i) "revenue" (sprintf "r%03d-%d" i round) (decimal (i * round))
        ]

        match! store.AssertBatch(scope, drafts) with
        | Ok _ -> ()
        | Error e -> failtestf "seed round %d refused: %s" round e

    let competing = [
        for i in 1..20 ->
            {
                draftFor (sprintf "m%03d" i) "revenue" (sprintf "e%03d" i) 1m with
                    Method = Computed("estimator", "1", "p0")
            }
    ]

    let visits = [
        for i in 1..100 -> draftFor (sprintf "v%03d" i) "visits" (sprintf "v%03d" i) (decimal i)
    ]

    for batch in [ competing; visits ] do
        match! store.AssertBatch(scope, batch) with
        | Ok _ -> ()
        | Error e -> failtestf "seed refused: %s" e
}

[<Literal>]
let private MigrationSeedFacts = 620

let private draftOf (f: Fact) : FactDraft = {
    Subject = f.Subject
    Metric = f.Metric
    Value = f.Value
    Period = f.Period
    Method = f.Method
    Evidence = f.Evidence
    Confidence = f.Confidence
    Disclosure = f.Disclosure
}

let private byTransactionTime (facts: Fact list) =
    facts
    |> List.sortWith (fun a b ->
        match compare a.AsOf.Ticks b.AsOf.Ticks with
        | 0 -> String.CompareOrdinal(a.FactId, b.FactId)
        | c -> c)

let private visibleIds (store: IFactStore) (scope: string) (t: DateTime) = async {
    let! facts = store.Query(scope, { FactQuery.all with AsOf = Some t })
    return facts |> List.map _.FactId |> List.sort
}

let private migrationEvents (log: IEventStore) (scope: string) = async {
    let! rows = log.ReadBySource(scope, FactEvents.SourceModule)
    return rows |> List.map _.EventType
}

let private migrationOfflineTests =
    testList "Phase 941 — FactStoreMigration (no database)" [

        testCase "the options are validated, each problem named"
        <| fun _ ->
            Expect.isEmpty (FactStoreMigrationOptions.validate FactStoreMigrationOptions.defaults) "defaults are usable"

            let problems =
                FactStoreMigrationOptions.validate {
                    FactStoreMigrationOptions.defaults with
                        PageSize = 0
                        AsOfSamples = -1
                        SourceName = " "
                }

            Expect.hasLength problems 3 "three problems, each named"

        testCase "every migration statement that names a scope's rows binds the scope (GP 4)"
        <| fun _ ->
            for statement in MigrationSql.scopeBoundStatements "toolup_facts" do
                Expect.stringContains statement "@scope" "the statement binds the scope"

            Expect.stringContains
                (MigrationSql.insertStaged "toolup_facts")
                "ON CONFLICT (scope, fact_id) DO NOTHING"
                "a replayed row is a no-op by the content-address key"

        testCaseAsync "the export reads every fact blob and names each one it cannot use"
        <| async {
            let source, storage = blobSource ()
            let scope = newScope ()
            do! migrationSeed source scope

            let good = draftFor "x" "revenue" "x1" 1m
            let! written = (source :> IFactStore).Assert(scope, good)

            let fact =
                match written with
                | Ok f -> f
                | Error e -> failtestf "assert refused: %s" e

            // One blob that is not JSON, and one that holds a fact under
            // another fact's name.
            let! _ = storage.Upload(scope, "_facts/garbage.json", Encoding.UTF8.GetBytes "{ not json")
            let! bytes = storage.Download(scope, sprintf "_facts/%s.json" fact.FactId)

            match bytes with
            | Ok b ->
                let! _ = storage.Upload(scope, "_facts/misnamed.json", b)
                ()
            | Error e -> failtestf "download failed: %s" e

            let! export = source.ExportScope scope
            Expect.hasLength export.Facts (MigrationSeedFacts + 1) "every readable fact"

            Expect.equal
                (export.Unreadable |> List.map fst |> List.sort)
                [ "_facts/garbage.json"; "_facts/misnamed.json" ]
                "both unusable blobs are named"

            let! listed =
                (source :> IFactStore)
                    .Query(
                        scope,
                        {
                            FactQuery.all with
                                IncludeSuperseded = true
                        }
                    )

            // Verify the probe: the query path really does skip the garbage
            // blob silently, which is why the export must not.
            Expect.equal
                listed.Length
                (MigrationSeedFacts + 2)
                "the query path lists the misnamed copy and drops the garbage"
        }

        testCaseAsync "the source check passes a valid fact base and names each fact of a broken one"
        <| async {
            let source, _ = blobSource ()
            let scope = newScope ()
            do! migrationSeed source scope
            let! export = source.ExportScope scope
            Expect.isEmpty (FactStoreMigration.validateSource export.Facts) "the seed is a valid fact base"

            let facts = byTransactionTime export.Facts
            let successor = facts |> List.find (fun f -> f.Supersedes.IsSome)
            let predecessor = facts |> List.find (fun f -> Some f.FactId = successor.Supersedes)

            // A fork: a second fact superseding the same predecessor.
            let fork = {
                successor with
                    FactId = "fork"
                    AsOf = successor.AsOf.AddTicks 1L
            }

            let forked = FactStoreMigration.validateSource (fork :: facts)

            let named =
                forked
                |> List.map (function
                    | InvalidSource(id, _) -> id
                    | _ -> "")

            Expect.contains named "fork" "the fork is named"
            Expect.contains named successor.FactId "the second head of the lineage is named"

            // A successor no later than its predecessor.
            let backwards =
                facts
                |> List.map (fun f ->
                    if f.FactId = successor.FactId then
                        { f with AsOf = predecessor.AsOf }
                    else
                        f)
                |> FactStoreMigration.validateSource

            Expect.equal
                (backwards
                 |> List.map (function
                     | InvalidSource(id, _) -> id
                     | _ -> ""))
                [ successor.FactId ]
                "the successor whose transaction time does not advance is named"
        }
    ]

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

        testCase "the scale verdict warns, then warns harder, naming the largest scope and the companion"
        <| fun _ ->
            match BlobFactStoreScale.verdict 2 10 20 [ "small", 3; "big", 15 ] with
            | ConfigValidation.ValidationResult.Warning message ->
                Expect.stringContains message "'big'" "the largest scope is named"
                Expect.stringContains message BlobFactStoreScale.Remedy "the remedy is named"
            | other -> failtestf "expected a warning, got %A" other

            // Warn-only in this release (operator decision, 2026-09-29):
            // past the upper threshold the guard still advises the move and
            // never refuses startup.
            match BlobFactStoreScale.verdict 2 10 20 [ "small", 3; "big", 21 ] with
            | ConfigValidation.ValidationResult.Warning message ->
                Expect.stringContains message "'big'" "the largest scope is named"
                Expect.stringContains message BlobFactStoreScale.Remedy "the remedy is named"
                Expect.stringContains message "20-fact upper threshold" "the upper threshold is named"
                Expect.stringContains message "does not refuse startup" "and says it does not refuse"
            | other -> failtestf "expected a warning, never a refusal, got %A" other

            match BlobFactStoreScale.verdict 2 10 20 [ "big", 2_000_000 ] with
            | ConfigValidation.ValidationResult.Error _ -> failtest "the guard never refuses startup in this release"
            | _ -> ()

        testCaseAsync "the guard counts the blob census, and stands down for a replacement store"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            do! seedCensus storage "team-a" 12
            do! seedCensus storage "team-b" 3
            let scopes () = async { return [ "team-a"; "team-b" ] }

            let validate (v: ConfigValidation.IConfigValidator) = v.Validate()

            let! aboveUpper = validate (BlobFactStoreScaleValidator(2, storage, scopes, true, 5, 10))

            match aboveUpper with
            | ConfigValidation.ValidationResult.Warning message ->
                Expect.stringContains message "holds 12 facts" "the census count is reported"
                Expect.stringContains message "upper threshold" "past the upper threshold, the stronger warning"
            | other -> failtestf "expected a warning (warn-only in this release), got %A" other

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

        // Phase 946 — the blob fact store's index check (Phase 890) reaches
        // /dev/inspect through the DI-registered inspector seam.
        testCaseAsync "the fact store's index check is one of /dev/inspect's inspectors (Phase 946)"
        <| async {
            let _, sp = composed 1 FactsCompose.withFactStore
            let inspectors = DevDiagnosticsHandler.indexInspectors [] sp

            Expect.hasLength inspectors 1 "the facts companion registers one inspector"

            let! entries = inspectors.Head "team-scale"

            Expect.equal
                (entries |> List.map _.StoreName |> List.distinct)
                [ "facts" ]
                "it samples the fact store's index"

            // A replaced store is not the blob store: nothing to sample.
            let replacement, _ = blobIndexed None (fun () -> DateTime.UtcNow)

            let _, replaced =
                composed
                    1
                    (FactsCompose.withFactStore
                     >> FactsCompose.withFactStoreImplementation "test" (fun _ -> replacement))

            match DevDiagnosticsHandler.indexInspectors [] replaced with
            | [ inspector ] ->
                let! none = inspector "team-scale"
                Expect.isEmpty none "the replacement's index is not this inspector's"
            | other -> failtestf "expected the one registered inspector, got %d" other.Length
        }

        // Phase 946 — the compose-time preflight reads every validator as
        // an INSTANCE registration and refuses a factory, so a guard
        // registered through a factory made a multi-replica composition
        // raise at preflight instead of validating.
        testCaseAsync "a multi-replica composition's preflight runs the guard instead of raising (Phase 946)"
        <| async {
            let services, sp = composed 3 FactsCompose.withFactStore

            let outcomes =
                try
                    Ok(ConfigValidatorAggregator.validate services None false)
                with ex ->
                    Error ex.Message

            match outcomes with
            | Error message -> failtestf "the preflight raised instead of validating: %s" message
            | Ok outcomes ->
                Expect.contains (outcomes |> List.map _.Name) "blob-fact-store-scale" "the guard ran at preflight"

            // The guard reads the composition it was registered into: past
            // the warning threshold it warns, at preflight, with no provider.
            do! seedCensus (sp.GetRequiredService<IBlobStorage>()) "team-scale" (BlobFactStoreScale.WarnAboveFacts + 1)

            match
                ConfigValidatorAggregator.validate services None false
                |> List.tryFind (fun o -> o.Name = "blob-fact-store-scale")
            with
            | Some {
                       Result = ConfigValidation.ValidationResult.Warning message
                   } -> Expect.stringContains message "team-scale" "the warning names the scope"
            | other -> failtestf "expected the guard to warn, got %A" other
        }

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
        //
        // Phase 946 — timed against Phase 762's slow-lane rule (a list
        // costing >= 10 s whose slowest case is >= 2 s) and LEFT in every
        // lane: five runs (Debug, a loaded shared machine, 2026-10-01) cost
        // 10.6 s cold, then 7.1, 7.6, 7.4 and 6.8 s over its 4 cases,
        // slowest case 3.5-6.7 s. It meets the per-case clause and misses
        // the list clause (median 7.4 s). Re-time before moving it.
        IFactStoreContract.differentialTests "BlobFactStore (index + surface)" blobReference blobIndexed

        // Phase 941 — the migration's option, statement, export and
        // source-law checks.
        migrationOfflineTests
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
/// was a sequential scan. A scan of a sub-select's output, a function's or
/// a CTE's reads no table row, so it is not counted (Phase 962: the
/// population summary's UNION branches plan as sub-query scans).
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

        let readsTable =
            nodeType.Contains "Scan"
            && not (List.contains nodeType [ "Subquery Scan"; "Function Scan"; "CTE Scan" ])

        if readsTable then
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

/// Phase 962 — every scan node of an executed plan as (node type, index
/// name), so a case can say WHICH index answered and whether it was
/// index-only.
let private scanNodes (planJson: string) : (string * string) list =
    use doc = JsonDocument.Parse planJson
    let found = ResizeArray<string * string>()

    let rec walk (node: JsonElement) =
        let nodeType = node.GetProperty("Node Type").GetString()

        if nodeType.Contains "Scan" then
            let index =
                match node.TryGetProperty "Index Name" with
                | true, v -> v.GetString()
                | _ -> ""

            found.Add((nodeType, index))

        match node.TryGetProperty "Plans" with
        | true, plans ->
            for child in plans.EnumerateArray() do
                walk child
        | _ -> ()

    for entry in doc.RootElement.EnumerateArray() do
        walk (entry.GetProperty "Plan")

    List.ofSeq found

/// Phase 962 — the population index cases: subjects x weekly periods of
/// one metric, so the latest week is one 52nd of the scope's history.
[<Literal>]
let private HistorySubjects = 400

[<Literal>]
let private HistoryWeeks = 52

let private historyWeek (w: int) : TemporalExtent = {
    From = DateTime(2025, 1, 6, 0, 0, 0, DateTimeKind.Utc).AddDays(7.0 * float w)
    To = DateTime(2025, 1, 6, 0, 0, 0, DateTimeKind.Utc).AddDays(7.0 * float (w + 1))
    Label = Some(sprintf "W%02d" (w + 1))
}

let private historyDraft (subject: int) (w: int) (tag: string) : FactDraft = {
    (draftFor (sprintf "h%04d" subject) "history" tag (decimal ((subject * 7 + w) % 1000))) with
        Period = historyWeek w
}

[<Literal>]
let private ScaleSubjects = 300_000

// ─── Phase 940 — the aggregated population read ──────────────────────
//
// The population read now answers its statistics and its top k in the
// database, and falls back to the member read when that answer would not
// be the shared pipeline's. The differential pack holds both paths to the
// blob store over its seed; these cases add the shapes that seed does not
// reach — a `FreshFor` staleness policy (the histogram restated as SQL),
// subjects with several members (the exact distinct-subject count behind
// the hash count), a canonical selector over a single-method population
// (the threshold pushed down after the in-snapshot method probe) and over
// a contested one (the member read), and a sum whose left-to-right
// `decimal` fold rounds (the member read, where the fold rounds).

let private steppedClock () : unit -> DateTime =
    let current = ref (DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc))

    fun () ->
        let value = current.Value
        current.Value <- value.AddMinutes 1.0
        value

let private aggregateMetric (id: string) (staleness: StalenessPolicy) (canonical: string option) : MetricDefinition = {
    Id = id
    Name = id
    Unit = "count"
    Dimensionality = "count"
    Direction = HigherIsBetter
    DisplayFormat = "N2"
    Staleness = staleness
    ProducingOperation = None
    CanonicalMethod = canonical
    RecomputePolicy = None
    RollUp = None
    Context = None
}

let private aggregateRegistry: IMetricRegistry =
    MetricRegistry.build [
        for definition in
            [
                aggregateMetric "footfall" (FreshFor(TimeSpan.FromMinutes 30.0)) None
                aggregateMetric "yield" UntilSuperseded (Some "computed:rollup")
                aggregateMetric "share" (FreshFor(TimeSpan.FromMinutes 45.0)) (Some "computed:rollup")
                aggregateMetric "precise" UntilSuperseded None
            ] do
            ({
                Module = "test"
                Definition = definition
            }
            : MetricRegistration)
    ] []

let private q3: TemporalExtent = {
    From = q2.To
    To = q2.To.AddMonths 3
    Label = Some "Q3-2026"
}

let private aggregateDraft
    (path: string list)
    (metric: string)
    (period: TemporalExtent)
    (method': MethodRef)
    (value: FactValue)
    (inputHash: string)
    : FactDraft =
    {
        (draftFor "unused" metric inputHash 0m) with
            Subject = { Hierarchy = "geography"; Path = path }
            Value = value
            Period = period
            Method = method'
    }

let private rollup = Computed("rollup", "1", "p0")
let private estimator = Computed("estimator", "1", "p0")

/// Asserted one at a time, so both stores read the same clock sequence.
let private aggregateSeed: FactDraft list = [
    for i in 0..23 do
        let path = [ "north"; sprintf "s%02d" i ]
        let value = decimal i * 1.5m
        aggregateDraft path "footfall" q2 rollup (Scalar value) (sprintf "f%d" i)

        if i % 3 = 0 then
            aggregateDraft path "footfall" q3 rollup (Scalar(value + 0.25m)) (sprintf "f%d-q3" i)

        if i % 5 = 0 then
            aggregateDraft path "footfall" q2 estimator (Scalar(value * 2m)) (sprintf "f%d-est" i)

        aggregateDraft path "yield" q2 rollup (Scalar(decimal (i % 7))) (sprintf "y%d" i)
        aggregateDraft path "share" q2 rollup (Scalar(decimal i / 4m)) (sprintf "s%d" i)

    // Equal values at two scales, a non-comparable member, supersessions.
    aggregateDraft [ "north"; "tie-a" ] "footfall" q2 rollup (Scalar 12.0m) "tie-a"
    aggregateDraft [ "north"; "tie-b" ] "footfall" q2 rollup (Scalar 12.00m) "tie-b"
    aggregateDraft [ "north"; "gap" ] "footfall" q2 rollup (Absent "not measured") "gap"
    aggregateDraft [ "north"; "s04" ] "footfall" q2 rollup (Scalar 40m) "f4-v2"
    aggregateDraft [ "north"; "s07" ] "yield" q2 rollup (Scalar 9m) "y7-v2"
    // The one competitor that makes `share` a contested population.
    aggregateDraft [ "north"; "s02" ] "share" q2 estimator (Scalar 99m) "s2-est"
    // Twenty copies of a value whose left-to-right decimal sum rounds
    // differently from the exact sum (20.000...001 against 20.000...002),
    // far enough that the two means differ too (1 against 1.000...001).
    for i in 0..19 do
        aggregateDraft
            [ "north"; sprintf "p%02d" i ]
            "precise"
            q2
            rollup
            (Scalar 1.0000000000000000000000000001m)
            (sprintf "p%d" i)
    aggregateDraft [ "north"; "s09" ] "footfall" q2 rollup (Scalar 3m) "f9-v2"
]

let private aggregateQueries (instants: DateTime list) : (string * PopulationQuery) list = [
    for metric in [ "footfall"; "yield"; "share"; "precise" ] do
        for ordering in [ Descending; Ascending ] do
            for threshold in [ None; Some(AtLeast 5m); Some(Between(3m, 12m)) ] do
                for methods in [ CanonicalMethodOnly; AllCompetingMethods ] do
                    for period in [ None; Some q2 ] do
                        for topK in [ 3; 50 ] do
                            for asOf in None :: (instants |> List.map Some) do
                                let query = {
                                    PopulationQuery.create (MetricRef metric) "geography" with
                                        Ordering = ordering
                                        Threshold = threshold
                                        Methods = methods
                                        PeriodOverlaps = period
                                        TopK = topK
                                        Level = Some 2
                                        AsOf = asOf
                                }

                                sprintf
                                    "%s %A %A %A %A top-%d as of %A"
                                    metric
                                    ordering
                                    threshold
                                    methods
                                    period
                                    topK
                                    asOf,
                                query
]

let private aggregateTests
    (reference: IMetricRegistry option -> (unit -> DateTime) -> IFactStore * string)
    (candidate: IMetricRegistry option -> (unit -> DateTime) -> IFactStore * string)
    =
    testCaseAsync "Phase 940 — every aggregated population shape answers as the blob store does"
    <| async {
        let expectedStore, expectedScope =
            reference (Some aggregateRegistry) (steppedClock ())

        let actualStore, actualScope = candidate (Some aggregateRegistry) (steppedClock ())
        let written = ResizeArray<Fact>()

        for draft in aggregateSeed do
            let! expected = expectedStore.Assert(expectedScope, draft)
            let! actual = actualStore.Assert(actualScope, draft)
            Expect.equal actual expected "both stores wrote the same fact"

            match actual with
            | Ok f -> written.Add f
            | Error e -> failtestf "seed refused: %s" e

        // Instants inside the seed, so a replay sees earlier heads, a
        // superseded predecessor and a freshness window part-elapsed.
        let instants = [ written[20].AsOf; written[70].AsOf ]
        let mutable freshAndStale = false

        for label, query in aggregateQueries instants do
            let! expected = expectedStore.QueryPopulation(expectedScope, query)
            let! actual = actualStore.QueryPopulation(actualScope, query)
            Expect.equal actual expected (sprintf "population '%s' answers identically" label)

            match actual with
            | Ok r when r.Stats.Freshness.FreshCount > 0 && r.Stats.Freshness.StaleCount > 0 -> freshAndStale <- true
            | _ -> ()

        // Verify the probe: the freshness cases must have split the
        // population, or the histogram was never really tested.
        Expect.isTrue freshAndStale "some population was part fresh and part stale"

        let precise = {
            PopulationQuery.create (MetricRef "precise") "geography" with
                Ordering = Descending
        }

        match! actualStore.QueryPopulation(actualScope, precise) with
        | Ok r ->
            Expect.equal r.Stats.ComparableCount 20 "the twenty precise members"
            // The fold's rounded sum over twenty, not the exact one's.
            Expect.equal
                r.Stats.Mean
                (Some(20.000000000000000000000000001m / 20m))
                "the mean is the left-to-right decimal fold's"
        | Error e -> failtestf "precise refused: %s" e
    }

let private migrationLiveTests (dataSource: NpgsqlDataSource) =
    let targetOn (table: string) (events: IEventStore) =
        PostgresFactStore.createWithDataSource
            dataSource
            {
                PostgresFactStoreOptions.defaults with
                    Table = table
            }
            events
            None
            (fun () -> DateTime.UtcNow)

    let scalar (sql: string) (bind: NpgsqlCommand -> unit) = async {
        use cmd = dataSource.CreateCommand sql
        bind cmd
        let! v = cmd.ExecuteScalarAsync() |> Async.AwaitTask
        return v
    }

    let execute (sql: string) (bind: NpgsqlCommand -> unit) = async {
        use cmd = dataSource.CreateCommand sql
        bind cmd
        let! n = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
        return n
    }

    let rowsOf (table: string) (scope: string) = async {
        use cmd =
            dataSource.CreateCommand(
                sprintf
                    "SELECT fact_id, lineage_hash, as_of_ticks, coalesce(supersedes, ''), is_head, payload FROM %s WHERE scope = @scope ORDER BY fact_id"
                    table
            )

        cmd.Parameters.AddWithValue("scope", scope) |> ignore
        use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
        let rows = ResizeArray<string>()

        let rec loop () = async {
            let! more = reader.ReadAsync() |> Async.AwaitTask

            if more then
                rows.Add(
                    String.concat "|" [
                        reader.GetString 0
                        reader.GetString 1
                        string (reader.GetInt64 2)
                        reader.GetString 3
                        string (reader.GetBoolean 4)
                        reader.GetString 5
                    ]
                )

                return! loop ()
        }

        do! loop ()
        return List.ofSeq rows
    }

    let bindScope (scope: string) (cmd: NpgsqlCommand) =
        cmd.Parameters.AddWithValue("scope", scope) |> ignore

    let options = {
        FactStoreMigrationOptions.defaults with
            PageSize = 100
    }

    testList "Phase 941 — FactStoreMigration (live)" [

        testCaseAsync "a copy through Assert answers AsOf reads differently from the source (pinned red)"
        <| async {
            let source, _ = blobSource ()
            let scope = newScope ()
            do! migrationSeed source scope
            let! export = source.ExportScope scope

            // The Assert copy: the target's own clock, a month later.
            let later = ref (DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc))

            let asserted =
                PostgresFactStore.createWithDataSource
                    dataSource
                    liveOptions
                    (InMemoryEventStore.InMemoryEventStore())
                    None
                    (fun () ->
                        later.Value <- later.Value.AddMinutes 1.0
                        later.Value)

            for f in byTransactionTime export.Facts do
                match! (asserted :> IFactStore).Assert(scope, draftOf f) with
                | Ok copy -> Expect.equal copy.FactId f.FactId "the content address survives an Assert copy"
                | Error e -> failtestf "assert copy refused: %s" e

            // Between the first and second versions of every chain.
            let t = (byTransactionTime export.Facts |> List.item 199).AsOf
            let! onSource = visibleIds (source :> IFactStore) scope t
            let! onCopy = visibleIds (asserted :> IFactStore) scope t
            Expect.hasLength onSource 200 "the source knew the first versions at t"
            Expect.notEqual onCopy onSource "the Assert copy answers the AsOf read differently"

            let! report = FactStoreMigration.verify source asserted options [ scope ]
            Expect.isFalse report.Passed "the verification refuses the Assert copy"
            Expect.equal (FactStoreMigration.exitCode report) 1 "a non-zero exit"

            let columns =
                report.Scopes.Head.Differences
                |> List.choose (function
                    | ColumnDiffers(_, column, _, _) -> Some column
                    | _ -> None)
                |> List.distinct
                |> List.sort

            Expect.equal columns [ "as_of_ticks"; "payload" ] "every row's transaction time moved"
        }

        testCaseAsync "the migration writes the original rows, every AsOf read matches, and audit is one record"
        <| async {
            let source, _ = blobSource ()
            let scope = newScope ()
            do! migrationSeed source scope
            let log = InMemoryEventStore.InMemoryEventStore()
            let target = targetOn TestTable log

            let! report = FactStoreMigration.migrate source target log options [ scope ]
            let s = report.Scopes.Head
            Expect.equal s.Outcome Verified (FactStoreMigration.render report)
            Expect.isTrue report.Passed "the report passes"
            Expect.equal (FactStoreMigration.exitCode report) 0 "a zero exit"
            Expect.equal s.SourceFacts MigrationSeedFacts "every source fact"
            Expect.equal s.Inserted MigrationSeedFacts "every fact written once"
            Expect.equal s.TargetFacts MigrationSeedFacts "every row on the target"
            Expect.equal s.Lineages 320 "200 revenue, 20 estimator and 100 visits lineages"
            Expect.equal s.Heads 320 "one head per lineage"

            // Independently of the verification: every distinct transaction
            // time of the source, read on both sides.
            let! export = source.ExportScope scope

            for t in export.Facts |> List.map _.AsOf |> List.distinct do
                let! onSource = visibleIds (source :> IFactStore) scope t
                let! onTarget = visibleIds (target :> IFactStore) scope t
                Expect.equal onTarget onSource (sprintf "the AsOf read at %O matches" t)

            let! doubled =
                scalar
                    (sprintf
                        "SELECT count(*) FROM (SELECT lineage_hash FROM %s WHERE scope = @scope AND is_head GROUP BY lineage_hash HAVING count(*) > 1) d"
                        TestTable)
                    (bindScope scope)

            Expect.equal (Convert.ToInt64 doubled) 0L "one current head per lineage"

            let! kinds = migrationEvents log scope
            Expect.equal kinds [ FactStoreMigration.MigratedType ] "one migration record and no per-fact event"

            let! rows = (log :> IEventStore).ReadBySource(scope, FactEvents.SourceModule)

            let payload =
                JsonSerializer.Deserialize<FactStoreMigratedEvent>(
                    rows.Head.Payload,
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()
                )

            Expect.equal payload.Verification "verified" "the verification result is recorded"
            Expect.equal payload.SourceFacts MigrationSeedFacts "the counts are recorded"
            Expect.equal payload.Target ("postgres:" + TestTable) "the target is named"

            // A second run over the same source does nothing.
            let! again = FactStoreMigration.migrate source target log options [ scope ]
            Expect.equal again.Scopes.Head.Outcome AlreadyVerified "a verified scope is skipped"
            let! kinds = migrationEvents log scope
            Expect.hasLength kinds 1 "a skipped scope adds no audit record"
        }

        testCaseAsync "an interrupted migration resumes to the uninterrupted result; a replayed page writes nothing"
        <| async {
            let source, _ = blobSource ()
            let scope = newScope ()
            do! migrationSeed source scope
            let log = InMemoryEventStore.InMemoryEventStore()
            let whole = targetOn "toolup_facts_migration_whole" log
            let resumed = targetOn TestTable log

            let! uninterrupted = FactStoreMigration.migrate source whole log options [ scope ]
            Expect.isTrue uninterrupted.Passed "the uninterrupted run verifies"

            // Interrupt the second run on its last page: a planted row
            // already heads the lineage of the last fact written, so that
            // page's insert fails and rolls back.
            let! export = source.ExportScope scope
            let last = byTransactionTime export.Facts |> List.last

            let! lineage =
                scalar
                    "SELECT lineage_hash FROM toolup_facts_migration_whole WHERE scope = @scope AND fact_id = @id"
                    (fun cmd ->
                        bindScope scope cmd
                        cmd.Parameters.AddWithValue("id", last.FactId) |> ignore)

            let plant =
                sprintf
                    "INSERT INTO %s (scope, fact_id, hierarchy, path, metric, period_from_ticks, period_to_ticks, method_identity, lineage_hash, as_of_ticks, as_of, supersedes, is_head, magnitude, payload) VALUES (@scope, 'planted', 'geography', ARRAY['planted'], 'revenue', 0, 0, 'planted', @lineage, 0, now(), NULL, true, NULL, '{}')"
                    TestTable

            let! _ =
                execute plant (fun cmd ->
                    bindScope scope cmd
                    cmd.Parameters.AddWithValue("lineage", string lineage) |> ignore)

            let! interrupted = FactStoreMigration.migrate source resumed log options [ scope ]
            let first = interrupted.Scopes.Head

            match first.Outcome with
            | CopyFailed _ -> ()
            | other -> failtestf "the interrupted run should fail its last page, not %A" other

            Expect.equal first.Inserted 600 "six pages committed before the interruption"

            let! _ =
                execute
                    (sprintf "DELETE FROM %s WHERE scope = @scope AND fact_id = 'planted'" TestTable)
                    (bindScope scope)

            let! second = FactStoreMigration.migrate source resumed log options [ scope ]
            let s = second.Scopes.Head
            Expect.equal s.Outcome Verified (FactStoreMigration.render second)
            Expect.equal s.ResumedFrom 600 "the second run continued after the last committed page"
            Expect.equal s.Inserted 20 "and wrote only the rest"

            let! wholeRows = rowsOf "toolup_facts_migration_whole" scope
            let! resumedRows = rowsOf TestTable scope
            Expect.equal resumedRows wholeRows "the resumed rows are the uninterrupted rows, byte for byte"

            // Forget the progress: every page is replayed, and writes nothing.
            let! _ = execute (sprintf "DELETE FROM %s_migration WHERE scope = @scope" TestTable) (bindScope scope)
            let! replay = FactStoreMigration.migrate source resumed log options [ scope ]
            let r = replay.Scopes.Head
            Expect.equal r.Outcome Verified "the replay verifies"
            Expect.equal r.ResumedFrom 0 "the replay started from the first page"
            Expect.equal r.Inserted 0 "a replayed row is a no-op by content address"
            let! afterReplay = rowsOf TestTable scope
            Expect.equal afterReplay wholeRows "no row was duplicated or changed"

            let! kinds = migrationEvents log scope
            Expect.hasLength kinds 4 "one record per scope per run: whole, interrupted, resumed, replayed"
        }

        testCaseAsync "the verification fails loudly on planted differences, naming each fact"
        <| async {
            let source, _ = blobSource ()
            let scope = newScope ()
            do! migrationSeed source scope
            let log = InMemoryEventStore.InMemoryEventStore()
            let target = targetOn TestTable log
            let! migrated = FactStoreMigration.migrate source target log options [ scope ]
            Expect.isTrue migrated.Passed "the clean copy verifies"

            let! export = source.ExportScope scope
            let facts = byTransactionTime export.Facts

            let superseded =
                facts
                |> List.find (fun f -> facts |> List.exists (fun g -> g.Supersedes = Some f.FactId))

            let moved = facts |> List.find (fun f -> f.Metric = MetricRef "visits")

            let lone =
                facts
                |> List.findBack (fun f -> f.Metric = MetricRef "visits" && f.FactId <> moved.FactId)

            let one (sql: string) (id: string) =
                execute (sql.Replace("{table}", TestTable)) (fun cmd ->
                    bindScope scope cmd
                    cmd.Parameters.AddWithValue("id", id) |> ignore)

            let! _ = one "DELETE FROM {table} WHERE scope = @scope AND fact_id = @id" superseded.FactId

            let! _ =
                one
                    "UPDATE {table} SET as_of_ticks = as_of_ticks + 1 WHERE scope = @scope AND fact_id = @id"
                    moved.FactId

            let! _ = one "UPDATE {table} SET is_head = false WHERE scope = @scope AND fact_id = @id" lone.FactId

            let! report = FactStoreMigration.verify source target options [ scope ]
            let s = report.Scopes.Head
            Expect.equal s.Outcome VerificationFailed "the verification fails"
            Expect.equal (FactStoreMigration.exitCode report) 1 "a non-zero exit"
            Expect.contains s.Differences (MissingOnTarget superseded.FactId) "the deleted row is named"

            Expect.contains
                s.Differences
                (ColumnDiffers(moved.FactId, "as_of_ticks", string moved.AsOf.Ticks, string (moved.AsOf.Ticks + 1L)))
                "the moved transaction time is named"

            Expect.contains s.Differences (HeadDiffers(lone.FactId, true, false)) "the cleared head is named"

            Expect.isTrue
                (s.Differences
                 |> List.exists (function
                     | AsOfReadDiffers(_, id, true) -> id = lone.FactId
                     | _ -> false))
                "the cleared head's AsOf read differs too"

            let text = FactStoreMigration.render report

            for id in [ superseded.FactId; moved.FactId; lone.FactId ] do
                Expect.stringContains text id "the rendered report names the fact"
        }

        testCaseAsync "a source with an unreadable blob is refused, named, and migrates only when allowed"
        <| async {
            let source, storage = blobSource ()
            let scope = newScope ()
            do! migrationSeed source scope
            let! _ = storage.Upload(scope, "_facts/garbage.json", Encoding.UTF8.GetBytes "{ not json")
            let log = InMemoryEventStore.InMemoryEventStore()
            let target = targetOn TestTable log

            let! refused = FactStoreMigration.migrate source target log options [ scope ]
            let s = refused.Scopes.Head
            Expect.equal s.Outcome SourceRefused "the scope is refused"

            Expect.isTrue
                (s.Differences
                 |> List.exists (function
                     | UnreadableSource(blob, _) -> blob = "_facts/garbage.json"
                     | _ -> false))
                "the blob is named"

            let! written = scalar (sprintf "SELECT count(*) FROM %s WHERE scope = @scope" TestTable) (bindScope scope)
            Expect.equal (Convert.ToInt64 written) 0L "nothing was written"

            let! allowed =
                FactStoreMigration.migrate
                    source
                    target
                    log
                    {
                        options with
                            AllowUnreadableSource = true
                    }
                    [ scope ]

            Expect.equal allowed.Scopes.Head.Outcome Verified "the operator's decision migrates the readable facts"
            let! kinds = migrationEvents log scope
            Expect.hasLength kinds 2 "a record for the refusal and one for the migration"
        }
    ]

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

        // The shapes the differential seed does not reach (Phase 940).
        aggregateTests blobReference candidate

        // Migrating a blob store into the table (Phase 941).
        migrationLiveTests dataSource

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

            testCaseAsync "the migration builds the covering population index and retires pop_idx (Phase 962)"
            <| async {
                let _ = store None (fun () -> DateTime.UtcNow)

                use cmd =
                    dataSource.CreateCommand "SELECT indexname FROM pg_indexes WHERE tablename = @table"

                cmd.Parameters.AddWithValue("table", TestTable) |> ignore
                use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
                let names = ResizeArray<string>()

                while reader.Read() do
                    names.Add(reader.GetString 0)

                Expect.contains names (TestTable + "_population_idx") "the covering population index exists"
                Expect.isFalse (names.Contains(TestTable + "_pop_idx")) "the index it replaces is dropped"
            }

            testCaseAsync
                "a latest-week population read is index-only over that week, however long the history and however stale the statistics (Phase 962)"
            <| async {
                // A table of its own, rebuilt per run: what the statistics
                // know must be exactly what this case wrote, not what the
                // shared test table accumulated across runs and packs.
                let table = TestTable + "_history"
                do! execute (sprintf "DROP TABLE IF EXISTS %s" table)
                let scope = "history-" + Guid.NewGuid().ToString("N").Substring(0, 12)

                let s =
                    PostgresFactStore.createWithDataSource
                        dataSource
                        { liveOptions with Table = table }
                        (events ())
                        None
                        (fun () -> DateTime.UtcNow)

                let facts = s :> IFactStore

                let drafts = [
                    for subject in 0 .. HistorySubjects - 1 do
                        for w in 0 .. HistoryWeeks - 1 -> historyDraft subject w "seed"
                ]

                for chunk in List.chunkBySize 5_000 drafts do
                    match! facts.AssertBatch(scope, chunk) with
                    | Ok _ -> ()
                    | Error e -> failtestf "seed failed: %s" e

                // Statistics and the visibility map taken BEFORE the
                // supersessions below, so the read is planned on a table
                // whose statistics predate its history: the state the old
                // `IN` form of the AsOf branch was planned into a walk of.
                do! execute (sprintf "VACUUM (ANALYZE) %s" table)
                let latest = HistoryWeeks - 1

                for subject in 0..99 do
                    match! facts.Assert(scope, historyDraft subject latest "revised") with
                    | Ok _ -> ()
                    | Error e -> failtestf "revision failed: %s" e

                let query = {
                    PopulationQuery.create (MetricRef "history") "geography" with
                        PeriodOverlaps = Some(historyWeek latest)
                        Ordering = Descending
                }

                let! plan = s.ExplainPopulation(scope, query)
                let touched, sequential = touchedRows plan
                let scans = scanNodes plan

                let! answer = facts.QueryPopulation(scope, query)

                printfn
                    "Phase 962: %d subjects x %d weeks, 100 revised after ANALYZE — the latest-week summary touched %d rows; scans %A"
                    HistorySubjects
                    HistoryWeeks
                    touched
                    scans

                match answer with
                | Ok result -> Expect.equal result.Stats.SubjectCount HistorySubjects "one head per subject"
                | Error e -> failtestf "population read refused: %s" e

                Expect.isFalse sequential "no sequential scan"

                Expect.isTrue
                    (scans |> List.contains ("Index Only Scan", table + "_population_idx"))
                    "the heads are read index-only from the covering population index"

                // The latest week's heads, and nothing of the 51 earlier
                // weeks or of the revised rows' predecessors.
                Expect.isLessThanOrEqual
                    touched
                    (int64 HistorySubjects + 10L)
                    "the rows touched are the asked week's population, not the metric's history"
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