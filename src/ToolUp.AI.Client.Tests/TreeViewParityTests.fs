// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.TreeViewParityTests

// ─── The migration kit: what a view moving to a tree must prove ───────
//
// A view moved from Feliz to a Fuaran-UI tree proves three things, through
// `ModuleHarness`, the same way every time:
//
//   1. ACCESSIBLE — `AssertAccessibleView` over the lowered tree
//      (`ModuleHarness.lowered`), the same rules the Feliz view is held to;
//   2. BEHAVES — `Interact` drives the mounted tree as a user would (a
//      click, a field edit), runs what it dispatched through `update`, and
//      the assertion is on the `Model`, never on markup;
//   3. AT PARITY — `AssertViewParity`: for one model, the Feliz view and the
//      tree expose the same controls and headings by role and name, in the
//      same tab order.
//
// The subjects are the views that moved: each module's own tree against
// the Feliz page it replaced (`FelizReferenceViews`), for the same `Model`
// and `Msg`. After the subjects, the kit's own go-red proofs: each
// assertion fails, by name, on the defect it exists for.
//
// The tree is drawn through the renderer's component directly
// (`lowerTree`), the way a deferred surface's own file draws it: the
// module's `TreeLowering.lower` puts the same component behind a dynamic
// import, which a synchronous mount cannot wait for, and which has tests of
// its own.

open Fuaran.MVU
open Fable.Core
open Fable.Core.JsInterop
open Fuaran.UI
open Fuaran.UI.Types
open ToolUp.Platform
open ToolUp.Platform.Testing
open ToolUp.Platform.Testing.ModuleHarness
open Toolup.UIToolkit
open ToolUp.AI.Client.Tests.NodeTest

// ─── The kit's lowering and drivers ──────────────────────────────────

/// `node` drawn through the renderer with `dispatch`, as an element a mount
/// can render: the `lower` argument of `ModuleHarness.lowered`.
let lowerTree (dispatch: 'Msg -> unit) (node: Node<'Msg>) : Feliz.ReactElement =
    let props =
        TreeLowering.treeProps (TreeLowering.TreeHost.ofSources HostBindingSources.empty) dispatch node

    Feliz.ReactLegacy.createElement (unbox<Feliz.ReactElement> TreeLoweringRuntime.Tree, box props)

/// The accessible name a driver finds a control by: `aria-label`, the
/// `<label>` naming it (by `for` or by wrapping), its text, its
/// `placeholder`. The drivers find controls as a user does, by name, so a
/// driver works over the Feliz view and the tree alike.
[<Emit("""(() => {
  const el = $0;
  const doc = el.ownerDocument;
  const aria = el.getAttribute("aria-label");
  if (aria && aria.trim() !== "") return aria.trim();
  if (el.id) {
    const byFor = doc.querySelector('label[for="' + el.id + '"]');
    if (byFor && byFor.textContent.trim() !== "") return byFor.textContent.trim();
  }
  const wrapping = el.closest("label");
  if (wrapping && wrapping.textContent.trim() !== "") return wrapping.textContent.trim();
  const text = (el.textContent || "").trim();
  if (text !== "") return text;
  return (el.getAttribute("placeholder") || "").trim();
})()""")>]
let private nameOfElement (element: obj) : string = jsNative

let private find (selector: string) (name: string) (host: obj) : obj =
    let candidates: obj array =
        host?querySelectorAll selector |> JS.Constructors.Array.from

    // The exact name (the first in document order when several share it),
    // else the one control whose name begins with it: a required field's
    // label carries a marker after its text.
    let exact = candidates |> Array.filter (fun e -> nameOfElement e = name)

    let prefixed =
        candidates |> Array.filter (fun e -> (nameOfElement e).StartsWith(name + " "))

    match exact, prefixed with
    | [| e |], _
    | [||], [| e |] -> Some e
    | _ when exact.Length > 1 -> Some exact[0]
    | _ -> None
    |> Option.defaultWith (fun () ->
        failwithf
            "no %s named \"%s\"; the names present are [%s]"
            selector
            name
            (candidates |> Array.map nameOfElement |> String.concat "; "))

/// React's own blur handler for `element`: leaving a field, which is when a
/// field that holds its text as display state commits it.
[<Emit("$0[Object.keys($0).find(k => k.startsWith('__reactProps'))].onBlur({ target: $0, currentTarget: $0 })")>]
let private invokeBlur (element: obj) : unit = jsNative

/// React's own change handler for `element`, called with `value`. The mount
/// installs jsdom after React has loaded, so a real `input` event takes
/// React's legacy path and never reaches `onChange`; the handler is the
/// wiring under test.
[<Emit("$0[Object.keys($0).find(k => k.startsWith('__reactProps'))].onChange({ target: { value: $1, valueAsNumber: Number($1), checked: $1 === 'true' }, currentTarget: { value: $1, valueAsNumber: Number($1), checked: $1 === 'true' } })")>]
let private invokeChange (element: obj) (value: string) : unit = jsNative

[<Import("flushSync", "react-dom")>]
let private flushSync (work: unit -> unit) : unit = jsNative

/// The drivers `Interact` runs against a mounted host.
[<RequireQualifiedAccess>]
module Drive =

    /// Click the button or link named `name`.
    let click (name: string) (host: obj) : unit =
        let target = find "button, a[href], [role=button]" name host
        target?click () |> ignore

    /// Edit the field named `name` to `value`, as a user's keystrokes do.
    let edit (name: string) (value: string) (host: obj) : unit =
        invokeChange (find "input, select, textarea" name host) value

    /// Type `value` into the field named `name` and let the page render it
    /// before the next gesture, as a browser does between two events: a field
    /// the renderer buffers is read by a later gesture (a submit) at its value
    /// then, and one interaction does not otherwise flush between its steps.
    let typeInto (name: string) (value: string) (host: obj) : unit =
        flushSync (fun () -> edit name value host)

    /// Leave the field named `name`, rendered before the next gesture, as a
    /// pointer press on another control does first in a browser.
    let leave (name: string) (host: obj) : unit =
        flushSync (fun () -> invokeBlur (find "input, select, textarea" name host))

    /// Submit the first form in the host.
    let submit (host: obj) : unit =
        let form: obj = host?querySelector "form"

        if isNull form then
            failwith "no form to submit"

        let window: obj = form?ownerDocument?defaultView

        let event: obj =
            createNew window?Event ("submit", createObj [ "bubbles" ==> true; "cancelable" ==> true ])

        form?dispatchEvent event |> ignore

let private expectThrows (body: unit -> unit) (what: string) : string =
    let caught =
        try
            body ()
            None
        with e ->
            Some e.Message

    match caught with
    | Some m -> m
    | None -> failwith (what + " — but it passed. An assertion that cannot fail proves nothing.")

// ─── The subject: the team manager, as Feliz and as a tree ───────────

let private msgs = MessageCatalog.english.TeamManager

/// The Feliz page the module's tree replaced, kept as this kit's reference.
let private felizView (model: TeamManagerUI.Model) (dispatch: TeamManagerUI.Msg -> unit) : Feliz.ReactElement =
    FelizReferenceViews.TeamManager.body msgs model dispatch

/// The module's own tree, drawn as its deferred surface draws it.
let private treeView (model: TeamManagerUI.Model) (dispatch: TeamManagerUI.Msg -> unit) : Feliz.ReactElement =
    lowerTree dispatch (TeamManagerTree.tree msgs model dispatch)

let private team (id: string) (name: string) : TeamInfo = {
    TeamId = id
    Name = name
    CreatedAt = System.DateTime(2026, 1, 1)
    Archived = false
}

let private teamInit () =
    fromUnitInit (fun () -> TeamManagerUI.init ClientModuleContext.empty) TeamManagerUI.update

let private twoTeams () =
    (teamInit ())
        .Dispatch(TeamManagerUI.TeamsLoaded [ team "t1" "Alpha"; team "t2" "Beta" ])
        .Dispatch(TeamManagerUI.ActiveTeamLoaded(Some "t1"))

let private inviting () =
    (twoTeams ()).Dispatch(TeamManagerUI.NavigatePendingInvites "t1").Dispatch(TeamManagerUI.OpenIssueByEmail "t1")

/// The states the subject is held at parity in, each reached through the
/// module's real `update`.
let private parityStates () = [
    "no teams yet", (teamInit ()).Dispatch(TeamManagerUI.TeamsLoaded [])
    "two teams, one active", twoTeams ()
    "an error over the team list", (twoTeams ()).Dispatch(TeamManagerUI.ApiError "the team service is unreachable")
]

let private subjectTests =
    testList "the team manager as a tree" [

        testCase "assertion 1: every state's tree passes the Strict floor" (fun () ->
            for _, harness in parityStates () @ [ "inviting by email", inviting () ] do
                harness.AssertAccessibleView(ViewMount.mount, treeView, Accessibility.Strict)
                |> ignore)

        testCase "assertion 2: a click and a field edit reach update, asserted on the model" (fun () ->
            (twoTeams ())
                .Interact(ViewMount.mountAndInteract, treeView, Drive.click msgs.Manage)
                .AssertModelWith(
                    "the first team's Manage opens its details",
                    fun m -> m.CurrentView = TeamManagerUI.TeamDetails "t1"
                )
            |> ignore

            (twoTeams ())
                .Dispatch(TeamManagerUI.ApiError "boom")
                .Interact(ViewMount.mountAndInteract, treeView, Drive.click msgs.Dismiss)
                .AssertModelWith("the banner's dismiss clears the error", fun m -> m.Error.IsNone)
            |> ignore

            (inviting ())
                .Interact(ViewMount.mountAndInteract, treeView, Drive.edit msgs.EmailPlaceholder "a@example.com")
                .Interact(ViewMount.mountAndInteract, treeView, Drive.edit msgs.ExpiresInDays "14")
                .AssertModelWith(
                    "both edits are lifted into the invite's model",
                    fun m ->
                        match m.IssueByEmail with
                        | Some s -> s.Email = "a@example.com" && s.ExpiresInDays = 14
                        | None -> false
                )
            |> ignore)

        testCase "assertion 2, the same driver over the Feliz view: the behaviour is the view's" (fun () ->
            (twoTeams ())
                .Interact(ViewMount.mountAndInteract, felizView, Drive.click msgs.Manage)
                .AssertModelWith(
                    "the Feliz view's Manage opens the same details",
                    fun m -> m.CurrentView = TeamManagerUI.TeamDetails "t1"
                )
            |> ignore)

        testCase "assertion 3: the tree is at parity with the Feliz view in every state" (fun () ->
            for name, harness in parityStates () do
                try
                    harness.AssertViewParity(ViewMount.mount, felizView, treeView) |> ignore
                with e ->
                    failwithf "state \"%s\": %s" name e.Message)

        testCase "parity is not vacuous: each state exposes controls or headings on both sides" (fun () ->
            for name, harness in parityStates () do
                let outline v =
                    harness.RenderViewHtml(ViewMount.mount, v) |> TreeViewParity.ofHtml

                Expect.isFalse
                    (List.isEmpty (outline felizView).Stops)
                    (sprintf "state \"%s\": the Feliz view's outline" name)

                Expect.isFalse
                    (List.isEmpty (outline treeView).Stops)
                    (sprintf "state \"%s\": the tree's outline" name)

            let tabs =
                ((twoTeams ()).RenderViewHtml(ViewMount.mount, treeView) |> TreeViewParity.ofHtml).TabOrder

            Expect.equal
                (tabs |> List.map _.Name)
                [ Some msgs.Manage; Some msgs.Switch; Some msgs.Manage ]
                "the tree's tab order is the rows' actions, in row order")

        // The invite dialog is held to the floor and to behaviour, not to
        // parity: the Feliz view's expiry input sits beside a `<label>` that
        // is not associated with it, so it has no accessible name, and its
        // number is a text box. The tree names it and makes it a number.
        // Parity reports exactly that, which is the instrument working.
        testCase "parity names the Feliz view's unnamed expiry input in the invite dialog" (fun () ->
            let differences =
                (inviting ()).CheckViewParity(ViewMount.mount, felizView, treeView)

            let unnamedTextbox =
                differences
                |> List.exists (function
                    | TreeViewParity.MissingFromTree s -> s.Role = "textbox" && s.Name.IsNone
                    | _ -> false)

            Expect.isTrue
                unnamedTextbox
                ("the Feliz view's unlabelled input is the difference parity must name. Got: "
                 + TreeViewParity.report differences))
    ]

// ─── The subject: the module-visibility editor ───────────────────────

let private visibilityMsgs = MessageCatalog.english.ModuleVisibilityAdmin

let private visibilityFeliz
    (model: ModuleVisibilityAdminUI.Model)
    (dispatch: ModuleVisibilityAdminUI.Msg -> unit)
    : Feliz.ReactElement =
    FelizReferenceViews.ModuleVisibilityAdmin.body visibilityMsgs model dispatch

/// The module's own tree, drawn as its deferred surface draws it.
let visibilityTree
    (model: ModuleVisibilityAdminUI.Model)
    (dispatch: ModuleVisibilityAdminUI.Msg -> unit)
    : Feliz.ReactElement =
    lowerTree dispatch (ModuleVisibilityAdminTree.tree visibilityMsgs model dispatch)

let visibilityInit () =
    fromUnitInit (fun () -> ModuleVisibilityAdminUI.init ClientModuleContext.empty) ModuleVisibilityAdminUI.update

/// Two registered modules, no stored profile, and the resolution beside them.
let visibilityLoaded () =
    (visibilityInit ())
        .Dispatch(ModuleVisibilityAdminUI.RegisteredLoaded(Ok [ "alpha"; "_sdk.Team_Manager" ]))
        .Dispatch(ModuleVisibilityAdminUI.ProfileLoaded(Ok None))
        .Dispatch(
            ModuleVisibilityAdminUI.ResolutionLoaded(
                Ok(
                    Some {
                        GovernedModuleIds = [ "_sdk.Team_Manager" ]
                        SelectedModuleIds = [ "alpha" ]
                        ExcludedEntryIds = []
                        ContributingScopes = []
                    }
                )
            )
        )

let private visibilityStates () = [
    "loading", visibilityInit ()
    "two modules, no profile", visibilityLoaded ()
    "one module picked", (visibilityLoaded ()).Dispatch(ModuleVisibilityAdminUI.ToggleModule "alpha")
]

