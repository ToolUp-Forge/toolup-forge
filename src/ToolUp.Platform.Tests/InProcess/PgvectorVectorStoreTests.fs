module ToolUp.Platform.Tests.InProcess.PgvectorVectorStoreTests

open System
open Expecto
open Npgsql
open ToolUp.Platform
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.VectorStores.Pgvector.PgvectorVectorStore

// ─── Phase 507 — Pgvector IVectorStore test pack ─────────────────────
//
// Two arms, deliberately:
//
//  • **Structural arm (always on).** Everything that can be proved
//    WITHOUT a database — the scope-isolation guarantee (GP 4) read off
//    the generated SQL, the create-time option guards (507.C), and the
//    vector / metadata codecs. This is the arm that makes the isolation
//    claim a gate rather than a comment: `Sql.scopeBoundStatements`
//    enumerates every statement that touches chunk rows together with
//    HOW it binds scope, and the test asserts that binding on each — a
//    `scope = @scope` predicate for the six that read or mutate rows,
//    and identity + conflict target for the one INSERT. A future
//    statement added without a binding fails here, in CI, on a fresh
//    checkout with no Postgres anywhere near it.
//
//  • **Live arm (env-gated on `TOOLUP_PGVECTOR_CONNECTION_STRING`).**
//    The full `IVectorStore` contract + the scope-isolation cases ported
//    from the HNSW pack + the shared deterministic-ordering contract +
//    the two-replica consistency case that is the whole point of an
//    external store. Reported **Pending** when the variable is unset, so
//    a fresh checkout is green without a database — the same posture the
//    `ToolUp.AIProviders.Tests` live arms take.
//
// Each live case gets its own table (`pgv_test_<guid>`) and drops it on
// the way out, so the arm is re-runnable and two concurrent runs against
// one database cannot interfere.

[<Literal>]
let private LiveConnectionEnvVar = "TOOLUP_PGVECTOR_CONNECTION_STRING"

let private liveConnectionString =
    match Environment.GetEnvironmentVariable LiveConnectionEnvVar with
    | null
    | "" -> None
    | s -> Some s

/// Silences the companion's own warn-level output during the live arm.
type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

let private chunk content tag : TextChunk = {
    Content = content
    Metadata = Map.ofList [ "tag", tag ]
}

/// Assert that `f` raises, and that the message NAMES the problem.
/// `Expect.throwsC` takes a continuation and yields nothing, and
/// `Expect.throws` discards the message — but for the fail-loud cases
/// here the message text IS the behaviour under test: a descriptive
/// error at the right moment is the whole of 507.C.
let private expectRaisesNaming (fragment: string) (message: string) (f: unit -> unit) =
    let caught =
        try
            f ()
            None
        with ex ->
            Some ex

    match caught with
    | None -> failtestf "%s — expected an exception naming '%s'; none was raised" message fragment
    | Some ex -> Expect.stringContains ex.Message fragment message

// ─── Structural arm ──────────────────────────────────────────────────

let private defaultOptions = PgvectorOptions.forDimensions 1536

