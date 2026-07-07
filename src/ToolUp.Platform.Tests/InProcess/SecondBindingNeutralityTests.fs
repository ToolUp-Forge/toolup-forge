module ToolUp.Platform.Tests.InProcess.SecondBindingNeutralityTests

open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.Tests.Contracts
open ToolUp.PublicRendering
open Toolup.Samples.ToyTreeBinding

// ─── Phase 202 — second in-tree reference tree-binding (neutrality) ───
//
// Wave 16 shipped the host-neutral seams (`ClientHostCapabilities` /
// `withElementView` — Phase 110; the server-rendered-fragment source —
// Phase 111; `ILiveSessionHost` / `ILiveChannel` — Phase 112;
// `IActionAuthorizer` / `ActionDescriptor` — Phase 113) and exactly ONE
// consumer of them. A neutrality claim with one consumer is asserted,
// not demonstrated. `samples/ToyTreeBinding/` is a SECOND, trivially-
// simple tree language; this pack proves each seam accepts it with no
// toy-specific assumption.
//
// Tier split (deliberate): the CLIENT seam (`withElementView`) is
// Fable-only, verified by the sample's `dotnet fable` pass and pinned
// here by a source-shape check that all four capabilities are routed.
// The SERVER seams (fragment source, live channel) and the Core
// authorizer are reachable by the .NET runner, so the toy's tier-neutral
// lowerings (`lowerToHtml` / `toAction`) are bound into them live.
//
// The grep-guard asserts the toy carries zero forge-banned-vocabulary
// token (GP 1 / open-core boundary): a renderer-neutral seam must accept
// a stranger to the substrate, and the toy IS that stranger.

let private run a = a |> Async.RunSynchronously

let private anonCtx: AccessContext =
    AccessContext.unrestricted (AnonymousSession "toy-session")

let private teamCtx (userId: string) (teamId: string) : AccessContext = {
    UserId = userId
    TeamId = Some teamId
    Subject = TeamMember(userId, teamId)
    ModulePermissions = Map.empty
    ModuleExposure = Map.empty
    PlatformRole = None
}

/// A small, representative toy tree reused across the seam bindings.
let private toyTree: ToyNode.ToyNode =
    ToyNode.Element(
        "section",
        [
            ToyNode.Element("p", [ ToyNode.Text "toy fragment" ])
            ToyNode.OnClick(ToyNode.DispatchBump, ToyNode.Element("button", [ ToyNode.Text "bump" ]))
        ]
    )

// ─── Toy source location (grep-guard + binding-shape pins) ────────────

let private repoRoot () =
    let asmLoc = System.Reflection.Assembly.GetExecutingAssembly().Location
    let asmDir = Path.GetDirectoryName asmLoc
    // bin/Debug/net10.0 → ToolUp.Platform.Tests → src → toolup-forge
    Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."))

let private toyDir () =
    Path.Combine(repoRoot (), "samples", "ToyTreeBinding")

let private toySourceFiles () =
    let dir = toyDir ()

    if Directory.Exists dir then
        Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories) |> List.ofSeq
    else
        []

// ─── 1. Phase 111 — server-rendered-fragment source ───────────────────

let private fragmentTests =
    testList "Phase 111 fragment source accepts the toy" [

        testCaseAsync "a ToyNode-derived fragment resolves through ContentSource.ofResolved"
        <| async {
            let html = ToyNode.lowerToHtml toyTree

            // The toy binds the seam as any external tree language would:
            // it lowers its own tree to an HTML body and hands it back.
            let source =
                ContentSource.ofResolved (fun _slug _ctx -> async {
                    return Some(ResolvedContent.ofBody (ContentBody.Html html))
                })

            match! source.Resolve (Slug "toy") anonCtx with
            | Some(ContentBody.Html fragment) ->
                Expect.equal fragment html "the seam returns the toy's own lowering verbatim"
                Expect.stringContains fragment "toy fragment" "the toy text survived the round-trip"
            | other -> failtestf "expected the toy HTML fragment; got %A" other
        }

        testCase "the toy source also satisfies IResolvedContentSource (Phase 111 richer channel)"
        <| fun _ ->
            let html = ToyNode.lowerToHtml toyTree

            let source =
                ContentSource.ofResolved (fun _slug _ctx -> async {
                    return Some(ResolvedContent.ofBody (ContentBody.Html html))
                })

            match source with
            | :? IResolvedContentSource as resolved ->
                match resolved.ResolveContent (Slug "toy") anonCtx |> run with
                | Some rc -> Expect.equal rc.Body (ContentBody.Html html) "resolved-content body is the toy lowering"
                | None -> failtest "the toy resolved-content source must claim its slug"
            | _ -> failtest "ofResolved must expose IResolvedContentSource — no toy-specific change required"
    ]

// ─── 2. Phase 112 — scope-isolated live channel ───────────────────────

let private liveChannelTests =
    testList "Phase 112 live channel carries a toy frame, scope-isolated" [

        testCase "a ToyNode-derived frame is delivered to a subscriber in the same scope"
        <| fun _ ->
            let host = LiveSessionHost.createInMemory None

            let scopeA: StorageScope = {
                ScopeId = "team-a"
                Container = "team-a"
                Persist = false
            }

            let descriptor =
                match host.OpenSession scopeA |> run with
                | Ok d -> d
                | Error r -> failtestf "open failed: %A" r

            let received = System.Collections.Generic.List<string>()

            host.Subscribe(scopeA.ScopeId, descriptor.SessionId, received.Add)
            |> run
            |> ignore

            let frame = ToyNode.lowerToHtml toyTree

            let channel =
                (host.TryGetChannel(scopeA.ScopeId, descriptor.SessionId) |> run).Value

            channel.PushFrame frame |> run

            Expect.contains received frame "the toy frame is delivered verbatim — the channel is renderer-neutral"

        testCase "the toy session is structurally unreachable from another scope (GP 4)"
        <| fun _ ->
            let host = LiveSessionHost.createInMemory None

            let scopeA: StorageScope = {
                ScopeId = "team-a"
                Container = "team-a"
                Persist = false
            }

            let descriptor =
                match host.OpenSession scopeA |> run with
                | Ok d -> d
                | Error r -> failtestf "open failed: %A" r

            match host.TryGetChannel("team-b", descriptor.SessionId) |> run with
            | None -> () // cross-scope resolution fails structurally
            | Some _ -> failtest "a toy frame must never reach a subscriber in a different scope"
    ]

