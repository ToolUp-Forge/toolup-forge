module ToolUp.Platform.Tests.InProcess.PgvectorVectorStoreTests

open System
open Expecto
open Npgsql
open ToolUp.Platform
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.VectorStores.Pgvector
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
//    `scope = @scope` predicate for the eight that read or mutate rows,
//    and identity + conflict target for the two INSERTs. A future
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
// Phase 892 adds, to the structural arm, the tuning guards, the
// per-query settings, the batched-write preparation and the batch-seam
// probe, and the health / preflight judgements; and to the live arm, the
// small-scope reproduction, the index-use confirmation (EXPLAIN), recall
// against the exact scan, and the batched write end to end.
//
// Phase 928 ran the live arm against a real server (pgvector 0.8.6 on
// PostgreSQL 17, the `pgvector` service in `compose.parity.yml`), found
// that the two-key `search` IS served by the approximate index, and
// replaced the index-use case with one asserting that — and that the
// fallback's `searchExact` never is — plus a case proving the fallback
// repairs a starved page exactly.
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
                    // another scope be updated. The single write binds it
                    // in a VALUES row, the batched write (Phase 892) as
                    // the first column of its SELECT.
                    Expect.isTrue
                        (sql.Contains "(@scope," || sql.Contains "SELECT @scope,")
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
                    "UpsertBatch"
                    "Search"
                    "SearchIndexOrdered"
                    "SearchExact"
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
            for searchSql in
                [
                    Sql.search defaultOptions
                    Sql.searchIndexOrdered defaultOptions
                    Sql.searchExact defaultOptions
                ] do
                Expect.isFalse
                    (searchSql.Contains "ANY(" || searchSql.Contains "IN (")
                    "a scope-set parameter would move the isolation guarantee inside an array — one query per scope instead"
        }

        test "the batched upsert binds scope once, as a scalar, never as an array" {
            let batchSql = Sql.upsertBatch defaultOptions

            Expect.isFalse
                (batchSql.Contains "@scope::text[]" || batchSql.Contains "ANY(")
                "one batch writes into exactly one scope — the scope must never ride inside the row arrays"
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

// ─── Phase 892 — structural arm ──────────────────────────────────────

let private hnswOptions = {
    defaultOptions with
        AnnIndex = HnswAnnIndex(16, 64)
}

let private ivfOptions = {
    defaultOptions with
        AnnIndex = IvfFlatAnnIndex 100
}

let private tuningTests =
    testList "Phase 892 tuning" [
        test "both presets validate against every index family" {
            for options in [ defaultOptions; hnswOptions; ivfOptions ] do
                Expect.isOk
                    (PgvectorTuning.validate options PgvectorTuning.unchanged)
                    "the unchanged preset must be valid"

                Expect.isOk
                    (PgvectorTuning.validate options PgvectorTuning.recommended)
                    "the recommended preset must be valid"
        }

        test "the unchanged preset is pgvector's own defaults" {
            let t = PgvectorTuning.unchanged
            Expect.isNone t.SearchWidth "no width is applied — the database default stays in force"
            Expect.isFalse t.IterativeScan "no iterative scan"
            Expect.isFalse t.ExactFallbackOnShortPage "no second query"
            Expect.equal t.MaxSearchConcurrency 1 "multi-scope search stays sequential"

            Expect.isEmpty
                (Sql.searchSettings hnswOptions t (Some "0.8.0"))
                "nothing is applied per query, so search runs outside any transaction, exactly as before"
        }

        test "an out-of-range search width is refused, per index family" {
            for bad in [ 0; PgvectorTuning.MaxHnswEfSearch + 1 ] do
                Expect.isError
                    (PgvectorTuning.validate hnswOptions {
                        PgvectorTuning.recommended with
                            SearchWidth = Some bad
                    })
                    (sprintf "hnsw.ef_search = %d is outside pgvector's bounds" bad)

            Expect.isError
                (PgvectorTuning.validate ivfOptions {
                    PgvectorTuning.recommended with
                        SearchWidth = Some 0
                })
                "ivfflat.probes = 0 probes nothing"

            Expect.isOk
                (PgvectorTuning.validate ivfOptions {
                    PgvectorTuning.recommended with
                        SearchWidth = Some 2000
                })
                "probes above hnsw's ceiling are legal for ivfflat"
        }

        test "a search width under NoAnnIndex is accepted and applies nothing" {
            let t = {
                PgvectorTuning.recommended with
                    SearchWidth = Some 5000
            }

            Expect.isOk (PgvectorTuning.validate defaultOptions t) "there is no index for the width to widen"
            Expect.isEmpty (Sql.searchSettings defaultOptions t (Some "0.8.0")) "an exact scan takes no settings"
        }

        test "search concurrency and the warning threshold are bounded" {
            for bad in [ 0; PgvectorTuning.MaxSearchConcurrencyLimit + 1 ] do
                Expect.isError
                    (PgvectorTuning.validate defaultOptions {
                        PgvectorTuning.recommended with
                            MaxSearchConcurrency = bad
                    })
                    (sprintf "MaxSearchConcurrency = %d is refused" bad)

            Expect.isError
                (PgvectorTuning.validate defaultOptions {
                    PgvectorTuning.recommended with
                        ExactScanWarningRows = -1L
                })
                "a negative row threshold is a typo"
        }

        test "createTuned refuses an invalid tuning before any I/O" {
            expectRaisesNaming
                "MaxSearchConcurrency"
                "tuning validation must precede any connection attempt, and name the field"
                (fun () ->
                    createTuned
                        "Host=192.0.2.1;Port=5432;Database=nope;Timeout=1"
                        hnswOptions
                        {
                            PgvectorTuning.recommended with
                                MaxSearchConcurrency = 0
                        }
                        None
                    |> ignore)
        }
    ]

let private searchSettingsTests =
    testList "Phase 892 per-query settings" [
        test "extension versions parse, and iterative scanning starts at 0.8.0" {
            Expect.isTrue (ExtensionVersion.supportsIterativeScan (Some "0.8.0")) "0.8.0 introduced it"
            Expect.isTrue (ExtensionVersion.supportsIterativeScan (Some "0.10.1")) "later minors keep it"
            Expect.isTrue (ExtensionVersion.supportsIterativeScan (Some "1.0")) "a major keeps it"
            Expect.isFalse (ExtensionVersion.supportsIterativeScan (Some "0.7.4")) "0.7 has no iterative scan"
            Expect.isFalse (ExtensionVersion.supportsIterativeScan None) "unknown is treated as unsupported"
            Expect.isFalse (ExtensionVersion.supportsIterativeScan (Some "dev")) "unparseable is unsupported"
        }

        test "hnsw takes ef_search and, when supported, relaxed iterative scanning" {
            let settings =
                Sql.searchSettings hnswOptions PgvectorTuning.recommended (Some "0.8.0")

            Expect.equal
                settings
                [ "hnsw.ef_search", "100"; "hnsw.iterative_scan", "relaxed_order" ]
                "the width and the iterative mode of the configured family"
        }

        test "iterative scanning is never sent to an extension that predates it" {
            let settings =
                Sql.searchSettings hnswOptions PgvectorTuning.recommended (Some "0.7.4")

            Expect.equal settings [ "hnsw.ef_search", "100" ] "0.7 rejects the setting, so it is withheld"
        }

        test "ivfflat takes probes and its own iterative setting" {
            let settings =
                Sql.searchSettings ivfOptions PgvectorTuning.recommended (Some "0.8.0")

            Expect.equal
                settings
                [ "ivfflat.probes", "100"; "ivfflat.iterative_scan", "relaxed_order" ]
                "the IVFFlat family's own settings"
        }

        test "settings are applied transaction-locally and bound as parameters" {
            let sql = Sql.setLocal 2

            Expect.stringContains
                sql
                "set_config(@setting_0, @value_0, true)"
                "is_local = true is SET LOCAL: the setting ends with the query's own transaction"

            Expect.stringContains sql "set_config(@setting_1, @value_1, true)" "one call per setting"

            Expect.isFalse
                (sql.Contains "SET hnsw" || sql.Contains "SET ivfflat")
                "a session-level SET would ride the pooled connection to the next caller"
        }

        test "the index-ordered search orders by distance alone; the exact one keeps the total order" {
            let indexOrdered = Sql.searchIndexOrdered defaultOptions

            Expect.stringContains
                indexOrdered
                "ORDER BY embedding <=> @embedding::vector\nLIMIT"
                "the index-ordered statement ranks by the distance alone"

            Expect.isFalse (indexOrdered.Contains ", chunk_id") "no secondary sort key in the index-ordered statement"
            Expect.stringContains indexOrdered "deleted_at IS NULL" "tombstones stay filtered"

            Expect.stringContains
                (Sql.search defaultOptions)
                ", chunk_id"
                "the two-key statement keeps the chunk_id tie-break"
        }

        test "the fallback's exact statement ranks a materialised scope, so no index can serve its ORDER BY" {
            // Phase 928: the planner serves the two-key `search` from the
            // HNSW index (an Incremental Sort presorted on the distance),
            // so only a statement whose ranking happens AFTER the scope's
            // rows are materialised is exact whatever indexes exist.
            let exact = Sql.searchExact defaultOptions
            Expect.stringContains exact "AS MATERIALIZED" "the scope's rows are materialised before they are ranked"
            Expect.stringContains exact "FROM scoped\nORDER BY" "the ranking reads the materialised rows, not the table"
            Expect.stringContains exact "deleted_at IS NULL" "tombstones stay filtered"
            Expect.stringContains exact ", chunk_id" "equal distances tie-break on chunk_id"
        }
    ]

let private tc content : TextChunk = {
    Content = content
    Metadata = Map.empty
}

let private batchTests =
    testList "Phase 892 batched upsert" [
        test "one statement whose text does not grow with the batch" {
            // The batch is bound as arrays, so the statement is the same
            // text at every size — one statement, one cached plan.
            let sql = Sql.upsertBatch defaultOptions
            Expect.stringContains sql "unnest(@chunk_ids::text[]" "rows arrive as arrays"
            Expect.stringContains sql "@embeddings::real[]" "vectors arrive as one binary float4 array"
            Expect.stringContains sql Sql.ScopeConflictTarget "the conflict target is the composite key"
            Expect.stringContains sql "deleted_at = NULL" "re-upserting clears a tombstone, as Upsert does"

            Expect.equal
                (sql.Split(';', StringSplitOptions.RemoveEmptyEntries)
                 |> Array.filter (fun s -> s.Trim() <> "")
                 |> Array.length)
                1
                "exactly one statement"
        }

        test "prepare flattens every size into one set of arrays" {
            let options = { defaultOptions with Dimensions = 4 }

            for size in [ 1; 10; 1000 ] do
                let chunks =
                    List.init size (fun i -> sprintf "c-%04d" i, Array.init 4 (fun j -> float32 (i + j + 1)), tc "x")

                let prepared = BatchUpsert.prepare options chunks
                Expect.equal prepared.ChunkIds.Length size "one row per chunk"
                Expect.equal prepared.Embeddings.Length (size * 4) "one flat array of size × dimensions"
                Expect.equal prepared.Contents.Length size "contents align with ids"
                Expect.equal prepared.Metadata.Length size "metadata aligns with ids"
        }

        test "a repeated chunk id keeps its position and its LAST value" {
            let options = { defaultOptions with Dimensions = 2 }

            let prepared =
                BatchUpsert.prepare options [
                    "a", [| 1.0f; 0.0f |], tc "first a"
                    "b", [| 0.0f; 1.0f |], tc "b"
                    "a", [| 0.0f; 1.0f |], tc "second a"
                ]

            Expect.equal prepared.ChunkIds [| "a"; "b" |] "one row per distinct id, first-seen order"
            Expect.equal prepared.Contents [| "second a"; "b" |] "the later upsert wins, as sequential writes would"
            Expect.equal prepared.Embeddings[0..1] [| 0.0f; 1.0f |] "the later vector wins too"
        }

        test "vectors are unit-normalised and the tombstone key is stripped" {
            let options = { defaultOptions with Dimensions = 2 }

            let chunk = {
                Content = "x"
                Metadata = Map.ofList [ ChunkMetadata.DeletedAtKey, "2026-01-01T00:00:00Z"; "tag", "t" ]
            }

            let prepared = BatchUpsert.prepare options [ "a", [| 3.0f; 4.0f |], chunk ]

            Expect.floatClose Accuracy.medium (float prepared.Embeddings[0]) 0.6 "3/5"
            Expect.floatClose Accuracy.medium (float prepared.Embeddings[1]) 0.8 "4/5"

            Expect.isFalse
                (prepared.Metadata[0].Contains ChunkMetadata.DeletedAtKey)
                "the tombstone lives in the column, never in the metadata"

            Expect.stringContains prepared.Metadata[0] "\"tag\"" "other metadata survives"
        }

        test "a wrong-dimension vector refuses the whole batch, naming the chunk" {
            let options = { defaultOptions with Dimensions = 2 }

            expectRaisesNaming "'bad'" "nothing is written when one vector does not fit the column" (fun () ->
                BatchUpsert.prepare options [ "ok", [| 1.0f; 0.0f |], tc "ok"; "bad", [| 1.0f |], tc "bad" ]
                |> ignore)
        }

        testCaseAsync "upsertBatch reaches IVectorStoreBatch in one call when the store implements it"
        <| async {
            let singles = ResizeArray<string>()
            let batches = ResizeArray<int>()

            let store =
                { new IVectorStore with
                    member _.Upsert _ chunkId _ _ = async { singles.Add chunkId }
                    member _.Search _ _ _ = async { return [] }
                    member _.ListChunks _ _ = async { return [] }
                    member _.DeleteChunk _ _ = async { return () }
                    member _.RestoreChunk _ _ = async { return () }
                    member _.Vacuum _ _ = async { return 0 }
                    member _.DeleteByScope _ = async { return () }
                    member _.ListScopes() = async { return [] }

                    member _.Erase(_, _, _, _) = async {
                        return Result.Error(ErasureError.StoreUnreachable("fake", "unused"))
                    }
                  interface IVectorStoreBatch with
                      member _.UpsertBatch _ chunks = async { batches.Add chunks.Length }
                }

            let chunks = List.init 25 (fun i -> sprintf "c-%d" i, [| 1.0f |], tc "x")
            do! upsertBatch store (Team "T") chunks

            Expect.equal (List.ofSeq batches) [ 25 ] "one batch call carrying every chunk"
            Expect.isEmpty singles "no per-chunk writes when the batch seam is present"
        }

        testCaseAsync "upsertBatch falls back to ordered single upserts on a store without the seam"
        <| async {
            let singles = ResizeArray<string>()

            let store =
                { new IVectorStore with
                    member _.Upsert _ chunkId _ _ = async { singles.Add chunkId }
                    member _.Search _ _ _ = async { return [] }
                    member _.ListChunks _ _ = async { return [] }
                    member _.DeleteChunk _ _ = async { return () }
                    member _.RestoreChunk _ _ = async { return () }
                    member _.Vacuum _ _ = async { return 0 }
                    member _.DeleteByScope _ = async { return () }
                    member _.ListScopes() = async { return [] }

                    member _.Erase(_, _, _, _) = async {
                        return Result.Error(ErasureError.StoreUnreachable("fake", "unused"))
                    }
                }

            do! upsertBatch store (Team "T") [ "a", [| 1.0f |], tc "a"; "b", [| 1.0f |], tc "b" ]
            Expect.equal (List.ofSeq singles) [ "a"; "b" ] "an existing store keeps working, chunk by chunk, in order"
        }
    ]

let private diagnosticsOf options tuning atCreate now methods rows : PgvectorDiagnostics = {
    Options = options
    Tuning = tuning
    ExtensionVersionAtCreate = atCreate
    ExtensionVersion = now
    AnnIndexMethods = methods
    DatabaseHnswEfSearch = Some "40"
    DatabaseIvfFlatProbes = Some "1"
    RowEstimate = rows
}

let private diagnosticsTests =
    testList "Phase 892 health + preflight" [
        test "the report names the index kind, the width in force and the extension version" {
            let d =
                diagnosticsOf hnswOptions PgvectorTuning.recommended (Some "0.8.0") (Some "0.8.0") [ "hnsw" ] 1234L

            let line = Health.describe d
            Expect.stringContains line "pgvector 0.8.0" "the extension version"
            Expect.stringContains line "hnsw (m=16, ef_construction=64) [present]" "the index kind, and that it exists"
            Expect.stringContains line "hnsw.ef_search = 100 (per query)" "the tuned width"
            Expect.stringContains line "iterative scan: relaxed_order" "iterative scanning applied"
            Expect.equal (Health.assess d) HealthChecks.Healthy "nothing to fix"
        }

        test "an untuned width reports the database default" {
            let d =
                diagnosticsOf hnswOptions PgvectorTuning.unchanged (Some "0.8.0") (Some "0.8.0") [ "hnsw" ] 0L

            Expect.stringContains (Health.describe d) "hnsw.ef_search = 40 (database default)" "the default in force"
        }

        test "iterative scanning requested on an old extension is Degraded, not Unhealthy" {
            let d =
                diagnosticsOf hnswOptions PgvectorTuning.recommended (Some "0.7.4") (Some "0.7.4") [ "hnsw" ] 0L

            match Health.assess d with
            | HealthChecks.Degraded message ->
                Expect.stringContains message "not applied" "says what is missing"
                Expect.stringContains message "pgvector 0.7.4" "and carries the whole report"
            | other -> failtestf "expected Degraded, got %A" other
        }

        test "a configured index absent from the table is Degraded" {
            let d =
                diagnosticsOf hnswOptions PgvectorTuning.recommended (Some "0.8.0") (Some "0.8.0") [] 0L

            match Health.assess d with
            | HealthChecks.Degraded message -> Expect.stringContains message "absent" "names the missing index"
            | other -> failtestf "expected Degraded, got %A" other
        }

        test "an extension upgraded since create is Degraded until restart" {
            let d =
                diagnosticsOf hnswOptions PgvectorTuning.recommended (Some "0.7.4") (Some "0.8.0") [ "hnsw" ] 0L

            match Health.assess d with
            | HealthChecks.Degraded message -> Expect.stringContains message "restart" "names the operator action"
            | other -> failtestf "expected Degraded, got %A" other
        }

        test "an exact-scan store is Healthy whatever its tuning" {
            let d =
                diagnosticsOf defaultOptions PgvectorTuning.recommended (Some "0.7.4") (Some "0.7.4") [] 10L

            Expect.equal (Health.assess d) HealthChecks.Healthy "no index, so nothing about an index can be wrong"
            Expect.stringContains (Health.describe d) "search width: n/a (exact scan)" "the width does not apply"
        }

        test "the validator warns above the row threshold with no approximate index, and only then" {
            let above =
                diagnosticsOf defaultOptions PgvectorTuning.unchanged None None [] 500_001L

            match Health.exactScanVerdict above with
            | ConfigValidation.ValidationResult.Warning message ->
                Expect.stringContains message "500001" "names the row estimate"
                Expect.stringContains message "HnswAnnIndex" "names the remedy"
            | other -> failtestf "expected a Warning, got %A" other

            let atThreshold =
                diagnosticsOf defaultOptions PgvectorTuning.unchanged None None [] 500_000L

            Expect.equal
                (Health.exactScanVerdict atThreshold)
                ConfigValidation.ValidationResult.Ok
                "at the threshold is not above it"

            let indexed =
                diagnosticsOf hnswOptions PgvectorTuning.unchanged None None [ "hnsw" ] 50_000_000L

            Expect.equal
                (Health.exactScanVerdict indexed)
                ConfigValidation.ValidationResult.Ok
                "an approximate index answers the warning"
        }
    ]

let private structuralTests =
    testList "structural (no database required)" [
        scopeIsolationTests
        schemaTests
        optionGuardTests
        codecTests
        tuningTests
        searchSettingsTests
        batchTests
        diagnosticsTests
    ]

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

/// Phase 892 — a tuned store over a fresh table. Returns the table name so
/// a case can open a second store (or a raw command) against it.
let private makeTunedStore
    (connectionString: string)
    (dimensions: int)
    (annIndex: PgvectorAnnIndex)
    (tuning: PgvectorTuning)
    : IVectorStore * IDisposable * string =
    let table = freshTableName ()

    let options = {
        PgvectorOptions.forDimensions dimensions with
            Table = table
            AnnIndex = annIndex
    }

    let store =
        createTuned connectionString options tuning (Some(SilentLogger() :> ILogger))

    let cleanup =
        { new IDisposable with
            member _.Dispose() =
                (store :?> IDisposable).Dispose()
                dropTable connectionString table
        }

    store, cleanup, table

/// Deterministic pseudo-random vectors — the same corpus every run.
let private randomVectors (seed: int) (count: int) (dimensions: int) : float32 array list =
    let rng = Random seed
    List.init count (fun _ -> Array.init dimensions (fun _ -> float32 (rng.NextDouble() * 2.0 - 1.0)))

let private cosine (a: float32 array) (b: float32 array) =
    let dot = Array.fold2 (fun acc x y -> acc + float x * float y) 0.0 a b

    let norm (v: float32 array) =
        sqrt (v |> Array.sumBy (fun x -> float x * float x))

    dot / (norm a * norm b)

/// EXPLAIN a search statement with the planner steered off sequential
/// scans, so the plan shows whether an index CAN serve the statement.
let private explain (connectionString: string) (sql: string) (scopeKey: string) (query: float32 array) (topK: int) =
    use dataSource = NpgsqlDataSource.Create connectionString
    use conn = dataSource.OpenConnection()
    use tx = conn.BeginTransaction()

    use off = new NpgsqlCommand("SET LOCAL enable_seqscan = off;", conn, tx)
    off.ExecuteNonQuery() |> ignore

    use cmd = new NpgsqlCommand("EXPLAIN " + sql, conn, tx)
    cmd.Parameters.AddWithValue("scope", scopeKey) |> ignore

    cmd.Parameters.AddWithValue("embedding", Vector.toLiteral (Vector.normalise query))
    |> ignore

    cmd.Parameters.AddWithValue("top_k", topK) |> ignore
    use reader = cmd.ExecuteReader()
    let lines = ResizeArray<string>()

    while reader.Read() do
        lines.Add(reader.GetString 0)

    String.concat "\n" lines

/// Refresh the planner statistics for a freshly written table, as
/// autovacuum would in production — a plan read over a never-analysed
/// table is a plan nobody runs.
let private analyze (connectionString: string) (table: string) =
    use dataSource = NpgsqlDataSource.Create connectionString
    use cmd = dataSource.CreateCommand(sprintf "ANALYZE %s;" table)
    cmd.ExecuteNonQuery() |> ignore

/// The plan the planner chooses on its own (nothing steered), for the
/// measurement record.
let private naturalPlan (connectionString: string) (sql: string) (scopeKey: string) (query: float32 array) (topK: int) =
    use dataSource = NpgsqlDataSource.Create connectionString
    use cmd = dataSource.CreateCommand("EXPLAIN " + sql)
    cmd.Parameters.AddWithValue("scope", scopeKey) |> ignore

    cmd.Parameters.AddWithValue("embedding", Vector.toLiteral (Vector.normalise query))
    |> ignore

    cmd.Parameters.AddWithValue("top_k", topK) |> ignore
    use reader = cmd.ExecuteReader()
    let lines = ResizeArray<string>()

    while reader.Read() do
        lines.Add(reader.GetString 0)

    String.concat "\n" lines

/// How many rows a search statement returns at the database's default
/// settings — the page the store sees BEFORE any fallback.
let private rawCount (connectionString: string) (sql: string) (scopeKey: string) (query: float32 array) (topK: int) =
    use dataSource = NpgsqlDataSource.Create connectionString
    use cmd = dataSource.CreateCommand sql
    cmd.Parameters.AddWithValue("scope", scopeKey) |> ignore

    cmd.Parameters.AddWithValue("embedding", Vector.toLiteral (Vector.normalise query))
    |> ignore

    cmd.Parameters.AddWithValue("top_k", topK) |> ignore
    use reader = cmd.ExecuteReader()
    let mutable n = 0

    while reader.Read() do
        n <- n + 1

    n

/// Phase 892 live cases — the reproduction, the index-use confirmation,
/// recall against the exact scan, the batched write and the tuned search
/// paths. Printed figures are the measurement record the README cites.
let private phase892LiveTests (connectionString: string) =
    testList "Phase 892 (live)" [
        testCaseAsync
            "reproduction: small and mid-size scopes in a large shared table, approximate index, default width"
        <| async {
            // The unmitigated shape: the index may serve the ORDER BY,
            // nothing widens the scan, no fallback. What comes back is
            // RECORDED, not asserted — it is the measurement, and the
            // mitigated cases below are the gate. Phase 928 ran it against
            // pgvector 0.8.6: the planner answers a 20-chunk scope from the
            // `(scope)` index (exact, full page), and the exposed band is a
            // scope large enough for the planner to route it to the
            // approximate index yet too small to fill its candidate list.
            let bare = PgvectorTuning.unchanged

            let store, dispose, table =
                makeTunedStore connectionString 16 (HnswAnnIndex(16, 64)) bare

            try
                do!
                    upsertBatch
                        store
                        (Team "big")
                        (randomVectors 892 5000 16
                         |> List.mapi (fun i v -> sprintf "big-%05d" i, v, chunk "big" "b"))

                let small = randomVectors 893 20 16
                let mid = randomVectors 894 250 16

                do!
                    upsertBatch
                        store
                        (Team "small")
                        (small |> List.mapi (fun i v -> sprintf "small-%03d" i, v, chunk "small" "s"))

                do!
                    upsertBatch
                        store
                        (Team "mid")
                        (mid |> List.mapi (fun i v -> sprintf "mid-%03d" i, v, chunk "mid" "m"))

                analyze connectionString table
                let options = { defaultOptions with Table = table }

                for name, vectors in [ "small", small; "mid", mid ] do
                    let query = vectors.Head
                    let! results = store.Search [ Team name ] query 10

                    let plan =
                        naturalPlan connectionString (Sql.searchIndexOrdered options) ("team:" + name) query 10

                    printfn
                        "[Phase 928 reproduction] %s scope (%d chunks) in a 5270-row table, hnsw(16,64), default ef_search, no iterative scan, no fallback: top-10 returned %d rows.\nPlan:\n%s"
                        name
                        vectors.Length
                        results.Length
                        plan

                    Expect.all results (fun m -> m.Scope = Team name) "isolation holds whatever the count"
            finally
                dispose.Dispose()
        }

        testCaseAsync "a small scope in a large shared table is returned a full top-k (recommended tuning)"
        <| async {
            let store, dispose, _ =
                makeTunedStore connectionString 16 (HnswAnnIndex(16, 64)) PgvectorTuning.recommended

            try
                do!
                    upsertBatch
                        store
                        (Team "big")
                        (randomVectors 892 5000 16
                         |> List.mapi (fun i v -> sprintf "big-%05d" i, v, chunk "big" "b"))

                let small = randomVectors 893 20 16

                do!
                    upsertBatch
                        store
                        (Team "small")
                        (small |> List.mapi (fun i v -> sprintf "small-%03d" i, v, chunk "small" "s"))

                for topK in [ 5; 10; 20 ] do
                    let! results = store.Search [ Team "small" ] small.Head topK
                    Expect.hasLength results topK (sprintf "the small scope holds 20 chunks, so top-%d is full" topK)
                    Expect.all results (fun m -> m.Scope = Team "small") "and every match is the small scope's"

                let! overAsk = store.Search [ Team "small" ] small.Head 50
                Expect.hasLength overAsk 20 "asking for more than the scope holds returns all of it"
            finally
                dispose.Dispose()
        }

        testCaseAsync "EXPLAIN: the index serves both ORDER BY shapes, and never the fallback's exact statement"
        <| async {
            // Phase 892 read the planner and concluded the two-key ORDER BY
            // (distance, then chunk_id) could not be served from the
            // approximate index. Phase 928 ran the plans on pgvector 0.8.6 /
            // PostgreSQL 17 and REFUTED it: the planner serves it through an
            // Incremental Sort presorted on the distance. So `search` is
            // approximate once an index exists, and the fallback needs a
            // statement no index can serve — `searchExact`.
            let store, dispose, table =
                makeTunedStore connectionString 16 (HnswAnnIndex(16, 64)) PgvectorTuning.recommended

            try
                do!
                    upsertBatch
                        store
                        (Team "T")
                        (randomVectors 7 2000 16
                         |> List.mapi (fun i v -> sprintf "c-%04d" i, v, chunk "c" "x"))

                analyze connectionString table
                let options = { defaultOptions with Table = table }
                let query = (randomVectors 8 1 16).Head
                let indexName = table + "_embedding_hnsw_idx"

                let distanceOnlyPlan =
                    explain connectionString (Sql.searchIndexOrdered options) "team:T" query 10

                let twoKeyPlan = explain connectionString (Sql.search options) "team:T" query 10
                let exactPlan = explain connectionString (Sql.searchExact options) "team:T" query 10

                printfn
                    "[Phase 928 ordering] distance-only plan:\n%s\ntwo-key plan:\n%s\nexact (fallback) plan:\n%s"
                    distanceOnlyPlan
                    twoKeyPlan
                    exactPlan

                Expect.stringContains distanceOnlyPlan indexName "the index serves a single distance sort key"

                Expect.stringContains
                    twoKeyPlan
                    indexName
                    "the index serves the two-key ORDER BY too, so `search` is approximate once an index exists"

                Expect.stringContains
                    twoKeyPlan
                    "Incremental Sort"
                    "the chunk_id tie-break is applied by an Incremental Sort over the index-ordered rows"

                // The falsifier the full top-k guarantee rests on.
                Expect.isFalse
                    (exactPlan.Contains indexName)
                    "the fallback's statement must never be served from the approximate index"
            finally
                dispose.Dispose()
        }

        testCaseAsync "a starved page is repaired exactly by the fallback, whichever ORDER BY produced it"
        <| async {
            // Default width, no iterative scan, the two-key statement: the
            // shape Phase 892 believed was an exact scan. The fallback alone
            // must turn its short page into the exact top-k.
            //
            // A table this size is answered from the (scope) index unless
            // the planner is steered, so every connection here carries
            // enable_sort = off: it routes the scope's page to the
            // approximate index, as the planner chose unprompted for 2,000-
            // to 20,000-chunk scopes of a 165,000-row table in Phase 928's
            // measurement. The exact statement still sorts, because nothing
            // else can serve it, which is the point.
            let steered =
                let b = NpgsqlConnectionStringBuilder connectionString
                b.Options <- "-c enable_sort=off"
                b.ConnectionString

            let tuning = {
                PgvectorTuning.unchanged with
                    ExactFallbackOnShortPage = true
            }

            let store, dispose, table = makeTunedStore steered 16 (HnswAnnIndex(16, 64)) tuning

            try
                do!
                    upsertBatch
                        store
                        (Team "big")
                        (randomVectors 892 5000 16
                         |> List.mapi (fun i v -> sprintf "big-%05d" i, v, chunk "big" "b"))

                let mid = randomVectors 894 250 16 |> List.mapi (fun i v -> sprintf "mid-%03d" i, v)

                do! upsertBatch store (Team "mid") (mid |> List.map (fun (id, v) -> id, v, chunk "mid" "m"))
                analyze steered table
                let options = { defaultOptions with Table = table }
                let query = (randomVectors 895 1 16).Head
                let starved = rawCount steered (Sql.search options) "team:mid" query 10
                let plan = naturalPlan steered (Sql.search options) "team:mid" query 10

                printfn
                    "[Phase 928 fallback] 250-chunk scope in a 5250-row table, two-key statement, default ef_search, planner steered to the index: the page before the fallback held %d of 10 rows.\nPlan:\n%s"
                    starved
                    plan

                // The case's own premise: if the corpus stops starving, it
                // proves nothing about the fallback, so it says so.
                Expect.isLessThan starved 10 "precondition: the two-key statement's page is short at the default width"

                let truth =
                    mid
                    |> List.sortBy (fun (id, v) -> -(cosine query v), id)
                    |> List.truncate 10
                    |> List.map fst

                let! results = store.Search [ Team "mid" ] query 10
                Expect.equal (results |> List.map _.ChunkId) truth "the fallback returns the exact top-10, in order"
            finally
                dispose.Dispose()
        }

        testCaseAsync "recall against the exact scan at the recommended settings"
        <| async {
            let store, dispose, _ =
                makeTunedStore connectionString 32 (HnswAnnIndex(16, 64)) PgvectorTuning.recommended

            try
                let corpus =
                    randomVectors 42 5000 32 |> List.mapi (fun i v -> sprintf "c-%05d" i, v)

                do! upsertBatch store (Team "T") (corpus |> List.map (fun (id, v) -> id, v, chunk "c" "x"))

                let queries = randomVectors 43 50 32
                let k = 10
                let mutable hits = 0
                let timings = ResizeArray<float>()

                for q in queries do
                    let truth =
                        corpus
                        |> List.sortBy (fun (id, v) -> -(cosine q v), id)
                        |> List.truncate k
                        |> List.map fst
                        |> Set.ofList

                    let clock = Diagnostics.Stopwatch.StartNew()
                    let! got = store.Search [ Team "T" ] q k
                    timings.Add clock.Elapsed.TotalMilliseconds
                    hits <- hits + (got |> List.filter (fun m -> truth.Contains m.ChunkId) |> List.length)

                let recall = float hits / float (k * queries.Length)
                let sorted = timings |> Seq.sort |> Array.ofSeq

                // Phase 928: the store-path latency beside the recall — one
                // `Search` call, client round-trip included (the settings
                // statement, the search and the commit on one connection).
                printfn
                    "[Phase 892 recall] 5000 x 32-dim, hnsw(16,64), ef_search=100, iterative relaxed: recall@10 = %.3f over %d queries; Search latency mean %.2f ms, p95 %.2f ms"
                    recall
                    queries.Length
                    (Array.average sorted)
                    sorted[int (float sorted.Length * 0.95) - 1]

                Expect.isGreaterThanOrEqual recall 0.9 "the recommended settings keep recall@10 at or above 0.9"
            finally
                dispose.Dispose()
        }

        testCaseAsync "a batched upsert writes, replaces and clears tombstones like single upserts"
        <| async {
            let store, dispose, _ =
                makeTunedStore connectionString 8 NoAnnIndex PgvectorTuning.recommended

            try
                let batch =
                    List.init 300 (fun i -> sprintf "c-%03d" i, eightDim (i % 8), chunk "v1" "x")

                do! upsertBatch store (Team "T") batch

                let! all = store.ListChunks (Team "T") false
                Expect.hasLength all 300 "every chunk of the batch is written"

                do! store.DeleteChunk (Team "T") "c-000"

                do!
                    upsertBatch store (Team "T") [
                        "c-000", eightDim 0, chunk "v2" "x"
                        "c-001", eightDim 1, chunk "v2" "x"
                        "c-001", eightDim 1, chunk "v3" "x"
                    ]

                let! after = store.ListChunks (Team "T") false
                let byId = after |> Map.ofList
                Expect.hasLength after 300 "replacing never duplicates"
                Expect.equal byId["c-000"].Content "v2" "a batched write clears the tombstone and replaces the content"
                Expect.equal byId["c-001"].Content "v3" "the later entry for a repeated id wins"

                let! other = store.ListChunks (Team "U") true
                Expect.isEmpty other "the batch wrote into its own scope only"
            finally
                dispose.Dispose()
        }

        testCaseAsync "concurrent multi-scope search returns exactly what sequential search returns"
        <| async {
            let concurrent, dispose, table =
                makeTunedStore connectionString 8 NoAnnIndex {
                    PgvectorTuning.unchanged with
                        MaxSearchConcurrency = 4
                }

            // Phase 939: `create` composes the recommended posture, whose
            // search is concurrent too, so the sequential reference is named.
            let sequential =
                createTuned
                    connectionString
                    {
                        PgvectorOptions.forDimensions 8 with
                            Table = table
                    }
                    PgvectorTuning.unchanged
                    (Some(SilentLogger() :> ILogger))

            try
                let scopes = [ for s in 1..6 -> Team(sprintf "t%d" s) ]

                for scope in scopes do
                    do!
                        upsertBatch
                            concurrent
                            scope
                            (List.init 20 (fun i -> sprintf "c-%02d" i, eightDim (i % 8), chunk "c" "x"))

                let! a = concurrent.Search scopes (eightDim 3) 15
                let! b = sequential.Search scopes (eightDim 3) 15

                Expect.equal
                    (a |> List.map (fun m -> m.Scope, m.ChunkId))
                    (b |> List.map (fun m -> m.Scope, m.ChunkId))
                    "concurrency changes latency, never the answer"
            finally
                (sequential :?> IDisposable).Dispose()
                dispose.Dispose()
        }

        testCaseAsync "the posture read reports the extension, the index and the width"
        <| async {
            let store, dispose, _ =
                makeTunedStore connectionString 8 (HnswAnnIndex(16, 64)) PgvectorTuning.recommended

            try
                let pg = store :?> PgvectorVectorStore
                let! d = pg.Diagnose()
                Expect.isSome d.ExtensionVersion "the installed extension version is read"
                Expect.contains d.AnnIndexMethods "hnsw" "the index built at create is found on the table"

                let! line = Health.report store
                printfn "[Phase 892 posture] %s" line
                Expect.stringContains line "hnsw.ef_search = 100 (per query)" "the width in force is reported"

                let! health = (Health.create store).Check()

                match health with
                | HealthChecks.Unhealthy message -> failtestf "a reachable store is not Unhealthy: %s" message
                | _ -> ()
            finally
                dispose.Dispose()
        }
    ]

/// Phase 939 live cases — `create`, with nothing tuned, over a configured
/// approximate index: the starved mid-sized scope Phase 928 measured, now
/// answered in full and exactly.
let private phase939LiveTests (connectionString: string) =
    testList "Phase 939 (live)" [
        testCaseAsync "create over an HNSW index returns the exact top-k for a starved mid-sized scope"
        <| async {
            // The same corpus and the same planner steering as the Phase 928
            // fallback case: a 250-chunk scope in a 5,250-row table, routed
            // to the approximate index. Built with plain `create` — no
            // tuning named — because that is what a deployment composes.
            let steered =
                let b = NpgsqlConnectionStringBuilder connectionString
                b.Options <- "-c enable_sort=off"
                b.ConnectionString

            let table = freshTableName ()

            let options = {
                PgvectorOptions.forDimensions 16 with
                    Table = table
                    AnnIndex = HnswAnnIndex(16, 64)
            }

            let store = create steered options (Some(SilentLogger() :> ILogger))

            try
                do!
                    upsertBatch
                        store
                        (Team "big")
                        (randomVectors 892 5000 16
                         |> List.mapi (fun i v -> sprintf "big-%05d" i, v, chunk "big" "b"))

                let mid = randomVectors 894 250 16 |> List.mapi (fun i v -> sprintf "mid-%03d" i, v)

                do! upsertBatch store (Team "mid") (mid |> List.map (fun (id, v) -> id, v, chunk "mid" "m"))
                analyze steered table
                let query = (randomVectors 895 1 16).Head

                // The case's own premise: at pgvector's defaults the index
                // path starves this scope. If the corpus stops starving, the
                // case proves nothing about the default, so it says so.
                let starved = rawCount steered (Sql.search options) "team:mid" query 10
                printfn "[Phase 939 red] the untuned index path returned %d of 10 rows for the 250-chunk scope" starved
                Expect.isLessThan starved 10 "precondition: pgvector's default posture starves this scope"

                let truth =
                    mid
                    |> List.sortBy (fun (id, v) -> -(cosine query v), id)
                    |> List.truncate 10
                    |> List.map fst

                let! results = store.Search [ Team "mid" ] query 10
                Expect.equal (results |> List.map _.ChunkId) truth "create returns the exact top-10, in order"
            finally
                (store :?> IDisposable).Dispose()
                dropTable connectionString table
        }

        testCaseAsync "create and createWithDataSource compose the recommended tuning and read the extension version"
        <| async {
            let table = freshTableName ()

            let options = {
                PgvectorOptions.forDimensions 8 with
                    Table = table
                    AnnIndex = HnswAnnIndex(16, 64)
            }

            let logger = Some(SilentLogger() :> ILogger)
            let owned = create connectionString options logger
            use dataSource = NpgsqlDataSource.Create connectionString
            let borrowed = createWithDataSource dataSource options logger

            try
                for store in [ owned; borrowed ] do
                    let pg = store :?> PgvectorVectorStore
                    Expect.equal pg.Tuning PgvectorTuning.recommended "the default posture is the recommended one"

                    Expect.isTrue
                        (ExtensionVersion.supportsIterativeScan pg.ExtensionVersionAtCreate)
                        "the extension version is read at create, so iterative scanning is applied on 0.8.0+"

                    let! line = Health.report store
                    Expect.stringContains line "hnsw.ef_search = 100 (per query)" "the raised width is in force"
                    Expect.stringContains line "index-ordered, page re-sorted" "the one ANN statement shape"
            finally
                (owned :?> IDisposable).Dispose()
                (borrowed :?> IDisposable).Dispose()
                dropTable connectionString table
        }

        testCaseAsync "create under NoAnnIndex stays exact and applies no per-query settings"
        <| async {
            let store, dispose = makeStoreWith connectionString 8 ()

            try
                let pg = store :?> PgvectorVectorStore
                Expect.equal pg.Tuning PgvectorTuning.recommended "the same default posture"

                Expect.isEmpty
                    (Sql.searchSettings pg.Options pg.Tuning pg.ExtensionVersionAtCreate)
                    "an exact scan takes no settings, so search runs outside any transaction as before"

                do!
                    upsertBatch
                        store
                        (Team "T")
                        (List.init 40 (fun i -> sprintf "c-%02d" i, eightDim (i % 8), chunk "c" "x"))

                let! results = store.Search [ Team "T" ] (eightDim 2) 5
                Expect.hasLength results 5 "a full page"
                Expect.all results (fun m -> m.Score > 0.99) "the five exact matches of the queried axis"
            finally
                dispose.Dispose()
        }
    ]


// ─── Phase 964 — an IVFFlat index is not built on an empty table ─────

/// 32-dimension vectors clustered around `topics` shared centres, as Phase
/// 939's IVFFlat measurement built them: IVFFlat's k-means needs structure
/// to find, and uniform noise has none.
let private clusteredVectors (seed: int) (count: int) (topics: int) : float32 array list =
    let rng = Random seed

    let centres =
        Array.init topics (fun _ -> Array.init 32 (fun _ -> rng.NextDouble() * 2.0 - 1.0))

    List.init count (fun i ->
        let c = centres[i % topics]
        Array.init 32 (fun d -> float32 (c[d] + (rng.NextDouble() - 0.5) * 0.2)))

/// IVFFlat at pgvector's default width (`probes` 1), no iterative scan and
/// no exact fallback, so recall measures the index alone.
let private probesOne: PgvectorTuning = {
    PgvectorTuning.unchanged with
        SearchWidth = Some 1
}

let private ivfIndexExists (connectionString: string) (table: string) : bool =
    use dataSource = NpgsqlDataSource.Create connectionString

    use cmd =
        dataSource.CreateCommand
            "SELECT count(*) FROM pg_indexes WHERE tablename = @t AND indexdef LIKE '%USING ivfflat%';"

    cmd.Parameters.AddWithValue("t", table) |> ignore
    Convert.ToInt64(cmd.ExecuteScalar()) > 0L

let private recallOf (store: IVectorStore) (corpus: (string * float32 array) list) (queries: float32 array list) = async {
    let k = 10
    let mutable hits = 0

    for q in queries do
        let truth =
            corpus
            |> List.sortBy (fun (id, v) -> -(cosine q v), id)
            |> List.truncate k
            |> List.map fst
            |> Set.ofList

        let! got = store.Search [ Team "T" ] q k
        hits <- hits + (got |> List.filter (fun m -> truth.Contains m.ChunkId) |> List.length)

    return float hits / float (k * queries.Length)
}

let private phase964LiveTests (connectionString: string) =
    testList "Phase 964 (live)" [
        testCaseAsync
            "create does not build an IVFFlat index on an empty table, and builds it once the table holds data"
        <| async {
            let lists = 100

            let corpus =
                clusteredVectors 964 10_000 lists
                |> List.mapi (fun i v -> sprintf "c-%05d" i, v)

            let queries = clusteredVectors 965 30 lists

            // A one-scope table this size is answered by an exact sort unless
            // the planner is steered, and an exact sort measures no index;
            // enable_sort = off routes the page to the IVFFlat index, as the
            // fallback case above does for HNSW.
            let connectionString =
                let b = NpgsqlConnectionStringBuilder connectionString
                b.Options <- "-c enable_sort=off"
                b.ConnectionString

            // The reference: the index built after the load, as the README
            // advises and as 939 measured it.
            let reference, disposeReference, referenceTable =
                makeTunedStore connectionString 32 NoAnnIndex probesOne

            // The store under test: AutoMigrate with IVFFlat, at first start,
            // on an empty table — the shape that used to build the index empty.
            let store, dispose, table =
                makeTunedStore connectionString 32 (IvfFlatAnnIndex lists) probesOne

            try
                let builtEmpty = ivfIndexExists connectionString table

                let rows = corpus |> List.map (fun (id, v) -> id, v, chunk "c" "x")
                do! upsertBatch reference (Team "T") rows
                do! upsertBatch store (Team "T") rows

                do! async {
                    use dataSource = NpgsqlDataSource.Create connectionString

                    use cmd =
                        dataSource.CreateCommand(
                            sprintf
                                "CREATE INDEX ON %s USING ivfflat (embedding vector_cosine_ops) WITH (lists = %d);"
                                referenceTable
                                lists
                        )

                    let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                    ()
                }

                let! afterLoad = recallOf reference corpus queries
                let! atFirstStart = recallOf store corpus queries

                // A second `create` — a restart — over the loaded table.
                let restarted =
                    createTuned
                        connectionString
                        {
                            PgvectorOptions.forDimensions 32 with
                                Table = table
                                AnnIndex = IvfFlatAnnIndex lists
                        }
                        probesOne
                        (Some(SilentLogger() :> ILogger))

                try
                    Expect.isTrue (ivfIndexExists connectionString table) "a restart over the loaded table builds it"
                    let! afterRestart = recallOf restarted corpus queries

                    printfn
                        "[Phase 964 IVFFlat] 10,000 x 32-dim, %d topics, lists = %d, probes = 1: recall@10 index built after the load = %.3f; store from a first start on the empty table = %.3f; after a restart over the loaded table = %.3f (built at first start: %b)"
                        lists
                        lists
                        afterLoad
                        atFirstStart
                        afterRestart
                        builtEmpty

                    Expect.isFalse
                        builtEmpty
                        "create on an empty table defers the IVFFlat build (pinned red: it was built)"

                    Expect.isGreaterThanOrEqual
                        atFirstStart
                        (afterLoad - 0.05)
                        "a store created on an empty table answers at least as well as an index built after the load (pinned red: an index built empty did not)"

                    Expect.isGreaterThanOrEqual
                        afterRestart
                        (afterLoad - 0.05)
                        "the index a restart builds is the after-load index"
                finally
                    (restarted :?> IDisposable).Dispose()
            finally
                dispose.Dispose()
                disposeReference.Dispose()
        }
    ]

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

        phase892LiveTests connectionString
        phase939LiveTests connectionString
        phase964LiveTests connectionString
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