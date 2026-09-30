// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.FactStores.Postgres

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open Npgsql
open NpgsqlTypes
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Facts

// ─── Phase 941 — migrating a BlobFactStore into the Postgres store ──
//
// A deployment that has run on `BlobFactStore` moves its facts into the
// table without losing history, and proves the copy faithful.
//
// **Why not `IFactStore.Assert`.** An assert stamps a NEW transaction time
// from the target's clock. Every AsOf read (law L4) depends on each fact's
// original `AsOf` and its `Supersedes` link, so a copy through `Assert`
// answers "what did we know at t" differently from the source — the live
// test pack pins that before it shows the import answering identically.
// The rows are therefore written raw, through the store's own row
// projection (`FactRow.write`, the one its write core uses), with the
// source's content address, transaction time, supersession link and head
// flag.
//
// **Per scope, in pages, resumably.** A scope's facts are read whole from
// `BlobFactStore.ExportScope` (the blob store itself reads a whole scope
// for a population read or an assert, so this is no larger than what it
// already does), checked (every chain linear, one head per lineage,
// transaction time strictly increasing along a chain), ordered by
// (transaction time, content address) and written a page per transaction.
// Each page stages its rows with a binary COPY and inserts them with
// `ON CONFLICT (scope, fact_id) DO NOTHING`: the content-address primary
// key makes a replayed row a no-op, so a page written twice writes it
// once. The same transaction records the scope's progress in
// `<table>_migration`, so a run that is interrupted resumes after its last
// committed page, and a scope already verified over the same source facts
// (a digest of their content addresses) is skipped.
//
// **Verified.** After the copy, a differential check compares source and
// target: every row's presence, payload, transaction time, supersession
// column, lineage hash and head flag; every lineage's chain as the target's
// columns walk it; and the AsOf reads of both stores at sampled
// transaction times. Each difference names its fact id.
//
// **Audit.** One `FactStoreMigrated` record per scope per run, under the
// reserved `_facts` source module, carrying the source, the target, the
// counts and the verification result. The per-fact `FactAsserted` history
// already lives in `IEventStore` and is not the fact store's to copy, so
// no per-fact event is re-emitted.
//
// Writers must be stopped while a scope migrates: the check is a snapshot
// comparison. Each page also takes the scope's exclusive write lock, so a
// Postgres-side writer that was not stopped waits rather than interleaves.

/// How `FactStoreMigration.migrate` pages, samples and names its source.
type FactStoreMigrationOptions = {
    /// Facts written per transaction — also the granularity a resumed run
    /// continues from.
    PageSize: int
    /// Transaction times the verification replays AsOf reads at, chosen
    /// evenly from the source's own; each is read at the instant and one
    /// tick before it, and "as of the end of time" is always read.
    AsOfSamples: int
    /// Migrate a scope whose source holds blobs that do not read, parse or
    /// sit under their own content address. `false` (the default) refuses
    /// such a scope and names each blob. The blob store's own reads skip
    /// them, so accepting them preserves every read — but that is an
    /// operator's decision, never a default.
    AllowUnreadableSource: bool
    /// The source's name in progress rows and audit records.
    SourceName: string
}

/// Defaults and validation for `FactStoreMigrationOptions`.
module FactStoreMigrationOptions =

    /// Pages of 1,000 facts, 16 sampled transaction times, unreadable
    /// source blobs refused, source named `BlobFactStore`.
    let defaults: FactStoreMigrationOptions = {
        PageSize = 1000
        AsOfSamples = 16
        AllowUnreadableSource = false
        SourceName = "BlobFactStore"
    }

    /// Every problem with `options`, empty when they are usable.
    let validate (options: FactStoreMigrationOptions) : string list = [
        if options.PageSize < 1 then
            sprintf "PageSize must be at least 1 (got %d)." options.PageSize
        if options.AsOfSamples < 0 then
            sprintf "AsOfSamples must not be negative (got %d)." options.AsOfSamples
        if String.IsNullOrWhiteSpace options.SourceName then
            "SourceName must not be empty."
    ]

