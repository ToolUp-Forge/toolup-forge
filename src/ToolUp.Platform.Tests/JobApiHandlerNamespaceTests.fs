// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.JobApiHandlerNamespaceTests

open System
open System.Collections.Concurrent
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Config
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose
open ToolUp.Platform.Config.TeamManagement
open ToolUp.Platform.Infra
open ToolUp.Platform.Testing

// ─── The job API schedules only handlers a caller may name ───────────
//
// The wire surface (`JobApi`) used to pass a caller's `Handler` name and
// `Payload` straight to the scheduler, which checks only that the name is
// registered. Every platform handler is registered, and the lifecycle
// handlers act on the scope their payload names, so a team admin could
// schedule `_platform.tenant.lifecycle` with another tenant's scope and run
// that tenant's offboard. Two layers close it, and each has its tests here:
//
//   * the wire schedules only a handler the deployment declared
//     wire-schedulable, and never acts under the platform container
//     (`TriggerOnce` fires an existing job in the caller's scope);
//   * a handler whose payload names a scope refuses one that is not the
//     job's own, so a registration forged in the store is refused at
//     `Execute` too.
//
// A third rule keeps a tenant from stopping platform work in its own scope:
// a handler declared protected has `Cancel`, `Disable` and `Enable` refused
// for every caller but a platform administrator.

// ─── A scheduler that records what reaches it ────────────────────────

type private RecordingScheduler() =
    let jobs = ConcurrentDictionary<JobId, JobDefinition>()
    let triggered = ConcurrentQueue<string * JobId>()
    let scheduled = ConcurrentQueue<JobRegistration>()
    let transitions = ConcurrentQueue<string * string * JobId>()

    member _.Scheduled = List.ofSeq scheduled
    member _.Triggered = List.ofSeq triggered

    /// Every status transition that reached the scheduler: verb, scope, job.
    member _.Transitions = List.ofSeq transitions

    /// Seed a job as if a registration had already been stored.
    member _.Seed(scopeId: string, handler: string, payload: string) : JobId =
        let id = Guid.NewGuid()

        jobs[id] <- {
            JobId = id
            ScopeId = scopeId
            Handler = handler
            Payload = payload
            Trigger = Manual
            Idempotency = None
            RetryPolicy = JobRetryPolicy.defaults
            ShardKey = None
            Precision = Minute
            Status = JobStatus.Active
            CreatedAt = DateTime.UtcNow
            CreatedBy = "seed"
            NextRunAt = None
            LastRunAt = None
            LastRunStatus = None
            LastRunError = None
            ConsecutiveFailures = 0
            Tags = Map.empty
            LastRunWarning = None
        }

        id

    interface IJobScheduler with
        member _.RegisterHandler(_, _) = ()
        member _.RegisterHandlerAsync(_, _) = async { return Ok() }

        member this.Schedule(scope: ResolvedScope, registration: JobRegistration) =
            (this :> IJobScheduler).Schedule {
                registration with
                    ScopeId = scope.ScopeId
            }

        member this.Schedule(registration: JobRegistration) = async {
            scheduled.Enqueue registration
            let id = this.Seed(registration.ScopeId, registration.Handler, registration.Payload)
            return Ok(Scheduled id)
        }

        member _.Cancel(scopeId, jobId) = async {
            transitions.Enqueue("cancel", scopeId, jobId)
            return Ok()
        }

        member _.Disable(scopeId, jobId) = async {
            transitions.Enqueue("disable", scopeId, jobId)
            return Ok()
        }

        member _.Enable(scopeId, jobId) = async {
            transitions.Enqueue("enable", scopeId, jobId)
            return Ok()
        }

        member _.Get(scopeId, jobId) = async {
            match jobs.TryGetValue jobId with
            | true, job when job.ScopeId = scopeId -> return Some job
            | _ -> return None
        }

        member _.ListJobs(scopeId) = async {
            return jobs.Values |> Seq.filter (fun j -> j.ScopeId = scopeId) |> List.ofSeq
        }

        member _.GetRecentRuns(_, _, _) = async { return [] }

        member _.TriggerOnce(scopeId, jobId, _) = async {
            triggered.Enqueue(scopeId, jobId)
            return Ok()
        }

        member _.NotifyEventWritten(_, _, _) = async { return () }

// ─── The caller ──────────────────────────────────────────────────────

let private attackerTeam = "team-attacker"
let private victimTeam = "team-victim"

