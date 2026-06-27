module ToolUp.AI.Client.Tests.HomeWidgetContributorTests

open ToolUp.AI.Client.Tests.NodeTest
open Feliz
open ToolUp.Platform

// ─── Phase 217 — module-contributed Home-widget seam tests ────────────
//
// The seam under test lives in `ToolUp.Platform.Client`
// (`HomeWidget.fs` registry + `Home.fs` render + the boot wiring in
// `SDK.Client.fs`); the tests live in this Fable-tier harness because
// only here does `ClientConfig.defaults` resolve (the in-process
// Expecto runner throws on the AG Grid Fable `import`; see
// `HomeLandingTests.fs`).
//
// Pins the registry contract:
//   - zero contributors ⇒ `HomeWidgetRegistry.widgets ()` is empty, so
//     `Home.fs` renders byte-for-byte as Phase 171 (GP 13);
//   - one contributor ⇒ its widget is present;
//   - widgets sort by `Weight` ascending (ties keep registration order);
//   - `ClientConfig.defaults` ships no contributors (default-off).

/// A contributor declaring the supplied widgets — the value a module
/// would export and the consumer would add to
/// `ClientConfig.Handlers.HomeWidgetContributors`.
let private contributor (widgets: HomeWidget list) : IHomeWidgetContributor =
    { new IHomeWidgetContributor with
        member _.Widgets() = widgets
    }

/// Minimal widget with a chosen id + weight; the body is never rendered
/// in these registry-level tests.
let private widget (id: string) (weight: int) : HomeWidget = {
    Id = id
    Title = id
    Icon = Html.none
    Weight = weight
    Body = fun _ -> Html.none
}

let tests =
    testList "Home widget contributors (Phase 217)" [

        testCase "zero contributors ⇒ registry empty (renders as Phase 171)"
        <| fun () ->
            HomeWidgetRegistry.setContributors []
            Expect.isTrue (List.isEmpty (HomeWidgetRegistry.widgets ())) "no contributors ⇒ no widgets"

        testCase "default ClientConfig ships no contributors (GP 13, default-off)"
        <| fun () ->
            Expect.isTrue
                (List.isEmpty ClientConfig.defaults.Handlers.HomeWidgetContributors)
                "ClientConfig.defaults.Handlers.HomeWidgetContributors is empty"

        testCase "recents/pinning is off by default (GP 13)"
        <| fun () -> Expect.isFalse ClientConfig.defaults.HomeRecents "ClientConfig.defaults.HomeRecents is false"

        testCase "one contributor ⇒ its widget is present"
        <| fun () ->
            HomeWidgetRegistry.setContributors [ contributor [ widget "solo" 0 ] ]
            let ids = HomeWidgetRegistry.widgets () |> List.map _.Id
            Expect.equal ids [ "solo" ] "the single contributed widget is registered"

        testCase "widgets sort by Weight ascending across contributors"
        <| fun () ->
            // Two contributors, interleaved weights — the registry
            // flattens then sorts by Weight, so the final order is by
            // weight, not by contributor.
            HomeWidgetRegistry.setContributors [
                contributor [ widget "c1-heavy" 30; widget "c1-light" 10 ]
                contributor [ widget "c2-mid" 20 ]
            ]

            let ids = HomeWidgetRegistry.registeredIds ()
            Expect.equal ids [ "c1-light"; "c2-mid"; "c1-heavy" ] "widgets ordered by ascending Weight"

        testCase "equal weights keep registration order (stable sort)"
        <| fun () ->
            HomeWidgetRegistry.setContributors [
                contributor [ widget "first" 5; widget "second" 5 ]
                contributor [ widget "third" 5 ]
            ]

            let ids = HomeWidgetRegistry.registeredIds ()
            Expect.equal ids [ "first"; "second"; "third" ] "ties preserve registration order"

        testCase "re-setting contributors replaces the prior set"
        <| fun () ->
            HomeWidgetRegistry.setContributors [ contributor [ widget "stale" 0 ] ]
            HomeWidgetRegistry.setContributors [ contributor [ widget "fresh" 0 ] ]
            let ids = HomeWidgetRegistry.registeredIds ()
            Expect.equal ids [ "fresh" ] "setContributors is last-wins, not additive"
    ]