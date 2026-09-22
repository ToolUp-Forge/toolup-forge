// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 828 — ILogStore ──────────────────────────────────────────────
//
// A queryable, in-platform store for the structured log lines the SDK's
// own logging already produces, so a deployment that cannot or will not
// send telemetry off-platform (air-gapped pilots, data-residency mandates)
// can still search its own logs. Today those lines go to stdout/stderr
// only — `ConsoleLogger` / `JsonConsoleLogger` — and searching them means
// an aggregator outside the deployment boundary.
//
// **Default impl** — `SqliteLogStore` (one local database file, an FTS5
// index on the message text, columnar filters, bounded by retention). A
// deployment that wants a distributed backend composes a companion against
// the same contract; `ILogStoreContract` validates any implementation.
//
// **Opt-in (GP 13).** `ServerConfig.LogStore` defaults to `NoLogStore`:
// nothing is registered, no logger is decorated, no file is opened, and
// the boot path is byte-for-byte what it was (GP 11).
//
// **Six portability rules (GP 12).**
// 1. *Identity by value* — an entry is a record of primitives; the scope
//    and correlation ids are strings. No live handles cross the boundary.
// 2. *Async at every boundary* — every method returns `Async<_>`.
// 3. *Retry / behaviour as data* — the search directive is a
//    `LogSearchQuery` record, never a caller-supplied predicate, so any
//    backend can honour it (a SQL `WHERE` + `MATCH`, an in-memory fold,
//    …). Retention is likewise a record (`LogStoreConfig`), not a hook.
// 4. *Stateless handlers between invocations* — each call carries its
//    whole query; the store caches nothing across calls.
// 5. *No cross-shard ordering* — ordering is promised only within one
//    store: newest-first by `TimestampUtc`, ties broken by reverse
//    append order. Nothing is promised across stores or processes.
// 6. *Precision at the lower bound* — timestamps are **per-second**.
//    `Append` truncates `TimestampUtc` to the second, and what `Search`
//    returns is the truncated value, so every backend agrees regardless of
//    the column type it stores. Sub-second ordering is therefore never
//    observable; within one second, reverse append order decides.

/// One stored log line. Fable-safe (records, primitives, `option`, a
/// Fable-safe DU) so the admin surface renders the same type the server
/// stores — no parallel DTO, no mapping layer (GP 10).
///
/// `TimestampUtc` is UTC and **second-granular** — see rule 6 above. The
/// originating exception rides as its already-rendered string rather than
/// an `exn`, because an exception is neither Fable-safe nor portable
/// across a storage boundary.
type LogRecord = {
    /// When the line was emitted, UTC, truncated to the second.
    TimestampUtc: DateTime
    /// Severity, as the SDK's own `LogLevel`.
    Level: LogLevel
    /// The emitting logger's name (the `logger` field
    /// `JsonConsoleLogger` writes — `"toolup"` by default).
    Logger: string
    /// The rendered message text. The only full-text-searchable field.
    Message: string
    /// The ambient `LoggerScope.ScopeId` at emission, when one was
    /// pushed — the sub-scope within a request (a job dispatch, a
    /// webhook delivery). `None` when nothing was in scope.
    ScopeId: string option
    /// The ambient `LoggerScope.RequestId` at emission, when one was
    /// pushed — the id tying every line of one unit of work together.
    /// `None` when nothing was in scope.
    CorrelationId: string option
    /// The originating exception, already rendered to a string, on the
    /// lines that carried one. `None` otherwise.
    Error: string option
}

[<RequireQualifiedAccess>]
module LogRecord =
    /// Truncate an instant to the second and normalise it to UTC — the
    /// single definition of rule 6's lower bound, applied by `Append` in
    /// every implementation so a round-trip is exact.
    let truncateToSecond (t: DateTime) : DateTime =
        let utc =
            match t.Kind with
            | DateTimeKind.Utc -> t
            | DateTimeKind.Local -> t.ToUniversalTime()
            | _ -> DateTime.SpecifyKind(t, DateTimeKind.Utc)

        DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, DateTimeKind.Utc)

    /// A message-only entry at `level`, stamped now, with no scope,
    /// correlation or error. The shape a caller wants when it is
    /// appending by hand rather than through the logger decorator.
    let create (level: LogLevel) (logger: string) (message: string) : LogRecord = {
        TimestampUtc = truncateToSecond DateTime.UtcNow
        Level = level
        Logger = logger
        Message = message
        ScopeId = None
        CorrelationId = None
        Error = None
    }

