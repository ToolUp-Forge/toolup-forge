// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Json

open System
open ToolUp.Remoting

// The JSON gates RUN on .NET only (a reflective draw, the server's
// converter set, a .NET mirror of the browser's writer), so they live in
// ToolUp.Platform.Server: `JsonDecoderOracle`, `BrowserJsonWriter`, the gate
// half of `JsonDecoders` and `JsonEncoders`, namespaces preserved. This tier
// keeps the gates' vocabulary — the records they return and
// `JsonDecoderGate` / `JsonEncoderGate`, each gate as a value a generated
// module's `verifyAll` takes.

/// A place the oracle is KNOWN to lose information the wire
/// carries, declared rather than discovered: the JSON twin of 799's
/// refuse-path table, for the accept path. A value of `Type` anywhere in
/// a drawn value's shape may come back through the oracle altered where
/// the algebra reads it exactly.
type JsonOracleLoss = {
    /// The CLR type the oracle reads lossily, matched anywhere in the
    /// verified type's shape (a record field, a union case field, an
    /// option, a collection element).
    Type: Type
    /// Why, for a reader of a verification record.
    Reason: string
}

/// One draw on which the candidate and the oracle differed
/// ONLY by a declared loss: the candidate recovered the written value
/// exactly, the oracle did not, and the verified type's shape reaches the
/// loss's type. Said, never refused, and never silently passed.
type JsonDeclaredDifference = {
    /// The zero-based draw.
    Draw: int
    /// The declared loss the difference is attributed to.
    Loss: JsonOracleLoss
    /// The candidate's decode — equal to the written value.
    Candidate: string
    /// The oracle's decode.
    Oracle: string
}

/// The JSON gate's record of an agreeing verification: the
/// shared `DecoderVerification` (its `Divergence` is `None`), beside every
/// draw that differed from the oracle only by a declared loss.
type JsonDecoderVerification = {
    /// Which type, how many draws, under which seed.
    Verification: DecoderVerification
    /// The draws the oracle's declared losses account for, in draw order.
    DeclaredDifferences: JsonDeclaredDifference list
}


/// The JSON wire's differential gate, as a value: what a
/// generated module's `verifyAll` runs each of its decoders through.
///
/// The gate draws values of a type by reflection and writes them with the
/// server's and the browser's writers, which only .NET can do, so this
/// tier declares its shape and the server tier supplies it:
/// `JsonDecoders.gateWith oracle` (ToolUp.Platform.Server) runs the
/// two-writer gate against `oracle`. Erased over the type, like
/// `RegisteredJsonDecoder`, so one value serves every decoder a module
/// registers: it compares an erased candidate with the oracle over `draws`
/// draws (first argument) of the given type, taken from `seed` (second
/// argument). A function type, for `DecoderGate`'s reason.
type JsonDecoderGate = int -> int -> Type -> RegisteredJsonDecoder -> Result<JsonDecoderVerification, DecoderRefusal>


/// The JSON ENCODER gate, as a value: what a generated encoder module's
/// `verifyAll` runs each of its encoders through. A registered encoder WINS
/// over the System.Text.Json writer on the response leg, so it is registered
/// only when its text equals the writer's, byte for byte, over draws of its
/// own type: `JsonEncoders.gateWith oracle` (ToolUp.Platform.Server) supplies
/// it. Erased over the type, like `RegisteredJsonEncoder`; the record is the
/// shared `DecoderVerification`, whose divergence carries the encoder's text
/// as `Candidate` and the writer's as `Reflection`.
type JsonEncoderGate = int -> int -> Type -> RegisteredJsonEncoder -> Result<DecoderVerification, DecoderRefusal>


