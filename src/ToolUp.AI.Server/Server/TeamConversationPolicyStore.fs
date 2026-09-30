// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 859 — the per-team conversation visibility policy as the assistant
/// applies it: the per-request gate the assistant handler filters every
/// conversation read and write through, the audit rows, and the
/// `TeamConversationVisibilityApi` a team owner sets the level with.
///
/// Phase 936 — the record the level is stored in, its guarded store, the
/// deployment declaration, and everything about the team's OUTPUT level
/// (Phase 896) moved to the platform tier (`ToolUp.Platform.TeamPolicyStore`,
/// with the types in Platform.Core `TeamOutputVisibility.fs`), so a
/// deployment that composes facts without the assistant has them. This
/// module is now a consumer of that record for the conversation level.
module ToolUp.AI.TeamConversationPolicyStore

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.TeamPolicyStore
open ToolUp.AI

// ─── The per-request gate ────────────────────────────────────────

/// Who is asking, as the rule needs them — the platform's viewer (Phase 936).
type ConversationViewer = TeamPolicyViewer

/// What one request knows about its team's policy.
type TeamVisibilityState = {
    /// The team the request is scoped to.
    TeamId: string
    /// The team's storage container.
    Container: string
    /// The deployment's declared default and allowed set.
    Settings: TeamConversationVisibilitySettings
    /// `None` when the record exists but cannot be read — every check then
    /// fails closed to the conversation's author.
    Record: TeamConversationPolicyRecord option
}

module ConversationVisibility =
    let private isAuthor (viewer: ConversationViewer) (owner: string) =
        not (String.IsNullOrEmpty owner) && viewer.UserId = owner

    /// The levels in force when the conversation was created and now.
    let levels (state: TeamVisibilityState) (createdAt: DateTime) =
        match state.Record with
        | Some record ->
            Some(
                TeamConversationPolicyRecord.levelAt state.Settings.Default record createdAt,
                TeamConversationPolicyRecord.current state.Settings.Default record
            )
        | None -> None

    /// Reads are unaffected: a readable record the team never set, under a
    /// `TeamVisible` default. The handler skips the per-conversation work
    /// (and the role lookup) entirely, so such a team is byte-identical to
    /// its pre-859 self.
    let isOpen (state: TeamVisibilityState) =
        match state.Record with
        | Some record ->
            TeamConversationPolicyRecord.isUnset record
            && state.Settings.Default = TeamVisible
        | None -> false

    /// Whether `viewer` may see the conversation. Narrowing applies at once
    /// and widening never exposes the past: the viewer must pass the level
    /// in force when the conversation was created AND the level in force
    /// now. (The three levels are not nested — a team admin who is not a
    /// platform admin and a platform admin who is not a team admin each see
    /// what the other does not — so "the narrower of the two" is exactly
    /// "passes both".)
    let canSee (state: TeamVisibilityState) (viewer: ConversationViewer) (owner: string) (createdAt: DateTime) =
        isAuthor viewer owner
        || (match levels state createdAt with
            | Some(atCreation, current) ->
                TeamConversationVisibility.canSee atCreation viewer.UserId owner viewer.TeamRole viewer.IsPlatformAdmin
                && TeamConversationVisibility.canSee current viewer.UserId owner viewer.TeamRole viewer.IsPlatformAdmin
            | None -> false)

    /// Whether `viewer` may delete or change the conversation: its author;
    /// anyone else only when they can see it and hold the CURRENT level's
    /// elevated role.
    let canModify (state: TeamVisibilityState) (viewer: ConversationViewer) (owner: string) (createdAt: DateTime) =
        isAuthor viewer owner
        || (canSee state viewer owner createdAt
            && (match levels state createdAt with
                | Some(_, current) ->
                    TeamConversationVisibility.isElevated current viewer.TeamRole viewer.IsPlatformAdmin
                | None -> false))

    /// Whether opening the conversation is an ELEVATED read, which is
    /// audited (859.E): the viewer is not its author and a level narrower
    /// than `TeamVisible` governs it.
    let isElevatedRead (state: TeamVisibilityState) (viewer: ConversationViewer) (owner: string) (createdAt: DateTime) =
        not (isAuthor viewer owner)
        && (match levels state createdAt with
            | Some(atCreation, current) -> atCreation <> TeamVisible || current <> TeamVisible
            | None -> false)

    /// The team the request is scoped to: the active team of a
    /// `TeamMember` subject, else the id behind a `team-` container.
    /// `None` outside a team scope — a personal container is owner-only by
    /// construction and not governed by the team policy.
    let teamOf (access: AccessContext option) (container: string) : string option =
        match access |> Option.map _.Subject with
        | Some(TeamMember(_, teamId)) -> Some teamId
        | _ when container.StartsWith("team-", StringComparison.Ordinal) -> Some(container.Substring 5)
        | _ -> None

    /// The request's `AccessContext`: the DI one, else the items-backed
    /// reconstruction (never a silent unrestricted fallback).
    let accessOf (ctx: HttpContext) : AccessContext option =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> Some ac
        | _ ->
            match ctx.Items.TryGetValue "ToolUp.StorageScope" with
            | true, _ -> Some(AIToolRegistry.reconstructAccessContext ctx)
            | _ -> None

    /// Load the request's team state; `None` outside a team scope.
    let resolveState
        (ctx: HttpContext)
        (storage: IBlobStorage)
        (container: string)
        : Async<TeamVisibilityState option> =
        async {
            match teamOf (accessOf ctx) container with
            | None -> return None
            | Some teamId ->
                let store = TeamPolicyStore storage
                let! record = store.Read(TeamConversationPolicyRecord.containerOf teamId)

                return
                    Some {
                        TeamId = teamId
                        Container = container
                        Settings = TeamPolicySettings.conversationOrUnrestricted ctx.RequestServices
                        Record =
                            match record with
                            | Ok r -> Some r
                            | Error _ -> None
                    }
        }

    /// The viewer, with their role in `teamId` read from `ITeamStore`
    /// (`None` when no team store is composed — fail closed) and their
    /// platform role from the request's `AccessContext`.
    let resolveViewer (ctx: HttpContext) (userId: string) (teamId: string) : Async<ConversationViewer> =
        TeamPolicyViewer.resolve ctx.RequestServices (accessOf ctx) userId teamId

