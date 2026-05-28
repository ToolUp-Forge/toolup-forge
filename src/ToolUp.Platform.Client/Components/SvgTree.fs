// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Components.SvgTree

open Feliz

/// Generic hierarchical tree carrying a per-leaf payload. Internal nodes
/// have a label and a list of children; leaves carry a typed payload plus
/// a display label. Single root only — wrap multiple roots in a synthetic
/// Node if a forest needs to render in one diagram.
type TreeNode<'TLeaf> =
    | Node of label: string * children: TreeNode<'TLeaf> list
    | Leaf of payload: 'TLeaf * label: string

type SvgTreeProps<'TLeaf> = {
    Root: TreeNode<'TLeaf>
    RowHeight: int
    NodeRadius: int
    LeafSpacing: int
    OnLeafClick: ('TLeaf -> unit) option
}

let defaults (root: TreeNode<'TLeaf>) : SvgTreeProps<'TLeaf> = {
    Root = root
    RowHeight = 60
    NodeRadius = 6
    LeafSpacing = 70
    OnLeafClick = None
}

/// Laid-out node carrying screen coordinates. The layout pass walks the
/// tree once, assigns each leaf a sequential x and each internal node the
/// midpoint of its children's xs; depth determines y. Not Reingold-Tilford
/// (which would balance subtree widths cleanly) — naive layout is enough
/// for the first round of consumers; revisit if real trees look ugly.
type private LaidNode<'TLeaf> = {
    X: float
    Y: float
    Depth: int
    Label: string
    Payload: 'TLeaf option
    Children: LaidNode<'TLeaf> list
}

let private layout (rowHeight: int) (leafSpacing: int) (root: TreeNode<'TLeaf>) : LaidNode<'TLeaf> * float * float =
    let mutable nextLeafIndex = 0
    let spacing = float leafSpacing
    let row = float rowHeight

    let rec walk depth node =
        let y = float depth * row + row / 2.0

        match node with
        | Leaf(payload, label) ->
            let x = float nextLeafIndex * spacing + spacing / 2.0
            nextLeafIndex <- nextLeafIndex + 1

            {
                X = x
                Y = y
                Depth = depth
                Label = label
                Payload = Some payload
                Children = []
            }
        | Node(label, children) ->
            let kids = children |> List.map (walk (depth + 1))

            let x =
                match kids with
                | [] ->
                    let xv = float nextLeafIndex * spacing + spacing / 2.0
                    nextLeafIndex <- nextLeafIndex + 1
                    xv
                | _ ->
                    let xs = kids |> List.map _.X
                    (List.min xs + List.max xs) / 2.0

            {
                X = x
                Y = y
                Depth = depth
                Label = label
                Payload = None
                Children = kids
            }

    let laid = walk 0 root
    let width = float nextLeafIndex * spacing |> max spacing

    let rec maxDepth node =
        match node.Children with
        | [] -> node.Depth
        | kids -> kids |> List.map maxDepth |> List.max

    let height = float (maxDepth laid + 1) * row
    laid, width, height

let private renderEdges (node: LaidNode<'TLeaf>) : ReactElement list =
    let rec walk acc n =
        let edges =
            n.Children
            |> List.map (fun c ->
                Svg.line [
                    svg.x1 (int n.X)
                    svg.y1 (int n.Y)
                    svg.x2 (int c.X)
                    svg.y2 (int c.Y)
                    svg.stroke "currentColor"
                    svg.strokeWidth 1
                ])

        let acc' = edges @ acc
        n.Children |> List.fold walk acc'

    walk [] node

let private renderNodes
    (nodeRadius: int)
    (onLeafClick: ('TLeaf -> unit) option)
    (node: LaidNode<'TLeaf>)
    : ReactElement list =
    let rec walk acc n =
        let isLeaf = n.Children.IsEmpty

        let circleProps = [
            svg.cx (int n.X)
            svg.cy (int n.Y)
            svg.r nodeRadius
            svg.fill (if isLeaf then "currentColor" else "white")
            svg.stroke "currentColor"
            svg.strokeWidth 1
            match n.Payload, onLeafClick with
            | Some p, Some handler ->
                svg.className "cursor-pointer"
                svg.onClick (fun _ -> handler p)
            | _ -> ()
        ]

        let labelY = int (n.Y + float nodeRadius + 12.0)

        let label =
            Svg.text [
                svg.x (int n.X)
                svg.y labelY
                svg.custom ("textAnchor", "middle")
                svg.custom ("fontSize", "11")
                svg.fill "currentColor"
                svg.children [ Html.text n.Label ]
            ]

        let acc' = label :: Svg.circle circleProps :: acc
        n.Children |> List.fold walk acc'

    walk [] node

/// Render a hierarchical tree as an inline SVG with naive level-aligned
/// layout. Edges paint first so node markers overlay them cleanly. Inherits
/// `text-*` colour via `currentColor` — the consumer controls colour via
/// CSS on the wrapping element. Sized via `viewBox` so the SVG scales
/// responsively without absolute pixel dimensions.
[<ReactComponent>]
let SvgTree<'TLeaf> (props: SvgTreeProps<'TLeaf>) : ReactElement =
    let laid, width, height = layout props.RowHeight props.LeafSpacing props.Root
    let viewW = max 100 (int width)
    let viewH = max 60 (int (height + 24.0))

    Svg.svg [
        svg.className "text-slate-700 w-full h-auto"
        svg.viewBox (0, 0, viewW, viewH)
        svg.children (renderEdges laid @ renderNodes props.NodeRadius props.OnLeafClick laid)
    ]