let private visibilityTests =
    testList "the module-visibility editor as a tree" [

        testCase "assertion 1: every state's tree passes the Strict floor" (fun () ->
            for name, harness in visibilityStates () do
                try
                    harness.AssertAccessibleView(ViewMount.mount, visibilityTree, Accessibility.Strict)
                    |> ignore
                with e ->
                    failwithf "state \"%s\": %s" name e.Message)

        testCase "assertion 2: a checkbox and the kind reach update, asserted on the model" (fun () ->
            (visibilityLoaded ())
                .Interact(
                    ViewMount.mountAndInteract,
                    visibilityTree,
                    Drive.edit ("alpha" + visibilityMsgs.VisibleNow) "true"
                )
                .AssertModelWith("the candidate's checkbox adds it to the rule", fun m -> m.Selection = [ "alpha" ])
            |> ignore

            (visibilityLoaded ())
                .Interact(ViewMount.mountAndInteract, visibilityTree, Drive.click visibilityMsgs.DenyLabel)
                .AssertModelWith(
                    "the Deny choice sets the rule's kind",
                    fun m -> m.Kind = ModuleVisibilityAdminUI.RuleKind.Deny
                )
            |> ignore)

        testCase "assertion 2: the note is the island's until the save, which carries it" (fun () ->
            let typed =
                (visibilityLoaded ())
                    .Dispatched(
                        ViewMount.mountAndInteract,
                        visibilityTree,
                        Drive.edit visibilityMsgs.NoteLabel "for the pilot"
                    )

            Expect.isEmpty typed "typing in the note dispatches nothing"

            let saved =
                (visibilityLoaded ())
                    .Dispatched(
                        ViewMount.mountAndInteract,
                        visibilityTree,
                        fun host ->
                            Drive.typeInto visibilityMsgs.NoteLabel "for" host
                            Drive.typeInto visibilityMsgs.NoteLabel "for the pilot" host
                            Drive.leave visibilityMsgs.NoteLabel host
                            Drive.click visibilityMsgs.SaveProfile host
                    )

            Expect.equal saved [ ModuleVisibilityAdminUI.Save "for the pilot" ] "the save carries the note typed, once"

            (visibilityLoaded ())
                .Interact(ViewMount.mountAndInteract, visibilityTree, Drive.click visibilityMsgs.SaveProfile)
                .AssertModelWith("the save reaches update and the editor is busy", fun m -> m.Busy)
            |> ignore)

        // Two differences, both judged. The Feliz page put each kind's
        // description inside its button, so the button's name was the label and
        // the sentence; the tree names the button by its label and shows the
        // sentence beside it. The Feliz page's note sat beside a `<label>` not
        // associated with it, so its name was its placeholder; the island's
        // label wraps it. Parity must name exactly these, in every loaded state.
        testCase "assertion 3: parity with the Feliz page names exactly the two judged differences" (fun () ->
            let m = visibilityMsgs

            let expected =
                Set.ofList [
                    "missing", "button", sprintf "%s %s" m.AllowLabel m.AllowDescription
                    "missing", "button", sprintf "%s %s" m.DenyLabel m.DenyDescription
                    "missing", "textbox", m.NotePlaceholder
                    "extra", "button", m.AllowLabel
                    "extra", "button", m.DenyLabel
                    "extra", "textbox", m.NoteLabel
                ]

            let named (differences: TreeViewParity.Difference list) =
                differences
                |> List.choose (function
                    | TreeViewParity.MissingFromTree s -> Some("missing", s.Role, defaultArg s.Name "")
                    | TreeViewParity.ExtraInTree s -> Some("extra", s.Role, defaultArg s.Name "")
                    | TreeViewParity.TabOrderDiffers _ -> None)
                |> Set.ofList

            for name, harness in visibilityStates () do
                let differences =
                    harness.CheckViewParity(ViewMount.mount, visibilityFeliz, visibilityTree)

                if name = "loading" then
                    Expect.isEmpty differences "while loading the two pages announce the same"
                else
                    Expect.equal
                        (Set.toList (named differences))
                        (Set.toList expected)
                        (sprintf
                            "state \"%s\": named %A, expected %A"
                            name
                            (Set.toList (named differences))
                            (Set.toList expected)))
    ]

// ─── The subject: Platform Management ────────────────────────────────

/// The buttons of a page, in tab order, but for those `skip` names.
let private buttonsOf (skip: string option -> bool) (outline: TreeViewParity.Outline) =
    outline.TabOrder
    |> List.filter (fun stop -> stop.Role = "button" && not (skip stop.Name))
    |> List.map _.Name

let private adminMsgs = MessageCatalog.english.PlatformAdmin
let private widgetMsgs = MessageCatalog.english.PublicUtilityWidgets

/// The operator a test signs in as; the session store is not reachable from a
/// test document.
let private operator: PlatformAdminUI.Caller = {
    UserId = "op"
    Display = "op@example.com"
}

let private adminFeliz (model: PlatformAdminUI.Model) (dispatch: PlatformAdminUI.Msg -> unit) : Feliz.ReactElement =
    FelizReferenceViews.PlatformAdmin.body PublicUtilityConfig.defaults operator adminMsgs widgetMsgs model dispatch

/// The module's own tree, drawn as its deferred surface draws it.
let adminTree (model: PlatformAdminUI.Model) (dispatch: PlatformAdminUI.Msg -> unit) : Feliz.ReactElement =
    lowerTree
        dispatch
        (PlatformAdminTree.tree adminMsgs widgetMsgs PublicUtilityConfig.defaults (fun () -> operator) model dispatch)

let adminInit () =
    fromUnitInit (fun () -> PlatformAdminUI.init ClientModuleContext.empty) PlatformAdminUI.update

let private summary (id: string) (name: string) (archived: bool) : TeamSummary = {
    TeamId = id
    Name = name
    CreatedAt = System.DateTime(2026, 1, 1)
    Archived = archived
    MemberCount = 2
    Owners = [ "owner-" + id ]
    Admins = []
}

let adminTeams () =
    (adminInit ())
        .Dispatch(PlatformAdminUI.SwitchTab PlatformAdminUI.TeamsTab)
        .Dispatch(PlatformAdminUI.TeamsLoaded(Ok [ summary "t1" "Alpha" false; summary "t2" "Beta" true ]))

/// The states Platform Management is held to, each reached through the
/// module's real `update`.
let adminStates () = [
    "loading", adminInit ()
    "two admins", (adminInit ()).Dispatch(PlatformAdminUI.AdminsLoaded(Ok [ "u1"; "u2" ]))
    "a revoke failed",
    (adminInit ())
        .Dispatch(PlatformAdminUI.AdminsLoaded(Ok [ "u1"; "u2" ]))
        .Dispatch(PlatformAdminUI.RevokeResolved("u2", Error "not permitted"))
    "granting",
    (adminInit ()).Dispatch(PlatformAdminUI.UpdateAssignInput "u3").Dispatch(PlatformAdminUI.RequestAssignConfirm)
    "the teams tab", adminTeams ()
    "deleting an archived team", (adminTeams ()).Dispatch(PlatformAdminUI.RequestDeleteTeam "t2")
    "the settings tab",
    (adminInit ())
        .Dispatch(PlatformAdminUI.SwitchTab PlatformAdminUI.SettingsTab)
        .Dispatch(PlatformAdminUI.PlatformKnowledgeBaseLoaded(Ok FeatureMode.Enabled))
]

let private adminTests =
    testList "Platform Management as a tree" [

        testCase "assertion 1: every state's tree passes the Strict floor" (fun () ->
            for name, harness in adminStates () do
                try
                    harness.AssertAccessibleView(ViewMount.mount, adminTree, Accessibility.Strict)
                    |> ignore
                with e ->
                    failwithf "state \"%s\": %s" name e.Message)

        testCase "assertion 2: a tab, a kept row's button and a field edit reach update" (fun () ->
            (adminInit ())
                .Interact(ViewMount.mountAndInteract, adminTree, Drive.click adminMsgs.TeamsTab)
                .AssertModelWith("the Teams tab is shown", fun m -> m.ActiveTab = PlatformAdminUI.TeamsTab)
            |> ignore

            (adminTeams ())
                .Interact(ViewMount.mountAndInteract, adminTree, Drive.click adminMsgs.Archive)
                .AssertModelWith(
                    "the live team's Archive is in flight for that team",
                    fun m -> m.TeamActionPending = Set.ofList [ "t1" ]
                )
            |> ignore

            (adminTeams ())
                .Interact(ViewMount.mountAndInteract, adminTree, Drive.click adminMsgs.Delete)
                .AssertModelWith(
                    "the archived team's Delete opens its confirmation",
                    fun m -> m.ConfirmDeleteTeam |> Option.exists (fun c -> c.TeamId = "t2")
                )
            |> ignore

            (adminTeams ())
                .Interact(
                    ViewMount.mountAndInteract,
                    adminTree,
                    fun host ->
                        Drive.typeInto adminMsgs.TeamNameLabel "Gamma" host
                        Drive.leave adminMsgs.TeamNameLabel host
                )
                .AssertModelWith("the team name reaches the model on leaving it", fun m -> m.NewTeamName = "Gamma")
            |> ignore

            (adminInit ())
                .Dispatch(PlatformAdminUI.AdminsLoaded(Ok [ "u1"; "u2" ]))
                .Interact(ViewMount.mountAndInteract, adminTree, Drive.click adminMsgs.Revoke)
                .AssertSteps(fun steps -> not (List.isEmpty steps))
            |> ignore)

        // Three differences, all judged, and nothing else. (1) Tabs: the Feliz
        // bar put every tab in the tab order; the tree's tabs are one stop, the
        // active tab, with its panel after it, as a tab list's keyboard pattern
        // asks. (2) Names: the Feliz page's "Team name" and "Initial owner"
        // labels were not associated with their inputs, which were named by
        // their placeholders; the tree's labels wrap them. (3) Confirmations:
        // the renderer's modal is a dialog with a close control and its heading
        // at level 2, where the Feliz confirmation was a dialog headed at level
        // 3. Every other control and heading, and the buttons' order, match.
        testCase "assertion 3: parity with the Feliz page, but for the three judged differences" (fun () ->
            let m = adminMsgs

            let renamed =
                Set.ofList [
                    "textbox", m.TeamNamePlaceholder
                    "textbox", m.TeamNameLabel
                    "textbox", m.InitialOwnerLabel
                ]

            let judged (difference: TreeViewParity.Difference) =
                match difference with
                | TreeViewParity.TabOrderDiffers _ -> true
                | TreeViewParity.MissingFromTree s
                | TreeViewParity.ExtraInTree s ->
                    s.Role = "dialog"
                    || s.Role = "tabpanel"
                    || (s.Role.StartsWith "heading"
                        && (s.Name = Some m.GrantHeading || s.Name = Some m.DeleteTeamHeading))
                    || (s.Role = "button" && s.Name = Some "Close")
                    || renamed.Contains(s.Role, defaultArg s.Name "")
                    // The owner typeahead's placeholder name, on the Feliz side only.
                    || (s.Role = "textbox"
                        && s.Name = Some m.UserPickerPlaceholder
                        && (match difference with
                            | TreeViewParity.MissingFromTree _ -> true
                            | _ -> false))

            let buttons = buttonsOf ((=) (Some "Close"))

            for name, harness in adminStates () do
                let unjudged =
                    harness.CheckViewParity(ViewMount.mount, adminFeliz, adminTree)
                    |> List.filter (judged >> not)

                Expect.isEmpty unjudged (sprintf "state \"%s\": %s" name (TreeViewParity.report unjudged))

                let outline view =
                    harness.RenderViewHtml(ViewMount.mount, view) |> TreeViewParity.ofHtml

                Expect.equal
                    (buttons (outline adminTree))
                    (buttons (outline adminFeliz))
                    (sprintf "state \"%s\": the buttons, in tab order" name))
    ]

// ─── The subject: session security ───────────────────────────────────

let private sessionMsgs = MessageCatalog.english.SessionSecurity

/// The minute the tree reads "last seen" against; the Feliz page read the clock.
let private sessionNow =
    System.DateTimeOffset(2026, 11, 1, 12, 0, 0, System.TimeSpan.Zero)

let private sessionFeliz
    (model: SessionSecurityUI.Model)
    (dispatch: SessionSecurityUI.Msg -> unit)
    : Feliz.ReactElement =
    FelizReferenceViews.SessionSecurity.body sessionMsgs model dispatch

/// The module's own tree, drawn as its deferred surface draws it.
let sessionTree (model: SessionSecurityUI.Model) (dispatch: SessionSecurityUI.Msg -> unit) : Feliz.ReactElement =
    lowerTree dispatch (SessionSecurityTree.tree sessionMsgs sessionNow model)

let private session (id: string) (device: string) (revoked: bool) : SessionRecord = {
    SessionId = id
    UserId = "op"
    ScopeId = "user-op"
    DeviceDescriptor = device
    AuthProvider = "HeaderAuthProvider"
    CreatedAt = sessionNow.AddDays -3.0
    LastSeenAt = sessionNow.AddMinutes -5.0
    Status = (if revoked then RevokedSession else ActiveSession)
    RevokedAt = (if revoked then Some(sessionNow.AddDays -1.0) else None)
    RevokedBy = (if revoked then Some "op" else None)
}

let sessionInit () =
    fromUnitInit (fun () -> SessionSecurityUI.init ClientModuleContext.empty) SessionSecurityUI.update

/// One live session and one revoked, as the server lists them.
let sessionsLoaded () =
    (sessionInit ())
        .Dispatch(
            SessionSecurityUI.SessionsLoaded(
                Ok [ session "s1" "Chrome on Windows" false; session "s2" "Safari on iOS" true ]
            )
        )

/// The states session security is held to, each reached through `update`.
let sessionStates () = [
    "loading", sessionInit ()
    "no sessions", (sessionInit ()).Dispatch(SessionSecurityUI.SessionsLoaded(Ok []))
    "one live session, one revoked", sessionsLoaded ()
    "a sign-out awaiting confirmation", (sessionsLoaded ()).Dispatch(SessionSecurityUI.AskRevoke "s1")
    "sign out everywhere awaiting confirmation", (sessionsLoaded ()).Dispatch SessionSecurityUI.AskRevokeAll
    "a refresh failed",
    (sessionsLoaded ()).Dispatch(SessionSecurityUI.SessionsLoaded(Error "the session service is unreachable"))
]

let private sessionTests =
    testList "session security as a tree" [

        testCase "assertion 1: every state's tree passes the Strict floor" (fun () ->
            for name, harness in sessionStates () do
                try
                    harness.AssertAccessibleView(ViewMount.mount, sessionTree, Accessibility.Strict)
                    |> ignore
                with e ->
                    failwithf "state \"%s\": %s" name e.Message)

        testCase "assertion 2: a kept row's button and the confirmations reach update" (fun () ->
            (sessionsLoaded ())
                .Interact(ViewMount.mountAndInteract, sessionTree, Drive.click sessionMsgs.SignOut)
                .AssertModelWith(
                    "the live session's Sign out asks to confirm it",
                    fun m -> m.PendingRevoke = Some "s1"
                )
            |> ignore

            (sessionsLoaded ())
                .Dispatch(SessionSecurityUI.AskRevoke "s1")
                .Interact(ViewMount.mountAndInteract, sessionTree, Drive.click sessionMsgs.Confirm)
                .AssertModelWith(
                    "the confirmation revokes, and the page is busy",
                    fun m -> m.Busy && m.PendingRevoke.IsNone
                )
            |> ignore

            (sessionsLoaded ())
                .Interact(ViewMount.mountAndInteract, sessionTree, Drive.click sessionMsgs.SignOutEverywhere)
                .AssertModelWith("sign out everywhere asks to confirm it", fun m -> m.PendingRevokeAll)
            |> ignore

            (sessionsLoaded ())
                .Dispatch(SessionSecurityUI.SessionsLoaded(Error "boom"))
                .Interact(ViewMount.mountAndInteract, sessionTree, Drive.click sessionMsgs.Dismiss)
                .AssertModelWith("the banner's dismiss clears the error", fun m -> m.Error.IsNone)
            |> ignore)

        // No difference to judge: the Feliz page was a heading, two notices,
        // a list of rows and its buttons, and so is the tree.
        testCase "assertion 3: at parity with the Feliz page in every state" (fun () ->
            let failures = [
                for name, harness in sessionStates () do
                    let differences =
                        harness.CheckViewParity(ViewMount.mount, sessionFeliz, sessionTree)

                    if not differences.IsEmpty then
                        yield sprintf "state \"%s\": %s" name (TreeViewParity.report differences)
            ]

            Expect.isEmpty
                failures
                (String.concat
                    "
"
                    failures))
    ]

