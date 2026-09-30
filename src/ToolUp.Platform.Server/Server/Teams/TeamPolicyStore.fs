// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 936 — the per-team policy record at the platform tier: its stored
/// shape, the guarded read-modify-write over it, the
/// `ITeamOutputVisibilitySource` the disclosure gate reads a team's output
/// level through, the startup check that the two deployment defaults do not
/// conflict, the audit row a change of the output level writes, and the
/// `TeamOutputVisibilityApi` a team owner sets that level with.
///
/// The record holds BOTH axes of team visibility: the conversation level
/// (Phase 859) as `Changes`, the output level (Phase 896) as
/// `OutputChanges`. Before Phase 936 all of this lived in the AI companion,
/// so a deployment that composed facts without the assistant got the
/// deployment default for every team and no way for an owner to change it.
/// The companion is now a consumer of this record for the conversation
/// level; the conversation rule and its API stay there.
module ToolUp.Platform.TeamPolicyStore

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Threading
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.TeamManagement

// ─── The deployment declarations, as DI holds them ───────────────

/// Read the two deployment declarations from DI.
module TeamPolicySettings =
    /// The output-visibility declaration, when team output visibility is
    /// composed (`FactsCompose.withTeamOutputVisibility`).
    let output (services: IServiceProvider) : TeamOutputVisibilitySettings option =
        match services.GetService(typeof<TeamOutputVisibilitySettings>) with
        | :? TeamOutputVisibilitySettings as s -> Some s
        | _ -> None

    /// The conversation-visibility declaration, when the conversation axis
    /// exists — the AI companion registers it whenever the assistant is
    /// composed (`TeamConversationVisibilitySettings.unrestricted` when the
    /// deployment declared nothing). `None` means there are no
    /// conversations for output to be quoted in.
    let conversation (services: IServiceProvider) : TeamConversationVisibilitySettings option =
        match services.GetService(typeof<TeamConversationVisibilitySettings>) with
        | :? TeamConversationVisibilitySettings as s -> Some s
        | _ -> None

    /// The conversation declaration, else `unrestricted`.
    let conversationOrUnrestricted (services: IServiceProvider) : TeamConversationVisibilitySettings =
        conversation services
        |> Option.defaultValue TeamConversationVisibilitySettings.unrestricted

// ─── The stored shape ────────────────────────────────────────────

/// The record's stored bytes. Unchanged by the move to the platform tier:
/// the same blob (`TeamConversationPolicyRecord.BlobName`) in the team's
/// container, the same Fable-compatible JSON, and — with no output change
/// — exactly the bytes the conversation part alone serialises to.
module TeamPolicyRecordCodec =
    let private jsonOptions = FableConverters.create ()

    let private serialiseConversation (record: TeamConversationPolicyRecord) : byte[] =
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, jsonOptions))

    /// Parse the conversation part. A record persisted without `Changes`
    /// reads as empty (the STJ path yields `null` for an absent list).
    let conversationOf (bytes: byte[]) : TeamConversationPolicyRecord option =
        try
            let parsed =
                JsonSerializer.Deserialize<TeamConversationPolicyRecord>(Encoding.UTF8.GetString bytes, jsonOptions)

            if isNull (box parsed) then
                None
            elif isNull (box parsed.Changes) then
                Some TeamConversationPolicyRecord.empty
            else
                Some {
                    Changes = parsed.Changes |> List.sortBy _.ChangedAt
                }
        with _ ->
            None

    /// The output part: empty when the member is absent, `None` when the
    /// bytes or the member cannot be read.
    let outputOf (bytes: byte[]) : TeamOutputPolicyRecord option =
        try
            use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))

            if document.RootElement.ValueKind <> JsonValueKind.Object then
                None
            else
                match document.RootElement.TryGetProperty TeamOutputPolicyRecord.MemberName with
                | true, entries when entries.ValueKind = JsonValueKind.Array ->
                    let changes =
                        JsonSerializer.Deserialize<TeamVisibilityChange list>(entries.GetRawText(), jsonOptions)

                    Some {
                        OutputChanges =
                            if isNull (box changes) then
                                []
                            else
                                changes |> List.sortBy _.ChangedAt
                    }
                | true, entries when entries.ValueKind = JsonValueKind.Null -> Some TeamOutputPolicyRecord.empty
                | true, _ -> None
                | false, _ -> Some TeamOutputPolicyRecord.empty
        with _ ->
            None

    /// The stored bytes of a record holding both parts. With no output
    /// change the output member is omitted, so the bytes are exactly the
    /// conversation part's — a team that never set an output level stores
    /// what it stored before Phase 896.
    let serialise (conversation: TeamConversationPolicyRecord) (output: TeamOutputPolicyRecord) : byte[] =
        if TeamOutputPolicyRecord.isUnset output then
            serialiseConversation conversation
        else
            let node = JsonSerializer.SerializeToNode(conversation, jsonOptions).AsObject()
            node[TeamOutputPolicyRecord.MemberName] <- JsonSerializer.SerializeToNode(output.OutputChanges, jsonOptions)
            Encoding.UTF8.GetBytes(node.ToJsonString(jsonOptions))

