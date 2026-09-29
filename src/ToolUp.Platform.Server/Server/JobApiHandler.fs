module ToolUp.Platform.JobApiHandler

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

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
// `JobContext.Scope` with the carried `ScopeId`, as before this phase.

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
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ ->
            // Fallback for tests bypassing the middleware. Mirrors
            // ConfigHandler.configApi's fallback.
            // (Phase 930 dropped an unused team-id read of the storage
            // scope item from here: the fallback is anonymous whatever that
            // item says, and the 797 guard now scans this file for the
            // pre-797 bag read.)
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            AccessContext.unrestricted (AnonymousSession userId)

    let scopeOpt = AccessContext.configScope accessContext

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
                let! role = ts.GetMemberRole(teamId, userId)

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

                        match requestScope with
                        | Some resolved when not resolved.IsAnonymous && resolved.ScopeId = scope.ScopeId ->
                            return! s.Schedule(resolved, safeRegistration)
                        | _ -> return! s.Schedule safeRegistration
            }

        Cancel =
            fun jobId ->
                withSchedulerResult (fun s scopeId -> async {
                    let! rbac = ensureWriteAllowed ()

                    match rbac with
                    | Error msg -> return Error msg
                    | Ok() ->
                        do! s.Cancel(scopeId, jobId)
                        return Ok()
                })

        Disable =
            fun jobId ->
                withSchedulerResult (fun s scopeId -> async {
                    let! rbac = ensureWriteAllowed ()

                    match rbac with
                    | Error msg -> return Error msg
                    | Ok() ->
                        do! s.Disable(scopeId, jobId)
                        return Ok()
                })

        Enable =
            fun jobId ->
                withSchedulerResult (fun s scopeId -> async {
                    let! rbac = ensureWriteAllowed ()

                    match rbac with
                    | Error msg -> return Error msg
                    | Ok() ->
                        do! s.Enable(scopeId, jobId)
                        return Ok()
                })

        TriggerOnce =
            fun jobId ->
                withSchedulerResult (fun s scopeId -> async {
                    let! rbac = ensureWriteAllowed ()

                    match rbac with
                    | Error msg -> return Error msg
                    | Ok() -> return! s.TriggerOnce(scopeId, jobId, accessContext.UserId)
                })
    }