let private scopeIsolationTests =
    testList "structural scope isolation (GP 4)" [
        test "every chunk-touching statement binds the scope column" {
            let statements = Sql.scopeBoundStatements defaultOptions

            Expect.isNonEmpty statements "the scope-bound statement set must not be empty"

            for name, binding, sql in statements do
                // Every statement carries the parameter — a statement
                // that never mentions @scope could not be isolated at all.
                Expect.stringContains
                    sql
                    Sql.ScopeParameter
                    (sprintf "%s must bind the %s parameter" name Sql.ScopeParameter)

                match binding with
                | Sql.ScopePredicated ->
                    Expect.stringContains
                        sql
                        Sql.ScopePredicate
                        (sprintf
                            "%s reads or mutates existing rows, so it must filter on `%s` — scope isolation is structural, not a remembered filter"
                            name
                            Sql.ScopePredicate)
                | Sql.ScopeKeyed ->
                    // An INSERT has no rows to filter yet: its isolation
                    // is the row identity. Assert BOTH halves — the
                    // written value and the conflict target — because
                    // either alone would let a conflicting row from
                    // another scope be updated.
                    Expect.stringContains
                        sql
                        "(@scope,"
                        (sprintf "%s must write the caller's scope into the row identity" name)

                    Expect.stringContains
                        sql
                        Sql.ScopeConflictTarget
                        (sprintf
                            "%s must resolve conflicts on the composite key, so an id colliding across scopes cannot be overwritten"
                            name)
        }

        test "the scope-bound set covers every chunk-touching interface member" {
            let covered =
                Sql.scopeBoundStatements defaultOptions
                |> List.map (fun (name, _, _) -> name)
                |> Set.ofList

            let expected =
                Set.ofList [
                    "Upsert"
                    "Search"
                    "ListChunks"
                    "DeleteChunk"
                    "RestoreChunk"
                    "Vacuum"
                    "DeleteByScope"
                ]

            // `ListScopes` is the one exempt member — it exists to
            // ENUMERATE scopes, so a scope predicate would make it
            // useless. Naming the exemption here means a future member
            // cannot join it by silent omission.
            Expect.equal covered expected "the scope-bound set must be exactly the chunk-touching members"
        }

        test "ListScopes is the only statement without a scope predicate" {
            let listScopes = Sql.listScopes defaultOptions

            Expect.isFalse
                (listScopes.Contains Sql.ScopePredicate)
                "ListScopes enumerates scopes and is exempt by construction"

            Expect.stringContains listScopes "SELECT DISTINCT scope" "ListScopes must read the scope column directly"
        }

        test "multi-scope search issues one scope-parameterised query, never a scope array" {
            let searchSql = Sql.search defaultOptions

            Expect.isFalse
                (searchSql.Contains "ANY(" || searchSql.Contains "IN (")
                "a scope-set parameter would move the isolation guarantee inside an array — one query per scope instead"
        }

        test "search filters tombstones and orders deterministically" {
            let searchSql = Sql.search defaultOptions
            Expect.stringContains searchSql "deleted_at IS NULL" "tombstoned chunks must never surface in search"
            Expect.stringContains searchSql "ORDER BY embedding <=>" "cosine distance is the ranking operator"
            Expect.stringContains searchSql ", chunk_id" "equal distances must tie-break on chunk_id, not on row order"
        }
    ]

let private schemaTests =
    testList "schema" [
        test "migration declares the composite key and the column dimension" {
            let ddl = Sql.migration defaultOptions |> String.concat "\n"

            Expect.stringContains ddl "PRIMARY KEY (scope, chunk_id)" "scope is part of the row identity, not a filter"
            Expect.stringContains ddl "vector(1536)" "the embedding column must be declared at the configured dimension"

            Expect.stringContains
                ddl
                "deleted_at timestamptz"
                "the tombstone column backs the Phase 14h soft-delete contract"

            Expect.stringContains ddl "CREATE TABLE IF NOT EXISTS" "migration must be idempotent"
        }

        test "migration honours a custom table name throughout" {
            let options = {
                defaultOptions with
                    Table = "custom_chunks"
            }

            let ddl = Sql.migration options |> String.concat "\n"

            Expect.stringContains ddl "custom_chunks" "the configured table name must reach the DDL"

            Expect.isFalse
                (ddl.Contains "toolup_rag_chunks")
                "the default table name must not leak into a custom deployment"
        }

        test "no ANN index is built by default" {
            let ddl = Sql.migration defaultOptions |> String.concat "\n"

            Expect.isFalse
                (ddl.Contains "USING hnsw" || ddl.Contains "USING ivfflat")
                "the default is exact search — an ANN index is a deliberate opt-in (GP 11)"
        }

        test "the HNSW ANN index is emitted when opted in" {
            let options = {
                defaultOptions with
                    AnnIndex = HnswAnnIndex(16, 64)
            }

            let ddl = Sql.migration options |> String.concat "\n"
            Expect.stringContains ddl "USING hnsw (embedding vector_cosine_ops)" "opted-in HNSW index must be built"
            Expect.stringContains ddl "m = 16" "the configured neighbour budget must reach the DDL"
            Expect.stringContains ddl "ef_construction = 64" "the configured build candidate list must reach the DDL"
        }

        test "the IVFFlat ANN index is emitted when opted in" {
            let options = {
                defaultOptions with
                    AnnIndex = IvfFlatAnnIndex 100
            }

            let ddl = Sql.migration options |> String.concat "\n"

            Expect.stringContains
                ddl
                "USING ivfflat (embedding vector_cosine_ops)"
                "opted-in IVFFlat index must be built"

            Expect.stringContains ddl "lists = 100" "the configured list count must reach the DDL"
        }
    ]

