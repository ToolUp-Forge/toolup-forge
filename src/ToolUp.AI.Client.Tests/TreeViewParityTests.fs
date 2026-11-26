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

            let buttons (outline: TreeViewParity.Outline) =
                outline.TabOrder
                |> List.filter (fun stop -> stop.Role = "button" && stop.Name <> Some "Close")
                |> List.map _.Name

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

            Expect.isTrue (built < 80) (sprintf "built %d of %d" built total)
            Expect.isTrue (total > 4000) "the page holds the 200 rows")
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
        constructedTests
        goRedTests
        nameTests
    ]