/// One way a migrated scope disagrees with its source, or the source with
/// itself — each names the fact (or blob, or lineage) it is about.
type MigrationDifference =
    /// A source fact with no target row.
    | MissingOnTarget of factId: string
    /// A target row with no source fact.
    | ExtraOnTarget of factId: string
    /// A target column that says something the source fact does not.
    | ColumnDiffers of factId: string * column: string * source: string * target: string
    /// The target's head flag disagrees with the source's supersession
    /// edges (a head is a fact no other fact supersedes).
    | HeadDiffers of factId: string * sourceHead: bool * targetHead: bool
    /// A lineage's chain, earliest first, as each side walks it — the
    /// source through `Supersedes`, the target from its head row through
    /// the `supersedes` column.
    | ChainDiffers of lineageHash: string * source: string list * target: string list
    /// An AsOf read (current heads, or the history listing) at `asOf`
    /// returns the fact on one side only.
    | AsOfReadDiffers of asOf: DateTime * factId: string * visibleOnSource: bool
    /// The source breaks a fact-base law, so its copy could not keep
    /// "one current head per lineage" or a linear chain.
    | InvalidSource of factId: string * reason: string
    /// A source blob that did not read, parse, or sit under its own id.
    | UnreadableSource of blobName: string * reason: string

/// Rendering for `MigrationDifference`.
module MigrationDifference =

    let private ids (xs: string list) = "[" + String.concat ", " xs + "]"

    /// One line naming the difference and the fact it is about.
    let describe (difference: MigrationDifference) : string =
        match difference with
        | MissingOnTarget id -> sprintf "fact %s: missing on the target" id
        | ExtraOnTarget id -> sprintf "fact %s: on the target but not the source" id
        | ColumnDiffers(id, column, source, target) ->
            sprintf "fact %s: column %s is %s on the source, %s on the target" id column source target
        | HeadDiffers(id, sourceHead, targetHead) ->
            sprintf "fact %s: head on the source %b, head on the target %b" id sourceHead targetHead
        | ChainDiffers(lineage, source, target) ->
            sprintf "lineage %s: chain %s on the source, %s on the target" lineage (ids source) (ids target)
        | AsOfReadDiffers(asOf, id, visibleOnSource) ->
            sprintf
                "fact %s: visible as of %s on the %s only"
                id
                (asOf.ToString("o"))
                (if visibleOnSource then "source" else "target")
        | InvalidSource(id, reason) -> sprintf "fact %s: invalid source: %s" id reason
        | UnreadableSource(blob, reason) -> sprintf "blob %s: unreadable source: %s" blob reason

/// What happened to one scope.
type ScopeMigrationOutcome =
    /// Every row is on the target and the verification found no difference.
    | Verified
    /// An earlier run verified this scope over the same source facts;
    /// nothing was written and no audit record was added.
    | AlreadyVerified
    /// The verification found differences (the report lists them).
    | VerificationFailed
    /// The source breaks a fact-base law, or holds unreadable blobs; nothing
    /// was written.
    | SourceRefused
    /// A page failed to write; pages already committed stay, and the next
    /// run resumes after them.
    | CopyFailed of error: string

/// One scope's migration (or verification) result.
type ScopeMigrationReport = {
    /// The scope (the same id on both sides).
    Scope: string
    /// What happened.
    Outcome: ScopeMigrationOutcome
    /// Facts the source holds.
    SourceFacts: int
    /// Rows the target holds for the scope afterwards.
    TargetFacts: int
    /// Current heads (facts no other fact supersedes) in the source.
    Heads: int
    /// Distinct lineages in the source.
    Lineages: int
    /// Rows this run wrote. A replayed row writes nothing.
    Inserted: int
    /// Facts an earlier, interrupted run had already committed.
    ResumedFrom: int
    /// Every difference found, each naming its fact.
    Differences: MigrationDifference list
}

/// The result of a migration (or verification) run over several scopes.
type FactStoreMigrationReport = {
    /// One entry per scope, in the order given.
    Scopes: ScopeMigrationReport list
} with

    /// Every scope verified, now or by an earlier run.
    member this.Passed =
        this.Scopes
        |> List.forall (fun s ->
            match s.Outcome with
            | Verified
            | AlreadyVerified -> true
            | _ -> false)

