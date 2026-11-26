// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The 200-row table `ClientBench` times, as Feliz and as a Fuaran-UI tree,
/// and the real module views that moved to a tree, each as the Feliz page it
/// replaced and as its shipped tree, in a module that imports nothing from
/// Node, so a browser can load it.
///
/// `ClientBench` times the Feliz view's construction under Node
/// (`viewPerDispatchUs`). `client-bench-browser.mjs` bundles this module for a
/// page and times one host dispatch of each table in a real browser: the view
/// rebuilt from a new model, React's commit, and a forced layout. That is the
/// figure a tree surface is judged on, because a tree's cost sits in commit as
/// well as construction, and Node sees only construction.
module ToolUp.AI.Client.Tests.ClientBenchTables

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Platform
open Toolup.UIToolkit
open Fuaran.MVU

type Row = { Id: int; Label: string; Value: int }

type BenchModel = { Rows: Row[]; Tick: int }

type BenchMsg =
    | Bump of index: int
    | Burst of count: int
    | Step
    | StartAsync
    | AsyncDone

/// A representative module-sized view: a table of rows, each with a text
/// cell, a value cell and a button carrying a dispatch closure.
let benchView (model: BenchModel) (dispatch: BenchMsg -> unit) : ReactElement =
    Html.div [
        prop.className "bench"
        prop.children [
            Html.h2 [ prop.text (sprintf "Tick %d" model.Tick) ]
            Html.table [
                Html.tbody [
                    for i in 0 .. model.Rows.Length - 1 do
                        let row = model.Rows[i]

                        Html.tr [
                            prop.key row.Id
                            prop.children [
                                Html.td [ prop.text row.Label ]
                                Html.td [ prop.text (string row.Value) ]
                                Html.td [ Html.button [ prop.text "+"; prop.onClick (fun _ -> dispatch (Bump i)) ] ]
                            ]
                        ]
                ]
            ]
        ]
    ]

/// The same rows as a tree, in the shape a settings surface authors: a heading
/// and, per row, the label and value as text and a button dispatching `Bump`.
let benchTree (model: BenchModel) : Fuaran.UI.Types.Node<BenchMsg> =
    Nodes.Layout.column "bench" [
        Nodes.heading "tick" 2 (sprintf "Tick %d" model.Tick)
        for i in 0 .. model.Rows.Length - 1 do
            let row = model.Rows[i]
            let id = "r" + string row.Id

            Nodes.Layout.row id [
                Nodes.text (id + "-label") row.Label
                Nodes.text (id + "-value") (string row.Value)
                Nodes.Actions.button (id + "-bump") Fuaran.UI.Types.ButtonVariant.Secondary "+" false (Bump i)
            ]
    ]

/// The tree drawn as a deferred surface's file draws it: built and rendered in
/// one render, through the renderer directly.
let benchTreeView (model: BenchModel) (dispatch: BenchMsg -> unit) : ReactElement =
    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps (TreeLowering.TreeHost.ofSources HostBindingSources.empty) dispatch (benchTree model)
    )

/// One row of the kept table, built once per row record: the toolkit's idiom,
/// every id derived from the row's identity and its message carrying the id.
let private keptRow =
    Keep.create (fun (row: Row) ->
        let id = Nodes.Ids.keyed "r" (string row.Id)

        Nodes.Layout.row id [
            Nodes.text (Nodes.Ids.child id "label") row.Label
            Nodes.text (Nodes.Ids.child id "value") (string row.Value)
            Nodes.Actions.button
                (Nodes.Ids.child id "bump")
                Fuaran.UI.Types.ButtonVariant.Secondary
                "+"
                false
                (Bump row.Id)
        ])

/// The same table with its rows kept: a dispatch that replaces one row record
/// rebuilds that row's node and the heading, and the renderer's subtree memo
/// serves every other row's element from the last render.
let benchKeptTree (model: BenchModel) : Fuaran.UI.Types.Node<BenchMsg> =
    Nodes.Layout.column "bench" [
        Nodes.heading "tick" 2 (sprintf "Tick %d" model.Tick)
        yield! Keep.list keptRow model.Rows
    ]

/// The kept table drawn as `benchTreeView` draws the rebuilt one.
let benchKeptTreeView (model: BenchModel) (dispatch: BenchMsg -> unit) : ReactElement =
    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps (TreeLowering.TreeHost.ofSources HostBindingSources.empty) dispatch (benchKeptTree model)
    )

