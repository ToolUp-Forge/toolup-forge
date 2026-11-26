// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.BrowserSmoke.Tests.PaletteSmokeTests

open Expecto
open Microsoft.Playwright
open ToolUp.Reporting.HtmlPdf.Tests
open ToolUp.BrowserSmoke.Tests.Harness

// ─── The command palette, opened through its public component ───────
//
// `?mode=palette` mounts `CommandPalette` open, so the overlay's code arrives
// on demand through the deferred surface, the path a shell's first Ctrl+K
// takes. A test that calls the palette's tree directly passes its props by
// the F# call; only this path hands them to React as the shell does. The
// scenario types a query, then asserts on the DOM (the rows narrow) and on
// the page's model (`#palette-state` holds the query it received).

let private input = "[data-toolup-palette='input']"

let private rowCount (page: IPage) = async {
    let! count =
        page.Locator("[data-toolup-palette='results'] [data-toolup-palette-index]").CountAsync()
        |> Async.AwaitTask

    return count
}

let private scenarios = [
    scenario "the command palette opens with its entries and narrows them on a query" (fun attempt -> async {
        let! page = openPage attempt "palette" "mode=palette"

        do!
            wait "the palette's query box to arrive with the renderer" (fun () -> async {
                let! count = page.Locator(input).CountAsync() |> Async.AwaitTask
                return count = 1
            })

        do!
            wait "every entry to be listed before a query" (fun () -> async {
                let! count = rowCount page
                return count = 4
            })

        do! page.Locator(input).FillAsync("knowledge") |> Async.AwaitTask

        do!
            wait "the model to receive the query" (fun () -> async {
                let! query = dataText page "#palette-state" "data-query"
                return query = "knowledge"
            })

        do!
            wait "the rows to narrow to the one match" (fun () -> async {
                let! count = rowCount page

                let! text =
                    page.Locator("[data-toolup-palette='results']").InnerTextAsync()
                    |> Async.AwaitTask

                return count = 1 && text.Contains "Knowledge Base"
            })
    })
]

let tests = BrowserGate.gated "browser smoke — the command palette" scenarios