/// A team store in which every member holds `role`: the caller passes the
/// job API's write gate exactly when `role` may write team config.
let private teamStoreWith (role: TeamRole) : ITeamStore =
    { new ITeamStore with
        member _.GetMemberRole(_, _) = async { return Some role }
        member _.ListTeams() = failwith "not used"
        member _.GetTeam _ = failwith "not used"
        member _.CreateTeam(_, _) = failwith "not used"
        member _.DeleteTeam _ = failwith "not used"
        member _.AddMember(_, _, _) = failwith "not used"
        member _.RemoveMember(_, _) = failwith "not used"
        member _.ChangeMemberRole(_, _, _) = failwith "not used"
        member _.GetTeamsForUser _ = failwith "not used"
        member _.GetTeamMembers _ = failwith "not used"
        member _.GetActiveTeam _ = failwith "not used"
        member _.SetActiveTeam(_, _) = failwith "not used"
        member _.SetArchived(_, _) = failwith "not used"
        member _.PurgeTeam _ = failwith "not used"
        member _.PurgeUser _ = failwith "not used"
    }

let private jobApiFor
    (access: AccessContext)
    (scheduler: IJobScheduler)
    (configure: IServiceCollection -> unit)
    : JobApi =
    let services = ServiceCollection()
    services.AddSingleton<AccessContext>(access) |> ignore
    services.AddSingleton<IJobScheduler>(scheduler) |> ignore
    services.AddSingleton<ITeamStore>(teamStoreWith TeamRole.Admin) |> ignore
    configure services
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- services.BuildServiceProvider()
    JobApiHandler.jobApi ctx

/// An admin of the attacker team: a member-scoped caller who passes the
/// job API's write gate.
let private teamAdmin = AccessContexts.teamMember "mallory" attackerTeam

let private registration (handler: string) (payload: string) : JobRegistration = {
    ScopeId = victimTeam
    Handler = handler
    Payload = payload
    Trigger = Manual
    Idempotency = None
    RetryPolicy = JobRetryPolicy.defaults
    ShardKey = None
    Precision = Minute
    CreatedBy = "mallory"
    Tags = Map.empty
}

let private victimOffboard =
    TenantLifecycleAggregator.LifecycleJobPayload.serialise Deprovisioning victimTeam "mallory"

let private victimGraceOffboard =
    TenantLifecycleAggregator.ScheduledDeprovisionPayload.serialise
        victimTeam
        "mallory"
        "no reason"
        (DateTimeOffset.UtcNow.AddDays -1.0)

// ─── A job context ───────────────────────────────────────────────────

let private jobContext (scopeId: string) (payload: string) : JobContext = {
    JobId = Guid.NewGuid()
    ScopeId = scopeId
    Scope = ResolvedScope.anonymous
    AccessContext = AccessContexts.system "job"
    Attempt = 1
    Trigger = Manual
    TriggerSource = ScheduledManually(UserId "mallory")
    ScheduledAt = DateTime.UtcNow
    RunningAt = DateTime.UtcNow
    Payload = payload
    DeadLetterDestination = None
}

/// A deprovision hook that records every scope it is run against.
type private RecordingHook() =
    let ran = ConcurrentQueue<string>()
    member _.Ran = List.ofSeq ran

    interface ITenantLifecycle with
        member _.Name = "recording"
        member _.OnProvisioned(_, _) = async { return LifecycleHookResult.Completed }

        member _.OnDeprovisioned(scopeId, _) = async {
            ran.Enqueue scopeId
            return LifecycleHookResult.Completed
        }

let private refusedForScope (what: string) (result: JobResult) =
    match result with
    | PermanentFailure reason ->
        Expect.stringContains reason "runs under scope" $"{what}: the refusal names the job's own scope"
    | other -> failtestf "%s: expected a permanent refusal naming the scope, got %A" what other