// ─── The subject: the permissions admin ──────────────────────────────

let private permissionsMsgs = MessageCatalog.english.PermissionsAdmin

/// The operator a test signs in as; the session store is not reachable from a
/// test document, so both pages take the caller.
let private permissionsCaller: PermissionsAdminUI.Caller = {
    UserId = "op"
    DisplayName = Some "Operator"
    Email = Some "op@example.com"
}

let private permissionsFeliz
    (model: PermissionsAdminUI.Model)
    (dispatch: PermissionsAdminUI.Msg -> unit)
    : Feliz.ReactElement =
    FelizReferenceViews.PermissionsAdmin.body permissionsMsgs permissionsCaller model dispatch

/// The module's own tree, drawn as its deferred surface draws it.
let permissionsTree (model: PermissionsAdminUI.Model) (dispatch: PermissionsAdminUI.Msg -> unit) : Feliz.ReactElement =
    lowerTree dispatch (PermissionsAdminTree.tree permissionsMsgs (fun () -> permissionsCaller) model dispatch)

let private membership (userId: string) (role: TeamRole) : TeamMembership = {
    TeamId = "t1"
    UserId = userId
    Role = role
    JoinedAt = System.DateTime(2026, 1, 1)
}

let private teamView (managed: string list) : PermissionsAdminUI.TeamView = {
    TeamId = "t1"
    Members = [ membership "u1" Owner; membership "u2" Member ]
    Managed = managed
    Permissions = {
        TeamPermissions.empty with
            Defaults = Map.ofList [ "alpha", [ ModulePermission.Read ] ]
            Members = Map.ofList [ "u2", Map.ofList [ "beta", [ ModulePermission.Write ] ] ]
            Exposure = Map.ofList [ "beta", ModuleExposure.Hidden ]
    }
}

let permissionsInit () =
    fromUnitInit (fun () -> PermissionsAdminUI.init ClientModuleContext.empty) PermissionsAdminUI.update

/// Two managed modules and two members, one with an override.
let permissionsLoaded () =
    (permissionsInit ()).Dispatch(PermissionsAdminUI.SnapshotLoaded(Ok(teamView [ "alpha"; "beta" ])))

/// The states the permissions admin is held to, each reached through `update`.
let permissionsStates () = [
    "loading", permissionsInit ()
    "the team defaults", permissionsLoaded ()
    "a default toggled",
    (permissionsLoaded ()).Dispatch(PermissionsAdminUI.ToggleDefault("beta", ModulePermission.Admin))
    "a save in flight",
    (permissionsLoaded ())
        .Dispatch(PermissionsAdminUI.ToggleDefault("beta", ModulePermission.Admin))
        .Dispatch(PermissionsAdminUI.SaveDefaults)
    "the members tab", (permissionsLoaded ()).Dispatch(PermissionsAdminUI.SwitchTab PermissionsAdminUI.MembersTab)
    "a member with an override",
    (permissionsLoaded ())
        .Dispatch(PermissionsAdminUI.SwitchTab PermissionsAdminUI.MembersTab)
        .Dispatch(PermissionsAdminUI.SelectMember "u2")
    "the modules tab", (permissionsLoaded ()).Dispatch(PermissionsAdminUI.SwitchTab PermissionsAdminUI.ModulesTab)
    "no managed modules", (permissionsInit ()).Dispatch(PermissionsAdminUI.SnapshotLoaded(Ok(teamView [])))
    "a save failed",
    (permissionsLoaded ()).Dispatch(PermissionsAdminUI.SaveDefaultsResult(Error "only an owner or admin may"))
    "the snapshot failed", (permissionsInit ()).Dispatch(PermissionsAdminUI.SnapshotLoaded(Error "unreachable"))
]

let private permissionsTests =
    testList "the permissions admin as a tree" [

        testCase "assertion 1: every state's tree passes the Strict floor" (fun () ->
            for name, harness in permissionsStates () do
                try
                    harness.AssertAccessibleView(ViewMount.mount, permissionsTree, Accessibility.Strict)
                    |> ignore
                with e ->
                    failwithf "state \"%s\": %s" name e.Message)

        testCase "assertion 2: a checkbox, a column toggle, a tab and a member reach update" (fun () ->
            (permissionsLoaded ())
                .Interact(ViewMount.mountAndInteract, permissionsTree, Drive.edit permissionsMsgs.PermWrite "true")
                .AssertModelWith(
                    "the first Write box grants alpha Write in the overlay",
                    fun m ->
                        m.EditingDefaults
                        |> Option.exists (fun e ->
                            Map.find "alpha" e = [ ModulePermission.Read; ModulePermission.Write ])
                )
            |> ignore

            (permissionsLoaded ())
                .Interact(ViewMount.mountAndInteract, permissionsTree, Drive.click permissionsMsgs.SelectAll)
                .AssertModelWith(
                    "the Read column's Select all grants Read on every module",
                    fun m ->
                        m.EditingDefaults
                        |> Option.exists (fun e ->
                            [ "alpha"; "beta" ]
                            |> List.forall (fun x -> Map.find x e |> List.contains ModulePermission.Read))
                )
            |> ignore

            (permissionsLoaded ())
                .Interact(ViewMount.mountAndInteract, permissionsTree, Drive.click permissionsMsgs.TabMembers)
                .AssertModelWith("the Members tab is shown", fun m -> m.ActiveTab = PermissionsAdminUI.MembersTab)
            |> ignore

            (permissionsLoaded ())
                .Dispatch(PermissionsAdminUI.SwitchTab PermissionsAdminUI.MembersTab)
                .Interact(ViewMount.mountAndInteract, permissionsTree, Drive.click "u2")
                .AssertModelWith("the member's button selects them", fun m -> m.SelectedMember = Some "u2")
            |> ignore

            (permissionsLoaded ())
                .Dispatch(PermissionsAdminUI.SwitchTab PermissionsAdminUI.ModulesTab)
                .Interact(ViewMount.mountAndInteract, permissionsTree, Drive.click permissionsMsgs.ExposureUnavailable)
                .AssertSteps(fun steps -> not (List.isEmpty steps))
            |> ignore)

        // Two differences, both judged, and nothing else. (1) Tabs: the Feliz bar
        // put every tab in the tab order; the tree's tabs are one stop, the active
        // tab, with its panel after it, as a tab list's keyboard pattern asks.
        // (2) The member list: the Feliz page put the member's override badge and
        // role inside the member's button, so its name was all three; the tree
        // names the button by the member and shows the badge and role beside it.
        // Every other control, the matrices' checkboxes above all, matches, and
        // so do the buttons' order and names.
        testCase "assertion 3: parity with the Feliz page, but for the two judged differences" (fun () ->
            let isMember (name: string option) =
                name
                |> Option.exists (fun n -> n = "u1" || n = "u2" || n.StartsWith "u1 " || n.StartsWith "u2 ")

            let judged (difference: TreeViewParity.Difference) =
                match difference with
                | TreeViewParity.TabOrderDiffers _ -> true
                | TreeViewParity.MissingFromTree s
                | TreeViewParity.ExtraInTree s -> s.Role = "tabpanel" || (s.Role = "button" && isMember s.Name)

            let buttons = buttonsOf isMember

            for name, harness in permissionsStates () do
                let unjudged =
                    harness.CheckViewParity(ViewMount.mount, permissionsFeliz, permissionsTree)
                    |> List.filter (judged >> not)

                Expect.isEmpty unjudged (sprintf "state \"%s\": %s" name (TreeViewParity.report unjudged))

                let outline view =
                    harness.RenderViewHtml(ViewMount.mount, view) |> TreeViewParity.ofHtml

                Expect.equal
                    (buttons (outline permissionsTree))
                    (buttons (outline permissionsFeliz))
                    (sprintf "state \"%s\": the buttons, in tab order" name))
    ]

// ─── The subject: platform users ─────────────────────────────────────

let private usersMsgs = MessageCatalog.english.PlatformUsers

let private usersFeliz (model: PlatformUsersUI.Model) (dispatch: PlatformUsersUI.Msg -> unit) : Feliz.ReactElement =
    FelizReferenceViews.PlatformUsers.body usersMsgs model dispatch

/// The module's own tree, drawn as its deferred surface draws it.
let usersTree (model: PlatformUsersUI.Model) (dispatch: PlatformUsersUI.Msg -> unit) : Feliz.ReactElement =
    lowerTree dispatch (PlatformUsersTree.tree usersMsgs model dispatch)

let private principal (userId: string) (teams: int) : PrincipalSummary = {
    UserId = userId
    Memberships = List.init teams (fun i -> sprintf "team-%d" i, Member)
    LastSeenAt = Some(System.DateTime(2026, 11, 1))
    HasUserScopeData = teams = 0
}

let usersInit () =
    fromUnitInit (fun () -> PlatformUsersUI.init ClientModuleContext.empty) PlatformUsersUI.update

/// One principal in a team and one in none.
let usersLoaded () =
    (usersInit ()).Dispatch(PlatformUsersUI.PrincipalsLoaded(Ok [ principal "u1" 2; principal "u2" 0 ]))

let private previewOf: LifecyclePreview = {
    ScopeId = "user-u2"
    Items = [
        LifecyclePreviewItem.affecting "blobs" 3 "3 blobs"
        LifecyclePreviewItem.noPreview "audit"
    ]
    TotalWouldAffect = 3
}

let private summaryOf: LifecycleSummary = {
    ScopeId = "user-u2"
    Phase = Deprovisioning
    Outcomes = [
        {
            HookName = "blobs"
            Result = LifecycleHookResult.Completed
            ElapsedMs = 12L
        }
        {
            HookName = "audit"
            Result = LifecycleHookResult.Skipped "kept for the record"
            ElapsedMs = 1L
        }
    ]
    TotalElapsedMs = 13L
}

let private offboarding () =
    (usersLoaded ()).Dispatch(PlatformUsersUI.OpenOffboard("u2", PlatformUsersUI.PlainOffboard))

/// The states platform users is held to, each reached through `update`.
let usersStates () = [
    "loading", usersInit ()
    "two principals", usersLoaded ()
    "team-less only", (usersLoaded ()).Dispatch PlatformUsersUI.ToggleTeamLessOnly
    "no principals", (usersInit ()).Dispatch(PlatformUsersUI.PrincipalsLoaded(Ok []))
    "the list failed", (usersInit ()).Dispatch(PlatformUsersUI.PrincipalsLoaded(Error "not a platform admin"))
    "the offboard dialog", offboarding ()
    "the impact previewed", (offboarding ()).Dispatch(PlatformUsersUI.PreviewLoaded(Ok previewOf))
    "a confirmation asked for",
    (offboarding ())
        .Dispatch(PlatformUsersUI.SetOffboardReason "left the company")
        .Dispatch(PlatformUsersUI.OffboardDone(Error "offboard confirmation required"))
    "the offboard completed", (offboarding ()).Dispatch(PlatformUsersUI.OffboardDone(Ok summaryOf))
]

let private usersTests =
    testList "platform users as a tree" [

        testCase "assertion 1: every state's tree passes the Strict floor" (fun () ->
            for name, harness in usersStates () do
                try
                    harness.AssertAccessibleView(ViewMount.mount, usersTree, Accessibility.Strict)
                    |> ignore
                with e ->
                    failwithf "state \"%s\": %s" name e.Message)

        testCase "assertion 2: the filter, a kept row's button and the dialog reach update" (fun () ->
            (usersLoaded ())
                .Interact(ViewMount.mountAndInteract, usersTree, Drive.edit usersMsgs.TeamLessOnly "true")
                .AssertModelWith("the filter shows team-less principals only", fun m -> m.TeamLessOnly)
            |> ignore

            (usersLoaded ())
                .Interact(ViewMount.mountAndInteract, usersTree, Drive.click usersMsgs.ExportOffboardAction)
                .AssertModelWith(
                    "the first row's export opens its dialog",
                    fun m ->
                        m.Offboard
                        |> Option.exists (fun o -> o.UserId = "u1" && o.Kind = PlatformUsersUI.ExportOffboard)
                )
            |> ignore

            (offboarding ())
                .Interact(
                    ViewMount.mountAndInteract,
                    usersTree,
                    fun host ->
                        Drive.typeInto usersMsgs.ReasonLabel "left the company" host
                        Drive.leave usersMsgs.ReasonLabel host
                )
                .AssertModelWith(
                    "the reason reaches the model on leaving it",
                    fun m -> m.Offboard |> Option.exists (fun o -> o.Reason = "left the company")
                )
            |> ignore

            (offboarding ())
                .Interact(ViewMount.mountAndInteract, usersTree, Drive.click usersMsgs.Cancel)
                .AssertModelWith("Cancel closes the dialog", fun m -> m.Offboard.IsNone)
            |> ignore)

        // Three differences, all judged, and nothing else. (1) The dialog: the
        // renderer's modal is a dialog with a close control and its heading at
        // level 2, where the Feliz overlay was a dialog named by its title and
        // headed at level 3. (2) Names: the Feliz page's reason and token labels
        // were not associated with their inputs, which were named by their
        // placeholders; the islands' labels wrap them. (3) The tab order, which
        // differs only by the dialog's close control. Every other control, and
        // the buttons' order and names, match.
        testCase "assertion 3: parity with the Feliz page, but for the three judged differences" (fun () ->
            let m = usersMsgs

            let titles = set [ m.OffboardTitle; m.ExportOffboardTitle; m.OffboardCompleteTitle ]

            let renamed =
                set [
                    "textbox", m.ReasonPlaceholder
                    "textbox", m.ReasonLabel
                    "textbox", m.ConfirmationTokenPlaceholder
                    "textbox", m.ConfirmationTokenLabel
                ]

            let judged (difference: TreeViewParity.Difference) =
                match difference with
                | TreeViewParity.TabOrderDiffers _ -> true
                | TreeViewParity.MissingFromTree s
                | TreeViewParity.ExtraInTree s ->
                    s.Role = "dialog"
                    || (s.Role.StartsWith "heading" && s.Name |> Option.exists titles.Contains)
                    || renamed.Contains(s.Role, defaultArg s.Name "")
                    || (s.Role = "button"
                        && s.Name = Some "Close"
                        && (match difference with
                            | TreeViewParity.ExtraInTree _ -> true
                            | _ -> false))

            let buttons = buttonsOf ((=) (Some "Close"))

            for name, harness in usersStates () do
                let unjudged =
                    harness.CheckViewParity(ViewMount.mount, usersFeliz, usersTree)
                    |> List.filter (judged >> not)

                Expect.isEmpty unjudged (sprintf "state \"%s\": %s" name (TreeViewParity.report unjudged))

                let outline view =
                    harness.RenderViewHtml(ViewMount.mount, view) |> TreeViewParity.ofHtml

                Expect.equal
                    (buttons (outline usersTree))
                    (buttons (outline usersFeliz))
                    (sprintf "state \"%s\": the buttons, in tab order" name))
    ]

/// Whether `difference` is a select named by its label as the renderer draws
/// it, the label wrapping the select so its options' text follows the label,
/// where the Feliz page's select was named by `label` or not at all.
let private selectRenamed (label: string) (difference: TreeViewParity.Difference) =
    match difference with
    | TreeViewParity.MissingFromTree s -> s.Role = "combobox" && (s.Name.IsNone || s.Name = Some label)
    | TreeViewParity.ExtraInTree s -> s.Role = "combobox" && s.Name |> Option.exists (fun n -> n.StartsWith label)
    | TreeViewParity.TabOrderDiffers _ -> false