// ─── Audit rows ──────────────────────────────────────────────────
//
// Both rows ride the uniform `RemotingMethodAudited` case with an
// open-vocabulary `Custom:` kind rather than new `AuditEvent` cases, so no
// exhaustive match over the audit union anywhere has to change.

module ConversationVisibilityAudit =
    /// Kind of the row an elevated open writes.
    [<Literal>]
    let ElevatedReadKind = "Custom:ConversationElevatedRead"

    /// Kind of the row a change of a team's level writes.
    [<Literal>]
    let LevelChangedKind = "Custom:TeamConversationVisibilityChanged"

    /// Not wrapped in a catch: `IAuditLog.Record` already absorbs a failed
    /// write under the default failure policy, and under a compliance-grade
    /// policy it raises on purpose so the action fails rather than
    /// completing un-audited — an elevated open then returns nothing.
    let private record (audit: IAuditLog option) (scopeId: string) (event: AuditEvent) = async {
        match audit with
        | Some log -> do! log.Record(scopeId, event)
        | None -> ()
    }

    /// An elevated open (859.E): the viewer, the author and the conversation.
    let elevatedRead
        (audit: IAuditLog option)
        (scopeId: string)
        (viewer: string)
        (author: string)
        (conversationId: Guid)
        (level: TeamConversationVisibility)
        =
        record
            audit
            scopeId
            (RemotingMethodAudited {
                Kind = ElevatedReadKind
                MethodName = "GetConversation"
                SubjectId = viewer
                CorrelationId = None
                Payload =
                    Map.ofList [
                        "viewer", viewer
                        "author", author
                        "conversationId", conversationId.ToString()
                        "level", TeamConversationVisibility.name level
                    ]
            })

    /// A change of a team's level (859.E): who, the old level and the new.
    let levelChanged
        (audit: IAuditLog option)
        (scopeId: string)
        (teamId: string)
        (changedBy: string)
        (oldLevel: TeamConversationVisibility)
        (newLevel: TeamConversationVisibility)
        =
        record
            audit
            scopeId
            (RemotingMethodAudited {
                Kind = LevelChangedKind
                MethodName = "SetConversationVisibility"
                SubjectId = changedBy
                CorrelationId = None
                Payload =
                    Map.ofList [
                        "teamId", teamId
                        "changedBy", changedBy
                        "oldLevel", TeamConversationVisibility.name oldLevel
                        "newLevel", TeamConversationVisibility.name newLevel
                    ]
            })

