module ToolUp.Platform.Tests.InProcess.HostRouteContractTests

open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.PublicRendering
open ToolUp.PublicRendering.PublicRenderingCompose
open Toolup.Samples.ToyTreeBinding.ToyNode

// ─── Phase 276 — hosted-tree navigation/route contract ─────────────────
//
// Phase 267 lets a hosted tree drive a multi-page module; Phase 110's
// `Navigate` reaches the shell router — but the module's pages had no
// stable URL, no deep link, no crawlability. Phase 276 ships one neutral
// route type (Core `HostRoute`) read by BOTH tiers: the client
// `IHostRouteContract` maps a path → a `NavigationRequest` deep-link +
// Phase 264 param restore, and the SSR `HostRouteRegistration` registers
// the same routes into the Phase 111 route table as enumerable content
// sources (crawlable). Six proofs:
//
//   1. A declared route deep-links to the right page with params; the
//      returned `HostBindingSources` restores the param state (round-trip).
//   2. The same route registers into the SSR route table (a matching slug
//      resolves; a non-matching slug falls through) and is enumerable
//      (crawlable — the concrete slug reaches the sitemap surface).
//   3. Back/forward navigation is consistent (deep-linking A then B fires
//      the shell hook in that order).
//   4. The param round-trip binds through the Phase 264 read-side: the toy
//      (a stranger tree language) resolves a `Bind` against the restored
//      namespace.
//   5. `HostRouteRegistration.register` appends the source to a
//      `PublicRenderingServerApp` (append-only, GP 11).
//   6. The seam sources carry no banned OSS vocabulary (GP 1 grep-guard).

let private run a = a |> Async.RunSynchronously

let private anonCtx: AccessContext =
    AccessContext.unrestricted (AnonymousSession "route-session")

let private reportsRoute: HostRoute = {
    PathPattern = "reports/{quarter}"
    SidebarId = "Reports/quarter"
    Title = "Quarterly Report"
}

let private homeRoute: HostRoute = {
    PathPattern = "home"
    SidebarId = "Home"
    Title = "Home"
}

let private contract = HostRouteContract.create [ homeRoute; reportsRoute ]

let private repoRoot () =
    let asmDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
    // bin/Debug/net10.0 → ToolUp.Platform.Tests → src → toolup-forge
    Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."))

// ─── 1. Client deep-link + param round-trip ───────────────────────────

let private deepLinkTests =
    testList "Phase 276 — client deep-link + param round-trip" [
        testCase "a declared route resolves to the matched route + captured params"
        <| fun _ ->
            match contract.TryResolve "reports/q3" with
            | Some(route, ps) ->
                Expect.equal route.SidebarId "Reports/quarter" "the reports route matched"
                Expect.equal (Map.tryFind "quarter" ps) (Some "q3") "the {quarter} segment was captured"
            | None -> failtest "the declared reports route must match 'reports/q3'"

        testCase "a non-matching path resolves to None (falls through)"
        <| fun _ ->
            Expect.isNone (contract.TryResolve "reports/q3/extra") "a segment-count mismatch does not match"
            Expect.isNone (contract.TryResolve "unknown") "an undeclared path does not match"

        testCase "deep-linking fires the shell nav hook + restores the param state"
        <| fun _ ->
            let navigated = System.Collections.Generic.List<string>()
            let dispose = NavigationRequest.subscribe navigated.Add

            try
                match HostRouteContract.deepLink contract "reports/q3" with
                | Some sources ->
                    Expect.contains navigated "Reports/quarter" "the deep link navigated the shell to the route's page"

                    Expect.equal
                        (HostBindingSources.tryResolve "quarter" sources)
                        (Some(box "q3"))
                        "the captured param restored into the Phase 264 binding namespace (round-trip)"
                | None -> failtest "the deep link must resolve the declared route"
            finally
                dispose ()

        testCase "an undeclared deep-link path navigates nothing"
        <| fun _ ->
            // `deepLink` returning None IS "navigates nothing" — it only fires
            // NavigationRequest on a Some. Asserting on the global listener
            // list would be racy (Expecto runs test cases in parallel and
            // NavigationRequest.listeners is a process-global).
            Expect.isNone
                (HostRouteContract.deepLink contract "nope")
                "no declared route → no deep link → no navigation"
    ]

