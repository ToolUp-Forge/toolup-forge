// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.JobSchedulerBuildOrchestrator

open System
open System.Collections.Concurrent
open System.Threading
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── JobSchedulerBuildOrchestrator (Phase 26 substrate default) ──────
//
// Single-node default `IBuildOrchestrator`. Wraps `IJobScheduler` with
// a single registered handler under `_platform.build`; per-build
// dispatch goes through `Schedule(Manual) + TriggerOnce` so the
// scheduler's existing retry / backoff / dead-letter machinery applies
// to build attempts without re-deriving them here.
//
// **Substrate-default semantics.** This implementation manages the
// queue, state transitions, retry, audit emission, and idempotency. It
// does NOT execute real builds — the handler treats `PrebuiltImage` as
// an immediate success (artefact ref = the image) and every other
// `BuildSource` as a synthetic success with
// `local-build:<appSlug>:<buildId>`. Operators wanting a real build
// pipeline (clone GitHub, run `docker build`, push to a registry)
// replace this whole orchestrator via DI; the contract is the typed
// `IBuildOrchestrator` interface, not how the substrate happens to
// dispatch.
//
// **State lives in memory.** Single-process; loses state on restart.
// A distributed companion (an Akka-cluster-sharded build orchestrator)
// replaces the singleton entirely and persists via Akka.Persistence.
//
// **Per-`AppSlug` serialisation.** A `SemaphoreSlim` per `AppSlug`
// enforces the substrate's no-cross-shard-ordering rule (Phase 9c rule
// 5) at single-node scale: two builds for the same app cannot
// interleave their state transitions. Builds for different apps run
// concurrently.

[<Literal>]
let BuildHandlerName = "_platform.build"

[<Literal>]
let BuildScopeId = "_platform"

// ─── JSON helper ─────────────────────────────────────────────────

module private Json =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    let deserialize<'T> (json: string) : 'T =
        JsonSerializer.Deserialize<'T>(json, options)

// ─── Audit payloads ──────────────────────────────────────────────
//
// The Phase 26 substrate emits one event per terminal build transition
// directly to `IEventStore` under `SourceModule = "_platform.build"`.
// This bypasses the `IAuditLog` schema (which enumerates every known
// audit case) because deploy-plane events are out-of-band of the
// general audit corpus; the operator's deploy dashboard reads them via
// `IEventStore.ReadBySource(scopeId, "_platform.build")`.

[<Literal>]
let BuildSourceModule = "_platform.build"

type private BuildEnqueuedPayload = {
    BuildId: BuildId
    AppSlug: string
    SubmittedBy: string
    SubmittedAt: DateTime
}

type private BuildSucceededPayload = {
    BuildId: BuildId
    AppSlug: string
    ArtefactRef: string
    CompletedAt: DateTime
    AttemptCount: int
}

type private BuildFailedPayload = {
    BuildId: BuildId
    AppSlug: string
    Reason: string
    CompletedAt: DateTime
    AttemptCount: int
}

type private BuildCancelledPayload = {
    BuildId: BuildId
    AppSlug: string
    CancelledBy: string
    CancelledAt: DateTime
}

// ─── Job payload ─────────────────────────────────────────────────

type private BuildJobPayload = { BuildId: BuildId }

// ─── JobSchedulerBuildOrchestrator ───────────────────────────────

