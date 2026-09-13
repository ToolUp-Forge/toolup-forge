// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting

open System
open ToolUp.Remoting.MsgPack

// ─── Phase 785 — the opt-in seam ─────────────────────────────────────
//
// The algebra is an OPT-IN path, never a replacement. A type with a
// registered decoder decodes through `Decode`'s total combinators; a
// type without one decodes through the reflection reader exactly as it
// did before (GP 11) — so adopting the SDK version carrying this phase
// changes no deployment's behaviour until it registers something.
//
// **Why this is not `GeneratedDispatchRegistry`, which Phase 785's
// shard proposed.** That registry is keyed by API-RECORD type and holds
// `('TContext -> 'TImpl -> Task) list` route handlers for the server
// dispatcher; this one is keyed by WIRE type and holds a decoder. They
// are a different key and a different payload. More decisively, the
// server dispatcher is not on the MsgPack path at all: `Read.Reader`
// has exactly one production call site in the tree — the client's
// binary-response decode (`Client/Remoting/Remoting.fs`) — because
// server arguments arrive as `Choice<byte[], JsonElement>` and decode
// through System.Text.Json. A MsgPack decoder registered for a server
// dispatch branch would never be consulted. The opt-in branch is
// therefore wired where the bytes actually are, and
// `GeneratedDispatchRegistry` is left exactly as Phase 69k shipped it.
//
// **Keyed by `Type.FullName`, and the failure mode is the safe one.**
// The client's response serializer holds a `System.Type` and nothing
// finer, and `FullName` is the one identity both hosts can compute
// (the reflection reader's own Fable branch already resolves types that
// way). A key that does not match — a generic instantiation Fable
// renders differently, a type from a rewritten assembly — resolves to
// `None`, which is the reflection path: a miss costs the algebra, never
// correctness.

/// Phase 785 — a registered decoder, erased to `obj` so one table holds
/// decoders for many types.
///
/// The erasure is the eighth boundary of the kind `CLAUDE.md`
/// enumerates, and it is symmetric in the same way as the others: the
/// only way to put a decoder in is `register<'T>`, which boxes the
/// result of a `Decoder<'T>`, and the only way to get one out is a
/// lookup by that same `'T`'s identity. Nothing casts blind.
type RegisteredDecoder = Value -> Result<obj, DecodeError>

/// Phase 785 — the process-wide table of algebra decoders.
///
/// **Module-level mutable state, justified.** The table is written once
/// per process during composition and read on every response decode
/// afterwards. It is an immutable `Map` behind a single mutable
/// binding, so a read is lock-free and always sees a whole table rather
/// than a half-updated one: registration replaces the binding with a
/// new map, and a reference assignment is atomic. That is the same
/// once-at-startup shape `GeneratedDispatchRegistry` uses, in the form
/// `Platform.Core` can carry — `ConcurrentDictionary` is not available
/// under Fable, and this file ships under `fable/` (GP 10).
[<RequireQualifiedAccess>]
module RemotingDecoders =

    let mutable private table: Map<string, RegisteredDecoder> = Map.empty

    /// The table's key. Deliberately one function rather than an
    /// expression at each call site: "what identifies a wire type here"
    /// must have exactly one answer, or a registration and a lookup can
    /// disagree while both look right.
    let private keyOf (wireType: Type) : string =
        let name = wireType.FullName
        if isNull name then wireType.Name else name

    /// Register the algebra decoder for `'T`. Idempotent — a second
    /// registration for the same type replaces the first, which is what
    /// a hot-reload or a re-run of a composition root has to mean.
    let register<'T> (decoder: Decoder<'T>) : unit =
        let erased: RegisteredDecoder = fun value -> decoder value |> Result.map box
        table <- Map.add (keyOf typeof<'T>) erased table

    /// The algebra decoder for `wireType`, or `None` — which means the
    /// reflection path, not an error.
    let tryGet (wireType: Type) : RegisteredDecoder option = Map.tryFind (keyOf wireType) table

    /// Whether `wireType` decodes through the algebra in this process.
    let isRegistered (wireType: Type) : bool = Map.containsKey (keyOf wireType) table

    /// Every registered type's key, ordered. The enumeration the Phase
    /// 785 composition-profile facet classifies an API record against.
    let registered () : string list = table |> Map.toList |> List.map fst

    /// How many types decode through the algebra in this process.
    let count () : int = Map.count table

    /// Test-only: empty the table. Production code never calls this —
    /// registration is once-per-process at composition.
    ///
    /// Public rather than `internal` because `Platform.Core` declares
    /// exactly one friend assembly (`Platform.Server`, for the MsgPack
    /// `TypeShape` module) and the test pack is not it. The name is the
    /// guard: a member called `resetForTests` in a shipped API is a
    /// statement about who may call it that no access modifier available
    /// here could make instead.
    let resetForTests () : unit = table <- Map.empty