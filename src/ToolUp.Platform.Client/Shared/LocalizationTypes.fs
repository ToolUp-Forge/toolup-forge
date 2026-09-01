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
    // Phase 751 — five literals Phase 444's own sweep left behind. Its
    // audit grep suppressed any all-lowercase string as a CSS class and
    // did not reach a `sprintf` template at all, so these were invisible
    // to the pass that declared this module residue-zero. Additive.
    /// Takes the team's id — the small caption under a team's name.
    TeamIdLabel: string -> string
    /// Placeholder of the invite identifier / typeahead field.
    InviteIdentifierPlaceholder: string
    /// Takes the incoming owner's label — the sentence explaining what
    /// the transfer does to each party's role.
    TransferRoleExplanation: string -> string
    /// Takes the role's display name and the formatted expiry date.
    InviteExpires: string -> string -> string
    /// …and the same for one that has already expired.
    InviteExpired: string -> string -> string
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
    LiveHealthHeading: string
    Loading: string
    SchedulerDriftHeading: string
    SchedulerMissed60m: string
    SchedulerLastDrift: string
    SchedulerLastMissAt: string
    DegradedReason: string
    DegradedImpact: string
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
    ColumnId: string
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
    SourcesHeading: string
    Loading: string
    AwaitingFirstRefresh: string
    AddDataSource: string
    CancelCreate: string
    DismissError: string
}

/// The administration-area landing surface (`AdminHome`, Phase 573).
type AdminHomeMessages = {
    /// Page heading.
    Heading: string
    /// Sub-heading prose under the page heading.
    Subheading: string
    /// Reload action.
    Refresh: string
    /// Tooltip on a tile's click target — takes the tile's title.
    OpenTile: string -> string
    /// Empty state: the deployment contributes no tiles at all.
    NoTilesContributedHeading: string
    NoTilesContributedBody: string
    NoTilesContributedFooter: string
    /// Empty state: tiles exist, none is visible to this subject.
    NoTilesForSubjectHeading: string
    NoTilesForSubjectBody: string
    NoTilesForSubjectFooter: string
}

/// The user-facing sentences an OIDC failure is described by
/// (`OidcTokenStore.describeError`). One field per `AuthError` case, so
/// adding a case upstream fails to compile here until it is worded.
///
/// The security-sensitive branches (signature / nonce / issuer /
/// audience) are deliberately opaque in English and a translation must
/// stay so: the developer-facing `diagnose` carries the withheld
/// sub-cause, and a message that named it would let a tampering
/// attacker probe the validator by reading the screen.
type AuthErrorMessages = {
    /// Takes the underlying transport message.
    DiscoveryFailed: string -> string
    InvalidState: string
    MissingCode: string
    /// Takes the issuer's error code.
    IssuerError: string -> string
    /// Takes the issuer's error code and its description.
    IssuerErrorDescribed: string -> string -> string
    /// Takes the underlying transport message.
    TokenExchangeFailed: string -> string
    /// Takes the underlying transport message.
    NetworkError: string -> string
    NonceMismatch: string
    MalformedIdToken: string
    SignatureInvalid: string
    IssuerInvalid: string
    AudienceInvalid: string
    Expired: string
}

/// The prompts only the passwordless flow has (`PasskeyClient`).
type PasskeyAuthMessages = {
    /// Prose under the sign-in heading.
    SignInPrompt: string
    UsernamePlaceholder: string
    SignIn: string
    /// Prose introducing the first-time registration block.
    RegisterPrompt: string
    BootstrapTokenPlaceholder: string
    Register: string
}

/// The sign-in surfaces the auth-UI companion packages render —
/// `OidcClient`, `PasskeyClient`, `EntraExternalIdClient` (Phase 751).
///
/// ONE shared section rather than one per package: the three render the
/// same vocabulary, and four near-identical sections would ask a
/// translator for "Sign in" four times. `Passkey` carries the prompts
/// only the passwordless flow has; `ClerkUI` contributes nothing,
/// because Clerk renders its own themed screens and the companion holds
/// no text of its own.
///
/// These screens render OUTSIDE the shell's own view — the auth gate
/// WRAPS it — which is why `Client.viewWithSignIn` mounts the catalog
/// provider outside the gate as well. Without that, a deployment's
/// `MessageCatalogOverride` would reach every surface except the one a
/// signed-out visitor actually sees.
type AuthMessages = {
    /// Shown while the shell decides whether this is a callback or a
    /// cold start.
    SigningIn: string
    /// Sign-in screen heading.
    Welcome: string
    /// Prose under the heading.
    SignInPrompt: string
    SignIn: string
    /// Secondary action, rendered only where a sign-up flow is declared.
    SignUp: string
    /// Header sign-out action of the companions' `UserMenu`.
    SignOut: string
    SignInFailedHeading: string
    TryAgain: string
    Errors: AuthErrorMessages
    Passkey: PasskeyAuthMessages
}

