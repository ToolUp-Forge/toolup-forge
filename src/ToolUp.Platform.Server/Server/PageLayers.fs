// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Fuaran.Core
open Fuaran.UI.Types
open Fuaran.UI.Ops
open Fuaran.UI.Ops.Types

// ─── The layer fold ─────────────────────────────────────────────────────
//
// A module page is a stored document in layers: the platform seed, then
// three op streams over it — the platform layer's own edits, a team's
// arrangement, a viewer's individual arrangement — folded in that order.
// Each layer is a `Fuaran.Core.OpStream` chain of the language tier's tree
// ops, and the fold applies them with the language tier's engine
// (`Fuaran.UI.Ops.Apply`): forge holds no op vocabulary and no interpreter
// of its own. What it adds is the bookkeeping a multi-layer fold needs and a
// single apply does not have:
//
//   * an op whose target is gone is REPORTED, never dropped: the conflict
//     names the op's own act (its layer and sequence) and, where an earlier
//     act removed the target, that act too;
//   * undo is the op algebra's inverse — `Fuaran.Core.Ops.invert` over the
//     tier's witnesses for the structural ops, and the pre-state's content
//     for an in-place edit, which is exactly Core's rule for `UpdateNode`.
//
// Pure: no IO, no clock. The store reads and writes the streams; this file
// only folds them.

/// Which layer of a module page an act belongs to. The fold applies them in
/// declaration order: platform, then team, then individual.
[<RequireQualifiedAccess>]
type PageLayer =
    /// The deployment's default: the seed and the platform's own edits.
    | Platform
    /// A team's arrangement over the platform layer.
    | Team
    /// One viewer's arrangement over their team's.
    | Individual

[<RequireQualifiedAccess>]
module PageLayer =
    /// Stable wire token, as stored and audited.
    let toToken =
        function
        | PageLayer.Platform -> "platform"
        | PageLayer.Team -> "team"
        | PageLayer.Individual -> "individual"

    /// The fold order: lower layers first.
    let rank =
        function
        | PageLayer.Platform -> 0
        | PageLayer.Team -> 1
        | PageLayer.Individual -> 2

/// One act in a page's history: the op record at `Seq` in the stream of
/// `Layer` held by `Principal` (the team id of a team layer, the user id of
/// an individual layer).
type LayerAct = {
    Layer: PageLayer
    Principal: string
    Seq: int
}

/// Why an op no longer applies in the fold.
[<RequireQualifiedAccess>]
type ConflictCause =
    /// The op addresses a node an earlier act removed — `RemovedBy` names
    /// that act.
    | Removed of target: NodeId * removedBy: LayerAct
    /// The op addresses a node the folded document never held: the seed it
    /// was written against has been replaced, or it names a node another
    /// principal's layer added.
    | NotInDocument of target: NodeId
    /// Every node the op addresses is present and the language tier's apply
    /// still refused it (a reorder over a child list another layer changed,
    /// an id the document now holds twice, …).
    | Refused of ApplyError

/// An op the fold could not apply. It is carried here, not dropped: the
/// document it would have changed is left as the layers below it made it.
type LayerConflict = {
    /// The act whose op did not apply.
    Act: LayerAct
    /// The op, as stored.
    Op: TreeOp<unit>
    Cause: ConflictCause
}

/// One layer's op stream, as the fold reads it.
type LayerStream = {
    Layer: PageLayer
    Principal: string
    Records: OpRecord<TreeOp<unit>> list
}

/// A fold's result: the document, and every op that did not apply.
type LayerFold = {
    Document: Node<unit>
    Conflicts: LayerConflict list
}

/// Why an op has no inverse.
[<RequireQualifiedAccess>]
type NotInvertible =
    /// The op is outside the stored vocabulary (`UpdateState`,
    /// `ReplaceRoot`).
    | OutsideVocabulary of opKind: string
    /// The op does not apply to the document it is inverted against, so
    /// there is nothing to undo.
    | DoesNotApply of ApplyError
    /// The op algebra refused to derive the inverse (Core's rejection code)
    /// — a structural op over a node held outside an ordered child list.
    | Rejected of code: string

