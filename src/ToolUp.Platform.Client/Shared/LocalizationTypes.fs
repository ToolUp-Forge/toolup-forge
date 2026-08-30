// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Client-shell localization types (Phase 444) ───────────────────────
//
// The client shell had no i18n at all: shell chrome, the SDK's built-in
// modules and their validation messages were hardcoded English, which
// blocked a non-English deployment however carefully the consumer
// localised its own modules.
//
// The substrate here is deliberately NOT a second string-key table.
// The Phase 12a `Translations` map (`LocaleTypes.fs`, Core) already
// covers the *open* case — a module shipping its own keys, resolved at
// runtime, missing entries degrading to the key text. That shape is
// right for content a module contributes and wrong for the SDK's own
// chrome, where the set of strings is closed, known at compile time,
// and a missing one is a defect rather than a degradation.
//
// So the shell catalog is a plain F# record. **The record IS the
// schema**: a consumer supplying a second language writes a record
// value, and a translation they forgot is a missing field the compiler
// names (GP 8 — use the build-time advantage instead of importing an
// i18n framework that re-implements it at runtime). Grouping is by
// surface (one nested record per shell surface / built-in module) so
// record-update syntax makes a partial override natural:
//
//     { english with Shell = { english.Shell with SignOut = "Se déconnecter" } }
//
// Fable-safety rules this file obeys, and why:
//   * no reflection, no attribute scanning, no `typeof<_>` — the whole
//     catalog is data the emitted bundle can tree-shake;
//   * no runtime format DSL. A message taking a parameter is a
//     `string -> string` FUNCTION field, so the substitution points are
//     part of the type. `"{0} of {1}"`-style templates move the arity
//     error from compile time to a wrong-looking string in production;
//   * no bundled CLDR data. Date / number / currency formatting
//     delegates to the browser's `Intl` via the resolved locale
//     (`MessageCatalogProvider.formatNumber` and friends) — GP 13.

/// How the shell resolves the active locale.
///
/// Default is `FixedLocale "en"`, which is why a deployment that never
/// touches `ClientConfig.Locale` renders byte-for-byte as it did before
/// Phase 444: the resolution collapses to the constant `"en"`, the
/// built-in English catalog is returned unmodified, and no browser or
/// team-config read happens at all (GP 11 / GP 13).
type LocaleMode =
    /// One locale for the whole deployment, whatever the browser or the
    /// team config says. `FixedLocale "en"` is the SDK default.
    | FixedLocale of locale: string
    /// Read the visitor's browser preference (`navigator.language`),
    /// falling back to `fallback` when it is unavailable or blank —
    /// which is every non-browser host, so the .NET-side resolution is
    /// total without a browser stub.
    | BrowserLocale of fallback: string
    /// The active team's default locale, stored beside the Phase 5e
    /// branding values in the `_platform` config under
    /// `_platform.locale` (the same key the server-side
    /// `LocaleResolver` reads, so one team setting drives both tiers).
    /// Falls through to the browser preference, then to `fallback`.
    | TeamDefault of fallback: string

/// Shell chrome — the header, the team switcher, the no-team gate, the
/// area switcher, and the messages the shell itself raises.
type ShellMessages = {
    /// Prefix on the header team switcher ("Team:").
    TeamLabel: string
    /// Placeholder shown by the team switcher when no team is active.
    SelectTeam: string
    /// Toast raised when `SetActiveTeam` fails.
    SwitchTeamFailed: string
    /// Toast raised when team creation fails.
    CreateTeamFailed: string
    /// Heading of the no-active-team gate.
    NoTeamHeading: string
    /// Body prose of the no-active-team gate.
    NoTeamBody: string
    /// Placeholder of the inline create-team field.
    TeamNamePlaceholder: string
    /// Label of the create-team button while the create is in flight.
    CreatingTeam: string
    /// Label of the create-team button at rest.
    CreateTeam: string
    /// Prompt above the pick-a-team list.
    PickTeam: string
    /// Header sign-out action.
    SignOut: string
    /// Sidebar entry returning from the Administration area to the app.
    BackToApp: string
    /// Display name of the Administration navigation area.
    AdministrationArea: string
    /// Display name of the Product navigation area.
    ProductArea: string
    /// Tooltip while the platform-admin "show hidden modules" toggle is ON.
    ShowingAllModulesHint: string
    /// Tooltip while the toggle is OFF (member view).
    MemberViewHint: string
    /// Label of the toggle while ON.
    ViewingAllModules: string
    /// Label of the toggle while OFF.
    ShowHiddenModules: string
    /// Cross-module result notification — takes the producing module's
    /// display name.
    ResultsAvailableIn: string -> string
    /// Placeholder rendered when a multi-page module has no view
    /// registered for the active route — takes the route.
    NoViewForRoute: string -> string
    /// Rendered when the active module id resolves to nothing.
    ModuleNotFound: string
}