/// Payload of a `FactStoreMigrated` audit event — one per scope per run.
type FactStoreMigratedEvent = {
    /// The source's name (`FactStoreMigrationOptions.SourceName`).
    Source: string
    /// The target, as `postgres:<table>`.
    Target: string
    /// `verified`, `verification-failed`, `source-refused` or `copy-failed`.
    Verification: string
    /// Facts the source holds.
    SourceFacts: int
    /// Rows the target holds afterwards.
    TargetFacts: int
    /// Current heads in the source.
    Heads: int
    /// Distinct lineages in the source.
    Lineages: int
    /// Rows this run wrote.
    Inserted: int
    /// Facts an earlier run had already committed.
    ResumedFrom: int
    /// How many differences the verification found.
    Differences: int
    /// The first differences, or the copy error, as text.
    Detail: string
}

/// The statements the migration adds to the store's own (Phase 941).
/// Every one that reads or writes a scope's rows binds `@scope`.
module MigrationSql =

    /// The progress table beside the fact table.
    let progressTable (table: string) = table + "_migration"

    /// The staging table a page is copied into before it is inserted.
    [<Literal>]
    let StageTable = "toolup_fact_migration_stage"

    let private columns =
        "scope, fact_id, hierarchy, path, metric, period_from_ticks, period_to_ticks, method_identity, lineage_hash, as_of_ticks, as_of, supersedes, is_head, magnitude, payload"

    /// The progress table's idempotent DDL.
    let progressDdl (table: string) =
        $"""CREATE TABLE IF NOT EXISTS {progressTable table} (
    scope text PRIMARY KEY,
    source text NOT NULL,
    source_digest text NOT NULL,
    source_facts integer NOT NULL,
    copied integer NOT NULL,
    state text NOT NULL,
    detail text NULL,
    updated_at timestamptz NOT NULL
)"""

    /// One scope's progress row.
    let readProgress (table: string) =
        $"SELECT source_digest, copied, state FROM {progressTable table} WHERE scope = @scope"

    /// Record one scope's progress.
    let saveProgress (table: string) =
        $"""INSERT INTO {progressTable table} (scope, source, source_digest, source_facts, copied, state, detail, updated_at)
VALUES (@scope, @source, @digest, @facts, @copied, @state, @detail, now())
ON CONFLICT (scope) DO UPDATE SET source = EXCLUDED.source, source_digest = EXCLUDED.source_digest,
    source_facts = EXCLUDED.source_facts, copied = EXCLUDED.copied, state = EXCLUDED.state,
    detail = EXCLUDED.detail, updated_at = EXCLUDED.updated_at"""

    /// A page's staging table, dropped when its transaction ends.
    let stage (table: string) =
        $"CREATE TEMP TABLE {StageTable} (LIKE {table} INCLUDING DEFAULTS) ON COMMIT DROP"

    /// Insert the staged page; a row already stored (by content address)
    /// is left as it is and counts nothing.
    let insertStaged (table: string) =
        $"INSERT INTO {table} ({columns}) SELECT {columns} FROM {StageTable} ON CONFLICT (scope, fact_id) DO NOTHING"

    /// Every row of one scope, as the verification compares them.
    let targetRows (table: string) =
        $"SELECT fact_id, lineage_hash, as_of_ticks, supersedes, is_head, payload FROM {table} WHERE scope = @scope"

    /// The statements that name a scope's rows, for the scope-binding check.
    let scopeBoundStatements (table: string) : string list = [
        readProgress table
        saveProgress table
        targetRows table
    ]

/// One target row, as the verification reads it.
type internal TargetRow = {
    FactId: string
    LineageHash: string
    AsOfTicks: int64
    Supersedes: string option
    IsHead: bool
    Payload: string
}

