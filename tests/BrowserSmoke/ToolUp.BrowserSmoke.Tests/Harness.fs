// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Scenario scaffolding: browser lifetime, the flake policy, the
/// failure artefacts, and the two waits every scenario is written in
/// terms of.
module ToolUp.BrowserSmoke.Tests.Harness

open System
open System.IO
open System.Runtime.ExceptionServices
open Expecto
open Microsoft.Playwright

// ─── Phase 761 — flake policy, stated once ───────────────────────────
//
// **One retry, then red.** A browser scenario has genuine sources of
// nondeterminism a unit test does not — process startup, a renderer
// under load, an OS scheduler — and a harness with no retry converts
// those into a red gate people learn to re-run rather than read. A
// harness with UNBOUNDED retries converts a real regression into a slow
// one. One is the smallest number that absorbs the first class without
// hiding the second, and the retry is REPORTED: a scenario that passed
// on the second attempt says so in its own output, so a scenario that
// starts needing the retry is visible long before it starts failing.
//
// **Failure artefacts: a Playwright trace per browser context and a
// screenshot per page**, written under `artifacts/` beside the test
// binary and named for the scenario and the attempt. A trace carries
// the DOM snapshots, the network log and the console — which is the
// whole diagnosis for a scenario that failed on a machine nobody can
// log into. Artefacts are written on the FAILING attempt only; a green
// run leaves nothing behind.
//
// Everything a scenario waits for is a FACT — an attribute, a value, a
// row in the store — never a duration. `wait` polls a predicate to a
// generous ceiling, so a slow machine costs seconds and a broken build
// costs one honest timeout naming what it was waiting for.

let private pollIntervalMs = 100

/// The ceiling for a positive wait. Generous on purpose: it is a
/// failure budget, not an expectation — the convergence steps settle in
/// well under a second locally.
let defaultTimeoutMs = 20_000

/// How long a NEGATIVE assertion watches before concluding the thing
/// genuinely does not happen. Shorter than the positive ceiling, and
/// deliberately several times the fixture's 200 ms relay poll: the
/// go-red scenarios assert an absence, and an absence is only
/// meaningful once the mechanism that would have produced it has had
/// many chances to run.
let quiescenceMs = 4_000

exception ScenarioTimeout of string

/// Poll `probe` until it holds. Raises `ScenarioTimeout` naming what
/// was awaited — the message is the whole diagnosis in a CI log.
let wait (description: string) (probe: unit -> Async<bool>) : Async<unit> = async {
    let deadline = DateTime.UtcNow.AddMilliseconds(float defaultTimeoutMs)
    let mutable satisfied = false

    while not satisfied && DateTime.UtcNow < deadline do
        let! result = probe ()

        if result then
            satisfied <- true
        else
            do! Async.Sleep pollIntervalMs

    if not satisfied then
        raise (ScenarioTimeout(sprintf "timed out after %dms waiting for: %s" defaultTimeoutMs description))
}

/// Assert `probe` stays false for the quiescence window. The go-red
/// half of each scenario: with the seam cut, the thing under test must
/// not happen — and "not yet" is only evidence once you have waited.
let stayFalse (description: string) (probe: unit -> Async<bool>) : Async<unit> = async {
    let deadline = DateTime.UtcNow.AddMilliseconds(float quiescenceMs)

    while DateTime.UtcNow < deadline do
        let! result = probe ()

        if result then
            failtestf "expected NOT to happen, but it did: %s" description

        do! Async.Sleep pollIntervalMs
}

/// One scenario attempt: the pages it opened, so the runner can
/// screenshot and trace them when it fails.
type Attempt = {
    Host: FixtureHost.Host
    Browser: IBrowser
    Opened: ResizeArray<string * IBrowserContext * IPage>
}

/// The directory the fixture bundle is served from. Resolved relative
/// to the test binary, which sits under
/// `tests/BrowserSmoke/ToolUp.BrowserSmoke.Tests/bin/<config>/<tfm>/`.
let fixtureDist () =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixture", "dist"))

let private artifactRoot () =
    let dir = Path.Combine(AppContext.BaseDirectory, "artifacts")
    Directory.CreateDirectory dir |> ignore
    dir

