// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Fuaran.UI.Types

#if FABLE_COMPILER
/// The weak, identity-keyed table behind `Keep` in the browser, a `WeakMap`
/// (on .NET it is a `ConditionalWeakTable`). An entry lives exactly as long as
/// its key, so a table never holds a sub-value the model has dropped.
module internal KeepTable =
    open Fable.Core

    [<Emit("new WeakMap()")>]
    let create () : obj = jsNative

    /// A `WeakMap` refuses a primitive key, so a string or a number is never
    /// cached: its node is built on every call, which is correct and merely
    /// unkept.
    [<Emit("($0 !== null && (typeof $0 === 'object' || typeof $0 === 'function'))")>]
    let canKey (key: obj) : bool = jsNative

    [<Emit("($0.get($1) ?? null)")>]
    let get (table: obj) (key: obj) : obj = jsNative

    [<Emit("$0.set($1, $2)")>]
    let set (table: obj) (key: obj) (value: obj) : unit = jsNative
#endif

/// A node built once per distinct input, by identity.
///
/// The renderer's subtree memo serves the element it built last time for the
/// SAME `Node` value under the same context, so a view that rebuilds every node
/// from its model on each dispatch gets nothing from it. `Keep` is the other
/// half: it maps a model sub-value to its node and hands back the identical node
/// while the sub-value is the identical object. Copy-and-update in `update`
/// keeps every sub-value it did not touch by reference, so a kept row renders
/// once until its record is replaced.
///
/// Hold a `Keep` for as long as the view lives (a module-level `let`), never
/// one per render: a new `Keep` is an empty table. `build` must depend on its
/// input alone, because the node it returns is reused for as long as that input
/// is: anything else it reads (the locale, a selection, a flag) belongs in the
/// input, or the node goes stale when it changes.
[<Sealed>]
type Keep<'In, 'Msg when 'In: not struct>(build: 'In -> Node<'Msg>) =
#if FABLE_COMPILER
    let table = KeepTable.create ()
#else
    let table = System.Runtime.CompilerServices.ConditionalWeakTable<'In, Node<'Msg>>()
#endif

    /// The node for `input`: the one built for this same object before, or a
    /// new one built now and kept for it.
    member _.Node(input: 'In) : Node<'Msg> =
#if FABLE_COMPILER
        let key = box input

        if not (KeepTable.canKey key) then
            build input
        else
            match KeepTable.get table key with
            | null ->
                let node = build input
                KeepTable.set table key (box node)
                node
            | kept -> unbox<Node<'Msg>> kept
#else
        table.GetValue(input, (fun key -> build key))
#endif

/// Building and reading a `Keep`.
[<RequireQualifiedAccess>]
module Keep =

    /// A table mapping each distinct input to the node `build` makes of it.
    let create (build: 'In -> Node<'Msg>) : Keep<'In, 'Msg> = Keep build

    /// The node `keep` holds for `input`, built now if it holds none.
    let node (keep: Keep<'In, 'Msg>) (input: 'In) : Node<'Msg> = keep.Node input

    /// The nodes for `inputs`, in order: each kept by its own input, so a list
    /// in which one item was replaced rebuilds that item's node and no other.
    let list (keep: Keep<'In, 'Msg>) (inputs: 'In seq) : Node<'Msg> list =
        inputs |> Seq.map keep.Node |> List.ofSeq