// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.BrowserSmoke.Tests.ShellSmokeTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Runtime.ExceptionServices
open System.Text
open System.Text.Json
open Expecto
open Microsoft.Playwright
open ToolUp.Reporting.HtmlPdf.Tests
open ToolUp.BrowserSmoke.Tests.Harness

// ─── The shell, mounted from source, with its tree surfaces opened in it ──
//
// The other scenarios mount one component on a fixture page. These run the
// whole shell as a deployment serves it: the HelloWorld sample's server
// process, on the sample's claimed port, serving the sample's client bundle,
// signed in as the sample's development identity (a platform admin in team
// mode). Each surface is opened the way a person opens it — from the shell's
// own navigation rail, or for the command palette its keyboard shortcut —
// then its tree is awaited, one dispatch is made, and its result asserted.
//
// Any console error or page error fails the case, collected from the moment
// the page opens: a surface that renders and logs a failed request, or that
// throws while it mounts, is the regression these exist to see.
//
// A phase that moves a view the shell opens adds its scenario here.
//
// The bundle, the server build and the npm tree are the VerifyBrowserSmoke
// target's to produce; a missing one is named rather than guessed at.

let private repoRoot () =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."))

let private sampleDir () =
    Path.Combine(repoRoot (), "samples", "HelloWorld")

/// The development identity profile the server declares in Debug builds.
[<Literal>]
let private DevIdentityProfile = "helloworld-dev-identity"

/// The first port of the sample's claimed server band, read from the claim
/// itself so the two cannot disagree.
let private claimedServerPort () =
    let file = Path.Combine(sampleDir (), "ports.json")
    use doc = JsonDocument.Parse(File.ReadAllText file)
    doc.RootElement.GetProperty("server").[0].GetInt32()

/// A fixed port another process holds is a named failure, never a host that
/// silently fails to bind while the browser talks to the other process.
let private ensurePortFree (port: int) =
    try
        let probe = TcpListener(IPAddress.Loopback, port)
        probe.Start()
        probe.Stop()
    with :? SocketException as ex ->
        failwithf
            "browser-smoke: port %d, the HelloWorld sample's claimed server port (samples/HelloWorld/ports.json), is in use (%s). Stop whatever holds it — a HelloWorld started by `run.ps1` is the usual one — and run again."
            port
            ex.Message

/// A running HelloWorld server: the process, its private data directory and
/// what it has printed, for the failure message.
type private ShellHost = {
    BaseUrl: string
    Process: Process
    DataDir: string
    Output: StringBuilder
}

let private outputOf (host: ShellHost) =
    lock host.Output (fun () -> host.Output.ToString())

let private stopHost (host: ShellHost) =
    try
        if not host.Process.HasExited then
            host.Process.Kill true
            host.Process.WaitForExit 10_000 |> ignore
    with _ ->
        ()

    host.Process.Dispose()

    try
        Directory.Delete(host.DataDir, true)
    with _ ->
        ()

/// Start the sample's server under its development identity, serving the
/// built client, in a fresh working directory (the local blob store is
/// `<cwd>/data`, so every attempt starts from an empty store and the
/// identity's bootstrap runs again). Returns once the page is served.
let private startHost () : ShellHost =
    let sample = sampleDir ()

    let serverDll =
        Path.Combine(sample, "HelloWorld.Server", "bin", "Debug", "net10.0", "HelloWorld.Server.dll")

    let dist = Path.Combine(sample, "HelloWorld.Client", "dist")

    if not (File.Exists serverDll) then
        failwithf
            "browser-smoke: the HelloWorld server is not built at %s. The VerifyBrowserSmoke target builds it (Debug: the development identity exists only there)."
            serverDll

    if not (File.Exists(Path.Combine(dist, "index.html"))) then
        failwithf
            "browser-smoke: the HelloWorld client bundle is missing at %s. The VerifyBrowserSmoke target builds it: npm ci, dotnet fable and vite build in samples/HelloWorld/HelloWorld.Client, with TOOLUP_PROFILE=%s."
            dist
            DevIdentityProfile

    let port = claimedServerPort ()
    ensurePortFree port

    let dataDir =
        Path.Combine(Path.GetTempPath(), sprintf "toolup-browser-smoke-helloworld-%s" (Guid.NewGuid().ToString "N"))

    Directory.CreateDirectory dataDir |> ignore

    let info = ProcessStartInfo("dotnet", [ serverDll ])
    info.WorkingDirectory <- dataDir
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.Environment["TOOLUP_PROFILE"] <- DevIdentityProfile
    info.Environment["SERVER_PORT"] <- string port
    info.Environment["TOOLUP_PUBLIC_PATH"] <- dist

    let output = StringBuilder()
    let proc = new Process(StartInfo = info)

    let append (line: string) =
        if not (isNull line) then
            lock output (fun () -> output.AppendLine line |> ignore)

    proc.OutputDataReceived.Add(fun e -> append e.Data)
    proc.ErrorDataReceived.Add(fun e -> append e.Data)
    proc.Start() |> ignore
    proc.BeginOutputReadLine()
    proc.BeginErrorReadLine()

    let host = {
        BaseUrl = sprintf "http://127.0.0.1:%d" port
        Process = proc
        DataDir = dataDir
        Output = output
    }

    use http = new HttpClient(Timeout = TimeSpan.FromSeconds 2.0)
    let deadline = DateTime.UtcNow.AddSeconds 90.0
    let mutable ready = false

    while not ready && DateTime.UtcNow < deadline && not proc.HasExited do
        try
            use response = http.GetAsync(host.BaseUrl + "/").GetAwaiter().GetResult()
            ready <- response.StatusCode = HttpStatusCode.OK
        with _ ->
            ()

        if not ready then
            Threading.Thread.Sleep 250

    if not ready then
        let log = outputOf host
        stopHost host

        failwithf
            "browser-smoke: the HelloWorld server did not serve its page on %s within 90s. Its output:\n%s"
            host.BaseUrl
            log

    host

