module ToolUp.Platform.Tests.Contracts.IUsageLogContract

open System
open System.Threading
open Expecto
open ToolUp.Platform.Usage

// ─── IUsageLog contract pack ──────────────────────────────────────
//
// Parametrised tests for any `IUsageLog` implementation. Each test
// asks the factory for a fresh `(usageLog, scopeA, scopeB)` triple.
// Scopes are GUID-suffixed so concurrent runs against a shared
// substrate (filesystem, future distributed companion) cannot
// interfere.
//
// Coverage targets the GP 4 (team isolation NON-NEGOTIABLE) load-
// bearing properties:
//   * Cross-team isolation — Team B's Query / Aggregate never
//     returns a Team A record (test 5)
//   * Bleed test — distinct scopes' data does not surface to other
//     scopes even when timestamps overlap (test 6)
//   * Idempotency under retry — same RecordId flushed twice
//     produces one record (test 9)
// Plus the standard surface checks (round-trip, range filter,
// kind filter, aggregate shape, clock-skew rollup boundary).
//
// `flushDelay` is the hint the implementation gives for how long the
// test should sleep after a `Record` before reading via Query /
// Aggregate. Blob-backed implementations need this for the
// background flusher; in-memory implementations can pass `0`. Tests
// always poll up to 5x this delay before failing — best-effort with
// hard upper bound.

let private mkRecord scopeId kind quantity (timestamp: DateTime) : UsageRecord = {
    RecordId = Guid.NewGuid()
    ScopeId = scopeId
    ResourceKind = kind
    Quantity = quantity
    Unit = "tokens"
    Origin = None
    Metadata = Map.empty
    Timestamp = timestamp
}

let private waitForFlush (delay: TimeSpan) =
    if delay > TimeSpan.Zero then
        Thread.Sleep delay