// ─── The store ───────────────────────────────────────────────────

/// Reads and guarded writes of a team's record. The write is a
/// read-modify-write guarded twice: an ETag compare-and-swap when the
/// backend implements `IConditionalBlobStorage` (retried a bounded number of
/// times on a lost race), and a per-container gate in this process so two
/// requests here never interleave even on a backend without conditional
/// writes — the same interim guard the other blob-backed ledgers use.
type TeamPolicyStore(storage: IBlobStorage) =
    static let gates = ConcurrentDictionary<string, SemaphoreSlim>()

    let conditional =
        match box storage with
        | :? IConditionalBlobStorage as c -> Some c
        | _ -> None

    let maxAttempts = 5

    /// Absent vs unreadable: an absent record is a team that never chose;
    /// a record that exists but cannot be read is an `Error`, so a caller
    /// fails closed rather than silently falling back to the default.
    let absentOrError
        (container: string)
        (message: string)
        : Async<Result<TeamConversationPolicyRecord * TeamOutputPolicyRecord, string>> =
        async {
            let! exists = async {
                try
                    return! storage.Exists(container, TeamConversationPolicyRecord.BlobName)
                with _ ->
                    return true
            }

            if exists then
                return Error $"the team's visibility policy record could not be read: {message}"
            else
                return Ok(TeamConversationPolicyRecord.empty, TeamOutputPolicyRecord.empty)
        }

    let parse (bytes: byte[]) =
        match TeamPolicyRecordCodec.conversationOf bytes, TeamPolicyRecordCodec.outputOf bytes with
        | Some record, Some output -> Ok(record, output)
        | None, _ -> Error "the team's conversation-visibility entries are unparseable"
        | Some _, None -> Error "the team's output-visibility entries are unparseable"

    let readWithETag
        (container: string)
        : Async<Result<(TeamConversationPolicyRecord * TeamOutputPolicyRecord) * string option, string>> =
        async {
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

    // The guarded read-modify-write over both parts of the record. `what`
    // names the setting in a refusal ("conversation visibility").
    let changeBoth
        (container: string)
        (what: string)
        (decide:
            TeamConversationPolicyRecord * TeamOutputPolicyRecord
                -> Result<TeamConversationPolicyRecord * TeamOutputPolicyRecord, string>)
        =
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
                            let bytes = TeamPolicyRecordCodec.serialise (fst after) (snd after)

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
                                    return Error $"the team's {what} was changed concurrently; try again."
                                | Error(ConditionalWriteFailure message) ->
                                    return Error $"the team's {what} could not be saved: {message}"
                            | None ->
                                match! storage.Upload(container, TeamConversationPolicyRecord.BlobName, bytes) with
                                | Ok _ -> return Ok(before, after)
                                | Error message -> return Error $"the team's {what} could not be saved: {message}"
                }

                return! attempt 1
            finally
                gate.Release() |> ignore
        }

    /// The conversation part of the team's record. `Ok empty` when the team
    /// never chose; `Error` when a record exists but cannot be read.
    member _.Read(container: string) : Async<Result<TeamConversationPolicyRecord, string>> = async {
        let! read = readWithETag container
        return read |> Result.map (fst >> fst)
    }

    /// The output part of the team's record. `Ok empty` when the team never
    /// chose; `Error` when the record exists but cannot be read.
    member _.ReadOutput(container: string) : Async<Result<TeamOutputPolicyRecord, string>> = async {
        let! read = readWithETag container
        return read |> Result.map (fst >> snd)
    }

    /// Guarded read-modify-write of the conversation part. `decide` sees the
    /// stored part and returns the part to write, or `Error` to refuse
    /// (nothing is written). Returns `(before, after)`; when `decide` returns
    /// the part unchanged nothing is written. The output part rides through
    /// untouched.
    member _.Change
        (container: string, decide: TeamConversationPolicyRecord -> Result<TeamConversationPolicyRecord, string>)
        : Async<Result<TeamConversationPolicyRecord * TeamConversationPolicyRecord, string>> =
        async {
            let! changed =
                changeBoth container "conversation visibility" (fun (record, output) ->
                    decide record |> Result.map (fun after -> after, output))

            return changed |> Result.map (fun (before, after) -> fst before, fst after)
        }

    /// Guarded read-modify-write over BOTH parts of the record, so a change
    /// of either level is judged against the other as actually stored.
    /// `what` names the setting in a refusal. Returns `(before, after)`;
    /// when `decide` returns the pair unchanged nothing is written.
    member _.ChangeBoth
        (
            container: string,
            what: string,
            decide:
                TeamConversationPolicyRecord * TeamOutputPolicyRecord
                    -> Result<TeamConversationPolicyRecord * TeamOutputPolicyRecord, string>
        ) : Async<
                Result<
                    (TeamConversationPolicyRecord * TeamOutputPolicyRecord) *
                    (TeamConversationPolicyRecord * TeamOutputPolicyRecord),
                    string
                 >
             >
        =
        changeBoth container what decide

