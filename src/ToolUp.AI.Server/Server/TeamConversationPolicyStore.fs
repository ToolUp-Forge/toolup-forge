// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 859 — the per-team conversation visibility policy: its deployment
/// settings, its stored record (with the change history the "widening never
/// exposes the past" rule reads), the per-request gate the assistant handler
/// filters every conversation read and write through, the audit rows, and
/// the `TeamConversationVisibilityApi` a team owner sets the level with.
module ToolUp.AI.TeamConversationPolicyStore

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Threading
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.TeamManagement
open ToolUp.AI

// ─── Deployment settings ─────────────────────────────────────────

/// What the deployment declares (Phase 859.D): the level a team starts
/// with, and the levels a team may choose from. Composed with
/// `AICompose.withTeamConversationVisibility`; a deployment that composes
/// nothing gets `TeamConversationVisibilitySettings.unrestricted`.
type TeamConversationVisibilitySettings = {
    /// The level in force for a team that has chosen nothing.
    Default: TeamConversationVisibility
    /// The levels a team owner may select. Always contains `Default`.
    Allowed: TeamConversationVisibility list
}

module TeamConversationVisibilitySettings =
    /// No composition: `TeamVisible` by default, every level allowed — a
    /// team that has set nothing behaves exactly as before Phase 859.
    let unrestricted: TeamConversationVisibilitySettings = {
        Default = TeamVisible
        Allowed = TeamConversationVisibility.all
    }

    /// Validate a declaration. The allowed set must be non-empty and must
    /// contain the default, or a team that never chose would sit at a
    /// level no one could select back to.
    let create
        (defaultLevel: TeamConversationVisibility)
        (allowed: TeamConversationVisibility list)
        : Result<TeamConversationVisibilitySettings, string> =
        let allowed = allowed |> List.distinct

        if List.isEmpty allowed then
            Error "withTeamConversationVisibility: the allowed set is empty; a team could select no level at all."
        elif not (List.contains defaultLevel allowed) then
            Error(
                sprintf
                    "withTeamConversationVisibility: the default %s is not in the allowed set [%s]."
                    (TeamConversationVisibility.name defaultLevel)
                    (allowed |> List.map TeamConversationVisibility.name |> String.concat ", ")
            )
        else
            Ok {
                Default = defaultLevel
                Allowed = TeamConversationVisibility.all |> List.filter (fun l -> List.contains l allowed)
            }

    /// The allowed set, named, for a refusal.
    let describeAllowed (settings: TeamConversationVisibilitySettings) : string =
        settings.Allowed
        |> List.map TeamConversationVisibility.name
        |> String.concat ", "

    /// Resolve the composed settings from DI, else `unrestricted`.
    let resolve (services: IServiceProvider) : TeamConversationVisibilitySettings =
        match services.GetService(typeof<TeamConversationVisibilitySettings>) with
        | :? TeamConversationVisibilitySettings as s -> s
        | _ -> unrestricted

// ─── The stored record ───────────────────────────────────────────

/// One change of a team's level (Phase 859.G).
type TeamConversationVisibilityChange = {
    /// The level from `ChangedAt` on.
    Level: TeamConversationVisibility
    /// The user who changed it.
    ChangedBy: string
    /// When, UTC.
    ChangedAt: DateTime
}

/// A team's own conversation-visibility record, stored in the team's
/// container — deliberately not a field on `TeamPermissions` and not a case
/// on the admin-mutation union (859.D). It keeps every change, oldest first,
/// so the level in force when a conversation was created is always
/// decidable without adding a field to the conversation.
type TeamConversationPolicyRecord = {
    /// Every change, oldest first. Empty for a team that never chose.
    Changes: TeamConversationVisibilityChange list
}

