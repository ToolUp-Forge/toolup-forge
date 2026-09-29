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