// ─── Who may change the level (859.G) ────────────────────────────

module ConversationVisibilityChange =
    /// The levels a caller may select now. Only the team `Owner` changes
    /// the level; `PlatformAdmins` is selectable, and leavable, only by a
    /// platform admin.
    let selectable
        (settings: TeamConversationVisibilitySettings)
        (current: TeamConversationVisibility)
        (role: TeamRole option)
        (isPlatformAdmin: bool)
        : TeamConversationVisibility list =
        match role with
        | Some TeamRole.Owner when current <> PlatformAdmins || isPlatformAdmin ->
            settings.Allowed
            |> List.filter (fun level -> level <> PlatformAdmins || isPlatformAdmin)
        | _ -> []

    /// Refuse a change, or `Ok ()`. `current` is the level read inside the
    /// guarded write, so a concurrent change is judged against what is
    /// actually stored.
    let check
        (settings: TeamConversationVisibilitySettings)
        (current: TeamConversationVisibility)
        (role: TeamRole option)
        (isPlatformAdmin: bool)
        (requested: TeamConversationVisibility)
        : Result<unit, string> =
        if not (List.contains requested settings.Allowed) then
            Error(
                sprintf
                    "%s is not an allowed conversation visibility in this deployment. Allowed: %s."
                    (TeamConversationVisibility.name requested)
                    (TeamConversationVisibilitySettings.describeAllowed settings)
            )
        else
            match role with
            | None -> Error "You are not a member of this team."
            | Some TeamRole.Admin when requested = TeamAdmins ->
                Error
                    "A team admin cannot select TeamAdmins: that would grant you the right to read your colleagues' conversations. Only the team owner sets conversation visibility."
            | Some TeamRole.Admin
            | Some TeamRole.Member -> Error "Only the team owner can set conversation visibility."
            | Some TeamRole.Owner ->
                if requested = PlatformAdmins && not isPlatformAdmin then
                    Error "Only a platform admin can restrict conversations to platform admins."
                elif current = PlatformAdmins && requested <> PlatformAdmins && not isPlatformAdmin then
                    Error
                        "This team's conversations are restricted to platform admins; only a platform admin can change that."
                else
                    Ok()

// ─── The API ─────────────────────────────────────────────────────

