// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The shell's boot sources as one table. Each row says, for one source, its
/// key, its catalog label, when the shell issues it, how it loads, whether the
/// banner offers Retry, whether the active module's first `Init` waits for it,
/// and which resets re-issue it. The shell's loader set, its reset re-issues,
/// its retry and the banner's labels are filters over this list, so a source
/// added here reaches all of them, and one missing a field does not compile.
///
/// The `Key` is the dedup and wire string (the banner's row id, the argument a
/// Retry carries) and nothing matches on it but the lookups below.
module ToolUp.Platform.BootSources

open ToolUp.Elmish

/// Which reset a source is re-issued by. Payload-free, so a row can declare
/// the set; `ResetKind.trigger` names the trigger a reset carries.
[<RequireQualifiedAccess>]
type ResetTrigger =
    | TeamSwitch
    | LocaleSwitch
    | TokenAcquired

/// A reset of the shell's per-scope state.
[<RequireQualifiedAccess>]
type ResetKind =
    /// The active team moved, to a team or (membership revoked) to none.
    | TeamSwitch of teamId: string option
    /// The reader chose a language in-session (`None` clears the choice).
    | LocaleSwitch of locale: string option
    /// A sign-in token arrived after the shell booted without one.
    | TokenAcquired

[<RequireQualifiedAccess>]
module ResetKind =

    let trigger (kind: ResetKind) : ResetTrigger =
        match kind with
        | ResetKind.TeamSwitch _ -> ResetTrigger.TeamSwitch
        | ResetKind.LocaleSwitch _ -> ResetTrigger.LocaleSwitch
        | ResetKind.TokenAcquired -> ResetTrigger.TokenAcquired

/// When the shell issues a source.
[<RequireQualifiedAccess>]
type BootScope =
    /// At boot, in every deployment.
    | Boot
    /// At boot, in a deployment with a team scope only.
    | TeamBoot
    /// Not at boot: the arm that learns what it needs issues it (the role
    /// once the active team is known, the snapshot once the server says
    /// the file API is mounted, the sole-team select once both team reads
    /// have answered).
    | OnDemand
    /// Not a load the shell issues: something else reports it (the auth
    /// bridge, which retries itself; a sidebar-preference write).
    | Reported

/// The loaders a row's `Load` picks from. A record, so a source whose loader
/// is missing fails to compile rather than retrying nothing.
type BootLoads<'model, 'msg> = {
    Permissions: Cmd<'msg>
    Configs: Cmd<'msg>
    Flags: Cmd<'msg>
    Teams: Cmd<'msg>
    ActiveTeam: Cmd<'msg>
    PlatformRole: Cmd<'msg>
    PlatformInfo: Cmd<'msg>
    DataSnapshot: Cmd<'msg>
    /// The caller's role on the model's active team (cleared with none).
    TeamRole: 'model -> Cmd<'msg>
    /// The sole-team select, when the model says there is one to make.
    TeamAutoSelect: 'model -> Cmd<'msg>
}

/// One boot source.
type BootSource<'model, 'msg> = {
    /// The dedup and wire key.
    Key: string
    /// The banner row's label, from the resolved catalog.
    Label: BootSourceMessages -> string
    Scope: BootScope
    /// Issue the load. `Cmd.none` for a `Reported` source.
    Load: BootLoads<'model, 'msg> -> 'model -> Cmd<'msg>
    /// Whether the banner offers Retry (which re-runs `Load`).
    Retryable: bool
    /// Whether the active module's first `Init` waits for this source to
    /// answer, success or failure.
    GatesFirstInit: bool
    /// The resets that re-issue this source.
    ClearedBy: Set<ResetTrigger>
}

/// The keys of the table, for the shell's own references to a source.
[<RequireQualifiedAccess>]
module Key =
    [<Literal>]
    let Permissions = "permissions"

    [<Literal>]
    let Configs = "configs"

    [<Literal>]
    let Flags = "flags"

    [<Literal>]
    let PlatformRole = "platform-role"

    [<Literal>]
    let PlatformInfo = "platform-info"

    [<Literal>]
    let Teams = "teams"

    [<Literal>]
    let ActiveTeam = "active-team"

    [<Literal>]
    let TeamRole = "team-role"

    [<Literal>]
    let TeamAutoSelect = "team-auto-select"

    [<Literal>]
    let DataSnapshot = "data-snapshot"

    [<Literal>]
    let AuthBridge = "auth-bridge"

    [<Literal>]
    let SidebarPrefs = "sidebar-prefs"

let private everyReset =
    Set.ofList [
        ResetTrigger.TeamSwitch
        ResetTrigger.LocaleSwitch
        ResetTrigger.TokenAcquired
    ]

