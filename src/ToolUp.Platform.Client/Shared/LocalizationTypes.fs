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

/// The built-in module-visibility profile editor (`ModuleVisibilityAdminUI`).
type ModuleVisibilityAdminMessages = {
    /// Page heading.
    Heading: string
    /// Sub-heading prose under the page heading.
    Subheading: string
    /// Dismiss action on the error / status banners.
    Dismiss: string
    /// Transient confirmation after a successful save.
    ProfileSaved: string
    /// Transient confirmation after a successful clear.
    ProfileCleared: string
    /// Generic "still fetching" state, shared by the resolved-answer pane
    /// and the editor while their respective loads are in flight.
    Loading: string
    /// Prose shown when no layer declares a profile yet.
    NoResolutionYet: string
    /// Empty-list placeholder inside each of the resolved-answer pane's
    /// four module-id lists.
    NoItems: string
    /// Takes the list's item count — heading of the "governed modules"
    /// list in the resolved-answer pane.
    GovernedModules: int -> string
    /// Takes the list's item count — heading of the "selected after every
    /// layer" list.
    SelectedAfterEveryLayer: int -> string
    /// Takes the list's item count — heading of the "excluded pages /
    /// entries" list.
    ExcludedEntries: int -> string
    /// Takes the list's item count — heading of the "contributing scopes"
    /// list.
    ContributingScopes: int -> string
    /// Heading of the resolved-answer pane.
    ResolvedForYouHeading: string
    /// Sub-heading prose under the resolved-answer pane's heading.
    ResolvedForYouHelp: string
    /// Badge on a candidate row the resolution currently admits.
    VisibleNow: string
    /// Badge on a candidate row the resolution currently hides.
    HiddenNow: string
    /// Empty state: this deployment registers no curatable modules.
    NoCuratableModules: string
    /// Takes the registered-module count and the selected-module count —
    /// header of the candidate list.
    RegisteredModulesHeader: int -> int -> string
    /// Heading of the editable-profile card.
    YourProfileHeading: string
    /// Sub-heading prose under the editable-profile card's heading.
    YourProfileHelp: string
    /// The Allow rule-kind button's label.
    AllowLabel: string
    /// The Allow rule-kind button's description.
    AllowDescription: string
    /// The Deny rule-kind button's label.
    DenyLabel: string
    /// The Deny rule-kind button's description.
    DenyDescription: string
    /// Label of the free-text note field.
    NoteLabel: string
    /// Placeholder of the free-text note field.
    NotePlaceholder: string
    /// Save button label while a save / clear is in flight.
    Working: string
    /// Save button label at rest.
    SaveProfile: string
    /// Clear button label.
    ClearProfile: string
}

