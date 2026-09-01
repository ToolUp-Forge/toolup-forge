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
}