// ─── The subject: the audit trail ────────────────────────────────────

let private auditMsgs = MessageCatalog.english.AuditLog

let private auditFeliz (model: AuditLogUI.Model) (dispatch: AuditLogUI.Msg -> unit) : Feliz.ReactElement =
    FelizReferenceViews.AuditLog.body auditMsgs model dispatch

/// The module's own tree, drawn as its deferred surface draws it.
let auditTree (model: AuditLogUI.Model) (dispatch: AuditLogUI.Msg -> unit) : Feliz.ReactElement =
    lowerTree dispatch (AuditLogTree.tree auditMsgs model dispatch)

let private auditEvent (n: int) : AuditEventView = {
    Id = System.Guid(sprintf "00000000-0000-0000-0000-%012d" n)
    OccurredAt = System.DateTime(2026, 11, 1, 12, n % 60, 0, System.DateTimeKind.Utc)
    EventType = (if n % 2 = 0 then "TeamCreated" else "MemberAdded")
    Actor = (if n % 3 = 0 then None else Some "dev-admin")
    Summary = sprintf "event %d" n
    Payload = sprintf """{"n":%d}""" n
}

let auditInit () =
    fromUnitInit AuditLogUI.init AuditLogUI.update

/// Two events of a trail of three, the older page still to come.
let auditLoaded () =
    (auditInit ())
        .Dispatch(AuditLogUI.EventTypesLoaded(Ok [ "MemberAdded"; "TeamCreated" ]))
        .Dispatch(
            AuditLogUI.PageLoaded(
                Ok {
                    Events = [ auditEvent 1; auditEvent 2 ]
                    NextCursor = Some "c2"
                    MatchedCount = 3
                }
            )
        )

/// The states the audit trail is held to, each reached through `update`.
let auditStates () = [
    "loading", auditInit ()
    "nothing recorded",
    (auditInit ())
        .Dispatch(
            AuditLogUI.PageLoaded(
                Ok {
                    Events = []
                    NextCursor = None
                    MatchedCount = 0
                }
            )
        )
    "two events of three", auditLoaded ()
    "a row's detail open", (auditLoaded ()).Dispatch(AuditLogUI.ToggleDetail((auditEvent 2).Id))
    "the older page in flight", (auditLoaded ()).Dispatch AuditLogUI.LoadMore
    "an export in flight", (auditLoaded ()).Dispatch AuditLogUI.ExportCsv
    "nothing matching the filters",
    (auditLoaded ())
        .Dispatch(AuditLogUI.SetEventType "TeamCreated")
        .Dispatch(AuditLogUI.ApplyFilters)
        .Dispatch(
            AuditLogUI.PageLoaded(
                Ok {
                    Events = []
                    NextCursor = None
                    MatchedCount = 0
                }
            )
        )
    "the read failed", (auditInit ()).Dispatch(AuditLogUI.PageLoaded(Error "the audit trail is unreachable"))
]

let private auditTests =
    testList "the audit trail as a tree" [

        testCase "assertion 1: every state's tree passes the Strict floor" (fun () ->
            for name, harness in auditStates () do
                try
                    harness.AssertAccessibleView(ViewMount.mount, auditTree, Accessibility.Strict)
                    |> ignore
                with e ->
                    failwithf "state \"%s\": %s" name e.Message)

        testCase "assertion 2: a kept row's toggle, the filters and the actor reach update" (fun () ->
            (auditLoaded ())
                .Interact(ViewMount.mountAndInteract, auditTree, Drive.click auditMsgs.ShowDetail)
                .AssertModelWith(
                    "the first row's toggle opens its detail",
                    fun m -> m.Expanded = Set.singleton (auditEvent 1).Id
                )
            |> ignore

            (auditLoaded ())
                .Interact(ViewMount.mountAndInteract, auditTree, Drive.edit auditMsgs.FromLabel "2026-11-01")
                .AssertModelWith("the from date reaches the draft", fun m -> m.Draft.From = "2026-11-01")
            |> ignore

            (auditLoaded ())
                .Interact(
                    ViewMount.mountAndInteract,
                    auditTree,
                    Drive.edit
                        (auditMsgs.EventTypeLabel + auditMsgs.AllEventTypes + "MemberAddedTeamCreated")
                        "TeamCreated"
                )
                .AssertModelWith("the event type reaches the draft", fun m -> m.Draft.EventType = "TeamCreated")
            |> ignore

            (auditLoaded ())
                .Interact(
                    ViewMount.mountAndInteract,
                    auditTree,
                    fun host ->
                        Drive.typeInto auditMsgs.ActorLabel "sam" host
                        Drive.leave auditMsgs.ActorLabel host
                )
                .AssertModelWith("the actor reaches the draft on leaving it", fun m -> m.Draft.Actor = "sam")
            |> ignore

            (auditLoaded ())
                .Dispatch(AuditLogUI.SetActor "sam")
                .Interact(ViewMount.mountAndInteract, auditTree, Drive.click auditMsgs.ApplyFilters)
                .AssertModelWith(
                    "Apply starts a fresh sequence under the draft",
                    fun m -> m.Applied.Actor = "sam" && List.isEmpty m.Events
                )
            |> ignore

            (auditLoaded ())
                .Interact(ViewMount.mountAndInteract, auditTree, Drive.click auditMsgs.LoadMore)
                .AssertModelWith("Load older events asks for the next page", fun m -> m.LoadingMore)
            |> ignore)

        // Three differences, all judged, and nothing else. (1) The event type is
        // a select its label wraps, so it is named by the label and then its
        // options, where the Feliz page's was named by an aria-label alone. (2)
        // A row's open payload is a code block, which draws no control. (3) The
        // tab order, which differs only by that name. The buttons and their
        // order match.
        testCase "assertion 3: parity with the Feliz page, but for the judged differences" (fun () ->
            let judged (difference: TreeViewParity.Difference) =
                match difference with
                | TreeViewParity.TabOrderDiffers _ -> true
                | _ -> selectRenamed auditMsgs.EventTypeLabel difference

            for name, harness in auditStates () do
                let outline view =
                    harness.RenderViewHtml(ViewMount.mount, view) |> TreeViewParity.ofHtml

                Expect.equal
                    (buttonsOf (fun _ -> false) (outline auditTree))
                    (buttonsOf (fun _ -> false) (outline auditFeliz))
                    (sprintf "state \"%s\": the buttons, in tab order" name)

            let unjudged = [
                for name, harness in auditStates () do
                    let found =
                        harness.CheckViewParity(ViewMount.mount, auditFeliz, auditTree)
                        |> List.filter (judged >> not)

                    if not found.IsEmpty then
                        sprintf "state \"%s\": %s" name (TreeViewParity.report found)
            ]

            Expect.isEmpty unjudged (String.concat "\n" unjudged))
    ]

// ─── The subject: data subject requests ──────────────────────────────

let private dsrMsgs = MessageCatalog.english.DataSubjectRequestAdmin

let private dsrFeliz
    (model: DataSubjectRequestAdminUI.Model)
    (dispatch: DataSubjectRequestAdminUI.Msg -> unit)
    : Feliz.ReactElement =
    FelizReferenceViews.DataSubjectRequestAdmin.body dsrMsgs model dispatch

/// The module's own tree, drawn as its deferred surface draws it.
let dsrTree
    (model: DataSubjectRequestAdminUI.Model)
    (dispatch: DataSubjectRequestAdminUI.Msg -> unit)
    : Feliz.ReactElement =
    lowerTree dispatch (DataSubjectRequestAdminTree.tree dsrMsgs model dispatch)

let private dsrRequest: DataSubjectRequest = {
    Id = "dsr-1"
    Kind = DataSubjectRequestKind.Erase
    SubjectUserId = "subject-1"
    TeamId = None
    RequestedBy = "op"
    RequestedAt = System.DateTimeOffset(2026, 11, 1, 12, 0, 0, System.TimeSpan.Zero)
    Reason = "asked to be forgotten"
    Policy = ErasurePolicy.Tombstone
}

let private dsrSummary (name: string) (affected: int) : ErasureSummary = {
    HandlerName = name
    RecordsAffected = affected
    Note = None
}

let private dsrPreview: DataSubjectRequestApi.ErasurePreview = {
    Request = dsrRequest
    PerHandlerCounts = Map.ofList [ "blobs", dsrSummary "blobs" 3; "audit", dsrSummary "audit" 0 ]
}

let private dsrRun: DataSubjectRequestApi.ErasureRunSummary = {
    Request = dsrRequest
    StartedAt = dsrRequest.RequestedAt
    CompletedAt = dsrRequest.RequestedAt.AddSeconds 2.0
    PerHandler =
        Map.ofList [
            "blobs", Ok(dsrSummary "blobs" 3)
            "audit", Error(ErasureError.HandlerRefused("audit", "kept for the record"))
        ]
    OverallSuccess = false
}

let dsrInit () =
    fromUnitInit (fun () -> DataSubjectRequestAdminUI.init ClientModuleContext.empty) DataSubjectRequestAdminUI.update

let private dsrErase () =
    (dsrInit ())
        .Dispatch(DataSubjectRequestAdminUI.SwitchTab DataSubjectRequestAdminUI.EraseTab)
        .Dispatch(DataSubjectRequestAdminUI.SetSubjectInput "subject-1")
        .Dispatch(DataSubjectRequestAdminUI.SetReasonInput "asked to be forgotten")

/// The erase tab with a preview pending.
let dsrPreviewed () =
    (dsrErase ()).Dispatch(DataSubjectRequestAdminUI.PreviewResolved(Ok dsrPreview))

/// The states data subject requests are held to, each reached through `update`.
let dsrStates () = [
    "the export tab", dsrInit ()
    "an export without a subject", (dsrInit ()).Dispatch DataSubjectRequestAdminUI.SubmitExport
    "a background export preparing",
    (dsrInit ())
        .Dispatch(DataSubjectRequestAdminUI.SetAsyncMode true)
        .Dispatch(DataSubjectRequestAdminUI.AsyncExportStarted(Ok "ticket-1"))
    "the erase tab", dsrErase ()
    "a policy chosen",
    (dsrErase ()).Dispatch(DataSubjectRequestAdminUI.SetOverridePolicy(Some ErasurePolicy.HardDelete))
    "a preview pending", dsrPreviewed ()
    "an empty preview",
    (dsrErase ())
        .Dispatch(
            DataSubjectRequestAdminUI.PreviewResolved(
                Ok {
                    dsrPreview with
                        PerHandlerCounts = Map.empty
                }
            )
        )
    "an erasure confirmed",
    (dsrPreviewed ())
        .Dispatch(
            DataSubjectRequestAdminUI.ConfirmResolved(Ok(DataSubjectRequestApi.ErasureRunResult.Completed dsrRun))
        )
    "an erasure refused",
    (dsrPreviewed ())
        .Dispatch(
            DataSubjectRequestAdminUI.ConfirmResolved(Ok(DataSubjectRequestApi.ErasureRunResult.Refused "no handlers"))
        )
]

let private dsrTests =
    testList "data subject requests as a tree" [

        testCase "assertion 1: every state's tree passes the Strict floor" (fun () ->
            for name, harness in dsrStates () do
                try
                    harness.AssertAccessibleView(ViewMount.mount, dsrTree, Accessibility.Strict)
                    |> ignore
                with e ->
                    failwithf "state \"%s\": %s" name e.Message)

        testCase "assertion 2: the tabs, the fields, a policy and the preview reach update" (fun () ->
            (dsrInit ())
                .Interact(
                    ViewMount.mountAndInteract,
                    dsrTree,
                    fun host ->
                        Drive.typeInto dsrMsgs.SubjectPlaceholder "subject-1" host
                        Drive.leave dsrMsgs.SubjectPlaceholder host
                )
                .AssertModelWith("the subject reaches the model on leaving it", fun m -> m.SubjectInput = "subject-1")
            |> ignore

            (dsrInit ())
                .Interact(ViewMount.mountAndInteract, dsrTree, Drive.edit dsrMsgs.AsyncModeLabel "true")
                .AssertModelWith("the async-mode checkbox turns the background path on", fun m -> m.AsyncMode)
            |> ignore

            (dsrErase ())
                .Interact(
                    ViewMount.mountAndInteract,
                    dsrTree,
                    Drive.edit (dsrMsgs.PolicyHardDeleteLabel + dsrMsgs.PolicyHardDeleteDescription) "true"
                )
                .AssertModelWith(
                    "a policy's radio overrides the deployment default",
                    fun m -> m.OverridePolicy = Some ErasurePolicy.HardDelete
                )
            |> ignore

            // The chosen policy is announced: its radio, and only its radio, is checked.
            let checkedRadio (harness: ModuleHarness<DataSubjectRequestAdminUI.Model, DataSubjectRequestAdminUI.Msg>) =
                harness.RenderViewHtml(ViewMount.mount, dsrTree).Split("type=\"radio\"")
                |> Array.skip 1
                |> Array.map (fun after -> after.Substring(0, after.IndexOf '>').Contains "checked")
                |> List.ofArray

            Expect.equal (checkedRadio (dsrErase ())) [ true; false; false; false ] "the deployment default is checked"

            Expect.equal
                (checkedRadio (
                    (dsrErase ()).Dispatch(DataSubjectRequestAdminUI.SetOverridePolicy(Some ErasurePolicy.HardDelete))
                ))
                [ false; true; false; false ]
                "a chosen policy is checked"

            (dsrErase ())
                .Interact(ViewMount.mountAndInteract, dsrTree, Drive.click dsrMsgs.PreviewErase)
                .AssertModelWith(
                    "Preview erasure asks for the preview",
                    fun m -> m.Busy = DataSubjectRequestAdminUI.RunningPreview
                )
            |> ignore

            (dsrPreviewed ())
                .Interact(ViewMount.mountAndInteract, dsrTree, Drive.click dsrMsgs.Cancel)
                .AssertModelWith("Cancel drops the pending preview", fun m -> m.PendingPreview.IsNone)
            |> ignore)

        // One difference, judged, and nothing else: the tabs are one tab stop
        // with the active tab's panel after it, so the tab order differs by
        // that alone. The buttons and their order match, and the policy radios
        // are named as the Feliz page's were. "An erasure confirmed" is not
        // compared: the Feliz page threw drawing its run summary (a custom
        // DateTimeOffset format under Fable), which the tree draws from the
        // instant's components.
        testCase "assertion 3: parity with the Feliz page, but for the judged differences" (fun () ->
            let judged (difference: TreeViewParity.Difference) =
                match difference with
                | TreeViewParity.TabOrderDiffers _ -> true
                | _ -> false

            let compared =
                dsrStates () |> List.filter (fun (name, _) -> name <> "an erasure confirmed")

            let unjudged = [
                for name, harness in compared do
                    let found =
                        harness.CheckViewParity(ViewMount.mount, dsrFeliz, dsrTree)
                        |> List.filter (judged >> not)

                    if not found.IsEmpty then
                        sprintf "state \"%s\": %s" name (TreeViewParity.report found)
            ]

            Expect.isEmpty unjudged (String.concat "\n" unjudged)

            for name, harness in compared do
                let outline view =
                    harness.RenderViewHtml(ViewMount.mount, view) |> TreeViewParity.ofHtml

                Expect.equal
                    (buttonsOf (fun _ -> false) (outline dsrTree))
                    (buttonsOf (fun _ -> false) (outline dsrFeliz))
                    (sprintf "state \"%s\": the buttons, in tab order" name))
    ]