/// The built-in data-migration admin module (`MigrationStatusUI`, Phase 10a).
type MigrationStatusMessages = {
    /// `MigrationRunState.MigrationIdle` status-pill label.
    NotYetRun: string
    /// `MigrationRunState.MigrationInProgress` status-pill label.
    InProgressLabel: string
    /// `MigrationRunState.MigrationComplete` status-pill label.
    UpToDate: string
    /// `MigrationRunState.MigrationCompleteWithFailures` status-pill label.
    CompletedWithFailures: string
    /// `MigrationRunState.MigrationChainBlocked` status-pill label.
    Blocked: string
    /// Progress-column text when no pass has ever been recorded for a data
    /// type — both the no-status case and `MigrationIdle`.
    NoPassRecorded: string
    /// Progress-column text while a pass is running. Takes the data type's
    /// display name, the target schema version, the migrated-so-far count,
    /// and the total object count.
    InProgressText: string -> int -> int -> int -> string
    /// Progress-column text once a pass has finished cleanly. Takes the
    /// total object count and the target schema version.
    CompleteText: int -> int -> string
    /// Progress-column text once a pass has finished with some objects left
    /// behind. Takes the migrated-so-far count, the total object count, the
    /// target schema version, and the failed-object count.
    CompleteWithFailuresText: int -> int -> int -> int -> string
    /// Dismiss action on the trigger-error banner.
    Dismiss: string
    /// Empty state of the expanded failure log.
    NoFailuresRecorded: string
    /// Heading prose above the failure log. Takes the failure count.
    FailuresSummary: int -> string
    /// One failure-log line. Takes the object id, the version it failed at,
    /// and the recorded error text.
    FailureLine: string -> int -> string -> string
    /// The "Declared" column value. Takes the data type's declared schema
    /// version.
    DeclaredVersion: int -> string
    /// Tooltip on a data type whose migrator chain has a gap.
    ChainIncomplete: string
    /// Migrate-now button label while a trigger is in flight.
    Migrating: string
    /// Migrate-now button label at rest.
    MigrateNow: string
    /// Failures-toggle button label while the failure log is expanded.
    HideFailures: string
    /// Failures-toggle button label while collapsed. Takes the failure
    /// count.
    FailuresButton: int -> string
    /// Empty state of the data-types table.
    NoDataTypes: string
    /// "Data type" column header.
    ColumnDataType: string
    /// "Declared" column header.
    ColumnDeclared: string
    /// "Progress" column header.
    ColumnProgress: string
    /// "State" column header.
    ColumnState: string
    /// "Actions" column header.
    ColumnActions: string
    /// Refresh button label while a refresh is in flight.
    Refreshing: string
    /// Refresh button label at rest.
    Refresh: string
    /// Page heading.
    Heading: string
    /// Sub-heading prose under the page heading.
    Subheading: string
    /// Loading placeholder shown before the first data-types response.
    LoadingDataTypes: string
}

/// The built-in session-security module (`SessionSecurityUI`, Phase 528).
type SessionSecurityMessages = {
    /// Page heading.
    Heading: string
    /// Sub-heading prose under the page heading.
    Subheading: string
    /// Shown while the first load is still in flight.
    Loading: string
    /// Empty state: no sessions have been recorded yet.
    EmptyState: string
    /// `lastSeenLabelWith`'s near-term reading — covers both "in the
    /// future" (clock skew) and "under two minutes ago".
    JustNow: string
    /// Takes whole elapsed minutes.
    MinutesAgo: int -> string
    /// Takes whole elapsed hours.
    HoursAgo: int -> string
    /// Takes whole elapsed days.
    DaysAgo: int -> string
    /// Takes the session's auth provider and the formatted "last seen"
    /// reading (`JustNow` / `MinutesAgo` / `HoursAgo` / `DaysAgo`) for
    /// an active session.
    DeviceLastSeen: string -> string -> string
    /// Takes the session's auth provider, for a revoked session.
    DeviceSignedOut: string -> string
    /// Badge on a revoked session row.
    Revoked: string
    /// Confirm a single-session revoke.
    Confirm: string
    /// Cancel either the single-session or the sign-out-everywhere
    /// confirm step — one field, since both read as plain "Cancel" in
    /// English.
    Cancel: string
    /// Ask to revoke a single session.
    SignOut: string
    /// "Sign out everywhere" — serves both the initial action button
    /// and its own confirm step, matching the English original.
    SignOutEverywhere: string
    /// Warning prose shown once sign-out-everywhere is pending confirm.
    SignOutEverywhereWarning: string
    /// Status banner after a single-session revoke completes. Read
    /// directly from `update` via `MessageCatalog.english.SessionSecurity`
    /// — there is no rendered tree at that point, per `TeamManagerUI`'s
    /// recorded pattern.
    RevokeSuccess: string
    /// Status banner after sign-out-everywhere completes, given the
    /// server's revoked-session count. Also read directly from
    /// `update`. The none/singular/plural branching lives inside the
    /// catalog function itself so a translation owns its own
    /// pluralisation rule.
    RevokeAllResult: int -> string
    /// Dismiss action shared by the error and status banners.
    Dismiss: string
}