/// A search over stored log lines, expressed as **data** (GP 12 rule 3)
/// so any backend can honour it — a SQL `WHERE` with an FTS5 `MATCH`, an
/// in-memory fold, a companion's own index.
///
/// Every filter is a conjunction: an entry is returned only when it
/// satisfies all of them. An absent filter (`None` / an empty `Levels`)
/// constrains nothing.
type LogSearchQuery = {
    /// Full-text needle matched against `Message`, **token-wise and
    /// case-insensitively** — see `LogSearchQuery.textMatches` for the
    /// exact rule, which is what keeps an FTS5 backend and an in-process
    /// one contract-identical. `None` matches every entry.
    TextMatch: string option
    /// The severities to include. **Empty means every level** — it is the
    /// unconstrained case, not the empty result set.
    Levels: LogLevel list
    /// Inclusive lower bound on `TimestampUtc`.
    SinceUtc: DateTime
    /// Exclusive upper bound on `TimestampUtc` — the window is
    /// `[SinceUtc, UntilUtc)`, the same half-open shape `ITimeSeriesStore`
    /// uses, so two adjacent windows partition rather than overlap.
    UntilUtc: DateTime
    /// Exact-match filter on `LogRecord.ScopeId`. An entry whose `ScopeId`
    /// is `None` never satisfies a `Some` filter.
    ScopeId: string option
    /// Exact-match filter on `LogRecord.CorrelationId`. An entry whose
    /// `CorrelationId` is `None` never satisfies a `Some` filter.
    CorrelationId: string option
    /// Maximum number of entries returned, applied **after** ordering, so
    /// a limited search returns the newest matches rather than an
    /// arbitrary subset. Zero or negative returns nothing.
    Limit: int
}

