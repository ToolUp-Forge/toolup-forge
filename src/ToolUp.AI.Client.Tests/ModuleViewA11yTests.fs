// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.ModuleViewA11yTests

// ─── The Phase 180 a11y floor over an ordinary MODULE view ────────────
//
// Phase 180 shipped `ModuleHarness.AssertAccessible`, and Phase 610 built
// a DOM harness that mounts a real Feliz view and runs those rules over
// the markup — but only the SHELL's sidebar rail was ever put through it.
// No ordinary module view was, and `AssertAccessible` still asked the
// caller to hand-supply a render function: for a Fable module the only
// tree it HAS is a `ReactElement`, so "supply a render" meant hand-writing
// a second description of the view, which then drifts from the view
// silently and in the direction of passing.
//
// This pack closes that. It drives a real SDK module —
// `UsageDashboard`, a `ClientModule` the shell injects into every
// non-Anonymous deployment — through its real `init` / `update`, and at
// each model state runs its real `view` through the Phase 180 rules with
// ONE line and no hand-written tree:
//
//     fromUnitInit UsageDashboard.init UsageDashboard.update
//         |> _.Dispatch(UsageDashboard.AggregateLoaded(Ok rows))
//         |> _.AssertAccessibleView(ViewMount.mount, usageView)
//
// The dashboard's view mounts its tree through a deferred surface, which a
// synchronous mount cannot wait for, so `usageView` draws the tree through
// the renderer's component directly, as the tree's own file draws it.
//
// ── Where the seam is, and why jsdom is not in the shipped package ──
// `ModuleHarness.AssertAccessibleView` takes a `mount: 'View -> string`.
// It names no renderer, no DOM and no npm package — `ToolUp.Platform.
// Testing` stays BCL-only. The jsdom implementation ships beside it as
// `Testing/ViewMount.fs`: `Content`-packed under `fable/` like every
// other file in that project, but deliberately NOT in its `<Compile>`
// list, so it is absent from the compiled assembly. A consumer opts in
// with one `<Compile Include>` (exactly as this project already does for
// `AccessibilityAssertions.fs`) plus a `jsdom` devDependency; a consumer
// that does not emits nothing from it and needs neither (GP 13). An SSR
// consumer passes its own string renderer and never touches a DOM.
//
// ── Why here and not in the .NET pack ──
// The same constraint every Fable-tier pack in this project records, and
// one more on top: `UsageDashboard` holds a module-level `Api.makeProxy`
// whose reflection-shaped builder raises under .NET reflection at
// static-init time, so `update` cannot even be CALLED there — and
// rendering React needs a JS runtime regardless.
//
// ── What it does NOT prove ──
// CSS, focus rings, contrast, the browser's real accessibility tree. And
// a state is covered only if it is in `viewStates` below.

open Fuaran.MVU
open Feliz
open ToolUp.Platform
open ToolUp.Platform.Testing
open ToolUp.Platform.Testing.ModuleHarness
open ToolUp.Platform.Usage
open ToolUp.AI.Client.Tests.NodeTest

// ─── The module's states ─────────────────────────────────────────────

let private sampleRows: UsageAggregateRow list = [
    {
        Bucket = "ai.tokens"
        Quantity = 128500M
    }
    {
        Bucket = "storage.bytes"
        Quantity = 4294967296M
    }
    {
        Bucket = "ingestion.rows"
        Quantity = 0M
    }
]

/// One state of the dashboard: how the harness got there, and the
/// accessible names that must be reachable once it has.
///
/// `MustName` is per-state rather than "every control the view can ever
/// draw" for the same reason Phase 610's is: which controls exist is a
/// property of the STATE (the export button renames itself mid-flight,
/// the table is absent until rows arrive), and writing it out is what
/// makes a silently-vanished control a failure rather than a pass. A rule
/// reports what is present and unnamed; it never reports what is absent.
type private ViewState = {
    Name: string
    Harness: ModuleHarness<UsageDashboard.Model, UsageDashboard.Msg>
    MustName: string list
}

/// The module's own `init` — including the steps it returns, which the
/// harness captures and does not run. That is what makes the initial
/// state honest: the real dashboard IS in `Loading` while its first
/// `Aggregate` call is in flight.
let private initial () =
    fromUnitInit UsageDashboard.init UsageDashboard.update

