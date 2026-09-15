// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting

open System
open ToolUp.Remoting.MsgPack

// ─── Phase 785 — total combinators over the closed value model ───────
//
// A `Decoder<'T>` is a PURE FUNCTION from a `Value` to either the
// declared type or a named refusal. Four properties hold of every
// combinator in this file, and all four are checkable rather than
// asserted — `DecoderAlgebraTests` pins each:
//
//   1. **Total.** Every combinator returns `Ok` or `Error` on every
//      `Value`. Nothing throws, nothing raises `DecodeException`, and
//      there is no `failwith` in the module.
//   2. **Pure.** No mutable state, no cursor, no cache, no clock. A
//      decoder applied twice to one value answers twice the same.
//   3. **Reflection-free.** No `FSharp.Reflection`, no `typeof`
//      dispatch, no `System.Type` at all. A decoder is a closed match
//      over nine cases, which is why its totality is a property a
//      compiler checks.
//   4. **Structurally recursive.** Every recursive combinator descends
//      only into a SUBTERM of its input, whose `Value.size` is strictly
//      smaller — so the recursion is well-founded by the model's own
//      measure rather than by a depth counter.
//
// **Width is refused, never cast.** `asInt32` applied to an `int64`
// value that does not fit an `int32` is an `Error`, not a narrowing.
// That is the whole reason the value model carries the source width
// class: Phase 786 established that the rule is about INFORMATION and
// not about signed range (`writeSByte` puts `-128y` on the wire as
// `uint8 128`), so the combinators apply `Value.signedFits` /
// `Value.unsignedFits` and never a range test of their own.
//
// **Records are positional on this wire, and `field` is what makes the
// PATH named anyway.** The writer emits a record as an array of its
// fields in declaration order (`Write.writeRecord`), so there are no
// keys to look a field up by. `field "Name" 0` decodes element 0 and
// annotates any refusal beneath it with `Name`, so a consumer reads
// `expected string at Probes.Name, got nil` rather than a byte offset.
// The path is therefore a property of the DECODER rather than of the
// wire — which is exactly what a hand-written or generated decoder can
// supply and a reflection walk cannot.
//
// **The applicative pipeline is the shape a generator emits.** A record
// decoder is `succeed ctor |> apply (field …) |> apply (field …)`, one
// `apply` per field, in declaration order. Phase 69k's generator emits
// exactly this from the record's own shape, so Phase 787's theorem
// covers generated decoders by construction rather than by a second
// proof.

/// Phase 785 — a decoder for `'T`: total, pure, reflection-free.
type Decoder<'T> = Value -> Result<'T, DecodeError>

