// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 896 — team output visibility ──────────────────────────
//
// Two axes, deliberately separate:
//
//   * **Module permission governs USE.** Whether a user may open a module,
//     call its API or have the assistant call its tools is decided by the
//     module permission map (`AccessContext.ModulePermissions`).
//   * **Team policy governs OUTPUT visibility.** Who sees what a module
//     PUBLISHED into the team's scope (its facts, as retrieval, tool
//     results, exports and the browse surface serve them) is decided by the
//     team's output-visibility level, below.
//
// The asymmetry is intended: a user may see a module's published output
// without being permitted to use the module. The module states what is
// publishable (each fact's disclosure class); the team states who sees it.
// The team level governs output published under a `Restricted` policy: a
// `Surfaceable` fact stays visible to every viewer the scope admits, and an
// `Internal` fact is never disclosed, exactly as before.
//
// This file is the single home of the level type both axes of team
// visibility share (conversation visibility, Phase 859, names it
// `ToolUp.AI.TeamConversationVisibility`), of the output rule, and of the
// one coupling check between the two.
//
// Phase 936 — it is also the home of the per-team policy RECORD both axes
// are stored in, of the two deployment declarations, and of the
// read-and-set contract for the output level, so a deployment that
// composes facts without the AI assistant has all of it. The store over the
// record lives in `ToolUp.Platform.Server` (`TeamPolicyStore`).

/// The three team visibility levels — one vocabulary for everything a team
/// can limit: who, besides its author, sees a conversation (Phase 859) and
/// who sees the output modules published into the team (Phase 896).
type TeamVisibilityLevel =
    /// Every member of the team. The default for both axes: a team that has
    /// chosen nothing behaves exactly as before either phase.
    | TeamVisible
    /// The team's `Owner` and `Admin` roles only.
    | TeamAdmins
    /// Holders of `PlatformRole.PlatformAdmin` only.
    | PlatformAdmins

/// Functions over the level alone.
module TeamVisibilityLevel =
    /// Every level, widest first.
    let all: TeamVisibilityLevel list = [ TeamVisible; TeamAdmins; PlatformAdmins ]

    /// Stable name of a level, as a refusal or an audit row names it.
    let name (level: TeamVisibilityLevel) : string =
        match level with
        | TeamVisible -> "TeamVisible"
        | TeamAdmins -> "TeamAdmins"
        | PlatformAdmins -> "PlatformAdmins"

    /// Whether every member of a team admitted at `inner` is also admitted
    /// at `outer`. The levels are not a chain: `TeamAdmins` and
    /// `PlatformAdmins` each admit someone the other does not (a team admin
    /// who is not a platform admin, and the reverse), so neither is within
    /// the other. Only `TeamVisible` contains the other two.
    let isWithin (inner: TeamVisibilityLevel) (outer: TeamVisibilityLevel) : bool = inner = outer || outer = TeamVisible

/// What the deployment declares about output visibility (Phase 896): the
/// level a team starts with, and the levels a team may choose from. The same
/// shape, and the same rules, as the conversation-visibility declaration.
type TeamOutputVisibilitySettings = {
    /// The level in force for a team that has chosen nothing.
    Default: TeamVisibilityLevel
    /// The levels a team owner may select. Always contains `Default`.
    Allowed: TeamVisibilityLevel list
}

/// Constructors for `TeamOutputVisibilitySettings`.
module TeamOutputVisibilitySettings =
    /// Nothing declared: `TeamVisible` by default, every level allowed.
    let unrestricted: TeamOutputVisibilitySettings = {
        Default = TeamVisible
        Allowed = TeamVisibilityLevel.all
    }

    /// Validate a declaration. The allowed set must be non-empty and must
    /// contain the default, or a team that never chose would sit at a level
    /// no one could select back to.
    let create
        (defaultLevel: TeamVisibilityLevel)
        (allowed: TeamVisibilityLevel list)
        : Result<TeamOutputVisibilitySettings, string> =
        let allowed = allowed |> List.distinct

        if List.isEmpty allowed then
            Error "withTeamOutputVisibility: the allowed set is empty; a team could select no level at all."
        elif not (List.contains defaultLevel allowed) then
            Error(
                sprintf
                    "withTeamOutputVisibility: the default %s is not in the allowed set [%s]."
                    (TeamVisibilityLevel.name defaultLevel)
                    (allowed |> List.map TeamVisibilityLevel.name |> String.concat ", ")
            )
        else
            Ok {
                Default = defaultLevel
                Allowed = TeamVisibilityLevel.all |> List.filter (fun l -> List.contains l allowed)
            }

    /// The allowed set, named, for a refusal.
    let describeAllowed (settings: TeamOutputVisibilitySettings) : string =
        settings.Allowed |> List.map TeamVisibilityLevel.name |> String.concat ", "