let private wireRefusals =
    testList "the wire cannot name a platform handler" [

        testCaseAsync "a team admin cannot schedule the lifecycle handler against another tenant"
        <| async {
            let scheduler = RecordingScheduler()
            let api = jobApiFor teamAdmin scheduler ignore

            let! answered = api.Schedule(registration TenantLifecycleAggregator.LifecycleJobHandlerName victimOffboard)

            Expect.equal
                answered
                (Error(ScheduleError.HandlerNotRegistered TenantLifecycleAggregator.LifecycleJobHandlerName))
                "a platform handler is not one the wire may name"

            Expect.isEmpty scheduler.Scheduled "nothing reached the scheduler"
        }

        testCaseAsync "a team admin cannot schedule the grace-period offboard poll"
        <| async {
            let scheduler = RecordingScheduler()
            let api = jobApiFor teamAdmin scheduler ignore

            let! answered =
                api.Schedule(registration TenantLifecycleAggregator.ScheduledDeprovisionHandlerName victimGraceOffboard)

            Expect.equal
                answered
                (Error(ScheduleError.HandlerNotRegistered TenantLifecycleAggregator.ScheduledDeprovisionHandlerName))
                "a platform handler is not one the wire may name"

            Expect.isEmpty scheduler.Scheduled "nothing reached the scheduler"
        }

        // `TriggerOnce` is not gated by the declaration: a forged job already
        // in the store fires, and the handler refuses it when it runs.
        testCaseAsync "a stored job naming another tenant fires, and is refused when it runs"
        <| async {
            let scheduler = RecordingScheduler()

            let jobId =
                scheduler.Seed(attackerTeam, TenantLifecycleAggregator.LifecycleJobHandlerName, victimOffboard)

            let api = jobApiFor teamAdmin scheduler ignore
            let! answered = api.TriggerOnce jobId
            Expect.isOk answered "the trigger reaches the scheduler"

            let! stored = (scheduler :> IJobScheduler).Get(attackerTeam, jobId)
            let job = stored |> Option.defaultWith (fun () -> failtest "the job is stored")
            let hook = RecordingHook()
            let services = ServiceCollection()
            services.AddSingleton<ITenantLifecycle>(hook) |> ignore
            let handler = LifecycleJobHandler.create (services.BuildServiceProvider())

            let! result =
                handler.Execute {
                    (jobContext job.ScopeId job.Payload) with
                        JobId = job.JobId
                }

            refusedForScope "a fired forged lifecycle job" result
            Expect.isEmpty hook.Ran "no deprovision hook ran against any scope"
        }
    ]

let private executeRefusals =
    testList "a lifecycle handler acts only on its job's own scope" [

        testCaseAsync "the lifecycle handler refuses a payload naming another tenant and runs no hook"
        <| async {
            let hook = RecordingHook()
            let services = ServiceCollection()
            services.AddSingleton<ITenantLifecycle>(hook) |> ignore
            let handler = LifecycleJobHandler.create (services.BuildServiceProvider())
            let! result = handler.Execute(jobContext attackerTeam victimOffboard)
            refusedForScope "lifecycle" result
            Expect.isEmpty hook.Ran "no deprovision hook ran against any scope"
        }

        testCaseAsync "the grace-period poll refuses a payload naming another tenant and fires nothing"
        <| async {
            let scheduler = RecordingScheduler()
            let services = ServiceCollection()
            services.AddSingleton<IJobScheduler>(scheduler) |> ignore

            let handler =
                ScheduledDeprovisionJobHandler.create (services.BuildServiceProvider())

            let! result = handler.Execute(jobContext attackerTeam victimGraceOffboard)
            refusedForScope "scheduled deprovision" result
            Expect.isEmpty scheduler.Scheduled "no offboard was enqueued"
            Expect.isEmpty scheduler.Triggered "no offboard was fired"
        }
    ]

// ─── Every other handler the census found trusting a payload scope ────
//
// Each runs with null substrate: a handler that compares first refuses
// before it touches any, and one that does not fails on the null.