let private optionGuardTests =
    testList "create-time option guards (507.C fail-loud)" [
        test "the default options validate" {
            Expect.isOk (PgvectorOptions.validate defaultOptions) "the shipped defaults must be valid"
        }

        test "a table name that is not a plain identifier is refused" {
            for bad in [ "chunks; DROP TABLE users"; "chunks\"x"; "1chunks"; ""; "   "; "chunk-table" ] do
                let result = PgvectorOptions.validate { defaultOptions with Table = bad }

                Expect.isError
                    result
                    (sprintf
                        "'%s' is interpolated into every statement — anything but a plain identifier must be refused"
                        bad)
        }

        test "an over-long table name is refused" {
            let tooLong = String.replicate 64 "a"

            Expect.isError
                (PgvectorOptions.validate { defaultOptions with Table = tooLong })
                "63 bytes is PostgreSQL's limit"
        }

        test "a plain identifier is accepted" {
            for good in [ "toolup_rag_chunks"; "_chunks"; "Chunks2" ] do
                Expect.isOk
                    (PgvectorOptions.validate { defaultOptions with Table = good })
                    (sprintf "'%s' is a plain SQL identifier" good)
        }

        test "out-of-range dimensions are refused" {
            Expect.isError
                (PgvectorOptions.validate { defaultOptions with Dimensions = 0 })
                "0 dimensions is not a vector"

            Expect.isError
                (PgvectorOptions.validate {
                    defaultOptions with
                        Dimensions = PgvectorOptions.MaxDimensions + 1
                })
                "pgvector's own ceiling is 16000"
        }

        test "a negative command timeout is refused" {
            Expect.isError
                (PgvectorOptions.validate {
                    defaultOptions with
                        CommandTimeoutSeconds = -1
                })
                "a negative timeout is a typo, not a configuration"
        }

        test "pathological ANN parameters are refused" {
            Expect.isError
                (PgvectorOptions.validate {
                    defaultOptions with
                        AnnIndex = HnswAnnIndex(1, 64)
                })
                "m = 1 cannot form a navigable graph"

            Expect.isError
                (PgvectorOptions.validate {
                    defaultOptions with
                        AnnIndex = HnswAnnIndex(16, 8)
                })
                "ef_construction below 2*m degrades the build"

            Expect.isError
                (PgvectorOptions.validate {
                    defaultOptions with
                        AnnIndex = IvfFlatAnnIndex 0
                })
                "zero lists is not a partitioning"
        }

        test "create raises before any I/O when the options are invalid" {
            // The connection string is deliberately unreachable: if the
            // guard did not run first, this would fail with a connection
            // error (or hang), not an option error. The assertion on the
            // MESSAGE is what makes the ordering observable.
            expectRaisesNaming
                "plain SQL identifier"
                "option validation must precede any connection attempt, and say what is wrong"
                (fun () ->
                    create
                        "Host=192.0.2.1;Port=5432;Database=nope;Timeout=1"
                        {
                            defaultOptions with
                                Table = "bad name"
                        }
                        None
                    |> ignore)
        }

        test "create refuses an empty connection string" {
            expectRaisesNaming
                "connection string is empty"
                "an empty connection string is a compose-time mistake, named as one"
                (fun () -> create "" defaultOptions None |> ignore)
        }
    ]

