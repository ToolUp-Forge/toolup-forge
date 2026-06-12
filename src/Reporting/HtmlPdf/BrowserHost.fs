// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Reporting.HtmlPdf

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Playwright

// ─── Phase 126 — warm-browser lifecycle seam ─────────────────────
//
// Per-render browser launch is seconds-class; a warm instance prints
// in milliseconds. This host launches Chromium lazily on the first
// render, reuses it across renders, closes it after an idle period,
// and relaunches transparently when the process has died (crash
// recovery) — a dead browser never surfaces as a hung Async; render
// failures flow back as exceptions the renderer lowers to
// `RenderError`.

type internal BrowserHost(idleTimeout: TimeSpan, launchArgs: string list) =
    let gate = new SemaphoreSlim(1, 1)
    let mutable session: (IPlaywright * IBrowser) option = None
    let mutable lastUsed = DateTime.UtcNow
    let mutable activeRenders = 0
    let mutable idleSweeper: Timer option = None

    let closeSession () =
        match session with
        | Some(playwright, browser) ->
            session <- None

            try
                browser.CloseAsync().GetAwaiter().GetResult()
            with _ ->
                ()

            playwright.Dispose()
        | None -> ()

    let sweepIdle _ =
        // Best-effort: skip the sweep entirely if a render holds (or
        // is waiting on) the gate — the point is reclaiming a browser
        // nobody is using, never stalling a live render.
        if gate.Wait 0 then
            try
                let idle = DateTime.UtcNow - lastUsed

                if Volatile.Read &activeRenders = 0 && idle >= idleTimeout && session.IsSome then
                    closeSession ()
            finally
                gate.Release() |> ignore

    let ensureSweeper () =
        if idleSweeper.IsNone then
            let period = TimeSpan.FromSeconds(max 5.0 (idleTimeout.TotalSeconds / 4.0))
            idleSweeper <- Some(new Timer(sweepIdle, null, period, period))

    /// Resolve a connected browser, launching (or relaunching after a
    /// crash) as needed. Callers hold the gate.
    let ensureBrowser () = task {
        match session with
        | Some(_, browser) when browser.IsConnected -> return browser
        | _ ->
            // Either first use or the previous process died — discard
            // any dead session and launch fresh.
            closeSession ()
            let! playwright = Playwright.CreateAsync()

            let options =
                BrowserTypeLaunchOptions(Headless = true, Args = List.toArray launchArgs)

            let! browser = playwright.Chromium.LaunchAsync options
            session <- Some(playwright, browser)
            ensureSweeper ()
            return browser
    }

    /// Run `work` against a fresh page on the warm browser. The gate
    /// covers only browser acquisition; pages are independent, so
    /// concurrent renders share the instance.
    member _.WithPage<'T>(work: IPage -> Task<'T>) : Task<'T> = task {
        do! gate.WaitAsync()

        let! browser = task {
            try
                return! ensureBrowser ()
            finally
                gate.Release() |> ignore
        }

        Interlocked.Increment &activeRenders |> ignore

        try
            let! page = browser.NewPageAsync()

            try
                return! work page
            finally
                page.CloseAsync().GetAwaiter().GetResult()
        finally
            Interlocked.Decrement &activeRenders |> ignore
            lastUsed <- DateTime.UtcNow
    }

    /// Simulate a browser crash (tests): close the underlying process
    /// without clearing the session, leaving a disconnected handle
    /// exactly as a real crash would.
    member internal _.KillBrowserForTests() = task {
        match session with
        | Some(_, browser) -> do! browser.CloseAsync()
        | None -> ()
    }

    member internal _.HasLiveBrowserForTests =
        match session with
        | Some(_, browser) -> browser.IsConnected
        | None -> false

    /// Close the warm browser and stop the idle sweeper. The host
    /// remains usable — the next render relaunches.
    member _.Shutdown() =
        gate.Wait()

        try
            closeSession ()

            idleSweeper |> Option.iter _.Dispose()
            idleSweeper <- None
        finally
            gate.Release() |> ignore