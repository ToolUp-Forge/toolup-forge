// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.VectorStores.Pgvector.PgvectorVectorStore

open System
open System.Globalization
open System.Text
open System.Text.Json
open Npgsql
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore

// ─── Phase 507 — PostgreSQL + pgvector IVectorStore ──────────────────
//
// The top rung of the documented RAG scale story:
//
//   InMemoryVectorStore   < ~50k chunks   (flat scan, blob-persisted)
//   HnswVectorStore       < ~1M chunks    (in-process ANN graph)
//   PgvectorVectorStore   external DB     (durable, multi-replica-safe)
//
// Beyond corpus scale, the reason to reach for this companion is that it
// removes the SINGLE-PROCESS ceiling the in-tree stores carry. Both of
// them hold their index in process memory and persist it asynchronously,
// so two replicas of the same deployment each own a private index and a
// chunk ingested on replica A is invisible to replica B until a flush +
// reload cycle. Here the index IS the database: every replica reads and
// writes the same rows, so retrieval is consistent across replicas with
// no per-process index state at all.
//
// **GP 1 — vendor isolation.** The Npgsql dependency lives in this
// companion and never reaches `ToolUp.Platform.*`. Nothing pgvector-shaped
// crosses the `IVectorStore` boundary; a deployment swaps stores by
// changing one composition line.
//
// **GP 4 — structural scope isolation.** Scope is a first-class `scope`
// column and part of the composite primary key `(scope, chunk_id)`.
// Every statement this companion issues except the scope *enumeration*
// binds it, in one of exactly two ways (`Sql.ScopeBinding`): the seven
// that read or mutate existing rows carry a `scope = @scope` predicate,
// and the two INSERTs (single and batched) — which have no rows to
// filter yet — carry the scope in the row identity they write plus their
// `ON CONFLICT (scope, chunk_id)` target, so a chunk id colliding across
// scopes cannot overwrite a neighbour's row. There is no statement shape that can
// reach across scopes, so cross-scope leakage is impossible by
// construction rather than by remembering to filter — the SQL twin of
// `HnswVectorStore`'s per-scope graph. `Sql.scopeBoundStatements` is the
// enumerable proof; the test pack asserts the binding on every member,
// with no database present.
//
// **GP 12 — portability.** Identity by value (`chunkId: string`,
// `VectorScope`), async at every boundary, no callbacks, entirely
// stateless between calls (the `NpgsqlDataSource` is a connection pool,
// not per-call state), scope is the shard key with no cross-scope
// ordering promise.
//
// **Fail-loud (507.C).** Connection failure, a missing `vector`
// extension, a missing table under `VerifyOnly`, or a malformed option
// raise a descriptive `PgvectorStoreException` at `create` time — never
// at first query, deep inside a request path. `TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION`
// keeps the same meaning it has for the in-tree stores: with it set, a
// row whose `metadata` JSON cannot be decoded aborts the read instead of
// degrading to empty metadata.
//
// Distributed-readiness: **production-ready / distributed-ready**. Two or
// more replicas may share one database.

// ─── Failure ─────────────────────────────────────────────────────────

/// Raised for every companion-level failure: option validation, the
/// `create`-time connectivity / extension / schema probe, a dimension
/// mismatch at upsert, and (under the refuse-on-corruption toggle) an
/// undecodable metadata row. One exception type so a composing app can
/// catch the companion's failures without matching on Npgsql internals.
exception PgvectorStoreException of message: string

let private fail (message: string) = raise (PgvectorStoreException message)

// ─── Scope key ───────────────────────────────────────────────────────
//
// The `scope` column's value. Same encoding as the in-tree stores' blob
// path segment, so an index exported from `HnswVectorStore` imports here
// without a translation table.

module Scope =
    /// Encode a `VectorScope` as its `scope` column value.
    let toKey (scope: VectorScope) : string =
        match scope with
        | Platform -> "platform"
        | Deployment -> "deployment"
        | Team teamId -> $"team:{teamId}"
        | User userId -> $"user:{userId}"

    /// Decode a `scope` column value. Total: an unrecognised prefix is
    /// read as a team id rather than throwing, matching the in-tree
    /// stores — an unknown scope key is a stale row, not a corrupt one.
    let fromKey (key: string) : VectorScope =
        if key = "platform" then
            Platform
        elif key = "deployment" then
            Deployment
        elif key.StartsWith "user:" then
            User(key.Substring "user:".Length)
        elif key.StartsWith "team:" then
            Team(key.Substring "team:".Length)
        else
            Team key

// ─── Vector literal ──────────────────────────────────────────────────
//
// pgvector's text input format is `[1,2,3]`, cast with `::vector`. Going
// through the text form rather than the `Pgvector` NuGet package's
// binary type handler keeps the dependency set to Npgsql alone — one
// vendor package, per the companion-authoring guide's "use the narrow
// surface" rule.

module Vector =
    /// Euclidean magnitude.
    let magnitude (v: float32 array) : float32 =
        let mutable sum = 0.0f

        for x in v do
            sum <- sum + x * x

        sqrt sum

    /// Unit-normalise. A zero vector is returned unchanged — pgvector's
    /// `<=>` yields NaN against a zero vector either way, and silently
    /// substituting a synthetic direction would fabricate a ranking.
    let normalise (v: float32 array) : float32 array =
        let m = magnitude v
        if m = 0.0f then v else Array.map (fun x -> x / m) v

    /// pgvector text literal — `[a,b,c]`, invariant culture, round-trippable.
    let toLiteral (v: float32 array) : string =
        let sb = StringBuilder()
        sb.Append '[' |> ignore

        v
        |> Array.iteri (fun i x ->
            if i > 0 then
                sb.Append ',' |> ignore

            sb.Append(x.ToString("R", CultureInfo.InvariantCulture)) |> ignore)

        sb.Append ']' |> ignore
        sb.ToString()

// ─── Metadata codec ──────────────────────────────────────────────────
//
// `TextChunk.Metadata` is a flat `Map<string, string>`, stored as a
// `jsonb` object so an operator can inspect and index it with ordinary
// SQL (`metadata ->> '_origin' = 'Document'`). A hand-rolled object
// encode/decode rather than the F#-aware converter set, because the
// on-disk shape here is a *query surface* the deployment reads with
// psql, not an opaque round-trip buffer.

module Metadata =
    /// Encode to a flat JSON object.
    let toJson (metadata: Map<string, string>) : string =
        use stream = new IO.MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()

        for KeyValue(k, v) in metadata do
            writer.WriteString(k, v)

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    /// Decode a flat JSON object. `Error` carries a human-readable
    /// reason for the fail-loud path; non-string values are coerced to
    /// their raw text so a row hand-edited in psql stays readable.
    let fromJson (json: string) : Result<Map<string, string>, string> =
        if String.IsNullOrWhiteSpace json then
            Ok Map.empty
        else
            try
                use doc = JsonDocument.Parse json

                if doc.RootElement.ValueKind <> JsonValueKind.Object then
                    Error(sprintf "expected a JSON object, got %O" doc.RootElement.ValueKind)
                else
                    doc.RootElement.EnumerateObject()
                    |> Seq.map (fun p ->
                        let value =
                            if p.Value.ValueKind = JsonValueKind.String then
                                p.Value.GetString()
                            else
                                p.Value.GetRawText()

                        p.Name, value)
                    |> Map.ofSeq
                    |> Ok
            with ex ->
                Error ex.Message

// ─── Options ─────────────────────────────────────────────────────────

/// Approximate-nearest-neighbour index built over the `embedding` column.
/// The default is **no ANN index** — exact search, which is correct at
/// every corpus size and fast below a few hundred thousand rows (GP 11:
/// the default is the conservative behaviour). An ANN index trades exact
/// recall for latency and is the deliberate opt-in at scale.
type PgvectorAnnIndex =
    /// Exact (sequential) cosine scan. Perfect recall.
    | NoAnnIndex
    /// pgvector ≥ 0.5 HNSW index. `m` is the neighbour budget,
    /// `efConstruction` the build-time candidate list.
    | HnswAnnIndex of m: int * efConstruction: int
    /// pgvector IVFFlat index. `lists` ≈ rows / 1000 is the usual
    /// starting point. Must be built AFTER the table holds data.
    | IvfFlatAnnIndex of lists: int