type HomeMessages = {
    /// Page heading.
    Heading: string
    /// Sub-heading prose under the page heading.
    Subheading: string
    /// Reload action.
    Refresh: string
    /// Shown while the overview is fetching.
    Loading: string
    /// One tool card's empty state — no records of any type yet.
    NoDataYet: string
    /// "Active AI" strip heading.
    ActiveAiHeading: string
    /// Shown when no AI provider is composed / resolvable.
    NoAiProvider: string
    /// Deployment-context strip: the coarse mode label — takes the
    /// server-derived mode string (e.g. "Team", "Individual").
    Mode: string -> string
    /// Deployment-context strip: the health summary, shown only to
    /// platform admins — takes the server-derived health string.
    Health: string -> string
    /// Tools-grid section heading.
    YourTools: string
    /// Tools-grid empty state — no data-producing tools registered.
    NoTools: string
    /// "Pinned / Recent" widget heading.
    PinnedAndRecent: string
    /// Pin-toggle tooltip when the tool is not currently pinned.
    Pin: string
    /// Pin-toggle tooltip when the tool is currently pinned.
    Unpin: string
}

/// The standalone team-invitation accept page (`InviteAccept`, Phase 3d /
/// Phase 751). Mounted OUTSIDE the shell — a `PublicEntryDispatchers`
/// entry renders it to its own React root before the shell's `program`
/// (and therefore before any `MessageCatalogProvider.provider` mount)
/// exists, so this surface can render before a team, and today before any
/// catalog provider, is in place.
type InviteAcceptMessages = {
    /// Rendered when the URL carries no invitation-token segment at all —
    /// the earliest failure, raised before any request fires.
    NoToken: string
    /// Heading of the "please sign in first" panel.
    SignInHeading: string
    /// Body prose of the "please sign in first" panel.
    SignInBody: string
    /// Link back to the deployment's home page, where the shell's own
    /// AuthUI flow signs the visitor in.
    GoToSignIn: string
    /// Shown while `AcceptInvite` is in flight.
    Joining: string
    /// Success-panel heading — takes the joined team's display name.
    WelcomeHeading: string -> string
    /// Success-panel prose — takes the accepted role's display name
    /// (`TeamRoles.displayName`).
    JoinedAs: string -> string
    /// Success-panel link back into the app.
    ContinueToApp: string
    /// Terminal-failure heading. The failure body text itself is NOT a
    /// catalog field — it is either the handler's own error message or a
    /// transport exception's message, rendered verbatim.
    FailedHeading: string
    /// Failure-panel link back to the home page.
    GoToHome: string
    /// Network-layer failure prefix — takes the underlying exception
    /// message. Mirrors `Auth.Errors.NetworkError`.
    NetworkError: string -> string
}

/// Fetch-failure sentences shared by every live-data widget on this
/// page — the traffic, rate-limit-event, ad-unit and premium-user
/// widgets all resolve their loads through the same generic
/// `fetchJson` helper.
type PublicUtilityFetchMessages = {
    /// A 2xx response body failed to deserialise. Takes the underlying
    /// exception message.
    ParseError: string -> string
    /// A 403 from a platform-admin-gated endpoint.
    AccessDenied: string
    /// Any other non-2xx, non-503 status. Takes the HTTP status code.
    RequestFailed: int -> string
    /// The request itself threw (offline, DNS, CORS). Takes the
    /// underlying exception message.
    NetworkError: string
}

/// Widget 1 — the traffic-dashboard stub. The server-side
/// `/api/_platform/admin/traffic` endpoint is a follow-up that has not
/// landed yet, so this renders a substrate stub in its place.
type TrafficWidgetMessages = {
    Title: string
    Subtitle: string
    /// Rendered in place of the (not-yet-built) traffic chart.
    Stub: string
}