/// `TeamConversationVisibilityApi` over the request. The team is always the
/// caller's active team.
let teamConversationVisibilityApi (ctx: HttpContext) : TeamConversationVisibilityApi =
    let services = ctx.RequestServices
    let settings = TeamPolicySettings.conversationOrUnrestricted services

    let storage =
        match services.GetService(typeof<IBlobStorage>) with
        | :? IBlobStorage as s -> Some s
        | _ -> None

    let logger: ILogger =
        match services.GetService(typeof<ILogger>) with
        | :? ILogger as l -> l
        | _ ->
            { new ILogger with
                member _.Debug _ = ()
                member _.Info _ = ()
                member _.Warn _ = ()
                member _.Error(_, _) = ()
            }

    let audit =
        match services.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as a -> Some a
        | _ -> None

    let access = ConversationVisibility.accessOf ctx

    // Phase 896 — composed only with team output visibility; `None` keeps
    // the policy-change check out of the conversation path entirely.
    let outputSettings = TeamPolicySettings.output services

    let team =
        match access |> Option.map _.Subject with
        | Some(TeamMember(userId, teamId)) -> Some(userId, teamId, TeamConversationPolicyRecord.containerOf teamId)
        | _ -> None

    let personalView = {
        InTeamScope = false
        Level = TeamVisible
        Allowed = settings.Allowed
        Selectable = []
    }

    let viewOf (record: TeamConversationPolicyRecord) (viewer: ConversationViewer) =
        let current = TeamConversationPolicyRecord.current settings.Default record

        {
            InTeamScope = true
            Level = current
            Allowed = settings.Allowed
            Selectable = ConversationVisibilityChange.selectable settings current viewer.TeamRole viewer.IsPlatformAdmin
        }

    {
        GetConversationVisibility =
            fun () -> async {
                match team, storage with
                | Some(userId, teamId, container), Some storage ->
                    let! viewer = ConversationVisibility.resolveViewer ctx userId teamId

                    match! TeamPolicyStore(storage).Read container with
                    | Ok record -> return viewOf record viewer
                    | Error message ->
                        logger.Warn $"Conversation visibility for team {teamId}: {message}"
                        // Unreadable: the handler fails closed to authors
                        // only. Show the narrowest level so a member is never
                        // told more people can read than can.
                        return {
                            InTeamScope = true
                            Level = PlatformAdmins
                            Allowed = settings.Allowed
                            Selectable = []
                        }
                | _ -> return personalView
            }

        SetConversationVisibility =
            fun requested -> async {
                match team, storage with
                | Some(userId, teamId, container), Some storage ->
                    let! viewer = ConversationVisibility.resolveViewer ctx userId teamId

                    let decide (record: TeamConversationPolicyRecord) =
                        let current = TeamConversationPolicyRecord.current settings.Default record

                        match
                            ConversationVisibilityChange.check
                                settings
                                current
                                viewer.TeamRole
                                viewer.IsPlatformAdmin
                                requested
                        with
                        | Error refusal -> Error refusal
                        | Ok() when requested = current -> Ok record
                        | Ok() ->
                            Ok {
                                Changes =
                                    record.Changes
                                    @ [
                                        {
                                            Level = requested
                                            ChangedBy = userId
                                            ChangedAt = DateTime.UtcNow
                                        }
                                    ]
                            }

                    // Phase 896 — the policy-change check: a CHANGED level
                    // must be no wider than the team's output level.
                    let decideBoth (record: TeamConversationPolicyRecord, output: TeamOutputPolicyRecord) =
                        decide record
                        |> Result.bind (fun after ->
                            match outputSettings with
                            | Some outputSettings when after <> record ->
                                TeamOutputVisibility.checkCombination
                                    (TeamConversationPolicyRecord.current settings.Default after)
                                    [ TeamOutputPolicyRecord.current outputSettings.Default output ]
                                |> Result.map (fun () -> after, output)
                            | _ -> Ok(after, output))

                    match! TeamPolicyStore(storage).ChangeBoth(container, "conversation visibility", decideBoth) with
                    | Error refusal -> return Error refusal
                    | Ok((before, _), (after, _)) ->
                        if after <> before then
                            do!
                                ConversationVisibilityAudit.levelChanged
                                    audit
                                    teamId
                                    teamId
                                    userId
                                    (TeamConversationPolicyRecord.current settings.Default before)
                                    (TeamConversationPolicyRecord.current settings.Default after)

                        return Ok(viewOf after viewer)
                | Some _, None -> return Error "Conversation visibility cannot be stored: no blob storage is composed."
                | None, _ -> return Error "Conversation visibility is a team setting; there is no active team."
            }
    }