/// How `create` reconciles the database schema.
type PgvectorSchemaMode =
    /// Issue the idempotent `CREATE EXTENSION` / `CREATE TABLE IF NOT
    /// EXISTS` / `CREATE INDEX IF NOT EXISTS` migration. Needs DDL rights.
    | AutoMigrate
    /// Verify the extension + table are present and refuse to start if
    /// not. For deployments whose application role has no DDL grant —
    /// the schema is provisioned by a migration tool, and the companion's
    /// job is to fail loudly rather than silently query a missing table.
    | VerifyOnly

/// Companion configuration. `Dimensions` is required and has no default:
/// the column is `vector(N)` and N is a property of the deployment's
/// embedding model, not something this companion can guess (GP 9 — never
/// fabricate).
type PgvectorOptions = {
    /// Unqualified table name. Validated as a plain SQL identifier —
    /// identifiers cannot be parameterised, so the guard is the injection
    /// boundary.
    Table: string
    /// Embedding dimensionality. Must match the composed
    /// `IEmbeddingProvider`'s output length.
    Dimensions: int
    /// Schema reconciliation performed at `create` time.
    SchemaMode: PgvectorSchemaMode
    /// ANN index to build (under `AutoMigrate`) / expect. Default `NoAnnIndex`.
    AnnIndex: PgvectorAnnIndex
    /// Per-command timeout in seconds. `0` inherits the data source's.
    CommandTimeoutSeconds: int
}

module PgvectorOptions =
    /// pgvector's own hard ceiling on a `vector` column.
    [<Literal>]
    let MaxDimensions = 16000

    /// Defaults for everything except `Dimensions`, which the caller must
    /// supply.
    let forDimensions (dimensions: int) : PgvectorOptions = {
        Table = "toolup_rag_chunks"
        Dimensions = dimensions
        SchemaMode = AutoMigrate
        AnnIndex = NoAnnIndex
        CommandTimeoutSeconds = 0
    }

    /// A plain unquoted SQL identifier: leading letter or underscore,
    /// then letters / digits / underscores, ≤ 63 bytes (PostgreSQL's
    /// `NAMEDATALEN - 1`). Deliberately narrower than what PostgreSQL
    /// would accept quoted — the table name is interpolated into every
    /// statement, so the narrow set is the injection guard, not a
    /// stylistic preference.
    let isSafeIdentifier (name: string) : bool =
        not (String.IsNullOrWhiteSpace name)
        && name.Length <= 63
        && (Char.IsLetter name[0] || name[0] = '_')
        && name |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_')
        && name |> Seq.forall (fun c -> int c < 128)

    /// Bounds-check before any I/O, so a fat-fingered option fails at
    /// `create` with a message naming the offending field rather than as
    /// a Postgres syntax error on the first query.
    let validate (o: PgvectorOptions) : Result<unit, string> =
        if not (isSafeIdentifier o.Table) then
            Error(
                sprintf
                    "Table must be a plain SQL identifier (letter or underscore, then letters/digits/underscores, max 63 chars); got '%s'."
                    o.Table
            )
        elif o.Dimensions < 1 || o.Dimensions > MaxDimensions then
            Error(sprintf "Dimensions must be in [1, %d]; got %d." MaxDimensions o.Dimensions)
        elif o.CommandTimeoutSeconds < 0 then
            Error(sprintf "CommandTimeoutSeconds must be >= 0; got %d." o.CommandTimeoutSeconds)
        else
            match o.AnnIndex with
            | NoAnnIndex -> Ok()
            | HnswAnnIndex(m, efConstruction) ->
                if m < 2 || m > 100 then
                    Error(sprintf "AnnIndex HNSW m must be in [2, 100]; got %d." m)
                elif efConstruction < m * 2 then
                    Error(sprintf "AnnIndex HNSW efConstruction must be >= 2*m (%d); got %d." (m * 2) efConstruction)
                else
                    Ok()
            | IvfFlatAnnIndex lists ->
                if lists < 1 then
                    Error(sprintf "AnnIndex IVFFlat lists must be >= 1; got %d." lists)
                else
                    Ok()

// ─── Tuning (Phase 892) ──────────────────────────────────────────────
//
// Search-time and write-time posture, kept OUT of `PgvectorOptions` so a
// deployment that constructs the options record in full keeps compiling,
// and so each posture is one named value rather than a set of defaults
// spread across a widened record. Since Phase 939 the `create` /
// `createWithDataSource` entry points compose `PgvectorTuning.recommended`
// (the posture Phase 928 measured to return full, correct pages), and
// `createTuned` / `createTunedWithDataSource` take one explicitly, which is
// how a deployment keeps `PgvectorTuning.unchanged`. That departs from
// GP 11 on purpose: the old default returned short pages and wrong answers
// for mid-sized scopes, silently, so keeping it would preserve a defect
// rather than a contract (docs/migrations/939-pgvector-tuned-default.md).

/// Search-time and write-time tuning for `PgvectorVectorStore`. Start
/// from `PgvectorTuning.recommended` (what `create` composes) or
/// `PgvectorTuning.unchanged` (pgvector's own defaults) and override fields
/// with `with`.
///
/// With an approximate index configured, the search statement always
/// orders by distance ALONE and the `(score, scope, chunkId)` total order
/// is restored by re-sorting the returned page (Phase 939 retired the
/// `IndexOrderedSearch` switch). Phase 928 measured the two-key statement
/// on pgvector 0.8.6 / PostgreSQL 17: the planner served it from the same
/// index through an Incremental Sort, with identical recall, so the switch
/// changed only that sort step. Under `NoAnnIndex` the statement keeps the
/// total order in SQL, and every search is exact.
type PgvectorTuning = {
    /// Per-query search width — `hnsw.ef_search` under an HNSW index,
    /// `ivfflat.probes` under IVFFlat. `None` leaves the database default
    /// in force. Applied with `set_config(name, value, true)` inside the
    /// query's OWN transaction, so the setting ends with that transaction
    /// and a pooled connection never carries one caller's width to the
    /// next. Has no effect under `NoAnnIndex`.
    SearchWidth: int option
    /// Ask pgvector to keep scanning the approximate index until the scope
    /// filter has yielded `topK` rows (`relaxed_order` iterative scan).
    /// Applied only when the installed extension supports it (pgvector
    /// 0.8.0 and later) — detected at `create` time, and reported by the
    /// health probe when requested but unavailable.
    IterativeScan: bool
    /// With an approximate index configured, when one scope's page comes
    /// back SHORTER than `topK`, re-run that scope with `Sql.searchExact`,
    /// the one statement no approximate index can serve. A short page
    /// means either the scope holds fewer live chunks than `topK`, or the
    /// scope filter starved the approximate scan; the exact re-run makes a
    /// full top-k a guarantee rather than a tuning outcome. Has no effect
    /// under `NoAnnIndex`, where every search is already exact.
    ExactFallbackOnShortPage: bool
    /// Upper bound on the per-scope queries a multi-scope `Search` runs
    /// concurrently. `1` is sequential (the pre-892 behaviour). Each
    /// concurrent query holds one pooled connection for its duration.
    MaxSearchConcurrency: int
    /// Estimated row count above which the preflight validator
    /// (`Health.validator`) warns that no approximate index is configured
    /// and every search is an exact scan.
    ExactScanWarningRows: int64
}