/// The rows as boxes with each cell as `Markdown`: the text carrier the
/// toolkit used before the `Text` node.
let benchMarkdownTree (model: BenchModel) : Fuaran.UI.Types.Node<BenchMsg> =
    Nodes.Layout.column "bench" [
        Nodes.heading "tick" 2 (sprintf "Tick %d" model.Tick)
        for row in model.Rows do
            let id = "r" + string row.Id

            Nodes.Layout.row id [
                Fuaran.UI.Fuaran.markdown (id + "-label") row.Label
                Fuaran.UI.Fuaran.markdown (id + "-value") (string row.Value)
                Nodes.Actions.button (id + "-bump") Fuaran.UI.Types.ButtonVariant.Secondary "+" false (Bump row.Id)
            ]
    ]

/// The same rows as the vocabulary's table: a `DataGrid` whose third column is
/// a button per row, the row's identity in its message. The table form a
/// settings surface authors.
let benchGridTree (model: BenchModel) : Fuaran.UI.Types.Node<BenchMsg> =
    let cell (key: string) (row: Fuaran.UI.Types.Row) = string row[key]

    Nodes.Layout.column "bench" [
        Nodes.heading "tick" 2 (sprintf "Tick %d" model.Tick)
        Fuaran.UI.Fuaran.grid
            "bench-grid"
            (fun (row: Row) ->
                Map [
                    "id", box (string row.Id)
                    "label", box row.Label
                    "value", box (string row.Value)
                ])
            {
                Fuaran.UI.Defaults.grid with
                    Source = Fuaran.UI.Types.Binding.Static(Some(Seq.ofArray model.Rows))
                    RowKey = cell "id"
                    Columns = [
                        Fuaran.UI.Column.text "Label" (cell "label")
                        Fuaran.UI.Column.text "Value" (cell "value")
                        {
                            Fuaran.UI.Defaults.column with
                                Label = "Bump"
                                Kind =
                                    Fuaran.UI.Types.CellKind.Button(
                                        Fuaran.UI.Types.TextSource.Literal "+",
                                        fun row -> Fuaran.UI.Action.dispatch (Bump(int (cell "id" row)))
                                    )
                        }
                    ]
            }
    ]

/// The grid drawn as `benchTreeView` draws the boxes.
let benchGridTreeView (model: BenchModel) (dispatch: BenchMsg -> unit) : ReactElement =
    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps (TreeLowering.TreeHost.ofSources HostBindingSources.empty) dispatch (benchGridTree model)
    )

/// The boxes with `Markdown` cells drawn the same way.
let benchMarkdownTreeView (model: BenchModel) (dispatch: BenchMsg -> unit) : ReactElement =
    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps
            (TreeLowering.TreeHost.ofSources HostBindingSources.empty)
            dispatch
            (benchMarkdownTree model)
    )

/// The rows a run measures, deterministic so the two tables carry the same data.
let benchRows (count: int) : Row[] =
    Array.init count (fun i -> {
        Id = i
        Label = sprintf "row-%04d" i
        Value = i
    })

// ─── The real module views ─────────────────────────────────────────────

/// One host dispatch of a real module page: the module's own `update` applied
/// to the model the page shows, its effects dropped (the leg times the view,
/// not the remote call). `tick` counts the dispatches, so each one changes
/// what the page shows.
type private ModuleStep<'Model, 'Msg> = {
    Update: 'Msg -> 'Model -> 'Model * Step<'Msg, RemoteEffect<'Msg>> list
    Next: int -> 'Msg
}

