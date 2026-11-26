// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.BrowserSmoke.Tests.InviteModalSmokeTests

open Expecto
open Microsoft.Playwright
open ToolUp.Reporting.HtmlPdf.Tests
open ToolUp.BrowserSmoke.Tests.Harness

// ─── The team manager's invite dialog, clicked in a real browser ─────
//
// The dialog is the team manager's tree, fetched on demand with the
// renderer, in `?mode=invite`. A click inside the dialog bubbles to its
// backdrop in a browser's own event dispatch, which is the case a jsdom
// mount does not exercise as a user meets it: the dialog must stay open on
// it, and close on a click on the backdrop itself. `#invite-state` carries
// the module model's answer, so each step is asserted on the model too.

// The page carries no stylesheet, so the backdrop is given the shape the
// renderer's reference stylesheet gives it: the whole viewport, the dialog
// inset within it. A click at the page's corner then lands on the backdrop.
let private backdropShape =
    ".fuaran-modal-overlay { position: fixed; inset: 0; padding: 64px; }"

let private isOpen (page: IPage) = async {
    let! state = dataText page "#invite-state" "data-open"
    return state = "true"
}

// The modal node itself carries `role=dialog` around its overlay, so the
// dialog is found by the renderer's class for it.
let private dialogCount (page: IPage) = async {
    let! count = page.Locator(".fuaran-modal-dialog").CountAsync() |> Async.AwaitTask
    return count
}

let private scenarios = [
    scenario "the invite dialog stays open on a click inside it and closes on its backdrop" (fun attempt -> async {
        let! page = openPage attempt "invite" "mode=invite"

        do!
            page.AddStyleTagAsync(PageAddStyleTagOptions(Content = backdropShape))
            |> Async.AwaitTask
            |> Async.Ignore

        do!
            wait "the invite dialog to arrive with the renderer" (fun () -> async {
                let! count = dialogCount page
                return count = 1
            })

        // Inside: the dialog's heading and its email field. Each
        // click bubbles to the backdrop and none may dismiss.
        do!
            page.Locator(".fuaran-modal-dialog .fuaran-modal-heading").First.ClickAsync()
            |> Async.AwaitTask

        do! page.Locator(".fuaran-modal-dialog input").First.ClickAsync() |> Async.AwaitTask

        do!
            page.Locator(".fuaran-modal-dialog input").First.FillAsync("a@example.com")
            |> Async.AwaitTask

        do!
            stayFalse "a click inside the dialog to close it" (fun () -> async {
                let! openNow = isOpen page
                let! count = dialogCount page
                return not openNow || count = 0
            })

        // The backdrop itself: the page's corner, outside the inset dialog.
        do! page.Mouse.ClickAsync(8.0f, 8.0f) |> Async.AwaitTask

        do!
            wait "a click on the backdrop to close the dialog through update" (fun () -> async {
                let! openNow = isOpen page
                let! count = dialogCount page
                return not openNow && count = 0
            })
    })
]

let tests =
    BrowserGate.gated "browser smoke — the team manager's invite dialog" scenarios