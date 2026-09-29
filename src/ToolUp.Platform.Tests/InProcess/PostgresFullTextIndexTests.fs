module ToolUp.Platform.Tests.InProcess.PostgresFullTextIndexTests

open System
open Expecto
open Npgsql
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Tests.Contracts
open ToolUp.RAG.SparseAnalysis
open ToolUp.SparseIndices.Postgres.PostgresFullTextIndex

// ─── Phase 893 — PostgreSQL full-text ISparseIndex companion ─────────
//
// Two arms, the shape the pgvector companion's pack established:
//
//  • **Structural (always on).** Option guards, the analyzer → text-search
//    configuration mapping and its refusal (raised before any I/O, so it
//    needs no database), and scope binding read off the SQL: every
//    statement that touches chunk rows binds one scope.
//  • **Live (env-gated on `TOOLUP_PG_FULLTEXT_CONNECTION_STRING`).** Both
//    contract packs bound to the companion — `ISparseIndexContract` and
//    `IHealthCheckContract` — then the acceptance criterion the companion
//    exists for: two pipelines, each with its own connections, over one
//    database return the same hybrid results. Plus the configuration
//    lifecycle (re-analysis under AutoMigrate, refusal under VerifyOnly).
//
// Each live case gets its own table and drops it on the way out, so the arm
// is re-runnable and concurrent runs against one database cannot interfere.

[<Literal>]
let private LiveConnectionEnvVar = "TOOLUP_PG_FULLTEXT_CONNECTION_STRING"

let private liveConnectionString =
    match Environment.GetEnvironmentVariable LiveConnectionEnvVar with
    | null
    | "" -> None
    | s -> Some s

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

let private silent = Some(SilentLogger() :> ILogger)

let private expectRefusalNaming (needle: string) (message: string) (f: unit -> unit) =
    let raised =
        try
            f ()
            None
        with
        | PostgresFullTextIndexException m -> Some m
        | :? AggregateException as ae when (ae.InnerException :? PostgresFullTextIndexException) ->
            match ae.InnerException with
            | PostgresFullTextIndexException m -> Some m
            | _ -> None

    match raised with
    | Some m -> Expect.stringContains m needle message
    | None -> failtestf "expected a PostgresFullTextIndexException naming '%s'" needle

let private snowballEnglish () =
    ToolUp.SparseIndices.Snowball.SnowballAnalyzer.english ()

let private cjk () =
    ToolUp.SparseIndices.Cjk.CjkAnalyzer.bigrams ()

/// A deterministic bag-of-words embedder: each word lands in one of 16
/// dimensions. Enough for the dense leg to rank by overlap, so the fused
/// result genuinely depends on both legs.
let private hashingEmbedder =
    let embed (text: string) =
        let v = Array.zeroCreate<float32> 16

        for word in tokeniseWords text do
            let h = word |> Seq.fold (fun acc c -> (acc * 31 + int c) &&& 0x7fffffff) 7
            v[h % 16] <- v[h % 16] + 1.0f

        v

    { new IEmbeddingProvider with
        member _.GenerateEmbedding text = async { return embed text }
        member _.GenerateEmbeddings texts = async { return texts |> Seq.map embed |> Seq.toArray }
        member _.ProviderId = "test"
        member _.ModelId = "hashing-bow-16"
        member _.Dimensions = 16
    }

// ─── Structural arm ──────────────────────────────────────────────────

