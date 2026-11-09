// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Json

open ToolUp.Remoting

/// A JSON decoder erased to `obj`, as the codec table holds it.
type RegisteredJsonDecoder = JsonValue -> Result<obj, DecodeError>

/// A JSON encoder erased to `obj`, as the codec table holds it. The only way in
/// is `Codecs.register<'T>`, which unboxes to the `'T` it was registered for.
type RegisteredJsonEncoder = obj -> JsonValue

namespace ToolUp.Remoting

open System
open ToolUp.Remoting.Json
open ToolUp.Remoting.MsgPack

// ─── One codec table, one seam, one posture ──────────────────────
//
// The closed algebra is an OPT-IN path beside the reflective readers and
// writers (GP 11): a wire type with a registered codec leg is read or written
// through it, and a type without one takes the reflective path exactly as it
// always did — so adopting this SDK changes no deployment until something is
// registered. What a type resolves to is answered in ONE place, `Codecs`, for
// every wire and both directions, on both hosts:
//
//   * the server's argument seam (`FableConverters.tryDeserialiseWith`) and
//     the client's JSON response read ask `resolveJson`;
//   * the server's response writer and stream encoder ask `resolveJsonEncoder`;
//   * the client's binary response read asks `resolveMsgPack`.
//
// **The posture is the seam's single switch.** Under `ReflectionFallback` a
// miss is `None` — the reflective path. Under `VerifiedOnly` a miss is
// `Some (Error refusal)` naming the type, so a deployment can state that
// nothing it decodes runs outside the proved path, and a boot walk over every
// mounted contract (the server's `RemotingDecoderFacet.inspectContracts`)
// refuses before the first request rather than at it.
//
// **Keyed by `Type.FullName`, and a miss is the safe failure.** It is the one
// identity both hosts compute (Fable resolves reflected types the same way).
// A key that does not match — a generic instantiation Fable renders
// differently, a type from a rewritten assembly — resolves to a miss: the
// reflective path under the default posture, a named refusal under the strict
// one; never a wrong codec.
//
// **Scoped, then unscoped.** A codec registered `ForRecord name` is consulted
// only for that API record's traffic, and wins there over an unscoped one for
// the same type; a caller with no record to name passes `Unscoped` and gets
// exactly the bare-type lookup. The rule is per LEG: a record-scoped
// registration carrying only a decoder still finds an unscoped encoder.
//
// **Module-level mutable state, justified.** The table is written during
// composition and read per request afterwards. It is an immutable `Map`
// behind one mutable binding, so a read is lock-free and always sees a whole
// table; registration replaces the binding. `ConcurrentDictionary` is not
// available under Fable, and this file ships to the browser.

/// A MessagePack decoder erased to `obj`, as the codec table holds it.
type RegisteredDecoder = Value -> Result<obj, DecodeError>

/// One wire type's codecs: each leg optional, because the legs are produced
/// separately — the server's argument decoders, its response encoders and the
/// client's binary response decoders are generated from different API-record
/// projections — and a leg absent here is simply not registered by this call.
type Codec<'T> = {
    /// Writes `'T` as the System.Text.Json converter set writes it.
    JsonEncoder: JsonEncoder<'T> option
    /// Reads `'T` from the JSON value model.
    JsonDecoder: JsonDecoder<'T> option
    /// Reads `'T` from the MessagePack value model.
    MsgPack: Decoder<'T> option
}

/// The same legs erased to `obj`: what the table holds.
type ErasedCodec = {
    /// The erased JSON encoder.
    Encode: RegisteredJsonEncoder option
    /// The erased JSON decoder.
    DecodeJson: RegisteredJsonDecoder option
    /// The erased MessagePack decoder.
    DecodeMsgPack: RegisteredDecoder option
}

/// Which API record's traffic a registration applies to.
[<RequireQualifiedAccess>]
type Scope =
    /// Every record's.
    | Unscoped
    /// Only the named record's (`Type.Name` of the API record).
    | ForRecord of record: string

/// What a miss in the codec table means.
[<RequireQualifiedAccess>]
type DecodePolicy =
    /// A miss is the reflective path — the default, so a deployment that
    /// registers nothing behaves exactly as before.
    | ReflectionFallback
    /// A miss is a refusal naming the type: nothing is read or written outside
    /// the registered (generated and gate-verified) codecs.
    | VerifiedOnly