/// Display labels for the rate-limit windows. Distinct from the
/// `RateLimitWindow` DU cases themselves, which are wire-shaped.
type RateLimitWindowMessages = {
    PerSecond: string
    PerMinute: string
    PerHour: string
    PerDay: string
    /// Shown for `SlidingWindow _`, whose duration is already in the
    /// limit sentence.
    Sliding: string
}

/// The Phase 56 rate-limit banner (`Components.RateLimitedBanner`).
type RateLimitedMessages = {
    Heading: string
    /// Takes the request limit and the window label from `Windows`.
    LimitExceeded: int -> string -> string
    TryAgain: string
    /// Takes the seconds remaining. A FUNCTION rather than a template
    /// with a bolted-on "s", because the plural rule is a property of
    /// the language: English needs two forms here, Welsh needs six, and
    /// Japanese needs one.
    TryAgainIn: int -> string
    Windows: RateLimitWindowMessages
}

/// Display labels for the consent categories the banner toggles. The
/// `ConsentCategory` DU cases themselves are wire-shaped — they are
/// persisted in `ConsentState` and compared by `ConsentState.hasAll` —
/// so only the label is localised.
type ConsentCategoryMessages = {
    Necessary: string
    Functional: string
    Analytics: string
    Marketing: string
    Personalisation: string
    ThirdPartyEmbeds: string
}

/// The SDK's own category-toggle consent banner (Phase 159).
type ConsentMessages = {
    /// The banner's explanatory sentence.
    Body: string
    RejectAll: string
    AcceptAll: string
    SavePreferences: string
    Categories: ConsentCategoryMessages
}

/// The Phase 10g OAuth 1.0a credential-form helper.
type OAuth1aCredentialMessages = {
    ConsumerKeyLabel: string
    ConsumerSecretLabel: string
    Save: string
    /// The authorize link's text before any token is held.
    Authorize: string
    /// …and after one is (the connection is being re-authorised).
    Reconnect: string
}

/// The SDK's built-in no-active-team landing (`NoActiveTeamLandingUI`).
/// Its heading, body and rail label are NOT here — they come from the
/// deployment's own `NoActiveTeamLandingConfig`, which is where a
/// deployment already words them.
type NoActiveTeamLandingMessages = {
    CheckForInvitations: string
    Checking: string
    NothingPending: string
    /// Takes the joined team's display name.
    Joined: string -> string
}

/// The built-in Platform Admin module (`PlatformAdminUI`) — role
/// management (assign / revoke Platform Admin), the all-teams admin
/// table (create / archive / restore / delete), and the Platform
/// Knowledge Base toggle, under the Admins / Teams / Settings tabs.
type PlatformAdminMessages = {
    /// Tab-bar labels.
    AdminsTab: string
    TeamsTab: string
    SettingsTab: string
    AssignHeading: string
    AssignHelp: string
    /// Placeholder for the user-directory typeahead, shared by the
    /// Assign-admin field and the Create-team initial-owner field.
    UserPickerPlaceholder: string
    Assign: string
    /// Inline-validation error read directly in `update` (a pure
    /// reducer with no rendered tree of its own) — the string ends up
    /// in `Model.AssignError`, rendered later via `errorBanner`.
    EnterUserId: string
    CurrentAdminsHeading: string
    /// Shared by the admin list and the all-teams table.
    Refresh: string
    Loading: string
    NoAdmins: string
    /// Per-admin-row revoke button.
    Revoke: string
    GrantHeading: string
    GrantBody: string
    UserLabel: string
    UserIdLabel: string
    /// Shared by the assign-confirm and delete-team-confirm modals.
    Cancel: string
    GrantConfirm: string
    CreateTeamHeading: string
    CreateTeamHelp: string
    TeamNameLabel: string
    TeamNamePlaceholder: string
    InitialOwnerLabel: string
    /// Inline-validation error read directly in `update` — see
    /// `EnterUserId`.
    TeamNameRequired: string
    /// Inline-validation error read directly in `update` — see
    /// `EnterUserId`.
    OwnerRequired: string
    /// Tooltip on the "Self" button — takes the operator's own
    /// resolved display label (email, display name, or raw id).
    SelfTooltip: string -> string
    SelfChecked: string
    SelfUnchecked: string
    /// Confirmation line when the operator picked themselves as the
    /// new team's owner — takes their resolved display label.
    SelfOwnerConfirm: string -> string
    /// Confirmation line when the operator picked someone else as
    /// owner — takes the picked user's resolved display label.
    OwnerConfirm: string -> string
    Creating: string
    /// The create-team submit button at rest.
    CreateTeam: string
    ColumnTeam: string
    ColumnCreated: string
    ColumnMembers: string
    ColumnOwners: string
    ColumnAdmins: string
    ColumnActions: string
    AllTeamsHeading: string
    LoadingTeams: string
    NoTeamsYet: string
    ArchivedBadge: string
    /// Per-team-row action buttons.
    Restore: string
    Delete: string
    Archive: string
    DeleteTeamHeading: string
    DeleteTeamBody: string
    TeamLabel: string
    /// The delete-team confirm button (distinct from the row-level
    /// `Delete` action that opens this modal).
    DeleteTeam: string
    /// Card heading, rendered both above and inside the toggle.
    PlatformKnowledgeBaseHeading: string
    /// Explanatory sentence under the heading — takes the current
    /// Enabled / Disabled status label.
    KnowledgeBaseStatus: string -> string
    Enabled: string
    Disabled: string
    /// Toggle button while the KB is currently enabled (click to
    /// disable it).
    DisableAction: string
    /// Toggle button while the KB is currently disabled (click to
    /// enable it).
    EnableAction: string
    LoadingCurrentState: string
    OtherSettingsHeading: string
    OtherSettingsBody: string
}

