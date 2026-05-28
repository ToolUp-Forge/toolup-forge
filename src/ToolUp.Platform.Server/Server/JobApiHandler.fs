module ToolUp.Platform.JobApiHandler

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

// ─── JobApi handler factory ──────────────────────────────────────
//
// Builds the `JobApi` Fable.Remoting handler. Resolves
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
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            let teamId =
                match ctx.Items.TryGetValue "ToolUp.StorageScope" with
                | true, (:? StorageScope as s) when s.Container.StartsWith "team-" -> Some s.ScopeId
                | _ -> None

            AccessContext.unrestricted (Subject.fromLegacyMode Anonymous userId teamId)

    let scopeOpt = AccessContext.configScope accessContext

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

                        return! s.Schedule safeRegistration
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