// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.FactStores.Postgres

open System
open System.Collections.Generic
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.Extensions.DependencyInjection
open Npgsql
open NpgsqlTypes
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Facts
open ToolUp.Platform.ProviderExceptions

// ─── Phase 888 — PostgreSQL-backed IFactStore ────────────────────────
//
// The indexed `IFactStore` the contract has anticipated since Phase 520.
// Facts are rows; every read is a keyed query, so the cost of a question
// scales with its answer and not with the scope's history:
//
//   point read       (scope, hierarchy, path, metric, period_from)  index
//   lineage head     (scope, lineage_hash) WHERE is_head             unique index
//   population read  (scope, metric, hierarchy, period_to) WHERE is_head
//                    INCLUDE the read's columns (Phase 962)       covering index
//   supersession     (scope, supersedes)                             index
//   transaction time (scope, as_of_ticks)                            index
//
// **Content address is the primary key.** `(scope, fact_id)` — so a
// replayed assert is a no-op by constraint rather than by a read, and two
// replicas racing to write the same fact cannot both succeed.
//
// **Supersession is one transaction.** Asserting a fact that supersedes a
// head clears the head's `is_head` flag and inserts the successor in the
// same transaction. The partial unique index on `(scope, lineage_hash)
// WHERE is_head` makes "one current head per lineage" a database
// invariant, and the clear is conditional on the flag still being set, so
// two replicas deriving against the same head cannot both win: the loser
// sees a unique violation or a zero-row clear, rolls back, and re-derives
// against the new head. Supersession chains therefore stay linear under
// concurrent writers (law L3).
//
// **AsOf reads use the same table (law L4).** A fact is visible at `t`
// when its transaction time is at most `t` and no successor with a
// transaction time at most `t` names it. The current-head flag answers the
// common case (a head written by `t`); a second, narrow branch finds the
// predecessors of successors written AFTER `t`, driven from the
// transaction-time index, so a replay costs what changed since `t` and a
// read "as of now" costs nothing extra. A head whose transaction time is in
// the future (a coarse or skewed clock) is simply not yet visible and its
// predecessor is — there is no separate read model for such a head to be
// missing from, so nothing declines (the Phase 702 case). Transaction and
// valid time are stored as `DateTime` ticks, so the one-tick successor rule
// (`AsOf` strictly increasing within a lineage) survives the round trip.
//
// **What the database decides, and what the shared pipeline decides.** A
// population read pushes down every decision whose arithmetic is provably
// the shared pipeline's:
//
//   pushed down  subject set (hierarchy, depth, path prefix), metric,
//                period overlap, L4 visibility, a single named method,
//                and — when no canonical-method selection can apply — the
//                value threshold (`numeric` comparison is exact decimal
//                comparison, and a non-scalar has no magnitude, so it
//                fails every threshold in both places).
//   aggregated   (Phase 940) — when no canonical selection can move the
//                population — the statistics and the top k:
//                  counts, and the distinct-subject count (a hash count
//                    first; an exact `count(DISTINCT path)` only when the
//                    hashes cannot prove every path distinct);
//                  earliest start and latest end (integer ticks);
//                  minimum and maximum (`numeric` comparison is exact
//                    decimal comparison; equal values at two scales are
//                    equal decimals, and which one the in-memory fold keeps
//                    is its enumeration order's, which no store pins);
//                  the sum, exact in `numeric`, used only when the sum of
//                    absolute values at the largest scale fits `decimal`'s
//                    mantissa — then every left-to-right partial sum is a
//                    `decimal` without rounding, so the fold's sum is this
//                    one, scale included — and the mean is the fold's own
//                    `total / decimal count`, taken in .NET;
//                  the method mix, ordered ordinally in .NET;
//                  the freshness histogram, as `as_of >= t - window` (the
//                    `Freshness.deriveAt` comparison restated), except where
//                    `asOf + window` could leave `DateTime`'s range;
//                  the top k by value, ties by content address in byte
//                    order (ordinal for a hex address), re-ranked in .NET by
//                    `PopulationRanking.rankMembers`.
//                All of it in one repeatable-read snapshot.
//   in memory    the D19 canonical-method selection (a registry fact) when
//                two methods are present to compete, and anything the
//                aggregated read declines (a sum `decimal` would round, a
//                freshness window at the edge of `DateTime`). Then every
//                visible member row is read and the whole shared pipeline
//                runs over the projected rows through
//                `PopulationQueryTypes`, as it did before Phase 940.
//
// Only the top-k facts are read in full. A point read's listing order is
// the contract's (hierarchy, metric, period start); ties, which the
// contract leaves open, are broken by content address.
//
// **Audit (GP 6)** is exactly `BlobFactStore`'s: per-fact `FactAsserted`
// (+ `FactSuperseded`) rows on the scalar path, one summarised
// `FactBatchAsserted` row on the batch path, under the reserved `_facts`
// source module. The events are written after the transaction commits.
//
// **GP 1** — Npgsql lives here and nowhere in `ToolUp.Platform.*` or
// `ToolUp.Facts.*`. **GP 4** — `scope` is the leading column of every key
// and a predicate of every statement; there is no statement shape that
// reaches across scopes. **GP 12** — identity by value, async at every
// boundary, failure as `Result`, stateless between calls (the
// `NpgsqlDataSource` is a connection pool, not per-call state), and
// ordering promised only within a scope.
//
// Distributed-readiness: **production-ready / distributed-ready**. Any
// number of replicas may share one database.

/// Raised for a companion-level failure at construction: option
/// validation, connectivity, or the schema probe. Store operations report
/// failure as data (`Result`), never through this.
exception PostgresFactStoreException of message: string

/// How `create` reconciles the database schema.
type PostgresFactSchemaMode =
    /// Issue the idempotent `CREATE TABLE IF NOT EXISTS` / `CREATE INDEX
    /// IF NOT EXISTS` migration. Needs DDL rights.
    | AutoMigrate
    /// Verify the table is present and refuse to start if not — for a role
    /// with no DDL grant, whose schema a migration tool provisions.
    | VerifyOnly

/// Companion configuration.
type PostgresFactStoreOptions = {
    /// Unqualified table name. Validated as a plain SQL identifier —
    /// identifiers cannot be parameterised, so the guard is the injection
    /// boundary.
    Table: string
    /// Schema reconciliation performed at `create` time.
    SchemaMode: PostgresFactSchemaMode
    /// Per-command timeout in seconds. `0` inherits the data source's.
    CommandTimeoutSeconds: int
    /// How many times a write transaction is attempted when it loses a
    /// race to a concurrent writer (a unique violation on the content
    /// address or the lineage head, a serialization failure, a deadlock).
    /// Each attempt re-derives against the heads the winner left.
    MaxWriteAttempts: int
}

/// Defaults and validation for `PostgresFactStoreOptions`.
module PostgresFactStoreOptions =

    /// `toolup_facts`, auto-migrated, the data source's timeout, five
    /// write attempts.
    let defaults: PostgresFactStoreOptions = {
        Table = "toolup_facts"
        SchemaMode = AutoMigrate
        CommandTimeoutSeconds = 0
        MaxWriteAttempts = 5
    }

    /// The longest table name accepted. Index names append a suffix of at
    /// most thirteen characters, and PostgreSQL truncates identifiers at
    /// 63 bytes — a truncated index name could collide with another's.
    [<Literal>]
    let MaxTableNameLength = 48

    /// A plain unquoted SQL identifier: leading letter or underscore, then
    /// letters, digits or underscores, at most `MaxTableNameLength` long.
    let isSafeIdentifier (name: string) : bool =
        not (String.IsNullOrWhiteSpace name)
        && name.Length <= MaxTableNameLength
        && (Char.IsLetter name[0] || name[0] = '_')
        && name
           |> Seq.forall (fun c -> (c < '\u0080' && Char.IsLetterOrDigit c) || c = '_')

    /// The problems with `options`, or the empty list.
    let validate (options: PostgresFactStoreOptions) : string list = [
        if not (isSafeIdentifier options.Table) then
            sprintf
                "Table '%s' is not a plain SQL identifier of at most %d characters (letters, digits, underscores; not starting with a digit)."
                options.Table
                MaxTableNameLength

        if options.CommandTimeoutSeconds < 0 then
            sprintf "CommandTimeoutSeconds is %d; it must be 0 (inherit) or positive." options.CommandTimeoutSeconds

        if options.MaxWriteAttempts < 1 then
            sprintf "MaxWriteAttempts is %d; it must be at least 1." options.MaxWriteAttempts
    ]