/// What the deployment declares about conversation visibility (Phase 859):
/// the level a team starts with, and the levels a team may choose from.
/// Composed by the AI companion (`AICompose.withTeamConversationVisibility`),
/// which registers `unrestricted` when nothing is declared; it lives here
/// (Phase 936) because the output axis's change check and startup check read
/// its default, and a deployment without the assistant has no conversation
/// axis at all.
type TeamConversationVisibilitySettings = {
    /// The level in force for a team that has chosen nothing.
    Default: TeamVisibilityLevel
    /// The levels a team owner may select. Always contains `Default`.
    Allowed: TeamVisibilityLevel list
}

/// Constructors for `TeamConversationVisibilitySettings`.
module TeamConversationVisibilitySettings =
    /// Nothing declared: `TeamVisible` by default, every level allowed — a
    /// team that has set nothing behaves exactly as before Phase 859.
    let unrestricted: TeamConversationVisibilitySettings = {
        Default = TeamVisible
        Allowed = TeamVisibilityLevel.all
    }

    /// Validate a declaration. The allowed set must be non-empty and must
    /// contain the default, or a team that never chose would sit at a level
    /// no one could select back to.
    let create
        (defaultLevel: TeamVisibilityLevel)
        (allowed: TeamVisibilityLevel list)
        : Result<TeamConversationVisibilitySettings, string> =
        let allowed = allowed |> List.distinct

        if List.isEmpty allowed then
            Error "withTeamConversationVisibility: the allowed set is empty; a team could select no level at all."
        elif not (List.contains defaultLevel allowed) then
            Error(
                sprintf
                    "withTeamConversationVisibility: the default %s is not in the allowed set [%s]."
                    (TeamVisibilityLevel.name defaultLevel)
                    (allowed |> List.map TeamVisibilityLevel.name |> String.concat ", ")
            )
        else
            Ok {
                Default = defaultLevel
                Allowed = TeamVisibilityLevel.all |> List.filter (fun l -> List.contains l allowed)
            }

    /// The allowed set, named, for a refusal.
    let describeAllowed (settings: TeamConversationVisibilitySettings) : string =
        settings.Allowed |> List.map TeamVisibilityLevel.name |> String.concat ", "

// ─── The per-team policy record (Phase 859 / 896; platform tier since 936) ──

/// One change of a team's level, on either axis.
type TeamVisibilityChange = {
    /// The level from `ChangedAt` on.
    Level: TeamVisibilityLevel
    /// The user who changed it.
    ChangedBy: string
    /// When, UTC.
    ChangedAt: System.DateTime
}

/// The conversation part of a team's policy record (Phase 859), stored in
/// the team's container — deliberately not a field on `TeamPermissions` and
/// not a case on the admin-mutation union. It keeps every change, oldest
/// first, so the level in force when a conversation was created is always
/// decidable without adding a field to the conversation.
type TeamConversationPolicyRecord = {
    /// Every change, oldest first. Empty for a team that never chose.
    Changes: TeamVisibilityChange list
}

/// Functions over the conversation part of the record.
module TeamConversationPolicyRecord =
    /// A team that has chosen nothing.
    let empty: TeamConversationPolicyRecord = { Changes = [] }

    /// Blob name inside the team's container. Kept, not renamed, when the
    /// record moved to the platform tier (Phase 936): every record already
    /// stored reads from where it was written, and no migration runs.
    [<Literal>]
    let BlobName = "team-policies/ai-conversation-visibility.json"

    /// The container a team's record lives in — the team's own container,
    /// read in the team's scope.
    let containerOf (teamId: string) = "team-" + teamId

    /// Whether the team has ever chosen a level.
    let isUnset (record: TeamConversationPolicyRecord) = List.isEmpty record.Changes

    /// The level in force now: the last change, else the deployment default.
    let current (defaultLevel: TeamVisibilityLevel) (record: TeamConversationPolicyRecord) =
        record.Changes
        |> List.tryLast
        |> Option.map _.Level
        |> Option.defaultValue defaultLevel

    /// The level in force at `at`: the last change made at or before it,
    /// else the deployment default.
    let levelAt (defaultLevel: TeamVisibilityLevel) (record: TeamConversationPolicyRecord) (at: System.DateTime) =
        record.Changes
        |> List.filter (fun c -> c.ChangedAt <= at)
        |> List.tryLast
        |> Option.map _.Level
        |> Option.defaultValue defaultLevel

