// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish

[<Struct>]
type internal RingState<'item> =
    | Writable of wx: 'item array * ix: int
    | ReadWritable of rw: 'item array * wix: int * rix: int

/// Bounded ring buffer used by the dispatch loop to defer reentrant
/// `dispatch` calls. Capacity defaults to 10 (matches upstream) and
/// auto-doubles on overflow. Capacity is configurable per-program via
/// `Program.withRingBufferCapacity` — apps that synchronously dispatch >10
/// follow-up messages from a single `update` no longer need to fork the
/// runtime.
type internal RingBuffer<'item>(size) =
    let doubleSize ix (items: 'item array) =
        seq {
            yield! items |> Seq.skip ix
            yield! items |> Seq.take ix

            // Grow-by-doubling: the new tail slots are placeholders that
            // the write head fills before any read head reaches them, so
            // `Unchecked.defaultof` is never observed as a value — the
            // standard idiom for pre-sizing a ring buffer's backing array.
            for _ in 0 .. items.Length do
                yield Unchecked.defaultof<'item>
        }
        |> Array.ofSeq

    let mutable state: 'item RingState = Writable(Array.zeroCreate (max size 10), 0)

    member __.Pop() =
        match state with
        | ReadWritable(items, wix, rix) ->
            let rix' = (rix + 1) % items.Length

            match rix' = wix with
            | true -> state <- Writable(items, wix)
            | _ -> state <- ReadWritable(items, wix, rix')

            Some items.[rix]
        | _ -> None

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