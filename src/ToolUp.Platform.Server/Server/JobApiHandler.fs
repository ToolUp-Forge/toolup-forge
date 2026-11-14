module ToolUp.Platform.Jobs.JobApiHandler

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Config.TeamManagement
open ToolUp.Platform.Config

// ─── JobApi handler factory ──────────────────────────────────────
//
// Builds the `JobApi` ToolUp.Remoting handler. Resolves
// `IJobScheduler`, `AccessContext`, and (in Team / MultiTeam mode)
// `ITeamStore` lazily from DI per request. Same pattern as
// `ConfigHandler.configApi` and `AISettingsHandler`.
//
// **Scope discipline.** Every method validates that the caller's
// resolved `AccessContext` produces a `configScope` for the active
// mode. `Anonymous` callers get a clear error rather than a no-op
// — background jobs require a persistent scope to be useful.
//
// **Write gating.** `Schedule` / `Cancel` / `Disable` / `Enable` /
// `TriggerOnce` require Owner / Admin in `Team` / `MultiTeam` mode
// (via `TeamRoles.canWriteTeamConfig`). Read paths (`ListJobs`,
// `GetJob`, `GetRecentRuns`) are ungated within the caller's scope
// — any team member can inspect the team's jobs.
//
// **Typed scheduling (Phase 930).** `Schedule` hands the scheduler the
// scope the platform RESOLVED for this request, through the typed
// `IJobScheduler.Schedule(ResolvedScope, …)` overload (Phase 818), so the
// job runs under that scope rather than the anonymous one. The scope is the
// value the scope-resolution middleware recorded on the request (only a
// `ResolvedScope` is honoured at that key, and only the platform can mint
// one); it is used only when it names the same shard the registration is
// written under. A request the middleware resolved no scope for (a caller
// bypassing it) keeps the string overload, whose job runs anonymous on
// `JobContext.Scope` with the carried `ScopeId`.
//
// **The wire names only a declared handler.** Every handler the platform
// and its companions register is reachable by name on the scheduler, and
// several act on a scope, user or container their payload names. So the
// wire may name a handler only when the deployment declared it
// wire-schedulable (`declareWireSchedulable`); nothing is declared by
// default. `Schedule` refuses an undeclared handler with
// `ScheduleError.HandlerNotRegistered`, the answer an unknown name gets, so
// the wire cannot tell a platform handler from a name nobody registered.
// `TriggerOnce` is not gated by the declaration: it fires an existing job in
// the caller's own scope, choosing neither its handler nor its payload, and a
// handler whose payload names a scope refuses one that is not its job's own
// when it runs (`JobPayloadScope`). And no method acts under the platform
// container (`_platform`): a job registered there may act on any tenant (the
// OAuth token refresh), so that scope is one only the platform's own paths
// can register under.

/// A handler a deployment lets a caller name over the job API, so that
/// `Schedule` registers a job for it. Declare it with
/// `declareWireSchedulable`. Declare only a handler whose payload can name
/// nothing outside the job's own scope, because the caller writes the
/// payload.
type WireSchedulableJobHandler = {
    /// The handler name, exactly as it is registered with the scheduler.
    Handler: string
}

/// Adds `handler` to the handlers a caller may name over the job API. A
/// handler nobody declares is not wire-schedulable.
let declareWireSchedulable (handler: string) (services: IServiceCollection) : IServiceCollection =
    services.AddSingleton<WireSchedulableJobHandler>({ Handler = handler })

/// The handlers `services` declares wire-schedulable.
let wireSchedulable (services: IServiceProvider) : Set<string> =
    match services.GetService(typeof<seq<WireSchedulableJobHandler>>) with
    | :? seq<WireSchedulableJobHandler> as declared -> declared |> Seq.map _.Handler |> Set.ofSeq
    | _ -> Set.empty

/// The `HttpContext.Items` key the scope-resolution middleware records the
/// request's `ResolvedScope` under (`ScopeResolution.ItemsKey`, which
/// compiles after this file). Only a `ResolvedScope` value is honoured there.
[<Literal>]
let private ResolvedScopeItemsKey = "ToolUp.ResolvedScope"