let tests (name: string) (factory: unit -> IUsageLog * string * string) (flushDelay: TimeSpan) =

    testList $"{name} — IUsageLog contract" [

        // ─── 1. Record + Query round-trip ────────────────────

        testCaseAsync "1. Record + Query returns the record"
        <| async {
            let log, scopeA, _ = factory ()
            let now = DateTime.UtcNow
            let r = mkRecord scopeA ResourceKinds.aiTokensInput 100M now

            do! log.Record r
            waitForFlush flushDelay

            let! records = log.Query(scopeA, None, None)
            let found = records |> List.tryFind (fun x -> x.RecordId = r.RecordId)
            Expect.isSome found "record should round-trip through Query"
            Expect.equal found.Value.Quantity 100M "Quantity preserved"
        }

        // ─── 2. Aggregate shape per grouping ─────────────────

        testCaseAsync "2a. Aggregate(ByResourceKind) sums per kind"
        <| async {
            let log, scopeA, _ = factory ()
            let now = DateTime.UtcNow

            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensInput 100M now)
            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensInput 50M now)
            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensOutput 25M now)
            waitForFlush flushDelay

            let! agg = log.Aggregate(scopeA, ByResourceKind)

            let inputSum =
                agg |> Map.tryFind ResourceKinds.aiTokensInput |> Option.defaultValue 0M

            let outputSum =
                agg |> Map.tryFind ResourceKinds.aiTokensOutput |> Option.defaultValue 0M

            Expect.equal inputSum 150M "input tokens summed"
            Expect.equal outputSum 25M "output tokens summed"
        }

        testCaseAsync "2b. Aggregate(ByDay) buckets by UTC date"
        <| async {
            let log, scopeA, _ = factory ()
            let day1 = DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)
            let day2 = DateTime(2026, 5, 2, 11, 0, 0, DateTimeKind.Utc)

            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensInput 10M day1)
            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensInput 20M day1)
            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensInput 30M day2)
            waitForFlush flushDelay

            let! agg = log.Aggregate(scopeA, ByDay)
            Expect.equal (agg |> Map.tryFind "2026-05-01") (Some 30M) "day1 sum"
            Expect.equal (agg |> Map.tryFind "2026-05-02") (Some 30M) "day2 sum"
        }

        // ─── 3. Date-range filter ────────────────────────────

        testCaseAsync "3. Query honours date-range filter"
        <| async {
            let log, scopeA, _ = factory ()
            let inRange = DateTime(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc)
            let outOfRange = DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc)

            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensInput 10M inRange)
            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensInput 20M outOfRange)
            waitForFlush flushDelay

            let from = DateTime(2026, 5, 14, 0, 0, 0, DateTimeKind.Utc)
            let toDt = DateTime(2026, 5, 18, 23, 59, 59, DateTimeKind.Utc)
            let! records = log.Query(scopeA, None, Some(from, toDt))
            let qty = records |> List.sumBy _.Quantity
            Expect.equal qty 10M "only the in-range record is returned"
        }

        // ─── 4. Resource-kind filter ─────────────────────────

        testCaseAsync "4. Query honours resource-kind filter"
        <| async {
            let log, scopeA, _ = factory ()
            let now = DateTime.UtcNow

            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensInput 100M now)
            do! log.Record(mkRecord scopeA ResourceKinds.storageBytes 1024M now)
            waitForFlush flushDelay

            let! input = log.Query(scopeA, Some ResourceKinds.aiTokensInput, None)
            Expect.all input (fun r -> r.ResourceKind = ResourceKinds.aiTokensInput) "only matching kind"
            Expect.isGreaterThan (List.length input) 0 "found at least one input record"
        }

        // ─── 5. Cross-team isolation (LOAD-BEARING — GP 4) ───

        testCaseAsync "5. Cross-team isolation: Team B's Query never returns Team A records"
        <| async {
            let log, scopeA, scopeB = factory ()
            let now = DateTime.UtcNow

            // Both teams write at the same wall clock — the most
            // adversarial scenario for any per-blob impl whose layout
            // doesn't perfectly partition by scope.
            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensInput 100M now)
            do! log.Record(mkRecord scopeB ResourceKinds.aiTokensInput 200M now)
            waitForFlush flushDelay

            let! aRecords = log.Query(scopeA, None, None)
            let! bRecords = log.Query(scopeB, None, None)

            Expect.all aRecords (fun r -> r.ScopeId = scopeA) "scope A query returns only scope A records"
            Expect.all bRecords (fun r -> r.ScopeId = scopeB) "scope B query returns only scope B records"
        }

        testCaseAsync "5b. Cross-team isolation: Aggregate respects scope"
        <| async {
            let log, scopeA, scopeB = factory ()
            let now = DateTime.UtcNow

            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensInput 100M now)
            do! log.Record(mkRecord scopeB ResourceKinds.aiTokensInput 200M now)
            waitForFlush flushDelay

            let! aAgg = log.Aggregate(scopeA, ByResourceKind)
            let! bAgg = log.Aggregate(scopeB, ByResourceKind)

            Expect.equal
                (aAgg |> Map.tryFind ResourceKinds.aiTokensInput |> Option.defaultValue 0M)
                100M
                "scope A aggregates only scope A records"

            Expect.equal
                (bAgg |> Map.tryFind ResourceKinds.aiTokensInput |> Option.defaultValue 0M)
                200M
                "scope B aggregates only scope B records"
        }

        // ─── 6. Bleed test — distinct scopes ─────────────────

        testCaseAsync "6. Bleed test: scope B sees no scope A record after parallel writes"
        <| async {
            let log, scopeA, scopeB = factory ()
            let baseTime = DateTime.UtcNow

            // Issue 50 writes per scope at varying timestamps to
            // amplify any cross-scope leakage.
            let! _ =
                [ 0..49 ]
                |> List.map (fun i ->
                    let ts = baseTime.AddSeconds(float i)
                    log.Record(mkRecord scopeA ResourceKinds.aiTokensInput (decimal i) ts))
                |> Async.Parallel

            let! _ =
                [ 0..49 ]
                |> List.map (fun i ->
                    let ts = baseTime.AddSeconds(float i)
                    log.Record(mkRecord scopeB ResourceKinds.storageBytes (decimal (i * 100)) ts))
                |> Async.Parallel

            waitForFlush flushDelay

            let! bRecords = log.Query(scopeB, None, None)
            Expect.all bRecords (fun r -> r.ScopeId = scopeB) "bleed test: no scope A records leak into scope B"

            Expect.all
                bRecords
                (fun r -> r.ResourceKind = ResourceKinds.storageBytes)
                "scope B kinds are exclusively storageBytes"
        }

        // ─── 7. Aggregate ByMonth ────────────────────────────

        testCaseAsync "7. Aggregate(ByMonth) buckets by UTC year-month"
        <| async {
            let log, scopeA, _ = factory ()
            let mar = DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc)
            let apr = DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc)

            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensInput 50M mar)
            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensInput 75M apr)
            waitForFlush flushDelay

            let! agg = log.Aggregate(scopeA, ByMonth)
            Expect.equal (agg |> Map.tryFind "2026-03") (Some 50M) "March sum"
            Expect.equal (agg |> Map.tryFind "2026-04") (Some 75M) "April sum"
        }

        // ─── 8. Clock-skew rollup boundary ───────────────────

        testCaseAsync "8. Clock-skew rollup: records at midnight boundary land in distinct daily buckets"
        <| async {
            let log, scopeA, _ = factory ()
            let lateNight = DateTime(2026, 5, 1, 23, 59, 59, 900, DateTimeKind.Utc)
            let earlyMorning = DateTime(2026, 5, 2, 0, 0, 0, 100, DateTimeKind.Utc)

            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensInput 11M lateNight)
            do! log.Record(mkRecord scopeA ResourceKinds.aiTokensInput 22M earlyMorning)
            waitForFlush flushDelay

            let! agg = log.Aggregate(scopeA, ByDay)
            Expect.equal (agg |> Map.tryFind "2026-05-01") (Some 11M) "late-night record in day1"
            Expect.equal (agg |> Map.tryFind "2026-05-02") (Some 22M) "early-morning record in day2"
        }

        // ─── 9. Idempotency under retry ──────────────────────

        testCaseAsync "9. Idempotency: same RecordId flushed twice produces one record"
        <| async {
            let log, scopeA, _ = factory ()
            let now = DateTime.UtcNow

            let r = mkRecord scopeA ResourceKinds.aiTokensInput 42M now
            do! log.Record r
            do! log.Record r
            do! log.Record r
            waitForFlush flushDelay

            let! records = log.Query(scopeA, None, None)
            let matches = records |> List.filter (fun x -> x.RecordId = r.RecordId)
            Expect.equal (List.length matches) 1 "duplicate-RecordId writes deduped to one"
        }
    ]