/// The built-in platform-users admin module (`PlatformUsersUI`, Phase 544).
type PlatformUsersMessages = {
    /// Page heading.
    Heading: string
    /// Sub-heading prose under the page heading.
    Subheading: string
    /// Filter-bar checkbox label restricting the list to team-less
    /// principals.
    TeamLessOnly: string
    /// Refresh action in the filter bar.
    Refresh: string
    /// Shown while the first `ListPrincipals` call is in flight.
    LoadingPrincipals: string
    /// Empty state: no principals at all (empty list, or the offboard
    /// substrate is disabled — the error banner carries the why).
    NoPrincipalsHeading: string
    NoPrincipalsBody: string
    /// Empty state: every enumerated principal belongs to a team, so the
    /// team-less filter has nothing to show.
    NoTeamLessPrincipals: string
    /// Dismiss action on the page-level error banner.
    Dismiss: string

    // ─── Row ────────────────────────────────────────────────────────
    /// Team-less badge on a row.
    TeamLessBadge: string
    /// "has data" badge on a row with `HasUserScopeData = true`.
    HasDataBadge: string
    /// Row / membership-summary text for a team-less principal.
    NoTeams: string
    /// Membership summary — "N team · role, role" (singular) or
    /// "N teams · role, role" (plural) — takes the team count and the
    /// comma-joined, deduplicated role list.
    MembershipSummary: int -> string -> string
    /// Row subtitle — "<membership summary> · last seen <date>" — takes
    /// the already-formatted membership summary and last-seen date.
    RowSubtitle: string -> string -> string
    /// Row action opening the read-only preview.
    PreviewAction: string
    /// Row / modal action for the plain-offboard path.
    OffboardAction: string
    /// Row / modal action for the export-then-offboard path.
    ExportOffboardAction: string

    // ─── Preview badge ──────────────────────────────────────────────
    /// Preview-table badge when a hook has no preview support.
    NoPreviewBadge: string

    // ─── Outcome badges (summary table) ─────────────────────────────
    OutcomeCompleted: string
    OutcomeSkipped: string
    OutcomeFailed: string

    // ─── Offboard modal ─────────────────────────────────────────────
    /// Modal heading for the plain-offboard kind.
    OffboardTitle: string
    /// Modal heading for the export-then-offboard kind.
    ExportOffboardTitle: string
    /// Modal heading once the destructive call has completed.
    OffboardCompleteTitle: string
    /// Modal subject line — takes the principal's display label; the
    /// scope id is rendered separately in its own monospace span.
    SubjectLabel: string -> string
    /// Primary-button label while a destructive / preview / mint call is
    /// in flight.
    Working: string
    /// Primary-button label at the `Confirming` step.
    ConfirmOffboard: string
    ReasonLabel: string
    ReasonPlaceholder: string
    Cancel: string
    PreviewImpact: string
    /// Preview-summary prose — takes `LifecyclePreview.TotalWouldAffect`.
    PreviewSummary: int -> string
    /// Column header shared by the preview table and the summary table.
    ColumnHook: string
    ColumnWouldAffect: string
    /// Column header shared by the preview table and the summary table.
    ColumnDetail: string
    ConfirmationRequiredHeading: string
    ConfirmationRequiredBody: string
    /// Extra note shown only for the export-offboard kind at the
    /// `Confirming` step (the confirmed path has no pre-export leg).
    ExportConfirmationNote: string
    ConfirmationTokenLabel: string
    ConfirmationTokenPlaceholder: string
    RequestToken: string
    Close: string
    /// Summary-table hook-outcome column header.
    ColumnResult: string
    /// Outcome-count badge — "N completed".
    CompletedCount: int -> string
    /// Outcome-count badge — "N skipped".
    SkippedCount: int -> string
    /// Outcome-count badge — "N failed".
    FailedCount: int -> string
    /// Takes the export archive's segment count.
    ExportArchiveWritten: int -> string
    /// Shown when the offboard ran with no registered lifecycle hooks.
    NoHooksRan: string

    // ─── Update-reducer validation (Phase 751 — read via
    // `MessageCatalog.english.PlatformUsers.*` from a pure-reducer branch
    // with no rendered tree at the point these are raised) ────────────
    ReasonRequired: string
    TokenRequired: string
}

