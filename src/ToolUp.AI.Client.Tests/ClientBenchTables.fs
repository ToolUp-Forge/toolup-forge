// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The 200-row table `ClientBench` times, as Feliz and as a Fuaran-UI tree, in
/// a module that imports nothing from Node, so a browser can load it.
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

/// The same rows as a tree, the shape forge#1160's surfaces author: a heading
/// and, per row, the label and value as text and a button dispatching `Bump`.
let benchTree (model: BenchModel) : Fuaran.UI.Types.Node<BenchMsg> =
    TreeLowering.column "bench" [
        TreeLowering.heading "tick" 2 (sprintf "Tick %d" model.Tick)
        for i in 0 .. model.Rows.Length - 1 do
            let row = model.Rows[i]
            let id = "r" + string row.Id

            TreeLowering.row id [
                TreeLowering.text (id + "-label") row.Label
                TreeLowering.text (id + "-value") (string row.Value)
                TreeLowering.button (id + "-bump") Fuaran.UI.Types.ButtonVariant.Secondary "+" false (Bump i)
            ]
    ]

/// The tree drawn as a deferred surface's file draws it: built and rendered in
/// one render, through the renderer directly.
let benchTreeView (model: BenchModel) (dispatch: BenchMsg -> unit) : ReactElement =
    TreeLoweringRuntime.Tree(TreeLowering.treeProps HostBindingSources.empty dispatch (benchTree model))

/// The rows a run measures, deterministic so the two tables carry the same data.
let benchRows (count: int) : Row[] =
    Array.init count (fun i -> {
        Id = i
        Label = sprintf "row-%04d" i
        Value = i
    })

// ─── The browser entry ─────────────────────────────────────────────────

[<Import("createRoot", from = "react-dom/client")>]
let private createRoot (container: obj) : obj = jsNative

[<Import("flushSync", from = "react-dom")>]
let private flushSync (work: unit -> unit) : unit = jsNative

type private BodyProps = {|
    Model: BenchModel
    Dispatch: BenchMsg -> unit
    View: BenchModel -> (BenchMsg -> unit) -> ReactElement
|}

/// Both tables are built inside a component's render, as a module view is.
let private Body (props: BodyProps) : ReactElement = props.View props.Model props.Dispatch

/// Mount the table named `kind` (`feliz` or `tree`) into `container` and return
/// a handle whose `dispatch` renders the next model synchronously: one row's
/// value bumped and the tick advanced, the view rebuilt whole, as a host
/// dispatch rebuilds it. Layout is the caller's to force and time.
let mount (kind: string) (rows: int) (container: obj) : obj =
    let view =
        match kind with
        | "feliz" -> benchView
        | "tree" -> benchTreeView
        | other -> failwithf "unknown table '%s': expected feliz or tree" other

    let root = createRoot container
    let mutable model = { Rows = benchRows rows; Tick = 0 }

    let render () =
        let props: BodyProps = {|
            Model = model
            Dispatch = ignore
            View = view
        |}

        flushSync (fun () -> root?render (ReactLegacy.createElement (unbox<ReactElement> Body, box props)))

    render ()

    createObj [
        "dispatch"
        ==> fun () ->
            let i = model.Tick % model.Rows.Length
            let rows' = Array.copy model.Rows

            rows'[i] <- {
                rows'[i] with
                    Value = rows'[i].Value + 1
            }

            model <- { Rows = rows'; Tick = model.Tick + 1 }
            render ()
        "unmount" ==> fun () -> root?unmount ()
    ]