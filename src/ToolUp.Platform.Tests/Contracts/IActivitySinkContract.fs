module ToolUp.Platform.Tests.Contracts.IActivitySinkContract

open System.Diagnostics
open Expecto
open ToolUp.Platform.Tracing

// ─── IActivitySink contract pack — Phase 9l ─────────────────────────
//
// Parametrised tests for any `IActivitySink` implementation. The
// factory returns a fresh `(sink, captured)` pair where `captured` is
// the implementation-specific lens into emitted activities — for
// `OtelActivitySink` it's the `ResizeArray` populated by an
// `ActivityListener` registered against the sink's `ActivitySource`;
// for a hypothetical test stub it could be an in-memory tree the sink
// builds during the test.
//
// **Coverage:**
//   1. No-listener / no-op sink returns `None` (zero-overhead path).
//   2. Listener-attached sink returns `Some` activity with W3C-format id.
//   3. Two `StartActivity` calls with `None` parent generate
//      independent traces (distinct TraceId).
//   4. `StartActivity` with `Some parentContext` inherits the
//      caller's TraceId (parent-child link preserved across the
//      boundary, which is the wire-level requirement for cross-process
//      tracing through `NotificationEnvelope.TraceContext`).
//   5. Nested activities under one ambient parent form a 3-deep tree
//      with consistent TraceId — models the job → audit → notification
//      chain the phase calls out in its acceptance criterion.
//   6. Disposed activity has its `Duration` set (basic timing sanity).

/// The factory abstraction. Returns:
///   * `sink`     — the IActivitySink under test
///   * `captured` — a thunk returning every activity the sink observed
///                  since the test started (the OTel binding wires a
///                  listener; a future fake sink would just expose its
///                  internal store).
///   * `disposeListener` — cleanup hook; called by the test runner
///                         after each test so `Activity.Current` state
///                         doesn't leak between cases.
type ActivitySinkFactory = unit -> IActivitySink * (unit -> Activity list) * (unit -> unit)

