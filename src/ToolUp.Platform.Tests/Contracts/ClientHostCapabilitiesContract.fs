// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.ClientHostCapabilitiesContract

open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Elmish
open Toolup.Samples.ToyTreeBinding

// ─── Phase 265 — ClientHostCapabilities conformance contract pack ─────
//
// The reusable conformance bar any `ClientHostCapabilities<'Msg>`
// implementation validates against — the shipped in-tree default, the
// Phase 202 second tree-binding, or a future distributed / alternate
// host. Phase 110 shipped the four-capability host-bridge seam
// (`Navigate` / `Notify` / `Dispatch` / `Call`) but only ad-hoc
// routing checks; Phase 202 proved neutrality ONCE with a sample, not
// against a bindable bar. This pack extracts that bar so neutrality is
// asserted by CONTRACT, not by a one-off consumer (GP 12).
//
// Tier split (deliberate — the same one Phase 202 documents):
//   - `Navigate` routes through the REAL shell/sidebar router
//     (`NavigationRequest.request`), observed through the REAL
//     subscription, so the bar exercises the genuine hook.
//   - `Call` rides the REAL `Cmd.OfRemoting` effect (interceptor chain,
//     error envelope), executed against the host's dispatch — the
//     substantive "Cmd.OfRemoting semantics" claim, asserted live.
//     Its `Async.Start` shape means the success / error message lands
//     on a background turn, so the pack waits with a bounded poll.
//   - `Dispatch` forwards straight into the module's Elmish loop —
//     observed directly.
//   - `Notify`'s shipped routing (`NotificationClient.publishLocal`
//     under the current identity) is Fable-only: `NotificationClient`'s
//     EventSource interop and `UserSession.getUserId` throw under the
//     .NET runner. The conformance bar therefore observes the toast
//     vocabulary through a sink, and the genuine routing of the shipped
//     `create` (the `SystemMessage(level, text)` mapping + the
//     `publishLocal` hop) is pinned by a source-shape check below.
//
// The conformance bar (`contract`) is renderer-neutral: it touches only
// the fixture's bag + its observation hooks, never a substrate-specific
// sink, so a third tree language binds it by supplying its own fixture.

// ─── The reusable fixture ─────────────────────────────────────────────

/// What a `ClientHostCapabilities` implementation supplies to the
/// conformance bar: the bag under test, an observation hook per
/// capability, and the sample inputs the pack drives through it. The
/// pack owns the success value and the thrown exception so a fixture's
/// `OnCallSuccess` / `OnCallError` need not be constant — the expected
/// message is computed from the same inputs the pack feeds the bag.
type ClientHostCapabilitiesContractFixture<'Msg when 'Msg: equality> = {
    /// Human label, suffixed onto the test-list name.
    Name: string
    /// The implementation under test.
    Capabilities: ClientHostCapabilities<'Msg>
    /// A sidebar id the pack routes through `Navigate`; read back via
    /// `Navigations`. Distinct per fixture so a shared global router hook
    /// can be asserted by membership without cross-fixture interference.
    NavigateTarget: NavigationRequest.SidebarId
    /// Every sidebar id `Navigate` has routed so far.
    Navigations: unit -> NavigationRequest.SidebarId list
    /// Every toast intent `Notify` has routed so far.
    Notifications: unit -> ToastIntent list
    /// Every message `Dispatch` (and `Call`'s success / error) has
    /// routed into the module loop so far.
    Dispatched: unit -> 'Msg list
    /// A message the pack dispatches via `Dispatch`.
    SampleMsg: 'Msg
    /// The value the pack's `Call` async resolves to on the success arm.
    CallSuccessValue: string
    /// How the fixture maps a successful `Call` result to a message.
    OnCallSuccess: string -> 'Msg
    /// How the fixture maps a thrown `Call` to a message.
    OnCallError: exn -> 'Msg
}

// ─── Bounded wait for an async (`Call`) dispatch to land ──────────────

let private waitUntil (timeoutMs: int) (predicate: unit -> bool) : bool =
    let sw = System.Diagnostics.Stopwatch.StartNew()

    while not (predicate ()) && sw.ElapsedMilliseconds < int64 timeoutMs do
        System.Threading.Thread.Sleep 10

    predicate ()

// ─── The conformance bar (IClientHostCapabilitiesContract) ────────────

