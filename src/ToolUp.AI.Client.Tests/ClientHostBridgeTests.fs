module ToolUp.AI.Client.Tests.ClientHostBridgeTests

open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.Platform

// ─── Phase 110 — ClientHostCapabilities routing tests ─────────────────
//
// The bridge under test lives in `ToolUp.Platform.Client`
// (`Client/ClientHostBridge.fs`); the tests live in this harness
// because it is the established Fable-tier client test rig (the
// ProjectReference drives Platform.Client through the Fable compiler
// transitively — see this fsproj's header note). Each test proves a
// capability routes to its shipped concrete:
//
//   - `Navigate`  → `NavigationRequest` pub/sub (synchronous fan-out)
//   - `Dispatch`  → the dispatch the bag was built from
//   - `Call`      → OfRemoting effect semantics: success / error map
//                   into the module's Msg and arrive via dispatch
//                   (one timer hop later — `AsyncHelpers.start`)
//
// `Notify` routes through `NotificationClient.publishLocal`, whose
// module initialisation is browser-coupled (per-tab `EventSource`
// plumbing); its routing is exercised by the MinimalClient Fable
// verification + the shipped ToastCentre subscription rather than
// under Node. The `ToastIntent` constructors it consumes are covered
// here.

type private Msg =
    | Got of int
    | Failed of string

let tests =
    testList "ClientHostBridge (Phase 110)" [

        testCase "Navigate routes through NavigationRequest to shell subscribers"
        <| fun () ->
            let received = ResizeArray<string>()
            let unsubscribe = NavigationRequest.subscribe received.Add

            try
                let caps = ClientHostCapabilities.create (fun (_: Msg) -> ())
                caps.Navigate "SalesAnalysis/dataset"

                Expect.equal (List.ofSeq received) [ "SalesAnalysis/dataset" ] "the sidebar id reaches the subscriber"
            finally
                unsubscribe ()

        testCase "Dispatch routes to the module dispatch the bag was built from"
        <| fun () ->
            let received = ResizeArray<Msg>()
            let caps = ClientHostCapabilities.create received.Add

            caps.Dispatch(Got 7)
            caps.Dispatch(Failed "x")

            Expect.equal (List.ofSeq received) [ Got 7; Failed "x" ] "messages arrive in dispatch order"

        testCase "ToastIntent constructors carry the right level"
        <| fun () ->
            Expect.equal (ToastIntent.info "i").Level SystemMessageLevel.Info "info"
            Expect.equal (ToastIntent.warning "w").Level SystemMessageLevel.Warning "warning"
            Expect.equal (ToastIntent.error "e").Level SystemMessageLevel.Error "error"
            Expect.equal (ToastIntent.error "e").Text "e" "text carried"

        testCaseDeferred "Call dispatches onSuccess with the async result" 25
        <| fun () ->
            let received = ResizeArray<Msg>()
            let caps = ClientHostCapabilities.create received.Add

            caps.Call(async { return 42 }, Got, (fun e -> Failed e.Message))

            fun () -> Expect.equal (List.ofSeq received) [ Got 42 ] "success mapped into the module Msg"

        testCaseDeferred "Call dispatches onError when the async raises" 25
        <| fun () ->
            let received = ResizeArray<Msg>()
            let caps = ClientHostCapabilities.create received.Add

            caps.Call(
                (async {
                    failwith "boom"
                    return 0
                }),
                Got,
                (fun e -> Failed e.Message)
            )

            fun () -> Expect.equal (List.ofSeq received) [ Failed "boom" ] "error mapped into the module Msg"
    ]