[<RequireQualifiedAccess>]
module PageLayers =

    /// The op kind outside the stored vocabulary that `op` carries, if any.
    /// `UpdateState` carries handler closures a stored op cannot hold, and
    /// `ReplaceRoot` replaces the whole document — a re-seed in disguise,
    /// which is the operator's recorded act, never a layer's op.
    let rec outsideVocabulary (op: TreeOp<'Msg>) : string option =
        match op with
        | TreeOp.UpdateState _ -> Some "UpdateState"
        | TreeOp.ReplaceRoot _ -> Some "ReplaceRoot"
        | TreeOp.Batch inner -> inner |> List.tryPick outsideVocabulary
        | _ -> None

    // ── The codec: the language tier's canonical JSON ─────────────────────

    let private retype (op: TreeOp<obj>) : Result<TreeOp<unit>, string> =
        Fuaran.UI.Ops.TreeOpMap.TreeOp.mapMsg (fun _ -> Some()) op
        |> Result.mapError Fuaran.UI.Ops.TreeOpMap.MapRefusal.render

    /// The canonical JSON of an op — what its record's hash is computed over.
    let encodeOp (op: TreeOp<unit>) : string =
        Fuaran.UI.OpStream.Abstractions.CanonicalJson.encodeOp op

    /// An op read back from its canonical JSON: the language tier's decoder,
    /// the erased message retyped to `unit`, and the vocabulary checked.
    let decodeOp (json: string) : Result<TreeOp<unit>, string> =
        JsonDecode.decodeOp json
        |> Result.mapError JsonDecode.DecodeError.render
        |> Result.bind retype
        |> Result.bind (fun op ->
            match outsideVocabulary op with
            | Some kind -> Error(sprintf "'%s' is outside the stored page vocabulary" kind)
            | None -> Ok op)

    /// The canonical wire JSON of a document.
    let encodeDocument (document: Node<unit>) : string =
        Fuaran.UI.OpStream.Abstractions.CanonicalJson.encodeNode document

    /// A document read from its wire JSON: the language tier's decoder, the
    /// erased message retyped to `unit`.
    let decodeDocument (json: string) : Result<Node<unit>, string> =
        JsonDecode.decodeNode json
        |> Result.mapError JsonDecode.DecodeError.render
        |> Result.map (WireTree.reify >> Fuaran.UI.NodeMap.mapMsg (fun _ -> ()))

    /// The stream every layer is: the language tier's apply as its reducer,
    /// the canonical op JSON as its encoding, chained under the canonical
    /// config with SHA-256.
    let stream: Stream<TreeOp<unit>, Node<unit>, ApplyError> =
        OpStream.canonical OpStream.sha256Hash {
            Apply = Apply.apply
            Encode = encodeOp
            Decode = decodeOp
        }

    /// SHA-256 (lowercase hex) over UTF-8 text — the digest the object store
    /// assigns the same bytes.
    let digest (text: string) : string = Hash.sha256Hex text

    /// SHA-256 of the canonical JSON array of `ops`.
    let opsHash (ops: TreeOp<unit> list) : string =
        "[" + (ops |> List.map encodeOp |> String.concat ",") + "]" |> digest

    // ── The fold ──────────────────────────────────────────────────────────

    let private idsOf (document: Node<unit>) : Set<string> =
        Introspect.allNodeIds document |> List.map (fun (NodeId id) -> id) |> Set.ofList

    let private holds (document: Node<unit>) (NodeId id) = Set.contains id (idsOf document)

    /// The innermost op of `op` the apply refused, the document it saw, and
    /// the refusal: a `Batch` reports its failing member, read by replaying
    /// the members before it.
    let rec private failingLeaf (op: TreeOp<unit>) (document: Node<unit>) (error: ApplyError) =
        match op, error.Code with
        | TreeOp.Batch inner, ApplyErrorCode.BatchAborted index when index < List.length inner ->
            let before =
                inner
                |> List.truncate index
                |> List.fold (fun state m -> state |> Result.bind (Apply.apply m)) (Ok document)

            match before with
            | Ok seen ->
                let m = List.item index inner

                match Apply.apply m seen with
                | Error inner' -> failingLeaf m seen inner'
                | Ok _ -> op, document, error
            | Error _ -> op, document, error
        | _ -> op, document, error

    /// Why `op` did not apply to `document`, given which act removed each
    /// id the fold has seen leave.
    let private causeOf (removedBy: Map<string, LayerAct>) (op: TreeOp<unit>) (document: Node<unit>) error =
        let leaf, seen, refusal = failingLeaf op document error

        match TreeOp.targets leaf |> List.tryFind (holds seen >> not) with
        | Some(NodeId id as target) ->
            match Map.tryFind id removedBy with
            | Some act -> ConflictCause.Removed(target, act)
            | None -> ConflictCause.NotInDocument target
        | None -> ConflictCause.Refused refusal

    type private FoldState = {
        Document: Node<unit>
        RemovedBy: Map<string, LayerAct>
        Conflicts: LayerConflict list
    }

    /// Apply one act. A refused op becomes a conflict and leaves the
    /// document as it was; an applied op records every id it removed
    /// against itself, and forgets every id it brought back.
    let private step (state: FoldState) (act: LayerAct) (op: TreeOp<unit>) : FoldState =
        match outsideVocabulary op with
        | Some kind ->
            let refusal: ApplyError = {
                Code = ApplyErrorCode.KindMismatch
                Message = sprintf "'%s' is outside the stored page vocabulary" kind
                Hint = ApplyHint.empty
            }

            {
                state with
                    Conflicts =
                        {
                            Act = act
                            Op = op
                            Cause = ConflictCause.Refused refusal
                        }
                        :: state.Conflicts
            }
        | None ->
            match Apply.apply op state.Document with
            | Error error -> {
                state with
                    Conflicts =
                        {
                            Act = act
                            Op = op
                            Cause = causeOf state.RemovedBy op state.Document error
                        }
                        :: state.Conflicts
              }
            | Ok next ->
                let before = idsOf state.Document
                let after = idsOf next

                let removedBy =
                    Set.difference before after
                    |> Set.fold (fun acc id -> Map.add id act acc) state.RemovedBy
                    |> Map.filter (fun id _ -> not (Set.contains id after))

                {
                    state with
                        Document = next
                        RemovedBy = removedBy
                }

    let private finish (state: FoldState) : LayerFold = {
        LayerFold.Document = state.Document
        Conflicts = List.rev state.Conflicts
    }

    let private run (seed: Node<unit>) (layers: LayerStream list) : FoldState =
        layers
        |> List.sortBy (fun l -> PageLayer.rank l.Layer)
        |> List.fold
            (fun state layer ->
                layer.Records
                |> List.fold
                    (fun state (record: OpRecord<TreeOp<unit>>) ->
                        step
                            state
                            {
                                LayerAct.Layer = layer.Layer
                                Principal = layer.Principal
                                Seq = record.Seq
                            }
                            record.Op)
                    state)
            {
                Document = seed
                RemovedBy = Map.empty
                Conflicts = []
            }

    /// Fold `layers` over `seed`: platform, then team, then individual
    /// (whatever order they are passed in), each layer's records in sequence.
    /// Deterministic — the same seed and records always fold to the same
    /// document and the same conflicts. An op that does not apply is a
    /// `LayerConflict`, never a silent drop.
    let fold (seed: Node<unit>) (layers: LayerStream list) : LayerFold = run seed layers |> finish

    /// Fold `layers`, then `ops` as the next acts of `layer` held by
    /// `principal`, numbered from `firstSeq`. Returns the fold of the layers
    /// alone and the conflicts the new ops raise — what a save checks before
    /// it appends.
    let foldThen
        (seed: Node<unit>)
        (layers: LayerStream list)
        (layer: PageLayer)
        (principal: string)
        (firstSeq: int)
        (ops: TreeOp<unit> list)
        : LayerFold * LayerFold =
        let below = run seed layers

        let after =
            ops
            |> List.indexed
            |> List.fold
                (fun state (i, op) ->
                    step
                        state
                        {
                            LayerAct.Layer = layer
                            Principal = principal
                            Seq = firstSeq + i
                        }
                        op)
                { below with Conflicts = [] }

        finish below, finish after

    // ── Undo: the op algebra's inverse ────────────────────────────────────

    let private skeleton (op: TreeOp<unit>) : SkeletonOp<Node<unit>, NodeId> option =
        match op with
        | TreeOp.InsertChild(parent, child) -> Some(SkeletonOp.InsertChild(parent, child))
        | TreeOp.RemoveNode target -> Some(SkeletonOp.RemoveNode target)
        | TreeOp.MoveNode(target, parent) -> Some(SkeletonOp.MoveNode(target, parent))
        | TreeOp.ReorderChildren(parent, order) -> Some(SkeletonOp.ReorderChildren(parent, order))
        | _ -> None

    let rec private ofSkeleton (op: SkeletonOp<Node<unit>, NodeId>) : TreeOp<unit> option =
        match op with
        | SkeletonOp.InsertChild(parent, child) -> Some(TreeOp.InsertChild(parent, child))
        | SkeletonOp.RemoveNode target -> Some(TreeOp.RemoveNode target)
        | SkeletonOp.MoveNode(target, parent) -> Some(TreeOp.MoveNode(target, parent))
        | SkeletonOp.ReorderChildren(parent, order) -> Some(TreeOp.ReorderChildren(parent, order))
        | SkeletonOp.Batch inner ->
            let lifted = inner |> List.map ofSkeleton

            if List.forall Option.isSome lifted then
                Some(TreeOp.Batch(List.choose id lifted))
            else
                None
        | SkeletonOp.UpdateNode _ -> None

    /// The inverse of `op` against `pre`, the document it applies to: the op
    /// that takes `Apply.apply op pre` back to `pre`.
    ///
    /// A structural op (insert, remove, move, reorder) inverts through
    /// `Fuaran.Core.Ops.invert` over the tier's own witnesses — the algebra's
    /// inverse, derived from the pre-state. An in-place edit (`EditNode`,
    /// `UpdateProp`, `ReplaceBinding`, `UpdateStyle`, `PatchStyle`) inverts to
    /// the pre-state node's content, the rule Core states for its own
    /// in-place edit: the node's kind, and its style when the op touched the
    /// style. A `Batch` inverts to its members' inverses in reverse order,
    /// each derived against the document that member saw.
    let rec invert (op: TreeOp<unit>) (pre: Node<unit>) : Result<TreeOp<unit>, NotInvertible> =
        match outsideVocabulary op with
        | Some kind -> Error(NotInvertible.OutsideVocabulary kind)
        | None ->
            match Apply.apply op pre with
            | Error error -> Error(NotInvertible.DoesNotApply error)
            | Ok _ ->
                match op with
                | TreeOp.Batch inner ->
                    let rec go (state: Node<unit>) (acc: TreeOp<unit> list) remaining =
                        match remaining with
                        | [] -> Ok(TreeOp.Batch acc)
                        | next :: rest ->
                            match invert next state with
                            | Error e -> Error e
                            | Ok inverse ->
                                match Apply.apply next state with
                                | Ok state' -> go state' (inverse :: acc) rest
                                | Error error -> Error(NotInvertible.DoesNotApply error)

                    go pre [] inner
                | TreeOp.EditNode(target, _)
                | TreeOp.UpdateProp(target, _, _)
                | TreeOp.ReplaceBinding(target, _, _) ->
                    match Introspect.findNode target pre with
                    | Some node -> Ok(TreeOp.EditNode(target, node.Kind))
                    | None -> Error(NotInvertible.Rejected "UnknownNode")
                | TreeOp.UpdateStyle(target, _)
                | TreeOp.PatchStyle(target, _) ->
                    match Introspect.findNode target pre with
                    | Some node ->
                        Ok(TreeOp.UpdateStyle(target, node.Style |> Option.defaultValue Fuaran.UI.Defaults.style))
                    | None -> Error(NotInvertible.Rejected "UnknownNode")
                | structural ->
                    match skeleton structural with
                    | None -> Error(NotInvertible.OutsideVocabulary(TreeOp.kindName structural))
                    | Some core ->
                        match Ops.invert Introspect.nodeWitness Introspect.idWitness core pre with
                        | Error rejection -> Error(NotInvertible.Rejected(Rejection.code rejection))
                        | Ok inverse ->
                            match ofSkeleton inverse with
                            | Some lifted -> Ok lifted
                            | None -> Error(NotInvertible.Rejected "UpdateNode")

    /// The inverses of a script, each derived against the document the
    /// forward op saw, in reverse order: applying them in order to the
    /// script's result restores `pre`. Refuses with the index of the first op
    /// that has no inverse.
    let invertAll (ops: TreeOp<unit> list) (pre: Node<unit>) : Result<TreeOp<unit> list, int * NotInvertible> =
        let rec go (state: Node<unit>) (acc: TreeOp<unit> list) index remaining =
            match remaining with
            | [] -> Ok acc
            | next :: rest ->
                match invert next state with
                | Error e -> Error(index, e)
                | Ok inverse ->
                    match Apply.apply next state with
                    | Ok state' -> go state' (inverse :: acc) (index + 1) rest
                    | Error error -> Error(index, NotInvertible.DoesNotApply error)

        go pre [] 0 ops