/// The reusable suite. Bind it against any `ClientHostCapabilities`
/// implementation by supplying a fixture. Asserts each of the four
/// capabilities routes to its hook and that the bag carries every
/// `ToastIntent` level — with no tree-language-specific assumption.
let contract (fixture: ClientHostCapabilitiesContractFixture<'Msg>) : Test =
    testList $"IClientHostCapabilities contract — {fixture.Name}" [

        testCase "Navigate routes the target to the shell / sidebar router"
        <| fun _ ->
            fixture.Capabilities.Navigate fixture.NavigateTarget

            Expect.contains
                (fixture.Navigations())
                fixture.NavigateTarget
                "the navigated-to sidebar id reached the router hook"

        testCase "Notify routes each ToastIntent level to the notification hook"
        <| fun _ ->
            let intents = [
                ToastIntent.info "host-info"
                ToastIntent.warning "host-warning"
                ToastIntent.error "host-error"
            ]

            for intent in intents do
                fixture.Capabilities.Notify intent

            let observed = fixture.Notifications()

            for intent in intents do
                Expect.contains observed intent $"the %A{intent.Level} toast reached the notification hook"

        testCase "Dispatch forwards the message into the module's Elmish loop"
        <| fun _ ->
            fixture.Capabilities.Dispatch fixture.SampleMsg

            Expect.contains (fixture.Dispatched()) fixture.SampleMsg "the dispatched message reached the module loop"

        testCase "Call dispatches the success message (Cmd.OfRemoting semantics)"
        <| fun _ ->
            let expected = fixture.OnCallSuccess fixture.CallSuccessValue
            let value = fixture.CallSuccessValue

            fixture.Capabilities.Call(async { return value }, fixture.OnCallSuccess, fixture.OnCallError)

            let landed =
                waitUntil 2000 (fun () -> fixture.Dispatched() |> List.contains expected)

            Expect.isTrue landed "Call's success outcome was mapped to a Msg and dispatched"

        testCase "Call dispatches the error message when the call throws"
        <| fun _ ->
            // The pack owns the exception instance, so the expected
            // message is `OnCallError` of the very exception the call
            // raises — a fixture whose error mapping reads `ex.Message`
            // still produces an equal message.
            let boom = exn "contract-call-boom"
            let expected = fixture.OnCallError boom

            fixture.Capabilities.Call(async { return (raise boom: string) }, fixture.OnCallSuccess, fixture.OnCallError)

            let landed =
                waitUntil 2000 (fun () -> fixture.Dispatched() |> List.contains expected)

            Expect.isTrue landed "Call's thrown outcome was mapped to a Msg and dispatched"
    ]

// ─── In-tree witness construction ─────────────────────────────────────
//
// Both witnesses route the three .NET-reachable capabilities through the
// GENUINE shipped seams (`NavigationRequest`, the host dispatch, and
// `Cmd.OfRemoting`) and observe the toast vocabulary through a sink. A
// fresh bag per witness keeps the dispatch observations isolated; the
// navigation hook is a per-tab global, so each witness uses a distinct
// `NavigateTarget` and the bar asserts by membership.

type private Bag<'Msg> = {
    Caps: ClientHostCapabilities<'Msg>
    Navs: ResizeArray<NavigationRequest.SidebarId>
    Notifs: ResizeArray<ToastIntent>
    Dispatched: System.Collections.Concurrent.ConcurrentQueue<'Msg>
}

let private makeBag () : Bag<'Msg> =
    let navs = ResizeArray<NavigationRequest.SidebarId>()
    let notifs = ResizeArray<ToastIntent>()
    let dispatched = System.Collections.Concurrent.ConcurrentQueue<'Msg>()

    let caps =
        { new ClientHostCapabilities<'Msg> with
            member _.Navigate sidebarId = NavigationRequest.request sidebarId
            member _.Notify intent = notifs.Add intent
            member _.Dispatch msg = dispatched.Enqueue msg

            member _.Call(call, onSuccess, onError) =
                // The exact shape the shipped `create` uses: ride the
                // OfRemoting effect and run it immediately against the
                // host's dispatch.
                Cmd.OfRemoting.call (fun () -> call) () onSuccess onError
                |> List.iter (fun effect -> effect dispatched.Enqueue)
        }

    // Observe the REAL navigation router hook (the shell's own
    // subscription point). Never disposed — the per-tab router lives for
    // the page lifetime, and the test process is short-lived.
    NavigationRequest.subscribe navs.Add |> ignore

    {
        Caps = caps
        Navs = navs
        Notifs = notifs
        Dispatched = dispatched
    }