/// Widget 2 — the rate-limit event log.
type RateLimitWidgetMessages = {
    Title: string
    Subtitle: string
    /// `InboundRateLimitKey.IpAddressKey` display prefix. Takes the ip.
    KeyIp: string -> string
    /// `InboundRateLimitKey.UserIdKey` display prefix. Takes the user id.
    KeyUser: string -> string
    /// `InboundRateLimitKey.InboundComposite` display prefix. Takes the
    /// composite key text.
    KeyComposite: string -> string
    WindowPerSecond: string
    WindowPerMinute: string
    WindowPerHour: string
    WindowPerDay: string
    /// `RateLimitWindow.SlidingWindow` display. Takes the already
    /// call-site-formatted duration (e.g. "30s") and the bucket count —
    /// the decimal rounding of the duration is a format-specifier
    /// concern and happens before this function is called, not inside
    /// the translated template.
    WindowSliding: string -> int -> string
    /// `InboundRateLimitDecision.AllowWithRemaining` display. Takes the
    /// remaining-request count.
    DecisionAllow: int -> string
    DecisionDeny: string
    ColumnOccurred: string
    ColumnKey: string
    ColumnRoute: string
    ColumnWindow: string
    ColumnThreshold: string
    ColumnDecision: string
    Refresh: string
    Refreshing: string
    ExportCsv: string
    Loading: string
    /// Empty state: rate-limiting not configured, or nothing recorded yet.
    EmptyState: string
}

/// Widget 3 — ad-unit CRUD.
type AdUnitWidgetMessages = {
    Title: string
    Subtitle: string
    /// Rendered when `ClientConfig.AdPanel = NoAdPanel`.
    DisabledStub: string
    Loading: string
    /// Empty state: no ad units configured yet.
    EmptyState: string
    ColumnSlotId: string
    ColumnAdClientId: string
    ColumnFormat: string
    ColumnStyle: string
    ColumnActions: string
    Edit: string
    Delete: string
    /// Form heading while editing an existing slot. Takes the slot id.
    EditSlotHeading: string -> string
    /// Form heading while creating a new slot.
    CreateSlotHeading: string
    Cancel: string
    SlotIdLabel: string
    SlotIdPlaceholder: string
    AdClientIdLabel: string
    AdClientIdPlaceholder: string
    FormatLabel: string
    StyleCssLabel: string
    StyleCssPlaceholder: string
    Saving: string
    Update: string
    Create: string
    Refresh: string
    /// Client-side validation: the slot id field was left blank.
    SlotIdRequired: string
    /// The create/update request failed. Takes the failure reason.
    SaveFailed: string -> string
    /// The delete request failed. Takes the failure reason.
    DeleteFailed: string -> string
    /// Fallback failure reason when the server returned an empty body.
    /// Takes the HTTP status code.
    EmptyResponseReason: int -> string
}

/// Widget 4 — premium-user list.
type PremiumUserWidgetMessages = {
    Title: string
    Subtitle: string
    ColumnUserId: string
    ColumnGrantedAt: string
    ColumnGrantedBy: string
    ColumnReason: string
    Refresh: string
    Refreshing: string
    Loading: string
    /// Empty state: no premium users granted yet.
    EmptyState: string
}

/// The Phase 61 public-utility PlatformAdmin widgets
/// (`PublicUtilityWidgets`) surfaced under
/// `ClientConfig.PlatformAdminProfile = PublicUtilityPlatformAdminProfile`.
type PublicUtilityWidgetsMessages = {
    /// Page heading above all four widgets.
    Heading: string
    Fetch: PublicUtilityFetchMessages
    Traffic: TrafficWidgetMessages
    RateLimits: RateLimitWidgetMessages
    AdUnits: AdUnitWidgetMessages
    PremiumUsers: PremiumUserWidgetMessages
}

