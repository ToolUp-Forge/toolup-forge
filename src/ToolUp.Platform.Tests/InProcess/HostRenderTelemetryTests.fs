module ToolUp.Platform.Tests.InProcess.HostRenderTelemetryTests

open Expecto
open ToolUp.Platform

// ─── Phase 268 — hosted-tree render-failure telemetry tests ───────────
//
// The sink makes a hosted-tree render / binding-resolution fault OBSERVABLE
// instead of a swallowed console warning. This pack pins:
//   * a forced render fault reaches the sink with the right kind + node id;
//   * a forced binding-resolution fault reaches the sink with the right
//     kind + node id + binding name;
//   * the Core `NoOpHostRenderTelemetrySink` default swallows silently
//     (captures without throwing, forwards nothing observable);
//   * the counting decorator counts and forwards each fault (the
//     boot-degradation / health surface signal);
//   * the Phase 270 `onMismatch` bridge captures a capability mismatch as a
//     render fault against the mounting node;
//   * the forwarding default writes each fault to its `ILogger`;
//   * `describe` names the node id (and, for a binding fault, the binding).

/// A capturing sink — records every fault so a test can assert what
/// reached the boundary.
type private CapturingSink() =
    let faults = ResizeArray<HostRenderFault>()
    member _.Faults = List.ofSeq faults

    interface IHostRenderTelemetrySink with
        member _.Capture(fault: HostRenderFault) = faults.Add fault

/// A capturing logger — records every `Warn` line so the forwarding-default
/// test can assert the fault was forwarded.
type private CapturingLogger() =
    let warns = ResizeArray<string>()
    member _.Warns = List.ofSeq warns

    interface ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(message: string) = warns.Add message
        member _.Error(_: string, _: exn option) = ()

let tests =
    testList "HostRenderTelemetry (Phase 268)" [

        testCase "a render fault reaches the sink with the right kind + node id"
        <| fun _ ->
            let capturing = CapturingSink()
            let sink = capturing :> IHostRenderTelemetrySink
            sink.Capture(HostRenderFault.render "node-42" "renderer threw")

            match capturing.Faults with
            | [ fault ] ->
                Expect.equal fault.Kind HostRenderFaultKind.RenderFault "captured as a render fault"
                Expect.equal fault.NodeId "node-42" "the faulting node id is carried"
                Expect.equal fault.Binding None "a render fault carries no binding name"
            | other -> failtestf "expected exactly one captured fault; got: %A" other

        testCase "a binding-resolution fault reaches the sink with the right kind + node id + binding"
        <| fun _ ->
            let capturing = CapturingSink()
            let sink = capturing :> IHostRenderTelemetrySink
            sink.Capture(HostRenderFault.bindingResolution "node-7" "customer.name" "no such binding source")

            match capturing.Faults with
            | [ fault ] ->
                Expect.equal
                    fault.Kind
                    HostRenderFaultKind.BindingResolutionFault
                    "captured as a binding-resolution fault"

                Expect.equal fault.NodeId "node-7" "the faulting node id is carried"
                Expect.equal fault.Binding (Some "customer.name") "the failed binding name is carried"
            | other -> failtestf "expected exactly one captured fault; got: %A" other

        testCase "the NoOp default swallows silently (captures without throwing, forwards nothing)"
        <| fun _ ->
            // A counting decorator over the NoOp default: the decorator sees
            // each call, but the NoOp swallows it — no throw, nothing shipped.
            let counting =
                HostRenderTelemetry.CountingHostRenderTelemetrySink(NoOpHostRenderTelemetrySink())

            let sink = counting :> IHostRenderTelemetrySink
            sink.Capture(HostRenderFault.render "n1" "boom")
            sink.Capture(HostRenderFault.bindingResolution "n2" "b" "boom")
            // No exception escaped the NoOp boundary; the decorator counted
            // the calls the NoOp silently discarded.
            Expect.equal counting.Count 2 "the NoOp accepted every capture without throwing"

        testCase "the counting decorator counts and forwards each fault to its inner sink"
        <| fun _ ->
            let capturing = CapturingSink()

            let counting =
                HostRenderTelemetry.CountingHostRenderTelemetrySink(capturing :> IHostRenderTelemetrySink)

            let sink = counting :> IHostRenderTelemetrySink
            sink.Capture(HostRenderFault.render "a" "1")
            sink.Capture(HostRenderFault.render "b" "2")
            sink.Capture(HostRenderFault.render "c" "3")

            Expect.equal counting.Count 3 "the decorator counts every fault (health-surface signal)"
            Expect.equal (List.length capturing.Faults) 3 "every fault is forwarded to the inner sink"

        testCase "the Phase 270 onMismatch bridge captures a capability mismatch as a render fault"
        <| fun _ ->
            let capturing = CapturingSink()

            let report =
                HostRenderTelemetry.onMismatch (capturing :> IHostRenderTelemetrySink) "mount-node"

            report (HostCapabilityMismatch.MissingCapabilities [ "host.file.read" ])

            match capturing.Faults with
            | [ fault ] ->
                Expect.equal
                    fault.Kind
                    HostRenderFaultKind.RenderFault
                    "a negotiation gap is reported as a render fault"

                Expect.equal fault.NodeId "mount-node" "the mounting node id is carried"
                Expect.stringContains fault.Message "host.file.read" "the message names the missing capability"
            | other -> failtestf "expected exactly one captured fault; got: %A" other

        testCase "the forwarding default writes each fault to its ILogger"
        <| fun _ ->
            let logger = CapturingLogger()
            let sink = HostRenderTelemetry.forwardingToLogger (logger :> ILogger)
            sink.Capture(HostRenderFault.render "node-9" "renderer threw")

            match logger.Warns with
            | [ line ] ->
                Expect.stringContains line "node-9" "the forwarded log line names the faulting node"
                Expect.stringContains line "renderer threw" "the forwarded log line carries the message"
            | other -> failtestf "expected exactly one Warn line; got: %A" other

        testCase "describe names the node id and, for a binding fault, the binding"
        <| fun _ ->
            let renderMsg = HostRenderFault.describe (HostRenderFault.render "n-r" "kaboom")
            Expect.stringContains renderMsg "n-r" "render-fault description names the node"
            Expect.stringContains renderMsg "kaboom" "render-fault description carries the message"

            let bindingMsg =
                HostRenderFault.describe (HostRenderFault.bindingResolution "n-b" "order.total" "missing key")

            Expect.stringContains bindingMsg "n-b" "binding-fault description names the node"
            Expect.stringContains bindingMsg "order.total" "binding-fault description names the binding"
    ]