/// The built-in permissions-admin module (`PermissionsAdminUI`, Tidy-Up
/// sweep #3). Covers the three tabs — Team Defaults, Members, Modules —
/// plus the shared permission-matrix chrome (`permTable`) they render
/// through.
type PermissionsAdminMessages = {
    /// Display label for `ModulePermission.Read` wherever a permission
    /// matrix renders it — the Team Defaults / Members grids and the
    /// Modules tab's summary column.
    PermRead: string
    PermWrite: string
    PermAdmin: string
    /// Display label for `ModulePermission.SchemaOnly`. Distinct from
    /// the stable wire token `ModulePermission.toToken` emits for audit
    /// rows — this one is UI-only.
    PermSchemaOnly: string
    /// Snapshot-load error shown when the caller has no active team.
    /// Read directly off `MessageCatalog.english` inside
    /// `loadSnapshotAsync`, which runs before any component mounts.
    NoActiveTeam: string
    /// Tab-bar labels.
    TabTeamDefaults: string
    TabMembers: string
    TabModules: string
    /// Shared permission-matrix header — `permTable`'s leading column,
    /// reused by the Team Defaults grid, the Members override editor,
    /// and the Modules summary table.
    ColumnModule: string
    /// Empty state for `permTable` and for the Modules tab when the
    /// deployment has no managed modules.
    NoManagedModules: string
    /// Per-column "grant everywhere" / "clear everywhere" toggle labels
    /// on `permTable`'s column-action links.
    SelectAll: string
    ClearAll: string
    /// Team Defaults tab.
    TeamDefaultsHeading: string
    TeamDefaultsSubheading: string
    /// Shared "discard uncommitted edits" action — the Team Defaults and
    /// Members tabs both use it.
    Reset: string
    /// Shared in-flight save-button label — Team Defaults ("Save
    /// defaults" → this while saving) and Members ("Update" → this
    /// while saving) both use it.
    Saving: string
    SaveDefaultsLabel: string
    TeamDefaultsSaved: string
    /// Takes the server error message.
    SaveFailed: string -> string
    /// Badge on a member's list entry when they carry at least one
    /// explicit per-module override.
    OverrideBadge: string
    /// Members tab.
    NoMembersYet: string
    /// Section label above the member list (distinct from the tab-bar
    /// "Members" label, which reads `TabMembers`).
    MembersListLabel: string
    SelectMemberPrompt: string
    /// Takes the selected member's display label.
    OverridesHeading: string -> string
    OverridesHelp: string
    /// Takes the comma-joined list of module names carrying an explicit
    /// override for the selected member.
    ActiveOverridesOn: string -> string
    Update: string
    /// Takes the saved module name, then the member's user id.
    SavedModuleForUser: string -> string -> string
    /// Takes the member's user id.
    SavedPermissionsFor: string -> string
    /// Takes the member's user id, the module name, then the server
    /// error message.
    OverrideSaveFailed: string -> string -> string -> string
    /// Modules tab.
    ModulesHeading: string
    ModulesSubheading: string
    ColumnExposure: string
    ColumnTeamDefault: string
    ColumnOverrides: string
    NoDefaultPermission: string
    /// Exposure-selector button labels — `ModuleExposure`'s three cases.
    ExposureAvailable: string
    ExposureHidden: string
    ExposureUnavailable: string
    /// Confirmation-banner sentences after a successful
    /// `SetModuleExposure` round-trip. One whole sentence per case
    /// rather than a composed "{moduleName} is now {verb}." template,
    /// so a translation is never assembled from independently-ordered
    /// fragments. Each takes the module name.
    ExposureNowAvailable: string -> string
    ExposureNowHidden: string -> string
    ExposureNowUnavailable: string -> string
    /// Takes the module name, then the server error message.
    ExposureChangeFailed: string -> string -> string
    /// Shared chrome.
    Retry: string
    Loading: string
    Dismiss: string
}

