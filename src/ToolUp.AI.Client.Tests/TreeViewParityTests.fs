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
// The subject is the team manager: its Feliz view as it ships, against its
// tree built on the node toolkit for the same `Model` and `Msg`. The tree
// is a fixture here until the view itself moves; it is then the module's
// own and this file points at it. After the subject, the kit's own go-red
// proofs: each assertion fails, by name, on the defect it exists for.
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
open ToolUp.Elmish
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

/// React's own change handler for `element`, called with `value`. The mount
/// installs jsdom after React has loaded, so a real `input` event takes
/// React's legacy path and never reaches `onChange`; the handler is the
/// wiring under test.
[<Emit("$0[Object.keys($0).find(k => k.startsWith('__reactProps'))].onChange({ target: { value: $1, valueAsNumber: Number($1), checked: $1 === 'true' }, currentTarget: { value: $1, valueAsNumber: Number($1), checked: $1 === 'true' } })")>]
let private invokeChange (element: obj) (value: string) : unit = jsNative

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

/// The view as it ships.
let private felizView (model: TeamManagerUI.Model) (dispatch: TeamManagerUI.Msg -> unit) : Feliz.ReactElement =
    match TeamManagerUI.builtIn.View with
    | BuiltInView.Page view ->
        match view model (RenderContext.create MessageCatalog.english dispatch) with
        | PageContent.FullWidth element -> element
        | other -> failwithf "the team manager renders a full-width page, not %A" other
    | BuiltInView.Pages _ -> failwith "the team manager renders one page"

/// The team manager as a tree: the spike's shape on the node toolkit, over
/// today's model. Every row id comes from the team's own id and every row
/// message carries it.
module private TeamTree =

    let private row (team: TeamInfo) (active: bool) : Node<TeamManagerUI.Msg> =
        let id = Nodes.Ids.keyed "team" team.TeamId

        Nodes.Layout.row id [
            Nodes.Layout.column (Nodes.Ids.child id "about") [
                Nodes.text (Nodes.Ids.child id "name") team.Name
                if active then
                    Nodes.text (Nodes.Ids.child id "active") msgs.ActiveBadge
                Nodes.text (Nodes.Ids.child id "id") (msgs.TeamIdLabel team.TeamId)
            ]
            Nodes.Layout.row (Nodes.Ids.child id "actions") [
                if not active then
                    Nodes.Actions.secondary
                        (Nodes.Ids.child id "switch")
                        msgs.Switch
                        (TeamManagerUI.SwitchActiveTeam team.TeamId)
                Nodes.Actions.secondary (Nodes.Ids.child id "manage") msgs.Manage (TeamManagerUI.SelectTeam team.TeamId)
            ]
        ]

    let private myTeams (model: TeamManagerUI.Model) : Node<TeamManagerUI.Msg> =
        Nodes.Panel.card "my-teams" msgs.MyTeamsPanel [
            if model.Teams.IsEmpty then
                Nodes.text "my-teams-none" msgs.NoTeamsYet
            else
                for team in model.Teams do
                    row team (model.ActiveTeamId = Some team.TeamId)
        ]

    let private teamName (model: TeamManagerUI.Model) (teamId: string) =
        model.Teams
        |> List.tryFind (fun t -> t.TeamId = teamId)
        |> Option.map _.Name
        |> Option.defaultValue teamId

    let private issueByEmail (state: TeamManagerUI.IssueByEmail.Model) : Node<TeamManagerUI.Msg> =
        let send msg = TeamManagerUI.IssueByEmailMsg msg

        Fuaran.modal "issue-by-email" {
            Defaults.modal with
                Open = Binding.Static(Some true)
                Heading = Some(Nodes.literal msgs.InviteByEmailHeading)
                OnDismiss = Some(Action.dispatch (send TeamManagerUI.IssueByEmail.Close))
                Children = [
                    Nodes.text "issue-by-email-help" msgs.InviteByEmailHelp
                    Nodes.Forms.form
                        "issue-by-email-form"
                        (if state.Submitting then
                             msgs.Issuing
                         else
                             msgs.IssueInvitation)
                        state.Submitting
                        (send TeamManagerUI.IssueByEmail.Submit)
                        [
                            Nodes.Forms.text "email" msgs.EmailPlaceholder state.Email (fun v ->
                                send (TeamManagerUI.IssueByEmail.SetEmail v))
                            |> Nodes.Forms.required
                            Nodes.Forms.number "expires" msgs.ExpiresInDays (float state.ExpiresInDays) (fun v ->
                                send (TeamManagerUI.IssueByEmail.SetExpiresInDays(int v)))
                        ]
                    match state.SubmitError with
                    | Some error -> Nodes.text "issue-by-email-error" error
                    | None -> ()
                    Nodes.Actions.secondary "issue-by-email-cancel" msgs.Cancel (send TeamManagerUI.IssueByEmail.Close)
                ]
        }

    let private pendingInvites (teamId: string) (model: TeamManagerUI.Model) : Node<TeamManagerUI.Msg> =
        Nodes.Layout.column "pending" [
            Nodes.Layout.row "pending-crumbs" [
                Nodes.Actions.button
                    "pending-back"
                    ButtonVariant.Tertiary
                    msgs.BreadcrumbMembers
                    false
                    (TeamManagerUI.SelectTeam teamId)
                Nodes.text "pending-team" (teamName model teamId)
            ]
            Nodes.Panel.card "pending-invites" msgs.PendingInvitesPanel [
                Nodes.Actions.primary "pending-invite" msgs.InviteByEmail (TeamManagerUI.OpenIssueByEmail teamId)
                match Map.tryFind teamId model.PendingByEmail with
                | None -> Nodes.text "pending-loading" msgs.PendingInvitesLoading
                | Some [] -> Nodes.text "pending-none" msgs.NoPendingInvites
                | Some rows ->
                    for email, _ in rows do
                        Nodes.Layout.row (Nodes.Ids.keyed "pending" email) [
                            Nodes.text (Nodes.Ids.keyed "pending-email" email) email
                            Nodes.Actions.secondary
                                (Nodes.Ids.keyed "pending-revoke" email)
                                msgs.RevokeInvite
                                (TeamManagerUI.OpenRevokeByEmail(teamId, email))
                        ]
            ]
            match model.IssueByEmail with
            | Some state when state.TeamId = teamId -> issueByEmail state
            | _ -> ()
        ]

    let private notice (model: TeamManagerUI.Model) : Node<TeamManagerUI.Msg> list =
        match model.Error with
        | Some(Notice.Problem text) -> [
            Nodes.Actions.banner "notice" ToneVariant.Critical text msgs.Dismiss TeamManagerUI.DismissError
          ]
        | Some(Notice.Info text) -> [
            Nodes.Actions.banner "notice" ToneVariant.Info text msgs.Dismiss TeamManagerUI.DismissError
          ]
        | None -> []

    /// The page: the notice above the current sub-view. Team details is not
    /// in the fixture; a model on it is not one this file asserts over.
    let tree (model: TeamManagerUI.Model) : Node<TeamManagerUI.Msg> =
        Nodes.Layout.column "team-manager" [
            yield! notice model
            match model.CurrentView with
            | TeamManagerUI.MyTeams -> myTeams model
            | TeamManagerUI.PendingInvites teamId -> pendingInvites teamId model
            | TeamManagerUI.TeamDetails teamId -> Nodes.text "team-details" teamId
        ]

let private treeView = lowered lowerTree TeamTree.tree

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
    testList "the migration kit: accessible, behaves, at parity" [ subjectTests; goRedTests; nameTests ]