/// Human labels for the boot-degradation banner's per-source rows —
/// the shell's `sourceLabel` fold. Keys stay the stable machine ids
/// (`"teams"`, `"permissions"`, …); only the display text is localised.
type BootSourceMessages = {
    Teams: string
    ActiveTeam: string
    TeamAutoSelect: string
    Permissions: string
    Configs: string
    Flags: string
    PlatformRole: string
    TeamRole: string
    AuthBridge: string
}

/// The dismissible banner that accumulates failed boot loads (Phase 121).
type BootDegradationMessages = {
    /// Banner heading.
    Heading: string
    /// Per-row retry action.
    Retry: string
    /// Accessible name of the dismiss control.
    Dismiss: string
    /// Display names for each degraded boot source.
    Sources: BootSourceMessages
}

/// The toast centre's severity badges.
type ToastMessages = {
    Info: string
    Warning: string
    Error: string
}

/// The route guard's "not authorised" surface (Phase 569). One entry
/// per `SidebarVisibility.NavigationDenial` case — adding a denial case
/// upstream therefore fails to compile here until it is worded, which
/// is the property this record exists for.
type NotAuthorisedMessages = {
    /// Headings. Several denials deliberately share one heading in
    /// English; a translation is free to distinguish them.
    TitleNotSignedIn: string
    TitleNoActiveTeam: string
    TitleNotInVisibilityProfile: string
    TitleNoAccess: string
    /// Remedy sentences. Each takes the refused module's display name,
    /// so the substitution point is typed rather than a `%s` a
    /// translator can drop.
    HintNotSignedIn: string -> string
    HintRequiresPlatformAdmin: string -> string
    HintRequiresTeamOwnerAdmin: string -> string
    HintNotExposedToTeam: string -> string
    HintNotAvailableToSubject: string -> string
    HintNoActiveTeam: string -> string
    HintNotInVisibilityProfile: string -> string
    /// The always-offered route home out of a refused deep link.
    GoHome: string
}

/// The per-module error boundary (Phase 12c).
type ModuleBoundaryMessages = {
    Heading: string
    Body: string
    Reload: string
}

/// The Ctrl+K command palette (Phase 571).
type CommandPaletteMessages = {
    /// Accessible name of the palette dialog.
    DialogLabel: string
    /// Accessible name of the query input.
    SearchLabel: string
    /// Query-input placeholder.
    SearchPlaceholder: string
    /// Empty state when nothing matches.
    NoMatches: string
    /// Footer key hints.
    HintMove: string
    HintOpen: string
    HintClose: string
}

/// The sidebar's per-entry controls and chrome.
type SidebarMessages = {
    /// Title of the pinned section.
    PinnedSection: string
    Pin: string
    Unpin: string
    Hide: string
    Restore: string
    /// Footer attribution rendered under the rail.
    PoweredBy: string
}

/// The built-in team-management module (`TeamManagerUI`).
type TeamManagerMessages = {
    MyTeamsPanel: string
    NoTeamsYet: string
    Switch: string
    Manage: string
    ActiveBadge: string
    MembersPanel: string
    NoMembers: string
    YouSuffix: string
    RemoveMember: string
    RoleLabel: string
    InvitePanel: string
    InviteHelp: string
    Inviting: string
    InviteMember: string
    IdentifierRequired: string
    EmailRequired: string
    TransferOwnership: string
    /// Takes the team's display name.
    TransferOwnershipHelp: string -> string
    TransferFilterPlaceholder: string
    TransferNoOtherMembers: string
    TransferNoMatches: string
    TransferConfirmHeading: string
    /// Takes team name, outgoing owner label, incoming owner label.
    TransferConfirmPrompt: string -> string -> string -> string
    Transferring: string
    ConfirmTransfer: string
    Back: string
    Cancel: string
    BreadcrumbMyTeams: string
    BreadcrumbMembers: string
    PendingInvites: string
    PendingInvitesPanel: string
    PendingInvitesLoading: string
    NoPendingInvites: string
    InviteByEmail: string
    InviteByEmailHeading: string
    InviteByEmailHelp: string
    EmailPlaceholder: string
    ExpiresInDays: string
    Issuing: string
    IssueInvitation: string
    RevokeInvite: string
    RevokeInviteHeading: string
    Reissue: string
    Reissuing: string
    Expired: string
    RecentlyExpired: string
    Dismiss: string
}