/// The column filter one read applies. Every clause is optional and
/// AND-combined; `scope` is not here because it is never optional.
type internal RowFilter = {
    Hierarchy: string option
    Path: string list option
    Level: int option
    PathPrefix: string list option
    Metric: string option
    Period: TemporalExtent option
    MethodIdentity: string option
    Threshold: ValueThreshold option
}

module internal RowFilter =
    let none: RowFilter = {
        Hierarchy = None
        Path = None
        Level = None
        PathPrefix = None
        Metric = None
        Period = None
        MethodIdentity = None
        Threshold = None
    }

/// The SQL this companion issues. Public so a reader (and the test pack)
/// can see every statement shape; every statement binds `@scope`.
module Sql =

    /// The table and its indexes. Idempotent.
    let migration (table: string) : string list = [
        $"""CREATE TABLE IF NOT EXISTS {table} (
    scope text NOT NULL,
    fact_id text NOT NULL,
    hierarchy text NOT NULL,
    path text[] NOT NULL,
    metric text NOT NULL,
    period_from_ticks bigint NOT NULL,
    period_to_ticks bigint NOT NULL,
    method_identity text NOT NULL,
    lineage_hash text NOT NULL,
    as_of_ticks bigint NOT NULL,
    as_of timestamptz NOT NULL,
    supersedes text NULL,
    is_head boolean NOT NULL,
    magnitude numeric NULL,
    payload text NOT NULL,
    PRIMARY KEY (scope, fact_id)
)"""
        $"CREATE INDEX IF NOT EXISTS {table}_point_idx ON {table} (scope, hierarchy, path, metric, period_from_ticks)"
        $"CREATE INDEX IF NOT EXISTS {table}_lineage_idx ON {table} (scope, lineage_hash)"
        $"CREATE UNIQUE INDEX IF NOT EXISTS {table}_head_idx ON {table} (scope, lineage_hash) WHERE is_head"
        // Phase 962 — the population read's covering index: the current
        // heads of one metric, keyed by period END so the latest period is a
        // narrow range, carrying every column the summary, the method mix and
        // the top k read, so the read is index-only. It replaces Phase 888's
        // `pop_idx` (scope, metric, is_head), which every one of those
        // statements now plans past, so that index is dropped once this one
        // exists.
        $"CREATE INDEX IF NOT EXISTS {table}_population_idx ON {table} (scope, metric, hierarchy, period_to_ticks) INCLUDE (period_from_ticks, as_of_ticks, magnitude, method_identity, path, fact_id) WHERE is_head"
        $"DROP INDEX IF EXISTS {table}_pop_idx"
        $"CREATE INDEX IF NOT EXISTS {table}_succ_idx ON {table} (scope, supersedes) WHERE supersedes IS NOT NULL"
        $"CREATE INDEX IF NOT EXISTS {table}_txtime_idx ON {table} (scope, as_of_ticks)"
    ]

    /// `to_regclass` probe for `VerifyOnly`.
    let tableRegclass = "SELECT to_regclass(@table)::text"

    /// The column list a population read projects.
    let memberColumns (alias: string) =
        $"{alias}.fact_id, {alias}.hierarchy, {alias}.path, {alias}.magnitude::text, {alias}.period_from_ticks, {alias}.period_to_ticks, {alias}.as_of_ticks, {alias}.method_identity"

    /// The filter clauses of `filter` over `alias`, each prefixed ` AND `.
    let internal filterClauses (alias: string) (filter: RowFilter) : string =
        let sb = StringBuilder()

        let add (clause: string) =
            sb.Append(" AND ").Append(clause) |> ignore

        filter.Hierarchy |> Option.iter (fun _ -> add $"{alias}.hierarchy = @hierarchy")
        filter.Path |> Option.iter (fun _ -> add $"{alias}.path = @path")
        filter.Level |> Option.iter (fun _ -> add $"cardinality({alias}.path) = @level")

        match filter.PathPrefix with
        | Some(_ :: _) -> add $"{alias}.path[1:@prefix_len] = @prefix"
        | _ -> ()

        filter.Metric |> Option.iter (fun _ -> add $"{alias}.metric = @metric")

        filter.Period
        |> Option.iter (fun _ ->
            add $"{alias}.period_from_ticks < @period_to AND @period_from < {alias}.period_to_ticks")

        filter.MethodIdentity
        |> Option.iter (fun _ -> add $"{alias}.method_identity = @method")

        match filter.Threshold with
        | Some(AtLeast _) -> add $"{alias}.magnitude >= @t_low"
        | Some(AtMost _) -> add $"{alias}.magnitude <= @t_high"
        | Some(Between _) -> add $"{alias}.magnitude >= @t_low AND {alias}.magnitude <= @t_high"
        | None -> ()

        sb.ToString()

    /// The facts visible at `@t` (law L4) that pass `filter`, projected to
    /// `columns`. Branch one is the current heads written by `@t`; branch
    /// two is the predecessors of successors written after `@t`, driven
    /// from the transaction-time index. The two are disjoint (a branch-two
    /// row has a successor, so it is not a head).
    ///
    /// Phase 962 — branch two is DRIVEN from the successors written after
    /// `@t`: their `supersedes` ids are read once from the transaction-time
    /// range (the sub-select names no `supersedes IS NOT NULL` predicate, so
    /// the partial supersession index cannot answer it), and each id is
    /// fetched by primary key in a lateral sub-select the planner may not
    /// flatten (`OFFSET 0`), so the join order is fixed: the ids drive, and
    /// no index on the metric can turn the read into a walk of its
    /// superseded rows. A read "as of now" therefore costs
    /// one empty index range, and a replay what changed since `@t`, by
    /// construction rather than by the planner's estimate. The `IN` form it
    /// replaces was planned on statistics older than the table's
    /// supersessions three ways, each one linear in the scope's history: a
    /// nested loop re-scanning every successor per superseded row (91 ms of
    /// a 94 ms summary at 12,000 facts; a 3.4 s population read at four
    /// callers), a walk of every successor, and a walk of every superseded
    /// row of the metric. A fresh `ANALYZE` hid all three.
    let internal visibleAt (table: string) (columns: string) (filter: RowFilter) : string =
        let clauses = filterClauses "f" filter

        $"""SELECT {columns} FROM {table} f
WHERE f.scope = @scope AND f.is_head AND f.as_of_ticks <= @t{clauses}
UNION ALL
SELECT {columns} FROM unnest(array_remove(ARRAY(SELECT DISTINCT s.supersedes FROM {table} s WHERE s.scope = @scope AND s.as_of_ticks > @t), NULL)) AS p(id)
CROSS JOIN LATERAL (SELECT * FROM {table} x WHERE x.scope = @scope AND x.fact_id = p.id OFFSET 0) f
WHERE NOT f.is_head AND f.as_of_ticks <= @t{clauses}
  AND NOT EXISTS (SELECT 1 FROM {table} s2 WHERE s2.scope = @scope AND s2.supersedes = f.fact_id AND s2.as_of_ticks <= @t)"""

    // ─── Population aggregates (Phase 940) ───────────────────────────
    //
    // The population read's statistics and top-k, answered in the database
    // over the same visible set `visibleAt` defines, so only a summary row,
    // the method mix and k members cross the wire. Every statement binds
    // what `visibleAt` binds; the summary also binds `@fresh_from` and the
    // top-k `@k`. Which arithmetic may run here, and which may not, is the
    // file header's "What the database decides" note.

    /// The visible rows, projected to what the aggregates read — `numeric`
    /// kept as `numeric` so it orders and sums as a number.
    let internal aggregateColumns =
        "f.fact_id, f.hierarchy, f.path, f.magnitude, f.period_from_ticks, f.period_to_ticks, f.as_of_ticks, f.method_identity"

    /// `decimal`'s largest mantissa, 2^96 - 1.
    [<Literal>]
    let internal DecimalMantissaMax = "79228162514264337593543950335"

    /// One row: fact count, comparable count, minimum, maximum, exact sum,
    /// whether every left-to-right partial sum is a `decimal` without
    /// rounding (the sum of absolute values at the largest scale fits the
    /// mantissa), earliest period start, latest period end, fresh count,
    /// and the count of distinct subject-path HASHES (a lower bound on the
    /// distinct subjects: equal paths hash equal, so a count equal to the
    /// fact count proves every path distinct).
    let internal populationSummary (table: string) (filter: RowFilter) : string =
        $"""SELECT count(*), count(m.magnitude), min(m.magnitude)::text, max(m.magnitude)::text, sum(m.magnitude)::text,
  coalesce(sum(abs(m.magnitude)) * power(10::numeric, max(scale(m.magnitude))) <= {DecimalMantissaMax}, true),
  min(m.period_from_ticks), max(m.period_to_ticks),
  count(*) FILTER (WHERE m.as_of_ticks >= @fresh_from),
  count(DISTINCT hash_array_extended(m.path, 0))
FROM ({visibleAt table aggregateColumns filter}) m"""

    /// The exact distinct-subject count — issued only when the hash count
    /// in `populationSummary` cannot prove it. The hierarchy is a filter,
    /// so a subject is its path.
    let internal populationSubjects (table: string) (filter: RowFilter) : string =
        $"SELECT count(DISTINCT m.path) FROM ({visibleAt table aggregateColumns filter}) m"

    /// `(method identity, count)` over the visible rows.
    let internal populationMethodMix (table: string) (filter: RowFilter) : string =
        $"SELECT m.method_identity, count(*) FROM ({visibleAt table aggregateColumns filter}) m GROUP BY m.method_identity"

    /// At most two of the visible rows' method identities — enough to tell
    /// a single-method population (where no canonical selection can apply)
    /// from a competing one.
    let internal populationMethodProbe (table: string) (filter: RowFilter) : string =
        let columns = "f.method_identity"
        $"SELECT m.method_identity FROM ({visibleAt table columns filter}) m GROUP BY m.method_identity LIMIT 2"

    /// The best `@k` comparable members, projected as `memberColumns`: by
    /// value in `direction`, ties by content address in byte order, which
    /// for a hex content address is `String.CompareOrdinal`'s — the
    /// comparator inside `PopulationRanking.rankBy`, so the SET of the top
    /// k is the shared ranking's; the caller re-ranks the k rows through
    /// that function for the order.
    let internal populationTop (table: string) (filter: RowFilter) (direction: RankDirection) : string =
        let order =
            match direction with
            | HighestFirst -> "DESC"
            | LowestFirst -> "ASC"

        $"""SELECT {memberColumns "m"} FROM ({visibleAt table aggregateColumns filter}) m
WHERE m.magnitude IS NOT NULL
ORDER BY m.magnitude {order}, m.fact_id COLLATE "C"
LIMIT @k"""

    /// Every fact written by `@t` that passes `filter` — the full-history
    /// listing (`IncludeSuperseded`).
    let internal historyAt (table: string) (filter: RowFilter) : string =
        let clauses = filterClauses "f" filter
        $"SELECT f.payload FROM {table} f WHERE f.scope = @scope AND f.as_of_ticks <= @t{clauses}"

    /// One fact by content address.
    let getById (table: string) =
        $"SELECT payload FROM {table} WHERE scope = @scope AND fact_id = @fact_id"

    /// Several facts by content address.
    let getByIds (table: string) =
        $"SELECT payload FROM {table} WHERE scope = @scope AND fact_id = ANY(@ids)"

    /// Which of the given content addresses are stored.
    let existing (table: string) =
        $"SELECT fact_id FROM {table} WHERE scope = @scope AND fact_id = ANY(@ids)"

    /// The current heads of the given lineages.
    let headsOf (table: string) =
        $"SELECT payload FROM {table} WHERE scope = @scope AND lineage_hash = ANY(@lineages) AND is_head"

    /// Every fact of one lineage.
    let lineage (table: string) =
        $"SELECT payload FROM {table} WHERE scope = @scope AND lineage_hash = @lineage"

    /// Clear the head flag of heads a batch superseded — conditional on
    /// the flag still being set, so a head a concurrent writer already
    /// superseded is not cleared twice (the row count tells the writer it
    /// lost the race).
    let clearHeads (table: string) =
        $"UPDATE {table} SET is_head = false WHERE scope = @scope AND fact_id = ANY(@ids) AND is_head"

    /// The scope's write lock, shared: every small write holds it, so the
    /// exclusive form below waits for them and they for it. The two-key
    /// advisory form keeps it out of the lineage locks' keyspace.
    let lockScopeShared = "SELECT pg_advisory_xact_lock_shared(888, hashtext(@scope))"

    /// The scope's write lock, exclusive: a batch touching more lineages
    /// than it is sensible to lock one by one serialises against every
    /// other write in the scope instead.
    let lockScopeExclusive = "SELECT pg_advisory_xact_lock(888, hashtext(@scope))"

    /// The lineage locks of a small write, taken in the (sorted) order
    /// given, so two writers never wait on each other in opposite orders.
    let lockLineages =
        "SELECT pg_advisory_xact_lock(hashtextextended(@scope || '|' || l, 0)) FROM unnest(@lineages) AS l"

    /// The binary COPY every write uses.
    let copyIn (table: string) =
        $"COPY {table} (scope, fact_id, hierarchy, path, metric, period_from_ticks, period_to_ticks, method_identity, lineage_hash, as_of_ticks, as_of, supersedes, is_head, magnitude, payload) FROM STDIN (FORMAT BINARY)"

    /// Every statement shape that reads or writes rows, for the scope-
    /// binding check: each one names `@scope` (the COPY carries the scope
    /// in every row it writes instead).
    let scopeBoundStatements (table: string) : string list = [
        visibleAt table (memberColumns "f") RowFilter.none
        historyAt table RowFilter.none
        getById table
        getByIds table
        existing table
        headsOf table
        lineage table
        clearHeads table
    ]

