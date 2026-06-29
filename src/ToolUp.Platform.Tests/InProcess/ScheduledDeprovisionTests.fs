module ToolUp.Platform.Tests.InProcess.ScheduledDeprovisionTests

open System
open System.Collections.Concurrent
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform

// ─── Phase 54f — scheduled / grace-period offboard tests ─────────────
//
// Two layers: the `IPlatformTenantApi` handler (Schedule / Get / Cancel
// over DI + a stub `IJobScheduler`) and the poll `IJobHandler`
// (`ScheduledDeprovisionJobHandler.Execute` — not-due → no-op, due →
// fires the offboard + retires its own job).

// ─── In-memory IJobScheduler stub ────────────────────────────────────

type private InMemoryJobScheduler() =
    let jobs = ConcurrentDictionary<JobId, JobDefinition>()
    let mutable triggeredOnce = 0

    member _.Jobs = jobs |> Seq.map (fun kv -> kv.Value) |> List.ofSeq
    member _.TriggeredOnceCount = triggeredOnce

    interface IJobScheduler with
        member _.RegisterHandler(_, _) = ()
        member _.RegisterHandlerAsync(_, _) = async { return Ok() }

        member _.Schedule(reg: JobRegistration) = async {
            let id = Guid.NewGuid()

            let def: JobDefinition = {
                JobId = id
                ScopeId = reg.ScopeId
                Handler = reg.Handler
                Payload = reg.Payload
                Trigger = reg.Trigger
                Idempotency = reg.Idempotency
                RetryPolicy = reg.RetryPolicy
                ShardKey = reg.ShardKey
                Precision = reg.Precision
                Status = JobStatus.Active
                CreatedAt = DateTime.UtcNow
                CreatedBy = reg.CreatedBy
                NextRunAt = None
                LastRunAt = None
                LastRunStatus = None
                LastRunError = None
                ConsecutiveFailures = 0
                Tags = reg.Tags
            }

            jobs[id] <- def
            return Ok id
        }

        member _.Cancel(_scopeId, jobId) = async {
            match jobs.TryGetValue jobId with
            | true, j -> jobs[jobId] <- { j with Status = JobStatus.Cancelled }
            | _ -> ()

            return ()
        }

        member _.Disable(_, _) = async { return () }
        member _.Enable(_, _) = async { return () }

        member _.Get(_scopeId, jobId) = async {
            return
                (match jobs.TryGetValue jobId with
                 | true, j -> Some j
                 | _ -> None)
        }

        member _.ListJobs(scopeId) = async {
            return
                jobs
                |> Seq.map (fun kv -> kv.Value)
                |> Seq.filter (fun j -> j.ScopeId = scopeId)
                |> List.ofSeq
        }

        member _.GetRecentRuns(_, _, _) = async { return [] }

        member _.TriggerOnce(_, _, _) = async {
            triggeredOnce <- triggeredOnce + 1
            return Ok()
        }

        member _.NotifyEventWritten(_, _, _) = async { return () }

// ─── Handler builder ─────────────────────────────────────────────────

