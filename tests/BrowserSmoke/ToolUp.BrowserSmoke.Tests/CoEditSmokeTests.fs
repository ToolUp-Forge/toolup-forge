// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Scenario 1 — two browser contexts co-editing one document.
module ToolUp.BrowserSmoke.Tests.CoEditSmokeTests

open Expecto
open Microsoft.Playwright
open ToolUp.Reporting.HtmlPdf.Tests
open ToolUp.BrowserSmoke.Tests.Harness

// ─── Phase 535's acceptance, in a browser ────────────────────────────
//
// "Two sessions co-edit the sample doc live" shipped verified
// STRUCTURALLY — a contract-tested convergence and a green Fable
// compile — because nothing in the repo could open two browsers. This
// is that same sentence, executed.
//
// Three claims, in the order they build on each other:
//
//   1. an edit made in one context appears in the other;
//   2. edits made in BOTH contexts converge — the property that
//      distinguishes a CRDT from last-writer-wins, so it is asserted as
//      equality of the two documents rather than against a string one
//      participant happened to win;
//   3. a context whose link drops for several updates catches up on
//      reconnect, from its retained cursor.
//
// And the fourth, which is what makes the other three mean anything:
// with the fan-out cut (`?fault=relay`), claim 1 must FAIL — while the
// server still holds the edit, so the failure is provably the relay and
// not the transport.

let private fill (page: IPage) (text: string) =
    page.FillAsync("#coedit", text) |> Async.AwaitTask

let private sessions (attempt: Attempt) (doc: string) =
    attempt.Host.Updates doc |> List.map _.OriginSession |> Set.ofList

let private scenarios = [
    scenario "an edit in one context reaches the other" (fun attempt -> async {
        let doc = "relay"
        let! a = openPage attempt "a" (sprintf "mode=coedit&doc=%s&session=alpha" doc)
        let! b = openPage attempt "b" (sprintf "mode=coedit&doc=%s&session=bravo" doc)

        do! fill a "first"

        do!
            wait "context b to show the edit typed in context a" (fun () -> async {
                let! value = textOf b "#coedit"
                return value = "first"
            })

        do! fill b "first and second"

        do!
            wait "context a to show the edit typed in context b" (fun () -> async {
                let! value = textOf a "#coedit"
                return value = "first and second"
            })
    })

    scenario "edits from both contexts converge on one document" (fun attempt -> async {
        let doc = "converge"
        let! a = openPage attempt "a" (sprintf "mode=coedit&doc=%s&session=alpha" doc)
        let! b = openPage attempt "b" (sprintf "mode=coedit&doc=%s&session=bravo" doc)

        // Both type without waiting for the other. What the CRDT
        // promises is not a particular merge — it is that both
        // documents end up the SAME, whichever way the merge went,
        // so that is what is asserted.
        do!
            [ fill a "typed-in-alpha"; fill b "typed-in-bravo" ]
            |> Async.Parallel
            |> Async.Ignore

        do!
            wait "both contexts to hold an identical, non-empty document" (fun () -> async {
                let! left = textOf a "#coedit"
                let! right = textOf b "#coedit"
                return left = right && left <> ""
            })

        // …and that both participants genuinely contributed: a
        // convergence in which one side never published would
        // satisfy the equality above and prove nothing.
        let published = sessions attempt doc
        Expect.isTrue (published.Contains "alpha") "context a published no update"
        Expect.isTrue (published.Contains "bravo") "context b published no update"
    })

    scenario "a context disconnected for several updates catches up on reconnect" (fun attempt -> async {
        let doc = "reconnect"
        let! a = openPage attempt "a" (sprintf "mode=coedit&doc=%s&session=alpha" doc)
        let! b = openPage attempt "b" (sprintf "mode=coedit&doc=%s&session=bravo" doc)

        do! fill a "base"

        do!
            wait "context b to join at the shared base" (fun () -> async {
                let! value = textOf b "#coedit"
                return value = "base"
            })

        // Pull b's link. Its catch-up reads now fail; a's edits keep
        // landing in the log without it.
        do! b.Context.SetOfflineAsync true |> Async.AwaitTask

        for update in 1..4 do
            do! fill a (sprintf "base + update %d" update)

            do!
                wait (sprintf "the server to log update %d" update) (fun () -> async {
                    return (attempt.Host.Updates doc |> List.length) >= update
                })

        do!
            stayFalse "context b to have seen anything while its link was down" (fun () -> async {
                let! value = textOf b "#coedit"
                return value <> "base"
            })

        do! b.Context.SetOfflineAsync false |> Async.AwaitTask

        do!
            wait "context b to converge on reconnect" (fun () -> async {
                let! left = textOf a "#coedit"
                let! right = textOf b "#coedit"
                return left = right && right = "base + update 4"
            })
    })

    // ── the go-red ──────────────────────────────────────────────
    scenario "GO-RED: with the relay cut, the second context never converges" (fun attempt -> async {
        let doc = "relay-broken"

        let! a = openPage attempt "a" (sprintf "mode=coedit&doc=%s&session=alpha&fault=relay" doc)

        let! b = openPage attempt "b" (sprintf "mode=coedit&doc=%s&session=bravo&fault=relay" doc)

        do! fill a "never-relayed"

        // The publish path is intact — the server holds the edit…
        do!
            wait "the server to log the edit context a typed" (fun () -> async {
                return attempt.Host.Updates doc |> List.exists (fun u -> u.OriginSession = "alpha")
            })

        // …and with nothing asking for the diff, it never arrives.
        do!
            stayFalse "context b to receive the edit with the relay cut" (fun () -> async {
                let! value = textOf b "#coedit"
                return value <> ""
            })
    })
]

let tests = BrowserGate.gated "browser smoke — co-edit (Phase 535)" scenarios