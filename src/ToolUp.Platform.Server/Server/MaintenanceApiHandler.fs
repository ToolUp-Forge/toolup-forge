module ToolUp.Platform.MaintenanceApiHandler

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

// ─── MaintenanceApi handler factory (Phase 9f) ───────────────────
//
// Builds the `MaintenanceApi` Fable.Remoting handler. Owner / Admin
// gated in `Team` / `MultiTeam` mode (mirrors `JobApiHandler`'s
// write gate). The two concrete stores (`PersistentEventStore`,
// `BlobJobStore`) are passed in as constructor closures so this
// handler doesn't have to reach back into DI for non-interface
// types — `compose` already retains typed references for the
// `/dev/inspect` index inspectors and reuses them here.
//
// **Scope discipline.** Both rebuild operations resolve the
// caller's scope from `AccessContext` and pass it to the store —
// the wire shape carries no `scopeId` parameter so a client
// cannot ask to rebuild another team's indexes.

let maintenanceApi
    (eventStoreInspector: unit -> (string -> Async<int>) option)
    (jobStoreInspector: unit -> (string -> Async<int>) option)
    (ctx: HttpContext)
    : MaintenanceApi =

    let accessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ ->
            // Same fallback shape as JobApiHandler — the middleware
            // is the canonical resolver but tests bypass it.
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            let teamId =
                match ctx.Items.TryGetValue "ToolUp.StorageScope" with
                | true, (:? StorageScope as s) when s.Container.StartsWith "team-" -> Some s.ScopeId
                | _ -> None

            AccessContext.unrestricted (AnonymousSession userId)

    let scopeOpt = AccessContext.configScope accessContext

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
                        Error $"Only team owners and admins can rebuild indexes. Your role: {TeamRoles.displayName r}."
                | None -> return Error "You are not a member of this team."
            | _ -> return Error "Team management is not available in this deployment."
        | _ -> return Ok()
    }

    let runRebuild (label: string) (inspector: (string -> Async<int>) option) = async {
        match inspector, scopeOpt with
        | None, _ -> return Error $"{label} is not enabled in this deployment."
        | _, None -> return Error $"{label} requires a persistent scope (sign in or join a team)."
        | Some rebuild, Some scope ->
            let! rbac = ensureWriteAllowed ()

            match rbac with
            | Error msg -> return Error msg
            | Ok() ->
                let! count = rebuild scope.ScopeId
                return Ok count
    }

    {
        RebuildEventIndexes = fun () -> runRebuild "Persistent event store" (eventStoreInspector ())

        RebuildJobIndexes = fun () -> runRebuild "Job scheduler" (jobStoreInspector ())
    }