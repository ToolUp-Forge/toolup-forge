module ToolUp.Platform.Tests.InProcess.HostStateProjectionTests

open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.PublicRendering
open Toolup.Samples.ToyTreeBinding.ToyNode

// ─── Phase 264 — host-state binding-source projection seam ─────────────
//
// The read-side complement to the Phase 110 action seam. Five proofs:
//   1. A client projection round-trips an Elmish model into the neutral
//      `HostBindingSources` namespace (CSR path).
//   2. An SSR projection is scope-isolated by construction — a projection
//      built for scope A provably excludes scope B's sessions (GP 4).
//   3. `tryResolve` agrees on one precedence rule (QueryResults shadows
//      State) so both tier projections resolve identically.
//   4. The toy tree (a SECOND, stranger tree language) resolves the SAME
//      `Bind` node against BOTH a CSR-projected and an SSR-projected
//      namespace — the renderer-neutral read-side, mirroring how `route`
//      proves the action side (acceptance: CSR + SSR binding resolution).
//   5. GP 13 — `HostBindingSources.empty` is a single interned value; a
//      pipeline that never projects allocates nothing per access. Plus a
//      grep-guard that the seam source carries no banned OSS vocabulary.

let private run a = a |> Async.RunSynchronously

let private scope (id: string) : StorageScope = {
    ScopeId = id
    Container = $"test-%s{id}"
    Persist = false
}

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
    // bin/Debug/net10.0/ToolUp.Platform.Tests.dll → repo root
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

// ─── 1. Client projection round-trip ─────────────────────────────────

type private SampleModel = { Count: int; Label: string }

let private clientProjection: IHostStateProjection<SampleModel> =
    IHostStateProjection.ofFunc (fun m -> {
        QueryResults = Map.ofList [ "count", box m.Count; "label", box m.Label ]
        State = Map.ofList [ "expanded", box true ]
    })

let private clientTests =
    testList "Phase 264 — client projection" [
        testCase "round-trips a model into QueryResults + State"
        <| fun _ ->
            let sources = clientProjection.Project { Count = 7; Label = "hi" }

            Expect.equal
                (HostBindingSources.tryResolve "count" sources)
                (Some(box 7))
                "count projected into QueryResults"

            Expect.equal
                (HostBindingSources.tryResolve "label" sources)
                (Some(box "hi"))
                "label projected into QueryResults"

            Expect.equal
                (HostBindingSources.tryResolve "expanded" sources)
                (Some(box true))
                "UI state projected into State"

            Expect.equal (HostBindingSources.tryResolve "absent" sources) None "an unprojected key resolves to None"

        testCase "QueryResults shadows State on a key clash"
        <| fun _ ->
            let sources = {
                QueryResults = Map.ofList [ "k", box "from-query" ]
                State = Map.ofList [ "k", box "from-state" ]
            }

            Expect.equal
                (HostBindingSources.tryResolve "k" sources)
                (Some(box "from-query"))
                "host domain values shadow UI state — the single shared resolution rule"
    ]

// ─── 2. SSR projection — scope isolation by construction ──────────────

let private ssrScopeIsolationTests =
    testList "Phase 264 — SSR projection scope isolation (GP 4)" [
        testCase "a projection built for scope A excludes scope B's sessions"
        <| fun _ ->
            let host = LiveSessionHost.createInMemory None
            // Two sessions in scope A, one in scope B.
            host.OpenSession(scope "a") |> run |> ignore
            host.OpenSession(scope "a") |> run |> ignore
            host.OpenSession(scope "b") |> run |> ignore

            let projA = ServerHostStateProjection.forScopeDefault host (scope "a")
            let projB = ServerHostStateProjection.forScopeDefault host (scope "b")

            let sourcesA = projA.Project() |> run
            let sourcesB = projB.Project() |> run

            Expect.equal
                (HostBindingSources.tryResolve "openSessionCount" sourcesA)
                (Some(box 2))
                "scope A's projection sees exactly its own two sessions"

            Expect.equal
                (HostBindingSources.tryResolve "openSessionCount" sourcesB)
                (Some(box 1))
                "scope B's projection sees exactly its own one session"

            Expect.equal
                (HostBindingSources.tryResolve "scopeId" sourcesA)
                (Some(box "a"))
                "scope A's projection is tagged with scope A"

            // The structural isolation claim: every descriptor scope A's
            // projection surfaces belongs to scope A — none leak from B.
            match HostBindingSources.tryResolve "openSessions" sourcesA with
            | Some boxed ->
                let sessions = boxed :?> LiveSessionDescriptor list
                Expect.all sessions (fun d -> d.ScopeId = "a") "scope A's projection contains only scope-A descriptors"
            | None -> failtest "expected openSessions to be projected"
    ]