// ─── The subject: external contacts ──────────────────────────────────

let private contactMsgs = MessageCatalog.english.ExternalContact

let private contactsFeliz
    (model: ExternalContactManagerUI.Model)
    (dispatch: ExternalContactManagerUI.Msg -> unit)
    : Feliz.ReactElement =
    FelizReferenceViews.ExternalContactManager.body contactMsgs model dispatch

/// The module's own tree, drawn as its deferred surface draws it.
let contactsTree
    (model: ExternalContactManagerUI.Model)
    (dispatch: ExternalContactManagerUI.Msg -> unit)
    : Feliz.ReactElement =
    lowerTree dispatch (ExternalContactManagerTree.tree contactMsgs model dispatch)

let private contact (id: string) (name: string) (consented: bool) : ExternalContact = {
    Id = id
    Type = ExternalContact.EntityType
    Version = 1
    DisplayName = name
    OptionalEmailAddress = Some(id + "@example.com")
    OptionalPhoneNumber = None
    OptionalWhatsAppNumber = None
    Owner = ContactOwner.Team "team-1"
    OptIns =
        if consented then
            Map.ofList [
                NotificationKind.SinkKind.Email,
                {
                    GrantedAt = System.DateTime(2026, 11, 1)
                    Source = "signed form"
                    ExpiresAt = None
                }
            ]
        else
            Map.empty
    Tags = [ "client" ]
    CreatedAt = System.DateTime(2026, 10, 1)
    LastInboundUtc = None
    Notes = None
}

let contactsInit () =
    fromUnitInit ExternalContactManagerUI.init ExternalContactManagerUI.update

/// One contact who consented to email and one who consented to nothing.
let contactsLoaded () =
    (contactsInit ())
        .Dispatch(ExternalContactManagerUI.ContactsLoaded(Ok [ contact "c1" "Ada" true; contact "c2" "Grace" false ]))

let private contactOpen () =
    (contactsLoaded ()).Dispatch(ExternalContactManagerUI.OpenContact "c1")

/// The states external contacts are held to, each reached through `update`.
let contactsStates () = [
    "loading", contactsInit ()
    "no contacts", (contactsInit ()).Dispatch(ExternalContactManagerUI.ContactsLoaded(Ok []))
    "two contacts", contactsLoaded ()
    "the add form", (contactsLoaded ()).Dispatch ExternalContactManagerUI.StartAdd
    "the list failed", (contactsInit ()).Dispatch(ExternalContactManagerUI.ContactsLoaded(Error "not an admin"))
    "a contact open", contactOpen ()
    "a contact with no consent", (contactsLoaded ()).Dispatch(ExternalContactManagerUI.OpenContact "c2")
    "a contact being edited", (contactOpen ()).Dispatch(ExternalContactManagerUI.StartEdit "c1")
    "a consent source typed", (contactOpen ()).Dispatch(ExternalContactManagerUI.SetOptInSource "verbal, 1 Nov")
    "a contact gone", (contactsLoaded ()).Dispatch(ExternalContactManagerUI.OpenContact "c9")
]

let private contactsTests =
    testList "external contacts as a tree" [

        testCase "assertion 1: every state's tree passes the Strict floor" (fun () ->
            for name, harness in contactsStates () do
                try
                    harness.AssertAccessibleView(ViewMount.mount, contactsTree, Accessibility.Strict)
                    |> ignore
                with e ->
                    failwithf "state \"%s\": %s" name e.Message)

        testCase "assertion 2: a kept row's button, the form and the consent controls reach update" (fun () ->
            (contactsLoaded ())
                .Interact(ViewMount.mountAndInteract, contactsTree, Drive.click contactMsgs.Manage)
                .AssertModelWith(
                    "the first row's Manage opens that contact",
                    fun m -> m.CurrentView = ExternalContactManagerUI.ContactDetail "c1"
                )
            |> ignore

            (contactsLoaded ())
                .Dispatch(ExternalContactManagerUI.StartAdd)
                .Interact(
                    ViewMount.mountAndInteract,
                    contactsTree,
                    fun host ->
                        Drive.typeInto contactMsgs.FieldDisplayName "Ada" host
                        Drive.leave contactMsgs.FieldDisplayName host
                )
                .AssertModelWith(
                    "the display name reaches the form on leaving it",
                    fun m -> m.Form |> Option.exists (fun f -> f.DisplayName = "Ada")
                )
            |> ignore

            (contactOpen ())
                .Interact(
                    ViewMount.mountAndInteract,
                    contactsTree,
                    Drive.edit
                        (contactMsgs.FieldChannel
                         + String.concat "" ExternalContactManagerUI.channelOptions)
                        "Sms"
                )
                .AssertModelWith("the channel reaches the draft", fun m -> m.OptIn.Channel = "Sms")
            |> ignore

            (contactOpen ())
                .Interact(ViewMount.mountAndInteract, contactsTree, Drive.click contactMsgs.WithdrawConsent)
                .AssertModelWith("a consent's withdrawal is asked for", fun m -> m.Busy)
            |> ignore

            (contactOpen ())
                .Interact(ViewMount.mountAndInteract, contactsTree, Drive.click contactMsgs.BackToList)
                .AssertModelWith(
                    "Back returns to the list",
                    fun m -> m.CurrentView = ExternalContactManagerUI.ContactList
                )
            |> ignore)

        // Four differences, all judged, and nothing else. (1) The contact list is
        // a table whose rows each hold a Manage button, where the Feliz page's
        // AG Grid draws two focusable grid regions and, under jsdom, no cell
        // renderer: its Manage buttons never render here. (2) Names: the Feliz
        // page's field labels were not associated with their inputs, which were
        // named by their placeholders; the islands' labels wrap them. (3) The
        // channel is a select its label wraps, named by the label and then its
        // options, where the Feliz select had no name. (4) The tab order, which
        // differs only by these. The other buttons and their order match.
        testCase "assertion 3: parity with the Feliz page, but for the judged differences" (fun () ->
            let m = contactMsgs

            let renamed =
                set [
                    m.PlaceholderDisplayName, m.FieldDisplayName
                    m.PlaceholderEmail, m.FieldEmail
                    m.PlaceholderPhone, m.FieldPhone
                    m.PlaceholderPhone, m.FieldWhatsApp
                    m.PlaceholderTags, m.FieldTags
                    m.PlaceholderNotes, m.FieldNotes
                    m.PlaceholderConsentSource, m.FieldConsentSource
                ]

            let placeholders = renamed |> Set.map fst
            let labels = renamed |> Set.map snd

            let judged (difference: TreeViewParity.Difference) =
                match difference with
                | TreeViewParity.TabOrderDiffers _ -> true
                | TreeViewParity.MissingFromTree s when s.Role = "textbox" ->
                    s.Name |> Option.exists placeholders.Contains
                | TreeViewParity.ExtraInTree s when s.Role = "textbox" -> s.Name |> Option.exists labels.Contains
                | TreeViewParity.ExtraInTree s when s.Role = "button" -> s.Name = Some m.Manage
                | _ -> selectRenamed m.FieldChannel difference

            for name, harness in contactsStates () do
                let outline view =
                    harness.RenderViewHtml(ViewMount.mount, view) |> TreeViewParity.ofHtml

                let skip (n: string option) = n = Some m.Manage

                Expect.equal
                    (buttonsOf skip (outline contactsTree))
                    (buttonsOf skip (outline contactsFeliz))
                    (sprintf "state \"%s\": the buttons, in tab order" name)

            let unjudged = [
                for name, harness in contactsStates () do
                    let found =
                        harness.CheckViewParity(ViewMount.mount, contactsFeliz, contactsTree)
                        |> List.filter (judged >> not)

                    if not found.IsEmpty then
                        sprintf "state \"%s\": %s" name (TreeViewParity.report found)
            ]

            Expect.isEmpty unjudged (String.concat "\n" unjudged))
    ]

// ─── The subject: the health monitor ─────────────────────────────────

let private healthMsgs = MessageCatalog.english.HealthMonitor

let private healthFeliz (model: HealthMonitorUI.Model) (dispatch: HealthMonitorUI.Msg -> unit) : Feliz.ReactElement =
    FelizReferenceViews.HealthMonitor.body healthMsgs model dispatch

/// The module's own tree, drawn as its deferred surface draws it.
let healthTree (model: HealthMonitorUI.Model) (dispatch: HealthMonitorUI.Msg -> unit) : Feliz.ReactElement =
    lowerTree dispatch (HealthMonitorTree.tree healthMsgs model)

let private healthAt =
    System.DateTime(2026, 11, 1, 12, 0, 0, System.DateTimeKind.Utc)

let private probe (name: string) (status: string) : HealthProbeView = {
    Name = name
    Kind = "Readiness"
    TimeoutMs = 5000
    Status = status
    Message = (if status = "Healthy" then "" else "slow to answer")
    ElapsedMs = 3L
}

let private snapshotOf (probes: HealthProbeView list) : HealthSnapshot = {
    GeneratedAt = healthAt
    Probes = probes
}

let private telemetry (missed: int) : JobSchedulerTelemetryView = {
    HasScheduler = true
    TickMissedCount60Min = missed
    LastDriftMs = (if missed > 0 then Some 1840L else None)
    LastTickMissedAt = (if missed > 0 then Some healthAt else None)
    GeneratedAt = healthAt
}

let private degraded: DegradedCapability = {
    Capability = "crypto-shred-cache-eviction"
    DegradedSince = System.DateTimeOffset(healthAt)
    Reason = "the eviction channel refused the connection"
    Impact = "a shredded key may stay readable in another silo's cache"
    Remediation = "check the channel's address and restart"
}

let private rollup (inWindow: int) : AIDenialRollup = {
    GeneratedAt = healthAt
    ScopeId = "helloworld"
    WindowMinutes = 60
    TotalDenialsAllTime = 7
    TotalDenialsInWindow = inWindow
    DenialsPerMinute = float inWindow / 60.0
    ByToolName = [
        {
            Key = "delete_team"
            Count = inWindow
        }
    ]
    ByActiveModule = [
        {
            Key = "_sdk.TeamManager"
            Count = inWindow
        }
    ]
    ByScopeId = [ { Key = "helloworld"; Count = inWindow } ]
    TopToolModulePairs = []
    RecentDenials = [
        {
            ToolName = "delete_team"
            ActiveModule = "_sdk.TeamManager"
            Reason = "not on the allowlist"
            OccurredAt = healthAt
        }
    ]
}

let healthInit () =
    fromUnitInit HealthMonitorUI.init HealthMonitorUI.update

let private outcome (name: string) (status: string) (message: string) : PreflightOutcomeView = {
    Name = name
    Status = status
    Message = message
    ElapsedMs = 9L
}

/// Both tabs read: two probes, three validators, no scheduler registered,
/// nothing degraded and no AI companion.
let healthLoaded () =
    (healthInit ())
        .Dispatch(
            HealthMonitorUI.LiveLoaded(
                Ok(snapshotOf [ probe "blob_storage" "Healthy"; probe "event_store" "Degraded" ])
            )
        )
        .Dispatch(
            HealthMonitorUI.SchedulerTelemetryLoaded(
                Ok {
                    telemetry 0 with
                        HasScheduler = false
                }
            )
        )
        .Dispatch(HealthMonitorUI.DegradedLoaded(Ok []))
        .Dispatch(HealthMonitorUI.AIDenialsLoaded(Ok None))
        .Dispatch(
            HealthMonitorUI.PreflightLoaded(
                Ok {
                    HasSnapshot = true
                    Outcomes = [
                        outcome "blob-storage" "Ok" ""
                        outcome "invite-email-capability" "Warning" "no directory companion"
                        outcome "secret-store" "Error" "unreachable"
                    ]
                }
            )
        )

/// The states the health monitor is held to, each reached through `update`.
let healthStates () = [
    "loading", healthInit ()
    "live health read", healthLoaded ()
    "a capability degraded, denials in the window, a missed tick",
    (healthLoaded ())
        .Dispatch(HealthMonitorUI.DegradedLoaded(Ok [ degraded ]))
        .Dispatch(HealthMonitorUI.AIDenialsLoaded(Ok(Some(rollup 3))))
        .Dispatch(HealthMonitorUI.SchedulerTelemetryLoaded(Ok(telemetry 2)))
    "a scheduler, no tick missed", (healthLoaded ()).Dispatch(HealthMonitorUI.SchedulerTelemetryLoaded(Ok(telemetry 0)))
    "denials recorded, the window quiet",
    (healthLoaded ()).Dispatch(HealthMonitorUI.AIDenialsLoaded(Ok(Some(rollup 0))))
    "a probe's status changed",
    (healthLoaded ())
        .Dispatch(
            HealthMonitorUI.LiveLoaded(Ok(snapshotOf [ probe "blob_storage" "Healthy"; probe "event_store" "Healthy" ]))
        )
    "no probes registered", (healthInit ()).Dispatch(HealthMonitorUI.LiveLoaded(Ok(snapshotOf [])))
    "the live read failed", (healthInit ()).Dispatch(HealthMonitorUI.LiveLoaded(Error "the probe sweep is unreachable"))
    "the preflight tab", (healthLoaded ()).Dispatch(HealthMonitorUI.SwitchTab HealthMonitorUI.PreflightTab)
    "no preflight snapshot",
    (healthInit ())
        .Dispatch(HealthMonitorUI.PreflightLoaded(Ok { HasSnapshot = false; Outcomes = [] }))
        .Dispatch(HealthMonitorUI.SwitchTab HealthMonitorUI.PreflightTab)
    "the preflight read failed",
    (healthInit ())
        .Dispatch(HealthMonitorUI.PreflightLoaded(Error "the snapshot is unreachable"))
        .Dispatch(HealthMonitorUI.SwitchTab HealthMonitorUI.PreflightTab)
]

/// Parity over `states`: no difference but the `judged` ones, and the same
/// buttons in the same tab order, but for those `skip` leaves out. A state
/// named in `felizThrows` is one the Feliz page could not draw: it is held to
/// throwing there (a custom date format Fable does not print), and the tree,
/// which formats the instant from its components, is held to the floor and to
/// behaviour in it instead.
let private atParity
    states
    feliz
    tree
    (felizThrows: string list)
    (judged: TreeViewParity.Difference -> bool)
    (skip: string option -> bool)
    =
    let compared =
        states |> List.filter (fun (name, _) -> not (List.contains name felizThrows))

    for name, (harness: ModuleHarness<_, _>) in states do
        if List.contains name felizThrows then
            let message =
                expectThrows
                    (fun () -> harness.RenderViewHtml(ViewMount.mount, feliz) |> ignore)
                    (sprintf "state \"%s\": the Feliz page was expected to throw" name)

            Expect.isTrue (message.Contains "Date print format") (sprintf "state \"%s\": why the Feliz page threw" name)

    let unjudged = [
        for name, (harness: ModuleHarness<_, _>) in compared do
            let found =
                harness.CheckViewParity(ViewMount.mount, feliz, tree)
                |> List.filter (judged >> not)

            if not found.IsEmpty then
                sprintf "state \"%s\": %s" name (TreeViewParity.report found)
    ]

    Expect.isEmpty unjudged (String.concat "\n" unjudged)

    for name, (harness: ModuleHarness<_, _>) in compared do
        let outline view =
            harness.RenderViewHtml(ViewMount.mount, view) |> TreeViewParity.ofHtml

        Expect.equal
            (buttonsOf skip (outline tree))
            (buttonsOf skip (outline feliz))
            (sprintf "state \"%s\": the buttons, in tab order" name)