/// The table. A function rather than a value so it stays generic in the
/// shell's model and message types.
let all<'model, 'msg> () : BootSource<'model, 'msg> list = [
    {
        Key = Key.Permissions
        Label = _.Permissions
        Scope = BootScope.Boot
        Load = fun l _ -> l.Permissions
        Retryable = true
        GatesFirstInit = false
        ClearedBy = everyReset
    }
    {
        Key = Key.Configs
        Label = _.Configs
        Scope = BootScope.Boot
        Load = fun l _ -> l.Configs
        Retryable = true
        GatesFirstInit = true
        ClearedBy = everyReset
    }
    {
        Key = Key.Flags
        Label = _.Flags
        Scope = BootScope.Boot
        Load = fun l _ -> l.Flags
        Retryable = true
        GatesFirstInit = true
        ClearedBy = everyReset
    }
    {
        Key = Key.PlatformRole
        Label = _.PlatformRole
        Scope = BootScope.Boot
        Load = fun l _ -> l.PlatformRole
        Retryable = true
        GatesFirstInit = false
        // The role is the user's, not the team's: a switch does not move it.
        ClearedBy = Set.ofList [ ResetTrigger.TokenAcquired ]
    }
    {
        Key = Key.PlatformInfo
        Label = _.PlatformInfo
        Scope = BootScope.Boot
        Load = fun l _ -> l.PlatformInfo
        Retryable = true
        GatesFirstInit = false
        ClearedBy = Set.ofList [ ResetTrigger.TokenAcquired ]
    }
    {
        Key = Key.Teams
        Label = _.Teams
        Scope = BootScope.TeamBoot
        Load = fun l _ -> l.Teams
        Retryable = true
        GatesFirstInit = false
        // A switch can follow a team's creation, which the list must show.
        ClearedBy = Set.ofList [ ResetTrigger.TeamSwitch; ResetTrigger.TokenAcquired ]
    }
    {
        Key = Key.ActiveTeam
        Label = _.ActiveTeam
        Scope = BootScope.TeamBoot
        Load = fun l _ -> l.ActiveTeam
        Retryable = true
        GatesFirstInit = false
        // A switch carries the new active team; only a sign-in re-reads it.
        ClearedBy = Set.ofList [ ResetTrigger.TokenAcquired ]
    }
    {
        Key = Key.TeamRole
        Label = _.TeamRole
        Scope = BootScope.OnDemand
        Load = fun l model -> l.TeamRole model
        Retryable = true
        GatesFirstInit = false
        ClearedBy = Set.ofList [ ResetTrigger.TeamSwitch ]
    }
    {
        Key = Key.TeamAutoSelect
        Label = _.TeamAutoSelect
        Scope = BootScope.OnDemand
        Load = fun l model -> l.TeamAutoSelect model
        Retryable = true
        GatesFirstInit = false
        ClearedBy = Set.empty
    }
    {
        Key = Key.DataSnapshot
        Label = _.DataSnapshot
        Scope = BootScope.OnDemand
        Load = fun l _ -> l.DataSnapshot
        Retryable = true
        GatesFirstInit = false
        ClearedBy = Set.empty
    }
    {
        Key = Key.AuthBridge
        Label = _.AuthBridge
        Scope = BootScope.Reported
        Load = fun _ _ -> Cmd.none
        // The bridge retries itself on its refresh interval.
        Retryable = false
        GatesFirstInit = false
        ClearedBy = Set.empty
    }
    {
        Key = Key.SidebarPrefs
        Label = _.SidebarPrefs
        Scope = BootScope.Reported
        Load = fun _ _ -> Cmd.none
        // A failed preference write has nothing to re-issue: the next
        // preference change writes again.
        Retryable = false
        GatesFirstInit = false
        ClearedBy = Set.empty
    }
]

/// The row for `key`, if the table has one.
let tryFind (key: string) (sources: BootSource<'model, 'msg> list) : BootSource<'model, 'msg> option =
    sources |> List.tryFind (fun s -> s.Key = key)

let private admits (hasTeamScope: bool) (source: BootSource<'model, 'msg>) : bool =
    source.Scope <> BootScope.TeamBoot || hasTeamScope

/// The sources the shell issues at boot.
let issuedAtBoot (hasTeamScope: bool) (sources: BootSource<'model, 'msg> list) : BootSource<'model, 'msg> list =
    sources
    |> List.filter (fun s ->
        (s.Scope = BootScope.Boot || s.Scope = BootScope.TeamBoot)
        && admits hasTeamScope s)

/// The sources a reset re-issues.
let reissuedBy
    (trigger: ResetTrigger)
    (hasTeamScope: bool)
    (sources: BootSource<'model, 'msg> list)
    : BootSource<'model, 'msg> list =
    sources
    |> List.filter (fun s -> s.ClearedBy.Contains trigger && admits hasTeamScope s)

/// The keys the active module's first `Init` waits for.
let firstInitGates (sources: BootSource<'model, 'msg> list) : Set<string> =
    sources |> List.filter _.GatesFirstInit |> List.map _.Key |> Set.ofList

/// Whether the banner offers Retry for `key`. A key the table does not hold
/// has nothing to re-issue.
let retryable (key: string) (sources: BootSource<'model, 'msg> list) : bool =
    tryFind key sources |> Option.exists _.Retryable

/// The banner label for `key`; a key the table does not hold labels itself
/// with `fallback`.
let label
    (msgs: BootSourceMessages)
    (key: string)
    (fallback: string)
    (sources: BootSource<'model, 'msg> list)
    : string =
    match tryFind key sources with
    | Some source -> source.Label msgs
    | None -> fallback