[<RequireQualifiedAccess>]
module Decode =

    // ─── The refusal ─────────────────────────────────────────────────

    /// A refusal at the value in hand. `expected` names the type or
    /// shape the decoder required; `found` is the value's own
    /// description, never a rendering of its contents.
    let private refuse (expected: string) (value: Value) : Result<'T, DecodeError> =
        Error(DecodeError.create expected (Value.describe value))

    /// A refusal whose `Found` text the caller supplies — the
    /// out-of-range arms, where "what was found" is the value AND the
    /// reason it does not fit.
    let private refuseWith (expected: string) (found: string) : Result<'T, DecodeError> =
        Error(DecodeError.create expected found)

    // ─── The monad / applicative ─────────────────────────────────────

    /// A decoder that ignores its input and succeeds. The head of every
    /// applicative pipeline.
    let succeed (value: 'T) : Decoder<'T> = fun _ -> Ok value

    /// A decoder that ignores its input and refuses, naming what was
    /// expected. The arm a `union` dispatch takes on an unknown tag.
    let fail (expected: string) : Decoder<'T> = fun value -> refuse expected value

    let map (f: 'A -> 'B) (decoder: Decoder<'A>) : Decoder<'B> =
        fun value -> decoder value |> Result.map f

    let bind (f: 'A -> Decoder<'B>) (decoder: Decoder<'A>) : Decoder<'B> =
        fun value ->
            match decoder value with
            | Ok decoded -> f decoded value
            | Error error -> Error error

    /// Apply one field decoder in an applicative pipeline. Short-circuits
    /// on the FIRST refusal: the remaining fields are not decoded, so a
    /// refusal costs nothing beyond the point it was found.
    let apply (argument: Decoder<'A>) (fn: Decoder<'A -> 'B>) : Decoder<'B> =
        fun value ->
            match fn value with
            | Error error -> Error error
            | Ok f ->
                match argument value with
                | Error error -> Error error
                | Ok decoded -> Ok(f decoded)

    // ─── Scalars ─────────────────────────────────────────────────────

    let asBool: Decoder<bool> =
        function
        | Value.Bool b -> Ok b
        | value -> refuse "bool" value

    /// The `nil` value and nothing else. `unit` rides this arm: the
    /// writer emits `unit` as `nil`.
    let asUnit: Decoder<unit> =
        function
        | Value.Nil -> Ok()
        | value -> refuse "nil" value

    let asString: Decoder<string> =
        function
        | Value.Str text -> Ok text
        | value -> refuse "string" value

    /// A one-character string. The writer emits `char` through the
    /// string arm, so this is a string plus a length requirement — and
    /// the length requirement is a refusal rather than a silent
    /// `str.[0]`, which is what the reflection reader does.
    let asChar: Decoder<char> =
        function
        | Value.Str text when text.Length = 1 -> Ok text.[0]
        | Value.Str text -> refuseWith "a one-character string" (sprintf "string of %d character(s)" text.Length)
        | value -> refuse "char" value

    /// The integer arms, one per target width. Each applies Phase 786's
    /// information rule against the SOURCE width the format byte
    /// declared, so a same-width reinterpretation survives and a genuine
    /// narrowing refuses.
    let private integer
        (typeName: string)
        (targetBits: int)
        (lo: int64)
        (hi: uint64)
        (ofSigned: int64 -> 'T)
        (ofUnsigned: uint64 -> 'T)
        : Decoder<'T> =
        function
        | Value.Int(n, width) ->
            if Value.signedFits (IntegerWidth.bits width) targetBits lo hi n then
                Ok(ofSigned n)
            else
                refuseWith typeName (sprintf "out-of-range integer %s" (string n))
        | Value.UInt(n, width) ->
            if Value.unsignedFits (IntegerWidth.bits width) targetBits hi n then
                Ok(ofUnsigned n)
            else
                refuseWith typeName (sprintf "out-of-range integer %s" (string n))
        | value -> refuse typeName value

    let asInt32: Decoder<int> = integer "Int32" 32 -2147483648L 2147483647UL int32 int32

    let asInt64: Decoder<int64> =
        integer "Int64" 64 Int64.MinValue 9223372036854775807UL int64 int64

    let asInt16: Decoder<int16> = integer "Int16" 16 -32768L 32767UL int16 int16

    let asSByte: Decoder<sbyte> = integer "SByte" 8 -128L 127UL sbyte sbyte

    let asByte: Decoder<byte> = integer "Byte" 8 0L 255UL byte byte

    let asUInt16: Decoder<uint16> = integer "UInt16" 16 0L 65535UL uint16 uint16

    let asUInt32: Decoder<uint32> = integer "UInt32" 32 0L 4294967295UL uint32 uint32

    let asUInt64: Decoder<uint64> =
        integer "UInt64" 64 0L 18446744073709551615UL uint64 uint64

    /// `TimeSpan` is a tick count on the wire — `Write.writeTimeSpan` is
    /// `writeInt64 ts.Ticks`, so it decodes through the `Int64` arm and
    /// inherits its width rule exactly.
    let asTimeSpan: Decoder<TimeSpan> =
        integer "TimeSpan" 64 Int64.MinValue 9223372036854775807UL (fun (n: int64) -> TimeSpan n) (fun (n: uint64) ->
            TimeSpan(int64 n))

    /// `float` accepts either IEEE width: a `float32` widened to a
    /// `float` is exact, so refusing it would refuse well-formed traffic.
    let asFloat: Decoder<float> =
        function
        | Value.Float(n, _) -> Ok n
        | value -> refuse "Double" value

    /// `float32` accepts ONLY a `float32` source. A `float` narrowed to a
    /// `float32` is the lossy direction and is refused, for the reason
    /// `asInt32` refuses a wide integer: the typed contract said one
    /// thing and the wire would deliver another.
    let asFloat32: Decoder<float32> =
        function
        | Value.Float(n, FloatWidth.Single) -> Ok(float32 n)
        | Value.Float(n, FloatWidth.Double) ->
            refuseWith "Single" (sprintf "float64 %s, which a Single cannot carry without loss" (string n))
        | value -> refuse "Single" value

    /// The `bin` family. Returns the payload as handed over — nothing in
    /// this module copies or mutates it.
    let asBytes: Decoder<byte[]> =
        function
        | Value.Bin bytes -> Ok bytes
        | value -> refuse "Byte[]" value

    /// A `Guid` is sixteen `bin` bytes (`Write.writeGuid`). The length is
    /// a refusal rather than a constructor throw.
    let asGuid: Decoder<Guid> =
        function
        | Value.Bin bytes when bytes.Length = 16 -> Ok(Guid bytes)
        | Value.Bin bytes -> refuseWith "Guid" (sprintf "bin of %d byte(s), not 16" bytes.Length)
        | value -> refuse "Guid" value

    // ─── Containers ──────────────────────────────────────────────────

    /// The elements of an array value.
    let items: Decoder<Value list> =
        function
        | Value.Arr elements -> Ok elements
        | value -> refuse "array" value

    /// An array of exactly `arity` elements. The arity check is what
    /// turns "the writer emitted a record of five fields and this
    /// decoder reads six" into a named refusal rather than a missing
    /// element read as absent.
    let exactly (arity: int) : Decoder<Value list> =
        function
        | Value.Arr elements when List.length elements = arity -> Ok elements
        | Value.Arr elements ->
            refuseWith
                (sprintf "an array of %d element(s)" arity)
                (sprintf "an array of %d element(s)" (List.length elements))
        | value -> refuse (sprintf "an array of %d element(s)" arity) value

    /// Decode element `index` of an array, annotating any refusal
    /// beneath it with `[index]`.
    let index (position: int) (decoder: Decoder<'T>) : Decoder<'T> =
        fun value ->
            match value with
            | Value.Arr elements ->
                match List.tryItem position elements with
                | Some element ->
                    // Phase 804 — the path segment is built ONLY on the refusal
                    // branch, as `list` and `entries` already do. It used to be
                    // the eagerly-evaluated argument of the `Result.mapError`
                    // function, so every SUCCESSFUL decode through `index` ran
                    // `sprintf` — the one printf-family call on the algebra's
                    // happy path, and under native AOT a fail-fast: F#'s printf
                    // builds its formatter through `MethodInfo.MakeGenericMethod`,
                    // which that runtime refuses. Found by the AOT sample's first
                    // native run, on `asDecimal`'s four-word read.
                    match decoder element with
                    | Ok decoded -> Ok decoded
                    | Error error -> Error(DecodeError.under (sprintf "[%d]" position) error)
                | None ->
                    refuseWith
                        (sprintf "an array with an element at index %d" position)
                        (sprintf "an array of %d element(s)" (List.length elements))
            | _ -> refuse (sprintf "an array with an element at index %d" position) value

    /// Decode element `position` of an array, annotating any refusal
    /// beneath it with the field's NAME. The combinator that gives a
    /// positional wire a named path — see the file header.
    let field (name: string) (position: int) (decoder: Decoder<'T>) : Decoder<'T> =
        fun value ->
            match value with
            | Value.Arr elements ->
                match List.tryItem position elements with
                | Some element -> decoder element |> Result.mapError (DecodeError.under name)
                | None ->
                    refuseWith
                        (sprintf "a record with a `%s` field at index %d" name position)
                        (sprintf "an array of %d element(s)" (List.length elements))
            | _ -> refuse (sprintf "a record with a `%s` field at index %d" name position) value

    /// Every element of an array, in order, through one element decoder.
    ///
    /// **Structural and short-circuiting.** It descends only into
    /// elements — each strictly smaller than the array by `Value.size` —
    /// and stops at the first refusal, annotating it with the element's
    /// index so a refusal fifty elements in says which one.
    let list (element: Decoder<'T>) : Decoder<'T list> =
        fun value ->
            match value with
            | Value.Arr elements ->
                let rec walk position remaining accumulated =
                    match remaining with
                    | [] -> Ok(List.rev accumulated)
                    | head :: tail ->
                        match element head with
                        | Ok decoded -> walk (position + 1) tail (decoded :: accumulated)
                        | Error error -> Error(DecodeError.under (sprintf "[%d]" position) error)

                walk 0 elements []
            | _ -> refuse "array" value

    /// The array form of `list`.
    let array (element: Decoder<'T>) : Decoder<'T[]> = list element |> map List.toArray

    /// The Set form of `list`. A `Set<_>` is written as an array
    /// (`Write.writeSet`), so the wire shape is the same and only the
    /// collection it lands in differs.
    let asSet<'T when 'T: comparison> (element: Decoder<'T>) : Decoder<Set<'T>> = list element |> map Set.ofList

    /// Every entry of a map value, through one key decoder and one value
    /// decoder, in wire order.
    ///
    /// Structural and short-circuiting like `list`: it descends only
    /// into an entry's key or value, and a refusal names which entry and
    /// which half of it.
    let entries (key: Decoder<'K>) (entry: Decoder<'V>) : Decoder<('K * 'V) list> =
        fun value ->
            match value with
            | Value.Map pairs ->
                let rec walk position remaining accumulated =
                    match remaining with
                    | [] -> Ok(List.rev accumulated)
                    | (k, v) :: tail ->
                        match key k with
                        | Error error -> Error(DecodeError.under (sprintf "[%d].key" position) error)
                        | Ok decodedKey ->
                            match entry v with
                            | Error error -> Error(DecodeError.under (sprintf "[%d].value" position) error)
                            | Ok decodedValue -> walk (position + 1) tail ((decodedKey, decodedValue) :: accumulated)

                walk 0 pairs []
            | _ -> refuse "map" value

    /// An F# `Map`, from the wire's map family.
    let asMap<'K, 'V when 'K: comparison> (key: Decoder<'K>) (entry: Decoder<'V>) : Decoder<Map<'K, 'V>> =
        entries key entry |> map Map.ofList

    // ─── Unions ──────────────────────────────────────────────────────

    /// What a union case does with its payload.
    ///
    /// The payload is a `Value option` rather than a `Value`, and the
    /// distinction is load-bearing: a case with NO fields is written as
    /// `[tag]` and has no payload slot at all, while a case with one
    /// field writes the field DIRECTLY into the second slot and a case
    /// with several writes them as an inner array there
    /// (`Write.writeUnion`). A single-field case whose field is itself
    /// an array is therefore indistinguishable from a multi-field case
    /// by inspection — only the case's own arity tells them apart, so
    /// the case decoder is handed the raw payload and decides.
    type UnionCase<'T> = Value option -> Result<'T, DecodeError>

    /// A union case carrying no fields. Refuses a payload: a `[tag;
    /// field]` term decoded as a zero-field case would silently drop the
    /// field.
    let case0 (value: 'T) : UnionCase<'T> =
        function
        | None -> Ok value
        | Some payload -> refuseWith "a union case with no fields" (Value.describe payload)

    /// A union case carrying a payload, decoded by `decoder`. Covers
    /// both the single-field shape (the decoder reads the field) and the
    /// several-field shape (the decoder is an applicative pipeline over
    /// the inner array).
    let payload (decoder: Decoder<'T>) : UnionCase<'T> =
        function
        | Some value -> decoder value
        | None -> refuseWith "a union case carrying a payload" "a union case with no payload"

    /// Dispatch on the tag the writer emits.
    ///
    /// `cases` answers for a tag it recognises and `None` for one it does
    /// not — an unrecognised tag is a refusal naming the type and the
    /// tag, never a fallback case. That is the arm the reflection reader
    /// cannot make total: it resolves a tag with `Array.find`, which
    /// throws.
    let union (typeName: string) (cases: int -> UnionCase<'T> option) : Decoder<'T> =
        fun value ->
            // The case decoder's refusal is carried out UNANNOTATED: a
            // union is one wire term, and pushing `Option[1]` onto the
            // path of every `Some` field would bury the field name the
            // reader actually came for under a segment per wrapper.
            let dispatch (tag: int) (carried: Value option) =
                match cases tag with
                | Some decodeCase -> decodeCase carried
                | None -> refuseWith typeName (sprintf "union tag %d, which names no case" tag)

            match value with
            | Value.Arr [ tag ] -> asInt32 tag |> Result.bind (fun tag -> dispatch tag None)
            | Value.Arr [ tag; carried ] -> asInt32 tag |> Result.bind (fun tag -> dispatch tag (Some carried))
            | _ -> refuse (sprintf "%s (a union term of [tag] or [tag; payload])" typeName) value

    /// A union carrying `[<StringEnum>]` is written as its CASE NAME, a
    /// string, rather than as a tag (`Write.writeStringEnum`).
    ///
    /// **It is the ATTRIBUTE that selects this shape, not the absence of
    /// fields.** A union whose cases all carry no fields is still written
    /// as `[tag]` unless it is attributed — `Write.makeSerializerAux`
    /// tests for `StringEnumAttribute` and nothing else — so a
    /// field-less union takes `union` with `case0` arms, and only an
    /// attributed one takes this. The caller supplies the wire names
    /// explicitly because the writer CAMEL-CASES the first letter of the
    /// case name; a combinator that derived them would have to duplicate
    /// that convention and could drift from it.
    let stringEnum (typeName: string) (cases: (string * 'T) list) : Decoder<'T> =
        fun value ->
            match value with
            | Value.Str name ->
                match
                    cases
                    |> List.tryPick (fun (label, case) -> if label = name then Some case else None)
                with
                | Some case -> Ok case
                | None -> refuseWith typeName (sprintf "case name `%s`, which names no case" name)
            | _ -> refuse typeName value

    /// `option` is an ordinary two-case union on this wire: `None` is
    /// tag 0 with no fields, `Some` is tag 1 carrying the value.
    let option (inner: Decoder<'T>) : Decoder<'T option> =
        union "Option" (function
            | 0 -> Some(case0 None)
            | 1 -> Some(payload (inner |> map Some))
            | _ -> None)

    /// `Result` is likewise an ordinary two-case union, and it is the
    /// established remoting failure shape — every `_platform.*` method
    /// returns one.
    let result (ok: Decoder<'T>) (error: Decoder<'E>) : Decoder<Result<'T, 'E>> =
        union "Result" (function
            | 0 -> Some(payload (ok |> map Ok))
            | 1 -> Some(payload (error |> map Error))
            | _ -> None)

    // ─── Composite scalars written as arrays ─────────────────────────

    /// `DateTime` is `[ticks; kind]` (`Write.writeDateTime`).
    ///
    /// The kind is mapped by the same total rule the reflection reader
    /// applies — 1 is UTC, 2 is Local, anything else Unspecified — so
    /// the two paths agree on a payload carrying a kind byte neither
    /// enum value names.
    let asDateTime: Decoder<DateTime> =
        fun value ->
            match exactly 2 value with
            | Error error -> Error(DecodeError.under "DateTime" error)
            | Ok _ ->
                (succeed (fun ticks kind ->
                    let kind =
                        match kind with
                        | 1L -> DateTimeKind.Utc
                        | 2L -> DateTimeKind.Local
                        | _ -> DateTimeKind.Unspecified

                    DateTime(ticks, kind))
                 |> apply (field "Ticks" 0 asInt64)
                 |> apply (field "Kind" 1 asInt64))
                    value

    /// `DateTimeOffset` is `[ticks; offsetMinutes]`
    /// (`Write.writeDateTimeOffset`).
    let asDateTimeOffset: Decoder<DateTimeOffset> =
        fun value ->
            match exactly 2 value with
            | Error error -> Error(DecodeError.under "DateTimeOffset" error)
            | Ok _ ->
                (succeed (fun (ticks: int64) (minutes: int64) ->
                    DateTimeOffset(ticks, TimeSpan.FromMinutes(float minutes)))
                 |> apply (field "Ticks" 0 asInt64)
                 |> apply (field "OffsetMinutes" 1 asInt64))
                    value

    /// `decimal` is its four 32-bit words (`Write.writeDecimal`), each
    /// written through `write32bitNumber` — so a negative word arrives
    /// as a `uint32` and the `Int32` target recovers the sign. That is
    /// the same-width reinterpretation `asInt32` admits by Phase 786's
    /// second rule, and refusing it would refuse every negative decimal
    /// the corpus carries.
    let asDecimal: Decoder<decimal> =
        fun value ->
            match exactly 4 value with
            | Error error -> Error(DecodeError.under "Decimal" error)
            | Ok _ ->
                (succeed (fun lo mid hi flags -> Decimal [| lo; mid; hi; flags |])
                 |> apply (index 0 asInt32)
                 |> apply (index 1 asInt32)
                 |> apply (index 2 asInt32)
                 |> apply (index 3 asInt32))
                    value

    // ─── Running one ─────────────────────────────────────────────────

    /// Run a decoder. Present so a call site reads in the order it
    /// happens — `Decode.run decoder value` rather than `decoder value`
    /// — and so the seam has one name to grep for.
    let run (decoder: Decoder<'T>) (value: Value) : Result<'T, DecodeError> = decoder value