/// The built-in health-monitor module (`HealthMonitorUI`).
type HealthMonitorMessages = {
    LiveHealthTab: string
    PreflightTab: string
    Refresh: string
    Refreshing: string
    Refetch: string
    ColumnStatus: string
    ColumnProbe: string
    ColumnKind: string
    ColumnTimeout: string
    ColumnElapsed: string
    ColumnMessage: string
    ColumnValidator: string
    NoProbes: string
    NoValidators: string
    ProbesFootnote: string
    PreflightHeading: string
    PreflightFootnote: string
    PreflightUnavailable: string
    SchedulerLagHelp: string
    /// Takes the degraded-capability count.
    DegradedCapabilities: int -> string
    DegradedCapabilitiesHelp: string
    Remediation: string
    /// Takes the formatted timestamp.
    AsOf: string -> string
    /// Takes the timestamp the capability degraded at.
    DegradedSince: string -> string
    /// Takes the snapshot timestamp and the probe count.
    GeneratedAt: string -> int -> string
    ModuleBlurb: string
}

/// The built-in data-ingestion / data-source module (`DataIngestionUI`).
type DataIngestionMessages = {
    StatusNotConfigured: string
    StatusNeedsAuthorization: string
    StatusConnected: string
    StatusNeedsReauthorization: string
    OutcomeRefreshed: string
    OutcomeTransientError: string
    OutcomeRequiresReauth: string
    OutcomeDeadLettered: string
    OutcomePending: string
    Connect: string
    Disconnect: string
    Disconnecting: string
    Refresh: string
    Refreshing: string
    ColumnStatus: string
    ColumnName: string
    ColumnKind: string
    ColumnTokenStatus: string
    ColumnActions: string
    NoSourcesYet: string
    NoCredentialUIsRegistered: string
    /// Takes the connector kind with no registered credential UI.
    NoCredentialUIForKind: string -> string
    /// Takes the kind that was unregistered mid-selection.
    CredentialUIUnregistered: string -> string
    /// Takes the connector kind — heading of the new-source form.
    NewSourceHeading: string -> string
    /// Takes the formatted timestamp a token was issued at.
    SinceLabel: string -> string
    /// Takes the formatted timestamp of the last refresh attempt.
    AtLabel: string -> string
    /// Takes the formatted timestamp of the next scheduled refresh.
    NextLabel: string -> string
    OAuthFootnote: string
    ModuleBlurb: string
}

/// The closed set of strings the SDK's own shell and built-in modules
/// render. One nested record per surface; `Locale` carries the BCP 47
/// tag the shell resolved, so a `MessageCatalogOverride` can branch on
/// it (that is how one override function serves several languages —
/// see `ClientConfig.MessageCatalogOverride`).
///
/// Consumer modules are NOT in scope here and never will be: a
/// deployment localises its own module strings its own way, or through
/// the Phase 12a `Translations` table. This record is the SDK's
/// surface, and its being closed is precisely what makes the
/// compile-time completeness check meaningful.
type MessageCatalog = {
    /// The resolved BCP 47 locale tag this catalog is for. `"en"` on
    /// the built-in catalog. Set by `MessageCatalog.forLocale` before
    /// the consumer's override runs, so the override can select a
    /// language without the shell needing a per-locale registry.
    Locale: string
    Shell: ShellMessages
    BootDegradation: BootDegradationMessages
    Toast: ToastMessages
    NotAuthorised: NotAuthorisedMessages
    ModuleBoundary: ModuleBoundaryMessages
    CommandPalette: CommandPaletteMessages
    Sidebar: SidebarMessages
    TeamManager: TeamManagerMessages
    HealthMonitor: HealthMonitorMessages
    DataIngestion: DataIngestionMessages
}