/// The built-in webhook-subscription admin module (`WebhookAdminUI`).
type WebhookAdminMessages = {
    /// Per-subscription status-banner text after `UpdateStatusSubmit` succeeds.
    StatusUpdated: string
    /// Per-subscription status-banner text after a secret rotation succeeds.
    SecretRotated: string
    /// Test-fire result banner when the target returned neither a status
    /// code nor an error (a legal but unusual `WebhookTestResult`).
    TestFired: string
    /// Test-fire result banner on an HTTP response. Takes the status code
    /// and the round-trip latency in milliseconds.
    TestFiredHttp: int -> int64 -> string
    /// Test-fire result banner on a transport-level failure. Takes the
    /// error text.
    TestFiredFailed: string -> string
    /// `WebhookStatus.Active` display label — shared by the status badge
    /// and the sidebar row.
    StatusActive: string
    /// `WebhookStatus.Paused` display label.
    StatusPaused: string
    /// `WebhookStatus.Disabled` display label.
    StatusDisabled: string
    /// Delivery-outcome label for `WebhookDeliveryOutcome.Success`. Takes
    /// the HTTP status code and the latency in milliseconds.
    OutcomeOk: int -> int64 -> string
    /// Delivery-outcome label for a `Failure` carrying a status code.
    /// Takes the status code, the error text, and the latency in
    /// milliseconds.
    OutcomeHttpError: int -> string -> int64 -> string
    /// Delivery-outcome label for a `Failure` with no status code (a
    /// transport-level failure). Takes the error text and the latency in
    /// milliseconds.
    OutcomeFailed: string -> int64 -> string
    /// Delivery-outcome label for `WebhookDeliveryOutcome.DeadLettered`.
    /// Takes the final error text.
    OutcomeDeadLettered: string -> string
    /// Transient "in flight" text under the per-subscription action row.
    Working: string
    /// Dismiss action shared by the per-subscription status banner (both
    /// its Done and Failed states) and the list-level error banner.
    Dismiss: string
    /// Heading of the create-subscription form.
    CreateHeading: string
    /// Label of the target-URL field.
    TargetUrlLabel: string
    /// Placeholder of the target-URL field.
    TargetUrlPlaceholder: string
    /// Label of the signing-secret field.
    SecretLabel: string
    /// Placeholder of the signing-secret field.
    SecretPlaceholder: string
    /// Generate-a-random-secret button.
    Generate: string
    /// Help text under the signing-secret field.
    SecretHelp: string
    /// Label of the event-types field.
    EventTypesLabel: string
    /// Create-subscription submit button.
    Create: string
    /// Heading of the one-time secret-reveal banner.
    SecretRevealHeading: string
    /// Body prose of the secret-reveal banner.
    SecretRevealBody: string
    /// Acknowledge-and-dismiss button on the secret-reveal banner.
    SecretRevealAck: string
    /// Pause action (shown while the subscription is Active).
    Pause: string
    /// Resume action (shown while the subscription is Paused).
    Resume: string
    /// Re-enable action (shown while the subscription is Disabled).
    ReEnable: string
    /// Test-fire action.
    TestFire: string
    /// Rotate-secret action.
    RotateSecret: string
    /// `window.confirm` prompt shown before rotating a secret.
    RotateSecretConfirm: string
    /// Delete action.
    Delete: string
    /// `window.confirm` prompt shown before deleting a subscription.
    DeleteConfirm: string
    /// Metadata row label — the subscription's id.
    SubscriptionIdLabel: string
    /// Metadata row label — the target URL.
    TargetLabel: string
    /// Metadata row label — the subscribed event types.
    EventTypesRowLabel: string
    /// Value shown in the event-types row when the subscription is
    /// subscribed to every event type (an empty `EventTypes` list).
    AllEvents: string
    /// Metadata row label — the subscription status.
    StatusLabel: string
    /// Metadata row label — the consecutive-failure counter.
    ConsecutiveFailuresLabel: string
    /// Metadata row label — creation provenance.
    CreatedLabel: string
    /// Creation-provenance value. Takes the already-formatted creation
    /// timestamp and the creator's identifier.
    CreatedByLine: string -> string -> string
    /// Heading of the per-subscription delivery log.
    RecentDeliveries: string
    /// Empty state of the delivery log.
    NoDeliveriesYet: string
    /// Delivery-log column header — attempt timestamp.
    ColumnAttempted: string
    /// Delivery-log column header — attempt number.
    ColumnAttempt: string
    /// Delivery-log column header — outcome label.
    ColumnOutcome: string
    /// Delivery-log column header — source event id.
    ColumnEventId: string
    /// Heading above the subscription list in the sidebar.
    SubscriptionsHeading: string
    /// Generic "in flight" text — the sidebar list while loading.
    Loading: string
    /// Empty state of the subscription list.
    NoSubscriptionsYet: string
    /// Detail-pane placeholder when nothing is selected but the list has
    /// finished loading.
    CreateOrSelectPrompt: string
    /// Detail-pane placeholder when nothing is selected and the list is
    /// still loading.
    LoadingSubscriptions: string
    /// Detail-pane placeholder when the selected id no longer resolves to
    /// a subscription in this scope.
    SubscriptionNotFound: string
}