// ─── Who is asking ───────────────────────────────────────────────

/// Who is asking, as the change rules need them.
type TeamPolicyViewer = {
    /// The viewer's user id.
    UserId: string
    /// The viewer's role in the team, `None` when not a member (or when no
    /// team store is composed to say).
    TeamRole: TeamRole option
    /// Whether the viewer holds `PlatformRole.PlatformAdmin`.
    IsPlatformAdmin: bool
}

/// Resolve a `TeamPolicyViewer`.
module TeamPolicyViewer =
    /// The viewer, with their role in `teamId` read from `ITeamStore`
    /// (`None` when no team store is composed — fail closed) and their
    /// platform role from `access`.
    let resolve
        (services: IServiceProvider)
        (access: AccessContext option)
        (userId: string)
        (teamId: string)
        : Async<TeamPolicyViewer> =
        async {
            let! role =
                match services.GetService(typeof<ITeamStore>) with
                | :? ITeamStore as teams -> teams.GetMemberRole(teamId, userId)
                | _ -> async.Return None

            return {
                UserId = userId
                TeamRole = role
                IsPlatformAdmin =
                    access
                    |> Option.map AccessContext.canModifyPlatformConfig
                    |> Option.defaultValue false
            }
        }

// ─── The disclosure gate's source ────────────────────────────────

/// `ITeamOutputVisibilitySource` over the per-team policy record: the level
/// a team chose, else the deployment default. An unreadable record is an
/// `Error`, and the disclosure gate fails closed on it. With no blob storage
/// composed every team sits at the default. Registered by
/// `FactsCompose.withTeamOutputVisibility` (Phase 936) — before it, only the
/// AI companion registered it.
type TeamPolicyOutputVisibilitySource(services: IServiceProvider) =
    interface ITeamOutputVisibilitySource with
        member _.Current(teamId: string) = async {
            let settings =
                TeamPolicySettings.output services
                |> Option.defaultValue TeamOutputVisibilitySettings.unrestricted

            match services.GetService(typeof<IBlobStorage>) with
            | :? IBlobStorage as storage ->
                match! TeamPolicyStore(storage).ReadOutput(TeamConversationPolicyRecord.containerOf teamId) with
                | Ok record -> return Ok(TeamOutputPolicyRecord.current settings.Default record)
                | Error message -> return Error message
            | _ -> return Ok settings.Default
        }