let private structuralTests =
    testList "structural (no database required)" [

        testList "analyzer → text-search configuration" [
            test "the identity analyzer maps to 'simple'" {
                Expect.equal (AnalyzerMapping.tryMap identity.Id) (Some "simple") "identity ↔ simple"
            }

            test "the Snowball English analyzer maps to 'english', whatever its folding / length settings" {
                Expect.equal (AnalyzerMapping.tryMap (snowballEnglish ()).Id) (Some "english") "default English"

                let unfolded =
                    ToolUp.SparseIndices.Snowball.SnowballAnalyzer.create {
                        ToolUp.SparseIndices.Snowball.SnowballAnalyzer.SnowballOptions.english with
                            FoldDiacritics = false
                            MinTermLength = 2
                    }

                Expect.equal (AnalyzerMapping.tryMap unfolded.Id) (Some "english") "folding / min length"
            }

            test "analyzers with no equivalent configuration do not map" {
                let french =
                    ToolUp.SparseIndices.Snowball.SnowballAnalyzer.forLanguage
                        ToolUp.SparseIndices.Snowball.StopWords.French

                let unstemmedEnglish =
                    ToolUp.SparseIndices.Snowball.SnowballAnalyzer.create {
                        ToolUp.SparseIndices.Snowball.SnowballAnalyzer.SnowballOptions.english with
                            Stemming = ToolUp.SparseIndices.Snowball.SnowballAnalyzer.NoStemming
                    }

                for analyzer in
                    [
                        cjk ()
                        french
                        unstemmedEnglish
                        ToolUp.RAG.SparseAnalysis.create "hand-rolled" tokeniseWords
                    ] do
                    Expect.isNone (AnalyzerMapping.tryMap analyzer.Id) (sprintf "'%s' has no configuration" analyzer.Id)
            }

            test "create refuses an unmappable analyzer before any I/O, naming it" {
                let analyzer = cjk ()

                expectRefusalNaming analyzer.Id "the refusal names the analyzer" (fun () ->
                    create "Host=unreachable.invalid;Database=none" PostgresFullTextOptions.defaults analyzer silent
                    |> ignore)
            }

            test "the composition factory refuses at composeRAG, naming the analyzer" {
                let analyzer = cjk ()

                let app =
                    ToolUp.RAG.RAGCompose.RAGServerApp.create
                        { new ToolUp.AI.IAIProviderFactory with
                            member _.Available = []
                            member _.PlatformDescriptors = []
                            member _.PlatformDescriptor = None
                            member _.Resolve _ = async { return Error ToolUp.AI.NoProviderConfigured }
                            member _.TryResolveByLabel(_, _) = async { return Error ToolUp.AI.NoProviderConfigured }
                            member _.BuildPlatform(_, _, _) = None
                        }
                        { new ToolUp.Platform.Providers.IProviderProfile with
                            member _.Get _ = async { return None }
                            member _.Set(_, _) = async { return Ok() }
                            member _.Clear _ = async { return () }
                            member _.ResolveEntry(_, _, _) = async { return None }
                            member _.SetEntryHealth(_, _, _) = async { return Ok() }
                        }
                        hashingEmbedder
                    |> ToolUp.RAG.RAGCompose.RAGServerApp.withSparseAnalyzer analyzer
                    |> ToolUp.RAG.RAGCompose.RAGServerApp.withAnalyzedSparseIndex (
                        factory "Host=unreachable.invalid;Database=none" PostgresFullTextOptions.defaults silent
                    )

                expectRefusalNaming analyzer.Id "composition refuses and names the analyzer" (fun () ->
                    ToolUp.RAG.RAGCompose.composeRAG app |> ignore)
            }

            test "an explicit configuration is the operator's choice and bypasses the mapping" {
                let options = {
                    PostgresFullTextOptions.defaults with
                        TextSearchConfiguration = Some "french"
                }

                Expect.equal
                    (PostgresFullTextOptions.resolveConfiguration options (cjk ()))
                    (Ok "french")
                    "explicit wins"
            }
        ]

        testList "option guards" [
            test "an unsafe table name is refused" {
                let bad = {
                    PostgresFullTextOptions.defaults with
                        Table = "chunks; DROP TABLE x"
                }

                Expect.isError (PostgresFullTextOptions.validate bad) "not a plain identifier"
            }

            test "an unsafe configuration name is refused" {
                let bad = {
                    PostgresFullTextOptions.defaults with
                        TextSearchConfiguration = Some "english'--"
                }

                Expect.isError (PostgresFullTextOptions.validate bad) "not a plain identifier"
            }

            test "a negative timeout is refused; the defaults validate" {
                Expect.isError
                    (PostgresFullTextOptions.validate {
                        PostgresFullTextOptions.defaults with
                            CommandTimeoutSeconds = -1
                    })
                    "negative timeout"

                Expect.isOk (PostgresFullTextOptions.validate PostgresFullTextOptions.defaults) "defaults"
            }
        ]

        test "every statement touching chunk rows binds one scope (GP 4)" {
            for member', sql in Sql.scopeBoundStatements PostgresFullTextOptions.defaults do
                let bound =
                    sql.Contains "scope = @scope"
                    || (sql.Contains "VALUES (@scope" && sql.Contains "ON CONFLICT (scope, chunk_id)")

                Expect.isTrue bound (sprintf "%s must bind one scope:\n%s" member' sql)

                Expect.isFalse (sql.Contains "ANY(") (sprintf "%s must not take a scope array" member')
        }
    ]

// ─── Live arm ────────────────────────────────────────────────────────

let private freshTable (prefix: string) =
    sprintf "%s_%s" prefix (Guid.NewGuid().ToString("N").Substring(0, 16))

let private dropTable (dataSource: NpgsqlDataSource) (table: string) =
    use cmd = dataSource.CreateCommand(sprintf "DROP TABLE IF EXISTS %s;" table)
    cmd.ExecuteNonQuery() |> ignore

let private liveTests (connectionString: string) =
    let harness () : ISparseIndexContract.SparseIndexHarness =
        let dataSource = NpgsqlDataSource.Create connectionString

        let options = {
            PostgresFullTextOptions.defaults with
                Table = freshTable "ftc"
        }

        {
            Open = fun () -> createWithDataSource dataSource options identity silent
            Close = fun index -> (index :?> IDisposable).Dispose()
            Dispose =
                fun () ->
                    dropTable dataSource options.Table
                    dataSource.Dispose()
        }

    testList "live (TOOLUP_PG_FULLTEXT_CONNECTION_STRING set)" [

        // The two contract packs, bound to the companion.
        ISparseIndexContract.tests "PostgresFullTextIndex" harness

        testList "readiness probe" [
            // The healthy binding probes one fixed, idempotently-created
            // table (the contract calls the factory once per case, and a
            // probe owns no teardown hook); it is left empty in the test
            // database, and every run reuses it.
            let probeIndex =
                lazy
                    (createWithDataSource
                        (NpgsqlDataSource.Create connectionString)
                        {
                            PostgresFullTextOptions.defaults with
                                Table = "toolup_ft_health_probe"
                        }
                        identity
                        silent)

            IHealthCheckContract.tests "PostgresFullTextIndexHealth" (fun () -> health probeIndex.Value) Healthy

            test "the probe is unhealthy once its table is gone" {
                use dataSource = NpgsqlDataSource.Create connectionString

                let options = {
                    PostgresFullTextOptions.defaults with
                        Table = freshTable "fth"
                }

                let index = createWithDataSource dataSource options identity silent
                dropTable dataSource options.Table

                match (health index).Check() |> Async.RunSynchronously with
                | Unhealthy _ -> ()
                | other -> failtestf "expected Unhealthy after the table was dropped, got %A" other
            }
        ]

        // The acceptance criterion the companion exists for. Each pipeline
        // opens its own data source (its own connections, as a separate
        // process would) over one database; the in-process keyword index
        // cannot pass this, because its postings live in the process that
        // wrote them.
        testCaseAsync "two pipelines over one database return the same hybrid results"
        <| async {
            let vectorTable = freshTable "ftv"
            let textTable = freshTable "ftt"

            let openReplica () =
                let dataSource = NpgsqlDataSource.Create connectionString

                let store =
                    ToolUp.RAG.VectorStores.Pgvector.PgvectorVectorStore.createWithDataSource
                        dataSource
                        {
                            ToolUp.RAG.VectorStores.Pgvector.PgvectorVectorStore.PgvectorOptions.forDimensions 16 with
                                Table = vectorTable
                        }
                        silent

                let sparse =
                    createWithDataSource
                        dataSource
                        {
                            PostgresFullTextOptions.defaults with
                                Table = textTable
                        }
                        identity
                        silent

                let pipeline =
                    new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(store, hashingEmbedder, sparse)
                    :> IRetrievalPipeline

                dataSource, store, sparse, pipeline

            let dsA, storeA, sparseA, pipelineA = openReplica ()
            let dsB, _, _, pipelineB = openReplica ()

            try
                let scope = Team "t1"

                let corpus = [
                    "c1", "quarterly revenue report for the northern region"
                    "c2", "revenue forecast and budget assumptions"
                    "c3", "office relocation timeline"
                    "c4", "northern region staffing plan"
                    "c5", "budget variance commentary on revenue"
                ]

                // Every write goes through replica A.
                for chunkId, content in corpus do
                    let chunk = {
                        Content = content
                        Metadata = Map.empty
                    }

                    let! vector = hashingEmbedder.GenerateEmbedding content
                    do! storeA.Upsert scope chunkId vector chunk
                    do! sparseA.Upsert scope chunkId chunk

                let ctx = AccessContext.unrestricted (TeamMember("u1", "t1"))

                for query in [ "revenue budget"; "northern region"; "relocation" ] do
                    let request = RetrievalRequest.create query [ scope ] 10 Interleaved
                    let! fromA = pipelineA.Retrieve request ctx
                    let! fromB = pipelineB.Retrieve request ctx
                    Expect.isNonEmpty fromA (sprintf "'%s' retrieves something" query)

                    Expect.equal
                        (fromB |> List.map (fun m -> m.ChunkId, m.Score))
                        (fromA |> List.map (fun m -> m.ChunkId, m.Score))
                        (sprintf "'%s': replica B serves exactly what replica A does" query)
            finally
                dropTable dsA vectorTable
                dropTable dsA textTable
                dsA.Dispose()
                dsB.Dispose()
        }

        testList "configuration lifecycle" [
            testCaseAsync "AutoMigrate re-analyses rows written under another configuration"
            <| async {
                use dataSource = NpgsqlDataSource.Create connectionString

                let options = {
                    PostgresFullTextOptions.defaults with
                        Table = freshTable "ftr"
                }

                try
                    let scope = Team "t1"
                    let simple = createWithDataSource dataSource options identity silent

                    do!
                        simple.Upsert scope "c1" {
                            Content = "the engines were running hot"
                            Metadata = Map.empty
                        }

                    let! unstemmed = simple.Search [ scope ] "run" 10
                    Expect.isEmpty unstemmed "'simple' does not stem: 'run' does not match 'running'"

                    let english = createWithDataSource dataSource options (snowballEnglish ()) silent

                    match english with
                    | :? PostgresFullTextIndex as pg ->
                        Expect.equal pg.TextSearchConfiguration "english" "the mapped configuration"
                    | _ -> failtest "expected the companion's type"

                    let! stemmed = english.Search [ scope ] "run" 10
                    Expect.equal (stemmed |> List.map _.ChunkId) [ "c1" ] "the stored row was re-analysed"
                finally
                    dropTable dataSource options.Table
            }

            testCaseAsync "VerifyOnly refuses rows written under another configuration, and a missing table"
            <| async {
                use dataSource = NpgsqlDataSource.Create connectionString

                let options = {
                    PostgresFullTextOptions.defaults with
                        Table = freshTable "ftv"
                }

                try
                    let simple = createWithDataSource dataSource options identity silent

                    do!
                        simple.Upsert (Team "t1") "c1" {
                            Content = "anything"
                            Metadata = Map.empty
                        }

                    expectRefusalNaming "english" "stale rows are refused under VerifyOnly" (fun () ->
                        createWithDataSource
                            dataSource
                            { options with SchemaMode = VerifyOnly }
                            (snowballEnglish ())
                            silent
                        |> ignore)

                    expectRefusalNaming "does not exist" "a missing table is refused under VerifyOnly" (fun () ->
                        createWithDataSource
                            dataSource
                            {
                                options with
                                    Table = freshTable "ftmissing"
                                    SchemaMode = VerifyOnly
                            }
                            identity
                            silent
                        |> ignore)
                finally
                    dropTable dataSource options.Table
            }

            test "a configuration the database does not have is refused, naming it" {
                use dataSource = NpgsqlDataSource.Create connectionString

                let options = {
                    PostgresFullTextOptions.defaults with
                        Table = freshTable "ftn"
                        TextSearchConfiguration = Some "no_such_configuration"
                }

                expectRefusalNaming "no_such_configuration" "the missing configuration is named" (fun () ->
                    createWithDataSource dataSource options identity silent |> ignore)
            }
        ]
    ]

// ─── Registration ────────────────────────────────────────────────────

let tests =
    testList "PostgresFullTextIndex" [
        structuralTests

        match liveConnectionString with
        | Some connectionString -> liveTests connectionString
        | None ->
            testList "live (TOOLUP_PG_FULLTEXT_CONNECTION_STRING set)" [
                ptestCase $"skipped — {LiveConnectionEnvVar} not set" <| fun _ -> ()
            ]
    ]