/// Presets and validation for `PgvectorTuning`.
module PgvectorTuning =
    /// pgvector's upper bound on `hnsw.ef_search`.
    [<Literal>]
    let MaxHnswEfSearch = 1000

    /// pgvector's upper bound on `ivfflat.probes`.
    [<Literal>]
    let MaxIvfFlatProbes = 32768

    /// Upper bound on `MaxSearchConcurrency` — a multi-scope search holding
    /// more pooled connections than this is a misconfiguration, not a
    /// tuning choice.
    [<Literal>]
    let MaxSearchConcurrencyLimit = 64

    /// pgvector's own defaults, with nothing added: the database-default
    /// search width, no iterative scan, no fallback, sequential multi-scope
    /// search. This was the store's posture before Phase 892, and what
    /// `create` composed until Phase 939, with one difference: with an
    /// approximate index configured, the search now orders by distance
    /// alone and re-sorts the page, which Phase 928 measured as the same
    /// index path with the same recall. Compose it with `createTuned` to
    /// keep the old behaviour. Measured, it returns short pages and a low
    /// recall for scopes of about 1-12 percent of a shared table (see the
    /// companion README).
    let unchanged: PgvectorTuning = {
        SearchWidth = None
        IterativeScan = false
        ExactFallbackOnShortPage = false
        MaxSearchConcurrency = 1
        ExactScanWarningRows = 500_000L
    }

    /// The production posture for a shared table holding many scopes, and
    /// what `create` / `createWithDataSource` compose since Phase 939: a
    /// raised search width (100, as `hnsw.ef_search` or `ivfflat.probes`),
    /// iterative scanning where the extension supports it, the exact
    /// fallback on a short page, and a bounded concurrent multi-scope
    /// search. See the companion README for what each value buys and what
    /// has been measured, for both index families.
    let recommended: PgvectorTuning = {
        SearchWidth = Some 100
        IterativeScan = true
        ExactFallbackOnShortPage = true
        MaxSearchConcurrency = 4
        ExactScanWarningRows = 500_000L
    }

    /// Bounds-check a tuning against the options it will run with, before
    /// any I/O, naming the offending field.
    let validate (options: PgvectorOptions) (t: PgvectorTuning) : Result<unit, string> =
        let widthCheck =
            match t.SearchWidth, options.AnnIndex with
            | None, _
            | Some _, NoAnnIndex -> Ok()
            | Some w, HnswAnnIndex _ when w < 1 || w > MaxHnswEfSearch ->
                Error(sprintf "SearchWidth (hnsw.ef_search) must be in [1, %d]; got %d." MaxHnswEfSearch w)
            | Some w, IvfFlatAnnIndex _ when w < 1 || w > MaxIvfFlatProbes ->
                Error(sprintf "SearchWidth (ivfflat.probes) must be in [1, %d]; got %d." MaxIvfFlatProbes w)
            | Some _, _ -> Ok()

        match widthCheck with
        | Error e -> Error e
        | Ok() ->
            if t.MaxSearchConcurrency < 1 || t.MaxSearchConcurrency > MaxSearchConcurrencyLimit then
                Error(
                    sprintf
                        "MaxSearchConcurrency must be in [1, %d]; got %d."
                        MaxSearchConcurrencyLimit
                        t.MaxSearchConcurrency
                )
            elif t.ExactScanWarningRows < 0L then
                Error(sprintf "ExactScanWarningRows must be >= 0; got %d." t.ExactScanWarningRows)
            else
                Ok()

/// The installed `vector` extension's version, as `pg_extension.extversion`
/// reports it, and the capabilities that hang off it.
module ExtensionVersion =
    /// The first pgvector release with iterative index scans.
    let iterativeScanSince = Version(0, 8, 0)

    /// Parse the leading numeric part of an `extversion` string (`0.8.0`,
    /// `0.7.4`); `None` for anything without one.
    let tryParse (extversion: string) : Version option =
        if String.IsNullOrWhiteSpace extversion then
            None
        else
            let m = Text.RegularExpressions.Regex.Match(extversion.Trim(), @"^\d+(\.\d+){1,3}")

            match m.Success, Version.TryParse m.Value with
            | true, (true, v) -> Some v
            | _ -> None

    /// Whether an extension at `extversion` supports iterative index scans.
    /// An unknown version is treated as unsupported, so the setting is never
    /// sent to a server that would reject it.
    let supportsIterativeScan (extversion: string option) : bool =
        extversion
        |> Option.bind tryParse
        |> Option.exists (fun v -> v >= iterativeScanSince)

// ─── SQL ─────────────────────────────────────────────────────────────
//
// Every statement lives here, built from the validated table name, so
// the scope-isolation guarantee is auditable in one place instead of
// being spread across nine interface members.