// ─── 3. Toy read-side — one tree, both projection paths ───────────────
//
// The acceptance proof: the toy (a stranger tree language) resolves the
// SAME `Bind` node against a CSR-projected namespace and an SSR-projected
// namespace. `resolve` + `lowerToHtml` are tier-neutral, so the .NET
// runner exercises both paths with one tree.

let private toyReadSideTests =
    testList "Phase 264 — toy read-side resolves on both paths" [
        testCase "CSR path: a Bind node resolves against a model projection"
        <| fun _ ->
            let sources = clientProjection.Project { Count = 42; Label = "x" }
            let tree = Element("p", [ Text "count: "; Bind "count" ])
            let html = tree |> resolve sources |> lowerToHtml

            Expect.stringContains html "count: 42" "the toy resolved the binding against the CSR projection"
            Expect.isFalse (html.Contains "{unbound") "no unbound marker — the key resolved"

        testCase "SSR path: the same Bind shape resolves against a session projection"
        <| fun _ ->
            let host = LiveSessionHost.createInMemory None
            host.OpenSession(scope "team-1") |> run |> ignore
            let proj = ServerHostStateProjection.forScopeDefault host (scope "team-1")
            let sources = proj.Project() |> run

            let tree = Element("p", [ Text "sessions: "; Bind "openSessionCount" ])
            let html = tree |> resolve sources |> lowerToHtml

            Expect.stringContains html "sessions: 1" "the toy resolved the binding against the SSR projection"

        testCase "an unbound key degrades to a stable marker, not a throw"
        <| fun _ ->
            let html = Bind "missing" |> resolve HostBindingSources.empty |> lowerToHtml
            Expect.stringContains html "{unbound:missing}" "an unprojected key renders the miss marker"
    ]

// ─── 4. GP 13 — zero-cost when not projected ──────────────────────────

let private zeroCostTests =
    testList "Phase 264 — GP 13 zero-cost + OSS boundary" [
        testCase "HostBindingSources.empty is a single interned value"
        <| fun _ ->
            // Module-level `let empty` is evaluated once: every access
            // returns the same reference, so resolving against `empty`
            // allocates nothing per access.
            Expect.isTrue
                (System.Object.ReferenceEquals(HostBindingSources.empty, HostBindingSources.empty))
                "empty returns the same object reference every access (no per-access allocation)"

            Expect.isTrue (Map.isEmpty HostBindingSources.empty.QueryResults) "empty QueryResults"
            Expect.isTrue (Map.isEmpty HostBindingSources.empty.State) "empty State"

        testCase "the seam source carries no banned OSS vocabulary"
        <| fun _ ->
            // The seam is forge-public; it must name no private layer.
            let seamFiles = [
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Core", "Shared", "HostBindingSources.fs")
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client", "Client", "HostStateProjection.fs")
                Path.Combine(repoRoot (), "src", "ToolUp.PublicRendering", "Server", "HostStateProjection.fs")
            ]

            for path in seamFiles do
                Expect.isTrue (File.Exists path) (sprintf "expected seam file at %s" path)
                NeutralityTokens.assertNoBannedTokens path (File.ReadAllText path)

            NeutralityTokens.skipUnlessExternalSource ()
    ]

[<Tests>]
let tests =
    testList "Phase 264 — host-state binding-source projection seam" [
        clientTests
        ssrScopeIsolationTests
        toyReadSideTests
        zeroCostTests
    ]