/// Migrating a `BlobFactStore` into a `PostgresFactStore`, and verifying
/// the copy (Phase 941). A compose-time / operator entry point: the
/// `toolup` CLI is dependency-free by rule and cannot carry Npgsql.
module FactStoreMigration =

    /// The audit event type — one per scope per run, under the reserved
    /// `_facts` source module.
    [<Literal>]
    let MigratedType = "FactStoreMigrated"

    let private jsonOptions = FableConverters.create ()

    let private ordinal = StringComparer.Ordinal

    /// The fact-base laws a copy must be able to keep, checked over one
    /// scope's facts: content addresses are unique; a `Supersedes` link
    /// names a fact of the same lineage whose transaction time is strictly
    /// earlier; no fact is superseded twice; and each lineage has exactly
    /// one current head. Together these make every lineage one linear
    /// chain. Empty when the facts are a valid fact base.
    let validateSource (facts: Fact list) : MigrationDifference list =
        let byId = Dictionary<string, Fact>(ordinal)
        let differences = ResizeArray<MigrationDifference>()

        for f in facts do
            if byId.ContainsKey f.FactId then
                differences.Add(InvalidSource(f.FactId, "the content address occurs more than once"))
            else
                byId[f.FactId] <- f

        let successors = Dictionary<string, string>(ordinal)

        for f in facts do
            match f.Supersedes with
            | None -> ()
            | Some predecessor ->
                match byId.TryGetValue predecessor with
                | false, _ ->
                    differences.Add(
                        InvalidSource(f.FactId, sprintf "supersedes %s, which the scope does not hold" predecessor)
                    )
                | true, p ->
                    if FactRow.lineageHash p <> FactRow.lineageHash f then
                        differences.Add(
                            InvalidSource(f.FactId, sprintf "supersedes %s, a fact of another lineage" predecessor)
                        )

                    if f.AsOf <= p.AsOf then
                        differences.Add(
                            InvalidSource(
                                f.FactId,
                                sprintf
                                    "its transaction time does not exceed that of %s, which it supersedes"
                                    predecessor
                            )
                        )

                    match successors.TryGetValue predecessor with
                    | true, other ->
                        differences.Add(
                            InvalidSource(f.FactId, sprintf "supersedes %s, which %s also supersedes" predecessor other)
                        )
                    | _ -> successors[predecessor] <- f.FactId

        for lineage in facts |> List.groupBy FactRow.lineageHash do
            let heads =
                snd lineage
                |> List.filter (fun f -> not (successors.ContainsKey f.FactId))
                |> List.map _.FactId
                |> List.distinct

            if List.length heads > 1 then
                for h in heads do
                    differences.Add(
                        InvalidSource(h, sprintf "one of %d current heads of lineage %s" heads.Length (fst lineage))
                    )

        List.ofSeq differences

    // A digest of the scope's content addresses: facts are append-only, so
    // the same set of addresses is the same set of facts.
    let private digestOf (facts: Fact list) =
        facts
        |> List.map _.FactId
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> String.concat "\n"
        |> FactRow.sha256Hex

    let private heads (facts: Fact list) =
        let superseded = HashSet<string>(facts |> List.choose _.Supersedes, ordinal)

        HashSet<string>(facts |> List.map _.FactId |> List.filter (superseded.Contains >> not), ordinal)

    // A chain, earliest first, walked back from `head` through `previous`.
    // Bounded by `limit` so a cyclic target cannot loop.
    let private walk (head: string) (previous: string -> string option) (limit: int) =
        let rec go (id: string) (acc: string list) (n: int) =
            if n > limit then
                List.rev acc
            else
                match previous id with
                | Some p -> go p (id :: acc) (n + 1)
                | None -> id :: acc

        go head [] 0

    let private command (target: PostgresFactStore) (sql: string) (conn: NpgsqlConnection) (tx: NpgsqlTransaction) =
        let cmd = new NpgsqlCommand(sql, conn, tx)

        if target.Options.CommandTimeoutSeconds > 0 then
            cmd.CommandTimeout <- target.Options.CommandTimeoutSeconds

        cmd

    let private add (cmd: NpgsqlCommand) (name: string) (kind: NpgsqlDbType) (value: obj) =
        cmd.Parameters.Add(NpgsqlParameter(name, kind, Value = value)) |> ignore

    let private readTargetRows (target: PostgresFactStore) (scope: string) : Async<TargetRow list> = async {
        use! conn = target.DataSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask
        use cmd = command target (MigrationSql.targetRows target.Options.Table) conn null
        add cmd "scope" NpgsqlDbType.Text scope
        use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
        let rows = ResizeArray<TargetRow>()

        let rec loop () = async {
            let! more = reader.ReadAsync() |> Async.AwaitTask

            if more then
                rows.Add {
                    FactId = reader.GetString 0
                    LineageHash = reader.GetString 1
                    AsOfTicks = reader.GetInt64 2
                    Supersedes = if reader.IsDBNull 3 then None else Some(reader.GetString 3)
                    IsHead = reader.GetBoolean 4
                    Payload = reader.GetString 5
                }

                return! loop ()
        }

        do! loop ()
        return List.ofSeq rows
    }

    // The instants the AsOf reads are compared at: `samples` of the
    // source's own transaction times, evenly spread, each read at the
    // instant and one tick before it, plus the end of time.
    let private sampleInstants (facts: Fact list) (samples: int) : DateTime list =
        let times = facts |> List.map _.AsOf |> List.distinct |> List.sort |> Array.ofList

        let picked =
            if samples = 0 || times.Length = 0 then
                []
            elif times.Length <= samples then
                List.ofArray times
            elif samples = 1 then
                [ times[times.Length - 1] ]
            else
                [ for i in 0 .. samples - 1 -> times[i * (times.Length - 1) / (samples - 1)] ]

        [
            for t in picked do
                if t > DateTime.MinValue then
                    t.AddTicks -1L

                t
            DateTime.MaxValue
        ]
        |> List.distinct

    let private readIds (store: IFactStore) (scope: string) (t: DateTime) (history: bool) = async {
        let! facts =
            store.Query(
                scope,
                {
                    FactQuery.all with
                        AsOf = Some t
                        IncludeSuperseded = history
                }
            )

        return HashSet<string>(facts |> List.map _.FactId, ordinal)
    }

    // The differential check: rows, columns, head flags, chains, AsOf reads.
    let private differential
        (source: BlobFactStore)
        (target: PostgresFactStore)
        (options: FactStoreMigrationOptions)
        (scope: string)
        (facts: Fact list)
        : Async<MigrationDifference list * int> =
        async {
            let! rows = readTargetRows target scope
            let differences = ResizeArray<MigrationDifference>()
            let sourceById = Dictionary<string, Fact>(ordinal)

            for f in facts do
                sourceById[f.FactId] <- f

            let targetById = Dictionary<string, TargetRow>(ordinal)

            for r in rows do
                targetById[r.FactId] <- r

            let sourceHeads = heads facts

            for f in facts do
                match targetById.TryGetValue f.FactId with
                | false, _ -> differences.Add(MissingOnTarget f.FactId)
                | true, r ->
                    let column (name: string) (expected: string) (actual: string) =
                        if expected <> actual then
                            differences.Add(ColumnDiffers(f.FactId, name, expected, actual))

                    column "payload" (FactRow.serialise f) r.Payload
                    column "as_of_ticks" (string f.AsOf.Ticks) (string r.AsOfTicks)
                    column "supersedes" (defaultArg f.Supersedes "NULL") (defaultArg r.Supersedes "NULL")
                    column "lineage_hash" (FactRow.lineageHash f) r.LineageHash

                    let isHead = sourceHeads.Contains f.FactId

                    if isHead <> r.IsHead then
                        differences.Add(HeadDiffers(f.FactId, isHead, r.IsHead))

            for r in rows do
                if not (sourceById.ContainsKey r.FactId) then
                    differences.Add(ExtraOnTarget r.FactId)

            // Every chain, as each side walks it.
            let sourceChains =
                facts
                |> List.groupBy FactRow.lineageHash
                |> List.map (fun (lineage, members) ->
                    let chain =
                        match members |> List.filter (fun f -> sourceHeads.Contains f.FactId) with
                        | [ head ] ->
                            walk
                                head.FactId
                                (fun id ->
                                    match sourceById.TryGetValue id with
                                    | true, f -> f.Supersedes
                                    | _ -> None)
                                members.Length
                        | _ -> []

                    lineage, chain)
                |> Map.ofList

            let targetChains =
                rows
                |> List.groupBy _.LineageHash
                |> List.map (fun (lineage, members) ->
                    let chain =
                        match members |> List.filter _.IsHead with
                        | [ head ] ->
                            walk
                                head.FactId
                                (fun id ->
                                    match targetById.TryGetValue id with
                                    | true, r -> r.Supersedes
                                    | _ -> None)
                                members.Length
                        | _ -> []

                    lineage, chain)
                |> Map.ofList

            for lineage in Seq.append (Map.keys sourceChains) (Map.keys targetChains) |> Seq.distinct do
                let s = sourceChains |> Map.tryFind lineage |> Option.defaultValue []
                let t = targetChains |> Map.tryFind lineage |> Option.defaultValue []

                if s <> t then
                    differences.Add(ChainDiffers(lineage, s, t))

            // The AsOf reads, through each store's own read path.
            for t in sampleInstants facts options.AsOfSamples do
                for history in [ false; true ] do
                    let! onSource = readIds (source :> IFactStore) scope t history
                    let! onTarget = readIds (target :> IFactStore) scope t history

                    for id in onSource |> Seq.filter (onTarget.Contains >> not) |> Seq.sort do
                        differences.Add(AsOfReadDiffers(t, id, true))

                    for id in onTarget |> Seq.filter (onSource.Contains >> not) |> Seq.sort do
                        differences.Add(AsOfReadDiffers(t, id, false))

            return (differences |> Seq.distinct |> List.ofSeq), rows.Length
        }

    let private exec (cmd: NpgsqlCommand) = async {
        let! n = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
        return n
    }

    let private ensureProgressTable (target: PostgresFactStore) = async {
        use! conn = target.DataSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask
        use cmd = command target (MigrationSql.progressDdl target.Options.Table) conn null
        let! _ = exec cmd
        ()
    }

    let private readProgress (target: PostgresFactStore) (scope: string) = async {
        use! conn = target.DataSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask
        use cmd = command target (MigrationSql.readProgress target.Options.Table) conn null
        add cmd "scope" NpgsqlDbType.Text scope
        use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
        let! found = reader.ReadAsync() |> Async.AwaitTask

        if found then
            return Some(reader.GetString 0, reader.GetInt32 1, reader.GetString 2)
        else
            return None
    }

    let private bindProgress
        (cmd: NpgsqlCommand)
        (options: FactStoreMigrationOptions)
        (scope: string)
        (digest: string)
        (facts: int)
        (copied: int)
        (state: string)
        (detail: string)
        =
        add cmd "scope" NpgsqlDbType.Text scope
        add cmd "source" NpgsqlDbType.Text options.SourceName
        add cmd "digest" NpgsqlDbType.Text digest
        add cmd "facts" NpgsqlDbType.Integer facts
        add cmd "copied" NpgsqlDbType.Integer copied
        add cmd "state" NpgsqlDbType.Text state
        add cmd "detail" NpgsqlDbType.Text detail

    let private saveProgress
        (target: PostgresFactStore)
        (options: FactStoreMigrationOptions)
        (scope: string)
        (digest: string)
        (facts: int)
        (copied: int)
        (state: string)
        (detail: string)
        =
        async {
            use! conn = target.DataSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask
            use cmd = command target (MigrationSql.saveProgress target.Options.Table) conn null
            bindProgress cmd options scope digest facts copied state detail
            let! _ = exec cmd
            ()
        }

    // One page, one transaction: the scope's write lock, the staged COPY,
    // the conflict-free insert and the progress row commit together.
    let private writePage
        (target: PostgresFactStore)
        (options: FactStoreMigrationOptions)
        (scope: string)
        (digest: string)
        (total: int)
        (copiedAfter: int)
        (page: (Fact * bool) list)
        : Async<int> =
        async {
            let table = target.Options.Table
            use! conn = target.DataSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask
            use! tx = conn.BeginTransactionAsync().AsTask() |> Async.AwaitTask

            do! async {
                use lock = command target Sql.lockScopeExclusive conn tx
                add lock "scope" NpgsqlDbType.Text scope
                let! _ = exec lock
                use stage = command target (MigrationSql.stage table) conn tx
                let! _ = exec stage
                ()
            }

            do! async {
                use importer = conn.BeginBinaryImport(Sql.copyIn MigrationSql.StageTable)

                for f, isHead in page do
                    FactRow.write importer scope f isHead

                importer.Complete() |> ignore
            }

            let! inserted = async {
                use insert = command target (MigrationSql.insertStaged table) conn tx
                return! exec insert
            }

            do! async {
                use progress = command target (MigrationSql.saveProgress table) conn tx
                bindProgress progress options scope digest total copiedAfter "copying" ""
                let! _ = exec progress
                ()
            }

            do! tx.CommitAsync() |> Async.AwaitTask
            return inserted
        }

    let private audit
        (events: IEventStore)
        (target: PostgresFactStore)
        (options: FactStoreMigrationOptions)
        (report: ScopeMigrationReport)
        (detail: string)
        =
        let verification =
            match report.Outcome with
            | Verified
            | AlreadyVerified -> "verified"
            | VerificationFailed -> "verification-failed"
            | SourceRefused -> "source-refused"
            | CopyFailed _ -> "copy-failed"

        let payload: FactStoreMigratedEvent = {
            Source = options.SourceName
            Target = "postgres:" + target.Options.Table
            Verification = verification
            SourceFacts = report.SourceFacts
            TargetFacts = report.TargetFacts
            Heads = report.Heads
            Lineages = report.Lineages
            Inserted = report.Inserted
            ResumedFrom = report.ResumedFrom
            Differences = report.Differences.Length
            Detail = detail
        }

        events.Write {
            Id = Guid.NewGuid()
            OccurredAt = DateTime.UtcNow
            ScopeId = report.Scope
            SourceModule = FactEvents.SourceModule
            EventType = MigratedType
            Payload = JsonSerializer.Serialize(payload, jsonOptions)
        }

    let private summarise (differences: MigrationDifference list) =
        let shown = differences |> List.truncate 10 |> List.map MigrationDifference.describe

        let more = differences.Length - shown.Length

        String.concat "; " shown
        + (if more > 0 then sprintf " (and %d more)" more else "")

    let private failOnInvalid (options: FactStoreMigrationOptions) =
        match FactStoreMigrationOptions.validate options with
        | [] -> ()
        | problems -> invalidArg "options" ("[FactStoreMigration] " + String.concat " " problems)

    let private baseReport (scope: string) (facts: Fact list) : ScopeMigrationReport = {
        Scope = scope
        Outcome = Verified
        SourceFacts = facts.Length
        TargetFacts = 0
        Heads = (heads facts).Count
        Lineages = facts |> List.map FactRow.lineageHash |> List.distinct |> List.length
        Inserted = 0
        ResumedFrom = 0
        Differences = []
    }

    let private refusals (options: FactStoreMigrationOptions) (export: FactScopeExport) =
        let unreadable =
            if options.AllowUnreadableSource then
                []
            else
                export.Unreadable |> List.map UnreadableSource

        unreadable @ validateSource export.Facts

    let private migrateScope
        (source: BlobFactStore)
        (target: PostgresFactStore)
        (events: IEventStore)
        (options: FactStoreMigrationOptions)
        (scope: string)
        : Async<ScopeMigrationReport> =
        async {
            let! export = source.ExportScope scope
            let facts = export.Facts
            let digest = digestOf facts
            let report = baseReport scope facts
            let! progress = readProgress target scope

            match progress, refusals options export with
            | Some(recorded, copied, "verified"), [] when recorded = digest ->
                return {
                    report with
                        Outcome = AlreadyVerified
                        TargetFacts = facts.Length
                        ResumedFrom = copied
                }
            | _, (_ :: _ as refused) ->
                let refusedReport = {
                    report with
                        Outcome = SourceRefused
                        Differences = refused
                }

                let detail = summarise refused
                do! saveProgress target options scope digest facts.Length 0 "source-refused" detail
                do! audit events target options refusedReport detail
                return refusedReport
            | _, [] ->
                let resumeFrom =
                    match progress with
                    | Some(recorded, copied, _) when recorded = digest -> min copied facts.Length
                    | _ -> 0

                let sourceHeads = heads facts

                let pages =
                    facts
                    |> List.sortWith (fun a b ->
                        match compare a.AsOf.Ticks b.AsOf.Ticks with
                        | 0 -> String.CompareOrdinal(a.FactId, b.FactId)
                        | c -> c)
                    |> List.skip resumeFrom
                    |> List.map (fun f -> f, sourceHeads.Contains f.FactId)
                    |> List.chunkBySize options.PageSize

                let copied = ref resumeFrom
                let inserted = ref 0
                let failure = ref None

                for page in pages do
                    if failure.Value.IsNone then
                        let! outcome =
                            writePage target options scope digest facts.Length (copied.Value + page.Length) page
                            |> Async.Catch

                        match outcome with
                        | Choice1Of2 n ->
                            copied.Value <- copied.Value + page.Length
                            inserted.Value <- inserted.Value + n
                        | Choice2Of2 ex ->
                            let message =
                                match ex with
                                | :? AggregateException as a when a.InnerExceptions.Count = 1 ->
                                    a.InnerExceptions[0].Message
                                | _ -> ex.Message

                            failure.Value <- Some message

                let written = {
                    report with
                        Inserted = inserted.Value
                        ResumedFrom = resumeFrom
                }

                match failure.Value with
                | Some message ->
                    let! rowsNow = readTargetRows target scope

                    let failed = {
                        written with
                            Outcome = CopyFailed message
                            TargetFacts = rowsNow.Length
                    }

                    let detail =
                        sprintf "page failed after %d of %d facts: %s" copied.Value facts.Length message

                    do! audit events target options failed detail
                    return failed
                | None ->
                    let! differences, targetFacts = differential source target options scope facts

                    let verified = {
                        written with
                            Outcome =
                                if List.isEmpty differences then
                                    Verified
                                else
                                    VerificationFailed
                            TargetFacts = targetFacts
                            Differences = differences
                    }

                    let state, detail =
                        if List.isEmpty differences then
                            "verified", ""
                        else
                            "verification-failed", summarise differences

                    do! saveProgress target options scope digest facts.Length copied.Value state detail
                    do! audit events target options verified detail
                    return verified
        }

    /// Migrate each scope from `source` into `target`, then verify it:
    /// rows written raw with their original content address, transaction
    /// time, supersession link and head flag; a page per transaction,
    /// resumable after an interruption; one `FactStoreMigrated` audit
    /// record per scope into `events` (no per-fact event). A scope an
    /// earlier run verified over the same source facts is skipped.
    ///
    /// Build `source` and `target` with the same metric registry (or both
    /// without): the verification compares their AsOf reads, and a
    /// registry decides the canonical-method selection those reads apply.
    /// Writers must be stopped for the duration. Scopes run one after
    /// another; one scope's failure does not stop the next. Raises
    /// `ArgumentException` for invalid options.
    let migrate
        (source: BlobFactStore)
        (target: PostgresFactStore)
        (events: IEventStore)
        (options: FactStoreMigrationOptions)
        (scopes: string list)
        : Async<FactStoreMigrationReport> =
        async {
            failOnInvalid options
            do! ensureProgressTable target
            let results = ResizeArray<ScopeMigrationReport>()

            for scope in scopes do
                let! result = migrateScope source target events options scope
                results.Add result

            return { Scopes = List.ofSeq results }
        }

    /// Verify each scope of `target` against `source` without writing
    /// anything — the same differential check `migrate` runs after its
    /// copy. Useful before switching the composition, and again after.
    let verify
        (source: BlobFactStore)
        (target: PostgresFactStore)
        (options: FactStoreMigrationOptions)
        (scopes: string list)
        : Async<FactStoreMigrationReport> =
        async {
            failOnInvalid options
            let results = ResizeArray<ScopeMigrationReport>()

            for scope in scopes do
                let! export = source.ExportScope scope
                let report = baseReport scope export.Facts

                match refusals options export with
                | _ :: _ as refused ->
                    results.Add {
                        report with
                            Outcome = SourceRefused
                            Differences = refused
                    }
                | [] ->
                    let! differences, targetFacts = differential source target options scope export.Facts

                    results.Add {
                        report with
                            Outcome =
                                if List.isEmpty differences then
                                    Verified
                                else
                                    VerificationFailed
                            TargetFacts = targetFacts
                            Differences = differences
                    }

            return { Scopes = List.ofSeq results }
        }

    /// A process exit code for an operator host: `0` when every scope
    /// verified, `1` otherwise.
    let exitCode (report: FactStoreMigrationReport) : int = if report.Passed then 0 else 1

    /// The report as text: one line per scope, then one line per
    /// difference, each naming its fact.
    let render (report: FactStoreMigrationReport) : string =
        let sb = StringBuilder()

        for s in report.Scopes do
            let outcome =
                match s.Outcome with
                | Verified -> "verified"
                | AlreadyVerified -> "already verified"
                | VerificationFailed -> "VERIFICATION FAILED"
                | SourceRefused -> "SOURCE REFUSED"
                | CopyFailed e -> "COPY FAILED: " + e

            sb.AppendLine(
                sprintf
                    "%s: %s — %d source facts, %d target rows, %d heads, %d lineages, %d inserted, resumed from %d, %d differences"
                    s.Scope
                    outcome
                    s.SourceFacts
                    s.TargetFacts
                    s.Heads
                    s.Lineages
                    s.Inserted
                    s.ResumedFrom
                    s.Differences.Length
            )
            |> ignore

            for d in s.Differences do
                sb.AppendLine("  " + MigrationDifference.describe d) |> ignore

        sb.ToString()