module Sql =
    /// Named parameter carrying the scope key on every scope-bound
    /// statement.
    [<Literal>]
    let ScopeParameter = "@scope"

    /// The predicate that makes scope isolation structural.
    [<Literal>]
    let ScopePredicate = "scope = @scope"

    let private annIndexDdl (o: PgvectorOptions) : string list =
        match o.AnnIndex with
        | NoAnnIndex -> []
        | HnswAnnIndex(m, efConstruction) -> [
            sprintf
                "CREATE INDEX IF NOT EXISTS %s_embedding_hnsw_idx ON %s USING hnsw (embedding vector_cosine_ops) WITH (m = %d, ef_construction = %d);"
                o.Table
                o.Table
                m
                efConstruction
          ]
        | IvfFlatAnnIndex lists -> [
            sprintf
                "CREATE INDEX IF NOT EXISTS %s_embedding_ivfflat_idx ON %s USING ivfflat (embedding vector_cosine_ops) WITH (lists = %d);"
                o.Table
                o.Table
                lists
          ]

    /// The idempotent schema migration, ordered. Excludes `CREATE
    /// EXTENSION`, which is issued separately because it needs elevated
    /// rights and has its own fallback probe.
    let migration (o: PgvectorOptions) : string list =
        [
            sprintf
                """CREATE TABLE IF NOT EXISTS %s (
    scope      text        NOT NULL,
    chunk_id   text        NOT NULL,
    content    text        NOT NULL,
    metadata   jsonb       NOT NULL DEFAULT '{}'::jsonb,
    embedding  vector(%d)  NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT %s_pkey PRIMARY KEY (scope, chunk_id)
);"""
                o.Table
                o.Dimensions
                o.Table
            // Partial index over live rows — the shape `Search` and the
            // default `ListChunks` both take.
            sprintf
                "CREATE INDEX IF NOT EXISTS %s_scope_live_idx ON %s (scope) WHERE deleted_at IS NULL;"
                o.Table
                o.Table
            // Tombstone sweep for `Vacuum`.
            sprintf "CREATE INDEX IF NOT EXISTS %s_scope_deleted_idx ON %s (scope, deleted_at);" o.Table o.Table
        ]
        @ annIndexDdl o

    /// Extension bootstrap (`AutoMigrate` only).
    [<Literal>]
    let CreateExtension = "CREATE EXTENSION IF NOT EXISTS vector;"

    /// Probe used when `CREATE EXTENSION` is refused for lack of rights,
    /// and as the whole extension check under `VerifyOnly`.
    [<Literal>]
    let ExtensionPresent = "SELECT 1 FROM pg_extension WHERE extname = 'vector';"

    /// Table-presence probe for `VerifyOnly`.
    let tableRegclass = "SELECT to_regclass(@table);"

    /// Upsert on the composite key. Re-upserting clears any tombstone —
    /// the new content supersedes the old (the `IVectorStore` contract).
    let upsert (o: PgvectorOptions) =
        sprintf
            """INSERT INTO %s (scope, chunk_id, content, metadata, embedding, deleted_at)
VALUES (@scope, @chunk_id, @content, @metadata::jsonb, @embedding::vector, NULL)
ON CONFLICT (scope, chunk_id) DO UPDATE
SET content = EXCLUDED.content,
    metadata = EXCLUDED.metadata,
    embedding = EXCLUDED.embedding,
    deleted_at = NULL;"""
            o.Table

    /// Phase 892 — batched upsert on the composite key: ONE statement per
    /// batch whatever its size. The rows arrive as parallel arrays
    /// (`unnest … WITH ORDINALITY`) and the embeddings as ONE flat `real[]`
    /// sliced per row, so every vector travels as a binary float4 array
    /// parameter rather than a text literal — without the separate
    /// pgvector type-handler package. The scope is bound once and written
    /// into every row's identity, with the same conflict target as
    /// `upsert`, so the batch is scope-keyed exactly as the single write
    /// is. The caller de-duplicates chunk ids first (a conflict target may
    /// not be hit twice by one statement).
    let upsertBatch (o: PgvectorOptions) =
        sprintf
            """INSERT INTO %s (scope, chunk_id, content, metadata, embedding, deleted_at)
SELECT @scope, b.chunk_id, b.content, b.metadata::jsonb,
       ((@embeddings::real[])[((b.ord - 1) * @dimensions + 1)::int:(b.ord * @dimensions)::int])::vector, NULL
FROM unnest(@chunk_ids::text[], @contents::text[], @metadata::text[]) WITH ORDINALITY AS b(chunk_id, content, metadata, ord)
ON CONFLICT (scope, chunk_id) DO UPDATE
SET content = EXCLUDED.content,
    metadata = EXCLUDED.metadata,
    embedding = EXCLUDED.embedding,
    deleted_at = NULL;"""
            o.Table

    /// Per-scope KNN under `NoAnnIndex`, where only an exact scan can serve
    /// it. `<=>` is pgvector's cosine distance, so `1 - distance` is the
    /// cosine similarity the other stores report. Tie-broken on `chunk_id`
    /// inside the scope; the caller applies the cross-scope total order.
    /// With an approximate index configured the store runs
    /// `searchIndexOrdered` instead.
    let search (o: PgvectorOptions) =
        sprintf
            """SELECT chunk_id, content, metadata, 1 - (embedding <=> @embedding::vector) AS score
FROM %s
WHERE scope = @scope AND deleted_at IS NULL
ORDER BY embedding <=> @embedding::vector, chunk_id
LIMIT @top_k;"""
            o.Table

    /// Phase 892 — per-scope KNN whose `ORDER BY` is the distance operator
    /// alone. The total order `search` states in SQL is restored by the
    /// caller re-sorting the returned page. Since Phase 939 it is the
    /// statement for every search with an approximate index configured.
    /// Phase 928's `EXPLAIN` showed the approximate index serves `search`
    /// too (an Incremental Sort presorted on the distance), so this saves
    /// the sort step; it is not what makes the index usable.
    let searchIndexOrdered (o: PgvectorOptions) =
        sprintf
            """SELECT chunk_id, content, metadata, 1 - (embedding <=> @embedding::vector) AS score
FROM %s
WHERE scope = @scope AND deleted_at IS NULL
ORDER BY embedding <=> @embedding::vector
LIMIT @top_k;"""
            o.Table

    /// Phase 928 — per-scope KNN that NO approximate index can serve: the
    /// scope's live rows are materialised first, and only then ranked, so
    /// the ordering operator never reaches an index scan. This is the
    /// short-page fallback's statement. `search` is not exact once an
    /// approximate index exists — measured on pgvector 0.8.6 / PostgreSQL
    /// 17, the planner serves its two-key `ORDER BY` from the HNSW index
    /// through an Incremental Sort — so a fallback re-running `search`
    /// could come back as short as the page it was meant to repair.
    let searchExact (o: PgvectorOptions) =
        sprintf
            """WITH scoped AS MATERIALIZED (
    SELECT chunk_id, content, metadata, embedding
    FROM %s
    WHERE scope = @scope AND deleted_at IS NULL
)
SELECT chunk_id, content, metadata, 1 - (embedding <=> @embedding::vector) AS score
FROM scoped
ORDER BY embedding <=> @embedding::vector, chunk_id
LIMIT @top_k;"""
            o.Table

    /// Administrative enumeration. `@include_deleted` keeps the statement
    /// shape constant so the plan is cached across both call shapes.
    let listChunks (o: PgvectorOptions) =
        sprintf
            """SELECT chunk_id, content, metadata, deleted_at
FROM %s
WHERE scope = @scope AND (@include_deleted OR deleted_at IS NULL)
ORDER BY chunk_id;"""
            o.Table

    /// Tombstone write. Already-tombstoned rows keep their original
    /// timestamp, so a repeated delete cannot extend the retention window.
    let deleteChunk (o: PgvectorOptions) =
        sprintf
            """UPDATE %s
SET deleted_at = @deleted_at
WHERE scope = @scope AND chunk_id = @chunk_id AND deleted_at IS NULL;"""
            o.Table

    /// Tombstone removal. Only a tombstoned row is touched, so a restore of
    /// a live chunk is a no-op.
    let restoreChunk (o: PgvectorOptions) =
        sprintf
            """UPDATE %s
SET deleted_at = NULL
WHERE scope = @scope AND chunk_id = @chunk_id AND deleted_at IS NOT NULL;"""
            o.Table

    /// Hard-delete of the scope's tombstones older than `@older_than`.
    let vacuum (o: PgvectorOptions) =
        sprintf
            """DELETE FROM %s
WHERE scope = @scope AND deleted_at IS NOT NULL AND deleted_at < @older_than;"""
            o.Table

    /// Hard-delete of every row in the scope — the configuration-grade
    /// reset `IVectorStore.DeleteByScope` names.
    let deleteByScope (o: PgvectorOptions) =
        sprintf "DELETE FROM %s WHERE scope = @scope;" o.Table

    /// The one statement with no scope predicate — it exists precisely to
    /// enumerate scopes, and is exempt by construction, not by omission.
    let listScopes (o: PgvectorOptions) =
        sprintf "SELECT DISTINCT scope FROM %s ORDER BY scope;" o.Table

    /// Phase 892 — the database settings a search applies for its own
    /// transaction, as `(setting, value)` pairs: the search width and the
    /// iterative-scan mode of the configured index family. Empty under
    /// `NoAnnIndex`, and empty when nothing is tuned — in which case the
    /// search runs exactly as it did before 892, with no transaction. The
    /// iterative-scan setting is included only when `extensionVersion`
    /// supports it, so it is never sent to a server that would reject it.
    let searchSettings
        (o: PgvectorOptions)
        (t: PgvectorTuning)
        (extensionVersion: string option)
        : (string * string) list =
        let iterative =
            t.IterativeScan && ExtensionVersion.supportsIterativeScan extensionVersion

        let family =
            match o.AnnIndex with
            | NoAnnIndex -> None
            | HnswAnnIndex _ -> Some("hnsw.ef_search", "hnsw.iterative_scan")
            | IvfFlatAnnIndex _ -> Some("ivfflat.probes", "ivfflat.iterative_scan")

        match family with
        | None -> []
        | Some(widthSetting, iterativeSetting) -> [
            match t.SearchWidth with
            | Some w -> widthSetting, string w
            | None -> ()
            if iterative then
                iterativeSetting, "relaxed_order"
          ]

    /// Phase 892 — one statement applying `count` settings for the current
    /// transaction only (`set_config(…, true)` is `SET LOCAL`). Names and
    /// values are bound as parameters `@setting_i` / `@value_i`.
    let setLocal (count: int) =
        let calls =
            List.init count (fun i -> sprintf "set_config(@setting_%d, @value_%d, true)" i i)

        sprintf "SELECT %s;" (String.concat ", " calls)

    /// Phase 892 — the posture read behind the health probe and the
    /// preflight validator, in one round-trip: the installed extension
    /// version, the approximate-index access methods present on the table,
    /// the database's current search-width settings, and an estimated row
    /// count (planner statistics, falling back to the live-tuple counter
    /// for a never-analysed table — never a `count(*)`). The `'[1]'::vector`
    /// literal is coerced at parse time, which loads the extension library
    /// in this backend so its settings are defined when read.
    let diagnostics =
        """SELECT
    ('[1]'::vector IS NOT NULL) AS vector_loaded,
    (SELECT extversion FROM pg_extension WHERE extname = 'vector') AS extversion,
    (SELECT COALESCE(string_agg(DISTINCT am.amname, ','), '')
       FROM pg_index i
       JOIN pg_class ic ON ic.oid = i.indexrelid
       JOIN pg_am am ON am.oid = ic.relam
      WHERE i.indrelid = to_regclass(@table) AND am.amname IN ('hnsw', 'ivfflat')) AS ann_methods,
    current_setting('hnsw.ef_search', true) AS hnsw_ef_search,
    current_setting('ivfflat.probes', true) AS ivfflat_probes,
    COALESCE((SELECT COALESCE(NULLIF(c.reltuples, -1)::bigint, s.n_live_tup, 0)
                FROM pg_class c
                LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
               WHERE c.oid = to_regclass(@table)), 0) AS row_estimate;"""

    /// The installed `vector` extension's version (`create`-time read).
    [<Literal>]
    let ExtensionVersionRead =
        "SELECT extversion FROM pg_extension WHERE extname = 'vector';"

    /// How a statement binds the scope column. Two shapes, because an
    /// INSERT has no rows to filter yet — its isolation is carried by
    /// the row IDENTITY rather than by a predicate. Distinguishing them
    /// keeps the guarantee checkable instead of approximately true.
    type ScopeBinding =
        /// Reads or mutates existing rows, filtered by `scope = @scope`.
        | ScopePredicated
        /// Writes a row whose composite identity `(scope, chunk_id)`
        /// binds the scope column to `@scope`. Nothing outside the
        /// caller's scope is reachable, including on conflict.
        | ScopeKeyed

    /// The composite-key conflict target — the scope half of a written
    /// row's identity.
    [<Literal>]
    let ScopeConflictTarget = "ON CONFLICT (scope, chunk_id)"

    /// Every statement that reads or writes chunk rows, paired with the
    /// interface member it serves and the way it binds scope. The
    /// scope-isolation guarantee (GP 4) is that EVERY member of this
    /// list carries one of the two bindings and none carries neither;
    /// the test pack asserts exactly that, so a future statement added
    /// without a scope binding fails the gate rather than shipping a
    /// leak. `ListScopes` is absent by design — it exists to enumerate
    /// scopes, so a scope predicate would make it useless.
    let scopeBoundStatements (o: PgvectorOptions) : (string * ScopeBinding * string) list = [
        "Upsert", ScopeKeyed, upsert o
        "UpsertBatch", ScopeKeyed, upsertBatch o
        "Search", ScopePredicated, search o
        "SearchIndexOrdered", ScopePredicated, searchIndexOrdered o
        "SearchExact", ScopePredicated, searchExact o
        "ListChunks", ScopePredicated, listChunks o
        "DeleteChunk", ScopePredicated, deleteChunk o
        "RestoreChunk", ScopePredicated, restoreChunk o
        "Vacuum", ScopePredicated, vacuum o
        "DeleteByScope", ScopePredicated, deleteByScope o
    ]