/// Every state's tree at the Strict floor.
let private strictInEvery states tree =
    for name, (harness: ModuleHarness<_, _>) in states do
        try
            harness.AssertAccessibleView(ViewMount.mount, tree, Accessibility.Strict)
            |> ignore
        with e ->
            failwithf "state \"%s\": %s" name e.Message

let private tabOrderOnly (difference: TreeViewParity.Difference) =
    match difference with
    | TreeViewParity.TabOrderDiffers _ -> true
    | _ -> false

let private healthTests =
    testList "the health monitor as a tree" [

        testCase "assertion 1: every state's tree passes the Strict floor" (fun () ->
            strictInEvery (healthStates ()) healthTree)

        testCase "assertion 2: the refreshes and the tabs reach update" (fun () ->
            (healthLoaded ())
                .Interact(ViewMount.mountAndInteract, healthTree, Drive.click healthMsgs.Refresh)
                .AssertModelWith("Refresh reads the live tab again", fun m -> Remote.isLoading m.Live)
            |> ignore

            (healthLoaded ())
                .Interact(ViewMount.mountAndInteract, healthTree, Drive.click healthMsgs.PreflightTab)
                .AssertModelWith("the preflight tab opens", fun m -> m.ActiveTab = HealthMonitorUI.PreflightTab)
            |> ignore

            (healthLoaded ())
                .Dispatch(HealthMonitorUI.SwitchTab HealthMonitorUI.PreflightTab)
                .Interact(ViewMount.mountAndInteract, healthTree, Drive.click healthMsgs.Refetch)
                .AssertModelWith("Re-fetch reads the preflight snapshot again", fun m -> Remote.isLoading m.Preflight)
            |> ignore)

        // One difference, judged, and nothing else: the tabs are one tab stop
        // with the active tab's panel after it. The buttons and their order
        // match. The two states with a scheduler are not compared: the Feliz
        // page threw drawing its scheduler card ("u" is not a date format
        // Fable prints), which the tree draws from the instant's components.
        testCase "assertion 3: parity with the Feliz page, but for the judged differences" (fun () ->
            atParity
                (healthStates ())
                healthFeliz
                healthTree
                [
                    "a capability degraded, denials in the window, a missed tick"
                    "a scheduler, no tick missed"
                ]
                tabOrderOnly
                (fun _ -> false))
    ]

// ─── The subject: background jobs ────────────────────────────────────

let private jobsMsgs = MessageCatalog.english.JobsAdmin

let private jobsFeliz (model: JobsAdminUI.Model) (dispatch: JobsAdminUI.Msg -> unit) : Feliz.ReactElement =
    FelizReferenceViews.JobsAdmin.body jobsMsgs model dispatch

/// The module's own tree, drawn as its deferred surface draws it.
let jobsTree (model: JobsAdminUI.Model) (dispatch: JobsAdminUI.Msg -> unit) : Feliz.ReactElement =
    lowerTree dispatch (JobsAdminTree.tree jobsMsgs model)

let private jobsAt = System.DateTime(2026, 11, 1, 6, 0, 0, System.DateTimeKind.Utc)

let jobDefinition (n: int) (handler: string) : JobDefinition = {
    JobId = System.Guid(sprintf "00000000-0000-0000-0000-%012d" n)
    ScopeId = "helloworld"
    Handler = handler
    Payload = "{}"
    Trigger = CronTrigger "0 6 * * *"
    Idempotency = None
    RetryPolicy = JobRetryPolicy.defaults
    ShardKey = None
    Precision = JobPrecision.Minute
    Status = JobStatus.Active
    CreatedAt = jobsAt.AddDays -10.0
    CreatedBy = "dev-admin"
    NextRunAt = Some(jobsAt.AddDays 1.0)
    LastRunAt = None
    LastRunStatus = None
    LastRunError = None
    ConsecutiveFailures = 0
    Tags = Map.empty
    LastRunWarning = None
}

/// A job that failed for good, one that succeeded with a warning, and a
/// disabled one that never ran.
let private jobsList () = [
    {
        jobDefinition 1 "daily-narrative" with
            LastRunAt = Some jobsAt
            LastRunStatus = Some JobRunStatus.DeadLettered
            LastRunError = Some "no usable AI provider"
    }
    {
        jobDefinition 2 "nightly-rollup" with
            LastRunAt = Some jobsAt
            LastRunStatus = Some JobRunStatus.Succeeded
            LastRunWarning = Some "two rows skipped"
    }
    {
        jobDefinition 3 "weekly-digest" with
            Status = JobStatus.Disabled
    }
]

let private attempt (job: JobDefinition) (n: int) (status: JobRunStatus) : JobRun = {
    RunId = System.Guid(sprintf "00000000-0000-0000-0001-%012d" n)
    JobId = job.JobId
    ScopeId = job.ScopeId
    Attempt = n
    StartedAt = jobsAt.AddMinutes(float -n)
    CompletedAt = Some(jobsAt.AddMinutes(float -n).AddSeconds 2.0)
    Status = status
    Error =
        (if status = JobRunStatus.Succeeded then
             None
         else
             Some "provider refused")
    DurationMs = Some 2000L
    ExternalHandle = None
    Warning = None
}

let jobsInit () =
    fromUnitInit JobsAdminUI.init JobsAdminUI.update

/// Three jobs and each one's attempts read.
let jobsLoaded () =
    let jobs = jobsList ()

    jobs
    |> List.fold
        (fun (harness: ModuleHarness<JobsAdminUI.Model, JobsAdminUI.Msg>) job ->
            harness.Dispatch(
                JobsAdminUI.AttemptsLoaded(
                    job.JobId,
                    Ok [
                        attempt job 1 (job.LastRunStatus |> Option.defaultValue JobRunStatus.Succeeded)
                    ]
                )
            ))
        ((jobsInit ()).Dispatch(JobsAdminUI.JobsLoaded(Ok jobs)))

let private firstJobId = (jobDefinition 1 "daily-narrative").JobId

let private confirmed () =
    (jobsLoaded ()).Dispatch(JobsAdminUI.RerunRequested firstJobId).Dispatch(JobsAdminUI.RerunConfirmed firstJobId)

/// The states the jobs admin is held to, each reached through `update`.
let jobsStates () = [
    "loading", jobsInit ()
    "no jobs", (jobsInit ()).Dispatch(JobsAdminUI.JobsLoaded(Ok []))
    "three jobs read", jobsLoaded ()
    "a job's attempts open, read",
    (jobsLoaded ())
        .Dispatch(JobsAdminUI.ToggleAttempts firstJobId)
        .Dispatch(
            JobsAdminUI.AttemptsLoaded(
                firstJobId,
                Ok [
                    attempt (List.head (jobsList ())) 1 JobRunStatus.DeadLettered
                    attempt (List.head (jobsList ())) 2 JobRunStatus.Failed
                ]
            )
        )
    "a job's attempts open, in flight", (jobsLoaded ()).Dispatch(JobsAdminUI.ToggleAttempts firstJobId)
    "a re-run awaiting confirmation", (jobsLoaded ()).Dispatch(JobsAdminUI.RerunRequested firstJobId)
    "a re-run in flight", confirmed ()
    "a re-run refused", (confirmed ()).Dispatch(JobsAdminUI.RerunAccepted(firstJobId, Error "the job is locked"))
    "a poll failed",
    (confirmed ())
        .Dispatch(JobsAdminUI.RerunAccepted(firstJobId, Ok()))
        .Dispatch(JobsAdminUI.JobPolled(firstJobId, Error "the scheduler is unreachable"))
    "the list read failed", (jobsInit ()).Dispatch(JobsAdminUI.JobsLoaded(Error "the job store is unreachable"))
]

let private jobsTests =
    testList "the background-jobs admin as a tree" [

        testCase "assertion 1: every state's tree passes the Strict floor" (fun () ->
            strictInEvery (jobsStates ()) jobsTree)

        testCase "assertion 2: a kept row's toggle and re-run, and the confirmation, reach update" (fun () ->
            (jobsLoaded ())
                .Interact(ViewMount.mountAndInteract, jobsTree, Drive.click jobsMsgs.ShowAttempts)
                .AssertModelWith(
                    "the first row's toggle opens its attempts",
                    fun m -> m.Expanded = Set.singleton firstJobId
                )
            |> ignore

            (jobsLoaded ())
                .Interact(ViewMount.mountAndInteract, jobsTree, Drive.click jobsMsgs.RerunButton)
                .AssertModelWith("Re-run asks for confirmation", fun m -> m.Confirming = Some firstJobId)
            |> ignore

            (jobsLoaded ())
                .Dispatch(JobsAdminUI.RerunRequested firstJobId)
                .Interact(ViewMount.mountAndInteract, jobsTree, Drive.click jobsMsgs.RerunConfirm)
                .AssertModelWith(
                    "confirming starts the re-run",
                    fun m -> m.Reruns.TryFind firstJobId = Some JobsAdminUI.RerunProgress.Starting
                )
            |> ignore

            (jobsLoaded ())
                .Dispatch(JobsAdminUI.RerunRequested firstJobId)
                .Interact(ViewMount.mountAndInteract, jobsTree, Drive.click jobsMsgs.RerunCancel)
                .AssertModelWith("Cancel closes the confirmation", fun m -> m.Confirming.IsNone)
            |> ignore

            (jobsLoaded ())
                .Interact(ViewMount.mountAndInteract, jobsTree, Drive.click jobsMsgs.Refresh)
                .AssertModelWith("Refresh reads the list again", fun m -> Remote.isLoading m.Jobs)
            |> ignore)

        // Judged, and nothing else: the confirmation is the renderer's modal, a
        // dialog with a close control and its heading at level 2, where the
        // Feliz confirmation was a dialog headed at level 3; the tab order
        // differs by that close control alone. The other buttons and their
        // order match.
        testCase "assertion 3: parity with the Feliz page, but for the judged differences" (fun () ->
            let judged (difference: TreeViewParity.Difference) =
                match difference with
                | TreeViewParity.TabOrderDiffers _ -> true
                | TreeViewParity.MissingFromTree s
                | TreeViewParity.ExtraInTree s ->
                    s.Role = "dialog"
                    || (s.Role.StartsWith "heading" && s.Name = Some jobsMsgs.RerunButton)
                    || (s.Role = "button" && s.Name = Some "Close")

            atParity (jobsStates ()) jobsFeliz jobsTree [] judged ((=) (Some "Close")))
    ]

// ─── The subject: the service status board ───────────────────────────

let private statusMsgs = MessageCatalog.english.ServiceStatusBoard

let private statusFeliz
    (model: ServiceStatusBoardUI.Model)
    (dispatch: ServiceStatusBoardUI.Msg -> unit)
    : Feliz.ReactElement =
    FelizReferenceViews.ServiceStatusBoard.body statusMsgs model dispatch

/// The module's own tree, drawn as its deferred surface draws it.
let statusTree (model: ServiceStatusBoardUI.Model) (dispatch: ServiceStatusBoardUI.Msg -> unit) : Feliz.ReactElement =
    lowerTree dispatch (ServiceStatusBoardTree.tree statusMsgs model)

let private section (severity: StatusSeverity) (headline: string) (details: string list) : SectionSummary = {
    Disabled = false
    DisabledReason = ""
    Severity = severity
    Headline = headline
    Details = details
}

let private statusSnapshot () : ServiceStatusSnapshot =
    let health = section StatusSeverity.Ok "4 of 4 probes healthy" []

    let preflight =
        section StatusSeverity.Warn "56 of 60 validators ok" [ "Warning: invite-email-capability" ]

    let drift = section StatusSeverity.Ok "no drift" []

    let rateLimit = {
        section StatusSeverity.Ok "" [] with
            Disabled = true
            DisabledReason = "no rate limiter composed"
    }

    let jobQueue = section StatusSeverity.Ok "no job waiting" []

    let smoke =
        section StatusSeverity.Error "1 of 3 smoke tests failed" [ "login: timed out" ]

    {
        GeneratedAt = System.DateTime(2026, 11, 1, 12, 0, 0, System.DateTimeKind.Utc)
        Overall = ServiceStatusSnapshot.computeOverall health preflight drift rateLimit jobQueue smoke
        Health = health
        Preflight = preflight
        Drift = drift
        RateLimit = rateLimit
        JobQueue = jobQueue
        SmokeTest = smoke
    }

let statusInit () =
    fromUnitInit (fun () -> ServiceStatusBoardUI.init ClientModuleContext.empty) ServiceStatusBoardUI.update

let statusLoaded () =
    (statusInit ()).Dispatch(ServiceStatusBoardUI.SnapshotLoaded(Ok(statusSnapshot ())))

/// The states the status board is held to, each reached through `update`.
let statusStates () = [
    "loading", statusInit ()
    "the board read", statusLoaded ()
    "a section collapsed",
    (statusLoaded ()).Dispatch(ServiceStatusBoardUI.ToggleCollapsed ServiceStatusSnapshot.PreflightSection)
    "a section refreshing",
    (statusLoaded ()).Dispatch(ServiceStatusBoardUI.RefreshSection ServiceStatusSnapshot.HealthSection)
    "a section's severity changed",
    (statusLoaded ())
        .Dispatch(ServiceStatusBoardUI.RefreshSection ServiceStatusSnapshot.HealthSection)
        .Dispatch(
            ServiceStatusBoardUI.SectionLoaded(
                ServiceStatusSnapshot.HealthSection,
                Ok(section StatusSeverity.Error "1 of 4 probes unhealthy" [ "event_store: unhealthy" ])
            )
        )
    "the board read failed",
    (statusInit ()).Dispatch(ServiceStatusBoardUI.SnapshotLoaded(Error "the board is unreachable"))
]

let private statusSections = [
    ServiceStatusSnapshot.HealthSection
    ServiceStatusSnapshot.PreflightSection
    ServiceStatusSnapshot.DriftSection
    ServiceStatusSnapshot.RateLimitSection
    ServiceStatusSnapshot.JobQueueSection
    ServiceStatusSnapshot.SmokeTestSection
]

let private statusTests =
    testList "the service status board as a tree" [

        testCase "assertion 1: every state's tree passes the Strict floor" (fun () ->
            strictInEvery (statusStates ()) statusTree)

        testCase "assertion 2: a section's toggle and refresh, and Refresh all, reach update" (fun () ->
            (statusLoaded ())
                .Interact(ViewMount.mountAndInteract, statusTree, Drive.click ServiceStatusSnapshot.HealthSection)
                .AssertModelWith(
                    "the section's toggle collapses it",
                    fun m -> m.Collapsed = Set.singleton ServiceStatusSnapshot.HealthSection
                )
            |> ignore

            (statusLoaded ())
                .Interact(ViewMount.mountAndInteract, statusTree, Drive.click statusMsgs.Refresh)
                .AssertModelWith(
                    "the first section's refresh re-reads that section",
                    fun m -> m.Refreshing = Set.singleton ServiceStatusSnapshot.HealthSection
                )
            |> ignore

            (statusLoaded ())
                .Interact(ViewMount.mountAndInteract, statusTree, Drive.click statusMsgs.RefreshAll)
                .AssertModelWith("Refresh all reads the board again", fun m -> Remote.isLoading m.Snapshot)
            |> ignore)

        // Nothing judged but the tab order. Only the board's loading and failed
        // states are compared: the Feliz page threw drawing a read board's
        // generated instant ("u" is not a date format Fable prints), so it never
        // drew a section; the tree, which formats the instant from its
        // components, is held to the floor and to behaviour in every read state.
        testCase "assertion 3: parity with the Feliz page, but for the judged differences" (fun () ->
            atParity
                (statusStates ())
                statusFeliz
                statusTree
                [
                    "the board read"
                    "a section collapsed"
                    "a section refreshing"
                    "a section's severity changed"
                ]
                tabOrderOnly
                (fun _ -> false))
    ]