// ─── 2. SSR route-table registration + crawlability ───────────────────

let private ssrRegistration: HostRouteRegistration =
    HostRouteRegistration.create
        reportsRoute
        (fun captures _ctx -> async {
            let quarter = Map.tryFind "quarter" captures |> Option.defaultValue "?"
            return Some(ResolvedContent.ofBody (ContentBody.Html $"<p>Report for {quarter}</p>"))
        })
        (fun () -> async { return [ Slug "reports/q3"; Slug "reports/q4" ] })

let private ssrTests =
    testList "Phase 276 — SSR route-table registration + crawlability" [
        testCase "a matching slug resolves to the route's body (registered into the route table)"
        <| fun _ ->
            let source = HostRouteRegistration.toContentSource ssrRegistration

            match source.Resolve (Slug "reports/q3") anonCtx |> run with
            | Some(ContentBody.Html html) ->
                Expect.stringContains html "Report for q3" "the SSR resolver rendered the captured param"
            | other -> failtestf "expected the hosted route's HTML body; got %A" other

        testCase "a non-matching slug falls through (None)"
        <| fun _ ->
            let source = HostRouteRegistration.toContentSource ssrRegistration
            Expect.isNone (source.Resolve (Slug "other/page") anonCtx |> run) "a non-matching slug falls through"

        testCase "the source is SEO-complete (IResolvedContentSource, Phase 111 richer channel)"
        <| fun _ ->
            let source = HostRouteRegistration.toContentSource ssrRegistration

            match source with
            | :? IResolvedContentSource as resolved ->
                match resolved.ResolveContent (Slug "reports/q4") anonCtx |> run with
                | Some rc ->
                    Expect.equal rc.Body (ContentBody.Html "<p>Report for q4</p>") "resolved-content body matches"
                | None -> failtest "the hosted route must claim its resolved-content slug"
            | _ -> failtest "a hosted route source must expose IResolvedContentSource"

        testCase "the route's concrete slugs are enumerable (crawlable — reach the sitemap)"
        <| fun _ ->
            let source = HostRouteRegistration.toContentSource ssrRegistration

            match source with
            | :? IEnumerableContentSource as enumerable ->
                let slugs = enumerable.EnumerateRoutes() |> run
                Expect.contains slugs (Slug "reports/q3") "q3 is discoverable for the sitemap"
                Expect.contains slugs (Slug "reports/q4") "q4 is discoverable for the sitemap"
            | _ -> failtest "a hosted route source must expose IEnumerableContentSource (crawlability)"
    ]

// ─── 3. Back/forward navigation consistency ───────────────────────────

let private backForwardTests =
    testList "Phase 276 — back/forward navigation consistency" [
        testCase "deep-linking A then B fires the shell hook in order"
        <| fun _ ->
            // Test-local contract with sidebar ids UNIQUE to this test, so the
            // exact-order assertion survives Expecto's parallel execution:
            // NavigationRequest.listeners is a process-global, so a concurrent
            // test's navigate lands in this subscriber too — we filter to this
            // test's own `bf/` prefix to isolate our three ordered fires.
            let localContract =
                HostRouteContract.create [
                    {
                        PathPattern = "bf-home"
                        SidebarId = "bf/Home"
                        Title = "H"
                    }
                    {
                        PathPattern = "bf-reports"
                        SidebarId = "bf/Reports"
                        Title = "R"
                    }
                ]

            let navigated = System.Collections.Generic.List<string>()
            let dispose = NavigationRequest.subscribe navigated.Add

            try
                HostRouteContract.deepLink localContract "bf-home" |> ignore
                HostRouteContract.deepLink localContract "bf-reports" |> ignore
                HostRouteContract.deepLink localContract "bf-home" |> ignore

                let mine = navigated |> Seq.filter (fun id -> id.StartsWith "bf/") |> List.ofSeq

                Expect.equal
                    mine
                    [ "bf/Home"; "bf/Reports"; "bf/Home" ]
                    "the shell router received the deep links in navigation order (back/forward consistent)"
            finally
                dispose ()
    ]