// ─── Batched write preparation (Phase 892) ───────────────────────────

/// The parallel arrays one `Sql.upsertBatch` statement binds: one entry
/// per DISTINCT chunk id, embeddings flattened row-major into one array
/// of `count × Dimensions` unit-normalised floats.
type PreparedBatch = {
    /// Distinct chunk ids, in first-seen order.
    ChunkIds: string array
    /// Chunk content, aligned with `ChunkIds`.
    Contents: string array
    /// Chunk metadata as JSON objects, aligned with `ChunkIds`.
    Metadata: string array
    /// Every row's unit-normalised vector, concatenated in `ChunkIds` order.
    Embeddings: float32 array
}

/// Pure preparation of a batched upsert — everything except the I/O, so
/// the de-duplication and dimension guard are provable without a database.
module BatchUpsert =
    /// Validate and flatten `chunks` for `Sql.upsertBatch`. A chunk id
    /// repeated inside the batch keeps its first position and its LAST
    /// value — the state sequential upserts would leave. A vector of the
    /// wrong length is refused, naming the chunk, before anything is
    /// written. The tombstone metadata key is stripped, as `Upsert` does.
    let prepare (options: PgvectorOptions) (chunks: (string * float32 array * TextChunk) list) : PreparedBatch =
        let order = ResizeArray<string>()
        let latest = Collections.Generic.Dictionary<string, float32 array * TextChunk>()

        for chunkId, vector, chunk in chunks do
            if vector.Length <> options.Dimensions then
                fail (
                    sprintf
                        "[PgvectorVectorStore] Chunk '%s' in a batch carries a %d-dimension vector but table '%s' is vector(%d). Nothing in the batch was written. The column dimension is fixed at migration time — re-embed the corpus with the composed provider, or compose a separate store per embedding model."
                        chunkId
                        vector.Length
                        options.Table
                        options.Dimensions
                )

            if not (latest.ContainsKey chunkId) then
                order.Add chunkId

            latest[chunkId] <- (vector, chunk)

        let ids = order.ToArray()
        let embeddings = Array.zeroCreate<float32> (ids.Length * options.Dimensions)

        ids
        |> Array.iteri (fun i id ->
            let vector, _ = latest[id]
            Array.blit (Vector.normalise vector) 0 embeddings (i * options.Dimensions) options.Dimensions)

        {
            ChunkIds = ids
            Contents = ids |> Array.map (fun id -> (snd latest[id]).Content)
            Metadata =
                ids
                |> Array.map (fun id ->
                    (snd latest[id]).Metadata
                    |> Map.remove ChunkMetadata.DeletedAtKey
                    |> Metadata.toJson)
            Embeddings = embeddings
        }

// ─── Diagnostics (Phase 892) ─────────────────────────────────────────

/// A live read of the store's posture — what the health probe reports and
/// the preflight validator judges. Produced by `PgvectorVectorStore.Diagnose`.
type PgvectorDiagnostics = {
    /// The options the store was built with.
    Options: PgvectorOptions
    /// The tuning the store was built with.
    Tuning: PgvectorTuning
    /// The `vector` extension version read at `create` — what the store's
    /// per-query settings were chosen against.
    ExtensionVersionAtCreate: string option
    /// The `vector` extension version installed now.
    ExtensionVersion: string option
    /// Approximate-index access methods (`hnsw` / `ivfflat`) of the
    /// indexes present on the table.
    AnnIndexMethods: string list
    /// The database's current `hnsw.ef_search`, when defined.
    DatabaseHnswEfSearch: string option
    /// The database's current `ivfflat.probes`, when defined.
    DatabaseIvfFlatProbes: string option
    /// Estimated row count (planner statistics; never a full count).
    RowEstimate: int64
}

// ─── Store ───────────────────────────────────────────────────────────