// ─── 3. Phase 113 — host-neutral action authorizer ────────────────────

let private allToyEvents = [
    ToyNode.NavigateTo "Home"
    ToyNode.NotifyWith(ToyNode.Info, "hello")
    ToyNode.DispatchBump
    ToyNode.CallEcho "ping"
]

let private mkStore () : IPermissionStore =
    PermissionStore(InMemoryBlobStorage.InMemoryBlobStorage()) :> IPermissionStore

let private allowAllPolicy = {
    Rules = [
        {
            Kind = "*"
            Target = "*"
            Requirement = ActionRequirement.Unrestricted
        }
    ]
}

let private authorizerTests =
    testList "Phase 113 authorizer gates toy events, default-deny intact" [

        testCaseAsync "every toy event is denied when no authorizer is wired (default-deny)"
        <| async {
            for event in allToyEvents do
                let action = ToyNode.toAction (Some "team-a") event

                match! ActionAuthorizer.denyAll.Authorize action (teamCtx "alice" "team-a") with
                | AuthorizationDecision.Deny _ -> ()
                | AuthorizationDecision.Allow ->
                    failtestf "absent-seam fallback must deny the toy %s action" (ToyNode.eventLabel event)
        }

        testCaseAsync "the shipped authorizer's empty policy denies every toy event (default-deny floor)"
        <| async {
            let auth =
                PermissionStoreActionAuthorizer.create ActionPolicy.empty (mkStore ()) None

            for event in allToyEvents do
                let action = ToyNode.toAction (Some "team-a") event

                match! auth.Authorize action (teamCtx "alice" "team-a") with
                | AuthorizationDecision.Deny _ -> ()
                | AuthorizationDecision.Allow ->
                    failtestf "empty policy must deny the toy %s action" (ToyNode.eventLabel event)
        }

        testCaseAsync "a permissive policy allows the toy descriptors (they are well-formed for the seam)"
        <| async {
            let auth = PermissionStoreActionAuthorizer.create allowAllPolicy (mkStore ()) None

            for event in allToyEvents do
                let action = ToyNode.toAction (Some "team-a") event

                match! auth.Authorize action (teamCtx "alice" "team-a") with
                | AuthorizationDecision.Allow -> ()
                | AuthorizationDecision.Deny reason ->
                    failtestf "toy %s action should pass an allow-all policy; got: %s" (ToyNode.eventLabel event) reason
        }

        testCaseAsync "a cross-scope toy action is denied even under allow-all (structural GP 4)"
        <| async {
            let auth = PermissionStoreActionAuthorizer.create allowAllPolicy (mkStore ()) None
            // The toy pins the action to a scope the principal does not own.
            let action = ToyNode.toAction (Some "team-b") ToyNode.DispatchBump

            match! auth.Authorize action (teamCtx "alice" "team-a") with
            | AuthorizationDecision.Deny reason -> Expect.stringContains reason "scope" "the cross-scope gate fired"
            | AuthorizationDecision.Allow -> failtest "a cross-scope toy action must deny even under allow-all"
        }
    ]

// ─── 4. Open-core boundary + client-binding shape ─────────────────────

// Forge-banned vocabulary — a forge-public sample must reference no
// private project / product / strategy artefact. The token list itself
// is private (publishing it here would leak exactly what the guard
// hides), so it is loaded at run time via `NeutralityTokens` — local
// file / env var, skip-loudly when absent, hardcoded public-safe canary
// always enforced. The renderer-neutral seam must accept a stranger to
// the substrate, so the toy is held to that bar.

let private boundaryTests =
    testList "Open-core boundary + client-binding shape" [

        testCase "the toy sample carries zero forge-banned-vocabulary token"
        <| fun _ ->
            let files = toySourceFiles ()
            Expect.isNonEmpty files "the toy source files must be locatable from the test assembly"

            for path in files do
                NeutralityTokens.assertNoBannedTokens (Path.GetFileName path) (File.ReadAllText path)

            NeutralityTokens.skipUnlessExternalSource ()

        testCase "the client binding routes all four ClientHostCapabilities through the toy"
        <| fun _ ->
            let bindingPath = Path.Combine(toyDir (), "Binding.fs")
            Expect.isTrue (File.Exists bindingPath) "Binding.fs must exist"
            let text = File.ReadAllText bindingPath

            Expect.stringContains
                text
                "ClientHostView.withElementView"
                "binds via the additive withElementView overload"

            Expect.stringContains text "host.Navigate" "routes the Navigate capability"
            Expect.stringContains text "host.Notify" "routes the Notify capability"
            Expect.stringContains text "host.Dispatch" "routes the Dispatch capability"
            Expect.stringContains text "host.Call" "routes the Call capability"
    ]

let tests =
    testList "SecondBindingNeutrality (Phase 202)" [ fragmentTests; liveChannelTests; authorizerTests; boundaryTests ]