/// The `NarrativeDocument` renderer's own chrome (`NarrativeRenderer.fs`,
/// Phase 751) — the "Save to Knowledge Base" control (including its
/// duplicate-save confirmation dialog) and the "Copy as Markdown" control.
/// Everything else the renderer draws is document content (headings,
/// table cells, block bodies) sourced from the `NarrativeDocument` itself,
/// not chrome the renderer authors.
type NarrativeRendererMessages = {
    /// Title of the "Save to Knowledge Base" control at rest.
    SaveToKnowledgeBase: string
    /// Title of the control while a save request is in flight.
    Saving: string
    /// Title of the control immediately after a save succeeds, before it
    /// reverts to its resting state.
    Saved: string
    /// Title set when the narrative carries no `Provenance` and therefore
    /// has no dedup key to save under.
    NoProvenance: string
    /// Heading of the dialog raised when a save collides with an
    /// existing Knowledge Base entry.
    DuplicateHeading: string
    /// Body sentence naming when the previous version was saved. Takes
    /// the already-formatted timestamp — the format is a call-site
    /// concern, never baked into the translated template.
    DuplicateBody: string -> string
    /// Prompt asking whether to overwrite the previous version.
    DuplicateConfirmPrompt: string
    /// Dismisses the overwrite dialog without saving.
    Cancel: string
    /// Confirms the overwrite.
    Overwrite: string
    /// Title of the "Copy as Markdown" control immediately after a
    /// successful copy.
    Copied: string
    /// Title of the "Copy as Markdown" control at rest.
    CopyAsMarkdown: string
}

/// The built-in service-account admin module (`ServiceAccountUI`, Phase 527).
type ServiceAccountMessages = {
    /// Page heading.
    Heading: string
    /// Sub-heading prose under the page heading.
    Subheading: string
    Loading: string
    /// The error banner's dismiss action.
    Dismiss: string
    /// Module-permission display labels, shared by the status badge, the
    /// create-form's permission picker and the summary pill. The
    /// underlying DU case names stay wire-shaped (sent to the server /
    /// used as the `<select>` option value as-is) — only these display
    /// labels are localised.
    PermissionRead: string
    PermissionWrite: string
    PermissionAdmin: string
    PermissionSchemaOnly: string
    /// Shared by an account's own status badge and a live (non-revoked,
    /// non-expired) token's status badge.
    StatusActive: string
    StatusDisabled: string
    StatusRevoked: string
    StatusExpired: string
    /// The one-time secret panel's heading — takes the minted token's
    /// display name.
    CopyTokenHeading: string -> string
    /// The one-time secret panel's explanatory prose: the secret cannot
    /// be shown again once acknowledged.
    SecretOneTimeBody: string
    /// The one-time secret panel's acknowledgement button.
    AcknowledgeSecret: string
    /// Create-account form heading.
    NewAccountHeading: string
    NameLabel: string
    NamePlaceholder: string
    ModulePermissionsLabel: string
    /// Placeholder of the inline module-name field in the permission
    /// picker (the module a grant applies to, e.g. "Sales").
    ModuleNamePlaceholder: string
    /// The picker's "add this grant to the pending set" button.
    AddPermission: string
    /// Shown while the pending permission set is empty — an account with
    /// no declared permissions is refused server-side.
    NoPermissionsHint: string
    /// Create-account submit button while the request is in flight.
    Working: string
    CreateAccount: string
    /// Mint-token form heading.
    MintTokenHeading: string
    MintLabelPlaceholder: string
    /// Unit suffix on the expiry day-count field.
    Days: string
    Mint: string
    /// Empty state when the deployment has no service accounts yet.
    NoAccountsHeading: string
    NoAccountsBody: string
    /// The per-account "view its tokens" button, and the fallback tokens
    /// heading when the selected account can no longer be found.
    Tokens: string
    Disable: string
    Enable: string
    /// Empty state when the selected account has no tokens yet.
    NoTokensYet: string
    /// A token row's summary line — takes the formatted issued-on date,
    /// the issuer's identity, and the formatted expiry date, in that
    /// order. Dates are formatted at the call site (444's recorded
    /// decision), so this receives plain strings.
    TokenIssuedSummary: string -> string -> string -> string
    Revoke: string
    /// The "back to the account list" breadcrumb.
    BackToList: string
    /// The tokens-panel heading for the selected account — takes the
    /// account's display name.
    TokensForAccount: string -> string
}

