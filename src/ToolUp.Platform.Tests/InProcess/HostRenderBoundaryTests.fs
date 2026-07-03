module ToolUp.Platform.Tests.InProcess.HostRenderBoundaryTests

open Expecto
open ToolUp.Platform
open ToolUp.PublicRendering
open ToolUp.Platform.Testing

// ─── Phase 273 — SSR hosted-tree error-boundary tests ─────────────────
//
// The boundary is the server-side peer of a client React error boundary: a
// thrown node yields a structured fallback fragment (not a 500), the fault
// reports through the Phase 268 sink, and the surrounding page completes.
// This pack pins:
//   * a throwing node yields the structured fallback fragment, never an
//     exception;
//   * the trip reports a `RenderFault` (node id + exception message) to the
//     Phase 268 sink;
//   * a healthy tree renders byte-identically and captures nothing (GP 11);
//   * a page assembled from several fragments survives one throwing subtree
//     (the healthy fragments render, exactly the bad one degrades);
//   * the default fallback hydrates parity-clean vs a matching CSR mount
//     (Phase 203 `HydrationParity`);
//   * `guardWith` honours a consumer-supplied fallback fragment.

/// A capturing sink — records every fault the boundary reports.
type private CapturingSink() =
    let faults = ResizeArray<HostRenderFault>()
    member _.Faults = List.ofSeq faults

    interface IHostRenderTelemetrySink with
        member _.Capture(fault: HostRenderFault) = faults.Add fault

let tests =
    testList "HostRenderBoundary (Phase 273)" [

        testCase "a throwing node yields the structured fallback fragment, not an exception"
        <| fun _ ->
            let sink = CapturingSink()

            let result =
                HostRenderBoundary.guard (sink :> IHostRenderTelemetrySink) "node-x" (fun () -> failwith "boom")

            Expect.stringContains result HostRenderBoundary.FallbackClass "the fallback fragment is returned"
            Expect.stringContains result "data-node-id=\"node-x\"" "the fallback carries the faulting node id"

            Expect.stringContains
                result
                HostRenderBoundary.FallbackMessage
                "the fallback carries the degraded-state message"

            Expect.isFalse (result.Contains "boom") "the exception text is NOT leaked into the served fragment"

        testCase "the boundary trip reports a RenderFault through the Phase 268 sink"
        <| fun _ ->
            let sink = CapturingSink()

            HostRenderBoundary.guard (sink :> IHostRenderTelemetrySink) "node-7" (fun () -> failwith "kaboom")
            |> ignore

            match sink.Faults with
            | [ fault ] ->
                Expect.equal fault.Kind HostRenderFaultKind.RenderFault "reported as a render fault"
                Expect.equal fault.NodeId "node-7" "the faulting node id is carried"
                Expect.stringContains fault.Message "kaboom" "the exception message rides the telemetry sink"
            | other -> failtestf "expected exactly one captured fault; got: %A" other

        testCase "a healthy tree renders byte-identically and captures nothing (GP 11)"
        <| fun _ ->
            let sink = CapturingSink()

            let result =
                HostRenderBoundary.guard (sink :> IHostRenderTelemetrySink) "node-ok" (fun () -> "<p>hello</p>")

            Expect.equal result "<p>hello</p>" "a healthy render is returned verbatim"
            Expect.isEmpty sink.Faults "a healthy render reports no fault"

        testCase "a page assembled from several fragments survives one throwing subtree"
        <| fun _ ->
            let sink = CapturingSink()
            let s = sink :> IHostRenderTelemetrySink

            // A page = concatenation of independently-guarded fragments. One
            // subtree throws; the page still assembles with the others intact.
            let page =
                [
                    HostRenderBoundary.guard s "head" (fun () -> "<h1>Title</h1>")
                    HostRenderBoundary.guard s "bad" (fun () -> failwith "bad node")
                    HostRenderBoundary.guard s "tail" (fun () -> "<p>tail</p>")
                ]
                |> String.concat ""

            Expect.stringContains page "<h1>Title</h1>" "the healthy leading fragment renders"
            Expect.stringContains page "<p>tail</p>" "the healthy trailing fragment renders"
            Expect.stringContains page HostRenderBoundary.FallbackClass "the bad subtree degrades to the fallback"

            match sink.Faults with
            | [ fault ] -> Expect.equal fault.NodeId "bad" "exactly the bad subtree reported a fault"
            | other -> failtestf "expected exactly one captured fault; got: %A" other

        testCase "the default fallback hydrates parity-clean vs a matching CSR mount (Phase 203)"
        <| fun _ ->
            let fault = HostRenderFault.render "n1" "whatever"
            let ssr = HostRenderBoundary.defaultFallback fault

            // A CSR React mount of the same structural fallback: attribute
            // order differs and React adds `data-reactroot` — both collapsed
            // by HydrationParity normalisation. Structurally identical ⇒ Parity.
            let csr =
                "<div data-reactroot=\"\" role=\"note\" data-node-id=\"n1\" class=\""
                + HostRenderBoundary.FallbackClass
                + "\">"
                + HostRenderBoundary.FallbackMessage
                + "</div>"

            match HydrationParity.check ssr csr with
            | HydrationParity.Parity -> ()
            | HydrationParity.Divergence msg -> failtestf "the fallback must hydrate parity-clean; got: %s" msg

        testCase "guardWith honours a consumer-supplied fallback fragment"
        <| fun _ ->
            let sink = CapturingSink()

            let result =
                HostRenderBoundary.guardWith
                    (sink :> IHostRenderTelemetrySink)
                    (fun _fault -> "<span>down</span>")
                    "node-c"
                    (fun () -> failwith "x")

            Expect.equal result "<span>down</span>" "the custom fallback fragment is returned"
            Expect.equal (List.length sink.Faults) 1 "the fault is still reported through the sink"
    ]