let private codecTests =
    testList "codecs" [
        test "scope keys round-trip for every scope shape" {
            for scope in [ Platform; Deployment; Team "acme"; User "u-1" ] do
                let roundTripped = Scope.toKey scope |> Scope.fromKey
                Expect.equal roundTripped scope (sprintf "%A must survive the scope-column round-trip" scope)
        }

        test "team and user scopes with colons in the id round-trip" {
            // A team id is opaque to the store; a naive split on ':'
            // would corrupt one containing a colon.
            let scope = Team "tenant:eu:1"
            Expect.equal (Scope.toKey scope |> Scope.fromKey) scope "the id is everything after the first prefix"
        }

        test "normalise produces a unit vector" {
            let v: float32 array = [| 3.0f; 4.0f |]
            let m = Vector.magnitude (Vector.normalise v)
            Expect.floatClose Accuracy.medium (float m) 1.0 "normalised vectors have unit magnitude"
        }

        test "a zero vector is left alone rather than given a fabricated direction" {
            let v: float32 array = [| 0.0f; 0.0f |]
            Expect.equal (Vector.normalise v) v "there is no honest direction to invent (GP 9)"
        }

        test "vector literals use pgvector's text form in invariant culture" {
            let literal = Vector.toLiteral [| 1.0f; -0.5f; 0.25f |]
            Expect.stringStarts literal "[" "pgvector's text input is bracketed"
            Expect.stringEnds literal "]" "pgvector's text input is bracketed"
            Expect.stringContains literal "-0.5" "the decimal separator must be invariant, never locale-dependent"
            Expect.isFalse (literal.Contains ";") "elements are comma-separated regardless of locale"
        }

        test "metadata round-trips through the jsonb codec" {
            let metadata =
                Map.ofList [ "_origin", "Document"; "tag", "a"; "unicode", "café — ünïcode"; "empty", "" ]

            let decoded = Metadata.toJson metadata |> Metadata.fromJson
            Expect.equal decoded (Ok metadata) "the jsonb column is a query surface — it must round-trip exactly"
        }

        test "empty metadata encodes as an empty JSON object" {
            Expect.equal (Metadata.toJson Map.empty) "{}" "the column default is '{}'::jsonb"
        }

        test "a non-object metadata value decodes as an error, not silently as empty" {
            Expect.isError (Metadata.fromJson "[1,2,3]") "an array is not a metadata map"
            Expect.isError (Metadata.fromJson "{not json") "a malformed value must be reported, not swallowed"
        }
    ]

let private structuralTests =
    testList "structural (no database required)" [ scopeIsolationTests; schemaTests; optionGuardTests; codecTests ]

// ─── Live arm ────────────────────────────────────────────────────────

let private freshTableName () =
    "pgv_test_" + Guid.NewGuid().ToString "N"

let private dropTable (connectionString: string) (table: string) =
    try
        use dataSource = NpgsqlDataSource.Create connectionString
        use cmd = dataSource.CreateCommand(sprintf "DROP TABLE IF EXISTS %s;" table)
        cmd.ExecuteNonQuery() |> ignore
    with _ ->
        () // Best-effort cleanup; a leaked test table is not a test failure.

/// Store factory over a fresh table. The returned `IDisposable` disposes
/// the store (and its owned data source) and drops the table.
let private makeStoreWith (connectionString: string) (dimensions: int) () : IVectorStore * IDisposable =
    let table = freshTableName ()

    let options = {
        PgvectorOptions.forDimensions dimensions with
            Table = table
    }

    let store = create connectionString options (Some(SilentLogger() :> ILogger))

    let cleanup =
        { new IDisposable with
            member _.Dispose() =
                (store :?> IDisposable).Dispose()
                dropTable connectionString table
        }

    store, cleanup

