// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Json

open System
open ToolUp.Remoting

// ─── Phase 799 — total combinators over the closed JSON value model ──
//
// The Phase 785 algebra, over `JsonValue` instead of `MsgPack.Value`.
// The four properties are the same and are pinned the same way
// (`JsonDecoderAlgebraTests`): every combinator is TOTAL (an `Ok` or an
// `Error` on every value, nothing throws), PURE, REFLECTION-FREE (a
// closed match over six cases) and STRUCTURALLY RECURSIVE (descends only
// into a subterm, whose `JsonValue.size` is strictly smaller).
//
// Where this wire differs from the MessagePack one, and how the
// combinators differ with it:
//
// **Records are NAMED here, not positional.** The STJ converter set
// writes a record as an object keyed by its field names, so `field` looks
// a member up BY NAME and the position argument the MsgPack `field`
// takes does not exist. An absent member is a refusal naming the member;
// `optionalField` is the arm for a field declared `option`, where the
// writer's `None` is `null` and an older client's omission reads the
// same way.
//
// **Numbers carry their text, and width is read from it.** There is no
// source-width class on a text wire — `1` and `1.0` differ in FORM,
// which is the only width a JSON token has — so the integer combinators
// require an integral token (no fraction, no exponent) and then a range
// fit, and `asDecimal` reads the 28 digits `decimal` carries straight
// from the digits written. `asInt64` and `asUInt64` accept the STRING
// form too, because that is how the writer emits them (`"+42"` — a
// leading sign, so a JavaScript reader cannot mistake it for a float).
//
// **`TimeSpan` is exact by construction.** The writer emits total
// milliseconds as a double, and the STJ reader multiplies that double
// back and TRUNCATES, which is the one-tick loss Phase 784 measured.
// `asTimeSpan` parses the token as a `decimal`, scales by ten thousand in
// decimal arithmetic and rounds to the nearest tick — recovering the
// tick the writer started from for every span the double could carry.
//
// **Unions dispatch on the CASE NAME.** A field-less case is its name as
// a string; a case with fields is a one-member object `{"Case": payload}`
// with the payload either the single field or an array of several. That
// is the shape the writer emits (`FSharpUnionConverter.Write`) and the
// shape the Fable client's serialiser produces; the three legacy READ
// shapes the STJ converter also tolerates (`{tag,name,fields}`,
// `__typename`, `["Case", …]`) are not admitted — a decoder accepts what
// the writer writes.

/// Phase 799 — a JSON decoder for `'T`: total, pure, reflection-free.
type JsonDecoder<'T> = JsonValue -> Result<'T, DecodeError>