/// Open a page in its OWN browser context. A context is the isolation
/// boundary that matters here: separate storage (so two participants do
/// not share one IndexedDB queue) and separate network state (so
/// `SetOfflineAsync` cuts one participant's link and not the other's).
let openPage (attempt: Attempt) (label: string) (query: string) : Async<IPage> = async {
    let! context = attempt.Browser.NewContextAsync() |> Async.AwaitTask

    do!
        context.Tracing.StartAsync(TracingStartOptions(Screenshots = true, Snapshots = true, Sources = false))
        |> Async.AwaitTask

    let! page = context.NewPageAsync() |> Async.AwaitTask

    // Surface what the page says about itself. Without this a fixture
    // that throws during boot presents as a wait that times out on
    // something several steps later, which is a much longer diagnosis
    // than the one line the browser already printed.
    // A page that has been deliberately taken offline logs a resource
    // error per failed request; that is the scenario working, so it is
    // filtered rather than reported as harness output.
    let expectedWhileOffline (text: string) =
        text.Contains "ERR_INTERNET_DISCONNECTED" || text.Contains "Failed to fetch"

    page.Console.Add(fun message ->
        if
            (message.Type = "error" || message.Type = "warning")
            && not (expectedWhileOffline message.Text)
        then
            printfn "browser-smoke [%s console %s] %s" label message.Type message.Text)

    page.PageError.Add(fun error -> printfn "browser-smoke [%s pageerror] %s" label error)

    attempt.Opened.Add(label, context, page)

    let! _ = page.GotoAsync(sprintf "%s/?%s" attempt.Host.BaseUrl query) |> Async.AwaitTask

    // The fixture sets `data-ready` once the pump is running, so the
    // scenario waits on the app rather than on the navigation.
    do!
        page.WaitForSelectorAsync("#fixture[data-ready='true']", PageWaitForSelectorOptions(Timeout = 30_000.0f))
        |> Async.AwaitTask
        |> Async.Ignore

    return page
}

/// The value of a textarea, as the DOM holds it.
let textOf (page: IPage) (selector: string) : Async<string> = async {
    let! value = page.InputValueAsync selector |> Async.AwaitTask
    return value
}

/// An integer read off a `data-` attribute the fixture maintains.
let dataInt (page: IPage) (selector: string) (attribute: string) : Async<int> = async {
    let! raw = page.GetAttributeAsync(selector, attribute) |> Async.AwaitTask

    return
        match Int32.TryParse raw with
        | true, value -> value
        | _ -> -1
}

let dataText (page: IPage) (selector: string) (attribute: string) : Async<string> = async {
    let! raw = page.GetAttributeAsync(selector, attribute) |> Async.AwaitTask
    return if isNull raw then "" else raw
}

let private capture (scenario: string) (attemptIndex: int) (attempt: Attempt) = async {
    let root = artifactRoot ()

    for label, context, page in attempt.Opened do
        let stem = sprintf "%s-attempt%d-%s" scenario attemptIndex label

        try
            do!
                page.ScreenshotAsync(PageScreenshotOptions(Path = Path.Combine(root, stem + ".png"), FullPage = true))
                |> Async.AwaitTask
                |> Async.Ignore
        with _ ->
            ()

        try
            do!
                context.Tracing.StopAsync(TracingStopOptions(Path = Path.Combine(root, stem + "-trace.zip")))
                |> Async.AwaitTask
        with _ ->
            ()
}

let private discardTraces (attempt: Attempt) = async {
    for _, context, _ in attempt.Opened do
        try
            do! context.Tracing.StopAsync() |> Async.AwaitTask
        with _ ->
            ()
}

/// Run one scenario body against a fresh host + browser, retrying once.
///
/// A fresh host per ATTEMPT, not per scenario: the store is the thing
/// under observation, and a retry that inherited the failed attempt's
/// updates would be answering a different question.
let private runAttempt (scenario: string) (attemptIndex: int) (body: Attempt -> Async<unit>) = async {
    let host = FixtureHost.start (fixtureDist ())
    use! playwright = Playwright.CreateAsync() |> Async.AwaitTask

    let! browser =
        playwright.Chromium.LaunchAsync(
            BrowserTypeLaunchOptions(
                Headless = true,
                // Standard CI hardening: the sandbox needs kernel
                // namespaces a container often does not grant, and this
                // browser only ever loads the harness's own loopback
                // bundle.
                Args = [| "--no-sandbox"; "--disable-dev-shm-usage" |]
            )
        )
        |> Async.AwaitTask

    let attempt = {
        Host = host
        Browser = browser
        Opened = ResizeArray()
    }

    try
        try
            do! body attempt
            do! discardTraces attempt
        with ex ->
            do! capture scenario attemptIndex attempt
            // `reraise` is not available inside an async computation
            // expression; this preserves the original stack trace,
            // which matters when the failure is a Playwright timeout
            // several frames deep.
            ExceptionDispatchInfo.Capture(ex).Throw()
    finally
        try
            browser.CloseAsync().GetAwaiter().GetResult()
        with _ ->
            ()

        host.Stop()
}

/// A browser scenario as an Expecto case: gated on a locally-installed
/// Chromium (Pending, never Failed, on a checkout without one — the
/// `BrowserGate` convention Phase 126 established), retried once, and
/// reporting the retry when it uses it.
let scenario (name: string) (body: Attempt -> Async<unit>) : Test =
    testCaseAsync
        name
        (async {
            try
                do! runAttempt name 1 body
            with first ->
                printfn "browser-smoke: %s failed its first attempt (%s); retrying once." name first.Message

                do! runAttempt name 2 body

                printfn "browser-smoke: %s PASSED ON RETRY — treat as a flake signal, not a green." name
        })