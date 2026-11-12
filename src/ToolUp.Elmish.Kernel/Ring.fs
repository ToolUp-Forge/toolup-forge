// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish.Kernel

[<Struct>]
type internal RingState<'item> =
    | Writable of wx: 'item array * ix: int
    | ReadWritable of rw: 'item array * wix: int * rix: int

/// Bounded ring buffer used by the dispatch loop to defer reentrant
/// `dispatch` calls. Auto-grows on overflow; the host chooses the initial
/// capacity (ToolUp.Elmish defaults it to 10, matching upstream, and lets a
/// program set it with `Program.withRingBufferCapacity`).
///
/// Proved a FIFO queue through every grow (`proofs/ElmishRing.fst`,
/// `ring_is_queue`): every pushed item is popped exactly once, in push
/// order, and never as one of the placeholder slots below. The
/// differential host runs the extracted model beside this class on both
/// runtimes.
type RingBuffer<'item>(size) =
    let doubleSize ix (items: 'item array) =
        seq {
            yield! items |> Seq.skip ix
            yield! items |> Seq.take ix

            // Grow on overflow: the new tail slots are placeholders that
            // the write head fills before any read head reaches them, so
            // `Unchecked.defaultof` is never observed as a value — the
            // standard idiom for pre-sizing a ring buffer's backing array.
            // (`placeholder_unobserved` is that sentence as a theorem.)
            // The range is inclusive, so the new array holds `2n + 1`
            // slots rather than `2n`; the model reproduces it, and the
            // theorem does not care.
            for _ in 0 .. items.Length do
                yield Unchecked.defaultof<'item>
        }
        |> Array.ofSeq

    let mutable state: 'item RingState =
        Writable(Array.zeroCreate (max size RingBuffer<'item>.MinimumCapacity), 0)

    /// The capacity floor — the ONE number, read by this constructor and by
    /// the host's capacity setter, and the precondition every ring theorem
    /// carries (`ElmishRing.minimum_capacity`). Two slots, not one: at one
    /// slot the `ReadWritable` state cannot tell one unread slot from none
    /// (`wix = rix` in both) and the second push overwrites the first item
    /// before the grow step runs — `capacity_one_loses_an_item` in the model
    /// is that execution, computed.
    static member MinimumCapacity: int = 2

    /// Take the oldest unread item, or `None` when the ring is empty.
    member __.Pop() =
        match state with
        | ReadWritable(items, wix, rix) ->
            let rix' = (rix + 1) % items.Length

            match rix' = wix with
            | true -> state <- Writable(items, wix)
            | _ -> state <- ReadWritable(items, wix, rix')

            Some items.[rix]
        | _ -> None

    /// Append an item, growing the backing array when the write head would
    /// reach the read head.
    member __.Push(item: 'item) =
        match state with
        | Writable(items, ix) ->
            items.[ix] <- item
            let wix = (ix + 1) % items.Length
            state <- ReadWritable(items, wix, ix)
        | ReadWritable(items, wix, rix) ->
            items.[wix] <- item
            let wix' = (wix + 1) % items.Length

            match wix' = rix with
            | true -> state <- ReadWritable(items |> doubleSize rix, items.Length, 0)
            | _ -> state <- ReadWritable(items, wix', rix)