/// The built-in GDPR data-subject-request admin module
/// (`DataSubjectRequestAdminUI`) — the Article 15 export tab, the
/// Article 17 erasure tab (preview + confirm), and the background-export
/// ticket panel. Request-kind / status / policy DUs stay wire-shaped and
/// out of scope; the fields below are their DISPLAY projections only.
type DataSubjectRequestAdminMessages = {
    /// Tab bar.
    TabExport: string
    TabErase: string
    /// Shared subject/team/reason form placeholders (both tabs).
    SubjectPlaceholder: string
    TeamPlaceholder: string
    ReasonPlaceholder: string
    /// Article 15 — export tab.
    ExportPanelTitle: string
    ExportPanelBody: string
    AsyncModeLabel: string
    RequestExport: string
    Exporting: string
    AggregatingSegments: string
    /// Background-export ticket panel (Phase 9h.A).
    BackgroundExportHeading: string
    /// Takes the ticket id and the resolved status label.
    TicketLine: string -> string -> string
    /// Shared "Cancel" action — the async-export ticket panel and the
    /// erasure preview panel both use this exact label.
    Cancel: string
    /// `ticketStatusLabel`'s catalog projection of `ExportStatus`.
    TicketPreparing: string
    /// Takes the ready envelope's size in bytes.
    TicketReady: int64 -> string
    /// Takes the failure reason.
    TicketFailed: string -> string
    TicketCancelled: string
    TicketExpired: string
    TicketUnknown: string
    /// `ErasurePolicy` display projections — the DU itself stays
    /// wire-shaped; only `policyLabel` / `policyDescription`'s rendered
    /// text is in scope.
    PolicyHardDeleteLabel: string
    PolicyHardDeleteDescription: string
    PolicyTombstoneLabel: string
    PolicyTombstoneDescription: string
    PolicyRetainPerComplianceLabel: string
    PolicyRetainPerComplianceDescription: string
    /// Policy-override radio group.
    OverridePolicyPrompt: string
    UseDeploymentDefault: string
    UseDeploymentDefaultDescription: string
    /// Erasure preview panel.
    PreviewPanelTitle: string
    /// Takes the preview's request id.
    RequestIdLine: string -> string
    /// Takes the policy label, the total affected count and the
    /// handler count.
    PreviewSummaryLine: string -> int -> int -> string
    PreviewEmpty: string
    /// Takes a handler's affected-record count. Shared between the
    /// preview panel's per-handler rows and the run-summary panel's.
    HandlerRecordsAffected: int -> string
    ConfirmErase: string
    Confirming: string
    ConfirmIrreversibleFootnote: string
    /// Last-run summary panel.
    RunSummaryPanelTitle: string
    /// Takes the formatted started timestamp, the formatted completed
    /// timestamp, and the localised overall-outcome word.
    RunSummaryLine: string -> string -> string -> string
    OverallSuccess: string
    OverallPartialFailure: string
    /// Article 17 — erase tab.
    ErasePanelTitle: string
    ErasePanelBody: string
    PreviewErase: string
    Previewing: string
    PendingPreviewHint: string
    /// Dismiss the inline status banner.
    DismissBanner: string
    /// Raised from the pure `update` reducer (no rendered tree at the
    /// point they're raised) rather than threaded through `msgs` — read
    /// directly off `MessageCatalog.english`, per `TeamManagerUI.update`.
    SubjectRequired: string
    ReasonRequired: string
    BackgroundExportQueued: string
    /// Takes the export-failure reason.
    ExportFailed: string -> string
    ExportCancelled: string
    ExportTicketExpiredOrUnknown: string
    /// Takes the downloaded byte count.
    BackgroundExportReady: int -> string
    /// Takes the downloaded byte count.
    ExportReady: int -> string
    RunPreviewFirst: string
    /// Takes the successfully-run handler count.
    EraseConfirmedSuccess: int -> string
    /// Takes the handler count.
    EraseConfirmedPartialFailure: int -> string
    /// Takes the refusal reason.
    EraseRefused: string -> string
    /// Takes the not-implemented detail.
    EraseNotImplemented: string -> string
}