let private storagePayloadRefusals =
    testList "a handler that reads a scope from its payload refuses a foreign one" [

        testCaseAsync "provider status probe"
        <| async {
            let handler =
                ProviderStatusProbeJobHandler.ProviderStatusProbeJobHandler(
                    Unchecked.defaultof<_>,
                    Unchecked.defaultof<_>,
                    Logger.silent
                )
                :> IJobHandler

            let payload: ProviderStatusProbeJobHandler.ProviderStatusProbePayload = {
                ScopeId = victimTeam
                Container = "team-" + victimTeam
                Persist = true
            }

            let! result = handler.Execute(jobContext attackerTeam (JobPayload.serialise payload))
            refusedForScope "provider status probe" result
        }

        testCaseAsync "provider OAuth refresh"
        <| async {
            let handler =
                ProviderOAuthRefreshJobHandler.ProviderOAuthRefreshJobHandler(
                    [],
                    Unchecked.defaultof<_>,
                    Unchecked.defaultof<_>,
                    Unchecked.defaultof<_>,
                    Logger.silent
                )
                :> IJobHandler

            let payload: ProviderOAuthRefreshJobHandler.ProviderOAuthRefreshPayload = {
                ScopeId = victimTeam
                Container = "team-" + victimTeam
                Persist = true
                EntryLabel = "entry"
                FlowName = "flow"
            }

            let! result =
                handler.Execute(
                    jobContext
                        attackerTeam
                        (ProviderOAuthRefreshJobHandler.ProviderOAuthRefreshPayload.serialize payload)
                )

            refusedForScope "provider OAuth refresh" result
        }

        testCaseAsync "report render"
        <| async {
            let handler =
                ToolUp.Reporting.ReportingAITools.renderJobHandler Unchecked.defaultof<_> Unchecked.defaultof<_>

            let payload: ToolUp.Reporting.ReportingAITools.RenderJobPayload = {
                TemplateId = "template"
                ScopeId = victimTeam
                UserId = UserId "mallory"
                ConversationId = None
                Values = Map.empty
            }

            let! result =
                handler.Execute(
                    jobContext attackerTeam (ToolUp.Reporting.ReportingAITools.RenderJobPayload.encode payload)
                )

            refusedForScope "report render" result
        }

        testCaseAsync "asset derivative"
        <| async {
            let handler =
                ToolUp.AssetStore.DerivativeJobHandler(
                    Unchecked.defaultof<_>,
                    Unchecked.defaultof<_>,
                    Unchecked.defaultof<_>,
                    None,
                    Logger.silent,
                    3
                )
                :> IJobHandler

            let payload: ToolUp.AssetStore.DerivativeJobPayload = {
                ScopeContainer = victimTeam
                AssetId = "asset"
                ContentHash = "hash"
                ProfileId = "profile"
                DerivativeName = "thumb"
                InputMime = "image/png"
            }

            let! result = handler.Execute(jobContext attackerTeam (JobPayload.serialise payload))
            refusedForScope "asset derivative" result
        }
    ]

let private fitRequest (scope: string) : FitRequest = {
    ScopeId = scope
    DatasetVersion = {
        ScopeId = scope
        DatasetId = "sales-panel"
        Version = 3
    }
    SpecRef = ModelSpecRef.ofPayload """{"opaque":"provider-spec"}"""
    ProviderKind = ToolUp.ModelProviders.Reference.ReferenceModelFitProvider.Kind
    Seed = 7L
    Gates = []
    SubmitterClass = SubmitterClass.Human
}

let private retryDeps: ToolUp.RAG.IngestionService.IngestionRetryDeps = {
    Pipeline = Unchecked.defaultof<_>
    EventStore = Unchecked.defaultof<_>
    Observers = []
    NotificationChannel = None
    Telemetry = Unchecked.defaultof<_>
    Logger = Logger.silent
    Policy = {
        ToolUp.RAG.IngestionTypes.IngestionRetryPolicy.defaults with
            InitialBackoff = TimeSpan.Zero
            MaxBackoff = TimeSpan.Zero
            JitterFactor = 0.0
    }
    AlertState = ToolUp.RAG.IngestionService.IngestionAlertState()
}

let private retryPayload
    (scopeId: string)
    (container: string)
    (vectorScope: VectorKnowledgeTypes.VectorScope)
    : ToolUp.RAG.IngestionTypes.IngestionRetryPayload =
    {
        DocumentId = "doc"
        DocumentName = "doc.pdf"
        ChunkId = "doc:chunk:0"
        ChunkIndex = 0
        Chunk = { Content = "x"; Metadata = Map.empty }
        Scope = vectorScope
        ScopeId = scopeId
        Container = container
        OriginatingUserId = None
        Attempt = None
    }