/// The output part of a team's policy record (Phase 896). Held in the SAME
/// stored record as the conversation part — the same blob, under the same
/// guarded write — as an `OutputChanges` member beside `Changes`. A team
/// that never set an output level stores nothing new, so its record is
/// byte-identical to one written before Phase 896.
type TeamOutputPolicyRecord = {
    /// Every change of the team's output level, oldest first. Empty for a
    /// team that never chose.
    OutputChanges: TeamVisibilityChange list
}

/// Functions over the output part of the record.
module TeamOutputPolicyRecord =
    /// A team that has chosen nothing.
    let empty: TeamOutputPolicyRecord = { OutputChanges = [] }

    /// The member of the stored record that holds the output history.
    [<Literal>]
    let MemberName = "OutputChanges"

    /// Whether the team has ever chosen an output level.
    let isUnset (record: TeamOutputPolicyRecord) = List.isEmpty record.OutputChanges

    /// The output level in force now: the last change, else the deployment
    /// default.
    let current (defaultLevel: TeamVisibilityLevel) (record: TeamOutputPolicyRecord) =
        record.OutputChanges
        |> List.tryLast
        |> Option.map _.Level
        |> Option.defaultValue defaultLevel

/// Reads the output-visibility level in force for a team (Phase 896). The
/// seam between the store that holds a team's policy record and the
/// disclosure gate that enforces it, so neither takes a type dependency on
/// the other.
type ITeamOutputVisibilitySource =
    /// The level in force for `teamId`: the team's own choice, else the
    /// deployment default. `Error` when the team's record exists but cannot
    /// be read — the caller fails closed.
    abstract Current: teamId: string -> Async<Result<TeamVisibilityLevel, string>>

