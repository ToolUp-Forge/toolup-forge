// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Hermetic doubles for the derivative-pipeline pack: counting
/// renderers, a recording notification channel, and a manual-pump
/// job scheduler that runs triggered jobs only when the test says
/// so (deterministic pending → ready transitions).
module ToolUp.AssetStore.Tests.Doubles

open System
open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.AssetStore

let nullLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let nullAuditLog =
    { new IAuditLog with
        member _.Record(_, _) = async.Return()

        member _.GetAuditTrail(_, _, _) = async.Return []
    }

/// Counting image renderer — deterministic bytes derived from the
/// spec name so cache-path assertions are content-checkable.
type CountingImageRenderer() =
    let mutable renders = 0
    member _.RenderCount = renders

    interface IDerivativeRenderer with
        member _.Render(_originalBytes, spec) = async {
            renders <- renders + 1

            return Ok(System.Text.Encoding.UTF8.GetBytes("image:" + spec.Name), ImageFormat.mimeType spec.Format)
        }

        member _.Probe _ = async.Return(Some(640, 480))

/// Counting general-MIME renderer; flips to failure mode on demand.
type CountingMimeRenderer(outputBytes: byte[]) =
    let mutable renders = 0
    member val FailWith: DerivativeRenderError option = None with get, set
    member _.RenderCount = renders

    interface IMimeDerivativeRenderer with
        member this.Render(_originalBytes, _inputMime, spec) = async {
            renders <- renders + 1

            return
                match this.FailWith with
                | Some error -> Error error
                | None -> Ok(outputBytes, spec.OutputMime)
        }

/// Records published notifications per scope.
type RecordingNotificationChannel() =
    let published = ConcurrentQueue<string * Notification>()
    member _.Published = published |> List.ofSeq

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Enqueue(scopeId, notification) }

        member _.Subscribe(_, _) : Async<NotificationSubscriptionId> =
            failwith "not used by the derivative pack"

        member _.Unsubscribe(_) : Async<unit> =
            failwith "not used by the derivative pack"

/// Records every counter increment, so a test can assert that a
/// derivation moved the retry / failure counters — and, on the
/// non-opted-in path, that it moved nothing at all.
type RecordingMetricsSink() =
    let increments = ConcurrentQueue<string * Map<string, string>>()

    member _.Increments = increments |> List.ofSeq

    /// How many times `metric` was incremented, across all tag sets.
    member this.CountOf(metric: string) =
        this.Increments |> List.filter (fst >> (=) metric) |> List.length

    interface IMetricsSink with
        member _.Increment(name, tags) = increments.Enqueue(name, tags)

        member _.Record(_, _, _) = ()
        member _.SetGauge(_, _, _) = ()

/// Manual-pump scheduler: `Schedule` records the registration
/// (honouring idempotency-key dedup like the real scheduler),
/// `TriggerOnce` queues a run, and `RunTriggered` executes queued
/// runs through the registered handler with a synthesised
/// `JobContext` — so tests decide exactly when the "background"
/// work happens.
type ManualJobScheduler() =
    let handlers = ConcurrentDictionary<string, IJobHandler>()
    let jobs = ConcurrentDictionary<JobId, JobRegistration>()
    let idempotency = ConcurrentDictionary<string * string, JobId>()
    let triggered = ConcurrentQueue<JobId>()
    let mutable scheduleCalls = 0

    member _.ScheduleCalls = scheduleCalls
    member _.ScheduledJobs = jobs.Values |> List.ofSeq
    member _.ScheduledJobIds = jobs.Keys |> List.ofSeq

    /// Execute every queued TriggerOnce run at the given attempt
    /// number; returns the handler results in run order.
    member _.RunTriggered(attempt: int) : JobResult list =
        let results = ResizeArray()
        let mutable jobId = Unchecked.defaultof<JobId>

        while triggered.TryDequeue &jobId do
            let registration = jobs[jobId]
            let handler = handlers[registration.Handler]

            let ctx: JobContext = {
                JobId = jobId
                ScopeId = registration.ScopeId
                AccessContext = AccessContext.unrestricted (AnonymousSession "test")
                Attempt = attempt
                Trigger = registration.Trigger
                TriggerSource = ScheduledManually "test"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = registration.Payload
                DeadLetterDestination = registration.RetryPolicy.DeadLetterDestination
            }

            results.Add(handler.Execute ctx |> Async.RunSynchronously)

        List.ofSeq results

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = handlers[name] <- handler

        member _.RegisterHandlerAsync(name, handler) = async {
            handlers[name] <- handler
            return Ok()
        }

        member _.Schedule(registration) = async {
            scheduleCalls <- scheduleCalls + 1

            match registration.Idempotency with
            | Some key ->
                match idempotency.TryGetValue((registration.ScopeId, key.Key)) with
                | true, existing -> return Ok existing
                | false, _ ->
                    let jobId = Guid.NewGuid()
                    jobs[jobId] <- registration
                    idempotency[(registration.ScopeId, key.Key)] <- jobId
                    return Ok jobId
            | None ->
                let jobId = Guid.NewGuid()
                jobs[jobId] <- registration
                return Ok jobId
        }

        member _.Cancel(_, _) = async.Return()
        member _.Disable(_, _) = async.Return()
        member _.Enable(_, _) = async.Return()
        member _.Get(_, _) = async.Return None
        member _.ListJobs _ = async.Return []
        member _.GetRecentRuns(_, _, _) = async.Return []

        member _.TriggerOnce(_, jobId, _) = async {
            triggered.Enqueue jobId
            return Ok()
        }

        member _.NotifyEventWritten(_, _, _) = async.Return()