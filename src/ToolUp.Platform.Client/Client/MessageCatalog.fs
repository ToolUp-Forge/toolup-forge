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