// ─── The subject: data migrations ────────────────────────────────────

let private migrationMsgs = MessageCatalog.english.MigrationStatus

let private migrationFeliz
    (model: MigrationStatusUI.Model)
    (dispatch: MigrationStatusUI.Msg -> unit)
    : Feliz.ReactElement =
    FelizReferenceViews.MigrationStatus.body migrationMsgs model dispatch

/// The module's own tree, drawn as its deferred surface draws it.
let migrationTree (model: MigrationStatusUI.Model) (dispatch: MigrationStatusUI.Msg -> unit) : Feliz.ReactElement =
    lowerTree dispatch (MigrationStatusTree.tree migrationMsgs model)

let private dataType (id: string) (version: int) (problem: string option) : MigrationDataTypeInfo = {
    DataTypeId = id
    DisplayName = id + " data"
    CurrentVersion = version
    ChainProblem = problem
}

let migrationStatus (id: string) (state: MigrationRunState) (failed: int) : MigrationStatus = {
    TeamId = "helloworld"
    DataTypeId = id
    TargetVersion = 3
    TotalObjects = 120
    MigratedObjects = 47
    AlreadyCurrentObjects = 10
    FailedObjects = failed
    State = state
    StartedAt = None
    CompletedAt = None
    Failures = [
        for i in 1..failed ->
            {
                ObjectId = sprintf "obj-%d" i
                AtVersion = 2
                Error = "a field the migrator did not expect"
                OccurredAt = System.DateTime(2026, 11, 1, 12, 0, 0, System.DateTimeKind.Utc)
            }
    ]
}

let migrationInit () =
    fromUnitInit MigrationStatusUI.init MigrationStatusUI.update

/// Three data types: one never migrated, one with failures, one whose chain
/// is incomplete.
let migrationLoaded () =
    (migrationInit ())
        .Dispatch(
            MigrationStatusUI.DataTypesLoaded(
                Ok [
                    dataType "media" 3 None
                    dataType "forecast" 3 None
                    dataType "survey" 4 (Some "no migrator from V2 to V3")
                ]
            )
        )
        .Dispatch(MigrationStatusUI.StatusesLoaded [ migrationStatus "forecast" MigrationCompleteWithFailures 2 ])

/// The states the data-migration admin is held to, each reached through `update`.
let migrationStates () = [
    "loading", migrationInit ()
    "no data types", (migrationInit ()).Dispatch(MigrationStatusUI.DataTypesLoaded(Ok []))
    "three data types read", migrationLoaded ()
    "a type's failures open", (migrationLoaded ()).Dispatch(MigrationStatusUI.ToggleFailures "forecast")
    "a migration in flight", (migrationLoaded ()).Dispatch(MigrationStatusUI.TriggerClicked "media")
    "a migration under way",
    (migrationLoaded ())
        .Dispatch(MigrationStatusUI.TriggerClicked "media")
        .Dispatch(MigrationStatusUI.TriggerComplete("media", Ok(migrationStatus "media" MigrationInProgress 0)))
    "a migration refused",
    (migrationLoaded ())
        .Dispatch(MigrationStatusUI.TriggerClicked "media")
        .Dispatch(MigrationStatusUI.TriggerComplete("media", Error "migrations require a persistent scope"))
    "the read failed",
    (migrationInit ()).Dispatch(MigrationStatusUI.DataTypesLoaded(Error "the registry is unreachable"))
]

let private migrationTests =
    testList "the data-migration admin as a tree" [

        testCase "assertion 1: every state's tree passes the Strict floor" (fun () ->
            strictInEvery (migrationStates ()) migrationTree)

        testCase "assertion 2: a kept row's trigger and failures toggle, and the notice, reach update" (fun () ->
            (migrationLoaded ())
                .Interact(ViewMount.mountAndInteract, migrationTree, Drive.click migrationMsgs.MigrateNow)
                .AssertModelWith(
                    "Migrate now starts the first type's pass",
                    fun m -> m.Triggering = Set.singleton "media"
                )
            |> ignore

            (migrationLoaded ())
                .Interact(ViewMount.mountAndInteract, migrationTree, Drive.click (migrationMsgs.FailuresButton 2))
                .AssertModelWith(
                    "the failures toggle opens that type's log",
                    fun m -> m.ExpandedFailures = Some "forecast"
                )
            |> ignore

            (migrationLoaded ())
                .Dispatch(MigrationStatusUI.TriggerClicked "media")
                .Dispatch(MigrationStatusUI.TriggerComplete("media", Error "refused"))
                .Interact(ViewMount.mountAndInteract, migrationTree, Drive.click migrationMsgs.Dismiss)
                .AssertModelWith("Dismiss clears the notice", fun m -> m.LastError.IsNone)
            |> ignore)

        testCase "assertion 3: parity with the Feliz page, but for the judged differences" (fun () ->
            atParity (migrationStates ()) migrationFeliz migrationTree [] tabOrderOnly (fun _ -> false))
    ]

// ─── What a dispatch constructs ──────────────────────────────────────