let private stepThrough (s: ModuleStep<'Model, 'Msg>) (model: 'Model, tick: int) : 'Model * int =
    fst (s.Update (s.Next tick) model), tick + 1

let private seedThrough (update: 'Msg -> 'Model -> 'Model * 'Steps) (init: 'Model) (msgs: 'Msg list) : 'Model =
    msgs |> List.fold (fun model msg -> fst (update msg model)) init

/// The teams a page lists: 200, as the leg's tables hold 200 rows.
let private benchTeamCount = 200

/// The team every dispatch renames, so one row changes per dispatch.
let private renamedTeam = 7

/// A team's name; `tick` below zero is the seed, before any rename.
let private teamName (i: int) (tick: int) =
    if i = renamedTeam && tick >= 0 then
        sprintf "Team %03d (%d)" i tick
    else
        sprintf "Team %03d" i

let private teamInfos (tick: int) : TeamInfo list =
    List.init benchTeamCount (fun i -> {
        TeamId = sprintf "t%03d" i
        Name = teamName i tick
        CreatedAt = System.DateTime(2026, 1, 1)
        Archived = false
    })

let private teamSummaries (tick: int) : TeamSummary list =
    List.init benchTeamCount (fun i -> {
        TeamId = sprintf "t%03d" i
        Name = teamName i tick
        CreatedAt = System.DateTime(2026, 1, 1)
        Archived = (i % 2 = 1)
        MemberCount = 2
        Owners = [ sprintf "owner-t%03d" i ]
        Admins = []
    })

/// The team manager's list of 200 teams, one active, reached through its
/// `update`; a dispatch renames one team.
let private teamManager () =
    let seed =
        seedThrough TeamManagerUI.update (fst (TeamManagerUI.init ClientModuleContext.empty)) [
            TeamManagerUI.TeamsLoaded(teamInfos -1)
            TeamManagerUI.ActiveTeamLoaded(Some "t000")
        ]

    seed,
    {
        Update = TeamManagerUI.update
        Next = fun tick -> TeamManagerUI.TeamsLoaded(teamInfos tick)
    }

/// The registered modules the visibility editor lists: 40, half selected by
/// the resolution, so the page is a working list rather than the two rows
/// its parity state holds.
let private visibilityModules = List.init 40 (sprintf "module-%02d")

/// The module-visibility editor with no stored profile and its resolution
/// beside it, reached through its `update`; a dispatch flips the rule's kind.
let private visibilityEditor () =
    let seed =
        seedThrough ModuleVisibilityAdminUI.update (fst (ModuleVisibilityAdminUI.init ClientModuleContext.empty)) [
            ModuleVisibilityAdminUI.RegisteredLoaded(Ok visibilityModules)
            ModuleVisibilityAdminUI.ProfileLoaded(Ok None)
            ModuleVisibilityAdminUI.ResolutionLoaded(
                Ok(
                    Some {
                        GovernedModuleIds = visibilityModules
                        SelectedModuleIds =
                            visibilityModules
                            |> List.indexed
                            |> List.filter (fun (i, _) -> i % 2 = 0)
                            |> List.map snd
                        ExcludedEntryIds = []
                        ContributingScopes = []
                    }
                )
            )
        ]

    seed,
    {
        Update = ModuleVisibilityAdminUI.update
        Next =
            fun tick ->
                ModuleVisibilityAdminUI.SetKind(
                    if tick % 2 = 0 then
                        ModuleVisibilityAdminUI.RuleKind.Allow
                    else
                        ModuleVisibilityAdminUI.RuleKind.Deny
                )
    }

/// Platform Management's teams tab with 200 teams, reached through its
/// `update`; a dispatch renames one team.
let private platformTeams () =
    let seed =
        seedThrough PlatformAdminUI.update (fst (PlatformAdminUI.init ClientModuleContext.empty)) [
            PlatformAdminUI.SwitchTab PlatformAdminUI.TeamsTab
            PlatformAdminUI.TeamsLoaded(Ok(teamSummaries -1))
        ]

    seed,
    {
        Update = PlatformAdminUI.update
        Next = fun tick -> PlatformAdminUI.TeamsLoaded(Ok(teamSummaries tick))
    }

/// The locale the shipped surfaces are drawn under.
let private benchLocale = "en"

// ─── The browser entry ─────────────────────────────────────────────────

[<Import("createRoot", from = "react-dom/client")>]
let private createRoot (container: obj) : obj = jsNative

[<Import("flushSync", from = "react-dom")>]
let private flushSync (work: unit -> unit) : unit = jsNative

type private BodyProps = {| Draw: unit -> ReactElement |}

/// Every page is built inside a component's render, as a module view is.
let private Body (props: BodyProps) : ReactElement = props.Draw()

/// Mount `view` of `seed` into `container` and return a handle whose
/// `dispatch` moves the model one `step` and renders it synchronously, the
/// view rebuilt whole as a host dispatch rebuilds it. Layout is the caller's
/// to force and time.
let private mountPage (container: obj) (view: 'Model -> ReactElement) (seed: 'Model) (step: 'Model -> 'Model) : obj =
    let root = createRoot container
    let mutable model = seed

    let render () =
        let current = model
        let props: BodyProps = {| Draw = fun () -> view current |}
        flushSync (fun () -> root?render (ReactLegacy.createElement (unbox<ReactElement> Body, box props)))

    render ()

    createObj [
        "dispatch"
        ==> fun () ->
            model <- step model
            render ()
        "unmount" ==> fun () -> root?unmount ()
    ]

/// The synthetic table's dispatch: one row's value bumped and the tick advanced.
let private bump (model: BenchModel) : BenchModel =
    let i = model.Tick % model.Rows.Length
    let rows' = Array.copy model.Rows

    rows'[i] <- {
        rows'[i] with
            Value = rows'[i].Value + 1
    }

    { Rows = rows'; Tick = model.Tick + 1 }

/// A real module page mounted through `view`, dispatched through its `update`.
let private mountModule (container: obj) (view: 'Model -> ReactElement) (seed: 'Model, step: ModuleStep<'Model, 'Msg>) =
    mountPage container (fst >> view) (seed, 0) (stepThrough step)

/// The tables and pages a run can mount, by name: the synthetic 200-row
/// table in five shapes, and each real module view as the Feliz page it
/// replaced (`FelizReferenceViews`) and as the module's shipped tree, mounted
/// by the component its deferred surface loads.
let tableKinds = [
    "feliz"
    "tree"
    "tree-kept"
    "tree-grid"
    "tree-markdown"
    "team-manager-feliz"
    "team-manager-tree"
    "visibility-feliz"
    "visibility-tree"
    "platform-teams-feliz"
    "platform-teams-tree"
]

/// Mount the table or page named `kind` (one of `tableKinds`) into
/// `container`, with `rows` rows for the synthetic table.
let mount (kind: string) (rows: int) (container: obj) : obj =
    let table view =
        mountPage container (fun m -> view m ignore) { Rows = benchRows rows; Tick = 0 } bump

    let teamMsgs = MessageCatalog.english.TeamManager
    let visibilityMsgs = MessageCatalog.english.ModuleVisibilityAdmin
    let adminMsgs = MessageCatalog.english.PlatformAdmin
    let widgetMsgs = MessageCatalog.english.PublicUtilityWidgets

    match kind with
    | "feliz" -> table benchView
    | "tree" -> table benchTreeView
    | "tree-kept" -> table benchKeptTreeView
    | "tree-grid" -> table benchGridTreeView
    | "tree-markdown" -> table benchMarkdownTreeView
    | "team-manager-feliz" ->
        mountModule container (fun m -> FelizReferenceViews.TeamManager.body teamMsgs m ignore) (teamManager ())
    | "team-manager-tree" ->
        mountModule
            container
            (fun m ->
                TeamManagerTree.Surface {|
                    Model = m
                    Dispatch = ignore
                    Messages = teamMsgs
                    Locale = benchLocale
                |})
            (teamManager ())
    | "visibility-feliz" ->
        mountModule
            container
            (fun m -> FelizReferenceViews.ModuleVisibilityAdmin.body visibilityMsgs m ignore)
            (visibilityEditor ())
    | "visibility-tree" ->
        mountModule
            container
            (fun m ->
                ModuleVisibilityAdminTree.Surface {|
                    Model = m
                    Dispatch = ignore
                    Messages = visibilityMsgs
                    Locale = benchLocale
                |})
            (visibilityEditor ())
    | "platform-teams-feliz" ->
        mountModule
            container
            (fun m ->
                FelizReferenceViews.PlatformAdmin.body
                    PublicUtilityConfig.defaults
                    (PlatformAdminUI.caller ())
                    adminMsgs
                    widgetMsgs
                    m
                    ignore)
            (platformTeams ())
    | "platform-teams-tree" ->
        mountModule
            container
            (fun m ->
                PlatformAdminTree.Surface {|
                    Model = m
                    Dispatch = ignore
                    Messages = adminMsgs
                    Widgets = widgetMsgs
                    Config = PublicUtilityConfig.defaults
                    Locale = benchLocale
                |})
            (platformTeams ())
    | other -> failwithf "unknown table '%s': expected one of %s" other (String.concat ", " tableKinds)