let tests (name: string) (factory: ActivitySinkFactory) =

    testList $"{name} — IActivitySink contract" [

        // ─── 1. No-listener path returns None ──────────────────

        testCase "1. NoOpActivitySink returns None for every call"
        <| fun _ ->
            let sink = NoOpActivitySink() :> IActivitySink
            let result = sink.StartActivity("test", None)
            Expect.isNone result "no-op sink must return None"

        // ─── 2. Listener-attached sink emits an activity ───────

        testCase "2. StartActivity with a registered listener returns a W3C-format activity"
        <| fun _ ->
            let sink, captured, dispose = factory ()

            try
                let opt = sink.StartActivity("op", None)

                match opt with
                | None -> failtest "real sink with listener must return Some activity"
                | Some activity ->
                    Expect.equal activity.IdFormat ActivityIdFormat.W3C "BCL must use W3C trace id format"

                    Expect.isFalse
                        (System.String.IsNullOrWhiteSpace(activity.TraceId.ToString()))
                        "TraceId must be populated"

                    Expect.isFalse
                        (System.String.IsNullOrWhiteSpace(activity.SpanId.ToString()))
                        "SpanId must be populated"

                    activity.Dispose()
                    let _ = captured ()
                    ()
            finally
                dispose ()

        // ─── 3. Independent traces without ambient parent ──────

        testCase "3. Two StartActivity(None, None) calls produce distinct trace ids"
        <| fun _ ->
            // Activity.Current is async-local; ensure no leakage from
            // sibling tests by stamping a fresh activity-less scope.
            Activity.Current <- null

            let sink, _, dispose = factory ()

            try
                let a = sink.StartActivity("alpha", None)
                let traceA = a |> Option.map (fun act -> act.TraceId.ToString())
                a |> Option.iter (fun act -> act.Dispose())

                Activity.Current <- null
                let b = sink.StartActivity("beta", None)
                let traceB = b |> Option.map (fun act -> act.TraceId.ToString())
                b |> Option.iter (fun act -> act.Dispose())

                match traceA, traceB with
                | Some ta, Some tb -> Expect.notEqual ta tb "independent root activities must own distinct trace ids"
                | _ -> failtest "both activities must be present under a real listener"
            finally
                dispose ()

        // ─── 4. parentContext inheritance (the cross-process link) ─

        testCase "4. parentContext = Some ctx → child activity carries the same TraceId"
        <| fun _ ->
            Activity.Current <- null
            let sink, _, dispose = factory ()

            try
                let root = sink.StartActivity("root", None)

                match root with
                | None -> failtest "factory must yield a listener-bound sink"
                | Some rootActivity ->
                    let rootCtx = rootActivity.Context
                    rootActivity.Dispose()

                    // Simulate cross-process resumption: a subscriber
                    // receives the parent context as a value (the W3C
                    // string round-tripped through
                    // NotificationEnvelope.TraceContext) and continues
                    // the trace under its own child span.
                    Activity.Current <- null
                    let child = sink.StartActivity("child-on-other-side", Some rootCtx)

                    match child with
                    | None -> failtest "child activity must materialise when parent context is supplied"
                    | Some childActivity ->
                        Expect.equal
                            (childActivity.TraceId.ToString())
                            (rootCtx.TraceId.ToString())
                            "child activity must inherit parent TraceId"

                        Expect.notEqual
                            (childActivity.SpanId.ToString())
                            (rootCtx.SpanId.ToString())
                            "child must own a fresh SpanId"

                        childActivity.Dispose()
            finally
                dispose ()

        // ─── 5. 3-deep nested chain under one ambient parent ───

        testCase "5. Nested StartActivity calls form a connected trace tree"
        <| fun _ ->
            Activity.Current <- null
            let sink, _, dispose = factory ()

            try
                // Models the job → audit → notify chain from the
                // phase's acceptance criterion. Each nested
                // StartActivity(name, None) call lets the BCL pick
                // Activity.Current as the implicit parent.
                let outer = sink.StartActivity("job", None)

                match outer with
                | None -> failtest "outer activity must be created under a real listener"
                | Some outerActivity ->
                    let outerTrace = outerActivity.TraceId.ToString()

                    let mid = sink.StartActivity("audit", None)

                    match mid with
                    | None -> failtest "mid activity must be created"
                    | Some midActivity ->
                        let inner = sink.StartActivity("notify", None)

                        match inner with
                        | None -> failtest "inner activity must be created"
                        | Some innerActivity ->
                            Expect.equal
                                (midActivity.TraceId.ToString())
                                outerTrace
                                "mid span must share outer's TraceId"

                            Expect.equal
                                (innerActivity.TraceId.ToString())
                                outerTrace
                                "inner span must share outer's TraceId"

                            // Verify the parent links via ParentSpanId.
                            Expect.equal
                                (midActivity.ParentSpanId.ToString())
                                (outerActivity.SpanId.ToString())
                                "mid's parent must be outer"

                            Expect.equal
                                (innerActivity.ParentSpanId.ToString())
                                (midActivity.SpanId.ToString())
                                "inner's parent must be mid"

                            innerActivity.Dispose()
                            midActivity.Dispose()
                            outerActivity.Dispose()
            finally
                dispose ()

        // ─── 6. Disposed activity records non-zero duration ────

        testCase "6. Activity.Dispose() finalises Duration"
        <| fun _ ->
            Activity.Current <- null
            let sink, _, dispose = factory ()

            try
                let opt = sink.StartActivity("timed", None)

                match opt with
                | None -> failtest "activity must be created under listener"
                | Some activity ->
                    // Tiny sleep so OS-tick precision can't collapse
                    // the duration to zero on aggressive low-res
                    // schedulers.
                    System.Threading.Thread.Sleep 5
                    activity.Dispose()

                    Expect.isGreaterThan activity.Duration.Ticks 0L "disposed activity must have a positive duration"
            finally
                dispose ()
    ]