/// The output-visibility rule (Phase 896). `canSee` is the single place it
/// lives; the disclosure gate applies it to every egress door.
module TeamOutputVisibility =

    let private isTeamAdmin (viewerTeamRole: TeamRole option) =
        match viewerTeamRole with
        | Some TeamRole.Owner
        | Some TeamRole.Admin -> true
        | Some TeamRole.Member
        | None -> false

    /// Whether a viewer may see `Restricted` output the team published,
    /// under the team's output level `policy`.
    ///
    ///  - `TeamVisible`: every viewer the scope admits. Scope isolation has
    ///    already decided who reaches the team's scope, so this level adds
    ///    nothing — it is the pre-896 behaviour.
    ///  - `TeamAdmins`: a team `Owner` or `Admin`.
    ///  - `PlatformAdmins`: a platform admin.
    ///
    /// Module permission is deliberately NOT an input: permission governs
    /// using a module, this rule governs seeing what it published.
    let canSee (policy: TeamVisibilityLevel) (viewerTeamRole: TeamRole option) (viewerIsPlatformAdmin: bool) : bool =
        match policy with
        | TeamVisible -> true
        | TeamAdmins -> isTeamAdmin viewerTeamRole
        | PlatformAdmins -> viewerIsPlatformAdmin

    /// The policy-change check. A conversation can quote a fact, so a team
    /// whose conversations are visible to someone its output is not visible
    /// to would let a member read, in a colleague's conversation, output
    /// they may not see. Refuse any combination in which the conversation
    /// level admits a member that one of `outputLevels` does not, naming
    /// both levels. `outputLevels` is every output level in force in the
    /// team (today the team's own level); an empty list refuses nothing.
    ///
    /// Run when either level CHANGES. Citations are not re-checked when a
    /// conversation is opened — this check is the cheap control, and the
    /// complicated one is deliberately not offered.
    let checkCombination
        (conversationLevel: TeamVisibilityLevel)
        (outputLevels: TeamVisibilityLevel list)
        : Result<unit, string> =
        match
            outputLevels
            |> List.tryFind (fun output -> not (TeamVisibilityLevel.isWithin conversationLevel output))
        with
        | None -> Ok()
        | Some output ->
            Error(
                sprintf
                    "Conversation visibility %s is wider than output visibility %s: a conversation can quote output that some of its readers may not see. Set conversation visibility to %s, or widen output visibility, first."
                    (TeamVisibilityLevel.name conversationLevel)
                    (TeamVisibilityLevel.name output)
                    (TeamVisibilityLevel.name output)
            )

    /// The levels a caller may select now. The same change rules as
    /// conversation visibility: only the team `Owner` changes the level,
    /// and `PlatformAdmins` is selectable, and leavable, only by a platform
    /// admin. Empty when the caller may not change it at all.
    let selectable
        (settings: TeamOutputVisibilitySettings)
        (current: TeamVisibilityLevel)
        (role: TeamRole option)
        (isPlatformAdmin: bool)
        : TeamVisibilityLevel list =
        match role with
        | Some TeamRole.Owner when current <> PlatformAdmins || isPlatformAdmin ->
            settings.Allowed
            |> List.filter (fun level -> level <> PlatformAdmins || isPlatformAdmin)
        | _ -> []

    /// Refuse a change of a team's output level, or `Ok ()`. `current` is
    /// the level read inside the guarded write, so a concurrent change is
    /// judged against what is actually stored. The policy-change check
    /// against the conversation level is the caller's second step.
    let checkChange
        (settings: TeamOutputVisibilitySettings)
        (current: TeamVisibilityLevel)
        (role: TeamRole option)
        (isPlatformAdmin: bool)
        (requested: TeamVisibilityLevel)
        : Result<unit, string> =
        if not (List.contains requested settings.Allowed) then
            Error(
                sprintf
                    "%s is not an allowed output visibility in this deployment. Allowed: %s."
                    (TeamVisibilityLevel.name requested)
                    (TeamOutputVisibilitySettings.describeAllowed settings)
            )
        else
            match role with
            | None -> Error "You are not a member of this team."
            | Some TeamRole.Admin
            | Some TeamRole.Member -> Error "Only the team owner can set output visibility."
            | Some TeamRole.Owner ->
                if requested = PlatformAdmins && not isPlatformAdmin then
                    Error "Only a platform admin can restrict output to platform admins."
                elif current = PlatformAdmins && requested <> PlatformAdmins && not isPlatformAdmin then
                    Error "This team's output is restricted to platform admins; only a platform admin can change that."
                else
                    Ok()

    /// The sentence a member reads to know who sees the team's output.
    let describe (level: TeamVisibilityLevel) : string =
        match level with
        | TeamVisible -> "Every member of this team can see what its modules publish."
        | TeamAdmins -> "Only this team's owners and admins can see restricted output its modules publish."
        | PlatformAdmins -> "Only the platform administrators can see restricted output this team's modules publish."
// ─── The read-and-set contract (Phase 896; platform tier since 936) ──

/// The output-visibility level in force for the caller's active team
/// (Phase 896).
type TeamOutputVisibilityView = {
    /// False outside a team scope: there is no team level to show or set.
    InTeamScope: bool
    /// False when the deployment has not composed team output visibility:
    /// the level shown is `TeamVisible` and cannot be changed.
    Enabled: bool
    /// The level in force now.
    Level: TeamVisibilityLevel
    /// The levels this deployment lets a team choose from.
    Allowed: TeamVisibilityLevel list
    /// The levels THIS caller may select now. Empty when the caller may not
    /// change the level at all.
    Selectable: TeamVisibilityLevel list
}

/// Read and set the active team's output visibility (Phase 896). Mounted by
/// the platform in every deployment (Phase 936), answering "not enabled"
/// until team output visibility is composed; the team is always the
/// caller's active team, never a request field.
type TeamOutputVisibilityApi = {
    /// The level in force for the caller's active team, and what the caller
    /// may change it to.
    [<AllowAnonymous>]
    GetOutputVisibility: unit -> Async<TeamOutputVisibilityView>
    /// Set the active team's level. Only the team `Owner` may; selecting
    /// `PlatformAdmins`, or leaving it, also needs a platform admin. A level
    /// outside the deployment's allowed set is refused, naming the set, and
    /// so is a level that would leave conversation visibility wider than
    /// output visibility, naming both.
    [<RequiresClaim "scope">]
    SetOutputVisibility: TeamVisibilityLevel -> Async<Result<TeamOutputVisibilityView, string>>
}