/// The built-in file-upload / data-manager module (`FileManagerUI`).
type FileManagerMessages = {
    /// `FileReader.onerror` — the local file couldn't be read at all
    /// (browser-side, before any request reaches the server). Takes the
    /// file's own name.
    FileReadError: string -> string
    /// `DeleteFile` server error. Takes the server's error string.
    DeleteFailed: string -> string
    /// `ReprocessFile` server error. Takes the server's error string.
    ReprocessFailed: string -> string
    /// `ResetDataStore` server error. Takes the server's error string.
    ResetFailed: string -> string
    /// `RetryIngestion` server error. Takes the server's error string.
    RetryFailed: string -> string
    /// File size under 1024 bytes — the exact byte count needs no
    /// locale-sensitive formatting.
    SizeBytes: int64 -> string
    /// File size in kibibytes. Takes the value ALREADY formatted to one
    /// decimal place at the call site (444's date/number decision: the
    /// format is a property of the call site, not of the language).
    SizeKilobytes: string -> string
    /// File size in mebibytes. Same pre-formatted-value convention as
    /// `SizeKilobytes`.
    SizeMegabytes: string -> string
    /// Subheading over the per-file processing-error list.
    ProcessingErrorsHeading: string
    /// One processing-error row. Takes the file name and the server's
    /// error string.
    ProcessingErrorLine: string -> string -> string
    /// Ingestion-status badge text/tooltip — searchable.
    IndexedLabel: string
    IndexedTooltip: string
    /// Ingestion-status badge text/tooltip — vectorisation in flight.
    IndexingLabel: string
    IndexingTooltip: string
    /// Ingestion-status badge text for `Failed` (the tooltip carries the
    /// server's own reason instead — left untranslated, see the view).
    NotIndexedLabel: string
    /// Phase 220 status-filter dropdown options. Kept as separate fields
    /// from the badge text above even where the English wording
    /// coincides — the dropdown and the badge are different surfaces and
    /// a translation is free to diverge between them.
    FilterAll: string
    FilterIndexed: string
    FilterIndexing: string
    FilterNotIndexed: string
    FilterNotAttempted: string
    FilterByStatusLabel: string
    UploadPanelTitle: string
    UploadSectionTitle: string
    ChooseFilesButton: string
    UploadHint: string
    UploadedFilesPanelTitle: string
    NoFilesUploaded: string
    NoFilesMatchFilter: string
    ColumnDataType: string
    ColumnFileName: string
    ColumnUploaded: string
    ColumnRows: string
    ColumnSize: string
    ColumnSearchIndex: string
    RetryTooltip: string
    RetryButton: string
    ReprocessTooltip: string
    ReprocessButton: string
    DeleteTooltip: string
    DeleteButton: string
    /// The native `window.confirm` prompt before a delete. Takes the
    /// file's name.
    ConfirmDelete: string -> string
    ResetHelp: string
    ResetTooltip: string
    ResetButton: string
    /// The native `window.confirm` prompt before a full reset. Takes the
    /// file count being deleted.
    ConfirmReset: int -> string
}

/// The built-in service-status-board admin module (`ServiceStatusBoardUI`,
/// Phase 9p.A / Phase 751). Aggregates Health / Preflight / Drift /
/// RateLimit / JobQueue / SmokeTest into one composite snapshot.
type ServiceStatusBoardMessages = {
    /// Page heading.
    Heading: string
    /// Sub-heading prose under the page heading.
    Subheading: string
    /// Per-section refresh button label, at rest.
    Refresh: string
    /// Top "refresh everything" button label, at rest.
    RefreshAll: string
    /// Shared loading-state label for both refresh buttons above.
    Refreshing: string
    /// Shown while the composite snapshot itself is still loading.
    Loading: string
    /// `OverallStatus.AllOk` pill label.
    AllSystemsOk: string
    /// `OverallStatus.DegradedBy` pill label — takes the comma-joined
    /// section names, which are wire-shaped and never translated.
    DegradedBy: string -> string
    /// `OverallStatus.UnhealthyBy` pill label — takes the comma-joined
    /// section names, which are wire-shaped and never translated.
    UnhealthyBy: string -> string
    /// Severity-pill label for `StatusSeverity.Ok`.
    SeverityOk: string
    /// Severity-pill label for `StatusSeverity.Warn`.
    SeverityWarn: string
    /// Severity-pill label for `StatusSeverity.Error`.
    SeverityError: string
    /// Severity-pill label for a disabled section.
    SeverityDisabled: string
    /// Snapshot-generated-at footnote — takes the already-formatted
    /// timestamp (formatting happens at the call site; the format
    /// specifier itself is not a translatable string).
    GeneratedAt: string -> string
    /// Fallback headline when a section name doesn't match any known
    /// section (`sectionOf`'s defensive branch, exercised only inside
    /// pure `update`-reachable comparisons — never itself rendered).
    /// Takes the unrecognised, wire-shaped section name.
    UnknownSectionHeadline: string -> string
    /// Fallback detail accompanying `UnknownSectionHeadline`.
    SectionMappingIncomplete: string
    /// Headline when a per-section refresh command itself errors.
    /// Takes the wire-shaped section name.
    SectionRefreshFailed: string -> string
    /// Internal error text raised when `loadSectionCmd` is asked to
    /// refresh a section name it doesn't recognise (defensive — every
    /// call site passes a known section constant). Takes the
    /// unrecognised, wire-shaped section name.
    UnknownSectionMessage: string -> string
}