let private adminCtx (userId: string) : AccessContext = {
    AccessContext.unrestricted (AuthenticatedUser userId) with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

let private handlerFor (userId: string) (scheduler: IJobScheduler option) : IPlatformTenantApi =
    let services = ServiceCollection()
    services.AddSingleton<AccessContext>(adminCtx userId) |> ignore

    services.AddSingleton<ServerConfig>(
        {
            ServerConfig.defaults with
                TenantLifecycle = EnabledTenantLifecycle
        }
    )
    |> ignore

    match scheduler with
    | Some s -> services.AddSingleton<IJobScheduler>(s) |> ignore
    | None -> ()

    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    PlatformTenantApiHandler.platformTenantApi ctx

/// Minimal `IServiceProvider` carrying just the scheduler — what the poll
/// handler resolves on `Execute`.
let private servicesWith (scheduler: InMemoryJobScheduler) : IServiceProvider =
    let services = ServiceCollection()
    services.AddSingleton<IJobScheduler>(scheduler :> IJobScheduler) |> ignore
    services.BuildServiceProvider() :> IServiceProvider

let private jobContext (scopeId: string) (jobId: JobId) (runningAt: DateTime) (payload: string) : JobContext = {
    JobId = jobId
    ScopeId = scopeId
    AccessContext = AccessContext.unrestricted (AuthenticatedUser "system")
    Attempt = 1
    Trigger = CronTrigger TenantLifecycleAggregator.ScheduledDeprovisionCron
    TriggerSource = ScheduledByCron
    ScheduledAt = runningAt
    RunningAt = runningAt
    Payload = payload
    DeadLetterDestination = None
}

let tests =
    testList "Phase 54f — scheduled / grace-period offboard" [

        testCaseAsync "ScheduleDeprovision registers a pending offboard with the right window + reason"
        <| async {
            let sched = InMemoryJobScheduler()
            let api = handlerFor "admin-1" (Some(sched :> IJobScheduler))
            let before = DateTimeOffset.UtcNow

            match! api.ScheduleDeprovision("team-x", "admin-1", 30, "contract wind-down") with
            | Error e -> failtestf "ScheduleDeprovision failed: %s" e
            | Ok sd ->
                Expect.equal sd.ScopeId "team-x" "scope recorded"
                Expect.equal sd.RequestedBy "admin-1" "requester recorded"
                Expect.equal sd.Reason "contract wind-down" "reason recorded"
                Expect.isGreaterThan sd.DueAt (before.AddDays 29.9) "dueAt ~30 days out (lower bound)"
                Expect.isLessThan sd.DueAt (before.AddDays 30.1) "dueAt ~30 days out (upper bound)"
                Expect.isFalse (String.IsNullOrEmpty sd.JobId) "a backing job id is returned"
        }

        testCaseAsync "GetScheduledDeprovision returns the pending offboard, then None after cancel"
        <| async {
            let sched = InMemoryJobScheduler()
            let api = handlerFor "admin-1" (Some(sched :> IJobScheduler))
            let! _ = api.ScheduleDeprovision("team-x", "admin-1", 14, "r")

            match! api.GetScheduledDeprovision "team-x" with
            | Ok(Some sd) -> Expect.equal sd.ScopeId "team-x" "pending offboard surfaced"
            | other -> failtestf "expected a pending offboard, got %A" other

            let! cancelled = api.CancelScheduledDeprovision "team-x"
            Expect.equal cancelled (Ok()) "cancel succeeds"

            match! api.GetScheduledDeprovision "team-x" with
            | Ok None -> ()
            | other -> failtestf "expected None after cancel, got %A" other
        }

        testCaseAsync "ScheduleDeprovision is idempotent per scope — a second schedule returns the existing one"
        <| async {
            let sched = InMemoryJobScheduler()
            let api = handlerFor "admin-1" (Some(sched :> IJobScheduler))
            let! first = api.ScheduleDeprovision("team-x", "admin-1", 30, "first")
            let! second = api.ScheduleDeprovision("team-x", "admin-1", 30, "second")

            match first, second with
            | Ok a, Ok b ->
                Expect.equal a.JobId b.JobId "same backing job id (no duplicate schedule)"
                Expect.equal b.Reason "first" "the existing pending offboard wins"

                let activeCount =
                    sched.Jobs |> List.filter (fun j -> j.Status = JobStatus.Active) |> List.length

                Expect.equal activeCount 1 "only one active poll job"
            | _ -> failtest "both schedules should succeed"
        }

        testCaseAsync "CancelScheduledDeprovision is idempotent — cancelling nothing is Ok"
        <| async {
            let sched = InMemoryJobScheduler()
            let api = handlerFor "admin-1" (Some(sched :> IJobScheduler))
            let! r = api.CancelScheduledDeprovision "team-never"
            Expect.equal r (Ok()) "cancelling a non-existent schedule is a no-op Ok"
        }

        testCaseAsync "NoJobScheduler — ScheduleDeprovision errors clearly; Get/Cancel degrade gracefully"
        <| async {
            let api = handlerFor "admin-1" None
            let! sched = api.ScheduleDeprovision("team-x", "admin-1", 30, "r")
            let! get = api.GetScheduledDeprovision "team-x"
            let! cancel = api.CancelScheduledDeprovision "team-x"

            match sched with
            | Error msg -> Expect.stringContains msg "IJobScheduler" "clear no-scheduler error"
            | Ok _ -> failtest "expected an error with no scheduler composed"

            Expect.equal get (Ok None) "Get → None with no scheduler"
            Expect.equal cancel (Ok()) "Cancel → Ok with no scheduler"
        }

        testCaseAsync "non-admin caller is refused"
        <| async {
            let sched = InMemoryJobScheduler()

            let nonAdmin =
                let services = ServiceCollection()

                services.AddSingleton<AccessContext>(AccessContext.unrestricted (AuthenticatedUser "u"))
                |> ignore

                services.AddSingleton<ServerConfig>(
                    {
                        ServerConfig.defaults with
                            TenantLifecycle = EnabledTenantLifecycle
                    }
                )
                |> ignore

                services.AddSingleton<IJobScheduler>(sched :> IJobScheduler) |> ignore
                let sp = services.BuildServiceProvider() :> IServiceProvider
                let ctx = DefaultHttpContext() :> HttpContext
                ctx.RequestServices <- sp
                PlatformTenantApiHandler.platformTenantApi ctx

            let! r = nonAdmin.ScheduleDeprovision("team-x", "u", 30, "r")
            Expect.equal r (Error PlatformTenantApiHandler.adminError) "non-admin refused"
        }

        // ─── Poll handler ────────────────────────────────────────────

        testCaseAsync "poll handler — not yet due → no-op Success, poll job stays Active"
        <| async {
            let sched = InMemoryJobScheduler()
            let handler = ScheduledDeprovisionJobHandler.create (servicesWith sched)
            let jobId = Guid.NewGuid()
            // dueAt 10 days out; running now → not due.
            let payload =
                TenantLifecycleAggregator.ScheduledDeprovisionPayload.serialise
                    "team-x"
                    "admin-1"
                    "r"
                    (DateTimeOffset.UtcNow.AddDays 10.0)

            let! result = handler.Execute(jobContext "team-x" jobId DateTime.UtcNow payload)
            Expect.equal result Success "not-due tick is a no-op success"
            Expect.equal sched.TriggeredOnceCount 0 "no offboard fired before the window"
        }

        testCaseAsync "poll handler — due → fires the offboard + retires its own poll job"
        <| async {
            let sched = InMemoryJobScheduler()
            // Pre-register the poll job so the handler can cancel it by id.
            let! scheduled =
                (sched :> IJobScheduler).Schedule {
                    ScopeId = "team-x"
                    Handler = TenantLifecycleAggregator.ScheduledDeprovisionHandlerName
                    Payload = ""
                    Trigger = CronTrigger TenantLifecycleAggregator.ScheduledDeprovisionCron
                    Idempotency = None
                    RetryPolicy = JobRetryPolicy.defaults
                    ShardKey = None
                    Precision = Minute
                    CreatedBy = "admin-1"
                    Tags = Map.empty
                }

            let pollJobId =
                match scheduled with
                | Ok id -> id
                | Error e -> failwithf "setup schedule failed: %A" e

            let handler = ScheduledDeprovisionJobHandler.create (servicesWith sched)
            // dueAt in the past → due now.
            let payload =
                TenantLifecycleAggregator.ScheduledDeprovisionPayload.serialise
                    "team-x"
                    "admin-1"
                    "r"
                    (DateTimeOffset.UtcNow.AddDays -1.0)

            let! result = handler.Execute(jobContext "team-x" pollJobId DateTime.UtcNow payload)
            Expect.equal result Success "due tick succeeds"
            Expect.isGreaterThan sched.TriggeredOnceCount 0 "the offboard was fired (enqueue → TriggerOnce)"

            let! pollJob = (sched :> IJobScheduler).Get("team-x", pollJobId)

            match pollJob with
            | Some j -> Expect.equal j.Status JobStatus.Cancelled "the poll job retired itself"
            | None -> failtest "poll job should still exist (cancelled, not deleted)"
        }
    ]