/// PostgreSQL + pgvector `IVectorStore`. Durable and multi-replica-safe:
/// the index is the database, so replicas share one view of the corpus
/// with no per-process state to reconcile.
///
/// Construct via `PgvectorVectorStore.create` / `createWithDataSource`,
/// which perform the `create`-time connectivity + schema probe. The
/// constructor itself is deliberately I/O-free so the probe's failure
/// mode is a single, descriptive exception from one place.
type PgvectorVectorStore
    (
        dataSource: NpgsqlDataSource,
        options: PgvectorOptions,
        tuning: PgvectorTuning,
        extensionVersionAtCreate: string option,
        ownsDataSource: bool,
        logger: ILogger option
    ) =

    let log =
        logger
        |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

    // Same toggle, same meaning as the in-tree stores: with it set, an
    // undecodable row aborts the read instead of degrading to empty
    // metadata. A compliance deployment would rather stop than answer
    // from a corpus it cannot fully read.
    let refuseOnCorruption =
        match Environment.GetEnvironmentVariable ConfigKeys.Names.ragRefuseOnIndexCorruption with
        | "1"
        | "true"
        | "TRUE" -> true
        | _ -> false

    let sqlUpsert = Sql.upsert options
    let sqlUpsertBatch = Sql.upsertBatch options
    let sqlSearchExact = Sql.searchExact options
    let sqlListChunks = Sql.listChunks options
    let sqlDeleteChunk = Sql.deleteChunk options
    let sqlRestoreChunk = Sql.restoreChunk options
    let sqlVacuum = Sql.vacuum options
    let sqlDeleteByScope = Sql.deleteByScope options
    let sqlListScopes = Sql.listScopes options

    let newCommand (sql: string) =
        let cmd = dataSource.CreateCommand sql

        if options.CommandTimeoutSeconds > 0 then
            cmd.CommandTimeout <- options.CommandTimeoutSeconds

        cmd

    /// Decode a `metadata` column, honouring the fail-loud toggle.
    let decodeMetadata (scopeKey: string) (chunkId: string) (json: string) : Map<string, string> =
        match Metadata.fromJson json with
        | Ok m -> m
        | Error reason ->
            if refuseOnCorruption then
                fail (
                    sprintf
                        "[PgvectorVectorStore] Refusing to read chunk '%s' in scope '%s' from table '%s': its metadata column is not a decodable JSON object (%s). TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION is set — repair or delete the row, then retry (unset the variable to fall back to the default empty-metadata behaviour)."
                        chunkId
                        scopeKey
                        options.Table
                        reason
                )

            log.Warn
                $"[PgvectorVectorStore] Undecodable metadata on chunk '{chunkId}' in scope '{scopeKey}': {reason} — reading it as empty."

            Map.empty

    /// Project the `deleted_at` column back onto the metadata map, so
    /// `ListChunks includeDeleted = true` surfaces `_deletedAt` exactly
    /// as the in-tree stores do. The column is the source of truth (it
    /// is what `Vacuum` predicates on); the metadata key is its
    /// contract-visible projection.
    let withTombstone (deletedAt: DateTime option) (metadata: Map<string, string>) =
        match deletedAt with
        | Some ts ->
            metadata
            |> Map.add
                ChunkMetadata.DeletedAtKey
                (DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc)).ToString "o")
        | None -> metadata |> Map.remove ChunkMetadata.DeletedAtKey

    let readerDeletedAt (reader: Data.Common.DbDataReader) (ordinal: int) =
        if reader.IsDBNull ordinal then
            None
        else
            Some(reader.GetFieldValue<DateTime> ordinal)

    // ─── Phase 892 search paths ──────────────────────────────────────

    let annConfigured =
        match options.AnnIndex with
        | NoAnnIndex -> false
        | _ -> true

    // Phase 939: with an approximate index configured, the statement
    // orders by distance alone and `Search` re-sorts the page; under
    // `NoAnnIndex` it keeps the total order in SQL.
    let sqlSearch =
        if annConfigured then
            Sql.searchIndexOrdered options
        else
            Sql.search options

    // Fixed for the store's lifetime: the settings depend only on the
    // options, the tuning and the extension version read at `create`.
    let settings = Sql.searchSettings options tuning extensionVersionAtCreate
    let sqlSetLocal = Sql.setLocal settings.Length

    let bindSearch (parameters: NpgsqlParameterCollection) (scopeKey: string) (queryLiteral: string) (topK: int) =
        parameters.AddWithValue("scope", scopeKey) |> ignore
        parameters.AddWithValue("embedding", queryLiteral) |> ignore
        parameters.AddWithValue("top_k", topK) |> ignore

    let readMatches (scope: VectorScope) (scopeKey: string) (reader: Data.Common.DbDataReader) = async {
        let acc = ResizeArray<VectorMatch>()
        let mutable go = true

        while go do
            let! has = reader.ReadAsync() |> Async.AwaitTask

            if has then
                let chunkId = reader.GetString 0

                acc.Add {
                    ChunkId = chunkId
                    Content = reader.GetString 1
                    Score = reader.GetDouble 3
                    Scope = scope
                    Metadata = decodeMetadata scopeKey chunkId (reader.GetString 2)
                }
            else
                go <- false

        return List.ofSeq acc
    }

    /// One statement on a pooled connection — the pre-892 path, used
    /// whenever there is nothing to apply for the query's transaction.
    let runPlain (sql: string) (scope: VectorScope) (queryLiteral: string) (topK: int) = async {
        let scopeKey = Scope.toKey scope
        use cmd = newCommand sql
        bindSearch cmd.Parameters scopeKey queryLiteral topK
        use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
        return! readMatches scope scopeKey reader
    }

    /// The search inside its own transaction, with the tuned settings
    /// applied `LOCAL` to it: they end at commit, so the pooled connection
    /// goes back to the pool carrying nothing of this caller's.
    let runWithSettings (sql: string) (scope: VectorScope) (queryLiteral: string) (topK: int) = async {
        let scopeKey = Scope.toKey scope
        use! conn = dataSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask
        use! tx = conn.BeginTransactionAsync().AsTask() |> Async.AwaitTask

        use setCmd = new NpgsqlCommand(sqlSetLocal, conn, tx)

        if options.CommandTimeoutSeconds > 0 then
            setCmd.CommandTimeout <- options.CommandTimeoutSeconds

        settings
        |> List.iteri (fun i (name, value) ->
            setCmd.Parameters.AddWithValue(sprintf "setting_%d" i, name) |> ignore
            setCmd.Parameters.AddWithValue(sprintf "value_%d" i, value) |> ignore)

        let! _ = setCmd.ExecuteNonQueryAsync() |> Async.AwaitTask

        use searchCmd = new NpgsqlCommand(sql, conn, tx)

        if options.CommandTimeoutSeconds > 0 then
            searchCmd.CommandTimeout <- options.CommandTimeoutSeconds

        bindSearch searchCmd.Parameters scopeKey queryLiteral topK

        let! rows = async {
            use! reader = searchCmd.ExecuteReaderAsync() |> Async.AwaitTask
            return! readMatches scope scopeKey reader
        }

        do! tx.CommitAsync() |> Async.AwaitTask
        return rows
    }

    /// One scope's page. With an approximate index configured, a page that
    /// comes back short of `topK` is re-run with the exact statement when
    /// the fallback is on (see `PgvectorTuning.ExactFallbackOnShortPage`).
    let searchScope (scope: VectorScope) (queryLiteral: string) (topK: int) = async {
        let! page =
            if List.isEmpty settings then
                runPlain sqlSearch scope queryLiteral topK
            else
                runWithSettings sqlSearch scope queryLiteral topK

        if annConfigured && tuning.ExactFallbackOnShortPage && page.Length < topK then
            return! runPlain sqlSearchExact scope queryLiteral topK
        else
            return page
    }

    /// The configuration this store was built with — read by the health
    /// probe and useful in diagnostics.
    member _.Options = options

    /// Phase 892 — the search / write tuning this store was built with.
    member _.Tuning = tuning

    /// Phase 892 — the `vector` extension version read at `create`, which
    /// decides whether iterative scanning is applied. `None` for a store
    /// constructed directly without one.
    member _.ExtensionVersionAtCreate = extensionVersionAtCreate

    /// Phase 892 — read the store's live posture in one round-trip: the
    /// installed extension version, the approximate indexes present on the
    /// table, the database's search-width settings and an estimated row
    /// count. Feeds the health probe and the preflight validator.
    member _.Diagnose() : Async<PgvectorDiagnostics> = async {
        use cmd = newCommand Sql.diagnostics
        cmd.Parameters.AddWithValue("table", options.Table) |> ignore
        use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
        let! has = reader.ReadAsync() |> Async.AwaitTask

        let text (ordinal: int) =
            if has && not (reader.IsDBNull ordinal) then
                Some(reader.GetValue(ordinal).ToString())
            else
                None

        let annMethods =
            text 2
            |> Option.map (fun s -> s.Split(',', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)
            |> Option.defaultValue []

        let rows =
            if has && not (reader.IsDBNull 5) then
                Convert.ToInt64(reader.GetValue 5, CultureInfo.InvariantCulture)
            else
                0L

        return {
            Options = options
            Tuning = tuning
            ExtensionVersionAtCreate = extensionVersionAtCreate
            ExtensionVersion = text 1
            AnnIndexMethods = annMethods
            DatabaseHnswEfSearch = text 3
            DatabaseIvfFlatProbes = text 4
            RowEstimate = rows
        }
    }

    /// The I/O-free constructor over a schema the caller has provisioned:
    /// `PgvectorTuning.unchanged` and no recorded extension version, since
    /// it reads nothing from the database. It does NOT take the posture
    /// `create` composes; build through `create` / `createWithDataSource`
    /// (or `createTuned`) for that.
    new(dataSource: NpgsqlDataSource, options: PgvectorOptions, ownsDataSource: bool, ?logger: ILogger) =
        new PgvectorVectorStore(dataSource, options, PgvectorTuning.unchanged, None, ownsDataSource, logger)

    interface IVectorStore with

        member _.Upsert scope chunkId vector chunk = async {
            if vector.Length <> options.Dimensions then
                fail (
                    sprintf
                        "[PgvectorVectorStore] Chunk '%s' carries a %d-dimension vector but table '%s' is vector(%d). The column dimension is fixed at migration time — re-embed the corpus with the composed provider, or compose a separate store per embedding model."
                        chunkId
                        vector.Length
                        options.Table
                        options.Dimensions
                )

            let scopeKey = Scope.toKey scope
            // The tombstone lives in the column; strip any caller-supplied
            // copy so the two can never disagree.
            let metadata = chunk.Metadata |> Map.remove ChunkMetadata.DeletedAtKey

            use cmd = newCommand sqlUpsert
            cmd.Parameters.AddWithValue("scope", scopeKey) |> ignore
            cmd.Parameters.AddWithValue("chunk_id", chunkId) |> ignore
            cmd.Parameters.AddWithValue("content", chunk.Content) |> ignore
            cmd.Parameters.AddWithValue("metadata", Metadata.toJson metadata) |> ignore

            cmd.Parameters.AddWithValue("embedding", Vector.toLiteral (Vector.normalise vector))
            |> ignore

            let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            return ()
        }

        member _.Search scopes query topK = async {
            if topK <= 0 || List.isEmpty scopes then
                return []
            else
                let queryLiteral = Vector.toLiteral (Vector.normalise query)

                // One scope-parameterised query per requested scope. A
                // single `scope = ANY(@scopes)` query would be fewer
                // round-trips but would put the isolation guarantee inside
                // an array parameter; per-scope keeps `Sql.search`'s
                // predicate the only shape that exists (GP 4). Phase 892
                // runs them concurrently under `MaxSearchConcurrency`
                // (`1` — the unchanged default — is sequential).
                let! pages =
                    Async.Parallel(
                        scopes |> List.map (fun scope -> searchScope scope queryLiteral topK),
                        maxDegreeOfParallelism = tuning.MaxSearchConcurrency
                    )

                // Same total order as every in-tree store: score
                // descending, ties broken on `(Scope, ChunkId)` so a
                // repeated query is byte-identical run to run — the
                // deterministic-ordering contract the eval gate rests on.
                // It is also what restores the total order of an
                // index-ordered page, whose SQL orders by distance alone.
                return
                    pages
                    |> Seq.concat
                    |> Seq.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)
                    |> Seq.truncate topK
                    |> Seq.toList
        }

        member _.ListChunks scope includeDeleted = async {
            let scopeKey = Scope.toKey scope
            use cmd = newCommand sqlListChunks
            cmd.Parameters.AddWithValue("scope", scopeKey) |> ignore
            cmd.Parameters.AddWithValue("include_deleted", includeDeleted) |> ignore

            use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
            let acc = ResizeArray<string * TextChunk>()
            let mutable go = true

            while go do
                let! has = reader.ReadAsync() |> Async.AwaitTask

                if has then
                    let chunkId = reader.GetString 0
                    let metadata = decodeMetadata scopeKey chunkId (reader.GetString 2)

                    acc.Add(
                        chunkId,
                        {
                            Content = reader.GetString 1
                            Metadata = withTombstone (readerDeletedAt reader 3) metadata
                        }
                    )
                else
                    go <- false

            return List.ofSeq acc
        }

        member _.DeleteChunk scope chunkId = async {
            use cmd = newCommand sqlDeleteChunk
            cmd.Parameters.AddWithValue("scope", Scope.toKey scope) |> ignore
            cmd.Parameters.AddWithValue("chunk_id", chunkId) |> ignore

            cmd.Parameters.AddWithValue("deleted_at", DateTimeOffset.UtcNow.UtcDateTime)
            |> ignore

            let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            return ()
        }

        member _.RestoreChunk scope chunkId = async {
            use cmd = newCommand sqlRestoreChunk
            cmd.Parameters.AddWithValue("scope", Scope.toKey scope) |> ignore
            cmd.Parameters.AddWithValue("chunk_id", chunkId) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            return ()
        }

        member _.Vacuum scope olderThan = async {
            use cmd = newCommand sqlVacuum
            cmd.Parameters.AddWithValue("scope", Scope.toKey scope) |> ignore
            cmd.Parameters.AddWithValue("older_than", olderThan.UtcDateTime) |> ignore
            let! purged = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            return purged
        }

        member _.DeleteByScope scope = async {
            use cmd = newCommand sqlDeleteByScope
            cmd.Parameters.AddWithValue("scope", Scope.toKey scope) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            return ()
        }

        member _.ListScopes() = async {
            use cmd = newCommand sqlListScopes
            use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
            let acc = ResizeArray<VectorScope>()
            let mutable go = true

            while go do
                let! has = reader.ReadAsync() |> Async.AwaitTask

                if has then
                    acc.Add(Scope.fromKey (reader.GetString 0))
                else
                    go <- false

            return List.ofSeq acc
        }

        member this.Erase(scope, subjectUserId, policy, dryRun) =
            ToolUp.Platform.IVectorStore.eraseSubject (this :> IVectorStore) scope subjectUserId policy dryRun

    // Phase 892 — one `INSERT … SELECT … FROM unnest(…)` per batch, the
    // vectors bound as one binary `real[]`. The statement count is one for
    // every non-empty batch, whatever its size.
    interface IVectorStoreBatch with
        member _.UpsertBatch scope chunks = async {
            let prepared = BatchUpsert.prepare options chunks

            if prepared.ChunkIds.Length > 0 then
                use cmd = newCommand sqlUpsertBatch
                cmd.Parameters.AddWithValue("scope", Scope.toKey scope) |> ignore
                cmd.Parameters.AddWithValue("chunk_ids", prepared.ChunkIds) |> ignore
                cmd.Parameters.AddWithValue("contents", prepared.Contents) |> ignore
                cmd.Parameters.AddWithValue("metadata", prepared.Metadata) |> ignore
                cmd.Parameters.AddWithValue("embeddings", prepared.Embeddings) |> ignore
                cmd.Parameters.AddWithValue("dimensions", options.Dimensions) |> ignore
                let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                ()
        }

    interface IDisposable with
        member _.Dispose() =
            if ownsDataSource then
                dataSource.Dispose()