/// The built-in module-configuration / feature-flag admin (`TeamConfigUI`).
/// Covers both tabs on the one page — the per-module config forms and the
/// feature-flag override editor — since they share several primitives
/// (the Save/Clear buttons' Saving/Saved/dismiss status banner) across
/// both.
type TeamConfigMessages = {
    /// Marks a required config field, next to its label.
    RequiredMarker: string
    /// Status-banner text while a save/clear call is in flight — reused
    /// by both the module-config form and the feature-flag row.
    Saving: string
    /// Status-banner text after a successful save/clear — reused the
    /// same way as `Saving`.
    Saved: string
    /// Dismiss action on every status/error banner on this page.
    Dismiss: string
    SaveButton: string
    ClearAllButton: string
    NoEditableConfig: string
    /// Module-config form subheading — takes the module's config key.
    ModuleKeyLabel: string -> string
    FlagEnabled: string
    FlagDisabled: string
    FlagOverridden: string
    FlagUsingDefault: string
    /// Takes the flag's declared owner.
    FlagOwnerLabel: string -> string
    /// Takes the flag's formatted default value.
    FlagDefaultLabel: string -> string
    SaveOverrideButton: string
    ClearOverrideButton: string
    FeatureFlagsHeading: string
    FeatureFlagsHelp: string
    LoadingFlags: string
    NoFlagsDeclared: string
    ModulesHeading: string
    /// Sidebar loading state, before the module list has landed.
    SidebarLoading: string
    NoConfigurableModules: string
    SelectModulePrompt: string
    LoadingModulesPrompt: string
    /// Takes the unresolvable module key.
    ModuleNotAvailable: string -> string
    ConfigurationTab: string
    FeatureFlagsTab: string
}

/// The built-in tenant-lifecycle diagnostics admin module
/// (`TenantLifecycleAdminUI`, Phase 54e / localized Phase 751).
type TenantLifecycleAdminMessages = {
    /// Page heading.
    Heading: string
    /// Sub-heading prose under the page heading.
    Subheading: string
    /// Label above the scope-id input.
    ScopeIdLabel: string
    /// Placeholder of the scope-id input.
    ScopeIdPlaceholder: string
    /// Submit-button label while a fetch is in flight.
    LoadingButton: string
    /// Submit-button label at rest.
    LoadLastRun: string
    /// Footnote under the scope-id form.
    ScopeFormFootnote: string
    /// Dismiss action on the error banner.
    Dismiss: string
    /// Shared "in progress" text — also used by the result pane while a
    /// fetch is loading (same English text as `LoadingButton`, but a
    /// distinct call site).
    Loading: string
    /// Result-pane empty state: queried, no run recorded — takes the
    /// submitted scope id.
    NoRunForScope: string -> string
    /// Result-pane empty-state fallback for when the queried scope is
    /// somehow unset (defensive; `QueriedScope` is always `Some` once
    /// `Loaded` is true).
    NoRunForScopeFallback: string
    /// Prose under the "no run recorded" empty state.
    NoRunHelp: string
    /// Prompt shown before the admin has submitted a scope.
    EnterScopePrompt: string
    /// Summary-table heading — takes the tenant-lifecycle phase name.
    LastRunHeading: string -> string
    /// Count-pill labels (completed / skipped / failed / total elapsed).
    PillCompletedLabel: string
    PillSkippedLabel: string
    PillFailedLabel: string
    PillMsTotalLabel: string
    /// Per-hook result badges.
    BadgeCompleted: string
    BadgeSkipped: string
    BadgeFailed: string
    /// Empty state: the run recorded no hooks at all (a valid no-op run).
    NoHooksRecorded: string
    /// Hook-outcome table column headers.
    ColumnHook: string
    ColumnResult: string
    ColumnDetail: string
    ColumnElapsed: string
    /// Per-hook elapsed-time cell — takes the elapsed milliseconds.
    ElapsedMsLabel: int -> string
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
    AdminHome: AdminHomeMessages
    Auth: AuthMessages
    RateLimited: RateLimitedMessages
    Consent: ConsentMessages
    OAuth1aCredential: OAuth1aCredentialMessages
    NoActiveTeamLanding: NoActiveTeamLandingMessages
    PlatformAdmin: PlatformAdminMessages
    PlatformUsers: PlatformUsersMessages
    PermissionsAdmin: PermissionsAdminMessages
    WebhookAdmin: WebhookAdminMessages
    NarrativeRenderer: NarrativeRendererMessages
    ServiceAccount: ServiceAccountMessages
    DataSubjectRequestAdmin: DataSubjectRequestAdminMessages
    TeamConfig: TeamConfigMessages
    TenantLifecycleAdmin: TenantLifecycleAdminMessages
}