module TeamConversationPolicyRecord =
    /// A team that has chosen nothing.
    let empty: TeamConversationPolicyRecord = { Changes = [] }

    /// Blob name inside the team's container.
    [<Literal>]
    let BlobName = "team-policies/ai-conversation-visibility.json"

    /// The container a team's record lives in — the team's own container,
    /// read in the team's scope.
    let containerOf (teamId: string) = $"team-{teamId}"

    /// Whether the team has ever chosen a level.
    let isUnset (record: TeamConversationPolicyRecord) = List.isEmpty record.Changes

    /// The level in force now: the last change, else the deployment default.
    let current (defaultLevel: TeamConversationVisibility) (record: TeamConversationPolicyRecord) =
        record.Changes
        |> List.tryLast
        |> Option.map _.Level
        |> Option.defaultValue defaultLevel

    /// The level in force at `at`: the last change made at or before it,
    /// else the deployment default.
    let levelAt (defaultLevel: TeamConversationVisibility) (record: TeamConversationPolicyRecord) (at: DateTime) =
        record.Changes
        |> List.filter (fun c -> c.ChangedAt <= at)
        |> List.tryLast
        |> Option.map _.Level
        |> Option.defaultValue defaultLevel

    let private jsonOptions = FableConverters.create ()

    /// The record's stored bytes (the Fable-compatible JSON shape).
    let serialise (record: TeamConversationPolicyRecord) : byte[] =
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, jsonOptions))

    /// Parse a stored record. A record persisted without `Changes` reads
    /// as empty (the STJ path yields `null` for an absent list).
    let deserialise (bytes: byte[]) : TeamConversationPolicyRecord option =
        try
            let parsed =
                JsonSerializer.Deserialize<TeamConversationPolicyRecord>(Encoding.UTF8.GetString bytes, jsonOptions)

            if isNull (box parsed) then
                None
            elif isNull (box parsed.Changes) then
                Some empty
            else
                Some {
                    Changes = parsed.Changes |> List.sortBy _.ChangedAt
                }
        with _ ->
            None

// ─── The store ───────────────────────────────────────────────────

/// Reads and guarded writes of a team's record. The write is a
/// read-modify-write guarded twice: an ETag compare-and-swap when the
/// backend implements `IConditionalBlobStorage` (retried a bounded number of
/// times on a lost race), and a per-container gate in this process so two
/// requests here never interleave even on a backend without conditional
/// writes — the same interim guard the other blob-backed ledgers use.
type TeamConversationPolicyStore(storage: IBlobStorage) =
    static let gates = ConcurrentDictionary<string, SemaphoreSlim>()

    let conditional =
        match box storage with
        | :? IConditionalBlobStorage as c -> Some c
        | _ -> None

    let maxAttempts = 5

    /// Absent vs unreadable: an absent record is a team that never chose;
    /// a record that exists but cannot be read is an `Error`, so a caller
    /// fails closed rather than silently falling back to the default.
    let absentOrError (container: string) (message: string) : Async<Result<TeamConversationPolicyRecord, string>> = async {
        let! exists = async {
            try
                return! storage.Exists(container, TeamConversationPolicyRecord.BlobName)
            with _ ->
                return true
        }

        if exists then
            return Error $"the team's conversation-visibility record could not be read: {message}"
        else
            return Ok TeamConversationPolicyRecord.empty
    }

    let parse (bytes: byte[]) =
        match TeamConversationPolicyRecord.deserialise bytes with
        | Some record -> Ok record
        | None -> Error "the team's conversation-visibility record is unparseable"

    let readWithETag (container: string) : Async<Result<TeamConversationPolicyRecord * string option, string>> = async {
        match conditional with
        | Some cas ->
            match! cas.DownloadWithETag(container, TeamConversationPolicyRecord.BlobName) with
            | Ok(bytes, etag) -> return parse bytes |> Result.map (fun r -> r, Some etag)
            | Error message ->
                let! fallback = absentOrError container message
                return fallback |> Result.map (fun r -> r, None)
        | None ->
            match! storage.Download(container, TeamConversationPolicyRecord.BlobName) with
            | Ok bytes -> return parse bytes |> Result.map (fun r -> r, None)
            | Error message ->
                let! fallback = absentOrError container message
                return fallback |> Result.map (fun r -> r, None)
    }

    /// The team's record. `Ok empty` when the team never chose; `Error`
    /// when a record exists but cannot be read.
    member _.Read(container: string) : Async<Result<TeamConversationPolicyRecord, string>> = async {
        let! read = readWithETag container
        return read |> Result.map fst
    }

    /// Guarded read-modify-write. `decide` sees the stored record and
    /// returns the record to write, or `Error` to refuse (nothing is
    /// written). Returns `(before, after)`; when `decide` returns the
    /// record unchanged nothing is written.
    member _.Change
        (container: string, decide: TeamConversationPolicyRecord -> Result<TeamConversationPolicyRecord, string>)
        : Async<Result<TeamConversationPolicyRecord * TeamConversationPolicyRecord, string>> =
        async {
            let gate = gates.GetOrAdd(container, fun _ -> new SemaphoreSlim(1, 1))
            do! gate.WaitAsync() |> Async.AwaitTask

            try
                let rec attempt n = async {
                    match! readWithETag container with
                    | Error e -> return Error e
                    | Ok(before, etag) ->
                        match decide before with
                        | Error refusal -> return Error refusal
                        | Ok after when after = before -> return Ok(before, after)
                        | Ok after ->
                            let bytes = TeamConversationPolicyRecord.serialise after

                            match conditional with
                            | Some cas ->
                                let condition =
                                    match etag with
                                    | Some e -> IfMatch e
                                    | None -> IfAbsent

                                match!
                                    cas.UploadWithETag(
                                        container,
                                        TeamConversationPolicyRecord.BlobName,
                                        bytes,
                                        condition
                                    )
                                with
                                | Ok _ -> return Ok(before, after)
                                | Error(ETagMismatch _) when n < maxAttempts -> return! attempt (n + 1)
                                | Error(ETagMismatch _) ->
                                    return
                                        Error "the team's conversation visibility was changed concurrently; try again."
                                | Error(ConditionalWriteFailure message) ->
                                    return Error $"the team's conversation visibility could not be saved: {message}"
                            | None ->
                                match! storage.Upload(container, TeamConversationPolicyRecord.BlobName, bytes) with
                                | Ok _ -> return Ok(before, after)
                                | Error message ->
                                    return Error $"the team's conversation visibility could not be saved: {message}"
                }

                return! attempt 1
            finally
                gate.Release() |> ignore
        }