/// Every state driven through the module's REAL `update`. Nothing here
/// constructs a `Model` literally — a hand-built model is a state the
/// module might never reach, and the point of running the floor through
/// the harness is that every state it checks is one a user can get to.
/// The dashboard's page under the English catalog: the tree its view mounts.
let private usageView (model: UsageDashboard.Model) (dispatch: UsageDashboard.Msg -> unit) : ReactElement =
    TreeViewParityTests.usageTree model dispatch

let private viewStates: ViewState list = [
    {
        Name = "initial aggregate load in flight"
        Harness = initial ()
        MustName = [ "Refresh"; "Export CSV" ]
    }

    {
        Name = "aggregate rows loaded"
        Harness = (initial ()).Dispatch(UsageDashboard.AggregateLoaded(Ok sampleRows))
        MustName = [ "Refresh"; "Export CSV" ]
    }

    {
        // The empty-state branch — a different render arm, and the one a
        // brand-new team sees first.
        Name = "no usage records for this scope"
        Harness = (initial ()).Dispatch(UsageDashboard.AggregateLoaded(Ok []))
        MustName = [ "Refresh"; "Export CSV" ]
    }

    {
        Name = "the aggregate query failed"
        Harness = (initial ()).Dispatch(UsageDashboard.AggregateLoaded(Error "the usage service is unreachable"))
        MustName = [ "Refresh"; "Export CSV" ]
    }

    {
        // The export button renames itself and goes `disabled` while the
        // CSV is being built. A disabled control is still announced, so it
        // still has to be named — and its name CHANGED, which is exactly
        // the kind of transient state a single-tree fixture never sees.
        Name = "a CSV export in flight"
        Harness =
            (initial ()).Dispatch(UsageDashboard.AggregateLoaded(Ok sampleRows)).Dispatch(UsageDashboard.ExportCsv)
        MustName = [ "Refresh"; "Exporting…" ]
    }

    {
        // Re-grouping drops back to `Loading` and re-issues the query;
        // asserted because it is the one transition a user drives from
        // inside the view rather than from the shell.
        Name = "regrouped by user, reload in flight"
        Harness = (initial ()).Dispatch(UsageDashboard.SetGrouping ByUser)
        MustName = [ "Refresh"; "Export CSV" ]
    }
]

// ─── Known, tracked gaps ─────────────────────────────────────────────

/// A finding that is REAL, is not this pack's to fix, and is pinned so it
/// cannot be mistaken for coverage — while a NEW finding still fails.
/// Same shape as Phase 610's, keyed off the offending element rather than
/// its document path so it survives unrelated markup moving around it.
type private KnownGap = {
    Rule: string
    /// Why the finding stands, and where the fix belongs.
    Why: string
    Matches: Accessibility.ElementRef -> bool
}

/// The floor found a real defect on its first ordinary module view, which
/// is the outcome that justifies wiring it: the dashboard's Feliz controls
/// drew its grouping `<select>` beside a `<label>` that neither wraps it
/// nor carries a `for`, so the control has no programmatic label at all —
/// a screen reader announces an unnamed combo box, and voice control has
/// nothing to say. The visible "Group by" text makes it look labelled to
/// everyone who can see it, which is why it survived to here.
///
/// Empty, and that is the point. This pack shipped with one pin —
/// `UsageDashboard`'s grouping `<select>`, labelled only by a sibling
/// `<label>` with no `for`, so an unnamed combo box in all six of the
/// module's states. It was the FIRST defect the module-view floor found on
/// the first ordinary module view it was pointed at, which is the argument
/// for the floor existing.
///
/// Fixed in the Feliz controls (the select carried its own `aria-label`,
/// bound to the same string as the visible label so the two could not
/// drift); the tree's select is wrapped by its label. The pin was deleted in that same commit — required, not
/// tidy-up: the `every pinned known gap still fires somewhere` case below
/// goes RED the moment a pinned finding stops firing, because an exemption
/// that no longer fires is one that has silently stopped checking a class
/// that is now clean.
///
/// The machinery stays for the next genuine not-mine-to-fix finding.
let private knownGaps: KnownGap list = []

