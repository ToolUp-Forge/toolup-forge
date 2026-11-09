// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting

open System

// ─── The differential gate ───────────────────────────────
//
// A registered decoder WINS over reflection, so a wrong one is worse
// than none: it decodes differently from what every deployment ran
// before, silently, for exactly the records someone cared enough about
// to opt in. What makes opting in safe is running the candidate beside
// the reflection reader over draws of the record's own shape BEFORE it
// is registered, and refusing by name on the first disagreement.
//
// The gate RUNS on .NET only, and structurally so: drawing a
// value of an arbitrary type and encoding it with the shipped writer both
// need `FSharp.Reflection` and the TypeShape-backed writer, neither of
// which the Fable host has. So the gate itself lives in
// ToolUp.Platform.Server (`RemotingDecoders.verify`, `gate`), and this
// tier holds only its vocabulary: the verification record, the refusal,
// and `DecoderGate`, the gate as a value. A generated module's `verifyAll`
// takes the gate as an argument, so the module compiles wherever its
// decoders do, in a project that references this assembly alone, and the
// browser registers decoders a server-side build gate verified.

/// How one draw diverged. `Candidate` and `Reflection` are
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

/// The record of one verification: which type, how many
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

/// A typed refusal: the registration did not happen, and
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


/// The MessagePack differential gate, as a value: what a
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


/// The MessagePack leg of `Codecs`, under the name it shipped with. Every
/// member forwards to the one codec table; a consumer moves to
/// `Codecs.register Scope.Unscoped (Codec.msgPack decoder)` before the minor
/// that retires this module.
[<RequireQualifiedAccess>]
module RemotingDecoders =

    /// `Codecs.keyFor`.
    let keyFor (wireType: Type) : string = Codecs.keyFor wireType

    /// Register an already-erased MessagePack decoder, unscoped.
    let registerByKey (key: string) (decoder: RegisteredDecoder) : unit =
        Codecs.registerErased Scope.Unscoped key {
            Encode = None
            DecodeJson = None
            DecodeMsgPack = Some decoder
        }

    /// `Codecs.register Scope.Unscoped (Codec.msgPack decoder)`. `inline` so
    /// `typeof<'T>` resolves at the call site under Fable.
    let inline register<'T> (decoder: Decoder<'T>) : unit =
        Codecs.register<'T> Scope.Unscoped (Codec.msgPack decoder)

    /// The registered MessagePack decoder for `wireType`, or `None`.
    let tryGet (wireType: Type) : RegisteredDecoder option =
        Codecs.tryMsgPack Scope.Unscoped wireType

    /// Whether `wireType` has a registered MessagePack decoder.
    let isRegistered (wireType: Type) : bool = (tryGet wireType).IsSome

    /// Every key with a registered MessagePack decoder, ordered.
    let registered () : string list =
        Codecs.registered CodecLeg.DecodeMsgPack |> List.map snd

    /// How many types have a registered MessagePack decoder.
    let count () : int = List.length (registered ())

    /// Test-only: drop every MessagePack decoder.
    let resetForTests () : unit =
        Codecs.resetLegForTests CodecLeg.DecodeMsgPack

    /// Run `gate` over `'T`'s decoder: `draws` draws of `'T` from `seed`,
    /// compared with the reflection reader. The typed form a generated
    /// module's `verifyAll` emits; `inline` for `register`'s reason.
    let inline verifyThrough<'T>
        (gate: DecoderGate)
        (draws: int)
        (seed: int)
        (decoder: Decoder<'T>)
        : Result<DecoderVerification, DecoderRefusal> =
        gate draws seed typeof<'T> (fun value -> decoder value |> Result.map box)