// ─── create-time probe (507.C) ───────────────────────────────────────

/// Connectivity + extension + schema reconciliation, run once at
/// construction. Every failure is a descriptive `PgvectorStoreException`
/// naming the operator action — never a deferred failure on the first
/// retrieval of a live request.
let private probeAndMigrate (dataSource: NpgsqlDataSource) (options: PgvectorOptions) : Async<unit> = async {
    // 1. Connectivity. A store that cannot reach its database must not
    //    be composed at all.
    try
        use cmd = dataSource.CreateCommand "SELECT 1;"
        let! _ = cmd.ExecuteScalarAsync() |> Async.AwaitTask
        ()
    with ex ->
        fail (
            sprintf
                "[PgvectorVectorStore] Cannot reach the configured PostgreSQL database: %s. The store is not composed — check the connection string, network reachability and credentials."
                ex.Message
        )

    // 2. The `vector` extension. Under AutoMigrate try to create it (the
    //    common single-role dev/CI case); if the role lacks the grant,
    //    fall through to the presence probe rather than reporting a
    //    permission error the operator cannot act on directly.
    let extensionPresent () = async {
        use cmd = dataSource.CreateCommand Sql.ExtensionPresent
        let! result = cmd.ExecuteScalarAsync() |> Async.AwaitTask
        return not (isNull result || result = box DBNull.Value)
    }

    match options.SchemaMode with
    | AutoMigrate ->
        let mutable created = false

        try
            use cmd = dataSource.CreateCommand Sql.CreateExtension
            let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            created <- true
        with _ ->
            created <- false

        if not created then
            let! present = extensionPresent ()

            if not present then
                fail
                    "[PgvectorVectorStore] The `vector` extension is not installed and this role may not create it. Run `CREATE EXTENSION vector;` as a superuser in the target database (pgvector must be installed on the server), then restart."
    | VerifyOnly ->
        let! present = extensionPresent ()

        if not present then
            fail
                "[PgvectorVectorStore] The `vector` extension is not installed in the target database. SchemaMode = VerifyOnly, so this companion will not create it — run `CREATE EXTENSION vector;` as a superuser, then restart."

    // 3. Schema.
    match options.SchemaMode with
    | AutoMigrate ->
        for statement in Sql.migration options do
            try
                use cmd = dataSource.CreateCommand statement
                let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                ()
            with ex ->
                fail (
                    sprintf
                        "[PgvectorVectorStore] Schema migration failed on `%s`: %s. Either grant this role DDL rights on the target schema, or provision the table out of band and compose with SchemaMode = VerifyOnly."
                        (statement.Split '\n' |> Array.head)
                        ex.Message
                )
    | VerifyOnly ->
        use cmd = dataSource.CreateCommand Sql.tableRegclass
        cmd.Parameters.AddWithValue("table", options.Table) |> ignore
        let! result = cmd.ExecuteScalarAsync() |> Async.AwaitTask

        if isNull result || result = box DBNull.Value then
            fail (
                sprintf
                    "[PgvectorVectorStore] Table '%s' does not exist in the target database and SchemaMode = VerifyOnly. Provision it with the DDL in the companion README, or compose with SchemaMode = AutoMigrate."
                    options.Table
            )
}

