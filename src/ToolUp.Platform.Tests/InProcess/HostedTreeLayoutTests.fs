module ToolUp.Platform.Tests.InProcess.HostedTreeLayoutTests

open System.IO
open Expecto
open Feliz
open ToolUp.Platform
open Toolup.Samples.ToyTreeBinding

// ─── Phase 267 — multi-region / PageContent hosted-tree composition ────
//
// Phase 110's `withElementView` hosts a hosted tree in exactly ONE
// full-width region. Phase 267 adds `withElementPanes` (control + output
// panes → `PageContent.SplitPanel`) and `withElementPages` (a hosted tree
// across every `PageContent` case), so a hosted module is a first-class
// LAYOUT peer of a hand-authored Feliz module. This pack proves, tier-
// neutrally (Feliz `ReactElement` is Fable-only and never invoked here —
// the `dotnet fable` pass on MinimalClient is the render gate):
//
//   1. `withElementPanes` populates the single-page `View` slot (the
//      SplitPanel shape) and leaves `PageViews` unset; `withElementPages`
//      populates `PageViews` across the declared routes and leaves `View`
//      unset — the two hosted overloads are peers of `withView` /
//      `withPages`.
//   2. A hosted tree renders into BOTH panes of a split layout and across
//      the PAGES of a multi-page layout (proven by the toy's tier-neutral
//      `lowerToHtml`, captured per region).
//   3. Capabilities reach the right concretes from EVERY region: a
//      `Navigate` from any region routes to the shipped `NavigationRequest`
//      hook and a `Dispatch` routes to the module's own dispatch — one bag,
//      built once from dispatch, shared across regions.
//   4. A hosted tree drives every `PageContent` case (SplitPanel / Stacked
//      / FullWidth / Dashboard).
//   5. GP 11 — the Phase 110 `withElementView` is untouched; GP 1 — the
//      seam sources carry no banned OSS vocabulary (grep-guard).

// ─── Hosting-module state (a toy-shaped MVU loop) ─────────────────────

type private Model = { Count: int }

type private Msg =
    | Bumped
    | Echoed of string

let private init () : Model * ToolUp.Elmish.Cmd<Msg> = { Count = 0 }, ToolUp.Elmish.Cmd.none

let private update (msg: Msg) (model: Model) : Model * ToolUp.Elmish.Cmd<Msg> =
    match msg with
    | Bumped -> { model with Count = model.Count + 1 }, ToolUp.Elmish.Cmd.none
    | Echoed _ -> model, ToolUp.Elmish.Cmd.none

let private baseModule () : ClientModule<Model, Msg> =
    ClientModule.create {
        Init = init
        Update = update
        Name = "Hosted Layout"
        Icon = Unchecked.defaultof<ReactElement>
    }

/// A representative toy subtree for a named region, lowered to a static
/// HTML string (tier-neutral — no Feliz) so the .NET runner can prove a
/// region actually rendered the hosted tree.
let private regionTree (label: string) (model: Model) : ToyNode.ToyNode =
    ToyNode.Element("section", [ ToyNode.Text $"{label}: count={model.Count}" ])

let private pageConfig (route: string) (title: string) : PageConfig = {
    Route = route
    Title = title
    Icon = Unchecked.defaultof<ReactElement>
}

// ─── Source location (grep-guard) ─────────────────────────────────────

let private repoRoot () =
    let asmDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
    // bin/Debug/net10.0 → ToolUp.Platform.Tests → src → toolup-forge
    Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."))

// ─── 1. Structural — the overloads populate the right layout slot ─────

let private structuralTests =
    testList "Phase 267 — hosted overloads populate the right layout slot" [
        testCase "withElementPanes populates the single-page View slot (SplitPanel shape)"
        <| fun _ ->
            let m =
                baseModule ()
                |> ClientHostView.withElementPanes (fun _ _ _ ->
                    Unchecked.defaultof<ReactElement>, Unchecked.defaultof<ReactElement>)

            Expect.isSome m.View "withElementPanes sets the single-page View (the SplitPanel tuple shape)"
            Expect.isNone m.PageViews "withElementPanes is single-page — PageViews stays unset"

        testCase "withElementPages populates PageViews across the declared routes"
        <| fun _ ->
            let pages = [ pageConfig "/overview" "Overview"; pageConfig "/detail" "Detail" ]

            let m =
                baseModule ()
                |> ClientHostView.withElementPages [
                    pages[0], (fun _ _ _ -> PageContent.Stacked [])
                    pages[1], (fun _ _ _ -> PageContent.Stacked [])
                ]

            Expect.isNone m.View "withElementPages is multi-page — the single View slot stays unset"

            match m.PageViews with
            | Some views ->
                Expect.isTrue (Map.containsKey "/overview" views) "the overview route is registered"
                Expect.isTrue (Map.containsKey "/detail" views) "the detail route is registered"
            | None -> failtest "withElementPages must set PageViews"

            let routes = m.Definition.Pages |> List.map _.Route
            Expect.equal routes [ "/overview"; "/detail" ] "Definition.Pages mirrors the declared page order"
    ]