let private moreStoragePayloadRefusals =
    testList "the other payload-scope handlers refuse a foreign scope" [

        testCaseAsync "OAuth token refresh, run under a tenant's scope"
        <| async {
            let handler =
                OAuthRefreshJobHandler.create
                    Unchecked.defaultof<_>
                    Unchecked.defaultof<_>
                    Unchecked.defaultof<_>
                    Logger.silent

            let descriptor =
                OAuthRefreshDescriptor.withDefaults
                    "google"
                    "config"
                    victimTeam
                    "https://token"
                    "client"
                    "secret"
                    "refresh"

            let! result =
                handler.Execute(jobContext attackerTeam (InProcessOAuthTokenRefresher.serializeDescriptor descriptor))

            refusedForScope "OAuth token refresh" result
        }

        testCaseAsync "model fit"
        <| async {
            let handler =
                ModelFitJobHandler.create Unchecked.defaultof<_> Unchecked.defaultof<_> Logger.silent

            let! result = handler.Execute(jobContext attackerTeam (JobPayload.serialise (fitRequest victimTeam)))
            refusedForScope "model fit" result
        }

        testCaseAsync "model fit batch item"
        <| async {
            let handler =
                ModelFitBatchItemJobHandler.create
                    Unchecked.defaultof<_>
                    Unchecked.defaultof<_>
                    Unchecked.defaultof<_>
                    Logger.silent

            let item: FitBatchItemPayload = {
                BatchId = "batch"
                Index = 0
                Request = fitRequest victimTeam
            }

            let! result = handler.Execute(jobContext attackerTeam (JobPayload.serialise item))
            refusedForScope "model fit batch item" result
        }

        testCaseAsync "RAG ingestion retry naming another tenant's scope"
        <| async {
            let handler = ToolUp.RAG.IngestionRetryJobHandler.create retryDeps

            let payload =
                retryPayload victimTeam victimTeam (VectorKnowledgeTypes.VectorScope.User victimTeam)

            let! result = handler.Execute(jobContext attackerTeam (JobPayload.serialise payload))
            refusedForScope "RAG ingestion retry" result
        }

        testCaseAsync "RAG ingestion retry naming its own scope but another vector scope"
        <| async {
            let handler = ToolUp.RAG.IngestionRetryJobHandler.create retryDeps

            let payload =
                retryPayload attackerTeam attackerTeam (VectorKnowledgeTypes.VectorScope.Team "victim")

            let! result = handler.Execute(jobContext attackerTeam (JobPayload.serialise payload))

            match result with
            | PermanentFailure reason ->
                Expect.stringContains reason "vector scope" "the refusal names the vector scope"
            | other -> failtestf "expected a permanent refusal naming the vector scope, got %A" other
        }
    ]

// ─── The comparison itself ───────────────────────────────────────────

let private helperTests =
    testList "JobPayloadScope" [

        test "a payload naming the job's own scope is admitted" {
            Expect.isOk (JobPayloadScope.requireOwn (jobContext attackerTeam "") attackerTeam) "own scope"
        }

        test "a payload naming another scope is refused" {
            Expect.isError (JobPayloadScope.requireOwn (jobContext attackerTeam "") victimTeam) "another scope"
        }

        test "a job under the platform container may name a tenant, and only there" {
            Expect.isOk
                (JobPayloadScope.requireOwnOrPlatform (jobContext ScopeKey.PlatformContainer "") victimTeam)
                "the platform container acts on a tenant"

            Expect.isError
                (JobPayloadScope.requireOwnOrPlatform (jobContext attackerTeam "") victimTeam)
                "a tenant's job does not"
        }

        test "a storage scope whose container names another principal is refused" {
            let ctx = jobContext "attacker" ""

            Expect.isOk
                (JobPayloadScope.requireOwnStorage ctx (StorageScope.ofKey true (ScopeKey.Team(TeamId "attacker"))))
                "the scope's own container"

            Expect.isError
                (JobPayloadScope.requireOwnStorage ctx {
                    ScopeId = "attacker"
                    Container = "team-victim"
                    Persist = true
                })
                "a container naming another team"
        }
    ]

// ─── What still runs ─────────────────────────────────────────────────

let private declaredHandler = "sales.daily-rollup"

