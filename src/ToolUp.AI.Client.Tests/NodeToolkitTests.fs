// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.NodeToolkitTests

// ─── The node toolkit, `Keep`, and the kept render context ────────────────
//
// Three claims, each pinned on the transpiled code, where `Keep` runs over a
// real `WeakMap` and the renderer returns real React elements:
//
//   1. the toolkit's composite shapes derive their inner ids from the id they
//      are given, so a view names one id per shape;
//   2. `Keep` hands back the identical node for the identical input and a new
//      node for a replaced one, which is what lets copy-and-update keep rows;
//   3. the lowering keeps its render context while the projected maps are the
//      same objects, so the subtree memo serves a kept row's element again,
//      and builds a new context when a map, the locale or the tree's
//      declarations change.

open Fable.Core
open Fuaran.UI
open Fuaran.UI.Types
open Fuaran.UI.Renderer
open ToolUp.Platform
open Toolup.UIToolkit
open ToolUp.AI.Client.Tests.NodeTest

type private Row = {
    Id: string
    Label: string
    Value: int
}

type private Msg =
    | Bump of id: string
    | Save

let private ids (node: Node<'Msg>) : string list =
    let rec walk (n: Node<'Msg>) =
        n.Id
        :: (NodeChildren.ordered n.Kind |> Option.defaultValue [] |> List.collect walk)

    walk node

let private toolkitTests =
    testList "the node toolkit" [

        testCase "pageHeader derives its title and actions ids from the id it is given" (fun () ->
            let header =
                Nodes.pageHeader "members" "Members" [ Nodes.Actions.primary "members-add" "Add" Save ]

            Expect.equal
                (ids header)
                [ "members"; "members-title"; "members-actions"; "members-add" ]
                "the header's ids")

        testCase "pageHeader with no actions carries no empty actions row" (fun () ->
            Expect.equal
                (ids (Nodes.pageHeader "members" "Members" []))
                [ "members"; "members-title" ]
                "no actions row")

        testCase "banner and section derive their parts' ids" (fun () ->
            let banner =
                Nodes.Actions.banner "saved" ToneVariant.Success "Saved." "Dismiss" Save

            Expect.equal (ids banner) [ "saved"; "saved-body"; "saved-dismiss" ] "the banner's ids"

            let section =
                Nodes.Panel.section "limits" "Limits" [ Nodes.text "limits-note" "None." ]

            Expect.equal (ids section) [ "limits"; "limits-title"; "limits-note" ] "the section's ids")

        testCase "a list item's id comes from its identity" (fun () ->
            Expect.equal (Nodes.Ids.keyed "rows" "r-17") "rows-r-17" "keyed id"
            Expect.equal (Nodes.Ids.child "rows" "header") "rows-header" "child id")

        testCase "text is literal: the Text node carries it unparsed" (fun () ->
            match (Nodes.text "t" "_sdk.Team_Manager": Node<Msg>).Kind with
            | NodeKind.Text spec ->
                Expect.equal spec.Text (TextSource.Literal "_sdk.Team_Manager") "the body as written"
            | other -> failwithf "expected a text node, got %A" other)

        testCase "a form's fields keep their names and its submit dispatches" (fun () ->
            let form =
                Nodes.Forms.form "invite" "Invite" false Save [
                    Nodes.Forms.text "email" "Email" "" (fun _ -> Save) |> Nodes.Forms.required
                ]

            match form.Kind with
            | NodeKind.Form spec ->
                Expect.equal (spec.Fields |> List.map _.Id) [ "email" ] "field names"
                Expect.isTrue spec.Fields.Head.Required "required"
                Expect.equal spec.OnSubmit (Action.dispatch Save) "submit"
            | other -> failwithf "expected a form node, got %A" other)
    ]

let private keepTests =
    let rowNode (row: Row) : Node<Msg> =
        Nodes.Layout.row (Nodes.Ids.keyed "row" row.Id) [
            Nodes.text (Nodes.Ids.keyed "label" row.Id) row.Label
            Nodes.Actions.secondary (Nodes.Ids.keyed "bump" row.Id) "+" (Bump row.Id)
        ]

    testList "Keep" [

        testCase "a kept input yields the identical node" (fun () ->
            let keep = Keep.create rowNode
            let row = { Id = "a"; Label = "A"; Value = 1 }
            Expect.isTrue (obj.ReferenceEquals(Keep.node keep row, Keep.node keep row)) "same object, same node")

        testCase "a replaced input yields a new node" (fun () ->
            let keep = Keep.create rowNode
            let row = { Id = "a"; Label = "A"; Value = 1 }
            let first = Keep.node keep row
            let replaced = { row with Value = 2 }
            Expect.isFalse (obj.ReferenceEquals(first, Keep.node keep replaced)) "a new record, a new node"
            Expect.isTrue (obj.ReferenceEquals(first, Keep.node keep row)) "the old record still maps to its node")

        testCase "over a copy-and-updated list only the replaced item is rebuilt" (fun () ->
            let mutable builds = 0

            let keep =
                Keep.create (fun (row: Row) ->
                    builds <- builds + 1
                    rowNode row)

            let rows = [
                for i in 1..5 ->
                    {
                        Id = string i
                        Label = $"Row {i}"
                        Value = i
                    }
            ]

            Keep.list keep rows |> ignore
            Expect.equal builds 5 "every row built once"

            let rows' =
                rows |> List.map (fun r -> if r.Id = "3" then { r with Value = 30 } else r)

            Keep.list keep rows' |> ignore
            Expect.equal builds 6 "the second pass built the replaced row and no other")

        testCase "a primitive input is built every time, never cached" (fun () ->
            let mutable builds = 0

            let keep =
                Keep.create (fun (label: string) ->
                    builds <- builds + 1
                    Nodes.text "t" label
                    : Node<Msg>)

            Keep.node keep "x" |> ignore
            Keep.node keep "x" |> ignore
            Expect.equal builds 2 "a string is not a WeakMap key")
    ]

/// Every element reachable from `root` through `props.children`.
[<Emit("""(() => {
  const found = [];
  const walk = (e) => {
    if (e == null || typeof e !== "object") return;
    if (Array.isArray(e)) { e.forEach(walk); return; }
    found.push(e);
    if (e.props) walk(e.props.children);
  };
  walk($0);
  return found;
})()""")>]
let private elementsOf (root: obj) : obj array = jsNative

[<Emit("($0.props != null ? $0.props[$1] : undefined)")>]
let private propOf (element: obj) (name: string) : obj = jsNative

/// The element carrying `data-fuaran-node-id = id`; absent is a failure, since
/// a probe that cannot find its subject proves nothing.
let private elementFor (id: string) (root: obj) : obj =
    match
        elementsOf root
        |> Array.tryFind (fun e -> (propOf e "data-fuaran-node-id") = box id)
    with
    | Some e -> e
    | None -> failwithf "no element carries data-fuaran-node-id=%s" id

[<Emit("(typeof $0.type === 'string' ? $0.type : '')")>]
let private tagOf (element: obj) : string = jsNative

/// The concatenated string children under `element`.
[<Emit("""(() => {
  let s = "";
  const walk = (e) => {
    if (e == null || typeof e === "boolean") return;
    if (typeof e === "string" || typeof e === "number") { s += e; return; }
    if (Array.isArray(e)) { e.forEach(walk); return; }
    if (e.props) walk(e.props.children);
  };
  walk($0);
  return s;
})()""")>]
let private textOf (element: obj) : string = jsNative

/// The heading level an element exposes to the accessibility tree: `h1`..`h6`,
/// or `role="heading"` with its `aria-level`; `None` for anything else.
let private headingLevel (element: obj) : int option =
    match tagOf element with
    | "h1" -> Some 1
    | "h2" -> Some 2
    | "h3" -> Some 3
    | "h4" -> Some 4
    | "h5" -> Some 5
    | "h6" -> Some 6
    | _ ->
        let level = propOf element "aria-level"

        if (propOf element "role") = box "heading" && not (isNull level) then
            Some(int (string level))
        else
            None

let private renderTree (node: Node<obj>) : obj =
    let props =
        TreeLowering.treeProps (TreeLowering.TreeHost.ofSources HostBindingSources.empty) ignore<obj> node

    let kept = TreeLoweringRuntime.keepContext None props ignore
    Render.render kept.Context node

let private panelTests =
    testList "the panel shapes" [

        testCase "a card's heading is a level-2 heading carrying its text" (fun () ->
            let card: Node<obj> =
                Nodes.Panel.card "billing" "Billing" [ Nodes.text "billing-note" "Paid." ]

            let headings =
                elementsOf (renderTree card)
                |> Array.choose (fun e -> headingLevel e |> Option.map (fun level -> level, textOf e))
                |> List.ofArray

            Expect.equal headings [ 2, "Billing" ] "the card's one heading, at level 2")

        testCase "control: a section's title is found at level 3" (fun () ->
            let section: Node<obj> =
                Nodes.Panel.section "limits" "Limits" [ Nodes.text "limits-note" "None." ]

            let headings =
                elementsOf (renderTree section)
                |> Array.choose (fun e -> headingLevel e |> Option.map (fun level -> level, textOf e))
                |> List.ofArray

            Expect.equal headings [ 3, "Limits" ] "the probe sees a known heading")
    ]

/// Every `gap` the mounted markup of `node` declares, in document order: the
/// style a browser reads, not the value the tree was built with.
let private gapsOf (node: Node<obj>) : string list =
    let html = ToolUp.Platform.Testing.ViewMount.mount (renderTree node)

    [
        for m in System.Text.RegularExpressions.Regex.Matches(html, @"gap:\s*([0-9.]+px)") do
            m.Groups[1].Value
    ]

// The renderer writes a box's gap as pixels, so each shape's spacing is pinned
// in the markup it draws, in the unit a browser reads.
let private layoutTests =
    let children: Node<obj> list = [ Nodes.text "a" "A"; Nodes.text "b" "B" ]

    testList "the layout shapes" [

        testCase "a row spaces its children 8 px apart" (fun () ->
            Expect.equal (gapsOf (Nodes.Layout.row "r" children)) [ "8px" ] "row gap")

        testCase "a column spaces its children 12 px apart" (fun () ->
            Expect.equal (gapsOf (Nodes.Layout.column "c" children)) [ "12px" ] "column gap")

        testCase "a grid spaces its cells 12 px apart" (fun () ->
            Expect.equal (gapsOf (Nodes.Layout.grid "g" 2 children)) [ "12px" ] "grid gap")
    ]

let private contextTests =
    let keptRow: Node<obj> = Nodes.text "kept-row" "Kept"

    let root (tick: int) : Node<obj> =
        Nodes.Layout.column "root" [ Nodes.heading "tick" 2 $"Tick {tick}"; keptRow ]

    let host = {
        HostBindingSources.empty with
            State = Map [ "k", box 1 ]
    }

    let propsOf (sources: HostBindingSources) (locale: string) (node: Node<obj>) =
        TreeLowering.treeProps
            {
                TreeLowering.TreeHost.ofSources sources with
                    Locale = locale
            }
            ignore<obj>
            node

    let keep previous props =
        TreeLoweringRuntime.keepContext previous props ignore

    testList "the kept render context" [

        testCase "a second lowering with equal sources reuses the context" (fun () ->
            let first = keep None (propsOf host "en" (root 1))
            let second = keep (Some first) (propsOf host "en" (root 2))
            Expect.isTrue (obj.ReferenceEquals(first.Context, second.Context)) "the same context object"
            Expect.isTrue first.Context.SubtreeMemo "the subtree memo is on")

        testCase "a new projected map, a new locale or a new declaration builds a new context" (fun () ->
            let first = keep None (propsOf host "en" (root 1))

            let newState = { host with State = Map [ "k", box 1 ] }

            let byState = keep (Some first) (propsOf newState "en" (root 1))
            Expect.isFalse (obj.ReferenceEquals(first.Context, byState.Context)) "an equal but new State map"

            let byLocale = keep (Some first) (propsOf host "fr" (root 1))
            Expect.isFalse (obj.ReferenceEquals(first.Context, byLocale.Context)) "a new locale"
            Expect.equal byLocale.Context.Sources.Locale "fr" "the locale reaches the renderer's sources"

            let seeded: Node<obj> =
                Nodes.Layout.column "root" [
                    Fuaran.button "seeded" {
                        Defaults.button with
                            Disabled = Some(Binding.State("busy", Some false))
                    }
                    keptRow
                ]

            let byDeclaration = keep (Some first) (propsOf host "en" seeded)

            Expect.isFalse
                (obj.ReferenceEquals(first.Context, byDeclaration.Context))
                "a tree that seeds new state needs a context that carries it")

        testCase "under a kept context a kept node renders to the identical element" (fun () ->
            let first = keep None (propsOf host "en" (root 1))
            let before = Render.render first.Context (root 1)
            let second = keep (Some first) (propsOf host "en" (root 2))
            let after = Render.render second.Context (root 2)

            Expect.isTrue
                (obj.ReferenceEquals(elementFor "kept-row" before, elementFor "kept-row" after))
                "the kept row's element is served from the memo"

            Expect.isFalse
                (obj.ReferenceEquals(elementFor "tick" before, elementFor "tick" after))
                "the rebuilt heading is a new element")

        testCase "control: a context rebuilt per render serves no kept element" (fun () ->
            let first = keep None (propsOf host "en" (root 1))
            let before = Render.render first.Context (root 1)
            let fresh = keep None (propsOf host "en" (root 2))
            let after = Render.render fresh.Context (root 2)

            Expect.isFalse
                (obj.ReferenceEquals(elementFor "kept-row" before, elementFor "kept-row" after))
                "without the kept context the memo cannot hit")
    ]

let tests =
    testList "Node toolkit, Keep and the kept render context" [
        toolkitTests
        keepTests
        panelTests
        layoutTests
        contextTests
    ]