// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.ILogStoreContract

open System
open Expecto
open ToolUp.Platform

// ─── Phase 828 — ILogStore conformance pack ─────────────────────────────
//
// Bound to BOTH the SQLite default (over a temp database file) and an
// in-memory fake built on `LogSearchQuery.apply`. Two bindings, not one,
// because the pack's whole job is to pin the portable contract: a rule
// only the SQL backend satisfies, or only the fold satisfies, is a rule
// the next backend will get wrong.
//
// What it asserts: the append/search round-trip and its newest-first
// order, the empty-`Levels` convention, the half-open time window, the
// scope and correlation filters (including that an absent value never
// satisfies a present filter), the whole-word text rule, the limit
// applying after the ordering, prune's strictness and idempotence, and
// rule 6's second-granular round-trip.
//
// A companion validates itself against the same bar by binding this pack
// to its own factory.

let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

/// The whole window every case searches unless it is testing the window.
let private wideOpen = LogSearchQuery.window t0 (t0.AddHours 1.0)

let private at (seconds: float) (message: string) : LogRecord = {
    TimestampUtc = t0.AddSeconds seconds
    Level = LogLevel.Info
    Logger = "toolup"
    Message = message
    ScopeId = None
    CorrelationId = None
    Error = None
}

let private appendAll (store: ILogStore) (entries: LogRecord list) = async {
    for entry in entries do
        do! store.Append entry
}

let private messages (entries: LogRecord list) = entries |> List.map _.Message

