// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.SqliteLogStoreTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts

// ─── Phase 828 — ILogStore conformance + the SQLite default's own laws ──
//
// The `ILogStoreContract` pack is bound TWICE here: to the shipped SQLite
// store over a temp database file, and to an in-memory fake built on
// `LogSearchQuery.apply`. Both bindings are always on — neither needs a
// server, a container or an env var — and that is the point: the contract
// is the portable rule, so a case that only the SQL backend satisfies (or
// only the fold satisfies) is caught here rather than by the first
// companion author.
//
// Beside the pack sit the laws that are NOT contract — the row-cap
// retention capability, durability across a reopen of the same file, and
// the inert `NoOpLogStore`'s deliberate non-conformance.

// ── the in-memory fake ──────────────────────────────────────────────────

/// An `ILogStore` that keeps its entries in a list and evaluates a search
/// with the canonical `LogSearchQuery.apply`. Deliberately the whole
/// implementation: if the shared fold is enough to satisfy the contract,
/// the contract really is backend-independent.
type private InMemoryLogStore() =
    let gate = obj ()
    let entries = ResizeArray<LogRecord>()

    interface ILogStore with
        member _.Append(entry: LogRecord) = async {
            lock gate (fun () ->
                entries.Add {
                    entry with
                        TimestampUtc = LogRecord.truncateToSecond entry.TimestampUtc
                })
        }

        member _.Search(query: LogSearchQuery) = async {
            return lock gate (fun () -> LogSearchQuery.apply query (List.ofSeq entries))
        }

        member _.Prune(olderThanUtc: DateTime) = async {
            return lock gate (fun () -> entries.RemoveAll(fun e -> e.TimestampUtc < olderThanUtc))
        }

// ── temp-file plumbing for the SQLite binding ───────────────────────────

let private root =
    Path.Combine(Path.GetTempPath(), "toolup-logstore-828", Guid.NewGuid().ToString "N")

/// Every store the SQLite binding opened, so the last case in the list can
/// close the connections and delete the directory. Expecto runs this pack
/// sequenced, so "the last case" is the last thing to run.
let private opened = ResizeArray<ILogStore>()

let private openStoreAt (path: string) : ILogStore =
    let store = SqliteLogStore.create (LogStoreConfig.create path)
    lock opened (fun () -> opened.Add store)
    store

let private sqliteFactory () : ILogStore =
    openStoreAt (Path.Combine(root, Guid.NewGuid().ToString "N", "logs.db"))

let private closeAll () =
    lock opened (fun () ->
        for store in opened do
            match box store with
            | :? IDisposable as d -> d.Dispose()
            | _ -> ()

        opened.Clear())

// ── the laws that are not contract ──────────────────────────────────────

let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

let private line (seconds: float) (message: string) : LogRecord = {
    TimestampUtc = t0.AddSeconds seconds
    Level = LogLevel.Info
    Logger = "toolup"
    Message = message
    ScopeId = None
    CorrelationId = None
    Error = None
}

let private wideOpen = LogSearchQuery.window t0 (t0.AddHours 1.0)

[<Tests>]
let tests =
    testList "Phase 828 — ILogStore" [
        ILogStoreContract.tests "SqliteLogStore" sqliteFactory
        ILogStoreContract.tests "InMemoryLogStore (fake)" (fun () -> InMemoryLogStore() :> ILogStore)

        testCaseAsync "SqliteLogStore — the row cap trims the OLDEST entries and is idempotent"
        <| async {
            let store = sqliteFactory ()

            for i in 1..5 do
                do! store.Append(line (float i * 10.0) $"line-{i}")

            let retention = store :?> ILogStoreRetention

            let! trimmed = retention.TrimToMaxRows 2L
            Expect.equal trimmed 3 "three surplus rows removed"

            let! remaining = store.Search wideOpen
            Expect.equal (remaining |> List.map _.Message) [ "line-5"; "line-4" ] "the two NEWEST survive"

            let! again = retention.TrimToMaxRows 2L
            Expect.equal again 0 "a store already under the cap deletes nothing"

            let! disabled = retention.TrimToMaxRows 0L
            Expect.equal disabled 0 "a non-positive cap disables the leg rather than deleting everything"
        }

        testCaseAsync "SqliteLogStore — entries survive closing and reopening the same file"
        <| async {
            let path = Path.Combine(root, Guid.NewGuid().ToString "N", "durable.db")
            let first = openStoreAt path
            do! first.Append(line 10.0 "written before the restart")
            (first :?> IDisposable).Dispose()

            let second = openStoreAt path
            let! found = second.Search wideOpen

            Expect.equal
                (found |> List.map _.Message)
                [ "written before the restart" ]
                "the store is a file, and a restart is not a reset"
        }

        testCaseAsync "SqliteLogStore — the FTS index tracks deletions, so a pruned line stops matching"
        <| async {
            let store = sqliteFactory ()
            do! store.Append(line 10.0 "capacitor discharged")
            do! store.Append(line 20.0 "capacitor recharged")

            let! before =
                store.Search {
                    wideOpen with
                        TextMatch = Some "capacitor"
                }

            Expect.equal (List.length before) 2 "both lines indexed"

            let! deleted = store.Prune(t0.AddSeconds 20.0)
            Expect.equal deleted 1 "one line pruned"

            let! after =
                store.Search {
                    wideOpen with
                        TextMatch = Some "capacitor"
                }

            Expect.equal
                (after |> List.map _.Message)
                [ "capacitor recharged" ]
                "the external-content index followed the delete — a stale rowid would resurrect the pruned line"
        }

        testCaseAsync "NoOpLogStore stores nothing, and is therefore deliberately not contract-conformant"
        <| async {
            let store = NoOpLogStore.create ()
            do! store.Append(line 10.0 "discarded")
            let! found = store.Search wideOpen
            Expect.isEmpty found "the null object keeps nothing — which is why the contract pack is not bound to it"

            let! pruned = store.Prune(t0.AddHours 1.0)
            Expect.equal pruned 0 "and prunes nothing"
        }

        // Last, deliberately: Expecto runs this pack sequenced, so by the
        // time this case runs every SQLite binding above has finished with
        // its file. Closing the connections here is what lets the
        // directory delete on Windows, where an open handle blocks it.
        testCase "the temp databases are closed and removed"
        <| fun () ->
            closeAll ()

            if Directory.Exists root then
                Directory.Delete(root, true)

            Expect.isFalse (Directory.Exists root) "no temp database directory is left behind"
    ]