// Witness 1 — the in-tree default routing behaviour.

type private DefaultMsg =
    | Bumped
    | Echoed of string
    | CallFailed of string

let private defaultFixture () : ClientHostCapabilitiesContractFixture<DefaultMsg> =
    let bag: Bag<DefaultMsg> = makeBag ()

    {
        Name = "in-tree default (NavigationRequest + Cmd.OfRemoting)"
        Capabilities = bag.Caps
        NavigateTarget = "ClientHostDefault/Home"
        Navigations = fun () -> List.ofSeq bag.Navs
        Notifications = fun () -> List.ofSeq bag.Notifs
        Dispatched = fun () -> List.ofSeq bag.Dispatched
        SampleMsg = Bumped
        CallSuccessValue = "echo-payload"
        OnCallSuccess = Echoed
        OnCallError = fun ex -> CallFailed ex.Message
    }

// Witness 2 — the Phase 202 `ToyNode` second tree-binding, the first
// non-substrate conformance witness: a different tree language's `Msg`
// vocabulary against the same bar.

let private toyFixture () : ClientHostCapabilitiesContractFixture<Binding.Msg> =
    let bag: Bag<Binding.Msg> = makeBag ()

    {
        Name = "ToyNode second binding (Phase 202)"
        Capabilities = bag.Caps
        NavigateTarget = "ToyTreeBinding/Home"
        Navigations = fun () -> List.ofSeq bag.Navs
        Notifications = fun () -> List.ofSeq bag.Notifs
        Dispatched = fun () -> List.ofSeq bag.Dispatched
        SampleMsg = Binding.Bumped
        CallSuccessValue = "ping"
        OnCallSuccess = Binding.Echoed
        OnCallError = fun ex -> Binding.EchoFailed ex.Message
    }

// ─── The toy event vocabulary routes through the bag ──────────────────
//
// Mirrors the (private) `Binding.route` adapter: each `ToyEvent` case
// routes through exactly one capability. Driving the toy's own public
// event vocabulary through the bar proves a second tree language binds
// every hook with no toy-specific seam change. The same events also map
// to `ActionDescriptor`s through the toy's public `toAction`, so the
// neutrality claim spans the action-gating seam too.

let private routeToyEvent (host: ClientHostCapabilities<Binding.Msg>) (event: ToyNode.ToyEvent) : unit =
    match event with
    | ToyNode.NavigateTo sidebarId -> host.Navigate sidebarId
    | ToyNode.NotifyWith(level, text) ->
        let intent =
            match level with
            | ToyNode.Info -> ToastIntent.info text
            | ToyNode.Warning -> ToastIntent.warning text
            | ToyNode.Error -> ToastIntent.error text

        host.Notify intent
    | ToyNode.DispatchBump -> host.Dispatch Binding.Bumped
    | ToyNode.CallEcho input ->
        host.Call(async { return input }, Binding.Echoed, (fun ex -> Binding.EchoFailed ex.Message))

let private allToyEvents = [
    ToyNode.NavigateTo "ToyTreeBinding/Reports"
    ToyNode.NotifyWith(ToyNode.Warning, "toy says hi")
    ToyNode.DispatchBump
    ToyNode.CallEcho "echo-me"
]

let private toyVocabularyTests =
    testList "ToyNode event vocabulary binds every capability" [

        testCase "each toy event routes to exactly one host capability"
        <| fun _ ->
            let bag: Bag<Binding.Msg> = makeBag ()

            for event in allToyEvents do
                routeToyEvent bag.Caps event

            let dispatchLanded =
                waitUntil 2000 (fun () ->
                    let d = List.ofSeq bag.Dispatched
                    List.contains Binding.Bumped d && List.contains (Binding.Echoed "echo-me") d)

            Expect.contains (List.ofSeq bag.Navs) "ToyTreeBinding/Reports" "NavigateTo reached the router"

            Expect.contains
                (List.ofSeq bag.Notifs)
                (ToastIntent.warning "toy says hi")
                "NotifyWith reached the notifier"

            Expect.isTrue dispatchLanded "DispatchBump + CallEcho reached the module loop"

        testCase "the toy events also map to host-neutral ActionDescriptors"
        <| fun _ ->
            // Each toy event carries a well-formed descriptor the action
            // authorizer can gate — the same vocabulary, the gating seam.
            let kinds =
                allToyEvents |> List.map (fun e -> (ToyNode.toAction (Some "team-a") e).Kind)

            Expect.equal kinds [ "navigate"; "notify"; "dispatch"; "call" ] "one descriptor Kind per capability"
    ]