let private stillRuns =
    testList "what the wire and the platform may still do" [

        testCaseAsync "a declared handler schedules, under the caller's own scope"
        <| async {
            let scheduler = RecordingScheduler()

            let api =
                jobApiFor teamAdmin scheduler (JobApiHandler.declareWireSchedulable declaredHandler >> ignore)

            let! answered = api.Schedule(registration declaredHandler "")
            Expect.isOk answered "a declared handler schedules"

            match scheduler.Scheduled with
            | [ stored ] -> Expect.equal stored.ScopeId attackerTeam "the job is registered under the caller's scope"
            | other -> failtestf "expected one registration, got %A" other
        }

        testCaseAsync "declaring one handler leaves every other undeclared"
        <| async {
            let scheduler = RecordingScheduler()

            let api =
                jobApiFor teamAdmin scheduler (JobApiHandler.declareWireSchedulable declaredHandler >> ignore)

            let! answered = api.Schedule(registration TenantLifecycleAggregator.LifecycleJobHandlerName victimOffboard)

            Expect.equal
                answered
                (Error(ScheduleError.HandlerNotRegistered TenantLifecycleAggregator.LifecycleJobHandlerName))
                "the lifecycle handler stays unreachable"
        }

        testCaseAsync "the jobs admin still re-runs a platform job in the caller's scope"
        <| async {
            let scheduler = RecordingScheduler()
            let jobId = scheduler.Seed(attackerTeam, "_platform.dataingestion.run", "")
            let api = jobApiFor teamAdmin scheduler ignore
            let! answered = api.TriggerOnce jobId
            Expect.isOk answered "an undeclared handler's existing job fires"
            Expect.equal scheduler.Triggered [ attackerTeam, jobId ] "it fired once, in the caller's scope"
        }

        testCaseAsync "a caller whose scope is the platform container schedules nothing"
        <| async {
            let scheduler = RecordingScheduler()

            let api =
                jobApiFor
                    (AccessContexts.user ScopeKey.PlatformContainer)
                    scheduler
                    (JobApiHandler.declareWireSchedulable declaredHandler >> ignore)

            let! answered = api.Schedule(registration declaredHandler "")
            Expect.isError answered "no job is registered under the platform container from the wire"
            Expect.isEmpty scheduler.Scheduled "nothing reached the scheduler"
            let! listed = api.ListJobs()
            Expect.isEmpty listed "nor are the platform's jobs listed to it"
        }

        testCaseAsync "the platform's own offboard still runs end to end"
        <| async {
            let scheduler = RecordingScheduler()
            let hook = RecordingHook()

            let! handle =
                TenantLifecycleAggregator.enqueue (scheduler :> IJobScheduler) Deprovisioning victimTeam "admin"

            let jobId =
                match handle with
                | Ok h -> h.JobId
                | Error e -> failtestf "the platform's enqueue was refused: %s" e

            let! stored = (scheduler :> IJobScheduler).Get(victimTeam, jobId)

            let job =
                stored |> Option.defaultWith (fun () -> failtest "the enqueued job is stored")

            let services = ServiceCollection()
            services.AddSingleton<ITenantLifecycle>(hook) |> ignore
            let handler = LifecycleJobHandler.create (services.BuildServiceProvider())

            let! result =
                handler.Execute {
                    (jobContext job.ScopeId job.Payload) with
                        JobId = job.JobId
                }

            Expect.equal result Success "the platform's offboard runs"
            Expect.equal hook.Ran [ victimTeam ] "against the scope it was enqueued for"
        }

        testCaseAsync "the platform's grace-period offboard still fires, against its own scope"
        <| async {
            let scheduler = RecordingScheduler()

            let! pending =
                TenantLifecycleAggregator.scheduleDeprovision
                    (scheduler :> IJobScheduler)
                    victimTeam
                    "admin"
                    0
                    "closing"

            let jobId =
                match pending with
                | Ok p -> Guid.Parse p.JobId
                | Error e -> failtestf "the platform's grace-period schedule was refused: %s" e

            let! stored = (scheduler :> IJobScheduler).Get(victimTeam, jobId)

            let job = stored |> Option.defaultWith (fun () -> failtest "the poll job is stored")

            let services = ServiceCollection()
            services.AddSingleton<IJobScheduler>(scheduler) |> ignore

            let handler =
                ScheduledDeprovisionJobHandler.create (services.BuildServiceProvider())

            let! result =
                handler.Execute {
                    (jobContext job.ScopeId job.Payload) with
                        JobId = job.JobId
                        RunningAt = DateTime.UtcNow.AddMinutes 1.0
                }

            Expect.equal result Success "the poll fires"

            let offboards =
                scheduler.Scheduled
                |> List.filter (fun r -> r.Handler = TenantLifecycleAggregator.LifecycleJobHandlerName)

            match offboards with
            | [ offboard ] -> Expect.equal offboard.ScopeId victimTeam "the offboard is enqueued for its own scope"
            | other -> failtestf "expected one offboard enqueued, got %A" other
        }
    ]

// ─── A protected handler's job is the platform's to stop ─────────────