/// The total combinators over `JsonValue` — see the file header for the
/// four properties every one of them holds, and how this wire's shapes
/// differ from the MessagePack wire's.
[<RequireQualifiedAccess>]
module JsonDecode =

    // ─── The refusal ─────────────────────────────────────────────────

    let private refuse (expected: string) (value: JsonValue) : Result<'T, DecodeError> =
        Error(DecodeError.create expected (JsonValue.describe value))

    let private refuseWith (expected: string) (found: string) : Result<'T, DecodeError> =
        Error(DecodeError.create expected found)

    // ─── The monad / applicative ─────────────────────────────────────

    /// A decoder that ignores its input and succeeds. The head of every
    /// applicative pipeline.
    let succeed (value: 'T) : JsonDecoder<'T> = fun _ -> Ok value

    /// A decoder that ignores its input and refuses, naming what was
    /// expected.
    let fail (expected: string) : JsonDecoder<'T> = fun value -> refuse expected value

    /// Map over a decoder's result.
    let map (f: 'A -> 'B) (decoder: JsonDecoder<'A>) : JsonDecoder<'B> =
        fun value -> decoder value |> Result.map f

    /// Sequence a decoder with one chosen from its result, over the same
    /// value.
    let bind (f: 'A -> JsonDecoder<'B>) (decoder: JsonDecoder<'A>) : JsonDecoder<'B> =
        fun value ->
            match decoder value with
            | Ok decoded -> f decoded value
            | Error error -> Error error

    /// Apply one field decoder in an applicative pipeline. Short-circuits
    /// on the FIRST refusal.
    let apply (argument: JsonDecoder<'A>) (fn: JsonDecoder<'A -> 'B>) : JsonDecoder<'B> =
        fun value ->
            match fn value with
            | Error error -> Error error
            | Ok f ->
                match argument value with
                | Error error -> Error error
                | Ok decoded -> Ok(f decoded)

    // ─── Scalars ─────────────────────────────────────────────────────

    /// A JSON boolean.
    let asBool: JsonDecoder<bool> =
        function
        | JsonValue.Bool b -> Ok b
        | value -> refuse "bool" value

    /// `null` and nothing else. `unit` rides this arm.
    let asUnit: JsonDecoder<unit> =
        function
        | JsonValue.Null -> Ok()
        | value -> refuse "null" value

    /// A JSON string. `null` is refused — the model has no null, and a
    /// declared `string` is a string.
    let asString: JsonDecoder<string> =
        function
        | JsonValue.String text -> Ok text
        | value -> refuse "string" value

    /// A one-character string; the length requirement is a refusal.
    let asChar: JsonDecoder<char> =
        function
        | JsonValue.String text when text.Length = 1 -> Ok text.[0]
        | JsonValue.String text -> refuseWith "a one-character string" (sprintf "string of %d character(s)" text.Length)
        | value -> refuse "char" value

    /// The signed integer arms: an integral token that fits the target's
    /// range. `lo`/`hi` are the target's bounds.
    let private signed (typeName: string) (lo: int64) (hi: int64) (convert: int64 -> 'T) : JsonDecoder<'T> =
        function
        | JsonValue.Number text as value ->
            match JsonValue.tryInt64 text with
            | Some n when n >= lo && n <= hi -> Ok(convert n)
            | Some n -> refuseWith typeName (sprintf "number %s, which does not fit %s" (string n) typeName)
            | None when JsonValue.isNumberToken text ->
                refuseWith typeName (sprintf "number %s, which is not an integer" text)
            | None -> refuse typeName value
        | value -> refuse typeName value

    let private unsigned (typeName: string) (hi: uint64) (convert: uint64 -> 'T) : JsonDecoder<'T> =
        function
        | JsonValue.Number text as value ->
            match JsonValue.tryUInt64 text with
            | Some n when n <= hi -> Ok(convert n)
            | Some n -> refuseWith typeName (sprintf "number %s, which does not fit %s" (string n) typeName)
            | None when JsonValue.isNumberToken text ->
                refuseWith typeName (sprintf "number %s, which is not a non-negative integer" text)
            | None -> refuse typeName value
        | value -> refuse typeName value

    /// An integral token that fits `int32`.
    let asInt32: JsonDecoder<int> = signed "Int32" -2147483648L 2147483647L int32

    /// An integral token that fits `int16`.
    let asInt16: JsonDecoder<int16> = signed "Int16" -32768L 32767L int16

    /// An integral token that fits `sbyte`.
    let asSByte: JsonDecoder<sbyte> = signed "SByte" -128L 127L sbyte

    /// A non-negative integral token that fits `byte`.
    let asByte: JsonDecoder<byte> = unsigned "Byte" 255UL byte

    /// A non-negative integral token that fits `uint16`.
    let asUInt16: JsonDecoder<uint16> = unsigned "UInt16" 65535UL uint16

    /// A non-negative integral token that fits `uint32`.
    let asUInt32: JsonDecoder<uint32> = unsigned "UInt32" 4294967295UL uint32

    /// `int64` — a number token, or the STRING the writer emits (a
    /// leading `+` or `-` sign; `Int64Converter.Write`).
    let asInt64: JsonDecoder<int64> =
        function
        | JsonValue.Number text as value ->
            match JsonValue.tryInt64 text with
            | Some n -> Ok n
            | None when JsonValue.isNumberToken text ->
                refuseWith "Int64" (sprintf "number %s, which does not fit Int64" text)
            | None -> refuse "Int64" value
        | JsonValue.String text ->
            let unsigned' = if text.StartsWith "+" then text.Substring 1 else text

            match JsonValue.tryInt64 unsigned' with
            | Some n -> Ok n
            | None -> refuseWith "Int64" (sprintf "string `%s`, which is not a signed integer" text)
        | value -> refuse "Int64" value

    /// `uint64` — a number token, or the digit string the writer emits.
    let asUInt64: JsonDecoder<uint64> =
        function
        | JsonValue.Number text as value ->
            match JsonValue.tryUInt64 text with
            | Some n -> Ok n
            | None when JsonValue.isNumberToken text ->
                refuseWith "UInt64" (sprintf "number %s, which does not fit UInt64" text)
            | None -> refuse "UInt64" value
        | JsonValue.String text ->
            match JsonValue.tryUInt64 text with
            | Some n -> Ok n
            | None -> refuseWith "UInt64" (sprintf "string `%s`, which is not an unsigned integer" text)
        | value -> refuse "UInt64" value

    /// A number token, correctly rounded to double precision.
    let asFloat: JsonDecoder<float> =
        function
        | JsonValue.Number text as value ->
            match JsonValue.tryFloat text with
            | Some f -> Ok f
            | None -> refuse "Float" value
        | value -> refuse "Float" value

    /// `float32` — the token correctly rounded to single precision, which
    /// is what "declared float32" means on a wire that carries no width.
    let asFloat32: JsonDecoder<float32> = asFloat |> map float32

    /// `decimal` — the digits as written, exactly. The whole point of the
    /// lexical carrier: `79228162514264337593543950335` arrives as itself.
    let asDecimal: JsonDecoder<decimal> =
        function
        | JsonValue.Number text as value ->
            match JsonValue.tryDecimal text with
            | Some d -> Ok d
            | None when JsonValue.isNumberToken text ->
                refuseWith "Decimal" (sprintf "number %s, which does not fit Decimal" text)
            | None -> refuse "Decimal" value
        | value -> refuse "Decimal" value

    /// A GUID in any form `Guid.TryParse` reads.
    let asGuid: JsonDecoder<Guid> =
        function
        | JsonValue.String text ->
            match Guid.TryParse text with
            | true, g -> Ok g
            | _ -> refuseWith "Guid" (sprintf "string `%s`, which is not a GUID" text)
        | value -> refuse "Guid" value

    /// `TimeSpan` — total milliseconds (`TimeSpanConverter.Write`), read
    /// EXACTLY: the token as a decimal, scaled to ticks in decimal
    /// arithmetic, rounded to the nearest tick. See the header for why
    /// this recovers the tick the STJ reader truncates away.
    let asTimeSpan: JsonDecoder<TimeSpan> =
        function
        | JsonValue.Number text as value ->
            match JsonValue.tryDecimal text with
            | Some ms ->
                let ticks = ms * 10000M

                // Nearest tick, half away from zero — spelled out rather
                // than `Math.Round(…, MidpointRounding)` so both hosts
                // compute it identically.
                let rounded =
                    if ticks >= 0M then
                        Decimal.Truncate(ticks + 0.5M)
                    else
                        Decimal.Truncate(ticks - 0.5M)

                if rounded >= -9223372036854775808M && rounded <= 9223372036854775807M then
                    Ok(TimeSpan.FromTicks(int64 rounded))
                else
                    refuseWith "TimeSpan" (sprintf "number %s milliseconds, which does not fit TimeSpan" text)
            | None -> refuse "TimeSpan" value
        | value -> refuse "TimeSpan" value

    /// `DateTime` — the ISO-8601 round-trip string the writer emits, read
    /// with its kind preserved (`DateTimeConverter.Read`'s rule).
    let asDateTime: JsonDecoder<DateTime> =
        function
        | JsonValue.String text ->
#if FABLE_COMPILER
            match DateTime.TryParse text with
            | true, d -> Ok d
            | _ -> refuseWith "DateTime" (sprintf "string `%s`, which is not an ISO-8601 date-time" text)
#else
            match
                DateTime.TryParse(
                    text,
                    Globalization.CultureInfo.InvariantCulture,
                    Globalization.DateTimeStyles.RoundtripKind
                )
            with
            | true, d -> Ok d
            | _ -> refuseWith "DateTime" (sprintf "string `%s`, which is not an ISO-8601 date-time" text)
#endif
        | value -> refuse "DateTime" value

    /// `DateTimeOffset` — the ISO-8601 string the writer emits, offset
    /// preserved.
    let asDateTimeOffset: JsonDecoder<DateTimeOffset> =
        function
        | JsonValue.String text ->
#if FABLE_COMPILER
            match DateTimeOffset.TryParse text with
            | true, d -> Ok d
            | _ -> refuseWith "DateTimeOffset" (sprintf "string `%s`, which is not an ISO-8601 date-time" text)
#else
            match
                DateTimeOffset.TryParse(
                    text,
                    Globalization.CultureInfo.InvariantCulture,
                    Globalization.DateTimeStyles.RoundtripKind
                )
            with
            | true, d -> Ok d
            | _ -> refuseWith "DateTimeOffset" (sprintf "string `%s`, which is not an ISO-8601 date-time" text)
#endif
        | value -> refuse "DateTimeOffset" value

#if !FABLE_COMPILER
    /// `DateOnly` — the day number (`DateOnlyConverter.Write`), bounded by
    /// the type's own domain. .NET only, as on the MsgPack side.
    let asDateOnly: JsonDecoder<DateOnly> =
        function
        | JsonValue.Number text as value ->
            match JsonValue.tryInt64 text with
            | Some n when n >= 0L && n <= int64 DateOnly.MaxValue.DayNumber -> Ok(DateOnly.FromDayNumber(int n))
            | Some n -> refuseWith "DateOnly" (sprintf "day number %s, which is outside DateOnly's range" (string n))
            | None -> refuse "DateOnly" value
        | value -> refuse "DateOnly" value

    /// `TimeOnly` — its ticks as a STRING (`TimeOnlyConverter.Write`), or
    /// a number, bounded by the type's own domain.
    let asTimeOnly: JsonDecoder<TimeOnly> =
        let ofTicks (text: string) (value: JsonValue) =
            match JsonValue.tryInt64 text with
            | Some n when n >= 0L && n <= TimeOnly.MaxValue.Ticks -> Ok(TimeOnly n)
            | Some n -> refuseWith "TimeOnly" (sprintf "tick count %s, which is outside TimeOnly's range" (string n))
            | None -> refuse "TimeOnly" value

        function
        | JsonValue.String text as value -> ofTicks text value
        | JsonValue.Number text as value -> ofTicks text value
        | value -> refuse "TimeOnly" value
#endif

    /// `byte[]` — base64 (`ByteArrayConverter.Write`), or the array of
    /// byte-valued numbers the Fable client sends.
    let asBytes: JsonDecoder<byte[]> =
        function
        | JsonValue.String text ->
            try
                Ok(Convert.FromBase64String text)
            with _ ->
                refuseWith "byte[]" (sprintf "string of %d character(s), which is not base64" text.Length)
        | JsonValue.Array items ->
            let rec go (remaining: JsonValue list) (index: int) (acc: byte list) =
                match remaining with
                | [] -> Ok(acc |> List.rev |> List.toArray)
                | item :: rest ->
                    match asByte item with
                    | Ok b -> go rest (index + 1) (b :: acc)
                    | Error error -> Error(DecodeError.under (sprintf "[%d]" index) error)

            go items 0 []
        | value -> refuse "byte[]" value

    // ─── Structure ───────────────────────────────────────────────────

    /// An array's elements, undecoded.
    let items: JsonDecoder<JsonValue list> =
        function
        | JsonValue.Array items -> Ok items
        | value -> refuse "array" value

    /// An array of exactly `arity` elements.
    let exactly (arity: int) : JsonDecoder<JsonValue list> =
        function
        | JsonValue.Array items when List.length items = arity -> Ok items
        | JsonValue.Array items ->
            refuseWith
                (sprintf "an array of %d element(s)" arity)
                (sprintf "array of %d element(s)" (List.length items))
        | value -> refuse (sprintf "an array of %d element(s)" arity) value

    /// Element `position` of an array, decoded by `decoder`; a refusal
    /// beneath it is annotated `[position]`.
    let index (position: int) (decoder: JsonDecoder<'T>) : JsonDecoder<'T> =
        fun value ->
            match value with
            | JsonValue.Array items ->
                match List.tryItem position items with
                | Some item -> decoder item |> Result.mapError (DecodeError.under (sprintf "[%d]" position))
                | None ->
                    refuseWith
                        (sprintf "an array with an element at %d" position)
                        (sprintf "array of %d element(s)" (List.length items))
            | _ -> refuse "array" value

    /// Member `name` of an object, decoded by `decoder`; a refusal beneath
    /// it is annotated with the name. An ABSENT member is a refusal — the
    /// field is declared, so the wire must carry it. (A field declared
    /// `option` takes `optionalField`.)
    let field (name: string) (decoder: JsonDecoder<'T>) : JsonDecoder<'T> =
        fun value ->
            match value with
            | JsonValue.Object _ ->
                match JsonValue.tryMember name value with
                | Some member' -> decoder member' |> Result.mapError (DecodeError.under name)
                | None -> Error(DecodeError.at [ name ] "a member" "no such member")
            | _ -> refuse "object" value

    /// Member `name` of an object when present and not `null`, decoded
    /// by `decoder`; `None` when absent or `null`. The arm for a field
    /// declared `option`: the writer emits `None` as `null`, and a client
    /// compiled against an older record omits it, which must read the
    /// same way.
    let optionalField (name: string) (decoder: JsonDecoder<'T>) : JsonDecoder<'T option> =
        fun value ->
            match value with
            | JsonValue.Object _ ->
                match JsonValue.tryMember name value with
                | None
                | Some JsonValue.Null -> Ok None
                | Some member' -> decoder member' |> Result.map Some |> Result.mapError (DecodeError.under name)
            | _ -> refuse "object" value

    /// Every element decoded by `element`, in order; the first refusal is
    /// annotated with its index. Structurally recursive on the list.
    let list (element: JsonDecoder<'T>) : JsonDecoder<'T list> =
        fun value ->
            match value with
            | JsonValue.Array items ->
                let rec go (remaining: JsonValue list) (position: int) (acc: 'T list) =
                    match remaining with
                    | [] -> Ok(List.rev acc)
                    | item :: rest ->
                        match element item with
                        | Ok decoded -> go rest (position + 1) (decoded :: acc)
                        | Error error -> Error(DecodeError.under (sprintf "[%d]" position) error)

                go items 0 []
            | _ -> refuse "array" value

    /// `list`, as an array.
    let array (element: JsonDecoder<'T>) : JsonDecoder<'T[]> = list element |> map List.toArray

    /// `list`, as a set (the writer emits a set as an array).
    let asSet<'T when 'T: comparison> (element: JsonDecoder<'T>) : JsonDecoder<Set<'T>> = list element |> map Set.ofList

    /// A key parser for `asMap`: the member NAME to a key. The writer
    /// emits a non-string key as its own JSON text used as the property
    /// name (`FSharpMapNonStringKeyConverter`), so an `int` key arrives
    /// as `"1"` and a `Guid` as its string form.
    type KeyDecoder<'K> = string -> Result<'K, DecodeError>

    /// The key parsers `asMap` takes.
    [<RequireQualifiedAccess>]
    module Key =
        /// The member name itself.
        let string: KeyDecoder<string> = Ok

        /// An `int32` written as the member name.
        let int32: KeyDecoder<int> =
            fun name ->
                match JsonValue.tryInt64 name with
                | Some n when n >= -2147483648L && n <= 2147483647L -> Ok(int n)
                | _ -> Error(DecodeError.create "an Int32 key" (sprintf "key `%s`" name))

        /// An `int64` written as the member name.
        let int64: KeyDecoder<int64> =
            fun name ->
                match JsonValue.tryInt64 name with
                | Some n -> Ok n
                | None -> Error(DecodeError.create "an Int64 key" (sprintf "key `%s`" name))

        /// A GUID written as the member name.
        let guid: KeyDecoder<Guid> =
            fun name ->
                match Guid.TryParse name with
                | true, g -> Ok g
                | _ -> Error(DecodeError.create "a Guid key" (sprintf "key `%s`" name))

        /// A key read by any total function of its text.
        let parse (typeName: string) (f: string -> 'K option) : KeyDecoder<'K> =
            fun name ->
                match f name with
                | Some k -> Ok k
                | None -> Error(DecodeError.create (sprintf "a %s key" typeName) (sprintf "key `%s`" name))

    /// An object's members as `(key, value)` pairs, in wire order.
    let entries (key: KeyDecoder<'K>) (entry: JsonDecoder<'V>) : JsonDecoder<('K * 'V) list> =
        fun value ->
            match value with
            | JsonValue.Object members ->
                let rec go (remaining: (string * JsonValue) list) (acc: ('K * 'V) list) =
                    match remaining with
                    | [] -> Ok(List.rev acc)
                    | (name, member') :: rest ->
                        match key name with
                        | Error error -> Error error
                        | Ok k ->
                            match entry member' with
                            | Ok v -> go rest ((k, v) :: acc)
                            | Error error -> Error(DecodeError.under name error)

                go members []
            | _ -> refuse "object" value

    /// `entries`, as a map (a later duplicate key wins, as `Map.ofList`).
    let asMap<'K, 'V when 'K: comparison> (key: KeyDecoder<'K>) (entry: JsonDecoder<'V>) : JsonDecoder<Map<'K, 'V>> =
        entries key entry |> map Map.ofList

    // ─── Tuples ──────────────────────────────────────────────────────
    //
    // A tuple is an array of its elements (`FSharpTupleConverter`).

    /// A pair from an array of exactly two elements.
    let tuple2 (first: JsonDecoder<'A>) (second: JsonDecoder<'B>) : JsonDecoder<'A * 'B> =
        exactly 2
        |> bind (fun _ -> succeed (fun a b -> a, b) |> apply (index 0 first) |> apply (index 1 second))

    /// A triple from an array of exactly three elements.
    let tuple3 (first: JsonDecoder<'A>) (second: JsonDecoder<'B>) (third: JsonDecoder<'C>) : JsonDecoder<'A * 'B * 'C> =
        exactly 3
        |> bind (fun _ ->
            succeed (fun a b c -> a, b, c)
            |> apply (index 0 first)
            |> apply (index 1 second)
            |> apply (index 2 third))

    /// A quadruple from an array of exactly four elements.
    let tuple4
        (first: JsonDecoder<'A>)
        (second: JsonDecoder<'B>)
        (third: JsonDecoder<'C>)
        (fourth: JsonDecoder<'D>)
        : JsonDecoder<'A * 'B * 'C * 'D> =
        exactly 4
        |> bind (fun _ ->
            succeed (fun a b c d -> a, b, c, d)
            |> apply (index 0 first)
            |> apply (index 1 second)
            |> apply (index 2 third)
            |> apply (index 3 fourth))

    // ─── Unions ──────────────────────────────────────────────────────
    //
    // The writer's shape (`FSharpUnionConverter.Write`): a field-less
    // case is its NAME as a string; a case with one field is
    // `{"Case": field}`; a case with several is `{"Case": [f1, f2, …]}`.

    /// A union case's decoder over the payload the wire carried — `None`
    /// for the string form, `Some` for the one-member object form.
    type JsonUnionCase<'T> = JsonValue option -> Result<'T, DecodeError>

    /// A case carrying no fields. Refuses a payload.
    let case0 (value: 'T) : JsonUnionCase<'T> =
        function
        | None -> Ok value
        | Some payload -> refuseWith "a union case with no fields" (JsonValue.describe payload)

    /// A case carrying ONE field, decoded by `decoder`.
    let payload (decoder: JsonDecoder<'T>) : JsonUnionCase<'T> =
        function
        | Some value -> decoder value
        | None -> refuseWith "a union case carrying a payload" "a union case with no payload"

    /// A case carrying SEVERAL fields — `arity` of them — written as an
    /// array in the payload slot. `decoder` is a pipeline over that
    /// array (`index 0 …`, `index 1 …`).
    let fields (arity: int) (decoder: JsonDecoder<'T>) : JsonUnionCase<'T> =
        let expected = sprintf "a union case carrying %d fields" arity

        function
        | Some(JsonValue.Array inner as value) when List.length inner = arity -> decoder value
        | Some(JsonValue.Array inner) -> refuseWith expected (sprintf "array of %d element(s)" (List.length inner))
        | Some value -> refuse expected value
        | None -> refuseWith expected "a union case with no payload"

    /// Dispatch on the case name. `cases` answers for a name it
    /// recognises and `None` for one it does not — an unrecognised name
    /// is a refusal naming the type and the name, never a fallback.
    let union (typeName: string) (cases: string -> JsonUnionCase<'T> option) : JsonDecoder<'T> =
        fun value ->
            let dispatch (name: string) (carried: JsonValue option) =
                match cases name with
                | Some decodeCase -> decodeCase carried
                | None -> refuseWith typeName (sprintf "case name `%s`, which names no case" name)

            match value with
            | JsonValue.String name -> dispatch name None
            | JsonValue.Object [ name, carried ] -> dispatch name (Some carried)
            | JsonValue.Object members ->
                refuseWith
                    (sprintf "%s (a case name, or a one-member object {\"Case\": payload})" typeName)
                    (sprintf "object of %d member(s)" (List.length members))
            | _ -> refuse (sprintf "%s (a case name, or a one-member object {\"Case\": payload})" typeName) value

    /// A `[<StringEnum>]` union, written as its case name with the first
    /// letter lower-cased (`FSharpStringEnumConverter`). The caller
    /// supplies the wire names, as on the MsgPack side.
    let stringEnum (typeName: string) (cases: (string * 'T) list) : JsonDecoder<'T> =
        fun value ->
            match value with
            | JsonValue.String name ->
                match
                    cases
                    |> List.tryPick (fun (label, case) -> if label = name then Some case else None)
                with
                | Some case -> Ok case
                | None -> refuseWith typeName (sprintf "case name `%s`, which names no case" name)
            | _ -> refuse typeName value

    /// `option` — `null` is `None`, anything else is `Some` of the inner
    /// decode (`FSharpOptionConverter`). Note `Some None` is not
    /// representable on this wire; the writer flattens it.
    let option (inner: JsonDecoder<'T>) : JsonDecoder<'T option> =
        function
        | JsonValue.Null -> Ok None
        | value -> inner value |> Result.map Some

    /// `Result` is an ordinary two-case union: `{"Ok": v}` / `{"Error": e}`.
    let result (ok: JsonDecoder<'T>) (error: JsonDecoder<'E>) : JsonDecoder<Result<'T, 'E>> =
        union "Result" (function
            | "Ok" -> Some(payload (ok |> map Ok))
            | "Error" -> Some(payload (error |> map Error))
            | _ -> None)

    /// Run a decoder. The identity, named so a call site reads as what
    /// it is.
    let run (decoder: JsonDecoder<'T>) (value: JsonValue) : Result<'T, DecodeError> = decoder value