// ─── The per-request gate ────────────────────────────────────────

/// Who is asking, as the rule needs them.
type ConversationViewer = {
    /// The viewer's user id.
    UserId: string
    /// The viewer's role in the team, `None` when not a member (or when no
    /// team store is composed to say).
    TeamRole: TeamRole option
    /// Whether the viewer holds `PlatformRole.PlatformAdmin`.
    IsPlatformAdmin: bool
}

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
                let store = TeamConversationPolicyStore storage
                let! record = store.Read(TeamConversationPolicyRecord.containerOf teamId)

                return
                    Some {
                        TeamId = teamId
                        Container = container
                        Settings = TeamConversationVisibilitySettings.resolve ctx.RequestServices
                        Record =
                            match record with
                            | Ok r -> Some r
                            | Error _ -> None
                    }
        }

    /// The viewer, with their role in `teamId` read from `ITeamStore`
    /// (`None` when no team store is composed — fail closed) and their
    /// platform role from the request's `AccessContext`.
    let resolveViewer (ctx: HttpContext) (userId: string) (teamId: string) : Async<ConversationViewer> = async {
        let! role =
            match ctx.RequestServices.GetService(typeof<ITeamStore>) with
            | :? ITeamStore as teams -> teams.GetMemberRole(teamId, userId)
            | _ -> async.Return None

        let isPlatformAdmin =
            accessOf ctx
            |> Option.map AccessContext.canModifyPlatformConfig
            |> Option.defaultValue false

        return {
            UserId = userId
            TeamRole = role
            IsPlatformAdmin = isPlatformAdmin
        }
    }

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
    let settings = TeamConversationVisibilitySettings.resolve services

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

                    match! TeamConversationPolicyStore(storage).Read container with
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

                    match! TeamConversationPolicyStore(storage).Change(container, decide) with
                    | Error refusal -> return Error refusal
                    | Ok(before, after) ->
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