/// One leg of a codec, for the table's census.
[<RequireQualifiedAccess>]
type CodecLeg =
    /// The JSON encoder (the server's response writer).
    | EncodeJson
    /// The JSON decoder (the argument seam; the client's JSON response).
    | DecodeJson
    /// The MessagePack decoder (the client's binary response).
    | DecodeMsgPack

/// Building a `Codec<'T>`.
[<RequireQualifiedAccess>]
module Codec =

    /// No leg at all.
    let empty<'T> : Codec<'T> = {
        JsonEncoder = None
        JsonDecoder = None
        MsgPack = None
    }

    /// The JSON wire, both directions.
    let json (encoder: JsonEncoder<'T>) (decoder: JsonDecoder<'T>) : Codec<'T> = {
        empty with
            JsonEncoder = Some encoder
            JsonDecoder = Some decoder
    }

    /// The JSON encoder alone.
    let jsonEncoder (encoder: JsonEncoder<'T>) : Codec<'T> = {
        empty with
            JsonEncoder = Some encoder
    }

    /// The JSON decoder alone.
    let jsonDecoder (decoder: JsonDecoder<'T>) : Codec<'T> = {
        empty with
            JsonDecoder = Some decoder
    }

    /// The MessagePack decoder alone.
    let msgPack (decoder: Decoder<'T>) : Codec<'T> = { empty with MsgPack = Some decoder }

    /// The legs erased to `obj`.
    let erase (codec: Codec<'T>) : ErasedCodec = {
        Encode =
            codec.JsonEncoder
            |> Option.map (fun encode -> fun (value: obj) -> encode (unbox<'T> value))
        DecodeJson =
            codec.JsonDecoder
            |> Option.map (fun decode -> fun value -> decode value |> Result.map box)
        DecodeMsgPack =
            codec.MsgPack
            |> Option.map (fun decode -> fun value -> decode value |> Result.map box)
    }

/// The process-wide codec table and its one seam.
[<RequireQualifiedAccess>]
module Codecs =

    let mutable private table: Map<Scope * string, ErasedCodec> = Map.empty

    /// The table's key for a wire type. One function rather than an expression
    /// at each call site: "what identifies a wire type" must have exactly one
    /// answer, or a registration and a lookup can disagree while both look
    /// right. Public because `register` is `inline`.
    let keyFor (wireType: Type) : string =
        let name = wireType.FullName
        if isNull name then wireType.Name else name

    /// Register already-erased legs under a key. The only writer of the table
    /// and the non-inline half of `register`. A leg the codec carries replaces
    /// the registered one; a leg it does not carry is left as it was, so the
    /// argument decoders and the response encoders of one type may be
    /// registered by different modules.
    let registerErased (scope: Scope) (key: string) (codec: ErasedCodec) : unit =
        let merged =
            match Map.tryFind (scope, key) table with
            | None -> codec
            | Some existing -> {
                Encode = Option.orElse existing.Encode codec.Encode
                DecodeJson = Option.orElse existing.DecodeJson codec.DecodeJson
                DecodeMsgPack = Option.orElse existing.DecodeMsgPack codec.DecodeMsgPack
              }

        table <- Map.add (scope, key) merged table

    /// Register `'T`'s codec legs for `scope`. Idempotent: a second
    /// registration of a leg replaces the first, which is what a re-run of a
    /// composition root has to mean.
    ///
    /// `inline`, and it has to be: Fable erases generics at runtime, so
    /// `typeof<'T>` must resolve at the call site.
    let inline register<'T> (scope: Scope) (codec: Codec<'T>) : unit =
        registerErased scope (keyFor typeof<'T>) (Codec.erase codec)

    /// The leg `pick` selects for `wireType` under `scope`: the record-scoped
    /// registration first, then the unscoped one.
    let private find (pick: ErasedCodec -> 'a option) (scope: Scope) (wireType: Type) : 'a option =
        let key = keyFor wireType

        let at s =
            Map.tryFind (s, key) table |> Option.bind pick

        match scope with
        | Scope.ForRecord _ ->
            match at scope with
            | Some leg -> Some leg
            | None -> at Scope.Unscoped
        | Scope.Unscoped -> at Scope.Unscoped

    /// The registered JSON decoder for `wireType`, policy-free: the census
    /// read the facets and the forwarding registries use, never a decode seam.
    let tryJsonDecoder (scope: Scope) (wireType: Type) : RegisteredJsonDecoder option = find _.DecodeJson scope wireType

    /// The registered JSON encoder for `wireType`, policy-free.
    let tryJsonEncoder (scope: Scope) (wireType: Type) : RegisteredJsonEncoder option = find _.Encode scope wireType

    /// The registered MessagePack decoder for `wireType`, policy-free.
    let tryMsgPack (scope: Scope) (wireType: Type) : RegisteredDecoder option = find _.DecodeMsgPack scope wireType

    /// The refusal a `VerifiedOnly` miss answers with, naming the type and
    /// the leg it has no registration for.
    let unverified (leg: CodecLeg) (wireType: Type) : DecodeError =
        let noun =
            match leg with
            | CodecLeg.EncodeJson -> "JSON encoder"
            | CodecLeg.DecodeJson -> "JSON decoder"
            | CodecLeg.DecodeMsgPack -> "MessagePack decoder"

        DecodeError.create
            (sprintf "a registered %s for %s" noun (keyFor wireType))
            "none; DecodePolicy.VerifiedOnly refuses the reflective path"

    let private resolve (leg: CodecLeg) (found: 'a option) (policy: DecodePolicy) (wireType: Type) =
        match found, policy with
        | Some codec, _ -> Some(Ok codec)
        | None, DecodePolicy.ReflectionFallback -> None
        | None, DecodePolicy.VerifiedOnly -> Some(Error(unverified leg wireType))

    /// THE seam for reading JSON. `Some (Ok decoder)` for a registered type;
    /// `None` — the reflective reader — for a miss under `ReflectionFallback`;
    /// `Some (Error refusal)` naming the type for a miss under `VerifiedOnly`.
    let resolveJson
        (policy: DecodePolicy)
        (scope: Scope)
        (wireType: Type)
        : Result<RegisteredJsonDecoder, DecodeError> option =
        resolve CodecLeg.DecodeJson (tryJsonDecoder scope wireType) policy wireType

    /// The seam for writing JSON, with `resolveJson`'s three answers.
    let resolveJsonEncoder
        (policy: DecodePolicy)
        (scope: Scope)
        (wireType: Type)
        : Result<RegisteredJsonEncoder, DecodeError> option =
        resolve CodecLeg.EncodeJson (tryJsonEncoder scope wireType) policy wireType

    /// The seam for reading MessagePack, with `resolveJson`'s three answers.
    let resolveMsgPack
        (policy: DecodePolicy)
        (scope: Scope)
        (wireType: Type)
        : Result<RegisteredDecoder, DecodeError> option =
        resolve CodecLeg.DecodeMsgPack (tryMsgPack scope wireType) policy wireType

    let private carries (leg: CodecLeg) (codec: ErasedCodec) =
        match leg with
        | CodecLeg.EncodeJson -> codec.Encode.IsSome
        | CodecLeg.DecodeJson -> codec.DecodeJson.IsSome
        | CodecLeg.DecodeMsgPack -> codec.DecodeMsgPack.IsSome

    /// Every `(scope, key)` registering `leg`, ordered: unscoped first, then
    /// by record, each by key.
    let registered (leg: CodecLeg) : (Scope * string) list =
        table
        |> Map.toList
        |> List.filter (fun (_, codec) -> carries leg codec)
        |> List.map fst

    /// Test-only: drop `leg` from every registration. Public because
    /// `Platform.Core` names one friend assembly and the test packs are not
    /// it; the name is the guard.
    let resetLegForTests (leg: CodecLeg) : unit =
        let drop (codec: ErasedCodec) =
            match leg with
            | CodecLeg.EncodeJson -> { codec with Encode = None }
            | CodecLeg.DecodeJson -> { codec with DecodeJson = None }
            | CodecLeg.DecodeMsgPack -> { codec with DecodeMsgPack = None }

        table <-
            table
            |> Map.map (fun _ codec -> drop codec)
            |> Map.filter (fun _ codec -> codec.Encode.IsSome || codec.DecodeJson.IsSome || codec.DecodeMsgPack.IsSome)

    /// Test-only: empty the table.
    let resetForTests () : unit = table <- Map.empty