let private classify (node: Accessibility.A11yNode) (findings: Accessibility.A11yFinding list) =
    findings
    |> List.partition (fun f ->
        match Accessibility.locate node f with
        | None -> false
        | Some e -> knownGaps |> List.exists (fun g -> g.Rule = f.Rule && g.Matches e))

let private inState (state: string) (finding: Accessibility.A11yFinding) : Accessibility.StateFinding = {
    State = state
    Finding = finding
}

// ─── A falsifier module — a view that can lose its name on demand ────

type private ToyModel = { Named: bool }

type private ToyMsg = StripTheName

let private toyInit () : ToyModel * Step<ToyMsg, RemoteEffect<ToyMsg>> list = { Named = true }, []

let private toyUpdate (_: ToyMsg) (model: ToyModel) : ToyModel * Step<ToyMsg, RemoteEffect<ToyMsg>> list =
    { model with Named = false }, []

/// An icon-only button — the exact shape Phase 609 found unnamed in the
/// shipped rail. With `Named = false` its `aria-label` is gone and there
/// is no text to fall back on, so its accessible name is nothing at all.
let private toyView (model: ToyModel) (_: ToyMsg -> unit) : ReactElement =
    let naming = if model.Named then [ prop.ariaLabel "Delete row" ] else []

    Html.div [
        prop.children [
            Html.button (naming @ [ prop.children [ Svg.svg [ svg.viewBox (0, 0, 24, 24) ] ] ])
        ]
    ]

let private expectThrows (body: unit -> unit) (message: string) : string =
    let caught =
        try
            body ()
            None
        with e ->
            Some e.Message

    match caught with
    | Some m -> m
    | None ->
        failwith (
            message
            + " — but it returned normally. A floor that cannot fail is the failure this whole line \
               of work exists to prevent."
        )

// ─── The three surfaces authored as trees ────────────────────────────
//
// The team manager, the module-visibility editor and the command palette
// render as Fuaran-UI trees. Each module's view mounts its tree through a
// deferred surface, which a synchronous mount cannot wait for, so the trees
// are drawn here through the renderer's component directly, as their own
// files draw them, and the same Strict floor holds them.

let private teamMsgs = MessageCatalog.english.TeamManager
let private visibilityMsgs = MessageCatalog.english.ModuleVisibilityAdmin

let private teamTree (model: TeamManagerUI.Model) (dispatch: TeamManagerUI.Msg -> unit) : ReactElement =
    TreeViewParityTests.lowerTree dispatch (TeamManagerTree.tree teamMsgs model dispatch)

let private on = System.DateTime(2026, 11, 26)

let private teamInit () =
    fromUnitInit (fun () -> TeamManagerUI.init ClientModuleContext.empty) TeamManagerUI.update

let private teamsLoaded () =
    (teamInit ())
        .Dispatch(
            TeamManagerUI.TeamsLoaded [
                {
                    TeamId = "t1"
                    Name = "Alpha"
                    CreatedAt = on
                    Archived = false
                }
                {
                    TeamId = "t2"
                    Name = "Beta"
                    CreatedAt = on
                    Archived = false
                }
            ]
        )
        .Dispatch(TeamManagerUI.ActiveTeamLoaded(Some "t1"))

let private details () =
    (teamsLoaded ()).Dispatch(TeamManagerUI.IsPlatformAdminLoaded true).Dispatch(TeamManagerUI.SelectTeam "t1")

let private invites () =
    (details ())
        .Dispatch(TeamManagerUI.NavigatePendingInvites "t1")
        .Dispatch(
            TeamManagerUI.PendingByEmailLoaded(
                "t1",
                [
                    "a@example.com",
                    {
                        TeamId = "t1"
                        Role = Member
                        ExpiresAt = on
                        InviterUserId = "u-owner"
                        IssuedAt = on
                    }
                ]
            )
        )
        .Dispatch(
            TeamManagerUI.ExpiredInvitesLoaded(
                "t1",
                [
                    {
                        TeamId = "t1"
                        InviteeEmail = "b@example.com"
                        InviterUserId = "u-owner"
                        Role = Admin
                        IssuedAt = on
                        ExpiredAt = on
                    }
                ]
            )
        )