// ─── The startup check ───────────────────────────────────────────

/// Refuses, at startup, deployment DEFAULTS that already break the
/// policy-change check: a team that chose nothing would sit at a conversation
/// level wider than its output level. Registered with the output axis
/// (`FactsCompose.withTeamOutputVisibility`), as an INSTANCE — the preflight
/// aggregator refuses a factory-registered validator. It reads the
/// conversation declaration from the finished service collection when it
/// runs, so the order the two axes are composed in does not matter; with no
/// conversation axis composed (no assistant) there is nothing to conflict
/// with, and it passes.
type TeamVisibilityDefaultsValidator(output: TeamOutputVisibilitySettings, services: IServiceCollection) =
    /// The validator's stable name.
    static member ValidatorName = "team-visibility-defaults"

    interface ConfigValidation.IConfigValidator with
        member _.Name = TeamVisibilityDefaultsValidator.ValidatorName
        member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

        member _.Validate() = async {
            let conversation =
                services
                |> Seq.filter (fun d -> d.ServiceType = typeof<TeamConversationVisibilitySettings>)
                |> Seq.tryLast
                |> Option.bind (fun d ->
                    match d.ImplementationInstance with
                    | :? TeamConversationVisibilitySettings as s -> Some s
                    | _ -> None)

            match conversation with
            | None -> return ConfigValidation.ValidationResult.Ok
            | Some conversation ->
                match TeamOutputVisibility.checkCombination conversation.Default [ output.Default ] with
                | Ok() -> return ConfigValidation.ValidationResult.Ok
                | Error message ->
                    return
                        ConfigValidation.ValidationResult.Error(
                            $"The deployment defaults for team visibility conflict. {message}"
                        )
        }

// ─── The audit row ───────────────────────────────────────────────

/// The audit row a change of a team's output level writes: who, the old
/// level and the new. Rides `RemotingMethodAudited` with a `Custom:` kind,
/// like the conversation rows, so no exhaustive match changes.
module OutputVisibilityAudit =
    /// Kind of the row a change of a team's output level writes.
    [<Literal>]
    let LevelChangedKind = "Custom:TeamOutputVisibilityChanged"

    /// A change of a team's output level.
    let levelChanged
        (audit: IAuditLog option)
        (scopeId: string)
        (teamId: string)
        (changedBy: string)
        (oldLevel: TeamVisibilityLevel)
        (newLevel: TeamVisibilityLevel)
        =
        async {
            match audit with
            | Some log ->
                do!
                    log.Record(
                        scopeId,
                        RemotingMethodAudited {
                            Kind = LevelChangedKind
                            MethodName = "SetOutputVisibility"
                            SubjectId = changedBy
                            CorrelationId = None
                            Payload =
                                Map.ofList [
                                    "teamId", teamId
                                    "changedBy", changedBy
                                    "oldLevel", TeamVisibilityLevel.name oldLevel
                                    "newLevel", TeamVisibilityLevel.name newLevel
                                ]
                        }
                    )
            | None -> ()
        }

// ─── The API ─────────────────────────────────────────────────────

