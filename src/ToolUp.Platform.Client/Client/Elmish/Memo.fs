// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish

/// One-entry memos for values a view derives from the model.
///
/// The model is immutable, so an input that is the SAME object as last time
/// cannot have changed, and a value derived from it need not be rebuilt. That
/// matters wherever the derived value's identity is observed: a React context
/// re-renders every reader when its value changes identity, and a memo
/// boundary holds only while its props keep theirs. Each memo keeps exactly
/// one entry — the last input and its result — so it never grows, and a new
/// input replaces the old one.
///
/// A memo is a value: build it once (a module- or program-level `let`) and
/// call it as the function it wraps. The entry lives inside the closure; no
/// caller declares a mutable.
[<RequireQualifiedAccess>]
module Memo =

    /// The primitive the named forms are built on: a one-entry memo whose key
    /// is compared by `same`. Use it when part of the key is a value compared
    /// by equality (a string, an id list rebuilt every render) rather than by
    /// reference.
    let oneEntryBy (same: 'k -> 'k -> bool) (compute: 'k -> 'v) : 'k -> 'v =
        let last: ('k * 'v) option ref = ref None

        fun key ->
            match last.Value with
            | Some(cachedKey, value) when same cachedKey key -> value
            | _ ->
                let value = compute key
                last.Value <- Some(key, value)
                value

    let private sameRef (a: 'a) (b: 'a) = obj.ReferenceEquals(a, b)

    /// A one-entry memo keyed by the reference of its input: the same object
    /// in gives the same result object back; a different object recomputes.
    let byRef (compute: 'a -> 'v) : 'a -> 'v =
        let memo = oneEntryBy sameRef compute
        fun input -> memo input

    /// `byRef` over two inputs, both compared by reference.
    let byRef2 (compute: 'a -> 'b -> 'v) : 'a -> 'b -> 'v =
        let memo =
            oneEntryBy (fun (a1, b1) (a2, b2) -> sameRef a1 a2 && sameRef b1 b2) (fun (a, b) -> compute a b)

        fun a b -> memo (a, b)