/// The one row projection every write uses — the store's own write core
/// and the Phase 941 migration from `BlobFactStore` — so a migrated row
/// and an asserted row are the same bytes for the same fact.
module internal FactRow =

    let private jsonOptions = FableConverters.create ()

    /// The fact exactly as `BlobFactStore` serialises it (the `payload`
    /// column).
    let serialise (f: Fact) : string =
        JsonSerializer.Serialize(f, jsonOptions)

    let deserialise (payload: string) : Fact =
        JsonSerializer.Deserialize<Fact>(payload, jsonOptions)

    let sha256Hex (s: string) =
        use sha = SHA256.Create()

        sha.ComputeHash(Encoding.UTF8.GetBytes s)
        |> Array.map (sprintf "%02x")
        |> String.concat ""

    /// The `lineage_hash` column: the SHA-256 of the fact's lineage key.
    let lineageHash (f: Fact) =
        sha256Hex (Fact.lineageKey f.Subject f.Metric f.Period f.Method)

    /// Write `f` as one row of an open `Sql.copyIn` import. Every column
    /// but the head flag is a projection of the stored payload, so a column
    /// can never say something the fact does not; `isHead` is the caller's
    /// (the writer knows which fact ends its lineage).
    let write (importer: NpgsqlBinaryImporter) (scopeId: string) (f: Fact) (isHead: bool) =
        let payload = serialise f
        let stored = deserialise payload
        importer.StartRow()
        importer.Write(scopeId, NpgsqlDbType.Text)
        importer.Write(stored.FactId, NpgsqlDbType.Text)
        importer.Write(stored.Subject.Hierarchy, NpgsqlDbType.Text)
        importer.Write(Array.ofList stored.Subject.Path, NpgsqlDbType.Array ||| NpgsqlDbType.Text)
        importer.Write(stored.Metric.Value, NpgsqlDbType.Text)
        importer.Write(stored.Period.From.Ticks, NpgsqlDbType.Bigint)
        importer.Write(stored.Period.To.Ticks, NpgsqlDbType.Bigint)
        importer.Write(Fact.methodIdentity stored.Method, NpgsqlDbType.Text)
        importer.Write(lineageHash f, NpgsqlDbType.Text)
        importer.Write(f.AsOf.Ticks, NpgsqlDbType.Bigint)
        importer.Write(DateTime(f.AsOf.Ticks, DateTimeKind.Utc), NpgsqlDbType.TimestampTz)

        match f.Supersedes with
        | Some id -> importer.Write(id, NpgsqlDbType.Text)
        | None -> importer.WriteNull()

        importer.Write(isHead, NpgsqlDbType.Boolean)

        match PopulationValue.comparable stored.Value with
        | Some d -> importer.Write(d, NpgsqlDbType.Numeric)
        | None -> importer.WriteNull()

        importer.Write(payload, NpgsqlDbType.Text)

/// What the store decided about one draft inside a write transaction.
type internal Disposition = {
    Outcome: BatchAssertOutcome
    FactId: string
    Written: Fact option
}

/// A lost race inside a write transaction — retried, never reported.
exception internal LostWriteRace of string

