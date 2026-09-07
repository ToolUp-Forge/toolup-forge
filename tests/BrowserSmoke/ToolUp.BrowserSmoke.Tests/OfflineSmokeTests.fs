// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Scenario 2 — an edit made with the link down, queued and applied.
module ToolUp.BrowserSmoke.Tests.OfflineSmokeTests

open Expecto
open Microsoft.Playwright
open ToolUp.Reporting.HtmlPdf.Tests
open ToolUp.BrowserSmoke.Tests.Harness

// ─── Phase 24's acceptance, plus Phase 760's routing ─────────────────
//
// "Drop the network, make a change, restore it" was operator-run only,
// and for a good reason: the queue is IndexedDB and the connectivity
// signal is the browser's own, so no .NET pack can reach either. This
// is that round trip, executed — with the CRDT routing of Phase 760
// underneath it, which is what makes an offline co-editing edit apply
// on reconnect instead of arriving in conflict UI nobody can answer.
//
// The claims:
//
//   1. with the link down, a local edit is HELD — the badge says
//      Offline with a count, and the server has nothing;
//   2. on reconnect the held update APPLIES — the queue empties, the
//      badge returns to Online, and the SERVER holds the update. The
//      server-side half is the one that matters: a queue that empties
//      because it discarded its contents looks identical from the page;
//   3. it applied through the MERGEABLE channel, never through
//      `IOfflineSyncApi` — the fixture counts any stray entity-sync
//      call and the count must be zero.
//
// And the go-red: with the queue's `Enqueue` dropped
// (`?fault=queue`), claim 1's hold never happens and claim 2's apply
// never follows.

let private fill (page: IPage) (text: string) =
    page.FillAsync("#coedit-offline", text) |> Async.AwaitTask

let private strayEntitySyncCalls (page: IPage) : Async<int> = async {
    let! raw =
        page.EvaluateAsync<int>("() => window.__smoke.strayEntitySync")
        |> Async.AwaitTask

    return raw
}

let private serverHas (attempt: Attempt) (doc: string) (session: string) =
    attempt.Host.Updates doc
    |> List.exists (fun update -> update.OriginSession = session)

let private scenarios = [
    scenario "an edit made offline is queued, then applies on reconnect" (fun attempt -> async {
        let doc = "offline-round-trip"
        let session = "held"

        let! page = openPage attempt "solo" (sprintf "mode=offline&doc=%s&session=%s" doc session)

        do! page.Context.SetOfflineAsync true |> Async.AwaitTask

        // Wait for the badge to notice, so the edit below is
        // unambiguously made while the link is down.
        do!
            wait "the status badge to report Offline" (fun () -> async {
                let! status = dataText page "#status" "data-status"
                return status.StartsWith "Offline"
            })

        do! fill page "written while disconnected"

        do!
            wait "the offline queue to hold the update" (fun () -> async {
                let! queued = dataInt page "#queued" "data-count"
                return queued >= 1
            })

        let! badge = dataText page "#status" "data-status"
        Expect.stringStarts badge "Offline" "the badge should report Offline while the link is down"

        Expect.isFalse (serverHas attempt doc session) "the server must hold nothing while the update is only queued"

        do! page.Context.SetOfflineAsync false |> Async.AwaitTask

        do!
            wait "the queue to drain on reconnect" (fun () -> async {
                let! queued = dataInt page "#queued" "data-count"
                return queued = 0
            })

        do!
            wait "the badge to return to Online" (fun () -> async {
                let! status = dataText page "#status" "data-status"
                return status = "Online"
            })

        // The claim that a drained queue alone cannot make.
        do!
            wait "the server to hold the update that was replayed" (fun () -> async {
                return serverHas attempt doc session
            })

        let! strays = strayEntitySyncCalls page

        Expect.equal
            strays
            0
            "a mergeable CRDT payload must be routed to its channel, never replayed through IOfflineSyncApi"
    })

    // ── the go-red ──────────────────────────────────────────────
    scenario "GO-RED: with the queue dropping, the offline edit is never applied" (fun attempt -> async {
        let doc = "offline-broken"
        let session = "dropped"

        let! page = openPage attempt "solo" (sprintf "mode=offline&doc=%s&session=%s&fault=queue" doc session)

        do! page.Context.SetOfflineAsync true |> Async.AwaitTask

        do!
            wait "the status badge to report Offline" (fun () -> async {
                let! status = dataText page "#status" "data-status"
                return status.StartsWith "Offline"
            })

        do! fill page "written while disconnected"

        // Nothing was held, so nothing is ever queued…
        do!
            stayFalse "the queue to hold anything with Enqueue dropping" (fun () -> async {
                let! queued = dataInt page "#queued" "data-count"
                return queued >= 1
            })

        do! page.Context.SetOfflineAsync false |> Async.AwaitTask

        // …and reconnecting cannot replay what was never held.
        do!
            stayFalse "the server to receive the dropped update after reconnect" (fun () -> async {
                return serverHas attempt doc session
            })
    })
]

let tests =
    BrowserGate.gated "browser smoke — offline round trip (Phases 24 / 760)" scenarios