// ─── 2. Render — a hosted tree renders into both panes / every page ───

let private renderTests =
    testList "Phase 267 — a hosted tree renders into every region" [
        testCase "a hosted tree renders into BOTH panes of a split layout"
        <| fun _ ->
            // Each pane lowers its own toy subtree; capture the two region
            // HTML strings the view produced (tier-neutral — no Feliz).
            let captured = System.Collections.Generic.List<string>()

            let paneView (model: Model) (_dispatch: Msg -> unit) (_host: ClientHostCapabilities<Msg>) =
                captured.Add(ToyNode.lowerToHtml (regionTree "control" model))
                captured.Add(ToyNode.lowerToHtml (regionTree "output" model))
                Unchecked.defaultof<ReactElement>, Unchecked.defaultof<ReactElement>

            let m = baseModule () |> ClientHostView.withElementPanes paneView
            let paneRender = m.View.Value
            paneRender { Count = 3 } ignore |> ignore

            Expect.hasLength captured 2 "both panes were produced"
            Expect.stringContains captured[0] "control: count=3" "the control pane rendered the hosted tree"
            Expect.stringContains captured[1] "output: count=3" "the output pane rendered the hosted tree"

        testCase "a hosted tree renders across the PAGES of a multi-page layout"
        <| fun _ ->
            let captured = System.Collections.Generic.List<string>()

            let mkPage label =
                fun (model: Model) (_dispatch: Msg -> unit) (_host: ClientHostCapabilities<Msg>) ->
                    captured.Add(ToyNode.lowerToHtml (regionTree label model))
                    PageContent.Stacked []

            let m =
                baseModule ()
                |> ClientHostView.withElementPages [
                    pageConfig "/a" "A", mkPage "page-a"
                    pageConfig "/b" "B", mkPage "page-b"
                ]

            let views = m.PageViews.Value
            let model = { Count = 7 }
            (Map.find "/a" views) model ignore |> ignore
            (Map.find "/b" views) model ignore |> ignore

            Expect.hasLength captured 2 "both pages rendered their hosted subtree"
            Expect.stringContains captured[0] "page-a: count=7" "page A rendered the hosted tree"
            Expect.stringContains captured[1] "page-b: count=7" "page B rendered the hosted tree"
    ]

// ─── 3. Capabilities reach concretes from every region ────────────────
//
// One capability bag is built from the module's dispatch and shared by
// every region. `Navigate` routes to the shipped `NavigationRequest` hook
// (a global concrete a test can subscribe to); `Dispatch` routes to the
// module's own dispatch (captured locally). `Notify` / `Call` route to
// Fable-only concretes (localStorage-backed session id / the OfRemoting
// effect) and are exercised by the Phase 110 pack + the `dotnet fable`
// pass, not the .NET runner.