let private eightDim (axis: int) : float32 array =
    Array.init 8 (fun i -> if i = axis then 1.0f else 0.0f)

let private liveTests (connectionString: string) =
    let makeStore = makeStoreWith connectionString 8

    testList "live (TOOLUP_PGVECTOR_CONNECTION_STRING set)" [
        testCaseAsync "create provisions the schema and the store answers immediately"
        <| async {
            let store, dispose = makeStore ()

            try
                let! scopes = store.ListScopes()
                Expect.isEmpty scopes "a freshly-migrated table holds no scopes"
            finally
                dispose.Dispose()
        }

        testCaseAsync "upsert / search round-trip"
        <| async {
            let store, dispose = makeStore ()

            try
                do! store.Upsert (Team "T") "c-1" (eightDim 0) (chunk "hello" "a")
                let! results = store.Search [ Team "T" ] (eightDim 0) 5

                Expect.hasLength results 1 "the one upserted chunk must be retrievable"
                Expect.equal results.Head.ChunkId "c-1" "the chunk id must survive the round-trip"
                Expect.equal results.Head.Content "hello" "the content must survive the round-trip"
                Expect.equal results.Head.Scope (Team "T") "the match must be stamped with the queried scope"
                Expect.equal (results.Head.Metadata.TryFind "tag") (Some "a") "metadata must survive the jsonb column"
                Expect.floatClose Accuracy.medium results.Head.Score 1.0 "an exact-direction match scores ~1.0"
            finally
                dispose.Dispose()
        }

        testCaseAsync "upsert is idempotent on (scope, chunkId) and replaces content"
        <| async {
            let store, dispose = makeStore ()

            try
                do! store.Upsert (Team "T") "c-1" (eightDim 0) (chunk "first" "a")
                do! store.Upsert (Team "T") "c-1" (eightDim 0) (chunk "second" "b")

                let! chunks = store.ListChunks (Team "T") false
                Expect.hasLength chunks 1 "re-upserting the same id must replace, not duplicate"
                Expect.equal (snd chunks.Head).Content "second" "the later upsert wins"
            finally
                dispose.Dispose()
        }

        testCaseAsync "scope isolation: a query against Team A never returns Team B chunks"
        <| async {
            let store, dispose = makeStore ()

            try
                do! store.Upsert (Team "A") "a-1" (eightDim 1) (chunk "team A note" "A")
                do! store.Upsert (Team "B") "b-1" (eightDim 2) (chunk "team B note" "B")
                do! store.Upsert Platform "p-1" (eightDim 0) (chunk "platform note" "P")

                // Query Team A with Team B's exact vector — the scope
                // predicate means B's row is not a candidate at all,
                // however close the query sits to it.
                let! results = store.Search [ Team "A" ] (eightDim 2) 5

                Expect.all results (fun m -> m.Scope = Team "A") "every match must come from Team A"

                Expect.isFalse
                    (results |> List.exists (fun m -> m.ChunkId = "b-1"))
                    "Team B's chunk must not surface in a Team A query"

                Expect.isFalse
                    (results |> List.exists (fun m -> m.ChunkId = "p-1"))
                    "the Platform chunk must not surface unless Platform scope was requested"
            finally
                dispose.Dispose()
        }

        testCaseAsync "scope isolation: a multi-scope query returns matches only from the requested scopes"
        <| async {
            let store, dispose = makeStore ()

            try
                do! store.Upsert (Team "A") "a-1" (eightDim 1) (chunk "team A" "A")
                do! store.Upsert (Team "B") "b-1" (eightDim 2) (chunk "team B" "B")
                do! store.Upsert Platform "p-1" (eightDim 0) (chunk "platform" "P")

                let! results = store.Search [ Platform; Team "A" ] (eightDim 0) 10

                Expect.all
                    results
                    (fun m -> m.Scope = Platform || m.Scope = Team "A")
                    "no result may originate outside the requested scopes"

                Expect.isFalse
                    (results |> List.exists (fun m -> m.ChunkId = "b-1"))
                    "Team B must be invisible when only Platform + Team A are requested"
            finally
                dispose.Dispose()
        }

        testCaseAsync "scope isolation: DeleteByScope touches only its own scope"
        <| async {
            let store, dispose = makeStore ()

            try
                do! store.Upsert (Team "A") "a-1" (eightDim 1) (chunk "team A" "A")
                do! store.Upsert (Team "B") "b-1" (eightDim 2) (chunk "team B" "B")

                do! store.DeleteByScope(Team "A")

                let! a = store.ListChunks (Team "A") true
                let! b = store.ListChunks (Team "B") true
                Expect.isEmpty a "the targeted scope must be cleared"
                Expect.hasLength b 1 "a sibling scope must be untouched"
            finally
                dispose.Dispose()
        }

        testCaseAsync "tombstone: delete hides from search, ListChunks surfaces it, restore reinstates"
        <| async {
            let store, dispose = makeStore ()

            try
                do! store.Upsert (Team "T") "live" (eightDim 1) (chunk "live" "x")
                do! store.Upsert (Team "T") "doomed" (eightDim 2) (chunk "doomed" "x")

                do! store.DeleteChunk (Team "T") "doomed"

                let! afterDelete = store.Search [ Team "T" ] (eightDim 2) 5

                Expect.isFalse
                    (afterDelete |> List.exists (fun m -> m.ChunkId = "doomed"))
                    "a tombstoned chunk must not surface in search"

                let! visible = store.ListChunks (Team "T") false
                Expect.hasLength visible 1 "ListChunks false must skip tombstoned chunks"

                let! all = store.ListChunks (Team "T") true
                Expect.hasLength all 2 "ListChunks true must return tombstoned chunks for re-embed workflows"

                let doomed = all |> List.find (fun (id, _) -> id = "doomed") |> snd

                Expect.isTrue
                    (doomed.Metadata.ContainsKey ChunkMetadata.DeletedAtKey)
                    "the tombstone column must project onto the contract's _deletedAt metadata key"

                do! store.RestoreChunk (Team "T") "doomed"
                let! afterRestore = store.Search [ Team "T" ] (eightDim 2) 5

                Expect.isTrue
                    (afterRestore |> List.exists (fun m -> m.ChunkId = "doomed"))
                    "a restored chunk must reappear in search"
            finally
                dispose.Dispose()
        }

        testCaseAsync "upsert over a tombstoned chunk clears the tombstone"
        <| async {
            let store, dispose = makeStore ()

            try
                do! store.Upsert (Team "T") "c-1" (eightDim 0) (chunk "before" "x")
                do! store.DeleteChunk (Team "T") "c-1"
                do! store.Upsert (Team "T") "c-1" (eightDim 0) (chunk "after" "x")

                let! results = store.Search [ Team "T" ] (eightDim 0) 5
                Expect.hasLength results 1 "the new content supersedes the tombstone (the IVectorStore contract)"
                Expect.equal results.Head.Content "after" "the superseding content must be what search returns"
            finally
                dispose.Dispose()
        }

        testCaseAsync "vacuum purges tombstones past the threshold and leaves live chunks alone"
        <| async {
            let store, dispose = makeStore ()

            try
                do! store.Upsert (Team "T") "live" (eightDim 0) (chunk "live" "x")
                do! store.Upsert (Team "T") "doomed" (eightDim 1) (chunk "doomed" "x")
                do! store.DeleteChunk (Team "T") "doomed"

                let! notYet = store.Vacuum (Team "T") (DateTimeOffset.UtcNow.AddHours -1.0)
                Expect.equal notYet 0 "a tombstone inside the retention window must not be purged"

                let! purged = store.Vacuum (Team "T") (DateTimeOffset.UtcNow.AddMinutes 1.0)
                Expect.equal purged 1 "a tombstone past the threshold must be purged and counted"

                let! all = store.ListChunks (Team "T") true
                Expect.hasLength all 1 "the live chunk must be untouched by vacuum"
            finally
                dispose.Dispose()
        }

        testCaseAsync "ListScopes enumerates every scope holding rows"
        <| async {
            let store, dispose = makeStore ()

            try
                do! store.Upsert Platform "p" (eightDim 0) (chunk "p" "x")
                do! store.Upsert (Team "A") "a" (eightDim 1) (chunk "a" "x")
                do! store.Upsert (User "u-1") "u" (eightDim 2) (chunk "u" "x")

                let! scopes = store.ListScopes()

                Expect.equal
                    (Set.ofList scopes)
                    (Set.ofList [ Platform; Team "A"; User "u-1" ])
                    "every scope holding rows must be enumerated, decoded back to its typed form"
            finally
                dispose.Dispose()
        }

        testCaseAsync "an upsert whose vector dimension does not match the column is refused, loudly"
        <| async {
            let store, dispose = makeStore ()

            try
                let wrongDim: float32 array = Array.init 4 (fun _ -> 1.0f)

                expectRaisesNaming
                    "re-embed"
                    "the message must name the operator action, not surface a raw driver error"
                    (fun () ->
                        store.Upsert (Team "T") "bad" wrongDim (chunk "bad" "x")
                        |> Async.RunSynchronously)
            finally
                dispose.Dispose()
        }

        // The shared deterministic-ordering contract, bound against
        // Pgvector exactly as the in-tree stores bind it. This is the
        // "run the existing contract pack against Pgvector" half of
        // 507.D — the same assertion, not a re-written cousin.
        HnswVectorStoreTests.deterministicOrderingContract "PgvectorVectorStore" makeStore

        // The acceptance criterion an external store exists for: two
        // stores over one database are two replicas, and a chunk written
        // by one is immediately retrievable by the other. Both in-tree
        // stores fail this by construction — their index is per-process.
        testCaseAsync "two replicas sharing one database serve consistent retrieval"
        <| async {
            let table = freshTableName ()

            let options = {
                PgvectorOptions.forDimensions 8 with
                    Table = table
            }

            let logger = Some(SilentLogger() :> ILogger)
            let replicaA = create connectionString options logger
            // The second store migrates against the same table — the
            // migration is idempotent, which is itself part of the claim.
            let replicaB = create connectionString options logger

            try
                do! replicaA.Upsert (Team "T") "written-by-a" (eightDim 3) (chunk "from replica A" "x")

                let! seenByB = replicaB.Search [ Team "T" ] (eightDim 3) 5

                Expect.hasLength seenByB 1 "replica B must see replica A's ingest with no flush/reload cycle"
                Expect.equal seenByB.Head.ChunkId "written-by-a" "the row is shared state, not per-process index state"

                // …and the reverse, including the tombstone.
                do! replicaB.DeleteChunk (Team "T") "written-by-a"
                let! seenByA = replicaA.Search [ Team "T" ] (eightDim 3) 5
                Expect.isEmpty seenByA "replica A must observe replica B's tombstone immediately"
            finally
                (replicaA :?> IDisposable).Dispose()
                (replicaB :?> IDisposable).Dispose()
                dropTable connectionString table
        }
    ]

// ─── Registration ────────────────────────────────────────────────────

let tests =
    testList "PgvectorVectorStore" [
        structuralTests

        match liveConnectionString with
        | Some connectionString -> liveTests connectionString
        | None ->
            // A single Pending case so the Expecto report lists the live
            // arm explicitly as skipped rather than absent — the absence
            // of a gate and a passing gate must not look alike.
            testList "live (TOOLUP_PGVECTOR_CONNECTION_STRING set)" [
                ptestCase $"skipped — {LiveConnectionEnvVar} not set" <| fun _ -> ()
            ]
    ]