/// Phase 892 — the installed extension version, read once at `create` by
/// the tuned entry points; it decides whether iterative scanning is sent.
let private readExtensionVersion (dataSource: NpgsqlDataSource) : Async<string option> = async {
    use cmd = dataSource.CreateCommand Sql.ExtensionVersionRead
    let! result = cmd.ExecuteScalarAsync() |> Async.AwaitTask

    return
        if isNull result || result = box DBNull.Value then
            None
        else
            Some(string result)
}

let private validateOrFail (options: PgvectorOptions) =
    match PgvectorOptions.validate options with
    | Ok() -> ()
    | Error message -> fail (sprintf "[PgvectorVectorStore] Invalid PgvectorOptions — %s" message)

let private validateTuningOrFail (options: PgvectorOptions) (tuning: PgvectorTuning) =
    match PgvectorTuning.validate options tuning with
    | Ok() -> ()
    | Error message -> fail (sprintf "[PgvectorVectorStore] Invalid PgvectorTuning — %s" message)

/// Warn once, at `create`, when iterative scanning was asked for and the
/// installed extension cannot provide it — the store still composes, and
/// the health probe keeps reporting it.
let private warnIfIterativeUnavailable
    (log: ILogger option)
    (tuning: PgvectorTuning)
    (options: PgvectorOptions)
    (version: string option)
    =
    let annConfigured =
        match options.AnnIndex with
        | NoAnnIndex -> false
        | _ -> true

    if
        annConfigured
        && tuning.IterativeScan
        && not (ExtensionVersion.supportsIterativeScan version)
    then
        let logger =
            log |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

        logger.Warn(
            sprintf
                "[PgvectorVectorStore] IterativeScan is requested but the installed vector extension (%s) predates %O — it is not applied. Upgrade pgvector (ALTER EXTENSION vector UPDATE) to enable it; the exact fallback on a short page still guarantees a full top-k."
                (version |> Option.defaultValue "version unknown")
                ExtensionVersion.iterativeScanSince
        )

/// Phase 892 — `createWithDataSource` with an explicit `PgvectorTuning`
/// (start from `PgvectorTuning.recommended`). The tuning is validated with
/// the options before any I/O, and the extension version is read once so
/// iterative scanning is sent only to a server that supports it. The
/// returned store also implements `IVectorStoreBatch`.
let createTunedWithDataSource
    (dataSource: NpgsqlDataSource)
    (options: PgvectorOptions)
    (tuning: PgvectorTuning)
    (logger: ILogger option)
    : IVectorStore =
    validateOrFail options
    validateTuningOrFail options tuning

    let version =
        async {
            do! probeAndMigrate dataSource options
            return! readExtensionVersion dataSource
        }
        |> Async.RunSynchronously

    warnIfIterativeUnavailable logger tuning options version
    new PgvectorVectorStore(dataSource, options, tuning, version, false, logger) :> IVectorStore

/// Phase 892 — `create` with an explicit `PgvectorTuning` (start from
/// `PgvectorTuning.recommended`). The store owns the resulting
/// `NpgsqlDataSource` and disposes it with itself.
///
/// ```
/// let store =
///     PgvectorVectorStore.createTuned
///         connectionString
///         { PgvectorOptions.forDimensions 1536 with AnnIndex = HnswAnnIndex(16, 64) }
///         PgvectorTuning.recommended
///         (Some logger)
/// ```
let createTuned
    (connectionString: string)
    (options: PgvectorOptions)
    (tuning: PgvectorTuning)
    (logger: ILogger option)
    : IVectorStore =
    validateOrFail options
    validateTuningOrFail options tuning

    if String.IsNullOrWhiteSpace connectionString then
        fail
            "[PgvectorVectorStore] The connection string is empty. Supply it from ISecretStore / configuration at compose time."

    let dataSource =
        try
            NpgsqlDataSource.Create connectionString
        with ex ->
            fail (sprintf "[PgvectorVectorStore] The connection string could not be parsed: %s" ex.Message)

    let version =
        try
            async {
                do! probeAndMigrate dataSource options
                return! readExtensionVersion dataSource
            }
            |> Async.RunSynchronously
        with _ ->
            dataSource.Dispose()
            reraise ()

    warnIfIterativeUnavailable logger tuning options version
    new PgvectorVectorStore(dataSource, options, tuning, version, true, logger) :> IVectorStore

/// Build a store over a data source the CALLER owns (a shared pool, or a
/// data source configured with TLS / logging the deployment supplies).
/// Disposing the store leaves the data source open. The returned store
/// also implements `IVectorStoreBatch`.
///
/// Options are validated and the database probed before the store is
/// returned, so a `create` that returns has a store that works. Since
/// Phase 939 it composes `PgvectorTuning.recommended`, including the
/// extension-version read, so iterative scanning is sent only to pgvector
/// 0.8.0 or later. Under `NoAnnIndex` (the `forDimensions` default) every
/// search is exact and the one change is that a multi-scope search runs up
/// to four scopes concurrently. Use `createTunedWithDataSource` with
/// `PgvectorTuning.unchanged` to keep the pre-939 posture.
let createWithDataSource
    (dataSource: NpgsqlDataSource)
    (options: PgvectorOptions)
    (logger: ILogger option)
    : IVectorStore =
    createTunedWithDataSource dataSource options PgvectorTuning.recommended logger

/// Build a store from a connection string. The store owns the resulting
/// `NpgsqlDataSource` and disposes it with itself. Since Phase 939 it
/// composes `PgvectorTuning.recommended` (see `createWithDataSource`); use
/// `createTuned` with `PgvectorTuning.unchanged` to keep the pre-939
/// posture.
///
/// ```
/// let store =
///     PgvectorVectorStore.create connectionString (PgvectorOptions.forDimensions 1536) (Some logger)
/// ```
let create (connectionString: string) (options: PgvectorOptions) (logger: ILogger option) : IVectorStore =
    createTuned connectionString options PgvectorTuning.recommended logger