/// Display labels for the `UsageGrouping` aggregation choices. Distinct
/// from the DU cases themselves, which select the server-side
/// aggregation and are wire-shaped (the select's value/onChange round-trip
/// compares against these same labels, but never leaves the client).
type UsageGroupingMessages = {
    ByDay: string
    ByMonth: string
    ByResourceKind: string
    ByUser: string
}

/// The built-in usage-dashboard admin module (`UsageDashboard`).
type UsageDashboardMessages = {
    /// Page heading.
    Heading: string
    /// Sub-heading prose under the page heading.
    Subheading: string
    /// Visible `<label>` text AND accessible name of the grouping
    /// `<select>` — both read from one field so they cannot drift.
    GroupByLabel: string
    /// Display labels for each `UsageGrouping` value.
    Grouping: UsageGroupingMessages
    /// Reload action.
    Refresh: string
    /// Export-CSV button label while the export is in flight.
    Exporting: string
    /// Export-CSV button label at rest.
    ExportCsv: string
    /// Aggregate-table bucket column header.
    ColumnBucket: string
    /// Aggregate-table quantity column header.
    ColumnQuantity: string
    /// Empty-state prose when the aggregate query returns no rows.
    NoRecords: string
    /// `NotLoaded` state prompt — shown before the first load.
    ClickRefresh: string
    /// `Loading` state prompt.
    Loading: string
}