let private capabilityTests =
    testList "Phase 267 — capabilities reach concretes from every region" [
        testCase "a Navigate from either pane routes to the NavigationRequest hook"
        <| fun _ ->
            let navigated = System.Collections.Generic.List<string>()
            let dispose = NavigationRequest.subscribe navigated.Add

            try
                let paneView (_model: Model) (_dispatch: Msg -> unit) (host: ClientHostCapabilities<Msg>) =
                    // Control pane and output pane both reach the same bag.
                    host.Navigate "FromControl"
                    host.Navigate "FromOutput"
                    Unchecked.defaultof<ReactElement>, Unchecked.defaultof<ReactElement>

                let m = baseModule () |> ClientHostView.withElementPanes paneView
                let paneRender = m.View.Value
                paneRender { Count = 0 } ignore |> ignore

                Expect.contains navigated "FromControl" "the control pane's Navigate reached the shipped hook"
                Expect.contains navigated "FromOutput" "the output pane's Navigate reached the shipped hook"
            finally
                dispose ()

        testCase "a Dispatch from any page routes to the module's own dispatch"
        <| fun _ ->
            let dispatched = System.Collections.Generic.List<Msg>()

            let pageView (_model: Model) (_dispatch: Msg -> unit) (host: ClientHostCapabilities<Msg>) =
                host.Dispatch Bumped
                PageContent.Stacked []

            let m =
                baseModule ()
                |> ClientHostView.withElementPages [ pageConfig "/p" "P", pageView ]

            let model = { Count = 0 }
            (Map.find "/p" m.PageViews.Value) model dispatched.Add |> ignore

            Expect.contains dispatched Bumped "the page's Dispatch routed to the module dispatch (the shared bag)"
    ]

// ─── 4. A hosted tree drives every PageContent case ───────────────────

let private pageContentCaseTests =
    testList "Phase 267 — a hosted tree drives every PageContent case" [
        testCase "each declared page picks its own layout shape"
        <| fun _ ->
            let m =
                baseModule ()
                |> ClientHostView.withElementPages [
                    pageConfig "/split" "Split",
                    (fun _ _ _ ->
                        PageContent.SplitPanel(Unchecked.defaultof<ReactElement>, Unchecked.defaultof<ReactElement>))
                    pageConfig "/stack" "Stack", (fun _ _ _ -> PageContent.Stacked [])
                    pageConfig "/full" "Full", (fun _ _ _ -> PageContent.FullWidth(Unchecked.defaultof<ReactElement>))
                    pageConfig "/dash" "Dash", (fun _ _ _ -> PageContent.Dashboard [])
                ]

            let views = m.PageViews.Value
            let model = { Count = 0 }

            match (Map.find "/split" views) model ignore with
            | PageContent.SplitPanel _ -> ()
            | other -> failtestf "expected SplitPanel; got %A" other

            match (Map.find "/stack" views) model ignore with
            | PageContent.Stacked _ -> ()
            | other -> failtestf "expected Stacked; got %A" other

            match (Map.find "/full" views) model ignore with
            | PageContent.FullWidth _ -> ()
            | other -> failtestf "expected FullWidth; got %A" other

            match (Map.find "/dash" views) model ignore with
            | PageContent.Dashboard _ -> ()
            | other -> failtestf "expected Dashboard; got %A" other
    ]

// ─── 5. GP 11 additive + OSS grep-guard ───────────────────────────────

let private boundaryTests =
    testList "Phase 267 — additive (GP 11) + OSS boundary" [
        testCase "the Phase 110 withElementView overload is untouched (GP 11)"
        <| fun _ ->
            // withElementPanes / withElementPages sit BESIDE withElementView;
            // the single-region overload still exists with its own shape.
            let m =
                baseModule ()
                |> ClientHostView.withElementView (fun _ _ _ -> Unchecked.defaultof<ReactElement>)

            Expect.isSome m.PageViews "withElementView remains the full-width single-page shape (via withFullWidthView)"

        testCase "the multi-region seam sources carry no banned OSS vocabulary (GP 1)"
        <| fun _ ->
            let seamFiles = [
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client", "Client", "ClientHostBridge.fs")
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client", "Client", "HostStateProjection.fs")
            ]

            for path in seamFiles do
                Expect.isTrue (File.Exists path) $"expected seam file at {path}"
                let contents = (File.ReadAllText path).ToLowerInvariant()
                Expect.isFalse (contents.Contains "fuaran") $"{path} must carry no Fuaran token (GP 1)"
    ]

[<Tests>]
// Shares the `NavigationRequest` process-global with HostRouteContractTests
// (Phase 276) and ClientHostCapabilitiesContract — the capability tests
// here subscribe + fire into it. Same `SequencedGroup` name so the three
// packs never run concurrently and no foreign thread fires into a live
// subscription. See HostRouteContractTests.fs for the full rationale.
let tests =
    testSequencedGroup "ToolUp.Platform.NavigationRequest"
    <| testList "Phase 267 — multi-region hosted-tree composition" [
        structuralTests
        renderTests
        capabilityTests
        pageContentCaseTests
        boundaryTests
    ]