let tests (name: string) (factory: unit -> ILogStore) =
    testList $"{name} — ILogStore contract" [
        testCaseAsync "Append + Search round-trips, newest first"
        <| async {
            let store = factory ()
            // Appended out of order; expect newest-first back regardless.
            do! appendAll store [ at 20.0 "beta"; at 30.0 "gamma"; at 10.0 "alpha" ]
            let! found = store.Search wideOpen
            Expect.equal (messages found) [ "gamma"; "beta"; "alpha" ] "descending by timestamp"
        }

        testCaseAsync "An empty store searches to the empty list, never an error"
        <| async {
            let store = factory ()
            let! found = store.Search wideOpen
            Expect.isEmpty found "nothing stored, nothing found"
        }

        testCaseAsync "An empty Levels list means every level; a non-empty one filters"
        <| async {
            let store = factory ()

            do!
                appendAll store [
                    {
                        at 10.0 "info" with
                            Level = LogLevel.Info
                    }
                    {
                        at 20.0 "warn" with
                            Level = LogLevel.Warn
                    }
                    {
                        at 30.0 "error" with
                            Level = LogLevel.Error
                    }
                ]

            let! all = store.Search wideOpen
            Expect.equal (List.length all) 3 "empty Levels is the unconstrained case, not the empty set"

            let! severe =
                store.Search {
                    wideOpen with
                        Levels = [ LogLevel.Warn; LogLevel.Error ]
                }

            Expect.equal (messages severe) [ "error"; "warn" ] "only the named levels, newest first"
        }

        testCaseAsync "The time window is half-open — [SinceUtc, UntilUtc)"
        <| async {
            let store = factory ()
            do! appendAll store [ at 0.0 "at-zero"; at 10.0 "at-ten"; at 20.0 "at-twenty" ]

            let! found =
                store.Search {
                    wideOpen with
                        SinceUtc = t0.AddSeconds 10.0
                        UntilUtc = t0.AddSeconds 20.0
                }

            Expect.equal (messages found) [ "at-ten" ] "lower bound included, upper bound excluded"
        }

        testCaseAsync "ScopeId filters exactly; an entry with no scope never matches a scoped query"
        <| async {
            let store = factory ()

            do!
                appendAll store [
                    {
                        at 10.0 "scoped-a" with
                            ScopeId = Some "job-a"
                    }
                    {
                        at 20.0 "scoped-b" with
                            ScopeId = Some "job-b"
                    }
                    at 30.0 "unscoped"
                ]

            let! found = store.Search { wideOpen with ScopeId = Some "job-a" }
            Expect.equal (messages found) [ "scoped-a" ] "only the named scope — the unscoped line is not a wildcard"
        }

        testCaseAsync "CorrelationId filters exactly, independently of the scope filter"
        <| async {
            let store = factory ()

            do!
                appendAll store [
                    {
                        at 10.0 "first-hop" with
                            CorrelationId = Some "req-1"
                            ScopeId = Some "job-a"
                    }
                    {
                        at 20.0 "second-hop" with
                            CorrelationId = Some "req-1"
                            ScopeId = Some "job-b"
                    }
                    {
                        at 30.0 "other-request" with
                            CorrelationId = Some "req-2"
                    }
                ]

            let! found =
                store.Search {
                    wideOpen with
                        CorrelationId = Some "req-1"
                }

            Expect.equal (messages found) [ "second-hop"; "first-hop" ] "one request's whole trail, across scopes"
        }

        testCaseAsync "TextMatch is whole-word and case-insensitive, never a substring"
        <| async {
            let store = factory ()

            do!
                appendAll store [
                    at 10.0 "Payment captured for order 42"
                    at 20.0 "payments are queued"
                    at 30.0 "nothing to see"
                ]

            let! caseInsensitive =
                store.Search {
                    wideOpen with
                        TextMatch = Some "PAYMENT"
                }

            Expect.equal
                (messages caseInsensitive)
                [ "Payment captured for order 42" ]
                "case-folded, and 'payments' is a different word"

            let! multiToken =
                store.Search {
                    wideOpen with
                        TextMatch = Some "order 42"
                }

            Expect.equal (messages multiToken) [ "Payment captured for order 42" ] "every needle token must be present"

            let! substring =
                store.Search {
                    wideOpen with
                        TextMatch = Some "ment"
                }

            Expect.isEmpty substring "a substring of a word is not a match"

            let! empty = store.Search { wideOpen with TextMatch = Some "  " }
            Expect.equal (List.length empty) 3 "a needle with no tokens constrains nothing"
        }

        testCaseAsync "Limit applies after the ordering, so it returns the NEWEST matches"
        <| async {
            let store = factory ()
            do! appendAll store [ at 10.0 "one"; at 20.0 "two"; at 30.0 "three"; at 40.0 "four" ]

            let! newest = store.Search { wideOpen with Limit = 2 }
            Expect.equal (messages newest) [ "four"; "three" ] "the two newest, not an arbitrary pair"

            let! none = store.Search { wideOpen with Limit = 0 }
            Expect.isEmpty none "a non-positive limit returns nothing"
        }

        testCaseAsync "Prune deletes strictly-older entries, returns the count, and is idempotent"
        <| async {
            let store = factory ()
            do! appendAll store [ at 10.0 "old"; at 20.0 "older-boundary"; at 30.0 "kept" ]

            let! deleted = store.Prune(t0.AddSeconds 30.0)
            Expect.equal deleted 2 "strictly before the bound — the entry ON the bound survives"

            let! again = store.Prune(t0.AddSeconds 30.0)
            Expect.equal again 0 "a second prune at the same bound deletes nothing"

            let! remaining = store.Search wideOpen
            Expect.equal (messages remaining) [ "kept" ] "and the survivor is still searchable"
        }

        testCaseAsync "Timestamps round-trip at second precision (GP 12 rule 6)"
        <| async {
            let store = factory ()

            let subSecond = {
                at 5.0 "truncated" with
                    TimestampUtc = t0.AddSeconds(5.0).AddMilliseconds 750.0
            }

            do! store.Append subSecond
            let! found = store.Search wideOpen
            let stored = List.exactlyOne found

            Expect.equal
                stored.TimestampUtc
                (t0.AddSeconds 5.0)
                "the sub-second part is dropped on append, not on the way out"

            Expect.equal stored.TimestampUtc.Kind DateTimeKind.Utc "and what comes back is marked UTC"
        }

        testCaseAsync "Every field of an entry survives the round-trip"
        <| async {
            let store = factory ()

            let full = {
                TimestampUtc = t0.AddSeconds 10.0
                Level = LogLevel.Error
                Logger = "toolup.jobs"
                Message = "handler threw"
                ScopeId = Some "job-7"
                CorrelationId = Some "req-9"
                Error = Some "System.InvalidOperationException: nope"
            }

            do! store.Append full
            let! found = store.Search wideOpen
            Expect.equal (List.exactlyOne found) full "no field is dropped or defaulted by the storage layer"
        }
    ]