// ─── Boundary: open-core grep-guard + shipped-seam source-shape pins ──

let private repoRoot () =
    let asmLoc = System.Reflection.Assembly.GetExecutingAssembly().Location
    let asmDir = Path.GetDirectoryName asmLoc
    // bin/Debug/net10.0 → ToolUp.Platform.Tests → src → toolup-forge
    Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."))

let private contractSourcePath () =
    Path.Combine(repoRoot (), "src", "ToolUp.Platform.Tests", "Contracts", "ClientHostCapabilitiesContract.fs")

let private bridgeSourcePath () =
    Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client", "Client", "ClientHostBridge.fs")

/// Forge-banned vocabulary (a representative high-signal subset of the
/// ToolUp-estate list). Built from fragments so the guard's own source —
/// which this very test reads — carries none of the banned literals
/// verbatim and cannot self-trip.
let private bannedTokens = [
    "Fua" + "ran"
    "Diametr" + "ical"
    "Con" + "cord"
    "Xcel" + "sys"
    "TaxTime" + "Machine"
    "Knowledge" + "Mart"
    "Marketplace" + "-app"
    "cook" + "book"
    "CHEF" + "-GUIDE"
    "Vision " + "Plan"
    "Refine " + "Roadmap"
]

let private boundaryTests =
    testList "Open-core boundary + shipped-seam fidelity" [

        testCase "the contract source carries zero forge-banned-vocabulary token"
        <| fun _ ->
            let path = contractSourcePath ()
            Expect.isTrue (File.Exists path) "the contract source must be locatable from the test assembly"
            let text = File.ReadAllText path

            for token in bannedTokens do
                Expect.isFalse
                    (text.Contains token)
                    $"the contract source must not reference the banned token '{token}' (GP 1 / open-core)"

        testCase "the real NavigationRequest router delivers a request to a subscriber"
        <| fun _ ->
            // Pins the genuine seam the witnesses route `Navigate` through:
            // a request fires every subscriber in registration order.
            let received = ResizeArray<NavigationRequest.SidebarId>()
            let dispose = NavigationRequest.subscribe received.Add

            NavigationRequest.request "BoundaryProbe/Home"
            dispose ()

            Expect.contains received "BoundaryProbe/Home" "the shell-router hook fired the subscriber"

        testCase "shipped create routes each capability through its concrete (source-shape)"
        <| fun _ ->
            // The Fable-only `Notify` path can't run under the .NET
            // runner, so its routing is pinned here against the seam
            // source alongside the .NET-reachable three.
            let path = bridgeSourcePath ()
            Expect.isTrue (File.Exists path) "ClientHostBridge.fs must be locatable"
            let text = File.ReadAllText path

            Expect.stringContains text "NavigationRequest.request" "Navigate routes through the shell router"
            Expect.stringContains text "NotificationClient.publishLocal" "Notify routes through the notification stream"

            Expect.stringContains
                text
                "Notification.SystemMessage"
                "Notify maps the intent level + text to a SystemMessage"

            Expect.stringContains text "Cmd.OfRemoting.call" "Call rides the OfRemoting effect"

        testCase "the capability bag round-trips from a ClientModule view via withElementView (source-shape)"
        <| fun _ ->
            let path = bridgeSourcePath ()
            let text = File.ReadAllText path

            Expect.stringContains text "withElementView" "the additive ClientModule view builder exists"

            Expect.stringContains
                text
                "ClientHostCapabilities.create dispatch"
                "withElementView constructs the bag from the view's own dispatch"
    ]

// ─── Aggregate ────────────────────────────────────────────────────────

let tests =
    testList "ClientHostCapabilitiesContract (Phase 265)" [
        contract (defaultFixture ())
        contract (toyFixture ())
        toyVocabularyTests
        boundaryTests
    ]