/// Of the nodes of `after`, how many are new objects, built for this dispatch,
/// and how many there are in all. A node that is the same object as one of
/// `before`'s was served from a `Keep`, and so is everything under it.
let constructed (before: Node<'Msg>) (after: Node<'Msg>) : int * int =
    let previous = System.Collections.Generic.HashSet<obj>(HashIdentity.Reference)

    let rec mark (node: Node<'Msg>) =
        previous.Add(box node) |> ignore
        Fuaran.UI.Generated.children node |> List.iter mark

    mark before

    let rec count (node: Node<'Msg>) : int * int =
        let below = Fuaran.UI.Generated.children node |> List.map count
        let size = 1 + List.sumBy snd below

        if previous.Contains(box node) then
            0, size
        else
            1 + List.sumBy fst below, size

    count after

/// A dispatch through the module's real `update`, and the tree before and
/// after it.
let private across (tree: 'Model -> Node<'Msg>) (harness: ModuleHarness<'Model, 'Msg>) (msg: 'Msg) =
    let before = tree harness.Model
    let after = tree (harness.Dispatch msg).Model
    constructed before after

/// How many nodes of `root` are rows of a table (`role="row"`) whose id
/// starts with `prefix`: the records a page holds, whatever each row shows.
let private rowsOf (prefix: string) (root: Node<'Msg>) : int =
    let rec count (node: Node<'Msg>) : int =
        let own =
            match node.Accessibility with
            | Some a when
                a.Role = Some(Fuaran.UI.HostPrelude.AriaRole.Custom "row")
                && node.Id.StartsWith prefix
                ->
                1
            | _ -> 0

        own + (Fuaran.UI.Generated.children node |> List.sumBy count)

    count root

/// The node of `root` whose id is `id`.
let rec private findNode (id: string) (node: Node<'Msg>) : Node<'Msg> option =
    if node.Id = id then
        Some node
    else
        Fuaran.UI.Generated.children node |> List.tryPick (findNode id)

let private manyTeams (count: int) : TeamInfo list =
    List.init count (fun i -> team (sprintf "t%03d" i) (sprintf "Team %03d" i))

let private manySummaries (count: int) : TeamSummary list =
    List.init count (fun i -> summary (sprintf "t%03d" i) (sprintf "Team %03d" i) (i % 2 = 1))

let private renamed (index: int) (rows: 'Row list) (rename: 'Row -> 'Row) =
    rows |> List.mapi (fun i row -> if i = index then rename row else row)

let private constructedTests =
    testList "a dispatch constructs the subtree it changed" [

        testCase "the team manager: one team renamed among 200 builds that row" (fun () ->
            let teams = manyTeams 200
            let harness = (teamInit ()).Dispatch(TeamManagerUI.TeamsLoaded teams)

            let built, total =
                across
                    (fun m -> TeamManagerTree.tree msgs m ignore)
                    harness
                    (TeamManagerUI.TeamsLoaded(renamed 7 teams (fun t -> { t with Name = "Renamed" })))

            let rowSize, _ =
                across
                    (fun m -> TeamManagerTree.tree msgs m ignore)
                    (teamInit ())
                    (TeamManagerUI.TeamsLoaded [ team "x" "X" ])

            Expect.isTrue (built <= rowSize) (sprintf "built %d of %d (a one-row page builds %d)" built total rowSize)
            Expect.isTrue (total > 1000) "the page holds the 200 rows")

        testCase "the team manager: the list loaded again with one team renamed builds that row" (fun () ->
            // A server response's records are all new objects; `update` keeps
            // the ones that say what the page already holds.
            let harness = (teamInit ()).Dispatch(TeamManagerUI.TeamsLoaded(manyTeams 200))

            let built, total =
                across
                    (fun m -> TeamManagerTree.tree msgs m ignore)
                    harness
                    (TeamManagerUI.TeamsLoaded(renamed 7 (manyTeams 200) (fun t -> { t with Name = "Renamed" })))

            let rowSize, _ =
                across
                    (fun m -> TeamManagerTree.tree msgs m ignore)
                    (teamInit ())
                    (TeamManagerUI.TeamsLoaded [ team "x" "X" ])

            Expect.isTrue (built <= rowSize) (sprintf "built %d of %d (a one-row page builds %d)" built total rowSize)
            Expect.isTrue (total > 1000) "the page holds the 200 rows")

        testCase "the visibility editor: the rule's kind rebuilds its own section" (fun () ->
            let harness = visibilityLoaded ()

            let built, total =
                across
                    (fun m -> ModuleVisibilityAdminTree.tree visibilityMsgs m ignore)
                    harness
                    (ModuleVisibilityAdminUI.SetKind(
                        if harness.Model.Kind = ModuleVisibilityAdminUI.RuleKind.Allow then
                            ModuleVisibilityAdminUI.RuleKind.Deny
                        else
                            ModuleVisibilityAdminUI.RuleKind.Allow
                    ))

            let resolvedKept, _ =
                across
                    (fun m -> ModuleVisibilityAdminTree.tree visibilityMsgs m ignore)
                    harness
                    ModuleVisibilityAdminUI.DismissStatus


            Expect.isTrue
                (resolvedKept < built)
                (sprintf
                    "a dispatch that moves nothing shown builds less (%d) than one that moves the kind"
                    resolvedKept)

            Expect.isTrue (built < total) (sprintf "built %d of %d" built total))

        testCase "Platform Management: one team renamed among 200 builds that row" (fun () ->
            let teams = manySummaries 200

            let harness =
                (adminInit ())
                    .Dispatch(PlatformAdminUI.SwitchTab PlatformAdminUI.TeamsTab)
                    .Dispatch(PlatformAdminUI.TeamsLoaded(Ok teams))

            let tree m =
                PlatformAdminTree.tree adminMsgs widgetMsgs PublicUtilityConfig.defaults (fun () -> operator) m ignore

            let built, total =
                across
                    tree
                    harness
                    (PlatformAdminUI.TeamsLoaded(Ok(renamed 7 teams (fun t -> { t with Name = "Team 007b" }))))

            let oneRow, _ =
                across
                    tree
                    ((adminInit ()).Dispatch(PlatformAdminUI.SwitchTab PlatformAdminUI.TeamsTab))
                    (PlatformAdminUI.TeamsLoaded(Ok [ { teams[7] with Name = "Team 007b" } ]))

            Expect.isTrue (built < 80) (sprintf "built %d of %d" built total)

            Expect.isTrue (built <= oneRow) (sprintf "built %d of %d (a one-row page builds %d)" built total oneRow)

            Expect.equal (rowsOf "team-" (tree harness.Model)) 200 "the page holds the 200 rows")

        testCase "session security: the list loaded again with one session renamed builds that row" (fun () ->
            let many () =
                List.init 100 (fun i -> session (sprintf "s%03d" i) (sprintf "Device %03d" i) false)

            let tree m =
                SessionSecurityTree.tree sessionMsgs sessionNow m

            let harness =
                (sessionInit ()).Dispatch(SessionSecurityUI.SessionsLoaded(Ok(many ())))

            let built, total =
                across
                    tree
                    harness
                    (SessionSecurityUI.SessionsLoaded(
                        Ok(renamed 7 (many ()) (fun s -> { s with DeviceDescriptor = "Renamed" }))
                    ))

            let oneRow, _ =
                across tree (sessionInit ()) (SessionSecurityUI.SessionsLoaded(Ok [ session "x" "X" false ]))

            Expect.isTrue (built <= oneRow) (sprintf "built %d of %d (a one-row page builds %d)" built total oneRow)
            Expect.isTrue (total > 500) (sprintf "the page holds the 100 rows (%d nodes)" total))

        testCase "platform users: the list loaded again with one principal changed builds that row" (fun () ->
            let many () =
                List.init 100 (fun i -> principal (sprintf "u%03d" i) (i % 3))

            let tree m =
                PlatformUsersTree.tree usersMsgs m ignore

            let harness = (usersInit ()).Dispatch(PlatformUsersUI.PrincipalsLoaded(Ok(many ())))

            let built, total =
                across
                    tree
                    harness
                    (PlatformUsersUI.PrincipalsLoaded(
                        Ok(
                            renamed 7 (many ()) (fun p -> {
                                p with
                                    HasUserScopeData = not p.HasUserScopeData
                            })
                        )
                    ))

            // A one-row page holding the changed record, so its badges match.
            let oneRow, _ =
                across
                    tree
                    (usersInit ())
                    (PlatformUsersUI.PrincipalsLoaded(
                        Ok [
                            {
                                principal "u007" 1 with
                                    HasUserScopeData = true
                            }
                        ]
                    ))

            Expect.isTrue (built <= oneRow) (sprintf "built %d of %d (a one-row page builds %d)" built total oneRow)
            Expect.isTrue (total > 1000) (sprintf "the page holds the 100 rows (%d nodes)" total))

        testCase "the permissions admin: a member's override toggle keeps the member list" (fun () ->
            let tree m =
                PermissionsAdminTree.tree permissionsMsgs (fun () -> permissionsCaller) m ignore

            let harness =
                (permissionsLoaded ())
                    .Dispatch(PermissionsAdminUI.SwitchTab PermissionsAdminUI.MembersTab)
                    .Dispatch(PermissionsAdminUI.SelectMember "u2")

            let before = tree harness.Model

            let after =
                tree
                    (harness.Dispatch(PermissionsAdminUI.ToggleMemberPermission("u2", "alpha", ModulePermission.Admin)))
                        .Model

            let built, total = constructed before after

            let rec find (id: string) (node: Node<PermissionsAdminUI.Msg>) : Node<PermissionsAdminUI.Msg> option =
                if node.Id = id then
                    Some node
                else
                    Fuaran.UI.Generated.children node |> List.tryPick (find id)

            let kept =
                match find "member-u1" before, find "member-u1" after with
                | Some a, Some b -> obj.ReferenceEquals(a, b)
                | _ -> false

            Expect.isTrue kept "the member rows are served again"
            Expect.isTrue (built < total) (sprintf "built %d of %d" built total))
        testCase "the audit trail: opening one row among 100 builds that row" (fun () ->
            let page = {
                Events = List.init 100 auditEvent
                NextCursor = None
                MatchedCount = 100
            }

            let tree m = AuditLogTree.tree auditMsgs m ignore
            let harness = (auditInit ()).Dispatch(AuditLogUI.PageLoaded(Ok page))

            let built, total = across tree harness (AuditLogUI.ToggleDetail((auditEvent 7).Id))

            // A one-row page with that row open.
            let oneRow, _ =
                across
                    tree
                    ((auditInit ()).Dispatch(AuditLogUI.PageLoaded(Ok { page with Events = [ auditEvent 7 ] })))
                    (AuditLogUI.ToggleDetail((auditEvent 7).Id))

            Expect.isTrue (built <= oneRow) (sprintf "built %d of %d (a one-row page builds %d)" built total oneRow)
            Expect.isTrue (total > 1000) (sprintf "the page holds the 100 rows (%d nodes)" total))

        testCase "external contacts: the list loaded again with one contact changed builds that row" (fun () ->
            let many () =
                List.init 100 (fun i -> contact (sprintf "c%03d" i) (sprintf "Contact %03d" i) (i % 2 = 0))

            let tree m =
                ExternalContactManagerTree.tree contactMsgs m ignore

            let harness =
                (contactsInit ()).Dispatch(ExternalContactManagerUI.ContactsLoaded(Ok(many ())))

            let built, total =
                across
                    tree
                    harness
                    (ExternalContactManagerUI.ContactsLoaded(
                        Ok(renamed 7 (many ()) (fun c -> { c with DisplayName = "Renamed" }))
                    ))

            let oneRow, _ =
                across
                    tree
                    (contactsInit ())
                    (ExternalContactManagerUI.ContactsLoaded(
                        Ok [
                            {
                                contact "c007" "Renamed" false with
                                    Version = 1
                            }
                        ]
                    ))

            Expect.isTrue (built <= oneRow) (sprintf "built %d of %d (a one-row page builds %d)" built total oneRow)
            Expect.isTrue (total > 1000) (sprintf "the page holds the 100 rows (%d nodes)" total))

        testCase "data subject requests: choosing a policy keeps the pending preview" (fun () ->
            let tree m =
                DataSubjectRequestAdminTree.tree dsrMsgs m ignore

            let harness = dsrPreviewed ()
            let before = tree harness.Model

            let after =
                tree
                    (harness.Dispatch(DataSubjectRequestAdminUI.SetOverridePolicy(Some ErasurePolicy.HardDelete)))
                        .Model

            let rec find (id: string) (node: Node<DataSubjectRequestAdminUI.Msg>) =
                if node.Id = id then
                    Some node
                else
                    Fuaran.UI.Generated.children node |> List.tryPick (find id)

            let kept =
                match find "dsr-preview" before, find "dsr-preview" after with
                | Some a, Some b -> obj.ReferenceEquals(a, b)
                | _ -> false

            Expect.isTrue kept "the preview panel is served again")

        testCase "the health monitor: the degraded set arriving serves every probe row again" (fun () ->
            let tree m = HealthMonitorTree.tree healthMsgs m
            let harness = healthLoaded ()
            let before = tree harness.Model

            let after =
                tree (harness.Dispatch(HealthMonitorUI.DegradedLoaded(Ok [ degraded ]))).Model

            for id in [ "health-probe-blob_storage"; "health-probe-event_store" ] do
                let kept =
                    match findNode id before, findNode id after with
                    | Some a, Some b -> obj.ReferenceEquals(a, b)
                    | _ -> false

                Expect.isTrue kept (id + " is served again"))

        testCase "background jobs: one job's attempts arriving among 100 builds that row" (fun () ->
            let many = List.init 100 (fun i -> jobDefinition i (sprintf "job-%03d" i))
            let tree m = JobsAdminTree.tree jobsMsgs m
            let harness = (jobsInit ()).Dispatch(JobsAdminUI.JobsLoaded(Ok many))
            let seventh = (List.item 7 many).JobId

            let built, total = across tree harness (JobsAdminUI.AttemptsLoaded(seventh, Ok []))

            let oneRow, _ =
                across
                    tree
                    ((jobsInit ()).Dispatch(JobsAdminUI.JobsLoaded(Ok [ List.item 7 many ])))
                    (JobsAdminUI.AttemptsLoaded(seventh, Ok []))

            Expect.isTrue (built <= oneRow) (sprintf "built %d of %d (a one-row page builds %d)" built total oneRow)
            Expect.isTrue (total > 1000) (sprintf "the page holds the 100 rows (%d nodes)" total))

        testCase "the service status board: one section's answer serves the other five panels again" (fun () ->
            let tree m =
                ServiceStatusBoardTree.tree statusMsgs m

            let harness =
                (statusLoaded ()).Dispatch(ServiceStatusBoardUI.RefreshSection ServiceStatusSnapshot.HealthSection)

            let before = tree harness.Model

            let after =
                tree
                    (harness.Dispatch(
                        ServiceStatusBoardUI.SectionLoaded(
                            ServiceStatusSnapshot.HealthSection,
                            Ok(section StatusSeverity.Warn "3 of 4 probes healthy" [])
                        )
                    ))
                        .Model

            for name in statusSections do
                let id = "status-section-" + name

                let kept =
                    match findNode id before, findNode id after with
                    | Some a, Some b -> obj.ReferenceEquals(a, b)
                    | _ -> false

                Expect.equal
                    kept
                    (name <> ServiceStatusSnapshot.HealthSection)
                    (id + ": kept unless it is the one answered"))

        testCase "data migrations: one type's trigger answered among 100 builds that row" (fun () ->
            let many = List.init 100 (fun i -> dataType (sprintf "type-%03d" i) 3 None)

            let tree m =
                MigrationStatusTree.tree migrationMsgs m

            let harness =
                (migrationInit ())
                    .Dispatch(MigrationStatusUI.DataTypesLoaded(Ok many))
                    .Dispatch(MigrationStatusUI.TriggerClicked "type-007")

            let answer =
                MigrationStatusUI.TriggerComplete("type-007", Ok(migrationStatus "type-007" MigrationInProgress 0))

            let built, total = across tree harness answer

            let oneRow, _ =
                across
                    tree
                    ((migrationInit ())
                        .Dispatch(MigrationStatusUI.DataTypesLoaded(Ok [ List.item 7 many ]))
                        .Dispatch(MigrationStatusUI.TriggerClicked "type-007"))
                    answer

            Expect.isTrue (built <= oneRow) (sprintf "built %d of %d (a one-row page builds %d)" built total oneRow)
            Expect.isTrue (total > 1000) (sprintf "the page holds the 100 rows (%d nodes)" total))
    ]

// ─── The kit's own go-red proofs ─────────────────────────────────────

type private ToyModel = { Text: string; Saved: int }

type private ToyMsg =
    | SetText of string
    | Save
    | Cancel

let private toyInit () : ToyModel * Step<ToyMsg, RemoteEffect<ToyMsg>> list = { Text = ""; Saved = 0 }, []

let private toyUpdate (msg: ToyMsg) (model: ToyModel) : ToyModel * Step<ToyMsg, RemoteEffect<ToyMsg>> list =
    match msg with
    | SetText t -> { model with Text = t }, []
    | Save -> { model with Saved = model.Saved + 1 }, []
    | Cancel -> model, []

let private toyTree (label: string) (model: ToyModel) : Node<ToyMsg> =
    Nodes.Layout.column "toy" [
        Nodes.heading "toy-title" 2 "Notes"
        Nodes.Forms.form "toy-form" "Save" false Save [ Nodes.Forms.text "note" label model.Text SetText ]
        Nodes.Actions.secondary "toy-cancel" "Cancel" Cancel
    ]

let private actions (first: string * ToyMsg) (second: string * ToyMsg) : Node<ToyMsg> =
    Nodes.Layout.row "toy-actions" [
        Nodes.Actions.secondary "toy-first" (fst first) (snd first)
        Nodes.Actions.secondary "toy-second" (fst second) (snd second)
    ]

/// A Feliz view with Save then Cancel.
let private felizActions (_: ToyModel) (dispatch: ToyMsg -> unit) : Feliz.ReactElement =
    Feliz.Html.div [
        Feliz.prop.children [
            Feliz.Html.button [ Feliz.prop.text "Save"; Feliz.prop.onClick (fun _ -> dispatch Save) ]
            Feliz.Html.button [ Feliz.prop.text "Cancel"; Feliz.prop.onClick (fun _ -> dispatch Cancel) ]
        ]
    ]

let private goRedTests =
    testList "the kit's go-red proofs" [

        // Why the note is still an island: the renderer's local buffer flushed
        // on submit keeps the submit listener of every render of the buffer, so
        // a submit commits each value the field held, in order, the empty one
        // first. When this goes red the buffer can replace the island.
        testCase "the renderer's submit-flushed buffer commits every value the field held" (fun () ->
            let form (model: ToyModel) : Node<ToyMsg> =
                Fuaran.form "toy-form" {
                    Defaults.form with
                        Fields = [
                            {
                                Defaults.formField with
                                    Id = "note"
                                    Label = Nodes.literal "Note"
                                    Kind =
                                        FormFieldKind.textControl (
                                            binding.local
                                                (Binding.Static(Some model.Text))
                                                LocalFlushTrigger.OnSubmit
                                                (SetText >> Action.dispatch)
                                                None
                                                Ok
                                        )
                            }
                        ]
                        OnSubmit = Action.none
                        SubmitLabel = Nodes.literal "Save"
                }

            let committed =
                (fromUnitInit toyInit toyUpdate)
                    .Dispatched(
                        ViewMount.mountAndInteract,
                        lowered lowerTree form,
                        fun host ->
                            Drive.typeInto "Note" "a" host
                            Drive.typeInto "Note" "ab" host
                            Drive.submit host
                    )

            Expect.equal committed [ SetText ""; SetText "a"; SetText "ab" ] "one commit per value the buffer held")

        testCase "a tree missing a label fails the floor, naming the rule" (fun () ->
            let toy = fromUnitInit toyInit toyUpdate

            toy.AssertAccessibleView(ViewMount.mount, lowered lowerTree (toyTree "Note"), Accessibility.Strict)
            |> ignore

            let message =
                expectThrows
                    (fun () ->
                        toy.AssertAccessibleView(
                            ViewMount.mount,
                            lowered lowerTree (toyTree ""),
                            Accessibility.Strict
                        )
                        |> ignore)
                    "a tree whose only field has no label must fail AssertAccessibleView"

            Expect.isTrue
                (message.Contains "everyControlHasLabel")
                ("the failure names the label rule. Got: " + message))

        testCase "a dispatch that does not reach update fails Interact, by name" (fun () ->
            let toy = fromUnitInit toyInit toyUpdate
            let wired = lowered lowerTree (toyTree "Note")

            toy
                .Interact(ViewMount.mountAndInteract, wired, Drive.edit "Note" "hello")
                .AssertModelWith("the control: a wired field reaches update", fun m -> m.Text = "hello")
            |> ignore

            // The defect: the view lowers its tree with a dispatch that goes
            // nowhere, so the control renders and does nothing.
            let unwired: ToyModel -> (ToyMsg -> unit) -> Feliz.ReactElement =
                lowered (fun _ node -> lowerTree ignore node) (toyTree "Note")

            let message =
                expectThrows
                    (fun () ->
                        toy.Interact(ViewMount.mountAndInteract, unwired, Drive.edit "Note" "hello")
                        |> ignore)
                    "a field whose dispatch goes nowhere must fail Interact"

            Expect.isTrue
                (message.Contains "dispatched no message")
                ("the failure says nothing reached update. Got: " + message))

        testCase "a reordered tab stop fails parity, and nothing else differs" (fun () ->
            let toy = fromUnitInit toyInit toyUpdate
            let same = lowered lowerTree (fun _ -> actions ("Save", Save) ("Cancel", Cancel))
            let swapped = lowered lowerTree (fun _ -> actions ("Cancel", Cancel) ("Save", Save))

            Expect.isEmpty
                (toy.CheckViewParity(ViewMount.mount, felizActions, same))
                "the control: the same stops in the same order are at parity"

            match toy.CheckViewParity(ViewMount.mount, felizActions, swapped) with
            | [ TreeViewParity.TabOrderDiffers(view, tree) ] ->
                Expect.equal view [ "button \"Save\""; "button \"Cancel\"" ] "the view's order"
                Expect.equal tree [ "button \"Cancel\""; "button \"Save\"" ] "the tree's order"
            | other ->
                failwithf
                    "a swap of two stops is exactly one tab-order difference, got: %s"
                    (TreeViewParity.report other)

            expectThrows
                (fun () -> toy.AssertViewParity(ViewMount.mount, felizActions, swapped) |> ignore)
                "AssertViewParity must throw on a reordered stop"
            |> ignore)

        testCase "a renamed control fails parity on both sides of the diff" (fun () ->
            let toy = fromUnitInit toyInit toyUpdate

            let renamed =
                lowered lowerTree (fun _ -> actions ("Store", Save) ("Cancel", Cancel))

            let differences = toy.CheckViewParity(ViewMount.mount, felizActions, renamed)

            Expect.isTrue
                (differences
                 |> List.exists (function
                     | TreeViewParity.MissingFromTree s -> s.Name = Some "Save"
                     | _ -> false))
                "the view's Save is missing from the tree"

            Expect.isTrue
                (differences
                 |> List.exists (function
                     | TreeViewParity.ExtraInTree s -> s.Name = Some "Store"
                     | _ -> false))
                "the tree's Store is extra")
    ]

/// Names resolve the way a browser resolves them, so two renders with
/// different ids compare by what the user hears.
let private nameTests =
    testList "parity's accessible names" [

        testCase "a name through aria-labelledby is the referenced text, not the id" (fun () ->
            let a =
                TreeViewParity.ofHtml """<span id="x1">Email</span><input aria-labelledby="x1">"""

            let b = TreeViewParity.ofHtml """<label for="f">Email</label><input id="f">"""
            Expect.isEmpty (TreeViewParity.compare a b) "both name the textbox Email"
            Expect.equal (a.Stops |> List.map _.Name) [ Some "Email" ] "the resolved name")

        testCase "a wrapping label names its control; a placeholder is the last resort" (fun () ->
            let o =
                TreeViewParity.ofHtml """<label>Agree <input type="checkbox"></label><input placeholder="Search">"""

            Expect.equal
                (o.Stops |> List.map (fun s -> s.Role, s.Name))
                [ "checkbox", Some "Agree"; "textbox", Some "Search" ]
                "the names")

        testCase "tab order: positive tabindex first, disabled and -1 skipped" (fun () ->
            let o =
                TreeViewParity.ofHtml
                    """<button>a</button><button tabindex="2">b</button><button disabled>c</button><button tabindex="-1">d</button><button tabindex="1">e</button>"""

            Expect.equal (o.TabOrder |> List.map _.Name) [ Some "e"; Some "b"; Some "a" ] "the order")

        testCase "aria-hidden subtrees are not in the outline" (fun () ->
            let o =
                TreeViewParity.ofHtml """<div aria-hidden="true"><button>x</button></div><h2>Title</h2>"""

            Expect.equal (o.Stops |> List.map _.Role) [ "heading(2)" ] "only the heading")
    ]

let tests =
    testList "the migration kit: accessible, behaves, at parity" [
        subjectTests
        visibilityTests
        adminTests
        sessionTests
        permissionsTests
        usersTests
        auditTests
        dsrTests
        contactsTests
        healthTests
        jobsTests
        statusTests
        migrationTests
        constructedTests
        goRedTests
        nameTests
    ]