[<RequireQualifiedAccess>]
module LogSearchQuery =
    /// The default page size an unspecified search reads.
    [<Literal>]
    let DefaultLimit = 100

    /// An otherwise-unconstrained query over `[sinceUtc, untilUtc)` —
    /// every level, no text, no scope or correlation filter, at the
    /// default limit. Narrow it with record-update syntax.
    let window (sinceUtc: DateTime) (untilUtc: DateTime) : LogSearchQuery = {
        TextMatch = None
        Levels = []
        SinceUtc = sinceUtc
        UntilUtc = untilUtc
        ScopeId = None
        CorrelationId = None
        Limit = DefaultLimit
    }

    /// Split text into the tokens the text filter compares — maximal runs
    /// of letters and digits, lower-cased. This is deliberately the
    /// tokenisation SQLite's default FTS5 `unicode61` tokeniser performs,
    /// so `textMatches` below and an FTS5 `MATCH` agree on what a word is.
    let tokenise (text: string) : string list =
        if String.IsNullOrEmpty text then
            []
        else
            let acc = ResizeArray<string>()
            let current = Text.StringBuilder()

            for ch in text do
                if Char.IsLetterOrDigit ch then
                    current.Append(Char.ToLowerInvariant ch) |> ignore
                elif current.Length > 0 then
                    acc.Add(current.ToString())
                    current.Clear() |> ignore

            if current.Length > 0 then
                acc.Add(current.ToString())

            List.ofSeq acc

    /// The text-filter rule, stated once so every backend implements the
    /// same thing: `needle` is tokenised, `message` is tokenised, and the
    /// entry matches when **every** needle token appears among the
    /// message's tokens (an AND of whole words, case-insensitive — never
    /// a substring). A needle with no tokens matches everything, which is
    /// what makes `TextMatch = Some ""` behave as `None` rather than as an
    /// impossible filter.
    let textMatches (needle: string) (message: string) : bool =
        match tokenise needle with
        | [] -> true
        | needleTokens ->
            let messageTokens = tokenise message |> Set.ofList
            needleTokens |> List.forall messageTokens.Contains

    /// Whether one entry satisfies every filter of `query` — the
    /// conjunction, minus ordering and the limit.
    let matches (query: LogSearchQuery) (entry: LogRecord) : bool =
        entry.TimestampUtc >= query.SinceUtc
        && entry.TimestampUtc < query.UntilUtc
        && (List.isEmpty query.Levels || List.contains entry.Level query.Levels)
        && (match query.ScopeId with
            | None -> true
            | Some s -> entry.ScopeId = Some s)
        && (match query.CorrelationId with
            | None -> true
            | Some c -> entry.CorrelationId = Some c)
        && (match query.TextMatch with
            | None -> true
            | Some needle -> textMatches needle entry.Message)

    /// Canonical in-process evaluation — filter, order newest-first, take
    /// `Limit`. `entries` must be in **append order**; ties on
    /// `TimestampUtc` (unavoidable at second granularity) are broken by
    /// reverse append order, which is what rule 5 promises. Any backend
    /// folding in process shares this definition; a SQL backend reproduces
    /// it with `ORDER BY timestamp DESC, rowid DESC LIMIT ?`.
    let apply (query: LogSearchQuery) (entries: LogRecord list) : LogRecord list =
        if query.Limit <= 0 then
            []
        else
            entries
            |> List.indexed
            |> List.filter (fun (_, e) -> matches query e)
            |> List.sortWith (fun (i, a) (j, b) ->
                match compare b.TimestampUtc a.TimestampUtc with
                | 0 -> compare j i
                | c -> c)
            |> List.truncate query.Limit
            |> List.map snd

/// A queryable store for the deployment's own structured log lines (see
/// the six-rule note above). Scope- and correlation-filtered rather than
/// scope-*partitioned*: unlike `ITimeSeriesStore`, a log store is an
/// operator surface over one deployment, and the admin surface in front of
/// it is what enforces who may read it.
type ILogStore =
    /// Append one line. `TimestampUtc` is truncated to the second
    /// (`LogRecord.truncateToSecond`) before it is stored, so what `Search`
    /// returns round-trips exactly. Appends are never deduplicated —
    /// appending the same entry twice stores it twice.
    abstract Append: entry: LogRecord -> Async<unit>

    /// The entries satisfying `query`, newest first, at most
    /// `query.Limit` of them. Equivalent to `LogSearchQuery.apply` over
    /// everything stored; an empty result is `[]`, never an error.
    abstract Search: query: LogSearchQuery -> Async<LogRecord list>

    /// Delete every entry stamped strictly before `olderThanUtc` and
    /// return how many were deleted. Idempotent — a second call with the
    /// same bound deletes nothing and returns `0`. This is the primitive
    /// the retention sweep drives; a backend enforcing a row cap prunes by
    /// age here and by count in its own sweep.
    abstract Prune: olderThanUtc: DateTime -> Async<int>

/// Optional capability for a backend that can enforce a **row cap** as
/// well as the age bound `ILogStore.Prune` covers. A separate capability
/// interface (the `ITraceLogger` shape) rather than a third method on
/// `ILogStore`, so a backend with no cheap row count — and every
/// hand-rolled `{ new ILogStore with … }` test double — stays valid
/// without implementing it. The retention sweep type-tests for it and
/// skips the row-cap leg when the store does not offer one.
type ILogStoreRetention =
    /// Delete the oldest entries until at most `maxRows` remain, and
    /// return how many were deleted. Idempotent — a store already under
    /// the cap deletes nothing and returns `0`. "Oldest" is the reverse
    /// of `Search`'s order: by `TimestampUtc` ascending, ties broken by
    /// append order.
    abstract TrimToMaxRows: maxRows: int64 -> Async<int>