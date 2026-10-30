// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── RunJob — one way a platform job is written ──────────────────────
//
// A platform job is a name, a parse and a run. `RunJob.handler` turns that
// value into the `IJobHandler` a scheduler registers, and decides the one
// question every handler used to decide for itself: which failures end the
// job and which are retried.
//
//   * a payload that does not parse is `PermanentFailure` — the same bytes
//     will not parse on the next attempt;
//   * `RunFailure.Terminal` is `PermanentFailure` — the run has said no
//     retry recovers it;
//   * `RunFailure.Retryable` is `TransientFailure` — the scheduler's retry
//     policy (and `JobLifecycle.transition` behind it) takes it from there.
//
// The handler only produces the `JobResult`; retry, backoff and
// dead-lettering stay with the scheduler, so a `RunJob` sits on the job
// lifecycle and never beside it. Stateless between invocations (GP 12
// rule 4): everything a run needs arrives as the `JobContext` and the
// parsed request.
//
// `JobPayload` is the one codec for the persisted request: the SDK's
// standard `FableConverters` options, which every platform handler already
// used, so a payload queued by an earlier version parses unchanged.

/// Why a run failed, in the one distinction the scheduler acts on.
[<RequireQualifiedAccess>]
type RunFailure =
    /// No retry recovers it (a missing prerequisite, a refusal, invalid
    /// input). The job ends `PermanentFailure`.
    | Terminal of reason: string
    /// A later attempt may succeed (storage, an upstream, a provider fault).
    /// The job ends `TransientFailure` and the scheduler's policy retries it.
    | Retryable of reason: string

[<RequireQualifiedAccess>]
module RunFailure =

    /// The human-readable reason a run failed.
    let reason (failure: RunFailure) : string =
        match failure with
        | RunFailure.Terminal reason
        | RunFailure.Retryable reason -> reason

/// A platform job: the handler name it registers under, how its persisted
/// payload parses, and the run itself.
type RunJob<'Req> = {
    /// The scheduler handler name. Persisted on every queued job: never
    /// rename one that has shipped.
    Name: string
    /// Parse the persisted payload. `Error` is a malformed payload.
    Parse: string -> Result<'Req, string>
    /// Run one attempt.
    Run: JobContext -> 'Req -> Async<Result<unit, RunFailure>>
}

/// The one codec for a platform job's persisted request.
[<RequireQualifiedAccess>]
module JobPayload =

    let private options = FableConverters.create ()

    /// The persisted payload string for a request.
    let serialise<'T> (request: 'T) : string =
        JsonSerializer.Serialize(request, options)

    /// Parse a persisted payload. `Error` carries the parser's detail.
    let parse<'T> (payload: string) : Result<'T, string> =
        try
            match JsonSerializer.Deserialize<'T>(payload, options) with
            | value when isNull (box value) -> Error "the payload is null"
            | value -> Ok value
        with ex ->
            Error ex.Message

[<RequireQualifiedAccess>]
module RunJob =

    /// A job whose payload is the `JobPayload` encoding of `'Req`.
    let ofPayload<'Req> (name: string) (run: JobContext -> 'Req -> Async<Result<unit, RunFailure>>) : RunJob<'Req> = {
        Name = name
        Parse = JobPayload.parse<'Req>
        Run = run
    }

    /// The `JobResult` a run's outcome maps to.
    let toJobResult (outcome: Result<unit, RunFailure>) : JobResult =
        match outcome with
        | Ok() -> Success
        | Error(RunFailure.Terminal reason) -> PermanentFailure reason
        | Error(RunFailure.Retryable reason) -> TransientFailure reason

    /// The `IJobHandler` for a job. A malformed payload and a
    /// `RunFailure.Terminal` are permanent; anything else that fails is
    /// transient. Each failure is logged once, under the job's name.
    let handler (logger: ILogger) (job: RunJob<'Req>) : IJobHandler =
        { new IJobHandler with
            member _.Execute(ctx: JobContext) : Async<JobResult> = async {
                match job.Parse ctx.Payload with
                | Error detail ->
                    let message = $"malformed {typeof<'Req>.Name} payload: {detail}"
                    logger.Error($"{job.Name}: job {ctx.JobId} — {message}", None)
                    return PermanentFailure message
                | Ok request ->
                    let! outcome = job.Run ctx request

                    match outcome with
                    | Ok() -> ()
                    | Error(RunFailure.Terminal reason) -> logger.Warn $"{job.Name}: terminal — {reason}"
                    | Error(RunFailure.Retryable reason) -> logger.Warn $"{job.Name}: retryable — {reason}"

                    return toJobResult outcome
            }
        }