let private protectedHandler =
    TenantLifecycleAggregator.ScheduledDeprovisionHandlerName

let private declareProtected (services: IServiceCollection) =
    JobApiHandler.declareProtected protectedHandler services |> ignore

let private platformAdmin = {
    teamAdmin with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

/// Each of the three stop-and-start verbs, by the name the scheduler records.
let private verbs: (string * (JobApi -> Guid -> Async<Result<unit, string>>)) list = [
    "cancel", (fun api jobId -> api.Cancel jobId)
    "disable", (fun api jobId -> api.Disable jobId)
    "enable", (fun api jobId -> api.Enable jobId)
]

let private protectedHandlers =
    testList "a protected handler's job is stopped by a platform administrator only" [

        for verb, call in verbs do
            testCaseAsync $"a team admin cannot {verb} a protected handler's job in their own scope"
            <| async {
                let scheduler = RecordingScheduler()
                let jobId = scheduler.Seed(attackerTeam, protectedHandler, victimGraceOffboard)
                let api = jobApiFor teamAdmin scheduler declareProtected
                let! answered = call api jobId

                Expect.equal
                    answered
                    (Error JobApiHandler.ProtectedHandlerRefusal)
                    "the refusal is the published answer, not an exception"

                Expect.isEmpty scheduler.Transitions "nothing reached the scheduler"
            }

            testCaseAsync $"a platform administrator can {verb} a protected handler's job"
            <| async {
                let scheduler = RecordingScheduler()
                let jobId = scheduler.Seed(attackerTeam, protectedHandler, victimGraceOffboard)
                let api = jobApiFor platformAdmin scheduler declareProtected
                let! answered = call api jobId
                Expect.isOk answered "a platform administrator manages the job"
                Expect.equal scheduler.Transitions [ verb, attackerTeam, jobId ] "the transition ran once, in scope"
            }

            testCaseAsync $"a team admin can still {verb} an unprotected job, exactly as before"
            <| async {
                let scheduler = RecordingScheduler()
                let jobId = scheduler.Seed(attackerTeam, declaredHandler, "")
                let api = jobApiFor teamAdmin scheduler declareProtected
                let! answered = call api jobId
                Expect.isOk answered "an unprotected job is managed as before"
                Expect.equal scheduler.Transitions [ verb, attackerTeam, jobId ] "the transition ran once, in scope"
            }

            testCaseAsync $"a protected job id from another scope answers a {verb} as it does today"
            <| async {
                let scheduler = RecordingScheduler()
                let jobId = scheduler.Seed(victimTeam, protectedHandler, victimGraceOffboard)
                let api = jobApiFor teamAdmin scheduler declareProtected
                let! answered = call api jobId

                // Today the job id reaches the scheduler under the caller's own
                // scope, which answers it as it answers any id it does not hold
                // there; the protected rule must neither refuse it (which would
                // reveal that the id exists elsewhere) nor reach another scope.
                Expect.isOk answered "the answer is the scheduler's, as for any id"
                Expect.equal scheduler.Transitions [ verb, attackerTeam, jobId ] "under the caller's own scope only"
            }

        testCase "a handler cannot be both wire-schedulable and protected"
        <| fun () ->
            let protectedFirst = ServiceCollection()
            declareProtected protectedFirst

            Expect.throws
                (fun () -> JobApiHandler.declareWireSchedulable protectedHandler protectedFirst |> ignore)
                "declaring a protected handler wire-schedulable is refused at composition"

            let wireFirst = ServiceCollection()
            JobApiHandler.declareWireSchedulable protectedHandler wireFirst |> ignore

            Expect.throws
                (fun () -> declareProtected wireFirst)
                "declaring a wire-schedulable handler protected is refused at composition"

        testCase "the platform's tenant offboard handlers are declared protected where they are registered"
        <| fun () ->
            let services = ServiceCollection()

            ComposeTenantLifecycle.registerTenantLifecycle services {
                ServerConfig.defaults with
                    TenantLifecycle = FeatureMode.Enabled
            }

            let declared =
                services
                |> Seq.choose (fun d ->
                    match d.ImplementationInstance with
                    | :? JobApiHandler.ProtectedJobHandler as h -> Some h.Handler
                    | _ -> None)
                |> Set.ofSeq

            Expect.equal
                declared
                (set [
                    TenantLifecycleAggregator.LifecycleJobHandlerName
                    TenantLifecycleAggregator.ScheduledDeprovisionHandlerName
                ])
                "the lifecycle and grace-period poll handlers are protected"
    ]

// ─── The companions declare their tenant-scoped handlers ─────────────
//
// The declared-delivery watch (data-sources) and the Google and Microsoft
// renewal jobs (calendar) run under the tenant scopes a deployment lists.
// A deployment that composes the companion and declares nothing of its own
// gets the protection: each composition declares its handler.

/// A bridge the renewal compositions only close over; no member is called.
let private unusedBridge: ToolUp.Scheduling.ICalendarBridge.ICalendarBridge =
    { new ToolUp.Scheduling.ICalendarBridge.ICalendarBridge with
        member _.Kind = "unused"
        member _.Capabilities = failwith "not used"
        member _.LinkResource _ = failwith "not used"
        member _.UnlinkResource _ = failwith "not used"
        member _.Push(_, _, _) = failwith "not used"
        member _.Pull(_, _, _) = failwith "not used"
        member _.HandleWebhook(_, _) = failwith "not used"
    }

/// Applies a composed app's service configuration, as `ServerApp.run` does.
let private composedFrom (app: ServerApp) (services: IServiceCollection) =
    match ServerApp.serviceConfig app with
    | Some configure -> configure services |> ignore
    | None -> failtest "the composition registers nothing"

let private renewalCron = CronTrigger "0 * * * *"

/// Each companion family: its handler, and the companion composed with no
/// declaration of the deployment's own.
let private companionFamilies: (string * string * (IServiceCollection -> unit)) list = [
    "the declared-delivery watch",
    ToolUp.DataSources.Common.DeclaredDelivery.WatchJobHandlerName,
    (fun services ->
        ToolUp.DataSources.Common.DeclaredDelivery.withDeclaredDataSources
            ServerConfig.defaults
            { Principals = []; Sources = [] }
            ToolUp.DataSources.Common.DeclaredDelivery.DefaultWatchPoll
            services
        |> ignore)

    "the Google channel renewal",
    ToolUp.Calendar.GoogleCalendarChannels.RenewalHandlerName,
    composedFrom (
        ServerApp.empty
        |> ToolUp.Calendar.GoogleCalendarChannels.withChannelRenewal unusedBridge [ attackerTeam ] renewalCron
    )

    "the Microsoft subscription renewal",
    ToolUp.Calendar.MicrosoftGraphSubscriptions.RenewalHandlerName,
    composedFrom (
        ServerApp.empty
        |> ToolUp.Calendar.MicrosoftGraphSubscriptions.withSubscriptionRenewal unusedBridge [ attackerTeam ] renewalCron
    )
]

let private companionHandlers =
    testList "a companion's tenant-scoped job is stopped by a platform administrator only" [

        for family, handler, compose in companionFamilies do
            for verb, call in verbs do
                testCaseAsync $"a team admin cannot {verb} {family} job in a deployment declaring nothing"
                <| async {
                    let scheduler = RecordingScheduler()
                    let jobId = scheduler.Seed(attackerTeam, handler, "")
                    let api = jobApiFor teamAdmin scheduler compose
                    let! answered = call api jobId

                    Expect.equal
                        answered
                        (Error JobApiHandler.ProtectedHandlerRefusal)
                        "the companion declared its handler protected"

                    Expect.isEmpty scheduler.Transitions "nothing reached the scheduler"
                }

                testCaseAsync $"a platform administrator can {verb} {family} job"
                <| async {
                    let scheduler = RecordingScheduler()
                    let jobId = scheduler.Seed(attackerTeam, handler, "")
                    let api = jobApiFor platformAdmin scheduler compose
                    let! answered = call api jobId
                    Expect.isOk answered "a platform administrator manages the job"
                    Expect.equal scheduler.Transitions [ verb, attackerTeam, jobId ] "the transition ran once, in scope"
                }

            testCase $"{family} cannot also be declared wire-schedulable"
            <| fun () ->
                let services = ServiceCollection()
                compose services

                Expect.throws
                    (fun () -> JobApiHandler.declareWireSchedulable handler services |> ignore)
                    "the companion's protected handler is refused as wire-schedulable"
    ]

let tests =
    testList "The job API schedules only handlers a caller may name" [
        wireRefusals
        executeRefusals
        storagePayloadRefusals
        moreStoragePayloadRefusals
        helperTests
        stillRuns
        protectedHandlers
        companionHandlers
    ]