let jobApi (ctx: HttpContext) : JobApi =

    let scheduler =
        match ctx.RequestServices.GetService(typeof<IJobScheduler>) with
        | :? IJobScheduler as s -> Some s
        | _ -> None

    let accessContext =
        AccessContext.tryResolve ctx.RequestServices
        |> Option.defaultWith (fun () -> AccessContext.unresolved "JobApiHandler")

    // A caller whose scope is the platform container is served as one with
    // no scope: a job there may act on any tenant.
    let scopeOpt =
        AccessContext.configScope accessContext
        |> Option.filter (fun scope -> scope.ScopeId <> ScopeKey.PlatformContainer)

    let declared = wireSchedulable ctx.RequestServices

    // Phase 930 — the scope the middleware resolved and recorded for this
    // request. `ScopeResolution.forRequest` compiles after this file, so the
    // record is read here by its key; `ScopeChokePointTests` pins that the
    // two agree. A type test, never a construction: a value that is not a
    // `ResolvedScope` is ignored.
    let requestScope =
        match ctx.Items.TryGetValue ResolvedScopeItemsKey with
        | true, (:? ResolvedScope as scope) -> Some scope
        | _ -> None

    // Team-mode write gate — Owner/Admin only. Mirrors
    // ConfigHandler.ensureWriteAllowed verbatim because the policy
    // is identical: team-scoped writes require admin in Team mode,
    // user-scope users own their own jobs.
    let ensureWriteAllowed () : Async<Result<unit, string>> = async {
        match accessContext.Subject with
        | TeamMember(userId, teamId) ->
            match ctx.RequestServices.GetService(typeof<ITeamStore>) with
            | :? ITeamStore as ts ->
                let! role = ts.GetMemberRole(TeamId teamId, UserId userId)

                match role with
                | Some r when TeamRoles.canWriteTeamConfig r -> return Ok()
                | Some r ->
                    return
                        Error
                            $"Only team owners and admins can manage scheduled jobs. Your role: {TeamRoles.displayName r}."
                | None -> return Error "You are not a member of this team."
            | _ -> return Error "Team management is not available in this deployment."
        | _ -> return Ok()
    }

    /// Phase 1064 — run a status transition, rendering the scheduler's typed
    /// refusal (an in-flight dispatch holds the job's lease, typically asleep
    /// in its retry backoff, or the store did not persist it) as
    /// a retryable `Error` rather than letting the request wait the backoff
    /// out or fail as a 500.
    let transition (work: Async<Result<unit, JobTransitionError>>) : Async<Result<unit, string>> = async {
        let! answered = work
        return Result.mapError JobTransitionError.message answered
    }

    let withSchedulerResult (f: IJobScheduler -> string -> Async<Result<'T, string>>) = async {
        match scheduler, scopeOpt with
        | None, _ -> return Error "Background-job scheduler is not enabled in this deployment."
        | _, None -> return Error "Background jobs require a persistent scope (sign in or join a team)."
        | Some s, Some scope -> return! f s scope.ScopeId
    }

    {
        ListJobs =
            fun () -> async {
                match scheduler, scopeOpt with
                | Some s, Some scope -> return! s.ListJobs scope.ScopeId
                | _ -> return []
            }

        GetJob =
            fun jobId -> async {
                match scheduler, scopeOpt with
                | Some s, Some scope -> return! s.Get(scope.ScopeId, jobId)
                | _ -> return None
            }

        GetRecentRuns =
            fun (jobId, count) -> async {
                match scheduler, scopeOpt with
                | Some s, Some scope -> return! s.GetRecentRuns(scope.ScopeId, jobId, count)
                | _ -> return []
            }

        Schedule =
            fun registration -> async {
                match scheduler, scopeOpt with
                | None, _ ->
                    return
                        Error(
                            ScheduleError.StorageFailure "Background-job scheduler is not enabled in this deployment."
                        )
                | _, None ->
                    return
                        Error(
                            ScheduleError.StorageFailure
                                "Background jobs require a persistent scope (sign in or join a team)."
                        )
                | Some s, Some scope ->
                    let! rbac = ensureWriteAllowed ()

                    match rbac with
                    | Error msg -> return Error(ScheduleError.StorageFailure msg)
                    | Ok() when not (declared.Contains registration.Handler) ->
                        return Error(ScheduleError.HandlerNotRegistered registration.Handler)
                    | Ok() ->
                        // Overwrite caller-supplied scope and creator
                        // with the resolved values from AccessContext.
                        // This prevents impersonation and cross-scope
                        // writes through the wire shape.
                        let safeRegistration = {
                            registration with
                                ScopeId = scope.ScopeId
                                CreatedBy = accessContext.UserId
                        }

                        // The wire answers the job id either way: an admin
                        // registration that coalesced names the job it joined.
                        let! scheduled =
                            match requestScope with
                            | Some resolved when not resolved.IsAnonymous && resolved.ScopeId = scope.ScopeId ->
                                s.Schedule(resolved, safeRegistration)
                            | _ -> s.Schedule safeRegistration

                        return scheduled |> Result.map ScheduleOutcome.jobId
            }

        Cancel =
            fun jobId ->
                withSchedulerResult (fun s scopeId -> async {
                    let! rbac = ensureWriteAllowed ()

                    match rbac with
                    | Error msg -> return Error msg
                    | Ok() -> return! transition (s.Cancel(scopeId, jobId))
                })

        Disable =
            fun jobId ->
                withSchedulerResult (fun s scopeId -> async {
                    let! rbac = ensureWriteAllowed ()

                    match rbac with
                    | Error msg -> return Error msg
                    | Ok() -> return! transition (s.Disable(scopeId, jobId))
                })

        Enable =
            fun jobId ->
                withSchedulerResult (fun s scopeId -> async {
                    let! rbac = ensureWriteAllowed ()

                    match rbac with
                    | Error msg -> return Error msg
                    | Ok() -> return! transition (s.Enable(scopeId, jobId))
                })

        TriggerOnce =
            fun jobId ->
                withSchedulerResult (fun s scopeId -> async {
                    let! rbac = ensureWriteAllowed ()

                    match rbac with
                    | Error msg -> return Error msg
                    | Ok() ->
                        let! triggered = s.TriggerOnce(scopeId, jobId, UserId accessContext.UserId)
                        return triggered |> Result.mapError TriggerError.message
                })
    }