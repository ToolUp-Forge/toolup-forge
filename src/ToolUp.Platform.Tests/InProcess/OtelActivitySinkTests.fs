module ToolUp.Platform.Tests.InProcess.OtelActivitySinkTests

open System.Diagnostics
open ToolUp.Platform.Tracing
open ToolUp.Platform.Tracing.OpenTelemetry
open ToolUp.Platform.Tests.Contracts

// ─── In-process binding — IActivitySinkContract ─────────────────────
//
// Binds the Phase 9l contract pack to `OtelActivitySink`. The
// `ActivityListener` is registered against the sink's `ActivitySource`
// name (`"ToolUp"`) before each test and cleared after; without an
// always-on registration the OTel companion's `StartActivity` returns
// `null` (the documented zero-overhead path), and the contract's
// "Some activity" assertions would fail.
//
// `ShouldListenTo` matches by source name; `Sample` returns
// `AllDataAndRecorded` so every started activity materialises in
// `Activity.Current` (the BCL's sampling primitive — without
// `AllDataAndRecorded` the listener can return `RecordingButNot
// Sampled` which still produces an activity, or `None` which doesn't).

let private factory () =
    let captured = ResizeArray<Activity>()

    let listener =
        new ActivityListener(
            ShouldListenTo = (fun src -> src.Name = "ToolUp"),
            Sample =
                (fun (sampling: byref<ActivityCreationOptions<ActivityContext>>) ->
                    ActivitySamplingResult.AllDataAndRecorded),
            ActivityStarted = (fun a -> lock captured (fun () -> captured.Add a))
        )

    ActivitySource.AddActivityListener listener

    let sink = new OtelActivitySink() :> IActivitySink

    let dispose () =
        listener.Dispose()

        match sink with
        | :? System.IDisposable as d -> d.Dispose()
        | _ -> ()

        Activity.Current <- null

    let snapshot () =
        lock captured (fun () -> captured |> List.ofSeq)

    sink, snapshot, dispose

let tests = IActivitySinkContract.tests "OtelActivitySink" factory