// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SessionApiHandler

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

// ─── Phase 528 — the ISessionApi handler ─────────────────────────────
//
// Where the AUTHORISATION for a revocation is decided. `ISessionRegistry`
// deliberately holds no policy — it records who acted so the decision is
// attributable, but it does not decide who may act (see its header). That
// decision lives here, in one place, where it can be read whole:
//
//   * every self-service method derives the owning identity from the
//     authenticated `AccessContext` and never reads a user id off the
//     wire, so a caller cannot address another user's sessions by naming
//     them (GP 4);
//   * the one method that DOES take a user id — the admin force-revoke —
//     requires `TeamRoles.canWriteTeamConfig` in team mode, verifies the
//     target is a member of the caller's OWN team, and is refused
//     outright in every non-team mode, because a deployment with no team
//     has no "administrator of someone else" to be;
//   * `NotFound` and `AccessDenied` collapse to one client message
//     (`SessionError.toClientMessage`), so the revoke endpoint cannot be
//     used as an oracle over other users' session ids.

/// Best-effort audit. A missing `IAuditLog` (test harnesses that bypass
/// compose) or a throwing write logs and returns: an operator cutting off
/// a compromised session must not be blocked because the audit fabric is
/// down — that is precisely the incident during which they need it to
/// work. The same posture `ModuleVisibilityApiHandler` takes (GP 6).
let private audit (auditLogOpt: IAuditLog option) (logger: ILogger option) (scopeId: string) (event: AuditEvent) = async {
    match auditLogOpt with
    | None -> return ()
    | Some auditLog ->
        try
            do! auditLog.Record(scopeId, event)
        with ex ->
            match logger with
            | Some l -> l.Warn(sprintf "SessionApi: audit write failed for scope %s: %s" scopeId ex.Message)
            | None -> ()
}

