// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting

open System
open System.IO
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

// ─── Phase 801 — the differential gate ───────────────────────────────
//
// A registered decoder WINS over reflection, so a wrong one is worse
// than none: it decodes differently from what every deployment ran
// before, silently, for exactly the records someone cared enough about
// to opt in. What makes opting in safe is running the candidate beside
// the reflection reader over draws of the record's own shape BEFORE it
// is registered, and refusing by name on the first disagreement.
//
// Phase 902 — the gate RUNS on .NET only, and structurally so: drawing a
// value of an arbitrary type and encoding it with the shipped writer both
// need `FSharp.Reflection` and the TypeShape-backed writer, neither of
// which the Fable host has. So the gate itself lives in
// ToolUp.Platform.Server (`RemotingDecoders.verify`, `gate`), and this
// tier holds only its vocabulary: the verification record, the refusal,
// and `DecoderGate`, the gate as a value. A generated module's `verifyAll`
// takes the gate as an argument, so the module compiles wherever its
// decoders do, in a project that references this assembly alone, and the
// browser registers decoders a server-side build gate verified.

/// Phase 801 — how one draw diverged. `Candidate` and `Reflection` are
/// the two decodes rendered the way `ProofOracleTests` renders them:
/// outcome class, value and runtime type at once, so a width narrowed on
/// the way back — equal-looking at every representation but its type —
/// is a disagreement and not a pass.
type DecoderDivergence = {
    /// The zero-based draw that diverged; every earlier draw agreed.
    Draw: int
    /// The candidate's decode of the draw's bytes.
    Candidate: string
    /// The reflection reader's decode of the same bytes.
    Reflection: string
}

/// Phase 801 — the record of one verification: which type, how many
/// draws, under which seed, and the first divergence if there was one.
type DecoderVerification = {
    /// `RemotingDecoders.keyFor` of the verified type.
    WireType: string
    /// How many draws were compared.
    Draws: int
    /// The seed the draws were taken from, so a refusal is reproducible.
    Seed: int
    /// `None` when every draw agreed; the first disagreement otherwise.
    Divergence: DecoderDivergence option
}

/// Phase 801 — a typed refusal: the registration did not happen, and
/// this is why, naming the record and the first diverging draw.
type DecoderRefusal =
    /// The candidate and the reflection reader disagreed.
    | DecoderDiverges of verification: DecoderVerification
    /// No draw of the type could be produced, so nothing was compared —
    /// a type the shape generator does not reach. Named rather than
    /// passed: a gate that could not run is not a gate that passed.
    | DecoderUndrawable of wireType: string * reason: string

/// Renders a refusal for a boot log or a test failure.
[<RequireQualifiedAccess>]
module DecoderRefusal =
    /// One line naming the type and, for a divergence, the draw and both
    /// decodes; for an undrawable type, why it could not be drawn.
    let describe (refusal: DecoderRefusal) : string =
        match refusal with
        | DecoderDiverges v ->
            match v.Divergence with
            | Some d ->
                sprintf
                    "decoder for %s refused: draw %d of %d (seed %d) decodes differently from the reflection reader — candidate: %s; reflection: %s"
                    v.WireType
                    d.Draw
                    v.Draws
                    v.Seed
                    d.Candidate
                    d.Reflection
            | None -> sprintf "decoder for %s refused (no divergence recorded)" v.WireType
        | DecoderUndrawable(wireType, reason) ->
            sprintf "decoder for %s refused: no draw of the type could be produced — %s" wireType reason


/// Phase 902 — the MessagePack differential gate, as a value: what a
/// generated module's `verifyAll` runs each of its decoders through.
///
/// The gate draws values of a type and encodes them with the shipped
/// writer, which only .NET reflection can do, so this tier declares its
/// shape and the server tier supplies it: `RemotingDecoders.gate` checks
/// against the shipped writer, `RemotingDecoders.gateWith` against the
/// writer it is given. Erased over the type, like `RegisteredDecoder`, so
/// one value serves every decoder a module registers: it compares an
/// erased candidate with the reflection reader over `draws` draws (first
/// argument) of the given type, taken from `seed` (second argument).
///
/// A function type rather than a record of one: a record whose every field
/// is a function is the shape of a Remoting API contract, and the
/// generator's census would count it as one.
type DecoderGate = int -> int -> Type -> RegisteredDecoder -> Result<DecoderVerification, DecoderRefusal>


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
    ///
    /// Public rather than private because `register` below is `inline`
    /// and an inline function may not reach a private binding.
    let keyFor (wireType: Type) : string =
        let name = wireType.FullName
        if isNull name then wireType.Name else name

    /// Register an already-erased decoder under a key.
    ///
    /// The non-inline half of `register`, and the only writer of the
    /// table. It exists so `register` can be `inline` without the table
    /// or the key function having to be public state: an inline function
    /// may not reach a private binding, and the mutable table must stay
    /// one.
    let registerByKey (key: string) (decoder: RegisteredDecoder) : unit = table <- Map.add key decoder table

    /// Register the algebra decoder for `'T`. Idempotent — a second
    /// registration for the same type replaces the first, which is what
    /// a hot-reload or a re-run of a composition root has to mean.
    ///
    /// **`inline`, and it has to be.** Fable erases generics at runtime,
    /// so `typeof<'T>` inside a non-inline generic function has no type
    /// to report — the compiler refuses it outright ("Cannot get type
    /// info of generic parameter T"). Inlining resolves `'T` at each call
    /// site, which is the same fix `WireCorpus.case` needed for the same
    /// reason. `verify.ps1` has no Fable leg, so this is caught only by
    /// compiling the client tier.
    let inline register<'T> (decoder: Decoder<'T>) : unit =
        registerByKey (keyFor typeof<'T>) (fun value -> decoder value |> Result.map box)

    /// The algebra decoder for `wireType`, or `None` — which means the
    /// reflection path, not an error.
    let tryGet (wireType: Type) : RegisteredDecoder option = Map.tryFind (keyFor wireType) table

    /// Whether `wireType` decodes through the algebra in this process.
    let isRegistered (wireType: Type) : bool = Map.containsKey (keyFor wireType) table

    /// Every registered type's key, ordered. The enumeration the Phase
    /// 785 composition-profile facet classifies an API record against.
    let registered () : string list = table |> Map.toList |> List.map fst

    /// How many types decode through the algebra in this process.
    let count () : int = Map.count table

    /// Test-only: empty the table. Production code never calls this —
    /// registration is once-per-process at composition.
    ///
    /// Public rather than `internal` because `Platform.Core` declares
    /// exactly one friend assembly (`Platform.Server`) and the test pack
    /// is not it. The name is the
    /// guard: a member called `resetForTests` in a shipped API is a
    /// statement about who may call it that no access modifier available
    /// here could make instead.
    let resetForTests () : unit = table <- Map.empty


    /// Phase 902 — run `gate` over `'T`'s decoder: `draws` draws of `'T`
    /// from `seed`, compared with the reflection reader. The typed form a
    /// generated module's `verifyAll` emits, one call per decoder. `inline`
    /// for `register`'s reason: Fable erases generics, so `typeof<'T>` must
    /// resolve at the call site, and a generated module compiles under
    /// Fable too.
    let inline verifyThrough<'T>
        (gate: DecoderGate)
        (draws: int)
        (seed: int)
        (decoder: Decoder<'T>)
        : Result<DecoderVerification, DecoderRefusal> =
        gate draws seed typeof<'T> (fun value -> decoder value |> Result.map box)