/// The PostgreSQL-backed `IFactStore` (Phase 888). Construct through
/// `PostgresFactStore.create` / `createWithDataSource`, which validate the
/// options and probe (or migrate) the schema first.
type PostgresFactStore
    internal
    (
        dataSource: NpgsqlDataSource,
        options: PostgresFactStoreOptions,
        events: IEventStore,
        registry: Grounding.IMetricRegistry option,
        clock: unit -> DateTime,
        ownsDataSource: bool
    ) =

    static let jsonOptions = FableConverters.create ()

    // A write touching at most this many lineages locks each one; a larger
    // batch locks the scope instead (advisory locks share one server-wide
    // table, so a population-scale batch must not take one per lineage).
    static let lineageLockLimit = 64

    let table = options.Table

    let deserialise (payload: string) : Fact = FactRow.deserialise payload

    let sha256Hex (s: string) = FactRow.sha256Hex s

    let lineageHashOf (subject: SubjectRef) (metric: MetricRef) (period: TemporalExtent) (method: MethodRef) =
        sha256Hex (Fact.lineageKey subject metric period method)

    let lineageKeyOf (f: Fact) =
        Fact.lineageKey f.Subject f.Metric f.Period f.Method

    let command (sql: string) (conn: NpgsqlConnection) (tx: NpgsqlTransaction) : NpgsqlCommand =
        let cmd = new NpgsqlCommand(sql, conn, tx)

        if options.CommandTimeoutSeconds > 0 then
            cmd.CommandTimeout <- options.CommandTimeoutSeconds

        cmd

    let addText (cmd: NpgsqlCommand) (name: string) (value: string) =
        cmd.Parameters.Add(NpgsqlParameter(name, NpgsqlDbType.Text, Value = value))
        |> ignore

    let addTextArray (cmd: NpgsqlCommand) (name: string) (values: string seq) =
        cmd.Parameters.Add(NpgsqlParameter(name, NpgsqlDbType.Array ||| NpgsqlDbType.Text, Value = Array.ofSeq values))
        |> ignore

    let addBigint (cmd: NpgsqlCommand) (name: string) (value: int64) =
        cmd.Parameters.Add(NpgsqlParameter(name, NpgsqlDbType.Bigint, Value = value))
        |> ignore

    let addInt (cmd: NpgsqlCommand) (name: string) (value: int) =
        cmd.Parameters.Add(NpgsqlParameter(name, NpgsqlDbType.Integer, Value = value))
        |> ignore

    let addNumeric (cmd: NpgsqlCommand) (name: string) (value: decimal) =
        cmd.Parameters.Add(NpgsqlParameter(name, NpgsqlDbType.Numeric, Value = value))
        |> ignore

    let bindFilter (cmd: NpgsqlCommand) (filter: RowFilter) =
        filter.Hierarchy |> Option.iter (addText cmd "hierarchy")
        filter.Path |> Option.iter (addTextArray cmd "path")
        filter.Level |> Option.iter (addInt cmd "level")

        match filter.PathPrefix with
        | Some(_ :: _ as prefix) ->
            addInt cmd "prefix_len" (List.length prefix)
            addTextArray cmd "prefix" prefix
        | _ -> ()

        filter.Metric |> Option.iter (addText cmd "metric")

        filter.Period
        |> Option.iter (fun p ->
            addBigint cmd "period_from" p.From.Ticks
            addBigint cmd "period_to" p.To.Ticks)

        filter.MethodIdentity |> Option.iter (addText cmd "method")

        match filter.Threshold with
        | Some(AtLeast low) -> addNumeric cmd "t_low" low
        | Some(AtMost high) -> addNumeric cmd "t_high" high
        | Some(Between(low, high)) ->
            addNumeric cmd "t_low" low
            addNumeric cmd "t_high" high
        | None -> ()

    // One read-only round trip on a pooled connection.
    let withConnection (work: NpgsqlConnection -> Async<'T>) : Async<'T> = async {
        use! conn = dataSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask
        return! work conn
    }

    let readPayloads (cmd: NpgsqlCommand) : Async<Fact list> = async {
        use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
        let facts = ResizeArray<Fact>()

        let rec loop () = async {
            let! more = reader.ReadAsync() |> Async.AwaitTask

            if more then
                facts.Add(deserialise (reader.GetString 0))
                return! loop ()
        }

        do! loop ()
        return List.ofSeq facts
    }

    let queryPayloads (sql: string) (bind: NpgsqlCommand -> unit) : Async<Fact list> =
        withConnection (fun conn -> async {
            use cmd = command sql conn null
            bind cmd
            return! readPayloads cmd
        })

    let utc (ticks: int64) = DateTime(ticks, DateTimeKind.Utc)

    // `numeric` crosses the wire as text and is parsed invariantly, so the
    // decimal keeps the scale it was written with (1.0 stays 1.0).
    let parseDecimal (text: string) =
        Decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)

    // Every member row a reader holds, in the `Sql.memberColumns` order.
    let readMemberRows (cmd: NpgsqlCommand) : Async<PopulationMember list> = async {
        use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
        let members = ResizeArray<PopulationMember>()

        let rec loop () = async {
            let! more = reader.ReadAsync() |> Async.AwaitTask

            if more then
                let magnitude =
                    if reader.IsDBNull 3 then
                        None
                    else
                        Some(parseDecimal (reader.GetString 3))

                members.Add {
                    FactId = reader.GetString 0
                    Subject = {
                        Hierarchy = reader.GetString 1
                        Path = reader.GetFieldValue<string[]> 2 |> List.ofArray
                    }
                    Magnitude = magnitude
                    PeriodFrom = utc (reader.GetInt64 4)
                    PeriodTo = utc (reader.GetInt64 5)
                    AsOf = utc (reader.GetInt64 6)
                    MethodIdentity = reader.GetString 7
                }

                return! loop ()
        }

        do! loop ()
        return List.ofSeq members
    }

    let readMembers (sql: string) (bind: NpgsqlCommand -> unit) : Async<PopulationMember list> =
        withConnection (fun conn -> async {
            use cmd = command sql conn null
            bind cmd
            return! readMemberRows cmd
        })

    let bindVisible (scopeId: string) (t: DateTime) (filter: RowFilter) (cmd: NpgsqlCommand) =
        addText cmd "scope" scopeId
        addBigint cmd "t" t.Ticks
        bindFilter cmd filter

    // ─── Point reads ──────────────────────────────────────────────────

    // The competition key and canonical selection — `BlobFactStore`'s,
    // over the same facts, through the same shared selection function.
    let competitionKey (f: Fact) =
        f.Subject, f.Metric, f.Period.From, f.Period.To

    let selectCanonical (heads: Fact list) : Fact list =
        match registry with
        | None -> heads
        | Some reg ->
            PopulationSelection.canonicalHeads
                competitionKey
                (fun f -> Fact.methodIdentity f.Method)
                (fun f -> reg.TryGetMetric f.Metric.Value |> Option.bind _.CanonicalMethod)
                heads

    let pointFilter (query: FactQuery) : RowFilter = {
        RowFilter.none with
            Hierarchy = query.Subject |> Option.map _.Hierarchy
            Path = query.Subject |> Option.map _.Path
            Metric = query.Metric |> Option.map _.Value
            Period = query.PeriodOverlaps
    }

    let byContentAddress (facts: Fact list) =
        facts |> List.sortWith (fun a b -> String.CompareOrdinal(a.FactId, b.FactId))

    // The shared query pipeline, keyed: the heads visible at `t` within
    // the competition scope (subject / metric / period — never the method,
    // so the competition indicator sees every competitor), then the
    // listing — the history when asked, the named method's heads, or the
    // canonical selection.
    let runQuery (scopeId: string) (query: FactQuery) : Async<Fact list * Fact list> = async {
        let t = query.AsOf |> Option.defaultValue (clock().ToUniversalTime())
        let filter = pointFilter query

        let! heads = queryPayloads (Sql.visibleAt table "f.payload" filter) (bindVisible scopeId t filter)
        let heads = byContentAddress heads

        let! listing =
            if query.IncludeSuperseded then
                let historyFilter = {
                    filter with
                        MethodIdentity = query.Method |> Option.map Fact.methodIdentity
                }

                async {
                    let! history =
                        queryPayloads (Sql.historyAt table historyFilter) (bindVisible scopeId t historyFilter)

                    return byContentAddress history
                }
            else
                match query.Method with
                | Some m ->
                    let identity = Fact.methodIdentity m

                    async { return heads |> List.filter (fun f -> Fact.methodIdentity f.Method = identity) }
                | None -> async { return selectCanonical heads }

        return
            heads,
            listing
            |> List.sortBy (fun f -> f.Subject.Hierarchy, f.Metric.Value, f.Period.From)
    }

    let competingMethods (heads: Fact list) (f: Fact) : string list =
        let key = competitionKey f
        let ownMethod = Fact.methodIdentity f.Method

        heads
        |> List.filter (fun g -> competitionKey g = key)
        |> List.map (fun g -> Fact.methodIdentity g.Method)
        |> List.filter (fun identity -> identity <> ownMethod)
        |> List.distinct

    // ─── Population read ──────────────────────────────────────────────

    let stalenessOf (metricDef: Grounding.MetricDefinition option) =
        metricDef
        |> Option.map _.Staleness
        |> Option.defaultValue Grounding.UntilSuperseded

    // The population's row filter; the threshold only when the caller
    // has shown no canonical selection can run before it.
    let populationFilter (query: PopulationQuery) (withThreshold: bool) : RowFilter = {
        RowFilter.none with
            Hierarchy = Some query.Hierarchy
            Level = query.Level
            PathPrefix = query.PathPrefix
            Metric = Some query.Metric.Value
            Period = query.PeriodOverlaps
            MethodIdentity =
                match query.Methods with
                | OneMethod m -> Some(Fact.methodIdentity m)
                | _ -> None
            Threshold = if withThreshold then query.Threshold else None
    }

    // The transaction-time floor a fact must reach to be FRESH at `t` —
    // `Freshness.deriveAt` over a current head, restated as a comparison
    // the database can count. Under `FreshFor window` a head is fresh while
    // `t <= asOf + window`, i.e. `asOf.Ticks >= t.Ticks - window.Ticks`;
    // under the other two policies every current head is fresh. `None`
    // when `asOf + window` could leave `DateTime`'s range (a negative
    // window, or one reaching past `DateTime.MaxValue` from `t`): the
    // shared derivation throws there, so the member read runs it rather
    // than the database quietly answering.
    let freshFloor (policy: Grounding.StalenessPolicy) (t: DateTime) : int64 option =
        match policy with
        | Grounding.UntilSuperseded
        | Grounding.UntilUpstreamChange -> Some Int64.MinValue
        | Grounding.FreshFor window ->
            if window < TimeSpan.Zero || window.Ticks > DateTime.MaxValue.Ticks - t.Ticks then
                None
            else
                Some(t.Ticks - window.Ticks)

    // The full facts of the ranked members, in the members' order.
    let factsInOrder (members: PopulationMember list) (facts: Fact list) : Fact list =
        let byId = facts |> List.map (fun f -> f.FactId, f) |> dict

        members
        |> List.choose (fun m ->
            if byId.ContainsKey m.FactId then
                Some byId[m.FactId]
            else
                None)

    /// The member read (Phase 888): every visible member row crosses the
    /// wire and the shared pipeline decides everything in memory. Runs
    /// when the aggregate read below cannot answer identically — a
    /// canonical-method selection over competing methods, a sum `decimal`
    /// would round, or a freshness window at the edge of `DateTime`.
    let populationFromMembers
        (scopeId: string)
        (query: PopulationQuery)
        (direction: RankDirection)
        (policy: Grounding.StalenessPolicy)
        (selector: string option)
        (t: DateTime)
        : Async<PopulationResult> =
        async {
            // The threshold may run in the database only when the canonical
            // selection cannot apply: selection runs BEFORE the threshold
            // and needs every member of a contested group to decide.
            let thresholdInDatabase =
                match query.Methods with
                | CanonicalMethodOnly -> selector.IsNone
                | AllCompetingMethods
                | OneMethod _ -> true

            let filter = populationFilter query thresholdInDatabase

            let! heads = readMembers (Sql.visibleAt table (Sql.memberColumns "f") filter) (bindVisible scopeId t filter)

            let selected =
                match query.Methods with
                | CanonicalMethodOnly ->
                    PopulationSelection.canonicalHeads
                        PopulationMember.competitionKey
                        _.MethodIdentity
                        (fun _ -> selector)
                        heads
                | AllCompetingMethods
                | OneMethod _ -> heads

            let population =
                match query.Threshold with
                | Some threshold when not thresholdInDatabase ->
                    selected
                    |> List.filter (fun m -> ValueThreshold.satisfiesMagnitude threshold m.Magnitude)
                | _ -> selected

            let stats =
                PopulationStats.ofMembers (fun m -> Freshness.deriveAt policy m.AsOf true t) population

            let k = PopulationQuery.effectiveTopK query
            let ranked = PopulationRanking.rankMembers direction population
            let top = ranked |> List.truncate k

            let! facts =
                match top with
                | [] -> async { return [] }
                | _ ->
                    queryPayloads (Sql.getByIds table) (fun cmd ->
                        addText cmd "scope" scopeId
                        addTextArray cmd "ids" (top |> List.map _.FactId))

            return {
                Ranked = factsInOrder top facts
                Direction = direction
                EffectiveTopK = k
                Truncated = List.length ranked > k
                Stats = stats
            }
        }

    /// The aggregate read (Phase 940): the summary, the method mix and the
    /// top k computed in the database, in ONE repeatable-read snapshot so
    /// the three agree with each other under concurrent writers. `None`
    /// when the answer would not be the shared pipeline's — the caller
    /// then runs the member read. The threshold is always in the database
    /// here: with `requireSingleMethod` (a canonical selector is declared)
    /// the same snapshot first proves the unthresholded population carries
    /// one method identity, where `PopulationSelection.canonicalHeads` is
    /// the identity (its own first branch) and so cannot move the
    /// threshold; two identities decline.
    let populationFromAggregates
        (scopeId: string)
        (query: PopulationQuery)
        (direction: RankDirection)
        (freshFrom: int64)
        (requireSingleMethod: bool)
        (t: DateTime)
        : Async<PopulationResult option> =
        withConnection (fun conn -> async {
            let filter = populationFilter query true
            let k = PopulationQuery.effectiveTopK query

            use! tx =
                conn.BeginTransactionAsync(Data.IsolationLevel.RepeatableRead).AsTask()
                |> Async.AwaitTask

            let bound (sql: string) =
                let cmd = command sql conn tx
                bindVisible scopeId t filter cmd
                cmd

            let! competing = async {
                if requireSingleMethod then
                    let unthresholded = populationFilter query false
                    use probe = command (Sql.populationMethodProbe table unthresholded) conn tx
                    bindVisible scopeId t unthresholded probe
                    use! reader = probe.ExecuteReaderAsync() |> Async.AwaitTask
                    let! first = reader.ReadAsync() |> Async.AwaitTask
                    let! second = reader.ReadAsync() |> Async.AwaitTask
                    return first && second
                else
                    return false
            }

            // Everything past the method probe, in the same snapshot.
            let summarise () = async {
                // The summary row.
                use summary = bound (Sql.populationSummary table filter)
                addBigint summary "fresh_from" freshFrom

                let! row = async {
                    use! reader = summary.ExecuteReaderAsync() |> Async.AwaitTask
                    let! _ = reader.ReadAsync() |> Async.AwaitTask

                    let text (i: int) =
                        if reader.IsDBNull i then None else Some(reader.GetString i)

                    return
                        reader.GetInt64 0,
                        reader.GetInt64 1,
                        text 2,
                        text 3,
                        text 4,
                        reader.GetBoolean 5,
                        (if reader.IsDBNull 6 then 0L else reader.GetInt64 6),
                        (if reader.IsDBNull 7 then 0L else reader.GetInt64 7),
                        reader.GetInt64 8,
                        reader.GetInt64 9
                }

                let (factCount,
                     comparableCount,
                     minimum,
                     maximum,
                     total,
                     sumExact,
                     periodFrom,
                     periodTo,
                     freshCount,
                     pathHashes) =
                    row

                if not sumExact then
                    // A partial sum would round in `decimal`, and the member
                    // read's left-to-right fold rounds where it rounds.
                    do! tx.CommitAsync() |> Async.AwaitTask
                    return None
                elif factCount = 0L then
                    do! tx.CommitAsync() |> Async.AwaitTask

                    return
                        Some {
                            Ranked = []
                            Direction = direction
                            EffectiveTopK = k
                            Truncated = false
                            Stats = PopulationStats.empty
                        }
                else
                    let! subjectCount = async {
                        if pathHashes = factCount then
                            return factCount
                        else
                            use exact = bound (Sql.populationSubjects table filter)
                            let! n = exact.ExecuteScalarAsync() |> Async.AwaitTask
                            return Convert.ToInt64 n
                    }

                    use mixCommand = bound (Sql.populationMethodMix table filter)

                    let! mix = async {
                        use! reader = mixCommand.ExecuteReaderAsync() |> Async.AwaitTask
                        let rows = ResizeArray<string * int>()

                        let rec loop () = async {
                            let! more = reader.ReadAsync() |> Async.AwaitTask

                            if more then
                                rows.Add((reader.GetString 0, int (reader.GetInt64 1)))
                                return! loop ()
                        }

                        do! loop ()
                        return List.ofSeq rows
                    }

                    use topCommand = bound (Sql.populationTop table filter direction)
                    addInt topCommand "k" k
                    let! top = readMemberRows topCommand

                    // The shared comparator orders the k rows the database chose.
                    let top = PopulationRanking.rankMembers direction top

                    let! facts =
                        match top with
                        | [] -> async { return [] }
                        | _ -> async {
                            use payloads = command (Sql.getByIds table) conn tx
                            addText payloads "scope" scopeId
                            addTextArray payloads "ids" (top |> List.map _.FactId)
                            return! readPayloads payloads
                          }

                    do! tx.CommitAsync() |> Async.AwaitTask

                    // `PopulationStats.ofMembersWithFreshness`, field for field:
                    // the extremes are `numeric` min / max (equal values at two
                    // scales are equal decimals; which of them the fold keeps
                    // is its enumeration order's, which neither store pins),
                    // the sum is exact and — `sumExact` — the fold's own, and
                    // the mean is the fold's decimal division of it.
                    let comparable = int comparableCount

                    let stats: PopulationStats = {
                        SubjectCount = int subjectCount
                        FactCount = int factCount
                        ComparableCount = comparable
                        NonComparableCount = int factCount - comparable
                        PeriodFrom = Some(utc periodFrom)
                        PeriodTo = Some(utc periodTo)
                        Minimum = minimum |> Option.map parseDecimal
                        Maximum = maximum |> Option.map parseDecimal
                        Mean =
                            if comparable = 0 then
                                None
                            else
                                total |> Option.map (fun s -> parseDecimal s / decimal comparable)
                        Freshness = {
                            FreshCount = int freshCount
                            StaleCount = int factCount - int freshCount
                        }
                        MethodMix = mix |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
                    }

                    return
                        Some {
                            Ranked = factsInOrder top facts
                            Direction = direction
                            EffectiveTopK = k
                            Truncated = comparable > k
                            Stats = stats
                        }
            }

            if competing then
                do! tx.CommitAsync() |> Async.AwaitTask
                return None
            else
                return! summarise ()
        })

    let runPopulation (scopeId: string) (query: PopulationQuery) : Async<Result<PopulationResult, string>> = async {
        let metricDef = registry |> Option.bind (fun r -> r.TryGetMetric query.Metric.Value)

        // Ordering first: a refusal costs no read (GP 9), exactly as the
        // blob store resolves it.
        match PopulationOrdering.resolve query.Metric.Value query.Ordering (metricDef |> Option.map _.Direction) with
        | Error refusal -> return Error refusal
        | Ok direction ->
            let t = query.AsOf |> Option.defaultValue (clock().ToUniversalTime())
            let selector = metricDef |> Option.bind _.CanonicalMethod
            let policy = stalenessOf metricDef

            // A canonical selection can only change the population when
            // two methods are present to choose between; the aggregate read
            // checks that in its own snapshot.
            let requireSingleMethod =
                match query.Methods with
                | CanonicalMethodOnly -> selector.IsSome
                | AllCompetingMethods
                | OneMethod _ -> false

            let! aggregated =
                match freshFloor policy t with
                | Some freshFrom -> populationFromAggregates scopeId query direction freshFrom requireSingleMethod t
                | None -> async { return None }

            match aggregated with
            | Some result -> return Ok result
            | None ->
                let! result = populationFromMembers scopeId query direction policy selector t
                return Ok result
    }

    // ─── Writes ───────────────────────────────────────────────────────

    // Any cause counts: the attempt is awaited through `Async.AwaitTask`, so
    // the race arrives wrapped (Phase 972's shared `causes` sees through it).
    let isRaceLost (ex: exn) : bool =
        causes ex
        |> List.exists (function
            | :? LostWriteRace -> true
            | ProviderException(p: PostgresException) ->
                p.SqlState = PostgresErrorCodes.UniqueViolation
                || p.SqlState = PostgresErrorCodes.SerializationFailure
                || p.SqlState = PostgresErrorCodes.DeadlockDetected
            | _ -> false)

    // One write attempt: one transaction. Derivation mirrors
    // `BlobFactStore`'s write core exactly — content address, idempotency
    // against the stored set and earlier drafts of the same batch, the
    // lineage head from the table or from an earlier draft, and the
    // strictly-increasing `AsOf` rule — so a batch settles as the same
    // sequence of scalar asserts would.
    let attemptWrite (scopeId: string) (drafts: FactDraft list) : Async<Disposition list> = async {
        use! conn = dataSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask
        use! tx = conn.BeginTransactionAsync().AsTask() |> Async.AwaitTask

        let addressed =
            drafts
            |> List.map (fun d ->
                let inputHashes = Fact.effectiveInputHashes d.Method d.Evidence d.Value
                d, Fact.compute d.Subject d.Metric d.Period d.Method inputHashes)

        // Writers of the same lineage serialise here rather than racing to
        // the constraint and retrying: a small write locks its lineages
        // (under a shared scope lock), a large batch takes the scope. The
        // constraints below remain the guarantee — the locks only make the
        // common contended case wait instead of spin.
        let lineages =
            addressed
            |> List.map (fun (d, _) -> lineageHashOf d.Subject d.Metric d.Period d.Method)
            |> List.distinct
            |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

        do! async {
            let exclusive = List.length lineages > lineageLockLimit

            use scopeLock =
                command
                    (if exclusive then
                         Sql.lockScopeExclusive
                     else
                         Sql.lockScopeShared)
                    conn
                    tx

            addText scopeLock "scope" scopeId
            let! _ = scopeLock.ExecuteNonQueryAsync() |> Async.AwaitTask

            if not exclusive then
                use lineageLocks = command Sql.lockLineages conn tx
                addText lineageLocks "scope" scopeId
                addTextArray lineageLocks "lineages" lineages
                let! _ = lineageLocks.ExecuteNonQueryAsync() |> Async.AwaitTask
                ()
        }

        let! stored = async {
            use cmd = command (Sql.existing table) conn tx
            addText cmd "scope" scopeId
            addTextArray cmd "ids" (addressed |> List.map snd |> List.distinct)
            use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
            let ids = HashSet<string>(StringComparer.Ordinal)

            let rec loop () = async {
                let! more = reader.ReadAsync() |> Async.AwaitTask

                if more then
                    ids.Add(reader.GetString 0) |> ignore
                    return! loop ()
            }

            do! loop ()
            return ids
        }

        if addressed |> List.forall (fun (_, factId) -> stored.Contains factId) then
            do! tx.CommitAsync() |> Async.AwaitTask

            return
                addressed
                |> List.map (fun (_, factId) -> {
                    Outcome = BatchIdempotent
                    FactId = factId
                    Written = None
                })
        else
            let touched =
                addressed
                |> List.filter (fun (_, factId) -> not (stored.Contains factId))
                |> List.map (fun (d, _) -> lineageHashOf d.Subject d.Metric d.Period d.Method)
                |> List.distinct

            let heads = Dictionary<string, Fact>(StringComparer.Ordinal)

            let! headFacts = async {
                use cmd = command (Sql.headsOf table) conn tx
                addText cmd "scope" scopeId
                addTextArray cmd "lineages" touched
                return! readPayloads cmd
            }

            for f in headFacts do
                heads[lineageKeyOf f] <- f

            let originalHeads = Dictionary<string, Fact>(heads, StringComparer.Ordinal)
            let seen = HashSet<string>(stored, StringComparer.Ordinal)
            let dispositions = ResizeArray<Disposition>()
            let writtenFacts = ResizeArray<Fact>()

            for draft, factId in addressed do
                if seen.Contains factId then
                    dispositions.Add {
                        Outcome = BatchIdempotent
                        FactId = factId
                        Written = None
                    }
                else
                    let key = Fact.lineageKey draft.Subject draft.Metric draft.Period draft.Method

                    let currentHead =
                        match heads.TryGetValue key with
                        | true, head -> Some head
                        | _ -> None

                    let now = clock().ToUniversalTime()

                    let asOf =
                        match currentHead with
                        | Some head when head.AsOf >= now -> head.AsOf.AddTicks 1L
                        | _ -> now

                    let fact = {
                        FactId = factId
                        Subject = draft.Subject
                        Metric = draft.Metric
                        Value = draft.Value
                        Period = draft.Period
                        AsOf = asOf
                        Method = draft.Method
                        Evidence = draft.Evidence
                        Confidence = draft.Confidence
                        Supersedes = currentHead |> Option.map _.FactId
                        Disclosure = draft.Disclosure
                    }

                    seen.Add factId |> ignore
                    heads[key] <- fact
                    writtenFacts.Add fact

                    dispositions.Add {
                        Outcome =
                            if currentHead.IsSome then
                                BatchSuperseding
                            else
                                BatchAsserted
                        FactId = factId
                        Written = Some fact
                    }

            // The stored heads this transaction retires: every original
            // head whose lineage now has a newer head.
            let retired =
                originalHeads
                |> Seq.filter (fun kv -> heads[kv.Key].FactId <> kv.Value.FactId)
                |> Seq.map _.Value.FactId
                |> List.ofSeq

            if not (List.isEmpty retired) then
                use cmd = command (Sql.clearHeads table) conn tx
                addText cmd "scope" scopeId
                addTextArray cmd "ids" retired
                let! cleared = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask

                if cleared <> List.length retired then
                    raise (
                        LostWriteRace(
                            sprintf
                                "%d of %d superseded heads were already retired"
                                (List.length retired - cleared)
                                retired.Length
                        )
                    )

            // The rows. A fact is the head of its lineage when it is the
            // last fact this batch wrote in that lineage.
            let finalHeads =
                HashSet<string>(heads.Values |> Seq.map _.FactId, StringComparer.Ordinal)

            let! _ = async {
                use importer = conn.BeginBinaryImport(Sql.copyIn table)

                for f in writtenFacts do
                    FactRow.write importer scopeId f (finalHeads.Contains f.FactId)

                return importer.Complete()
            }

            do! tx.CommitAsync() |> Async.AwaitTask
            return List.ofSeq dispositions
    }

    // The write core: one transaction, re-attempted when it loses a race
    // to a concurrent writer. A lost race rolls back whole — the batch is
    // all-or-nothing — and the next attempt re-derives against the winner.
    let writeDrafts (scopeId: string) (drafts: FactDraft list) : Async<Result<Disposition list, string>> =
        let rec attempt (n: int) = async {
            let! outcome = attemptWrite scopeId drafts |> Async.Catch

            match outcome with
            | Choice1Of2 dispositions -> return Ok dispositions
            | Choice2Of2 ex when isRaceLost ex && n < options.MaxWriteAttempts -> return! attempt (n + 1)
            | Choice2Of2 ex ->
                let message =
                    match ex with
                    | :? AggregateException as a when a.InnerExceptions.Count = 1 -> a.InnerExceptions[0].Message
                    | _ -> ex.Message

                return Error(sprintf "fact store write failed: %s" message)
        }

        attempt 1

    let writeEvent (scopeId: string) (occurredAt: DateTime) (eventType: string) (payload: string) : Async<unit> =
        events.Write {
            Id = Guid.NewGuid()
            OccurredAt = occurredAt
            ScopeId = scopeId
            SourceModule = FactEvents.SourceModule
            EventType = eventType
            Payload = payload
        }

    // `BlobFactStore`'s caller-facing refusal for a malformed batch, verbatim.
    let renderOffenders (total: int) (offenders: (int * FactDraft * string list) list) : string =
        let named =
            offenders
            |> List.truncate 10
            |> List.map (fun (index, draft, defects) ->
                sprintf
                    "#%d %s / %s: %s"
                    index
                    (SubjectRef.toString draft.Subject)
                    draft.Metric.Value
                    (String.concat ", " defects))
            |> String.concat "; "

        let more =
            let hidden = List.length offenders - 10
            if hidden > 0 then sprintf " (and %d more)" hidden else ""

        sprintf
            "fact store batch rejected: %d of %d drafts are malformed and none were committed — %s%s"
            (List.length offenders)
            total
            named
            more

    let load (scopeId: string) (factId: string) : Async<Fact option> = async {
        let! facts =
            queryPayloads (Sql.getById table) (fun cmd ->
                addText cmd "scope" scopeId
                addText cmd "fact_id" factId)

        return List.tryHead facts
    }

    /// The query plan PostgreSQL chose for `query`'s visible-heads read,
    /// executed (`EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`), as JSON text.
    /// An operator's answer to "which index did this read use, and how
    /// many rows did it touch" — the rows a read touches is the figure
    /// that survives a change of hardware.
    member _.ExplainQuery(scopeId: string, query: FactQuery) : Async<string> =
        let t = query.AsOf |> Option.defaultValue (clock().ToUniversalTime())
        let filter = pointFilter query

        withConnection (fun conn -> async {
            use cmd =
                command
                    ("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "
                     + Sql.visibleAt table "f.payload" filter)
                    conn
                    null

            bindVisible scopeId t filter cmd
            let! plan = cmd.ExecuteScalarAsync() |> Async.AwaitTask
            return string plan
        })

    /// Phase 962 — the query plan PostgreSQL chose for `query`'s population
    /// summary (the statement that visits the whole selected population),
    /// executed (`EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`), as JSON text.
    /// The operator's check that the covering population index is in place
    /// and answers the read: an `Index Only Scan` on `<table>_population_idx`,
    /// touching the selected population rather than the table. A
    /// `VerifyOnly` deployment, which provisions its indexes out of band,
    /// confirms the index this way.
    member _.ExplainPopulation(scopeId: string, query: PopulationQuery) : Async<string> =
        let t = query.AsOf |> Option.defaultValue (clock().ToUniversalTime())
        let filter = populationFilter query true

        withConnection (fun conn -> async {
            use cmd =
                command ("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + Sql.populationSummary table filter) conn null

            bindVisible scopeId t filter cmd
            // The freshness floor changes a count, never the plan.
            addBigint cmd "fresh_from" 0L
            let! plan = cmd.ExecuteScalarAsync() |> Async.AwaitTask
            return string plan
        })

    /// The pool and options this store reads and writes through — the
    /// Phase 941 migration writes its rows into the same table, through
    /// the same connection pool, as the store it verifies them with.
    member internal _.DataSource: NpgsqlDataSource = dataSource

    member internal _.Options: PostgresFactStoreOptions = options

    interface IDisposable with
        member _.Dispose() =
            if ownsDataSource then
                dataSource.Dispose()

    interface IFactStore with

        // Phase 797 — the request-path form keys on the resolved scope's
        // shard, exactly as the string form does.
        member this.Assert(scope: ResolvedScope, draft: FactDraft) : Async<Result<Fact, string>> =
            (this :> IFactStore).Assert(scope.ScopeId, draft)

        member this.AssertBatch
            (scope: ResolvedScope, drafts: FactDraft list)
            : Async<Result<BatchAssertReceipt, string>> =
            (this :> IFactStore).AssertBatch(scope.ScopeId, drafts)

        member this.Get(scope: ResolvedScope, factId: string) : Async<Fact option> =
            (this :> IFactStore).Get(scope.ScopeId, factId)

        member this.Query(scope: ResolvedScope, query: FactQuery) : Async<Fact list> =
            (this :> IFactStore).Query(scope.ScopeId, query)

        member this.QueryWithCompetition(scope: ResolvedScope, query: FactQuery) : Async<FactWithCompetition list> =
            (this :> IFactStore).QueryWithCompetition(scope.ScopeId, query)

        member this.QuerySupersessionChain(scope: ResolvedScope, factId: string) : Async<Fact list> =
            (this :> IFactStore).QuerySupersessionChain(scope.ScopeId, factId)

        member this.QueryPopulation
            (scope: ResolvedScope, query: PopulationQuery)
            : Async<Result<PopulationResult, string>> =
            (this :> IFactStore).QueryPopulation(scope.ScopeId, query)

        member _.Assert(scopeId: string, draft: FactDraft) : Async<Result<Fact, string>> = async {
            try
                let! written = writeDrafts scopeId [ draft ]

                match written with
                | Error e -> return Error e
                | Ok [ { Written = Some fact } ] ->
                    let assertedPayload: FactAssertedEvent = {
                        FactId = fact.FactId
                        Subject = SubjectRef.toString fact.Subject
                        Metric = fact.Metric.Value
                        Method = Fact.methodIdentity fact.Method
                        Disclosure = Disclosure.toString fact.Disclosure
                        AsOf = fact.AsOf
                    }

                    do!
                        writeEvent
                            scopeId
                            fact.AsOf
                            FactEvents.AssertedType
                            (JsonSerializer.Serialize(assertedPayload, jsonOptions))

                    match fact.Supersedes with
                    | Some supersededId ->
                        let supersededPayload: FactSupersededEvent = {
                            NewFactId = fact.FactId
                            SupersededFactId = supersededId
                            Subject = SubjectRef.toString fact.Subject
                            Metric = fact.Metric.Value
                            AsOf = fact.AsOf
                        }

                        do!
                            writeEvent
                                scopeId
                                fact.AsOf
                                FactEvents.SupersededType
                                (JsonSerializer.Serialize(supersededPayload, jsonOptions))
                    | None -> ()

                    return Ok fact
                | Ok [ { FactId = factId; Written = None } ] ->
                    // Idempotent (law L2): the identical tuple is stored —
                    // return it unchanged, with no write and no audit.
                    let! existing = load scopeId factId

                    match existing with
                    | Some fact -> return Ok fact
                    | None -> return Error(sprintf "fact store read failed: %s is stored but unreadable" factId)
                | Ok other ->
                    return Error(sprintf "fact store assert failed: %d dispositions for one draft" other.Length)
            with ex ->
                return Error(sprintf "fact store assert failed: %s" ex.Message)
        }

        member _.AssertBatch(scopeId: string, drafts: FactDraft list) : Async<Result<BatchAssertReceipt, string>> = async {
            try
                let offenders =
                    drafts
                    |> List.mapi (fun index draft -> index, draft, FactDraft.defects draft)
                    |> List.filter (fun (_, _, defects) -> not (List.isEmpty defects))

                if not (List.isEmpty offenders) then
                    return Error(renderOffenders (List.length drafts) offenders)
                elif List.isEmpty drafts then
                    return Ok BatchAssertReceipt.empty
                else
                    let! written = writeDrafts scopeId drafts

                    match written with
                    | Error e -> return Error e
                    | Ok dispositions ->
                        let receipt =
                            dispositions
                            |> List.map (fun d -> d.Outcome, d.FactId)
                            |> BatchAssertReceipt.ofDispositions

                        let facts = dispositions |> List.choose _.Written

                        let asOf =
                            match facts with
                            | [] -> clock().ToUniversalTime()
                            | _ -> facts |> List.map _.AsOf |> List.max

                        let payload: FactBatchAssertedEvent = { Receipt = receipt; AsOf = asOf }

                        do!
                            writeEvent
                                scopeId
                                asOf
                                FactEvents.BatchAssertedType
                                (JsonSerializer.Serialize(payload, jsonOptions))

                        return Ok receipt
            with ex ->
                return Error(sprintf "fact store batch assert failed: %s" ex.Message)
        }

        member _.Get(scopeId: string, factId: string) : Async<Fact option> = load scopeId factId

        member _.Query(scopeId: string, query: FactQuery) : Async<Fact list> = async {
            let! _, listing = runQuery scopeId query
            return listing
        }

        member _.QueryWithCompetition(scopeId: string, query: FactQuery) : Async<FactWithCompetition list> = async {
            let! heads, listing = runQuery scopeId query

            return
                listing
                |> List.map (fun f -> {
                    Fact = f
                    CompetingMethods = competingMethods heads f
                })
        }

        member _.QueryPopulation(scopeId: string, query: PopulationQuery) : Async<Result<PopulationResult, string>> =
            runPopulation scopeId query

        member _.QuerySupersessionChain(scopeId: string, factId: string) : Async<Fact list> = async {
            let! target = load scopeId factId

            match target with
            | None -> return []
            | Some f ->
                let key = lineageKeyOf f

                let! lineage =
                    queryPayloads (Sql.lineage table) (fun cmd ->
                        addText cmd "scope" scopeId
                        addText cmd "lineage" (lineageHashOf f.Subject f.Metric f.Period f.Method))

                return
                    lineage
                    |> byContentAddress
                    |> List.filter (fun g -> lineageKeyOf g = key)
                    |> List.sortBy _.AsOf
        }

/// Construction for `PostgresFactStore`.
module PostgresFactStore =

    let private fail (message: string) =
        raise (PostgresFactStoreException message)

    let private validateOrFail (options: PostgresFactStoreOptions) =
        match PostgresFactStoreOptions.validate options with
        | [] -> ()
        | problems -> fail ("[PostgresFactStore] Invalid options: " + String.concat " " problems)

    // Connectivity + schema reconciliation, once, at construction — never a
    // deferred failure on the first read of a live request.
    let private probeAndMigrate (dataSource: NpgsqlDataSource) (options: PostgresFactStoreOptions) : Async<unit> = async {
        try
            use cmd = dataSource.CreateCommand "SELECT 1"
            let! _ = cmd.ExecuteScalarAsync() |> Async.AwaitTask
            ()
        with ex ->
            fail (
                sprintf
                    "[PostgresFactStore] Cannot reach the configured PostgreSQL database: %s. The store is not composed — check the connection string, network reachability and credentials."
                    ex.Message
            )

        match options.SchemaMode with
        | AutoMigrate ->
            for statement in Sql.migration options.Table do
                try
                    use cmd = dataSource.CreateCommand statement
                    let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                    ()
                with ex ->
                    fail (
                        sprintf
                            "[PostgresFactStore] Schema migration failed on `%s`: %s. Grant this role DDL rights on the target schema, or provision the table out of band (the DDL is in the companion README) and compose with SchemaMode = VerifyOnly."
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
                        "[PostgresFactStore] Table '%s' does not exist and SchemaMode = VerifyOnly. Provision it with the DDL in the companion README, or compose with SchemaMode = AutoMigrate."
                        options.Table
                )
    }

    /// Build a store over a data source the CALLER owns (a shared pool).
    /// Disposing the store leaves the data source open. Options are
    /// validated and the database probed (or migrated) before the store is
    /// returned, so a store that is returned works.
    ///
    /// `registry` supplies the D19 canonical-method selection, the
    /// registry-directed population ordering and the staleness policy —
    /// `None` behaves exactly as a registry-less `BlobFactStore`. `clock`
    /// stamps transaction time (`AsOf`).
    let createWithDataSource
        (dataSource: NpgsqlDataSource)
        (options: PostgresFactStoreOptions)
        (events: IEventStore)
        (registry: Grounding.IMetricRegistry option)
        (clock: unit -> DateTime)
        : PostgresFactStore =
        validateOrFail options
        probeAndMigrate dataSource options |> Async.RunSynchronously
        new PostgresFactStore(dataSource, options, events, registry, clock, false)

    /// Build a store from a connection string. The store owns the pooled
    /// `NpgsqlDataSource` it creates and disposes it with itself.
    let create
        (connectionString: string)
        (options: PostgresFactStoreOptions)
        (events: IEventStore)
        (registry: Grounding.IMetricRegistry option)
        (clock: unit -> DateTime)
        : PostgresFactStore =
        validateOrFail options

        if String.IsNullOrWhiteSpace connectionString then
            fail
                "[PostgresFactStore] The connection string is empty. Supply it from ISecretStore / configuration at compose time."

        let dataSource =
            try
                NpgsqlDataSource.Create connectionString
            with ex ->
                fail (sprintf "[PostgresFactStore] The connection string could not be parsed: %s" ex.Message)

        try
            probeAndMigrate dataSource options |> Async.RunSynchronously
        with _ ->
            dataSource.Dispose()
            reraise ()

        new PostgresFactStore(dataSource, options, events, registry, clock, true)

/// Composition for `PostgresFactStore` (Phase 888).
module PostgresFactStoreCompose =

    /// Replace the composed fact store with a `PostgresFactStore` over
    /// `connectionString` — `FactsCompose.withFactStoreImplementation` with
    /// the store built on first use from the composed `IEventStore`, the
    /// metric registry when one is registered, and `DateTime.UtcNow` as the
    /// transaction-time clock. Every registration the fact tier builds over
    /// `IFactStore` (the evidence source, the disclosure gate, the resolver,
    /// the provenance graph, reactive recomputation, the fact tools) follows
    /// the replacement. Construction probes (or migrates) the schema, so a
    /// misconfigured database fails the first resolution loudly rather than
    /// a later request.
    ///
    /// Requires the fact tier (`ServerConfig.FactStore = EnabledFactStore`)
    /// and goes straight after `FactsCompose.withFactStore`; under
    /// `NoFactStore` the app is returned unchanged.
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withFactStore
    /// |> PostgresFactStoreCompose.withPostgresFactStore connectionString PostgresFactStoreOptions.defaults
    /// |> ServerApp.run
    /// ```
    let withPostgresFactStore
        (connectionString: string)
        (options: PostgresFactStoreOptions)
        (app: ServerApp)
        : ServerApp =
        app
        |> FactsCompose.withFactStoreImplementation "postgres" (fun sp ->
            let registry =
                match sp.GetService(typeof<Grounding.IMetricRegistry>) with
                | :? Grounding.IMetricRegistry as r -> Some r
                | _ -> None

            PostgresFactStore.create
                connectionString
                options
                (sp.GetRequiredService<IEventStore>())
                registry
                (fun () -> DateTime.UtcNow)
            :> IFactStore)