/// Build the `ISessionApi` ToolUp.Remoting handler. Resolves the registry,
/// the access context and the team store lazily from DI per request — the
/// pattern `ModuleVisibilityApiHandler` / `FeatureFlagHandler` use.
let sessionApi (ctx: HttpContext) : ISessionApi =

    let registryOpt: ISessionRegistry option =
        match ctx.RequestServices.GetService typeof<ISessionRegistry> with
        | :? ISessionRegistry as r -> Some r
        | _ -> None

    let auditLogOpt: IAuditLog option =
        match ctx.RequestServices.GetService typeof<IAuditLog> with
        | :? IAuditLog as a -> Some a
        | _ -> None

    let logger: ILogger option =
        match ctx.RequestServices.GetService typeof<ILogger> with
        | :? ILogger as l -> Some l
        | _ -> None

    let accessContext =
        match ctx.RequestServices.GetService typeof<AccessContext> with
        | :? AccessContext as ac -> ac
        | _ ->
            // Fallback for tests that bypass ScopeResolutionMiddleware —
            // same pattern as ConfigHandler / ModuleVisibilityApiHandler.
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            AccessContext.unrestricted (AnonymousSession userId)

    let scopeIdOpt =
        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
        | true, (:? StorageScope as s) -> Some s.ScopeId
        | _ -> None

    /// Every method needs the same two things: a composed registry and a
    /// resolved scope. Absent either, the honest answer is that sessions
    /// are unavailable — not an empty list, which reads as "you have no
    /// other sessions" and is the one wrong answer a security surface
    /// must not give.
    let withRegistry (f: ISessionRegistry -> string -> Async<Result<'a, string>>) = async {
        match registryOpt, scopeIdOpt with
        | Some registry, Some scopeId -> return! f registry scopeId
        | _ -> return Error(SessionError.toClientMessage (SessionError.StoreUnavailable "no session registry composed"))
    }

    let toClientResult (result: Result<'a, SessionError>) : Result<'a, string> =
        match result with
        | Ok v -> Ok v
        | Error e ->
            match logger, e with
            | Some l, SessionError.StoreUnavailable msg -> l.Warn(sprintf "SessionApi: store unavailable: %s" msg)
            | _ -> ()

            Error(SessionError.toClientMessage e)

    /// Team-mode admin gate for the force-revoke path. Returns the team
    /// id on success so the caller cannot forget to bound the operation
    /// to it — the authority and its scope arrive together or not at all.
    let ensureTeamAdmin () : Async<Result<string, string>> = async {
        match accessContext.Subject with
        | TeamMember(actorId, teamId) ->
            match ctx.RequestServices.GetService typeof<ITeamStore> with
            | :? ITeamStore as teams ->
                let! role = teams.GetMemberRole(teamId, actorId)

                match role with
                | Some r when TeamRoles.canWriteTeamConfig r -> return Ok teamId
                | Some r ->
                    return
                        Error
                            $"Only team owners and admins can sign other members out. Your role: {TeamRoles.displayName r}."
                | None -> return Error "You are not a member of this team."
            | _ -> return Error "Team management is not available in this deployment."
        | _ ->
            // Not a hedge: outside team mode there is no administrative
            // relationship for one user to hold over another, so there is
            // nothing to authorise against and the request is refused
            // rather than silently narrowed to a self-revoke.
            return Error "Signing another user out requires a team-scoped deployment."
    }

    {
        ListMySessions =
            fun () ->
                withRegistry (fun registry scopeId -> async {
                    let! records = registry.ListForUser(scopeId, accessContext.UserId)

                    return records |> Result.map (List.sortByDescending _.LastSeenAt) |> toClientResult
                })

        RevokeSession =
            fun sessionId ->
                withRegistry (fun registry scopeId -> async {
                    // Ownership is checked by READING the record first,
                    // not by trusting the id. Revoking straight through
                    // would let any caller cut off any session in their
                    // scope whose id they had seen — and in a team scope
                    // that is every colleague's.
                    let! records = registry.ListForUser(scopeId, accessContext.UserId)

                    match records with
                    | Error e -> return toClientResult (Error e)
                    | Ok mine ->
                        match mine |> List.tryFind (fun r -> r.SessionId = sessionId) with
                        | None ->
                            // Not ours, or not there. One message for both
                            // — see `SessionError.toClientMessage`.
                            return toClientResult (Error(SessionError.NotFound sessionId))
                        | Some record ->
                            let! revoked = registry.Revoke(scopeId, sessionId, accessContext.UserId)

                            match revoked with
                            | Error e -> return toClientResult (Error e)
                            | Ok() ->
                                do!
                                    audit
                                        auditLogOpt
                                        logger
                                        scopeId
                                        (AuditEvent.SessionRevoked {
                                            ActorUserId = accessContext.UserId
                                            SubjectUserId = record.UserId
                                            SessionId = record.SessionId
                                            DeviceDescriptor = record.DeviceDescriptor
                                            ByAdministrator = false
                                        })

                                return Ok()
                })

        RevokeAllMySessions =
            fun () ->
                withRegistry (fun registry scopeId -> async {
                    let! revoked = registry.RevokeAllForUser(scopeId, accessContext.UserId, accessContext.UserId)

                    match revoked with
                    | Error e -> return toClientResult (Error e)
                    | Ok count ->
                        do!
                            audit
                                auditLogOpt
                                logger
                                scopeId
                                (AuditEvent.AllSessionsRevoked {
                                    ActorUserId = accessContext.UserId
                                    SubjectUserId = accessContext.UserId
                                    RevokedCount = count
                                    ByAdministrator = false
                                })

                        return Ok count
                })

        RevokeAllForUser =
            fun targetUserId ->
                withRegistry (fun registry scopeId -> async {
                    let! gate = ensureTeamAdmin ()

                    match gate with
                    | Error msg -> return Error msg
                    | Ok teamId ->
                        // The target must be a member of the caller's own
                        // team. Without this an Owner of team A could sign
                        // out a user of team B by naming them — the scope
                        // check alone does not cover it, because a user id
                        // is not scope-qualified (GP 4).
                        match ctx.RequestServices.GetService typeof<ITeamStore> with
                        | :? ITeamStore as teams ->
                            let! targetRole = teams.GetMemberRole(teamId, targetUserId)

                            match targetRole with
                            | None ->
                                return
                                    Error(SessionError.toClientMessage (SessionError.AccessDenied "not a team member"))
                            | Some _ ->
                                let! revoked = registry.RevokeAllForUser(scopeId, targetUserId, accessContext.UserId)

                                match revoked with
                                | Error e -> return toClientResult (Error e)
                                | Ok count ->
                                    do!
                                        audit
                                            auditLogOpt
                                            logger
                                            scopeId
                                            (AuditEvent.AllSessionsRevoked {
                                                ActorUserId = accessContext.UserId
                                                SubjectUserId = targetUserId
                                                RevokedCount = count
                                                ByAdministrator = true
                                            })

                                    return Ok count
                        | _ -> return Error "Team management is not available in this deployment."
                })
    }