/// The JSON decoder leg of `Codecs`, under the name it shipped with. Every
/// member forwards to the one codec table; a consumer moves to
/// `Codecs.register (Scope.ForRecord name) (Codec.jsonDecoder decoder)` before
/// the minor that retires this module.
[<RequireQualifiedAccess>]
module JsonDecoders =

    let private scopeOf (recordName: string option) =
        match recordName with
        | Some name -> Scope.ForRecord name
        | None -> Scope.Unscoped

    /// Register an already-erased JSON decoder under a wire-type key, scoped
    /// to `recordName` (`None` = unscoped).
    let registerByKey (recordName: string option) (key: string) (decoder: RegisteredJsonDecoder) : unit =
        Codecs.registerErased (scopeOf recordName) key {
            Encode = None
            DecodeJson = Some decoder
            DecodeMsgPack = None
        }

    /// `Codecs.register Scope.Unscoped (Codec.jsonDecoder decoder)`. `inline`
    /// so `typeof<'T>` resolves at the call site under Fable.
    let inline register<'T> (decoder: JsonDecoder<'T>) : unit =
        Codecs.register<'T> Scope.Unscoped (Codec.jsonDecoder decoder)

    /// `Codecs.register (Scope.ForRecord recordName) (Codec.jsonDecoder decoder)`.
    let inline registerFor<'T> (recordName: string) (decoder: JsonDecoder<'T>) : unit =
        Codecs.register<'T> (Scope.ForRecord recordName) (Codec.jsonDecoder decoder)

    /// The JSON decoder for `wireType`, record-scoped first, then unscoped.
    let tryGet (recordName: string option) (wireType: Type) : RegisteredJsonDecoder option =
        Codecs.tryJsonDecoder (scopeOf recordName) wireType

    /// Whether `wireType` has a JSON decoder for `recordName`.
    let isRegistered (recordName: string option) (wireType: Type) : bool = (tryGet recordName wireType).IsSome

    /// Every registered `(record, type)` key, ordered; `fst` is `None` for an
    /// unscoped registration.
    let registered () : (string option * string) list =
        Codecs.registered CodecLeg.DecodeJson
        |> List.map (fun (scope, key) ->
            match scope with
            | Scope.Unscoped -> None, key
            | Scope.ForRecord name -> Some name, key)

    /// How many `(record, type)` pairs have a JSON decoder.
    let count () : int = List.length (registered ())

    /// Test-only: drop every JSON decoder.
    let resetForTests () : unit =
        Codecs.resetLegForTests CodecLeg.DecodeJson

    /// Run `gate` over `'T`'s decoder: `draws` draws of `'T` from `seed`. The
    /// typed form a generated module's `verifyAll` emits.
    let inline verifyThrough<'T>
        (gate: JsonDecoderGate)
        (draws: int)
        (seed: int)
        (decoder: JsonDecoder<'T>)
        : Result<JsonDecoderVerification, DecoderRefusal> =
        gate draws seed typeof<'T> (fun value -> decoder value |> Result.map box)


/// The RESPONSE leg's encoders: the typed gate entry a generated encoder
/// module's `verifyAll` emits, and the two combinators in which the server's
/// converter set writes a value differently from the browser's writer, whose
/// convention `JsonEncode` follows because the argument seam reads it.
[<RequireQualifiedAccess>]
module JsonEncoders =

    /// A `DateTimeOffset` as the converter set writes one: the round-trip
    /// form with the fraction's trailing zeros trimmed, and the fraction
    /// dropped when it is zero (`2000-06-30T15:17:00+00:00`,
    /// `2000-06-30T15:17:00.12+00:00`).
    let dateTimeOffset: JsonEncoder<DateTimeOffset> =
        fun d ->
            let text = d.ToString("O", Globalization.CultureInfo.InvariantCulture)
            let dot = text.IndexOf '.'

            if dot < 0 then
                JsonValue.String text
            else
                let mutable fractionEnd = dot + 1

                while fractionEnd < text.Length && Char.IsDigit text[fractionEnd] do
                    fractionEnd <- fractionEnd + 1

                let fraction = text.Substring(dot + 1, fractionEnd - dot - 1).TrimEnd '0'
                let head = text.Substring(0, dot)
                let tail = text.Substring fractionEnd

                JsonValue.String(
                    if fraction = "" then
                        head + tail
                    else
                        head + "." + fraction + tail
                )

    /// A map keyed by a value (a record, a tuple, a union) as the converter
    /// set writes it: an object whose member names are each key's compact
    /// JSON text, in the map's order.
    let mapOf<'K, 'V when 'K: comparison> (key: JsonEncoder<'K>) (value: JsonEncoder<'V>) : JsonEncoder<Map<'K, 'V>> =
        fun m -> JsonValue.Object(m |> Map.toArray |> Array.map (fun (k, v) -> JsonEncode.toText (key k), value v))

    /// Run `gate` over `'T`'s encoder: `draws` draws of `'T` from `seed`.
    /// `inline` so `typeof<'T>` resolves at the call site under Fable.
    let inline verifyThrough<'T>
        (gate: JsonEncoderGate)
        (draws: int)
        (seed: int)
        (encoder: JsonEncoder<'T>)
        : Result<DecoderVerification, DecoderRefusal> =
        gate draws seed typeof<'T> (fun value -> encoder (unbox<'T> value))