let private artifactRoot () =
    let dir = Path.Combine(AppContext.BaseDirectory, "artifacts")
    Directory.CreateDirectory dir |> ignore
    dir

/// The shell is up once its navigation rail has drawn: every rail stop
/// carries `data-toolup-rail-stop`.
let private shellReady = "[data-toolup-rail-stop^='group:']"

/// One attempt: a fresh host, a fresh browser, the page opened at the shell,
/// the body, then the page's own errors. A trace and a screenshot are kept on
/// a failing attempt only, as the other scenarios do.
let private runAttempt (scenarioName: string) (attemptIndex: int) (body: IPage -> Async<unit>) = async {
    let host = startHost ()

    try
        use! playwright = Playwright.CreateAsync() |> Async.AwaitTask

        let! browser =
            playwright.Chromium.LaunchAsync(
                BrowserTypeLaunchOptions(Headless = true, Args = [| "--no-sandbox"; "--disable-dev-shm-usage" |])
            )
            |> Async.AwaitTask

        try
            let! context = browser.NewContextAsync() |> Async.AwaitTask

            do!
                context.Tracing.StartAsync(TracingStartOptions(Screenshots = true, Snapshots = true, Sources = false))
                |> Async.AwaitTask

            let! page = context.NewPageAsync() |> Async.AwaitTask
            let errors = ResizeArray<string>()

            page.Console.Add(fun message ->
                if message.Type = "error" then
                    lock errors (fun () -> errors.Add(sprintf "console error: %s" message.Text)))

            page.PageError.Add(fun error -> lock errors (fun () -> errors.Add(sprintf "page error: %s" error)))

            // A console line for a failed load does not name the URL; the
            // response does, so the failure message carries both.
            let refused = ResizeArray<string>()

            page.Response.Add(fun response ->
                if response.Status >= 400 then
                    lock refused (fun () -> refused.Add(sprintf "%d %s" response.Status response.Url)))

            try
                let! _ = page.GotoAsync(host.BaseUrl + "/") |> Async.AwaitTask

                do!
                    page.WaitForSelectorAsync(shellReady, PageWaitForSelectorOptions(Timeout = 30_000.0f))
                    |> Async.AwaitTask
                    |> Async.Ignore

                do! body page

                let seen = lock errors (fun () -> List.ofSeq errors)

                if not seen.IsEmpty then
                    let responses = lock refused (fun () -> List.ofSeq refused)

                    failtestf
                        "the page reported %d error(s):\n%s\nresponses refused: %s"
                        seen.Length
                        (String.concat "\n" seen)
                        (if responses.IsEmpty then
                             "none"
                         else
                             String.concat ", " responses)

                do! context.Tracing.StopAsync() |> Async.AwaitTask
            with ex ->
                let stem = sprintf "shell-%s-attempt%d" scenarioName attemptIndex

                let stem =
                    String(
                        stem
                        |> Seq.map (fun c -> if Char.IsLetterOrDigit c then c else '-')
                        |> Array.ofSeq
                    )

                let root = artifactRoot ()

                try
                    do!
                        page.ScreenshotAsync(
                            PageScreenshotOptions(Path = Path.Combine(root, stem + ".png"), FullPage = true)
                        )
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

                let seen = lock errors (fun () -> List.ofSeq errors)

                if not seen.IsEmpty then
                    printfn "browser-smoke [%s] the page reported:\n%s" scenarioName (String.concat "\n" seen)

                printfn "browser-smoke [%s] the server printed:\n%s" scenarioName (outputOf host)
                ExceptionDispatchInfo.Capture(ex).Throw()
        finally
            browser.CloseAsync().GetAwaiter().GetResult()
    finally
        stopHost host
}

/// A shell scenario: one retry, reported when it is used, as `Harness.scenario`.
let private shellScenario (name: string) (body: IPage -> Async<unit>) : Test =
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

