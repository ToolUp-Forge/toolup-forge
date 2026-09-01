// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── The built-in English catalog + locale resolution (Phase 444) ─────
//
// Two modules, both deliberately Feliz-free so they compile and run on
// .NET as well as under Fable. That is the same discipline
// `SidebarVisibility.fs` / `NoActiveTeamLanding.fs` / `AdminTiles.fs`
// document immediately below this point in the compile order: the
// DECISION (which locale wins, what the resolved catalog is) is pure,
// so the in-process test harness exercises it without a browser, a
// React renderer, or a Fable build. The React context + hook that
// publish the result to the view tree live in
// `MessageCatalogProvider.fs`, which is where the Feliz dependency
// starts.

/// The English catalog the SDK ships, plus locale resolution and the
/// consumer override hook.
///
/// `ModuleSuffix` is load-bearing: the record type `MessageCatalog` is
/// declared in `Shared/LocalizationTypes.fs`, and F# refuses a module
/// and a type of the same name in one namespace across two files
/// (FS0250) unless the module's compiled name is distinguished. The
/// type-plus-companion-module pairing is the idiom this substrate wants
/// (`Translations` / `LocaleCode` in Core do the same, in one file); the
/// attribute is what lets the pairing survive the split that keeps the
/// type declarations ahead of `SDK.ClientTypes.fs`.
[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module MessageCatalog =

    // ─── Locale resolution ────────────────────────────────────────────

    /// The locale of the built-in catalog, and the last link in every
    /// fallback chain. A blank fallback anywhere resolves to this
    /// rather than to an empty tag, so `Intl` is never handed `""`.
    [<Literal>]
    let BuiltInLocale = "en"

    /// The `_platform` config key carrying a team's default locale.
    /// Identical to the server-side `LocaleResolver.TeamLocale`, on
    /// purpose: one team setting drives the SSR tier and the client
    /// shell, and a deployment that sets it does not have to discover
    /// that the two tiers read different keys.
    [<Literal>]
    let TeamLocaleConfigKey = "_platform.locale"

    let private normalise (candidate: string) : string option =
        if System.String.IsNullOrWhiteSpace candidate then
            None
        else
            Some(candidate.Trim())

    let private firstNonBlank (candidates: string option list) : string option =
        candidates |> List.tryPick (Option.bind normalise)

    /// Resolve the active locale from the declared mode and the two
    /// ambient inputs the shell can observe. Total: every arm ends at
    /// `BuiltInLocale`, so there is no configuration that produces a
    /// blank tag.
    ///
    /// Resolution order, by mode:
    ///   `FixedLocale c`      → `c` (blank → `"en"`). Nothing else is read.
    ///   `BrowserLocale fb`   → browser → `fb` → `"en"`.
    ///   `TeamDefault fb`     → team config → browser → `fb` → `"en"`.
    ///
    /// The browser link sits inside `TeamDefault` deliberately. A team
    /// that has set no default is not asking for English — it is asking
    /// for nothing, and the visitor's own preference is a better answer
    /// than the deployment's fallback. A team that HAS set one wins over
    /// the browser, because that setting is an explicit act by someone
    /// who administers the team.
    let resolveLocale (mode: LocaleMode) (teamLocale: string option) (browserLocale: string option) : string =
        let resolved =
            match mode with
            | FixedLocale locale -> firstNonBlank [ Some locale ]
            | BrowserLocale fallback -> firstNonBlank [ browserLocale; Some fallback ]
            | TeamDefault fallback -> firstNonBlank [ teamLocale; browserLocale; Some fallback ]

        resolved |> Option.defaultValue BuiltInLocale

    /// Read a team's default locale out of the prefetched `_platform`
    /// config map (`Model.PlatformConfig`). `None` when the key is
    /// absent or blank — which is the common case, and is why
    /// `TeamDefault` falls through rather than resolving to `""`.
    let teamLocaleOf (platformConfig: Map<string, string>) : string option =
        platformConfig |> Map.tryFind TeamLocaleConfigKey |> Option.bind normalise

    // ─── The built-in English catalog ─────────────────────────────────

    /// The SDK's English catalog. Every string the shell and the swept
    /// built-in modules render comes from here, so this value is also
    /// the readable inventory of what a translation has to cover.
    let english: MessageCatalog = {
        Locale = BuiltInLocale
        Shell = {
            TeamLabel = "Team:"
            SelectTeam = "Select team"
            SwitchTeamFailed = "Couldn't switch team. Please try again."
            CreateTeamFailed = "Couldn't create the team. Please try again."
            NoTeamHeading = "You're not in a team yet"
            NoTeamBody =
                "Analysis tools are scoped to a team. Create one to get started, or ask a Platform Admin to add you (or open an invite link you've been sent)."
            TeamNamePlaceholder = "Team name"
            CreatingTeam = "Creating…"
            CreateTeam = "Create team"
            PickTeam = "Pick a team to continue:"
            SignOut = "Sign out"
            BackToApp = "Back to app"
            AdministrationArea = "Administration"
            ProductArea = "App"
            ShowingAllModulesHint =
                "Showing every module, including ones hidden from this team. Click to return to the member view."
            MemberViewHint =
                "You're seeing this team's member view — modules hidden from the team are omitted. Click to reveal them."
            ViewingAllModules = "Viewing all modules"
            ShowHiddenModules = "Show hidden modules"
            ResultsAvailableIn = fun moduleName -> $"Results available in {moduleName}"
            NoViewForRoute = fun route -> $"No view registered for page route {route}"
            ModuleNotFound = "Error: Module not found"
        }
        BootDegradation = {
            Heading = "Some data failed to load"
            Retry = "Retry"
            Dismiss = "Dismiss"
            Sources = {
                Teams = "Team memberships"
                ActiveTeam = "Active team"
                TeamAutoSelect = "Team auto-select"
                Permissions = "Module permissions"
                Configs = "Saved configuration"
                Flags = "Feature flags"
                PlatformRole = "Platform-admin role"
                TeamRole = "Team role"
                AuthBridge = "Session refresh"
            }
        }
        Toast = {
            Info = "Info"
            Warning = "Warning"
            Error = "Error"
        }
        NotAuthorised = {
            TitleNotSignedIn = "Sign in to continue"
            TitleNoActiveTeam = "Pick a team first"
            TitleNotInVisibilityProfile = "This page isn't part of this workspace"
            TitleNoAccess = "You don't have access to this page"
            HintNotSignedIn =
                fun moduleName ->
                    $"\"{moduleName}\" isn't available to signed-out visitors. Sign in and try the link again."
            HintRequiresPlatformAdmin =
                fun moduleName ->
                    $"\"{moduleName}\" is a platform administration page. Ask a Platform Admin if you need access."
            HintRequiresTeamOwnerAdmin =
                fun moduleName ->
                    $"\"{moduleName}\" is for team owners and admins. Ask an owner of this team if you need access."
            HintNotExposedToTeam =
                fun moduleName -> $"\"{moduleName}\" isn't switched on for this team. A Platform Admin can enable it."
            HintNotAvailableToSubject =
                fun moduleName ->
                    $"\"{moduleName}\" isn't available in your current workspace. Switching team or scope may reach it."
            HintNoActiveTeam =
                fun moduleName ->
                    $"\"{moduleName}\" is scoped to a team, and you haven't picked one yet. Choose a team to continue."
            HintNotInVisibilityProfile =
                fun moduleName ->
                    $"\"{moduleName}\" isn't one of the modules this workspace uses. An owner can add it to the workspace's module selection."
            GoHome = "Go to home"
        }
        ModuleBoundary = {
            Heading = "This module encountered an error."
            Body = "You can reload just this module without affecting the rest of the app."
            Reload = "Reload module"
        }
        CommandPalette = {
            DialogLabel = "Command palette"
            SearchLabel = "Search pages"
            SearchPlaceholder = "Jump to a page…"
            NoMatches = "No pages match that."
            HintMove = "↑↓ to move"
            HintOpen = "↵ to open"
            HintClose = "esc to close"
        }
        Sidebar = {
            PinnedSection = "Pinned"
            Pin = "Pin"
            Unpin = "Unpin"
            Hide = "Hide"
            Restore = "Restore"
            PoweredBy = "Powered by ToolUp-Forge"
        }
        TeamManager = {
            MyTeamsPanel = "My teams"
            NoTeamsYet = "You're not a member of any team yet. Ask a Platform Admin to add you to one."
            Switch = "Switch"
            Manage = "Manage"
            ActiveBadge = "active"
            MembersPanel = "Members"
            NoMembers = "No members (loading, or team was deleted)."
            YouSuffix = "(you)"
            RemoveMember = "Remove"
            RoleLabel = "Role:"
            InvitePanel = "Invite a member"
            InviteHelp =
                "Start typing a name or email — directory matches appear as you type. Select one, or enter an email manually to send a pending invite (the recipient is added when they next sign in). Advanced: paste a raw identity-provider user id (e.g. an Entra `oid`) to add the member directly without an invite step."
            Inviting = "Inviting…"
            InviteMember = "Invite member"
            IdentifierRequired = "User ID or email can't be empty"
            EmailRequired = "Email can't be empty"
            TransferOwnership = "Transfer ownership"
            TransferOwnershipHelp =
                fun teamName ->
                    $"Choose a current member of {teamName} to become the new Owner. You'll be demoted to Admin once the transfer completes."
            TransferFilterPlaceholder = "Filter members by name or email"
            TransferNoOtherMembers =
                "This team has no other members to transfer ownership to. Add a member first, then transfer."
            TransferNoMatches = "No members match your filter."
            TransferConfirmHeading = "Confirm ownership transfer"
            TransferConfirmPrompt =
                fun teamName outgoing incoming ->
                    $"Transfer ownership of {teamName} from {outgoing} (you) to {incoming}?"
            Transferring = "Transferring…"
            ConfirmTransfer = "Confirm transfer"
            Back = "Back"
            Cancel = "Cancel"
            BreadcrumbMyTeams = "← My teams"
            BreadcrumbMembers = "← Members"
            PendingInvites = "Pending invites"
            PendingInvitesPanel = "Pending email invites"
            PendingInvitesLoading = "Loading pending invitations…"
            NoPendingInvites =
                "No pending email invitations. Use 'Invite by email' to add one — the recipient will auto-join the team on their first sign-in matching the email."
            InviteByEmail = "Invite by email"
            InviteByEmailHeading = "Invite by email (no link)"
            InviteByEmailHelp =
                "The recipient joins automatically on their first sign-in matching the email. No invitation link is generated."
            EmailPlaceholder = "Email address"
            ExpiresInDays = "Expires in (days):"
            Issuing = "Issuing…"
            IssueInvitation = "Issue invitation"
            RevokeInvite = "Revoke"
            RevokeInviteHeading = "Revoke pending invitation?"
            Reissue = "Re-issue"
            Reissuing = "Re-issuing…"
            Expired = "Expired"
            RecentlyExpired = "Recently expired (last 30 days)"
            Dismiss = "dismiss"
            TeamIdLabel = fun teamId -> $"Team ID: {teamId}"
            InviteIdentifierPlaceholder = "person@example.com"
            TransferRoleExplanation =
                fun newOwner ->
                    $"{newOwner} becomes the Owner and you become an Admin. Only the new Owner can transfer it back."
            InviteExpires = fun role at -> $"{role} · expires {at}"
            InviteExpired = fun role at -> $"{role} · expired {at}"
        }
        HealthMonitor = {
            LiveHealthTab = "Live health"
            PreflightTab = "Preflight"
            Refresh = "Refresh"
            Refreshing = "Refreshing..."
            Refetch = "Re-fetch"
            ColumnStatus = "Status"
            ColumnProbe = "Probe"
            ColumnKind = "Kind"
            ColumnTimeout = "Timeout"
            ColumnElapsed = "Elapsed"
            ColumnMessage = "Message"
            ColumnValidator = "Validator"
            NoProbes =
                "No health probes registered. Companions self-register via services.AddSingleton<IHealthCheck>(instance) — see TECHNICAL_GUIDE.md."
            NoValidators =
                "No validators recorded. Either no IConfigValidator was registered at the most recent boot, or ServerConfig.SkipPreflight = true was set for an emergency boot."
            ProbesFootnote =
                "Each refresh re-runs every registered IHealthCheck in parallel. Probes are deployment-wide — no per-team filter applies."
            LiveHealthHeading = "Live health"
            Loading = "Loading..."
            SchedulerDriftHeading = "Job scheduler tick drift"
            SchedulerMissed60m = "Missed (60-min)"
            SchedulerLastDrift = "Last drift"
            SchedulerLastMissAt = "Last miss at"
            DegradedReason = "Reason"
            DegradedImpact = "Impact"
            PreflightHeading = "Preflight (most recent boot)"
            PreflightFootnote =
                "Snapshot from the most recent startup. Re-fetch to confirm a redeploy passed without a hard reload — validators do not re-run against this view."
            PreflightUnavailable =
                "Preflight snapshot is not available — this deployment was composed before Phase 9m landed, or no IPreflightSnapshot service is registered."
            SchedulerLagHelp =
                "Counts minute boundaries where the scheduler woke late (debugger pause, GC stall, container throttling). Healthy deployments stay at zero; recovers automatically once the process resumes."
            DegradedCapabilities = fun count -> $"Degraded capabilities ({count})"
            DegradedCapabilitiesHelp =
                "A capability wired best-effort at startup and failed without crashing the deployment. The server is up, but the listed capability is down until remediated. Alert on a non-empty set."
            Remediation = "Remediation"
            AsOf = fun at -> $"as of {at}"
            DegradedSince = fun at -> $"since {at}"
            GeneratedAt = fun at probeCount -> $"Generated at {at} ({probeCount} probes)"
        }
        DataIngestion = {
            StatusNotConfigured = "Not configured"
            StatusNeedsAuthorization = "Needs authorization"
            StatusConnected = "Connected"
            StatusNeedsReauthorization = "Reconnect required"
            OutcomeRefreshed = "Refreshed"
            OutcomeTransientError = "Transient error"
            OutcomeRequiresReauth = "Requires reauth"
            OutcomeDeadLettered = "Dead-lettered"
            OutcomePending = "Pending"
            Connect = "Connect"
            Disconnect = "Disconnect"
            Disconnecting = "Disconnecting..."
            Refresh = "Refresh"
            Refreshing = "Refreshing..."
            ColumnStatus = "Status"
            ColumnName = "Name"
            ColumnKind = "Kind"
            ColumnTokenStatus = "Token status"
            ColumnActions = "Actions"
            ColumnId = "Id"
            NoSourcesYet = "No data sources configured yet. Pick a connector to start configuring its credentials:"
            NoCredentialUIsRegistered =
                "No connector credential UIs are registered. Import a connector companion (e.g. ToolUp.DataSources.Strava or src/DataSources/GoogleAnalytics) and call its register() in Client.fs at module load."
            NoCredentialUIForKind =
                fun kind ->
                    $"No credential UI registered for kind '{kind}'. Import the matching connector companion's .Client.props in the client .fsproj to activate it."
            CredentialUIUnregistered =
                fun kind ->
                    $"Credential UI for kind '{kind}' was unregistered between selection and render — try clicking the Kind button again."
            NewSourceHeading = fun kind -> $"New {kind} data source"
            SinceLabel = fun at -> $"since {at}"
            AtLabel = fun at -> $"at {at}"
            NextLabel = fun at -> $"next {at}"
            OAuthFootnote =
                "OAuth-based connectors bounce through the upstream consent screen on Connect. Refresh tokens are stored in ISecretStore and never returned to the browser."
            SourcesHeading = "Data sources"
            Loading = "Loading data sources..."
            AwaitingFirstRefresh = "awaiting first refresh"
            AddDataSource = "Add data source:"
            CancelCreate = "cancel"
            DismissError = "dismiss"
        }
        AdminHome = {
            Heading = "Administration"
            Subheading = "The administration surfaces this deployment runs, filtered to the ones your role can open."
            Refresh = "Refresh"
            OpenTile = fun title -> $"Open {title}"
            NoTilesContributedHeading = "No administration tiles are contributed yet"
            NoTilesContributedBody =
                "Tiles are contributed by the modules they front, not by this page. Each administration module this deployment enables in its client configuration contributes one — a health monitor, a service status board, a usage dashboard, a team manager — and a module of your own contributes one by adding a tile contributor to the client handler registry."
            NoTilesContributedFooter =
                "Until then, the rail on the left is the complete list of administration surfaces you can reach."
            NoTilesForSubjectHeading = "Nothing to show for your role"
            NoTilesForSubjectBody =
                "A tile follows the same access rules as the surface it opens, so this page shows only what you could navigate to anyway — and right now that is nothing on this deployment."
            NoTilesForSubjectFooter = "If you expected more here, ask an administrator to review your role."
        }
        Auth = {
            SigningIn = "Signing you in…"
            Welcome = "Welcome"
            SignInPrompt = "Sign in to continue."
            SignIn = "Sign in"
            SignUp = "Sign up"
            SignOut = "Sign out"
            SignInFailedHeading = "Sign-in failed"
            TryAgain = "Try again"
            Errors = {
                DiscoveryFailed = fun detail -> $"Could not reach the identity provider ({detail})."
                InvalidState = "Sign-in state mismatch. Please try again."
                MissingCode = "No authorization code received from the identity provider."
                IssuerError = fun code -> $"Identity provider returned {code}."
                IssuerErrorDescribed = fun code description -> $"Identity provider returned {code}: {description}."
                TokenExchangeFailed = fun detail -> $"Token exchange failed ({detail})."
                NetworkError = fun detail -> $"Network error ({detail})."
                NonceMismatch = "Sign-in token did not match the original request. Please try again."
                MalformedIdToken = "Sign-in token was malformed. Please try again."
                SignatureInvalid = "Sign-in token signature could not be verified. Please try again."
                IssuerInvalid = "Sign-in token came from an unexpected identity provider. Please try again."
                AudienceInvalid = "Sign-in token was not issued for this application. Please try again."
                Expired = "Sign-in token has expired. Please try again."
            }
            Passkey = {
                SignInPrompt = "Sign in with a passkey — no password required."
                UsernamePlaceholder = "Username"
                SignIn = "Sign in with passkey"
                RegisterPrompt = "First time here? Register a passkey."
                BootstrapTokenPlaceholder = "Bootstrap token (first-time setup only)"
                Register = "Register a passkey"
            }
        }
        RateLimited = {
            Heading = "Too many requests"
            LimitExceeded =
                fun limit window -> $"You've hit the limit of {limit} requests per {window}. Please slow down."
            TryAgain = "Try again"
            TryAgainIn =
                fun seconds ->
                    if seconds = 1 then
                        "Try again in 1 second"
                    else
                        $"Try again in {seconds} seconds"
            Windows = {
                PerSecond = "second"
                PerMinute = "minute"
                PerHour = "hour"
                PerDay = "day"
                Sliding = "window"
            }
        }
        Consent = {
            Body =
                "We use cookies and similar technologies. Choose which categories to allow. Strictly necessary cookies are always on."
            RejectAll = "Reject all"
            AcceptAll = "Accept all"
            SavePreferences = "Save preferences"
            Categories = {
                Necessary = "Strictly necessary"
                Functional = "Functional"
                Analytics = "Analytics"
                Marketing = "Marketing"
                Personalisation = "Personalisation"
                ThirdPartyEmbeds = "Third-party embeds"
            }
        }
        OAuth1aCredential = {
            ConsumerKeyLabel = "Consumer key"
            ConsumerSecretLabel = "Consumer secret"
            Save = "Save"
            Authorize = "Authorize"
            Reconnect = "Reconnect"
        }
        NoActiveTeamLanding = {
            CheckForInvitations = "Check for invitations"
            Checking = "Checking for invitations…"
            NothingPending = "No invitations are waiting for you yet."
            Joined = fun teamName -> $"You've joined {teamName} — loading it now…"
        }
        PlatformAdmin = {
            AdminsTab = "Admins"
            TeamsTab = "Teams"
            SettingsTab = "Settings"
            AssignHeading = "Assign Platform Admin"
            AssignHelp =
                "Start typing a name or email — directory matches appear as you type. Pick one to capture their stable identity-provider user id. Advanced: paste a raw user id (e.g. an Entra `oid`) to assign directly."
            UserPickerPlaceholder = "Name, email, or user id"
            Assign = "Assign"
            EnterUserId = "Enter a user id."
            CurrentAdminsHeading = "Current Platform Admins"
            Refresh = "Refresh"
            Loading = "Loading..."
            NoAdmins = "No Platform Admins configured."
            Revoke = "Revoke"
            GrantHeading = "Grant Platform Admin?"
            GrantBody =
                "Platform Admins can change deployment-wide configuration, manage every team, and assign / revoke other Platform Admins. The grant takes effect immediately and the recipient retains any team-level roles they already hold."
            UserLabel = "User: "
            UserIdLabel = "User id: "
            Cancel = "Cancel"
            GrantConfirm = "Grant Platform Admin"
            CreateTeamHeading = "Create a team"
            CreateTeamHelp =
                "Spin up a new team and name its initial Owner. The Owner is the only role that can add or remove Team Admins; everything else (Members, Admins, ownership transfer) is managed within the team or by another Platform Admin."
            TeamNameLabel = "Team name"
            TeamNamePlaceholder = "e.g. Marketing Analytics"
            InitialOwnerLabel = "Initial owner"
            TeamNameRequired = "Team name can't be empty."
            OwnerRequired = "Pick an initial owner (or use \"Self\")."
            SelfTooltip = fun selfDisplay -> $"Make yourself ({selfDisplay}) the owner"
            SelfChecked = "Self ✓"
            SelfUnchecked = "Self"
            SelfOwnerConfirm = fun selfDisplay -> $"You ({selfDisplay}) will become the team's initial Owner."
            OwnerConfirm = fun label -> $"{label} will become the team's initial Owner."
            Creating = "Creating…"
            CreateTeam = "Create team"
            ColumnTeam = "Team"
            ColumnCreated = "Created"
            ColumnMembers = "Members"
            ColumnOwners = "Owners"
            ColumnAdmins = "Admins"
            ColumnActions = "Actions"
            AllTeamsHeading = "All teams"
            LoadingTeams = "Loading teams…"
            NoTeamsYet = "No teams yet."
            ArchivedBadge = "Archived"
            Restore = "Restore"
            Delete = "Delete"
            Archive = "Archive"
            DeleteTeamHeading = "Delete this team?"
            DeleteTeamBody =
                "This permanently removes the team record and every member's membership of it. It cannot be undone — restore is not possible after deletion."
            TeamLabel = "Team: "
            DeleteTeam = "Delete team"
            PlatformKnowledgeBaseHeading = "Platform Knowledge Base"
            KnowledgeBaseStatus =
                fun status ->
                    $"Currently {status}. When enabled, platform-scope KB content is universally readable for authenticated users; when disabled, RAG retrieval filters it out (admin uploads still work)."
            Enabled = "Enabled"
            Disabled = "Disabled"
            DisableAction = "Disable"
            EnableAction = "Enable"
            LoadingCurrentState = "Loading current state…"
            OtherSettingsHeading = "Other Settings"
            OtherSettingsBody =
                "Additional runtime-mutable deployment knobs land here as future Phase 4b follow-ups expose them. Today: PlatformKnowledgeBase only."
        }
        PlatformUsers = {
            Heading = "Users"
            Subheading =
                "Every principal the platform has evidence for — memberships, personal scopes, and sign-in audit. Flag team-less accounts and offboard them end-to-end."
            TeamLessOnly = "Team-less only"
            Refresh = "Refresh"
            LoadingPrincipals = "Loading principals…"
            NoPrincipalsHeading = "No principals to show."
            NoPrincipalsBody =
                "The registry is a derived projection over memberships, user scopes and sign-in audit. If you expected users here, check that the storage / event-store substrate is composed."
            NoTeamLessPrincipals = "No team-less principals — every enumerated user belongs to at least one team."
            Dismiss = "dismiss"
            TeamLessBadge = "team-less"
            HasDataBadge = "has data"
            NoTeams = "no teams"
            MembershipSummary =
                fun count roles ->
                    let teamWord = if count = 1 then "team" else "teams"
                    $"{count} {teamWord} · {roles}"
            RowSubtitle = fun membership lastSeen -> $"{membership} · last seen {lastSeen}"
            PreviewAction = "Preview"
            OffboardAction = "Offboard"
            ExportOffboardAction = "Export & offboard"
            NoPreviewBadge = "no preview"
            OutcomeCompleted = "Completed"
            OutcomeSkipped = "Skipped"
            OutcomeFailed = "Failed"
            OffboardTitle = "Offboard user"
            ExportOffboardTitle = "Export & offboard user"
            OffboardCompleteTitle = "Offboard complete"
            SubjectLabel = fun label -> $"{label} — scope "
            Working = "Working…"
            ConfirmOffboard = "Confirm offboard"
            ReasonLabel = "Reason (audited)"
            ReasonPlaceholder = "e.g. departed employee cleanup"
            Cancel = "Cancel"
            PreviewImpact = "Preview impact"
            PreviewSummary =
                fun n ->
                    $"{n} record(s) / key(s) / job(s) would be affected across the registered hooks. This does not mutate anything — proceed to run the offboard."
            ColumnHook = "Hook"
            ColumnWouldAffect = "Would affect"
            ColumnDetail = "Detail"
            ConfirmationRequiredHeading = "Confirmation required"
            ConfirmationRequiredBody =
                "This deployment gates offboards behind a confirmation token. Request one below (single-approver policy), or paste a token a second admin minted (two-person policy)."
            ExportConfirmationNote =
                "Note: export-then-offboard has no confirmation-gated path — the confirmed run performs the plain erasure without the pre-export."
            ConfirmationTokenLabel = "Confirmation token"
            ConfirmationTokenPlaceholder = "paste token, or request one →"
            RequestToken = "Request token"
            Close = "Close"
            ColumnResult = "Result"
            CompletedCount = fun n -> $"{n} completed"
            SkippedCount = fun n -> $"{n} skipped"
            FailedCount = fun n -> $"{n} failed"
            ExportArchiveWritten = fun n -> $"Export archive written ({n} segments)"
            NoHooksRan = "The offboard ran with no registered lifecycle hooks (a valid no-op run)."
            ReasonRequired = "A reason is required for the audit trail."
            TokenRequired = "Enter or request a confirmation token to proceed."
        }
        PermissionsAdmin = {
            PermRead = "Read"
            PermWrite = "Write"
            PermAdmin = "Admin"
            PermSchemaOnly = "Schema-only"
            NoActiveTeam = "No active team selected. Switch into a team before managing permissions."
            TabTeamDefaults = "Team Defaults"
            TabMembers = "Members"
            TabModules = "Modules"
            ColumnModule = "Module"
            NoManagedModules = "No managed modules. Modules are registered server-side via `ServerConfig.ModuleNames`."
            SelectAll = "Select all"
            ClearAll = "Clear all"
            TeamDefaultsHeading = "Team defaults"
            TeamDefaultsSubheading =
                "Per-module permissions applied to every team member who has no explicit override. Empty rows mean the module is unreachable for that member."
            Reset = "Reset"
            Saving = "Saving..."
            SaveDefaultsLabel = "Save defaults"
            TeamDefaultsSaved = "Team defaults saved."
            SaveFailed = fun msg -> $"Save failed: {msg}"
            OverrideBadge = "override"
            NoMembersYet =
                "This team has no members yet. Add members from Team Manager before configuring per-member overrides."
            MembersListLabel = "Members"
            SelectMemberPrompt = "Select a member to view and edit their overrides."
            OverridesHeading = fun displayLabel -> $"Overrides — {displayLabel}"
            OverridesHelp =
                "Toggle any cell to stage an explicit per-member override. Use the column \"Select all\" links to grant or clear a level across every module. Effective permissions resolve to the override if present, otherwise the team default. Setting every level off for a module clears the override (member falls back to defaults)."
            ActiveOverridesOn = fun joined -> $"Active overrides on: {joined}"
            Update = "Update"
            SavedModuleForUser = fun moduleName userId -> $"Saved {moduleName} for {userId}."
            SavedPermissionsFor = fun userId -> $"Saved permissions for {userId}."
            OverrideSaveFailed = fun userId moduleName msg -> $"Override save failed for {userId} / {moduleName}: {msg}"
            ModulesHeading = "Modules"
            ModulesSubheading =
                "Set each module's exposure for this team. Available shows it in the sidebar + Home; Hidden removes it from both but keeps its data formats mappable in Import & Map; Unavailable removes it AND blocks mapping any data into it (\"not cleared for this team\"). Exposure is orthogonal to permission levels, which you set on the Team Defaults and Members tabs. The Overrides column counts members carrying an explicit per-module permission override."
            ColumnExposure = "Exposure"
            ColumnTeamDefault = "Team default"
            ColumnOverrides = "Overrides"
            NoDefaultPermission = "No default permission"
            ExposureAvailable = "Available"
            ExposureHidden = "Hidden"
            ExposureUnavailable = "Unavailable"
            ExposureNowAvailable = fun moduleName -> $"{moduleName} is now available in this team."
            ExposureNowHidden =
                fun moduleName -> $"{moduleName} is now hidden — off the sidebar and Home, data still mappable."
            ExposureNowUnavailable =
                fun moduleName -> $"{moduleName} is now unavailable — off the sidebar and Home, data mapping blocked."
            ExposureChangeFailed = fun moduleName msg -> $"Could not change exposure for {moduleName}: {msg}"
            Retry = "Retry"
            Loading = "Loading..."
            Dismiss = "Dismiss"
        }
        WebhookAdmin = {
            StatusUpdated = "Status updated."
            SecretRotated = "Secret rotated — copy the new value below."
            TestFired = "Test fired."
            TestFiredHttp = fun code latencyMs -> $"Test fired — HTTP {code} in {latencyMs} ms."
            TestFiredFailed = fun err -> $"Test fired — failed: {err}"
            StatusActive = "Active"
            StatusPaused = "Paused"
            StatusDisabled = "Disabled"
            OutcomeOk = fun code ms -> $"OK {code} ({ms} ms)"
            OutcomeHttpError = fun code err ms -> $"HTTP {code}: {err} ({ms} ms)"
            OutcomeFailed = fun err ms -> $"failed: {err} ({ms} ms)"
            OutcomeDeadLettered = fun err -> $"dead-lettered: {err}"
            Working = "Working..."
            Dismiss = "dismiss"
            CreateHeading = "Create subscription"
            TargetUrlLabel = "Target URL"
            TargetUrlPlaceholder = "https://hooks.example.com/services/..."
            SecretLabel = "Secret (HMAC-SHA256 signing key)"
            SecretPlaceholder = "≥ 32 char random string"
            Generate = "Generate"
            SecretHelp =
                "Copy this value into the receiving service. To replace it later, use Rotate secret on the subscription — no delete + recreate needed."
            EventTypesLabel = "Event types (comma-separated, blank = all)"
            Create = "Create"
            SecretRevealHeading = "Copy this secret now"
            SecretRevealBody = "This is the only time the secret will be shown. After dismiss, you cannot retrieve it."
            SecretRevealAck = "I have copied this"
            Pause = "Pause"
            Resume = "Resume"
            ReEnable = "Re-enable"
            TestFire = "Test fire"
            RotateSecret = "Rotate secret"
            RotateSecretConfirm =
                "Rotate this subscription's signing secret? The new secret is shown once. The previous secret keeps verifying during a short grace window so deliveries are not missed."
            Delete = "Delete"
            DeleteConfirm = "Delete this subscription? Delivery log will be pruned."
            SubscriptionIdLabel = "Subscription Id"
            TargetLabel = "Target"
            EventTypesRowLabel = "Event types"
            AllEvents = "(all)"
            StatusLabel = "Status"
            ConsecutiveFailuresLabel = "Consecutive failures"
            CreatedLabel = "Created"
            CreatedByLine = fun createdAt createdBy -> $"{createdAt} by {createdBy}"
            RecentDeliveries = "Recent deliveries"
            NoDeliveriesYet = "No deliveries yet. Use Test fire to send a synthetic event."
            ColumnAttempted = "Attempted"
            ColumnAttempt = "Attempt"
            ColumnOutcome = "Outcome"
            ColumnEventId = "Event Id"
            SubscriptionsHeading = "Subscriptions"
            Loading = "Loading..."
            NoSubscriptionsYet = "No subscriptions yet."
            CreateOrSelectPrompt = "Create a subscription, or select one to view details."
            LoadingSubscriptions = "Loading subscriptions..."
            SubscriptionNotFound = "Subscription not found in this scope."
        }
        NarrativeRenderer = {
            SaveToKnowledgeBase = "Save to Knowledge Base"
            Saving = "Saving…"
            Saved = "Saved to Knowledge Base"
            NoProvenance = "This narrative has no provenance and cannot be saved."
            DuplicateHeading = "Narrative already saved"
            DuplicateBody = fun when' -> $"A previous version was saved to the Knowledge Base on {when'}."
            DuplicateConfirmPrompt = "Overwrite it with the current version?"
            Cancel = "Cancel"
            Overwrite = "Overwrite"
            Copied = "Copied!"
            CopyAsMarkdown = "Copy as Markdown"
        }
        ServiceAccount = {
            Heading = "Service accounts"
            Subheading =
                "Machine identities owned by this scope. Each mints scoped, expiring API tokens that authenticate as the account — never as a person."
            Loading = "Loading…"
            Dismiss = "dismiss"
            PermissionRead = "Read"
            PermissionWrite = "Write"
            PermissionAdmin = "Admin"
            PermissionSchemaOnly = "Schema only"
            StatusActive = "Active"
            StatusDisabled = "Disabled"
            StatusRevoked = "Revoked"
            StatusExpired = "Expired"
            CopyTokenHeading = fun name -> $"Copy the token for \"{name}\" now"
            SecretOneTimeBody =
                "This is the only time this secret is shown. The server stores only a salted hash of it, so it cannot be shown again — if it is lost, revoke this token and mint another."
            AcknowledgeSecret = "I have copied this token"
            NewAccountHeading = "New service account"
            NameLabel = "Name"
            NamePlaceholder = "e.g. nightly-export"
            ModulePermissionsLabel = "Module permissions"
            ModuleNamePlaceholder = "module name"
            AddPermission = "Add"
            NoPermissionsHint =
                "Add at least one module permission. An account with no declared permissions is refused — an empty set would grant unrestricted access, not none."
            Working = "Working…"
            CreateAccount = "Create account"
            MintTokenHeading = "Mint a token"
            MintLabelPlaceholder = "label, e.g. CI deploy key"
            Days = "days"
            Mint = "Mint"
            NoAccountsHeading = "No service accounts yet."
            NoAccountsBody =
                "A service account is a machine identity — a CI job, a partner integration, an agent host — that authenticates as itself rather than borrowing a person's account."
            Tokens = "Tokens"
            Disable = "Disable"
            Enable = "Enable"
            NoTokensYet = "This account has no tokens yet."
            TokenIssuedSummary =
                fun issuedOn issuedBy expiresOn -> $"issued {issuedOn} by {issuedBy} · expires {expiresOn}"
            Revoke = "Revoke"
            BackToList = "← All service accounts"
            TokensForAccount = fun name -> $"Tokens — {name}"
        }
        DataSubjectRequestAdmin = {
            TabExport = "Export (Article 15)"
            TabErase = "Erase (Article 17)"
            SubjectPlaceholder = "Subject user id (e.g. identity-provider sub claim)"
            TeamPlaceholder = "Team id (optional — leave blank for cross-team)"
            ReasonPlaceholder = "Reason (ticket / case / regulator inquiry — lands in audit)"
            ExportPanelTitle = "Article 15 — data export"
            ExportPanelBody =
                "Streams every record across every registered exporter that names the subject. Scope-isolated when a Team id is supplied."
            AsyncModeLabel =
                "Run as a background job (large exports — returns a ticket, polls until ready, then downloads). Requires async DSR enabled server-side."
            RequestExport = "Request export"
            Exporting = "Exporting…"
            AggregatingSegments = "Aggregating segments from every store…"
            BackgroundExportHeading = "Background export"
            TicketLine = fun ticket status -> $"Ticket {ticket} • {status}"
            Cancel = "Cancel"
            TicketPreparing = "Preparing — assembling segments…"
            TicketReady = fun size -> $"Ready — {size} bytes; downloading…"
            TicketFailed = fun reason -> $"Failed — {reason}"
            TicketCancelled = "Cancelled"
            TicketExpired = "Expired"
            TicketUnknown = "Unknown"
            PolicyHardDeleteLabel = "Hard delete"
            PolicyHardDeleteDescription =
                "Remove records entirely. Breaks event-log integrity for the subject — only valid where no compliance-driven retention applies."
            PolicyTombstoneLabel = "Tombstone"
            PolicyTombstoneDescription =
                "Replace user-identifying fields with markers; preserve shape and version chain. Fits most GDPR / CCPA / DPDPA regimes."
            PolicyRetainPerComplianceLabel = "Retain per compliance"
            PolicyRetainPerComplianceDescription =
                "Redact only where possible; audit / event records survive. For jurisdictions where retention legally overrides erasure."
            OverridePolicyPrompt = "Override deployment default for this request (optional):"
            UseDeploymentDefault = "Use deployment default"
            UseDeploymentDefaultDescription = "Apply the policy set in ServerConfig."
            PreviewPanelTitle = "Preview — review then confirm"
            RequestIdLine = fun requestId -> $"Request id: {requestId}"
            PreviewSummaryLine =
                fun policyLabel total handlerCount ->
                    $"Policy: {policyLabel} • Total affected: {total} across {handlerCount} handler(s)"
            PreviewEmpty = "No handlers registered or no records matched — confirm is a no-op."
            HandlerRecordsAffected = fun count -> $"{count} record(s)"
            ConfirmErase = "Confirm erase"
            Confirming = "Confirming…"
            ConfirmIrreversibleFootnote = "Confirmation is irreversible under HardDelete and event-store Tombstone."
            RunSummaryPanelTitle = "Last run summary"
            RunSummaryLine =
                fun started completed overall -> $"Started {started} • Completed {completed} • Overall: {overall}"
            OverallSuccess = "success"
            OverallPartialFailure = "partial failure"
            ErasePanelTitle = "Article 17 — data erasure"
            ErasePanelBody =
                "Erase or redact every record naming the subject across every registered handler. Two-phase — Preview shows per-store affected counts; Confirm executes."
            PreviewErase = "Preview erase"
            Previewing = "Previewing…"
            PendingPreviewHint = "Pending preview ready below — confirm or cancel."
            DismissBanner = "dismiss"
            SubjectRequired = "Subject user id is required."
            ReasonRequired = "Reason is required (lands in audit)."
            BackgroundExportQueued = "Background export queued — assembling segments…"
            ExportFailed = fun reason -> $"Export failed: {reason}"
            ExportCancelled = "Export cancelled."
            ExportTicketExpiredOrUnknown = "Export ticket expired or unknown — re-submit."
            BackgroundExportReady = fun bytes -> $"Background export ready — {bytes} bytes downloaded."
            ExportReady = fun bytes -> $"Export ready — {bytes} bytes downloaded."
            RunPreviewFirst = "Run Preview first."
            EraseConfirmedSuccess = fun count -> $"Erase confirmed — {count} handler(s) ran successfully."
            EraseConfirmedPartialFailure =
                fun count -> $"Erase ran with partial failures — {count} handler(s); inspect per-handler results."
            EraseRefused = fun reason -> $"Refused: {reason}"
            EraseNotImplemented = fun detail -> $"Not implemented: {detail}"
        }
        TeamConfig = {
            RequiredMarker = "*"
            Saving = "Saving..."
            Saved = "Saved."
            Dismiss = "dismiss"
            SaveButton = "Save"
            ClearAllButton = "Clear all"
            NoEditableConfig = "This module has no editable configuration."
            ModuleKeyLabel = fun key -> $"Key: {key}"
            FlagEnabled = "Enabled"
            FlagDisabled = "Disabled"
            FlagOverridden = "Overridden"
            FlagUsingDefault = "Using default"
            FlagOwnerLabel = fun owner -> $"Owner: {owner}"
            FlagDefaultLabel = fun value -> $"Default: {value}"
            SaveOverrideButton = "Save override"
            ClearOverrideButton = "Clear override"
            FeatureFlagsHeading = "Feature flags"
            FeatureFlagsHelp =
                "Set overrides at your admin scope. Cleared overrides fall through to the next layer (User → Team → Platform → declared default)."
            LoadingFlags = "Loading flags..."
            NoFlagsDeclared =
                "No feature flags declared. Modules surface flags automatically when they list them at register() time."
            ModulesHeading = "Modules"
            SidebarLoading = "Loading..."
            NoConfigurableModules = "No configurable modules."
            SelectModulePrompt = "Select a module to configure."
            LoadingModulesPrompt = "Loading modules..."
            ModuleNotAvailable = fun key -> $"Module '{key}' is not available for configuration."
            ConfigurationTab = "Configuration"
            FeatureFlagsTab = "Feature flags"
        }
        TenantLifecycleAdmin = {
            Heading = "Tenant lifecycle"
            Subheading = "The outcome of a tenant scope's most recent provision / offboard run."
            ScopeIdLabel = "Tenant / team scope id"
            ScopeIdPlaceholder = "e.g. team-abc123"
            LoadingButton = "Loading…"
            LoadLastRun = "Load last run"
            ScopeFormFootnote =
                "Shows the durable summary of the scope's most recent provision / offboard run. The registered lifecycle hook set is listed at the /dev/inspect diagnostics endpoint."
            Dismiss = "dismiss"
            Loading = "Loading…"
            NoRunForScope = fun scope -> $"No lifecycle run recorded for \"{scope}\"."
            NoRunForScopeFallback = "No lifecycle run recorded for this scope."
            NoRunHelp =
                "A provision or offboard run for the scope will appear here once one has executed (the summary is durable across restarts)."
            EnterScopePrompt = "Enter a tenant or team scope id above to view its last lifecycle run."
            LastRunHeading = fun phase -> $"Last run — {phase}"
            PillCompletedLabel = "completed"
            PillSkippedLabel = "skipped"
            PillFailedLabel = "failed"
            PillMsTotalLabel = "ms total"
            BadgeCompleted = "Completed"
            BadgeSkipped = "Skipped"
            BadgeFailed = "Failed"
            NoHooksRecorded = "The run completed with no registered lifecycle hooks (a valid no-op run)."
            ColumnHook = "Hook"
            ColumnResult = "Result"
            ColumnDetail = "Detail"
            ColumnElapsed = "Elapsed"
            ElapsedMsLabel = fun ms -> $"{ms} ms"
        }
        ModuleVisibilityAdmin = {
            Heading = "Module visibility"
            Subheading = "Curate which of this deployment's registered modules are surfaced at your scope."
            Dismiss = "dismiss"
            ProfileSaved = "Profile saved."
            ProfileCleared = "Profile cleared — this scope no longer contributes a layer."
            Loading = "Loading…"
            NoResolutionYet =
                "No layer declares a profile, so every registered module is surfaced. Saving a profile below makes this scope the first contributing layer."
            NoItems = "none"
            GovernedModules = fun n -> $"Governed modules ({n})"
            SelectedAfterEveryLayer = fun n -> $"Selected after every layer ({n})"
            ExcludedEntries = fun n -> $"Excluded pages / entries ({n})"
            ContributingScopes = fun n -> $"Contributing scopes ({n})"
            ResolvedForYouHeading = "Resolved for you"
            ResolvedForYouHelp =
                "Composed platform → team → user; each layer may only remove. An outer layer can already have narrowed what your profile allows."
            VisibleNow = "visible now"
            HiddenNow = "hidden now"
            NoCuratableModules =
                "This deployment registers no curatable modules. The SDK's own admin surfaces are deliberately absent from the governed set, so a profile can never hide the surface it is administered from."
            RegisteredModulesHeader = fun registered selected -> $"Registered modules ({registered}) — {selected} named"
            YourProfileHeading = "Your profile"
            YourProfileHelp =
                "Stored at your admin scope — the active team in team mode, your own scope otherwise. Modules this deployment does not register are ignored."
            AllowLabel = "Allow"
            AllowDescription = "Surface only the modules named below."
            DenyLabel = "Deny"
            DenyDescription = "Surface everything except the modules named below."
            NoteLabel = "Note (why this profile exists)"
            NotePlaceholder = "e.g. this deployment ships the finance family only"
            Working = "Working…"
            SaveProfile = "Save profile"
            ClearProfile = "Clear profile"
        }
        MigrationStatus = {
            NotYetRun = "Not yet run"
            InProgressLabel = "In progress"
            UpToDate = "Up to date"
            CompletedWithFailures = "Completed with failures"
            Blocked = "Blocked"
            NoPassRecorded = "No pass recorded yet."
            InProgressText =
                fun name targetVersion done' total -> $"Migrating {name} to V{targetVersion}: {done'}/{total} objects"
            CompleteText = fun total targetVersion -> $"{total} objects at V{targetVersion}"
            CompleteWithFailuresText =
                fun done' total targetVersion failed ->
                    $"{done'}/{total} objects at V{targetVersion}; {failed} left behind"
            Dismiss = "Dismiss"
            NoFailuresRecorded = "No failures recorded for the last pass."
            FailuresSummary =
                fun count ->
                    $"{count} most recent failure(s). Each object is still at its pre-migration version; fix the migrator and run again to retry only these."
            FailureLine = fun objectId atVersion error -> $"{objectId} (v{atVersion}) — {error}"
            DeclaredVersion = fun version -> $"V{version}"
            ChainIncomplete = "Migrator chain incomplete"
            Migrating = "Migrating…"
            MigrateNow = "Migrate now"
            HideFailures = "Hide failures"
            FailuresButton = fun count -> $"Failures ({count})"
            NoDataTypes =
                "No data types are registered, or data migrations are not enabled server-side (ServerConfig.DataMigrations)."
            ColumnDataType = "Data type"
            ColumnDeclared = "Declared"
            ColumnProgress = "Progress"
            ColumnState = "State"
            ColumnActions = "Actions"
            Refreshing = "Refreshing…"
            Refresh = "Refresh"
            Heading = "Data migrations"
            Subheading =
                "Each module declares the schema version it reads. Objects stored at an older version are upgraded forward through the module's migrators; a failed object stays at its old version and is retried on the next pass."
            LoadingDataTypes = "Loading data types..."
        }
        SessionSecurity = {
            Heading = "Session security"
            Subheading =
                "Devices currently signed in as you. Sign out any you do not recognise — a signed-out session stops working within the deployment's revocation window."
            Loading = "Loading…"
            EmptyState = "No sessions recorded yet. Sessions appear here after your next request."
            JustNow = "Just now"
            MinutesAgo = fun n -> $"{n} minutes ago"
            HoursAgo = fun n -> $"{n} hours ago"
            DaysAgo = fun n -> $"{n} days ago"
            DeviceLastSeen = fun provider lastSeen -> $"{provider} · last seen {lastSeen}"
            DeviceSignedOut = fun provider -> $"{provider} · signed out"
            Revoked = "Revoked"
            Confirm = "Confirm"
            Cancel = "Cancel"
            SignOut = "Sign out"
            SignOutEverywhere = "Sign out everywhere"
            SignOutEverywhereWarning =
                "This signs out every device, including this one. You will need to sign in again to continue."
            RevokeSuccess = "Session signed out."
            RevokeAllResult =
                fun count ->
                    match count with
                    | 0 -> "No active sessions to sign out."
                    | 1 -> "1 session signed out."
                    | n -> $"{n} sessions signed out."
            Dismiss = "Dismiss"
        }
        Home = {
            Heading = "Home"
            Subheading = "An overview of your tools, the data in each, and the active AI."
            Refresh = "Refresh"
            Loading = "Loading…"
            NoDataYet = "No data yet — open this tool to add some."
            ActiveAiHeading = "Active AI"
            NoAiProvider = "No AI provider configured."
            Mode = fun m -> $"Mode: {m}"
            Health = fun h -> $"Health: {h}"
            YourTools = "Your tools"
            NoTools = "No data-producing tools are registered in this deployment yet."
            PinnedAndRecent = "Pinned & recent"
            Pin = "Pin"
            Unpin = "Unpin"
        }
        InviteAccept = {
            NoToken = "No invitation token in URL."
            SignInHeading = "Sign in to accept"
            SignInBody =
                "You'll need to sign in before you can join the team. Sign in at the home page, then re-open this invitation link."
            GoToSignIn = "Go to sign in"
            Joining = "Joining the team…"
            WelcomeHeading = fun teamName -> $"Welcome to {teamName}"
            JoinedAs = fun roleName -> $"You've joined as {roleName}."
            ContinueToApp = "Continue to the app"
            FailedHeading = "Could not accept invitation"
            GoToHome = "Go to home"
            NetworkError = fun detail -> $"Network error: {detail}"
        }
        PublicUtilityWidgets = {
            Heading = "Public utility"
            Fetch = {
                ParseError = fun msg -> $"Could not parse response: {msg}"
                AccessDenied = "Access denied — platform-admin role required."
                RequestFailed = fun code -> $"Request failed (HTTP {code})"
                NetworkError = fun msg -> $"Network error: {msg}"
            }
            Traffic = {
                Title = "Traffic"
                Subtitle = "Request volume + error-rate + latency per route"
                Stub =
                    "Traffic counters require the server-side /api/_platform/admin/traffic surface. Widget renders when that endpoint lands."
            }
            RateLimits = {
                Title = "Rate-limit events"
                Subtitle = "Recent decisions by key + route (newest first)"
                KeyIp = fun ip -> $"ip:{ip}"
                KeyUser = fun uid -> $"user:{uid}"
                KeyComposite = fun c -> $"composite:{c}"
                WindowPerSecond = "1s"
                WindowPerMinute = "1m"
                WindowPerHour = "1h"
                WindowPerDay = "1d"
                WindowSliding = fun duration buckets -> $"sliding {duration}/{buckets}"
                DecisionAllow = fun remaining -> $"Allow (rem {remaining})"
                DecisionDeny = "Deny"
                ColumnOccurred = "Occurred"
                ColumnKey = "Key"
                ColumnRoute = "Route"
                ColumnWindow = "Window"
                ColumnThreshold = "Threshold"
                ColumnDecision = "Decision"
                Refresh = "Refresh"
                Refreshing = "Refreshing..."
                ExportCsv = "Export CSV"
                Loading = "Loading..."
                EmptyState = "Rate-limiting not configured for this deployment, or no decisions recorded yet."
            }
            AdUnits = {
                Title = "Ad units"
                Subtitle = "AdSense slot configuration"
                DisabledStub = "AdPanel is disabled (ClientConfig.AdPanel = NoAdPanel) — no ad units to configure."
                Loading = "Loading..."
                EmptyState = "No ad units configured yet. Use the form below to create one."
                ColumnSlotId = "Slot id"
                ColumnAdClientId = "Ad-client id"
                ColumnFormat = "Format"
                ColumnStyle = "Style"
                ColumnActions = "Actions"
                Edit = "Edit"
                Delete = "Delete"
                EditSlotHeading = fun slotId -> $"Edit slot {slotId}"
                CreateSlotHeading = "Create slot"
                Cancel = "Cancel"
                SlotIdLabel = "Slot id"
                SlotIdPlaceholder = "1234567890"
                AdClientIdLabel = "Ad-client id"
                AdClientIdPlaceholder = "ca-pub-..."
                FormatLabel = "Format"
                StyleCssLabel = "Style CSS (optional)"
                StyleCssPlaceholder = "display:block; width:300px; height:250px;"
                Saving = "Saving..."
                Update = "Update"
                Create = "Create"
                Refresh = "Refresh"
                SlotIdRequired = "Slot id is required."
                SaveFailed = fun reason -> $"Save failed: {reason}"
                DeleteFailed = fun reason -> $"Delete failed: {reason}"
                EmptyResponseReason = fun code -> $"HTTP {code}"
            }
            PremiumUsers = {
                Title = "Premium users"
                Subtitle = "Operator-granted premium claims"
                ColumnUserId = "User id"
                ColumnGrantedAt = "Granted at"
                ColumnGrantedBy = "Granted by"
                ColumnReason = "Reason"
                Refresh = "Refresh"
                Refreshing = "Refreshing..."
                Loading = "Loading..."
                EmptyState =
                    "No premium users granted yet. Grant via POST /api/_platform/users/{userId}/premium (Phase 62)."
            }
        }
        FileManager = {
            FileReadError = fun fileName -> $"Couldn't read '{fileName}' — the file may be unreadable."
            DeleteFailed = fun msg -> $"Delete failed: {msg}"
            ReprocessFailed = fun msg -> $"Reprocess failed: {msg}"
            ResetFailed = fun msg -> $"Reset failed: {msg}"
            RetryFailed = fun msg -> $"Re-ingestion failed: {msg}"
            SizeBytes = fun bytes -> $"{bytes} B"
            SizeKilobytes = fun value -> $"{value} KB"
            SizeMegabytes = fun value -> $"{value} MB"
            ProcessingErrorsHeading = "Processing Errors"
            ProcessingErrorLine = fun fileName error -> $"{fileName}: {error}"
            IndexedLabel = "Indexed"
            IndexedTooltip = "Indexed — searchable from the knowledge base."
            IndexingLabel = "Indexing…"
            IndexingTooltip = "Vectorisation in progress — not yet searchable."
            NotIndexedLabel = "Not indexed"
            FilterAll = "All"
            FilterIndexed = "Indexed"
            FilterIndexing = "Indexing"
            FilterNotIndexed = "Not indexed"
            FilterNotAttempted = "Not attempted"
            FilterByStatusLabel = "Filter by index status:"
            UploadPanelTitle = "Data Upload"
            UploadSectionTitle = "Upload Files"
            ChooseFilesButton = "CHOOSE FILES"
            UploadHint = "Select CSV files to upload — file types are detected automatically"
            UploadedFilesPanelTitle = "Uploaded Files"
            NoFilesUploaded = "No files uploaded yet."
            NoFilesMatchFilter = "No files match the selected index-status filter."
            ColumnDataType = "Data Type"
            ColumnFileName = "File Name"
            ColumnUploaded = "Uploaded"
            ColumnRows = "Rows"
            ColumnSize = "Size"
            ColumnSearchIndex = "Search index"
            RetryTooltip = "Re-run vectorisation for this file's persisted bytes."
            RetryButton = "Retry"
            ReprocessTooltip =
                "Re-run processing on this file's persisted bytes. Use this when the file's processed summary is missing or shows a stale-DataType error after a deploy."
            ReprocessButton = "Reprocess"
            DeleteTooltip =
                "Delete this file. The processed data is removed from this scope and the underlying blob is purged."
            DeleteButton = "Delete"
            ConfirmDelete =
                fun fileName ->
                    $"Delete {fileName}? This removes the file from this scope and any analyses that depend on it will lose access."
            ResetHelp =
                "Reset removes every uploaded file and its derived data from this scope. Owner / Admin only on team deployments."
            ResetTooltip =
                "Wipe every file, processed-data summary, and entry sidecar in this scope. This cannot be undone."
            ResetButton = "Reset data store"
            ConfirmReset =
                fun n ->
                    if n = 1 then
                        "Reset the data store? This permanently deletes 1 file and every derived summary in this scope. Analyses depending on this data will lose access. This cannot be undone."
                    else
                        $"Reset the data store? This permanently deletes {n} files and every derived summary in this scope. Analyses depending on this data will lose access. This cannot be undone."
        }
        ServiceStatusBoard = {
            Heading = "Service status"
            Subheading =
                "Composite snapshot of every operator-facing observability surface. Refresh re-runs every section in parallel; per-section refresh re-runs that section alone."
            Refresh = "Refresh"
            RefreshAll = "Refresh all"
            Refreshing = "Refreshing..."
            Loading = "Loading..."
            AllSystemsOk = "All systems Ok"
            DegradedBy = fun joined -> $"Degraded — {joined}"
            UnhealthyBy = fun joined -> $"Unhealthy — {joined}"
            SeverityOk = "Ok"
            SeverityWarn = "Warn"
            SeverityError = "Error"
            SeverityDisabled = "Disabled"
            GeneratedAt = fun formatted -> $"Generated at {formatted}"
            UnknownSectionHeadline = fun name -> $"Unknown section: {name}"
            SectionMappingIncomplete = "Client-side section mapping is incomplete."
            SectionRefreshFailed = fun section -> $"{section} refresh failed."
            UnknownSectionMessage = fun other -> $"unknown section: {other}"
        }
        UsageDashboard = {
            Heading = "Usage"
            Subheading =
                "Per-team consumption — AI tokens, storage bytes, ingestion rows, request counts. Owner / Admin only."
            GroupByLabel = "Group by"
            Grouping = {
                ByDay = "By day"
                ByMonth = "By month"
                ByResourceKind = "By resource kind"
                ByUser = "By user"
            }
            Refresh = "Refresh"
            Exporting = "Exporting…"
            ExportCsv = "Export CSV"
            ColumnBucket = "Bucket"
            ColumnQuantity = "Quantity"
            NoRecords =
                "No usage records for this scope. Records appear after the first metered AI call, file upload, or ingestion run."
            ClickRefresh = "Click Refresh."
            Loading = "Loading…"
        }
        MappingDataManager = {
            ReingestionFailed = fun msg -> $"Re-ingestion failed: {msg}"
            FileReadFailed = fun name -> $"Couldn't read '{name}' — the file may be unreadable."
            NoSchemaCannotMap = "The selected data type publishes no schema, so it can't be mapped."
            NoSchemaPublished = "The selected data type publishes no schema."
            ReprocessFailed = fun msg -> $"Reprocess failed: {msg}"
            ResetFailed = fun msg -> $"Reset failed: {msg}"
            DerivedRemediationStep = fun desc -> $"derived {desc}"
            UnitBytes = "B"
            UnitKilobytes = "KB"
            UnitMegabytes = "MB"
            UnrecognisedLabel = "Unrecognised"
            TypeText = "Text"
            TypeNumber = "Number"
            TypeDate = "Date"
            TypeBoolean = "Boolean"
            MatchConfident = "OK"
            MatchLowConfidence = "Low confidence"
            MatchTypeMismatch = "Type mismatch"
            MatchAmbiguous = "Ambiguous"
            MatchUnmatched = "Unmatched"
            NotMappedOption = "— not mapped —"
            TargetField = "Target field"
            ColumnType = "Type"
            ColumnCsvColumn = "CSV column"
            ColumnMatch = "Match"
            RequiredTooltip = "Required"
            DerivedBadge = "Derived"
            DateOrderDayFirst = "Day first (DD/MM)"
            DateOrderMonthFirst = "Month first (MM/DD)"
            DateOrderYearFirst = "ISO (YYYY-MM-DD)"
            ReviewDataIntro =
                "We scanned the data for problems before mapping. Safe fixes are pre-selected; ambiguous dates need a choice."
            ApplyFixes = "Apply fixes"
            ExampleValues = fun examples -> $"e.g. {examples}"
            UnitKeptInLabel = fun u -> $"unit {u} → kept in label"
            PreviewBeforeAfter = fun raw after -> $"preview: \"{raw}\" → \"{after}\""
            ChooseDateOrderFor = fun cols -> $"Choose a date order for: {cols}"
            ContinueToMapping = "Continue to mapping"
            AutoMappedWarningHeading = "⚠ Auto-mapped — please double-check these fields"
            GuessedColumn = fun c -> $"→ guessed \"{c}\""
            NoColumnFound = "→ no column found"
            DerivedKindConcat = "Join columns"
            DerivedKindSplitTake = "Split & take part"
            DerivedKindSubstring = "Substring"
            DerivedKindConstant = "Constant value"
            AddDerivedColumnHeading = "Add a derived column"
            FieldPlaceholder = "— field —"
            DerivedFromLabel = "From"
            ValueLabel = "Value"
            ColumnALabel = "Column A"
            ColumnBLabel = "Column B"
            ColumnPlaceholder = "— column —"
            SeparatorLabel = "Separator"
            ColumnLabel = "Column"
            DelimiterLabel = "Delimiter"
            PartNumberLabel = "Part #"
            StartLabel = "Start"
            LengthLabel = "Length"
            AddButton = "Add"
            DerivedColumnsFootnote =
                "Derived columns draw from source columns only and re-derive automatically on re-import."
            RemoveButton = "Remove"
            AllRowsValidatedCleanly =
                fun total -> $"""All {total} row{if total = 1 then "" else "s"} validated cleanly."""
            RowsFailBlocked =
                fun failed total ->
                    $"""{failed} of {total} row{if total = 1 then "" else "s"} would fail — commit is blocked until the mapping or source is fixed."""
            RowsFailWarn =
                fun failed total ->
                    $"""{failed} of {total} row{if total = 1 then "" else "s"} would fail. You can fix the mapping or import anyway."""
            FailingCellsHeading =
                fun column count -> $"""{column} — {count} failing cell{if count = 1 then "" else "s"}"""
            ExpectedValue = fun expected -> $"expected {expected}"
            RowIssueDetail = fun row actual reason -> $"row {row}: \"{actual}\" — {reason}"
            TruncatedCellsNote = "Showing a sample of the failing cells — more exist than are listed here."
            Importing = "Importing…"
            ImportButton = "Import"
            BackToMapping = "Back to mapping"
            MapFileNameHeading = fun name -> $"Map: {name}"
            CancelButton = "Cancel"
            DetectedColumnsPrompt =
                fun count -> $"Detected {count} columns. Choose the data format to map this CSV into:"
            NoSchemaTypesRegistered = "No schema-bearing data types are registered in this deployment."
            MappingToLabel = fun label -> $"Mapping to: {label}"
            ChangeFormatButton = "Change format"
            ReusedSavedMappingNote = "Reused a saved mapping for this column structure. Review before confirming."
            RequiredFieldsUnmapped =
                fun count names -> $"""Required field{if count = 1 then "" else "s"} still unmapped: {names}"""
            DerivedColumnError = fun field detail -> $"Derived column {field} {detail}"
            Validating = "Validating…"
            ConfirmAndValidateButton = "Confirm & validate"
            ValidateEveryRowNote = "We check every row against the format before importing."
            ColumnMappingPanelTitle = "Column Mapping"
            IndexedTooltip = "Indexed — searchable from the knowledge base."
            IndexedBadge = "Indexed"
            IndexingTooltip = "Vectorisation in progress — not yet searchable."
            IndexingBadge = "Indexing…"
            NotIndexedBadge = "Not indexed"
            FilterAll = "All"
            FilterIndexing = "Indexing"
            FilterNotAttempted = "Not attempted"
            FilterByIndexStatus = "Filter by index status:"
            NoFilesImportedYet = "No files imported yet."
            NoFilesMatchFilter = "No files match the selected index-status filter."
            ColumnDataType = "Data Type"
            ColumnFileName = "File Name"
            ColumnRows = "Rows"
            ColumnSize = "Size"
            ColumnSearchIndex = "Search index"
            NoRemediationLabel = "no remediation"
            ConvertedFromTooltip = fun source steps -> $"Converted from {source} — {steps}"
            ConvertedBadge = "Converted"
            RetryIngestionTooltip = "Re-run vectorisation for this file's persisted bytes."
            RetryButton = "Retry"
            NewMappingTooltip =
                "Map this file's columns to a known format to produce a data object. Available for every file — map an unrecognised file for the first time, or spawn an additional data object from an already-mapped one."
            NewMappingButton = "New Mapping"
            ReprocessTooltip =
                "Re-run processing on this file's persisted bytes. Use this when the file's processed summary is missing or shows a stale-DataType error after a deploy."
            ReprocessButton = "Reprocess"
            DeleteButton = "Delete"
            DeleteFileConfirm =
                fun fileName ->
                    $"Delete {fileName}? Any data objects mapped from it are removed too, and analyses depending on them will lose access."
            ResetScopeNote =
                "Reset removes every imported file and its derived data from this scope. Owner / Admin only on team deployments."
            ResetDataStoreTooltip =
                "Wipe every file, processed-data summary, and entry sidecar in this scope. This cannot be undone."
            ResetDataStoreButton = "Reset data store"
            ResetConfirm =
                fun count ->
                    $"""Reset the data store? This permanently deletes {count} file{if count = 1 then "" else "s"} and every derived summary in this scope. This cannot be undone."""
            ImportCsvPanelTitle = "Import CSV"
            UploadFileSectionTitle = "Upload a file"
            ChooseCsvButton = "CHOOSE CSV"
            CheckingKnownStructure = "Checking for a known structure…"
            UploadHelpText =
                "Upload one or more CSVs. Known structures re-import automatically; unrecognised ones land below — use New Mapping to map them."
            ImportedFilesPanelTitle = "Imported Files"
        }
    }

    /// The built-in catalog re-stamped for `locale`. This is what a
    /// `MessageCatalogOverride` is handed, and the `Locale` field is how
    /// ONE override function serves several languages: it matches on
    /// `catalog.Locale`, returns its translation for a language it
    /// covers, and returns the argument unchanged for one it does not —
    /// which is exactly the fallback chain back to English, expressed as
    /// the identity function rather than as a per-field lookup.
    let forLocale (locale: string) : MessageCatalog =
        if System.String.IsNullOrWhiteSpace locale then
            english
        else
            { english with Locale = locale.Trim() }

    /// Apply the deployment's override, if any, over the built-in
    /// catalog stamped with `locale`. An override that raises is
    /// swallowed back to the English catalog: a translation bug must not
    /// be able to take the shell down, and a shell rendering English is a
    /// visibly-degraded state a deployment can diagnose, whereas a blank
    /// page is not.
    let resolve (locale: string) (over: (MessageCatalog -> MessageCatalog) option) : MessageCatalog =
        let baseCatalog = forLocale locale

        match over with
        | None -> baseCatalog
        | Some f ->
            try
                f baseCatalog
            with _ ->
                baseCatalog

