// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 1107 — the server half of `IPublicationReaderApi`: an Owner or
/// Admin of a publishing scope lists, grants and revokes the scope's
/// in-platform publication readers.
///
/// Gates, every method, in one place (`authorised`): the caller is a
/// `TeamMember` (the active team IS the publishing scope — a share-token
/// bearer, an anonymous session, a publication reader and a member in no
/// team are all refused); its team role passes `TeamRoles.canWriteTeamConfig`
/// (Owner or Admin). The platform-admin role is not a bypass here, exactly
/// as it is not a bypass of the `Publication` audience it administers.
/// A grant must name a role the scope's publications declare
/// (`IPublicationReaderRoleSource`). Each grant and revocation is audited
/// under the publishing scope, awaited before the response.
module ToolUp.Platform.PublicationReaderApiHandler

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

/// The handler's one read of the request's container: the per-request
/// `AccessContext` and the substrate below are request-scoped, and
/// `Api.make`'s builder is handed only the `HttpContext`, so they cannot be
/// passed in from a registration factory. Every lookup goes through here.
let private service<'T when 'T: not struct> (ctx: HttpContext) : 'T option =
    match ctx.RequestServices.GetService(typeof<'T>) with
    | :? 'T as s -> Some s
    | _ -> None

/// The roles the scope's publications declare, distinct and ordered; none
/// when no role source is composed.
let private declaredRoles (ctx: HttpContext) (scope: string) : Async<string list> = async {
    match service<IPublicationReaderRoleSource> ctx with
    | Some source ->
        let! roles = source.DeclaredRoles scope
        return roles |> List.distinct |> List.sort
    | None -> return []
}

let publicationReaderApi (ctx: HttpContext) : IPublicationReaderApi =
    let accessContext =
        service<AccessContext> ctx
        |> Option.defaultWith (fun () -> AccessContext.unrestricted (AnonymousSession "anonymous"))

    let authorised (f: IPublicationReaderGrantStore -> string -> Async<Result<'T, string>>) = async {
        match service<IPublicationReaderGrantStore> ctx with
        | None -> return Error "Publication readers are not enabled in this deployment."
        | Some store ->
            match accessContext.Subject with
            | TeamMember(userId, teamId) when not (AccessContext.isPublicationReader accessContext) ->
                match service<ITeamStore> ctx with
                | Some teams ->
                    match! teams.GetMemberRole(teamId, userId) with
                    | Some r when TeamRoles.canWriteTeamConfig r -> return! f store teamId
                    | Some r ->
                        return
                            Error
                                $"Only team owners and admins can manage publication readers. Your role: {TeamRoles.displayName r}."
                    | None -> return Error "You are not a member of this team."
                | None -> return Error "Team management is not available in this deployment."
            | _ -> return Error "Publication readers are managed by an owner or admin of the publishing team."
    }

    let audit
        (scope: string)
        (entry: PublicationReaderEntry)
        (event: PublicationReaderGrantChangedPayload -> AuditEvent)
        =
        async {
            match service<IAuditLog> ctx with
            | Some log ->
                do!
                    log.Record(
                        AuditStreamKey.ofTeam scope,
                        event {
                            ActorId = accessContext.UserId
                            PublishingScope = scope
                            ReaderId = entry.UserId
                            Role = entry.Role
                            OccurredAt = DateTimeOffset.UtcNow
                        }
                    )
            | None -> ()
        }

    let trimmed (entry: PublicationReaderEntry) = {
        UserId = (if isNull entry.UserId then "" else entry.UserId.Trim())
        Role = (if isNull entry.Role then "" else entry.Role.Trim())
    }

    {
        ListReaders =
            fun () ->
                authorised (fun store scope -> async {
                    let! roles = declaredRoles ctx scope
                    let! readers = store.ListScope scope

                    return
                        Ok {
                            PublishingScope = scope
                            DeclaredRoles = roles
                            Readers = readers
                        }
                })
        GrantReader =
            fun entry ->
                authorised (fun store scope -> async {
                    let entry = trimmed entry
                    let! roles = declaredRoles ctx scope

                    if not (List.contains entry.Role roles) then
                        let declared = String.concat ", " roles

                        return
                            Error
                                $"'{entry.Role}' is not a reader role this team's publications declare. Declared: {declared}."
                    else
                        match! store.Grant(scope, entry, accessContext.UserId) with
                        | Error reason -> return Error reason
                        | Ok() ->
                            do! audit scope entry PublicationReaderGranted
                            return Ok()
                })
        RevokeReader =
            fun entry ->
                authorised (fun store scope -> async {
                    let entry = trimmed entry

                    match! store.Revoke(scope, entry) with
                    | Error reason -> return Error reason
                    | Ok() ->
                        do! audit scope entry PublicationReaderRevoked
                        return Ok()
                })
    }