/// Open a page from the navigation rail: its group, then its row. The rail
/// expands while the pointer is over it, and its expanded form overlays the
/// page's left edge, so the pointer then leaves it, as a person's does on
/// the way to the page.
let private openFromRail (page: IPage) (group: string) (moduleId: string) = async {
    let row = page.Locator(sprintf "[data-toolup-rail-stop='row:%s:%s']" group moduleId)
    let! shown = row.CountAsync() |> Async.AwaitTask

    if shown = 0 then
        do!
            page.Locator(sprintf "[data-toolup-rail-stop='group:%s']" group).ClickAsync()
            |> Async.AwaitTask

    do! row.ClickAsync() |> Async.AwaitTask
    do! page.Mouse.MoveAsync(1000.0f, 600.0f) |> Async.AwaitTask
}

let private count (page: IPage) (selector: string) = async {
    let! n = page.Locator(selector).CountAsync() |> Async.AwaitTask
    return n
}

let private innerTextOf (page: IPage) (selector: string) = async {
    let! n = count page selector

    if n = 0 then
        return ""
    else
        let! text = page.Locator(selector).First.InnerTextAsync() |> Async.AwaitTask
        return text
}

let private node (id: string) = sprintf "[data-fuaran-node-id='%s']" id

let private scenarios = [
    shellScenario "the team manager opens from the rail and manages the identity's team" (fun page -> async {
        do! openFromRail page "Team Management" "_sdk.TeamManager"

        do!
            wait "the team manager's tree to list the bootstrap team" (fun () -> async {
                let! text = innerTextOf page (node "team-manager")
                return text.Contains "Hello World team"
            })

        do!
            page
                .Locator(node "team-manager")
                .GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Manage"))
                .ClickAsync()
            |> Async.AwaitTask

        do!
            wait "the team's members, with the identity as its Owner" (fun () -> async {
                let! members = innerTextOf page (node "members")
                return members.Contains "dev-admin" && members.Contains "Owner"
            })
    })

    shellScenario "the module-visibility editor opens from the rail and names a module on a toggle" (fun page -> async {
        do! openFromRail page "Team Management" "_sdk.ModuleVisibilityAdmin"

        let editor = node "visibility-editor"
        let toggle = sprintf "%s input[type='checkbox']" editor

        do!
            wait "the editor's tree to list the one registered module, none named" (fun () -> async {
                let! boxes = count page toggle
                let! text = innerTextOf page editor
                return boxes = 1 && text.Contains "HelloWorld" && text.Contains "0 named"
            })

        do! page.Locator(toggle).ClickAsync() |> Async.AwaitTask

        do!
            wait "the toggle to name the module in the profile being edited" (fun () -> async {
                let! isChecked = page.Locator(toggle).IsCheckedAsync() |> Async.AwaitTask
                let! text = innerTextOf page editor
                return isChecked && text.Contains "1 named"
            })
    })

    shellScenario "Platform Management opens from the rail and its Teams tab lists the platform's teams" (fun page -> async {
        do! openFromRail page "Platform Management" "_sdk.PlatformAdmin"

        let tabs = node "platform-admin-tabs"

        let teamsTab =
            page.Locator(tabs).GetByRole(AriaRole.Tab, LocatorGetByRoleOptions(Name = "Teams"))

        do!
            wait "the Platform Management tree, on its Admins tab, listing the identity" (fun () -> async {
                let! tabCount = count page (sprintf "%s [role='tab']" tabs)
                let! text = innerTextOf page tabs
                return tabCount = 3 && text.Contains "dev-admin"
            })

        let! before = teamsTab.GetAttributeAsync "aria-selected" |> Async.AwaitTask
        Expect.equal before "false" "the Teams tab is not the one open"

        do! teamsTab.ClickAsync() |> Async.AwaitTask

        do!
            wait "the Teams tab to open and list the bootstrap team" (fun () -> async {
                let! selected = teamsTab.GetAttributeAsync "aria-selected" |> Async.AwaitTask
                let! text = innerTextOf page tabs

                return
                    selected = "true"
                    && text.Contains "Hello World team"
                    && text.Contains "helloworld"
            })
    })

    shellScenario "the command palette opens on Ctrl+K, narrows on a query and navigates" (fun page -> async {
        let input = "[data-toolup-palette='input']"
        let rows = "[data-toolup-palette='results'] [data-toolup-palette-index]"

        do! page.Keyboard.PressAsync "Control+k" |> Async.AwaitTask

        do!
            wait "the palette's query box, with every visible page listed" (fun () -> async {
                let! boxes = count page input
                let! listed = count page rows
                return boxes = 1 && listed > 4
            })

        do! page.Locator(input).FillAsync "visibility" |> Async.AwaitTask

        do!
            wait "the rows to narrow to the one match" (fun () -> async {
                let! listed = count page rows
                let! text = innerTextOf page "[data-toolup-palette='results']"
                return listed = 1 && text.Contains "Module Visibility"
            })

        do! page.Keyboard.PressAsync "Enter" |> Async.AwaitTask

        do!
            wait "the palette to close on the module-visibility editor" (fun () -> async {
                let! boxes = count page input
                let! editors = count page (node "visibility-editor")
                return boxes = 0 && editors = 1
            })
    })
]

let tests =
    BrowserGate.gated "browser smoke — the shell's tree surfaces, opened in a running host" scenarios