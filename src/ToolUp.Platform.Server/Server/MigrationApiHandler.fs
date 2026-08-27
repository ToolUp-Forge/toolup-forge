// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.MigrationApiHandler

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

// ─── IDataMigrationApi handler factory (Phase 10a) ───────────────
//
// Builds the `IDataMigrationApi` ToolUp.Remoting handler. Resolves the
// `MigrationRegistry`, `IMigrationStatusStore` and `MigrationRunner`
// lazily from DI per request — same pattern as
// `DataIngestionApiHandler.dataIngestionApi` and
// `JobApiHandler.jobApi`. Each is optional: a deployment running
// `DataMigrations = NoDataMigrations` registers none of them, and
// every method collapses to an empty result or a named refusal rather
// than throwing.
//
// **Scope discipline.** `ListStatuses` and `TriggerMigration` read the
// caller's resolved `AccessContext` and never accept a team id off the
// wire, so a caller cannot read or migrate another tenant's scope
// (GP 4). `ListAllStatuses` is the only cross-team read and is gated
// on `PlatformRole.PlatformAdmin`, returning `[]` rather than throwing
// for everyone else so the admin UI can simply not render the section.
//
// **Write gating.** `TriggerMigration` requires Owner / Admin in
// `Team` / `MultiTeam` mode (`TeamRoles.canWriteTeamConfig`); modes
// with no role concept are ungated, exactly as the ingestion admin
// behaves.

let dataMigrationApi (ctx: HttpContext) : IDataMigrationApi =

    let registry =
        match ctx.RequestServices.GetService(typeof<MigrationRegistry>) with
        | :? MigrationRegistry as r -> Some r
        | _ -> None

    let statusStore =
        match ctx.RequestServices.GetService(typeof<IMigrationStatusStore>) with
        | :? IMigrationStatusStore as s -> Some s
        | _ -> None

    let runner =
        match ctx.RequestServices.GetService(typeof<MigrationRunner>) with
        | :? MigrationRunner as r -> Some r
        | _ -> None

    let accessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ ->
            // Fallback for tests bypassing the scope middleware.
            // Mirrors `DataIngestionApiHandler`'s.
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

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
                        Error
                            $"Only team owners and admins can run data migrations. Your role: {TeamRoles.displayName r}."
                | None -> return Error "You are not a member of this team."
            | _ -> return Error "Team management is not available in this deployment."
        | _ -> return Ok()
    }

    /// Every registered data type's status for one scope, with an
    /// `MigrationIdle` placeholder for pairs no pass has visited — the
    /// admin table wants a row per data type from the first render,
    /// not a table that fills in as passes happen.
    let statusesFor (reg: MigrationRegistry) (store: IMigrationStatusStore) (scopeId: string) = async {
        let! recorded = store.ListForTeam scopeId
        let byDataType = recorded |> List.map (fun s -> s.DataTypeId, s) |> Map.ofList

        return
            reg.DataTypes
            |> List.map (fun dt ->
                match byDataType.TryFind dt.Id with
                | Some status -> {
                    status with
                        TargetVersion = dt.SchemaVersion
                  }
                | None -> MigrationStatus.idle scopeId dt.Id dt.SchemaVersion)
    }

    {
        ListDataTypes =
            fun () -> async {
                return
                    match registry with
                    | Some reg -> reg.DescribeDataTypes()
                    | None -> []
            }

        ListStatuses =
            fun () -> async {
                match registry, statusStore, scopeOpt with
                | Some reg, Some store, Some scope -> return! statusesFor reg store scope.ScopeId
                | _ -> return []
            }

        ListAllStatuses =
            fun () -> async {
                match statusStore with
                | Some store when accessContext.PlatformRole = Some PlatformRole.PlatformAdmin ->
                    return! store.ListAll()
                | _ -> return []
            }

        TriggerMigration =
            fun dataTypeId -> async {
                match registry, runner, scopeOpt with
                | None, _, _
                | _, None, _ -> return Error "Data migrations are not enabled in this deployment."
                | _, _, None -> return Error "Data migrations require a persistent scope (sign in or join a team)."
                | Some reg, Some run, Some scope ->
                    match reg.TryFind dataTypeId with
                    | None -> return Error $"Unknown data type '%s{dataTypeId}'."
                    | Some dataType ->
                        let! rbac = ensureWriteAllowed ()

                        match rbac with
                        | Error msg -> return Error msg
                        | Ok() ->
                            let! status = run.RunForTeam(scope.ScopeId, dataType)
                            return Ok status
            }
    }