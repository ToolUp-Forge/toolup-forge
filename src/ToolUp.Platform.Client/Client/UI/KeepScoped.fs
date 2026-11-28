// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Kept subtrees whose node shows more than its key: the shapes a tree view
/// uses where `Toolup.UIToolkit.Keep`'s rule (the build depends on its input
/// alone) meets a real page, whose rows also show the catalog's strings, a
/// selection or the caller's role. Each holds what it built for the context it
/// was last drawn under and builds again when that context changes, so a
/// subtree is reused while both its input and its context are the same.
module internal KeepScoped

open Fuaran.UI.Types
open Toolup.UIToolkit

/// Whether two values are the same object: the test for a context member that
/// `update` keeps by reference while it does not touch it (a list, a map, the
/// catalog section).
let sameObject (a: 'T) (b: 'T) = obj.ReferenceEquals(box a, box b)

/// An instant as the universal sortable form (`2026-01-02 03:04:05Z`), written
/// from its components: the `"u"` format is not one Fable prints. The tree
/// views that show an instant this way share it.
let universal (at: System.DateTime) =
    sprintf "%04d-%02d-%02d %02d:%02d:%02dZ" at.Year at.Month at.Day at.Hour at.Minute at.Second

/// A `Keep` per context for rows: one table for the context last drawn,
/// replaced when `same` says the context moved, so every row is rebuilt once
/// when it does and only a replaced row record after that.
type Rows<'Ctx, 'Row, 'Msg when 'Row: not struct>(same: 'Ctx -> 'Ctx -> bool, build: 'Ctx -> 'Row -> Node<'Msg>) =
    // Sanctioned module-level mutable state: the table for the context last
    // drawn, as `Keep` holds its own table.
    let mutable current: ('Ctx * Keep<'Row, 'Msg>) option = None

    member _.List (context: 'Ctx) (rows: 'Row seq) : Node<'Msg> list =
        let keep =
            match current with
            | Some(drawn, keep) when same drawn context -> keep
            | _ ->
                let keep = Keep.create (build context)
                current <- Some(context, keep)
                keep

        Keep.list keep rows

/// One subtree built from several model sub-values: the node built for the
/// context last drawn, served again while `same` holds. The shape for a list
/// whose items cannot key a `Keep` (strings, which a `WeakMap` cannot hold) and
/// for a section that shows several lists at once.
type One<'Ctx, 'Msg>(same: 'Ctx -> 'Ctx -> bool, build: 'Ctx -> Node<'Msg>) =
    // Sanctioned module-level mutable state: the node for the context last drawn.
    let mutable current: ('Ctx * Node<'Msg>) option = None

    member _.Node(context: 'Ctx) : Node<'Msg> =
        match current with
        | Some(drawn, node) when same drawn context -> node
        | _ ->
            let node = build context
            current <- Some(context, node)
            node

/// The inputs of a row that shows its record and that record's entries in the
/// model's maps (its attempts, its call in flight), served as the objects drawn
/// last while `same` holds for the row under the same key. A row keyed on its
/// input in a `Keep` is then built again only when its own record or its own
/// entries moved, whatever happened to the other rows' entries.
type RowInputs<'Key, 'Row when 'Key: comparison and 'Row: not struct>(key: 'Row -> 'Key, same: 'Row -> 'Row -> bool) =
    // Sanctioned module-level mutable state: the inputs last drawn, by key.
    let mutable drawn: Map<'Key, 'Row> = Map.empty

    member _.Of(rows: 'Row list) : 'Row list =
        let stable =
            rows
            |> List.map (fun row ->
                match drawn.TryFind(key row) with
                | Some was when same was row -> was
                | _ -> row)

        drawn <- stable |> List.map (fun row -> key row, row) |> Map.ofList
        stable