/// The mapping-aware Data Manager module (`MappingDataManagerUI`) — CSV
/// upload, the data-quality review step, the target-format picker, the
/// column-mapping wizard (with its derived-column builder), the dry-run
/// validation preview, and the imported-files list.
type MappingDataManagerMessages = {
    // ─── Errors raised from the pure `update` reducer ──────────────────
    /// Error banner after a one-click re-ingest fails. Takes the server
    /// error text.
    ReingestionFailed: string -> string
    /// Error banner when the browser `FileReader` can't read a chosen
    /// file. Takes the file name.
    FileReadFailed: string -> string
    /// Error banner when a mapping target is picked whose `DataType` has
    /// no schema registered (`SelectTarget`).
    NoSchemaCannotMap: string
    /// Error banner when confirm/commit is attempted against a target
    /// type that no longer publishes a schema (`ConfirmMapping` /
    /// `CommitConversion` — the schema was available when the wizard
    /// opened but the deployment changed underneath it).
    NoSchemaPublished: string
    /// Error banner after `ReprocessFile` fails. Takes the server error
    /// text.
    ReprocessFailed: string -> string
    /// Error banner after `ResetDataStore` fails. Takes the server error
    /// text.
    ResetFailed: string -> string
    /// One derived-column step in a `ConversionRecord`'s remediation-steps
    /// provenance list. Takes the already-described expression (e.g. "Full
    /// Name = concat(First, Last)"). Read directly off the catalog — this
    /// runs inside the pure conversion pipeline, not a rendered tree.
    DerivedRemediationStep: string -> string

    // ─── File-size units (`formatSize`) ────────────────────────────────
    UnitBytes: string
    UnitKilobytes: string
    UnitMegabytes: string

    // ─── Data-type / column-type labels ────────────────────────────────
    /// File-list "Data Type" column value for the `UnrecognisedData`
    /// detect sentinel — the sentinel id itself is a wire value and stays
    /// unlocalised; only this display label is.
    UnrecognisedLabel: string
    /// `ColumnDataKind.StringColumn` display label.
    TypeText: string
    /// `ColumnDataKind.NumberColumn` display label.
    TypeNumber: string
    /// `ColumnDataKind.DateColumn` display label.
    TypeDate: string
    /// `ColumnDataKind.BooleanColumn` display label.
    TypeBoolean: string

    // ─── Field-match badges (`MatchFlag`) ───────────────────────────────
    MatchConfident: string
    MatchLowConfidence: string
    MatchTypeMismatch: string
    MatchAmbiguous: string
    MatchUnmatched: string

    // ─── Mapping-grid chrome ────────────────────────────────────────────
    /// The unmapped option in a per-field column `<select>`.
    NotMappedOption: string
    /// Reused for the mapping-grid column header and the derived-column
    /// builder's target-field select label — same concept, same wording.
    TargetField: string
    ColumnType: string
    ColumnCsvColumn: string
    ColumnMatch: string
    /// Tooltip on the red `*` beside a required field's name.
    RequiredTooltip: string
    /// Badge shown instead of a match badge when a field is satisfied by a
    /// derived column rather than a 1:1 map.
    DerivedBadge: string

    // ─── Date-order choice (`DateOrder`) ────────────────────────────────
    DateOrderDayFirst: string
    DateOrderMonthFirst: string
    DateOrderYearFirst: string

    // ─── ReviewData step (data-quality scan + remediation) ─────────────
    ReviewDataIntro: string
    /// Opt-out toggle label for a column's safe fixes.
    ApplyFixes: string
    /// "e.g. …" prefix before a column issue's example values. Takes the
    /// already-joined example list.
    ExampleValues: string -> string
    /// Detected-unit annotation beside a column name. Takes the unit
    /// symbol (e.g. "$").
    UnitKeptInLabel: string -> string
    /// Before/after remediation preview on an example cell value. Takes
    /// the raw and the remediated value.
    PreviewBeforeAfter: string -> string -> string
    /// Blocker line naming the ambiguous-date columns still awaiting an
    /// order choice. Takes the already-joined column list.
    ChooseDateOrderFor: string -> string
    ContinueToMapping: string

    // ─── Auto-mapped review list (flagged fields banner) ───────────────
    AutoMappedWarningHeading: string
    /// Per-field detail when the suggester found a column. Takes the
    /// guessed column name.
    GuessedColumn: string -> string
    NoColumnFound: string

    // ─── Derived-column builder ─────────────────────────────────────────
    /// `ColumnExpr` kind picker option: `Concat`.
    DerivedKindConcat: string
    /// `ColumnExpr` kind picker option: `SplitTake`.
    DerivedKindSplitTake: string
    /// `ColumnExpr` kind picker option: `Substring`.
    DerivedKindSubstring: string
    /// `ColumnExpr` kind picker option: `Constant`.
    DerivedKindConstant: string
    AddDerivedColumnHeading: string
    /// Placeholder of the target-field select.
    FieldPlaceholder: string
    /// "From" — the expression-kind select label.
    DerivedFromLabel: string
    /// The constant-value text input label.
    ValueLabel: string
    ColumnALabel: string
    ColumnBLabel: string
    /// Placeholder shared by every source-column select in the builder.
    ColumnPlaceholder: string
    /// The join-separator text input label (`Concat`).
    SeparatorLabel: string
    /// Reused by the `SplitTake` and `Substring` kinds, each of which has
    /// only one source-column select.
    ColumnLabel: string
    /// The split-delimiter text input label (`SplitTake`).
    DelimiterLabel: string
    /// The split-index text input label (`SplitTake`).
    PartNumberLabel: string
    /// The substring-start text input label (`Substring`).
    StartLabel: string
    /// The substring-length text input label (`Substring`).
    LengthLabel: string
    AddButton: string
    DerivedColumnsFootnote: string
    RemoveButton: string

    // ─── ReviewValidation step (dry-run report) ─────────────────────────
    /// Green summary banner. Takes the total row count.
    AllRowsValidatedCleanly: int -> string
    /// Red summary banner — commit is blocked. Takes the failing and
    /// total row counts.
    RowsFailBlocked: int -> int -> string
    /// Amber summary banner — commit is allowed anyway. Takes the failing
    /// and total row counts.
    RowsFailWarn: int -> int -> string
    /// Per-column card heading. Takes the column name and its failing-cell
    /// count.
    FailingCellsHeading: string -> int -> string
    /// Fallback cell-issue reason when the report carries no violation
    /// text. Takes the expected-shape description.
    ExpectedValue: string -> string
    /// One cell-issue line. Takes the row number, the actual (offending)
    /// value, and the reason.
    RowIssueDetail: int -> string -> string -> string
    TruncatedCellsNote: string
    Importing: string
    ImportButton: string
    BackToMapping: string

    // ─── Wizard shell ────────────────────────────────────────────────
    /// Wizard header. Takes the file name being mapped.
    MapFileNameHeading: string -> string
    CancelButton: string
    /// PickTarget-step intro. Takes the detected column count.
    DetectedColumnsPrompt: int -> string
    NoSchemaTypesRegistered: string
    /// ReviewMapping-step subheading. Takes the target type's display
    /// name.
    MappingToLabel: string -> string
    ChangeFormatButton: string
    ReusedSavedMappingNote: string
    /// Blocker line naming the still-unmapped required fields. Takes the
    /// unmapped count and the already-joined field-name list.
    RequiredFieldsUnmapped: int -> string -> string
    /// One derived-column validation error. Takes the field name and the
    /// detail.
    DerivedColumnError: string -> string -> string
    Validating: string
    ConfirmAndValidateButton: string
    ValidateEveryRowNote: string
    ColumnMappingPanelTitle: string

    // ─── Ingestion-status badges + filter ───────────────────────────────
    IndexedTooltip: string
    /// Reused for the badge text and the status-filter option.
    IndexedBadge: string
    IndexingTooltip: string
    /// Badge text, with the in-progress ellipsis — distinct from the
    /// filter option's `FilterIndexing`, which has none.
    IndexingBadge: string
    /// Reused for the `Failed` badge text and the status-filter option.
    NotIndexedBadge: string
    FilterAll: string
    /// The status-filter option for `OnlyPending` — no ellipsis, unlike
    /// the badge's `IndexingBadge`.
    FilterIndexing: string
    /// The status-filter option for `OnlyNotIndexed`.
    FilterNotAttempted: string
    FilterByIndexStatus: string

    // ─── Imported-files table ────────────────────────────────────────
    NoFilesImportedYet: string
    NoFilesMatchFilter: string
    ColumnDataType: string
    ColumnFileName: string
    ColumnRows: string
    ColumnSize: string
    ColumnSearchIndex: string
    /// Fallback fragment inside `ConvertedFromTooltip` when a conversion
    /// applied no remediation steps.
    NoRemediationLabel: string
    /// Tooltip on the "Converted" badge. Takes the source file name and
    /// the already-joined remediation-steps summary (or
    /// `NoRemediationLabel`).
    ConvertedFromTooltip: string -> string -> string
    ConvertedBadge: string
    RetryIngestionTooltip: string
    RetryButton: string
    NewMappingTooltip: string
    NewMappingButton: string
    ReprocessTooltip: string
    ReprocessButton: string
    DeleteButton: string
    /// `window.confirm` prompt before deleting a file. Takes the file
    /// name.
    DeleteFileConfirm: string -> string
    ResetScopeNote: string
    ResetDataStoreTooltip: string
    ResetDataStoreButton: string
    /// `window.confirm` prompt before wiping the data store. Takes the
    /// file count about to be deleted.
    ResetConfirm: int -> string

    // ─── Top-level panels ────────────────────────────────────────────
    ImportCsvPanelTitle: string
    UploadFileSectionTitle: string
    ChooseCsvButton: string
    CheckingKnownStructure: string
    UploadHelpText: string
    ImportedFilesPanelTitle: string
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
    ModuleVisibilityAdmin: ModuleVisibilityAdminMessages
    MigrationStatus: MigrationStatusMessages
    SessionSecurity: SessionSecurityMessages
    Home: HomeMessages
    InviteAccept: InviteAcceptMessages
    PublicUtilityWidgets: PublicUtilityWidgetsMessages
    FileManager: FileManagerMessages
    ServiceStatusBoard: ServiceStatusBoardMessages
    UsageDashboard: UsageDashboardMessages
    MappingDataManager: MappingDataManagerMessages
}