let private teamStates () = [
    "no teams yet", (teamInit ()).Dispatch(TeamManagerUI.TeamsLoaded [])
    "two teams, one active", teamsLoaded ()
    "an error over the team list", (teamsLoaded ()).Dispatch(TeamManagerUI.ApiError "unreachable")
    "a team's details, managed by a platform admin", details ()
    "pending and expired invites", invites ()
    "inviting by email", (invites ()).Dispatch(TeamManagerUI.OpenIssueByEmail "t1")
    "revoking an invite", (invites ()).Dispatch(TeamManagerUI.OpenRevokeByEmail("t1", "a@example.com"))
]

let private paletteProps: Components.CommandPalette.CommandPaletteProps = {
    IsOpen = true
    Query = ""
    Highlight = 0
    Entries = []
    OnOpen = ignore
    OnDismiss = ignore
    OnQueryChanged = ignore
    OnHighlightMoved = ignore
    OnSelect = ignore
}

let private treeSurfaceTests =
    testList "the surfaces as trees" [

        testCase "every team manager state passes the Strict floor as a tree" (fun () ->
            for name, harness in teamStates () do
                try
                    harness.AssertAccessibleView(ViewMount.mount, teamTree, Accessibility.Strict)
                    |> ignore
                with e ->
                    failwithf "state \"%s\": %s" name e.Message)

        testCase "every module-visibility state passes the Strict floor as a tree" (fun () ->
            for harness in
                [
                    TreeViewParityTests.visibilityInit ()
                    TreeViewParityTests.visibilityLoaded ()
                ] do
                harness.AssertAccessibleView(ViewMount.mount, TreeViewParityTests.visibilityTree, Accessibility.Strict)
                |> ignore)

        testCase "every Platform Management state passes the Strict floor as a tree" (fun () ->
            TreeViewParityTests.strictInEvery (TreeViewParityTests.adminStates ()) TreeViewParityTests.adminTree)

        testCase "every session-security state passes the Strict floor as a tree" (fun () ->
            TreeViewParityTests.strictInEvery (TreeViewParityTests.sessionStates ()) TreeViewParityTests.sessionTree)

        testCase "every permissions-admin state passes the Strict floor as a tree" (fun () ->
            TreeViewParityTests.strictInEvery
                (TreeViewParityTests.permissionsStates ())
                TreeViewParityTests.permissionsTree)

        testCase "every platform-users state passes the Strict floor as a tree" (fun () ->
            TreeViewParityTests.strictInEvery (TreeViewParityTests.usersStates ()) TreeViewParityTests.usersTree)

        testCase "every audit-trail state passes the Strict floor as a tree" (fun () ->
            TreeViewParityTests.strictInEvery (TreeViewParityTests.auditStates ()) TreeViewParityTests.auditTree)

        testCase "every data-subject-request state passes the Strict floor as a tree" (fun () ->
            TreeViewParityTests.strictInEvery (TreeViewParityTests.dsrStates ()) TreeViewParityTests.dsrTree)

        testCase "every external-contact state passes the Strict floor as a tree" (fun () ->
            TreeViewParityTests.strictInEvery (TreeViewParityTests.contactsStates ()) TreeViewParityTests.contactsTree)

        testCase "every health-monitor state passes the Strict floor as a tree" (fun () ->
            TreeViewParityTests.strictInEvery (TreeViewParityTests.healthStates ()) TreeViewParityTests.healthTree)

        testCase "every background-jobs state passes the Strict floor as a tree" (fun () ->
            TreeViewParityTests.strictInEvery (TreeViewParityTests.jobsStates ()) TreeViewParityTests.jobsTree)

        testCase "every service-status state passes the Strict floor as a tree" (fun () ->
            TreeViewParityTests.strictInEvery (TreeViewParityTests.statusStates ()) TreeViewParityTests.statusTree)

        testCase "every data-migration state passes the Strict floor as a tree" (fun () ->
            TreeViewParityTests.strictInEvery
                (TreeViewParityTests.migrationStates ())
                TreeViewParityTests.migrationTree)

        testCase "every usage-dashboard state passes the Strict floor as a tree" (fun () ->
            TreeViewParityTests.strictInEvery (TreeViewParityTests.usageStates ()) TreeViewParityTests.usageTree)

        testCase "every no-active-team landing state passes the Strict floor as a tree" (fun () ->
            TreeViewParityTests.strictInEvery (TreeViewParityTests.landingStates ()) TreeViewParityTests.landingTree)

        testCase "every composition-inspector page passes the Strict floor as a tree in every state" (fun () ->
            for panel in TreeViewParityTests.inspectorPanels do
                TreeViewParityTests.strictInEvery
                    (TreeViewParityTests.inspectorStates panel)
                    (TreeViewParityTests.inspectorTree panel))

        testCase "the palette's tree, its overlay an island, passes the Strict floor" (fun () ->
            let html = ViewMount.mount (Components.CommandPaletteTree.Surface paletteProps)
            let node = Accessibility.ofHtml html
            Expect.isEmpty (Accessibility.check Accessibility.Strict node) "the open palette has no finding"

            Expect.isTrue
                (html.Contains MessageCatalog.english.CommandPalette.NoMatches)
                "and the island drew the overlay")

        testCase "a module id reads as itself through the Text node" (fun () ->
            let html =
                (TreeViewParityTests.visibilityLoaded ())
                    .RenderViewHtml(ViewMount.mount, TreeViewParityTests.visibilityTree)

            Expect.isTrue (html.Contains "_sdk.Team_Manager") "the id is not read as emphasis"
            Expect.isFalse (html.Contains "<em>") "and nothing in the page is parsed as markdown")

        testCase "a tree button and a kept row's button reach update" (fun () ->
            (teamsLoaded ())
                .Interact(ViewMount.mountAndInteract, teamTree, TreeViewParityTests.Drive.click teamMsgs.Manage)
                .AssertModelWith(
                    "the first team's Manage opens its details",
                    fun m -> m.CurrentView = TeamManagerUI.TeamDetails "t1"
                )
            |> ignore

            (TreeViewParityTests.visibilityLoaded ())
                .Interact(
                    ViewMount.mountAndInteract,
                    TreeViewParityTests.visibilityTree,
                    TreeViewParityTests.Drive.click visibilityMsgs.DenyLabel
                )
                .AssertModelWith(
                    "the Deny choice sets the rule's kind",
                    fun m -> m.Kind = ModuleVisibilityAdminUI.RuleKind.Deny
                )
            |> ignore)

        testCase "the modules' views mount their trees on demand, not at boot" (fun () ->
            let teamPage =
                match TeamManagerUI.builtIn.View with
                | BuiltInView.Page view ->
                    match view (teamsLoaded ()).Model (RenderContext.create MessageCatalog.english ignore) with
                    | PageContent.FullWidth element -> ViewMount.mount element
                    | other -> failwithf "a full-width page, not %A" other
                | BuiltInView.Pages _ -> failwith "one page"

            Expect.isTrue
                (teamPage.Contains "role=\"status\"" && teamPage.Contains "aria-busy=\"true\"")
                "the team manager shows the loading state until its tree arrives"

            Expect.isFalse (teamPage.Contains teamMsgs.MyTeamsPanel) "and draws nothing of the tree synchronously"

            let adminPage =
                match (PlatformAdminUI.builtIn PublicUtilityConfig.defaults).View with
                | BuiltInView.Page view ->
                    match
                        view
                            (TreeViewParityTests.adminInit ()).Model
                            (RenderContext.create MessageCatalog.english ignore)
                    with
                    | PageContent.FullWidth element -> ViewMount.mount element
                    | other -> failwithf "a full-width page, not %A" other
                | BuiltInView.Pages _ -> failwith "one page"

            Expect.isTrue
                (adminPage.Contains "role=\"status\"" && adminPage.Contains "aria-busy=\"true\"")
                "Platform Management shows the loading state until its tree arrives"

            Expect.isFalse
                (adminPage.Contains MessageCatalog.english.PlatformAdmin.AssignHeading)
                "and draws nothing of the tree synchronously")

        testCase "the second batch's views mount their trees on demand, not at boot" (fun () ->
            let pageOf (view: BuiltInView<'Model, 'Msg>) (model: 'Model) =
                match view with
                | BuiltInView.Page view ->
                    match view model (RenderContext.create MessageCatalog.english ignore) with
                    | PageContent.FullWidth element -> ViewMount.mount element
                    | other -> failwithf "a full-width page, not %A" other
                | BuiltInView.Pages _ -> failwith "one page"

            let pages = [
                "session security",
                pageOf SessionSecurityUI.builtIn.View (TreeViewParityTests.sessionsLoaded ()).Model,
                MessageCatalog.english.SessionSecurity.Heading
                "the permissions admin",
                pageOf PermissionsAdminUI.builtIn.View (TreeViewParityTests.permissionsLoaded ()).Model,
                MessageCatalog.english.PermissionsAdmin.TeamDefaultsHeading
                "platform users",
                pageOf PlatformUsersUI.builtIn.View (TreeViewParityTests.usersLoaded ()).Model,
                MessageCatalog.english.PlatformUsers.Heading
            ]

            for name, page, heading in pages do
                Expect.isTrue
                    (page.Contains "role=\"status\"" && page.Contains "aria-busy=\"true\"")
                    (name + " shows the loading state until its tree arrives")

                Expect.isFalse (page.Contains heading) (name + " draws nothing of the tree synchronously"))

        testCase "the third batch's views mount their trees on demand, not at boot" (fun () ->
            let pageOf (view: BuiltInView<'Model, 'Msg>) (model: 'Model) =
                match view with
                | BuiltInView.Page view ->
                    match view model (RenderContext.create MessageCatalog.english ignore) with
                    | PageContent.FullWidth element -> ViewMount.mount element
                    | other -> failwithf "a full-width page, not %A" other
                | BuiltInView.Pages _ -> failwith "one page"

            let pages = [
                "the audit trail",
                pageOf AuditLogUI.builtIn.View (TreeViewParityTests.auditLoaded ()).Model,
                MessageCatalog.english.AuditLog.Heading
                "data subject requests",
                pageOf DataSubjectRequestAdminUI.builtIn.View (TreeViewParityTests.dsrPreviewed ()).Model,
                MessageCatalog.english.DataSubjectRequestAdmin.PreviewPanelTitle
                "external contacts",
                pageOf ExternalContactManagerUI.builtIn.View (TreeViewParityTests.contactsLoaded ()).Model,
                MessageCatalog.english.ExternalContact.Heading
            ]

            for name, page, heading in pages do
                Expect.isTrue
                    (page.Contains "role=\"status\"" && page.Contains "aria-busy=\"true\"")
                    (name + " shows the loading state until its tree arrives")

                Expect.isFalse (page.Contains heading) (name + " draws nothing of the tree synchronously"))

        testCase "the fourth batch's views mount their trees on demand, not at boot" (fun () ->
            let pageOf (view: BuiltInView<'Model, 'Msg>) (model: 'Model) =
                match view with
                | BuiltInView.Page view ->
                    match view model (RenderContext.create MessageCatalog.english ignore) with
                    | PageContent.FullWidth element -> ViewMount.mount element
                    | other -> failwithf "a full-width page, not %A" other
                | BuiltInView.Pages _ -> failwith "one page"

            let pages = [
                "the health monitor",
                pageOf HealthMonitorUI.builtIn.View (TreeViewParityTests.healthLoaded ()).Model,
                MessageCatalog.english.HealthMonitor.ProbesFootnote
                "background jobs",
                pageOf JobsAdminUI.builtIn.View (TreeViewParityTests.jobsLoaded ()).Model,
                MessageCatalog.english.JobsAdmin.Subheading
                "the service status board",
                pageOf ServiceStatusBoardUI.builtIn.View (TreeViewParityTests.statusLoaded ()).Model,
                MessageCatalog.english.ServiceStatusBoard.Subheading
                "data migrations",
                pageOf MigrationStatusUI.builtIn.View (TreeViewParityTests.migrationLoaded ()).Model,
                MessageCatalog.english.MigrationStatus.Subheading
            ]

            for name, page, text in pages do
                Expect.isTrue
                    (page.Contains "role=\"status\"" && page.Contains "aria-busy=\"true\"")
                    (name + " shows the loading state until its tree arrives")

                Expect.isFalse (page.Contains text) (name + " draws nothing of the tree synchronously"))

        testCase "the fifth batch's views mount their trees on demand, not at boot" (fun () ->
            let render (view: BuiltInView<'Model, 'Msg>) (model: 'Model) =
                let ctx = RenderContext.create MessageCatalog.english ignore

                let content =
                    match view with
                    | BuiltInView.Page view -> [ view model ctx ]
                    | BuiltInView.Pages pages -> [ for _, view in pages -> view model ctx ]

                [
                    for page in content ->
                        match page with
                        | PageContent.FullWidth element -> ViewMount.mount element
                        | other -> failwithf "a full-width page, not %A" other
                ]

            let landing: NoActiveTeamLandingConfig = {
                Label = "Welcome"
                Title = "You're not on a team yet"
                Body = "Ask a team owner to invite you."
                Icon = None
            }

            let pages = [
                "the usage dashboard",
                render UsageDashboard.builtIn.View (TreeViewParityTests.usageLoaded ()).Model,
                MessageCatalog.english.UsageDashboard.Subheading
                "the no-active-team landing",
                render
                    (ToolUp.Platform.NoActiveTeamLandingUI.builtIn landing).View
                    (TreeViewParityTests.landingInit ()).Model,
                landing.Body
                "the composition inspector's five pages",
                render CompositionInspectorUI.builtIn.View (TreeViewParityTests.inspectorLoaded ()).Model,
                MessageCatalog.english.CompositionInspector.Subheading
            ]

            for name, rendered, text in pages do
                for page in rendered do
                    Expect.isTrue
                        (page.Contains "role=\"status\"" && page.Contains "aria-busy=\"true\"")
                        (name + " shows the loading state until its tree arrives")

                    Expect.isFalse (page.Contains text) (name + " draws nothing of the tree synchronously"))
    ]

// ─── Cases ───────────────────────────────────────────────────────────

let tests =
    testList "the Phase 180 a11y floor over a consumer module view" [

        // The floor itself, over every reachable state of a real module.
        testCase "every UsageDashboard state passes the Phase 180 Strict rule set" (fun () ->
            let failures =
                viewStates
                |> List.collect (fun st ->
                    let node =
                        st.Harness.RenderViewHtml(ViewMount.mount, usageView) |> Accessibility.ofHtml

                    let _, real = classify node (Accessibility.check Accessibility.Strict node)
                    real |> List.map (inState st.Name))

            if not (List.isEmpty failures) then
                failwith (
                    Accessibility.reportStates Accessibility.Strict failures
                    + "\n\nEach finding is a control the dashboard's tree renders in that state with \
                       no accessible name, no alt text, no label, or invalid ARIA. The rules are \
                       Phase 180's, unchanged; what this pack adds is running them over a MODULE's \
                       own view instead of a hand-built tree. Fix the control in \
                       `ToolUp.Platform.Client/Client/UsageDashboardTree.fs`."
                ))

        // The half a rule cannot do: a rule reports what is present and
        // unnamed, never what is absent, so a control that vanished from
        // the view is indistinguishable from a view with nothing wrong.
        testCase "every state's expected controls are reachable BY NAME" (fun () ->
            let missing = [
                for st in viewStates do
                    let node =
                        st.Harness.RenderViewHtml(ViewMount.mount, usageView) |> Accessibility.ofHtml

                    let names = Accessibility.interactiveNames node |> List.choose snd

                    for expected in st.MustName do
                        if not (List.contains expected names) then
                            yield st.Name, expected, names
            ]

            if not (List.isEmpty missing) then
                let detail = [
                    for stateName, expected, found in missing do
                        yield
                            sprintf
                                "  state \"%s\": no control is reachable as \"%s\" — the names actually exposed were [%s]"
                                stateName
                                expected
                                (found |> List.map (sprintf "\"%s\"") |> String.concat "; ")
                ]

                failwith (
                    sprintf "%d expected control(s) are not reachable by name:\n" missing.Length
                    + (detail |> String.concat "\n")
                    + "\n\nEither the control lost its accessible name, or it is no longer rendered \
                       in that state at all, or the expectation in `viewStates` is stale (fix it \
                       HERE, and say why in the same commit — this list is the contract)."
                ))

        // Non-vacuity. A mount that silently produced "" would leave every
        // case above passing, which is indistinguishable from a clean
        // module — and is the precise failure mode this line of work
        // exists to close.
        testCase "the mount really renders the module's markup" (fun () ->
            for st in viewStates do
                let html = st.Harness.RenderViewHtml(ViewMount.mount, usageView)

                Expect.isTrue
                    (html.Contains "<button")
                    ("state \""
                     + st.Name
                     + "\" rendered no <button> at all — the mount is not mounting")

                Expect.isTrue
                    (html.Contains "Per-team consumption")
                    ("state \""
                     + st.Name
                     + "\" is missing the view's own copy — the markup captured is \
                      not the dashboard tree's")

            // And the state-dependent arms really differ, so the states
            // are not six captures of one tree.
            let loaded =
                viewStates
                |> List.find (fun s -> s.Name = "aggregate rows loaded")
                |> fun s -> s.Harness.RenderViewHtml(ViewMount.mount, usageView)

            let empty =
                viewStates
                |> List.find (fun s -> s.Name = "no usage records for this scope")
                |> fun s -> s.Harness.RenderViewHtml(ViewMount.mount, usageView)

            Expect.isTrue (loaded.Contains "role=\"table\"") "the loaded state renders the aggregate table"
            Expect.isFalse (empty.Contains "role=\"table\"") "the empty state renders the empty copy, not a table")

        // The pins have to be load-bearing in both directions: a pinned
        // gap that has been FIXED is a stale pin, and a stale pin is how a
        // whole class quietly stops being checked.
        testCase "every pinned known gap still fires somewhere" (fun () ->
            for gap in knownGaps do
                let fires =
                    viewStates
                    |> List.exists (fun st ->
                        let node =
                            st.Harness.RenderViewHtml(ViewMount.mount, usageView) |> Accessibility.ofHtml

                        let pinned, _ = classify node (Accessibility.check Accessibility.Strict node)
                        not (List.isEmpty pinned))

                Expect.isTrue
                    fires
                    ("the known gap pinned for rule `"
                     + gap.Rule
                     + "` no longer fires in any UsageDashboard state. If it was fixed, DELETE the "
                     + "pin — leaving it in place exempts an element class that is now clean, and "
                     + "the next real instance of it would pass. The pin reads: "
                     + gap.Why))

        // The floor has to be able to FAIL, and through the harness path
        // rather than by calling the rules directly — what is under test
        // here is the WIRING (view → mount → parse → rules → throw), not
        // the Phase 180 rules, which have their own pack.
        testCase "AssertAccessibleView rejects a module view that lost a control's name" (fun () ->
            let named = fromUnitInit toyInit toyUpdate

            named.AssertAccessibleView(ViewMount.mount, toyView, Accessibility.Strict)
            |> ignore

            let stripped = named.Dispatch StripTheName

            let message =
                expectThrows
                    (fun () ->
                        stripped.AssertAccessibleView(ViewMount.mount, toyView, Accessibility.Strict)
                        |> ignore)
                    "removing the only accessible name from a module view's button must fail \
                     AssertAccessibleView"

            Expect.isTrue
                (message.Contains "everyInteractiveHasAccessibleName")
                ("the failure must NAME the rule that fired, so the reader knows what broke. Got: "
                 + message)

            Expect.isTrue
                (message.Contains "button")
                ("the failure must NAME the offending element, so the fix has a target. Got: "
                 + message))

        // `CheckAccessibleView` is the non-throwing half the pinning above
        // runs on; if it ever stopped agreeing with the throwing one, the
        // pins would be classifying a different finding set than the gate
        // fails on.
        testCase "CheckAccessibleView reports exactly what AssertAccessibleView fails on" (fun () ->
            let stripped = (fromUnitInit toyInit toyUpdate).Dispatch StripTheName

            let findings =
                stripped.CheckAccessibleView(ViewMount.mount, toyView, Accessibility.Strict)

            Expect.equal
                (findings |> List.map _.Rule)
                [ "everyInteractiveHasAccessibleName" ]
                "the unnamed icon-only button produces exactly the accessible-name finding — if this \
                 is empty the rules are inspecting nothing and every other case here is vacuous"

            Expect.isEmpty
                ((fromUnitInit toyInit toyUpdate).CheckAccessibleView(ViewMount.mount, toyView, Accessibility.Strict))
                "and the same view WITH its aria-label is clean, so the finding tracks the name and \
                 not the shape")

        treeSurfaceTests
    ]