/// `TeamOutputVisibilityApi` over the request. Mounted by the platform in
/// every deployment; it answers "not enabled" until team output visibility
/// is composed. The team is always the caller's active team. The change
/// rules are conversation visibility's (the team `Owner` sets it,
/// `PlatformAdmins` only by a platform admin, within the deployment's
/// allowed set), and — when the conversation axis exists — a change is
/// refused when it would leave conversation visibility wider than output
/// visibility.
let teamOutputVisibilityApi (ctx: HttpContext) : TeamOutputVisibilityApi =
    let services = ctx.RequestServices
    let conversationSettings = TeamPolicySettings.conversation services
    let outputSettings = TeamPolicySettings.output services

    let storage =
        match services.GetService(typeof<IBlobStorage>) with
        | :? IBlobStorage as s -> Some s
        | _ -> None

    let audit =
        match services.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as a -> Some a
        | _ -> None

    let access =
        match services.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> Some ac
        | _ -> None

    let team =
        match access |> Option.map _.Subject with
        | Some(TeamMember(userId, teamId)) -> Some(userId, teamId, TeamConversationPolicyRecord.containerOf teamId)
        | _ -> None

    let notEnabledView (inTeamScope: bool) = {
        InTeamScope = inTeamScope
        Enabled = false
        Level = TeamVisible
        Allowed = [ TeamVisible ]
        Selectable = []
    }

    let viewOf (settings: TeamOutputVisibilitySettings) (record: TeamOutputPolicyRecord) (viewer: TeamPolicyViewer) =
        let current = TeamOutputPolicyRecord.current settings.Default record

        {
            InTeamScope = true
            Enabled = true
            Level = current
            Allowed = settings.Allowed
            Selectable = TeamOutputVisibility.selectable settings current viewer.TeamRole viewer.IsPlatformAdmin
        }

    {
        GetOutputVisibility =
            fun () -> async {
                match team, storage, outputSettings with
                | Some(userId, teamId, container), Some storage, Some settings ->
                    let! viewer = TeamPolicyViewer.resolve services access userId teamId

                    match! TeamPolicyStore(storage).ReadOutput container with
                    | Ok record -> return viewOf settings record viewer
                    | Error _ ->
                        // Unreadable: the gate denies every restricted fact.
                        // Show the narrowest level and offer no change.
                        return {
                            InTeamScope = true
                            Enabled = true
                            Level = PlatformAdmins
                            Allowed = settings.Allowed
                            Selectable = []
                        }
                | Some _, _, None -> return notEnabledView true
                | Some _, None, Some settings ->
                    return {
                        InTeamScope = true
                        Enabled = true
                        Level = settings.Default
                        Allowed = settings.Allowed
                        Selectable = []
                    }
                | None, _, _ -> return notEnabledView false
            }

        SetOutputVisibility =
            fun requested -> async {
                match team, storage, outputSettings with
                | Some(userId, teamId, container), Some storage, Some settings ->
                    let! viewer = TeamPolicyViewer.resolve services access userId teamId

                    let decide (record: TeamConversationPolicyRecord, output: TeamOutputPolicyRecord) =
                        let current = TeamOutputPolicyRecord.current settings.Default output

                        TeamOutputVisibility.checkChange
                            settings
                            current
                            viewer.TeamRole
                            viewer.IsPlatformAdmin
                            requested
                        |> Result.bind (fun () ->
                            if requested = current then
                                Ok(record, output)
                            else
                                let combination =
                                    match conversationSettings with
                                    | Some conversation ->
                                        TeamOutputVisibility.checkCombination
                                            (TeamConversationPolicyRecord.current conversation.Default record)
                                            [ requested ]
                                    // No conversation axis composed: nothing
                                    // can quote the output, so nothing to check.
                                    | None -> Ok()

                                combination
                                |> Result.map (fun () ->
                                    record,
                                    {
                                        OutputChanges =
                                            output.OutputChanges
                                            @ [
                                                {
                                                    Level = requested
                                                    ChangedBy = userId
                                                    ChangedAt = DateTime.UtcNow
                                                }
                                            ]
                                    }))

                    match! TeamPolicyStore(storage).ChangeBoth(container, "output visibility", decide) with
                    | Error refusal -> return Error refusal
                    | Ok((_, before), (_, after)) ->
                        if after <> before then
                            do!
                                OutputVisibilityAudit.levelChanged
                                    audit
                                    teamId
                                    teamId
                                    userId
                                    (TeamOutputPolicyRecord.current settings.Default before)
                                    (TeamOutputPolicyRecord.current settings.Default after)

                        return Ok(viewOf settings after viewer)
                | _, _, None -> return Error "Team output visibility is not enabled in this deployment."
                | Some _, None, Some _ ->
                    return Error "Output visibility cannot be stored: no blob storage is composed."
                | None, _, Some _ -> return Error "Output visibility is a team setting; there is no active team."
            }
    }