// ─── 4. Param round-trip binds through the Phase 264 read-side (toy) ───

let private toyRoundTripTests =
    testList "Phase 276 — param round-trip binds a stranger tree language" [
        testCase "a toy Bind node resolves against the restored deep-link params"
        <| fun _ ->
            let navigated = System.Collections.Generic.List<string>()
            let dispose = NavigationRequest.subscribe navigated.Add

            try
                match HostRouteContract.deepLink contract "reports/q3" with
                | Some sources ->
                    let tree = Element("p", [ Text "quarter: "; Bind "quarter" ])
                    let html = tree |> resolve sources |> lowerToHtml

                    Expect.stringContains
                        html
                        "quarter: q3"
                        "the toy resolved the restored param via the Phase 264 read-side"
                | None -> failtest "the deep link must resolve"
            finally
                dispose ()
    ]

// ─── 5. Append-only registration into PublicRenderingServerApp ────────

let private composeTests =
    testList "Phase 276 — append-only registration (GP 11)" [
        testCase "register appends one content source per route"
        <| fun _ ->
            let app0 = PublicRenderingServerApp.create ()
            Expect.isEmpty app0.ContentSources "a fresh PublicRenderingServerApp registers no content sources"

            let app1 = HostRouteRegistration.register [ ssrRegistration ] app0
            Expect.hasLength app1.ContentSources 1 "registering one hosted route appends exactly one content source"
    ]

// ─── 6. OSS grep-guard ────────────────────────────────────────────────

let private boundaryTests =
    testList "Phase 276 — OSS boundary" [
        testCase "the route-contract seam sources carry no banned OSS vocabulary (GP 1)"
        <| fun _ ->
            let seamFiles = [
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Core", "Shared", "HostRoutes.fs")
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client", "Client", "HostRouteContract.fs")
                Path.Combine(repoRoot (), "src", "ToolUp.PublicRendering", "Server", "HostRouteRegistration.fs")
            ]

            for path in seamFiles do
                Expect.isTrue (File.Exists path) $"expected seam file at {path}"
                let contents = (File.ReadAllText path).ToLowerInvariant()
                Expect.isFalse (contents.Contains "fuaran") $"{path} must carry no Fuaran token (GP 1)"
    ]

// These cases subscribe to `NavigationRequest.subscribe` and fire
// `NavigationRequest.request` — a *process-global* listener registry
// (see NavigationRequest.fs "sanctioned mutable global"). Under Expecto's
// default parallel runner, one case's `deepLink` fires every currently-
// subscribed callback, including a sibling case's `navigated.Add`, from a
// foreign thread while that sibling enumerates its own list — a
// `List<_>` concurrent-mutation crash ("Collection was modified…") whose
// victim changes run-to-run. The `SequencedGroup` name is SHARED with
// every other pack that touches the same global (ClientHostCapabilitiesContract,
// HostedTreeLayoutTests): tests in the group never run concurrently with
// each other, so no foreign thread ever fires into a live subscription.
// They still parallelise against the rest of the suite (nothing outside
// the group publishes navigation).
let tests =
    testSequencedGroup "ToolUp.Platform.NavigationRequest"
    <| testList "Phase 276 — hosted-tree navigation/route contract" [
        deepLinkTests
        ssrTests
        backForwardTests
        toyRoundTripTests
        composeTests
        boundaryTests
    ]