/// Single-node default `IBuildOrchestrator`. Wraps `IJobScheduler`,
/// registers a handler under `_platform.build`, owns the per-build
/// state dictionary. Per-`AppSlug` SemaphoreSlim enforces serialisation.
type JobSchedulerBuildOrchestrator(scheduler: IJobScheduler, eventStore: IEventStore, logger: ILogger) =

    let builds = ConcurrentDictionary<BuildId, BuildSummary>()
    let requests = ConcurrentDictionary<BuildId, BuildRequest>()
    let jobByBuild = ConcurrentDictionary<BuildId, JobId>()
    let idempotencyIndex = ConcurrentDictionary<string, BuildId>()
    let perSlugLocks = ConcurrentDictionary<string, SemaphoreSlim>()

    let lockFor (appSlug: string) =
        perSlugLocks.GetOrAdd(appSlug, fun _ -> new SemaphoreSlim(1, 1))

    let emitEvent (eventType: string) (payload: 'T) = async {
        try
            let evt =
                Events.create BuildScopeId BuildSourceModule eventType (Json.serialize payload)

            do! eventStore.Write evt
        with ex ->
            logger.Warn $"[BuildOrchestrator] event=write_failed eventType={eventType}: {ex.Message}"
    }

    let validate (request: BuildRequest) : Result<unit, BuildOrchestratorError> =
        if String.IsNullOrWhiteSpace request.AppSlug then
            Error(InvalidRequest "AppSlug is required")
        elif request.RetryPolicy.MaxAttempts < 1 then
            Error(InvalidRequest "RetryPolicy.MaxAttempts must be >= 1")
        else
            match request.Source with
            | GitHubRef("", _) -> Error(InvalidRequest "GitHubRef.repo is required")
            | GitHubRef(_, "") -> Error(InvalidRequest "GitHubRef.sha is required")
            | LocalPath "" -> Error(InvalidRequest "LocalPath.path is required")
            | PrebuiltImage "" -> Error(InvalidRequest "PrebuiltImage.image is required")
            | ExternalSource("", _) -> Error(InvalidRequest "ExternalSource.kind is required")
            | _ -> Ok()

    /// Compute the artefact ref a Succeeded build produces in the
    /// substrate default. `PrebuiltImage` passes through unchanged;
    /// every other source produces a local-default placeholder the
    /// downstream `IDeployPipeline` forwards to the consumer-supplied
    /// `IContainerScheduler`. Companions wiring a real build pipeline
    /// replace this whole orchestrator.
    let artefactRefFor (request: BuildRequest) (buildId: BuildId) : string =
        match request.Source with
        | PrebuiltImage image -> image
        | _ -> sprintf "local-build:%s:%s" request.AppSlug buildId

    /// Translate `BuildRetryPolicy` to the `JobRetryPolicy` shape the
    /// scheduler consumes. The build's first backoff seconds value
    /// becomes the job's `InitialBackoff`; the last value becomes
    /// `MaxBackoff` (or `InitialBackoff` again when the list is short).
    /// Substrate defaults that never actually retry the synthetic-
    /// success path see this unchanged; consumers replacing the
    /// handler get the documented per-attempt cadence.
    let translateRetryPolicy (policy: BuildRetryPolicy) : JobRetryPolicy =
        let initial =
            match policy.BackoffSeconds with
            | head :: _ -> TimeSpan.FromSeconds(float head)
            | [] -> TimeSpan.Zero

        let maxBackoff =
            match List.tryLast policy.BackoffSeconds with
            | Some last -> TimeSpan.FromSeconds(float (max last (initial.TotalSeconds |> int)))
            | None -> initial

        {
            MaxAttempts = policy.MaxAttempts
            InitialBackoff = initial
            MaxBackoff = maxBackoff
            DeadLetterDestination = None
        }

    // The IJobHandler that runs per dispatched build. Reads BuildId
    // from `ctx.Payload`, looks up the persisted BuildRequest +
    // BuildSummary, transitions through Building → Succeeded/Failed,
    // emits the matching audit event.
    let buildHandler =
        { new IJobHandler with
            member _.Execute(ctx: JobContext) = async {
                let payload =
                    try
                        Json.deserialize<BuildJobPayload> ctx.Payload
                    with _ -> { BuildId = "" }

                if String.IsNullOrEmpty payload.BuildId then
                    return PermanentFailure "BuildJobPayload missing BuildId"
                else
                    match builds.TryGetValue payload.BuildId, requests.TryGetValue payload.BuildId with
                    | (true, current), (true, request) ->
                        let slugLock = lockFor request.AppSlug
                        do! slugLock.WaitAsync() |> Async.AwaitTask

                        try
                            let startedAt = DateTime.UtcNow

                            let buildingSummary = {
                                current with
                                    Status = BuildStatus.Building(startedAt, ctx.Attempt)
                                    AttemptCount = ctx.Attempt
                            }

                            builds[payload.BuildId] <- buildingSummary

                            // Substrate default: synthetic success.
                            let completedAt = DateTime.UtcNow
                            let artefactRef = artefactRefFor request payload.BuildId

                            let succeededSummary = {
                                buildingSummary with
                                    Status = BuildStatus.Succeeded(completedAt, artefactRef)
                            }

                            builds[payload.BuildId] <- succeededSummary

                            do!
                                emitEvent "BuildSucceeded" {
                                    BuildId = payload.BuildId
                                    AppSlug = request.AppSlug
                                    ArtefactRef = artefactRef
                                    CompletedAt = completedAt
                                    AttemptCount = ctx.Attempt
                                }

                            return Success
                        finally
                            slugLock.Release() |> ignore
                    | _ -> return PermanentFailure(sprintf "BuildId %s not in orchestrator state" payload.BuildId)
            }
        }

    // Register handler at construction time. Idempotent per
    // `IJobScheduler.RegisterHandler` (re-registration overwrites);
    // safe even if a sibling code path also registered.
    do scheduler.RegisterHandler(BuildHandlerName, buildHandler)

    /// Schedule + dispatch a fresh build through `IJobScheduler`.
    /// Returns the assigned `BuildId` on success.
    let scheduleNew (request: BuildRequest) (buildId: BuildId) : Async<Result<BuildId, BuildOrchestratorError>> = async {
        let submittedAt = DateTime.UtcNow

        let summary: BuildSummary = {
            BuildId = buildId
            AppSlug = request.AppSlug
            Status = BuildStatus.Queued
            SubmittedAt = submittedAt
            SubmittedBy = request.SubmittedBy
            AttemptCount = 0
        }

        builds[buildId] <- summary
        requests[buildId] <- request

        let registration: JobRegistration = {
            ScopeId = BuildScopeId
            Handler = BuildHandlerName
            Payload = Json.serialize { BuildId = buildId }
            Trigger = Manual
            Idempotency = None
            RetryPolicy = translateRetryPolicy request.RetryPolicy
            ShardKey = Some request.AppSlug
            Precision = JobPrecision.Minute
            CreatedBy = request.SubmittedBy
            Tags = Map [ "appSlug", request.AppSlug; "buildId", buildId ]
        }

        let! scheduled = scheduler.Schedule registration

        match scheduled with
        | Error e ->
            // Roll back our state — the build never actually
            // entered the queue.
            builds.TryRemove buildId |> ignore
            requests.TryRemove buildId |> ignore

            let reason =
                match e with
                | HandlerNotRegistered h -> sprintf "scheduler rejected: handler %s not registered" h
                | InvalidCron(expr, why) -> sprintf "scheduler rejected: invalid cron %s (%s)" expr why
                | PrecisionUnsupported(p, supported) ->
                    sprintf "scheduler rejected: precision %A unsupported (supported: %A)" p supported
                | ScheduleError.StorageFailure msg -> sprintf "scheduler storage failure: %s" msg

            return Error(OrchestratorStorageFailure reason)
        | Ok jobId ->
            jobByBuild[buildId] <- jobId

            do!
                emitEvent "BuildEnqueued" {
                    BuildId = buildId
                    AppSlug = request.AppSlug
                    SubmittedBy = request.SubmittedBy
                    SubmittedAt = submittedAt
                }

            // Dispatch immediately — Manual jobs do not auto-fire on
            // tick. The scheduler's `Async.Start dispatchOne` runs
            // the handler in the background; our state dict tracks
            // the transition.
            let! triggered = scheduler.TriggerOnce(BuildScopeId, jobId, request.SubmittedBy)

            match triggered with
            | Ok _ -> return Ok buildId
            | Error msg ->
                logger.Warn $"[BuildOrchestrator] event=triggeronce_failed buildId={buildId}: {msg}"
                return Ok buildId
    }

    interface IBuildOrchestrator with

        member _.EnqueueBuild(request) = async {
            match validate request with
            | Error e -> return Error e
            | Ok() ->
                match request.Idempotency with
                | Some token ->
                    match idempotencyIndex.TryGetValue token with
                    | true, existingId -> return Ok existingId
                    | _ ->
                        let buildId = Guid.NewGuid().ToString("N")
                        idempotencyIndex[token] <- buildId
                        return! scheduleNew request buildId
                | None ->
                    let buildId = Guid.NewGuid().ToString("N")
                    return! scheduleNew request buildId
        }

        member _.GetBuild(buildId) = async {
            match builds.TryGetValue buildId with
            | true, summary -> return Ok summary
            | _ -> return Error(UnknownBuild buildId)
        }

        member _.ListActiveBuilds(appSlug) = async {
            let isActive (s: BuildSummary) =
                match s.Status with
                | BuildStatus.Queued
                | BuildStatus.Building _ -> true
                | BuildStatus.Succeeded _
                | BuildStatus.Failed _
                | BuildStatus.Cancelled _ -> false

            let filter =
                match appSlug with
                | Some slug -> fun (s: BuildSummary) -> isActive s && s.AppSlug = slug
                | None -> isActive

            return builds.Values |> Seq.filter filter |> Seq.sortBy _.SubmittedAt |> Seq.toList
        }

        member self.GetQueueDepth() = async {
            let! actives = (self :> IBuildOrchestrator).ListActiveBuilds None
            return actives.Length
        }

        member _.CancelBuild(buildId, byUserId) = async {
            match builds.TryGetValue buildId with
            | false, _ -> return Error(UnknownBuild buildId)
            | true, current ->
                match current.Status with
                | BuildStatus.Cancelled _ ->
                    // Idempotent.
                    return Ok()
                | BuildStatus.Succeeded _
                | BuildStatus.Failed _ -> return Error(AlreadyTerminated(buildId, current.Status))
                | BuildStatus.Queued
                | BuildStatus.Building _ ->
                    let slugLock = lockFor current.AppSlug
                    do! slugLock.WaitAsync() |> Async.AwaitTask

                    try
                        let cancelledAt = DateTime.UtcNow

                        let cancelled = {
                            current with
                                Status = BuildStatus.Cancelled(cancelledAt, byUserId)
                        }

                        builds[buildId] <- cancelled

                        match jobByBuild.TryGetValue buildId with
                        | true, jobId -> do! scheduler.Cancel(BuildScopeId, jobId)
                        | _ -> ()

                        do!
                            emitEvent "BuildCancelled" {
                                BuildId = buildId
                                AppSlug = current.AppSlug
                                CancelledBy = byUserId
                                CancelledAt = cancelledAt
                            }

                        return Ok()
                    finally
                        slugLock.Release() |> ignore
        }

        member _.GetBuildHistory(appSlug, count) = async {
            return
                builds.Values
                |> Seq.filter (fun s -> s.AppSlug = appSlug)
                |> Seq.sortByDescending _.SubmittedAt
                |> Seq.truncate (max 0 count)
                |> Seq.toList
        }

// ─── Convenience constructor ─────────────────────────────────────

let create (scheduler: IJobScheduler) (eventStore: IEventStore) (logger: ILogger) : JobSchedulerBuildOrchestrator =
    JobSchedulerBuildOrchestrator(scheduler, eventStore, logger)