/// Shell-locale switch request hook.
///
/// Mirrors `NavigationRequest` exactly — a sanctioned mutable global
/// with subscribe/publish, so a module (a settings page, a language
/// picker in a consumer's own chrome) can ask the shell to change locale
/// without taking a dependency on the shell's internal `Msg` type, and
/// without `ClientModuleContext` growing a field every consumer's
/// full-literal construction would have to be re-written for.
///
/// The shell subscribes once at boot and translates each request into
/// the same reset path `TeamSwitched` takes: every module's state is
/// cleared and re-initialised, so a module that cached a formatted
/// string at `Init` is rebuilt rather than left showing the previous
/// language.
module LocaleRequest =

    open System.Collections.Generic

    let private listeners = List<string -> unit>()

    // Same `gate` discipline, and the same reason, as
    // `NavigationRequest`: free in the browser (Fable compiles `lock` to
    // a plain call of the body), and required on .NET where Expecto's
    // parallel runner can subscribe on one thread while another
    // enumerates.
    let private gate = obj ()

    /// Shell-side subscription. Returns a dispose thunk; the shell never
    /// disposes (the subscription lives for the page's lifetime).
    let subscribe (callback: string -> unit) : unit -> unit =
        lock gate (fun () -> listeners.Add(callback))
        fun () -> lock gate (fun () -> listeners.Remove(callback) |> ignore)

    /// Ask the shell to switch to `locale` (a BCP 47 tag). No-op when
    /// nothing is subscribed — the case in any harness that has not
    /// mounted the shell. Fires against a snapshot so a callback that
    /// (un)subscribes during delivery cannot disturb the iteration.
    let request (locale: string) : unit =
        let snapshot = lock gate (fun () -> listeners.ToArray())

        for cb in snapshot do
            try
                cb locale
            with _ ->
                ()