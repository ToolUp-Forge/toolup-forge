// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Json

open System
open ToolUp.Remoting

/// Phase 799 — a JSON decoder erased to `obj`, as the registry holds it.
type RegisteredJsonDecoder = JsonValue -> Result<obj, DecodeError>

#if !FABLE_COMPILER
/// Phase 840 — a place the oracle is KNOWN to lose information the wire
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

/// Phase 840 — what the JSON wire's registration gate compares a
/// candidate decoder against.
///
/// On the MessagePack wire the oracle is the reflection reader, which
/// lives beside the registry in this assembly. On this wire it is the
/// System.Text.Json converter set — the very thing a registered decoder
/// replaces — and that set is composed in `Platform.Server`, which this
/// assembly cannot reference. So the gate takes its oracle as a value:
/// the server supplies the shipped one (`FableConverters.decoderOracle`),
/// and a test can supply its own.
///
/// `Write`, `Read` and `Decode` may throw; the gate turns a throw into a
/// finding in its own vocabulary rather than letting it escape.
type JsonDecoderOracle = {
    /// The shipped writer: a value of the given type to its JSON text,
    /// exactly as a client writes an argument.
    Write: Type -> obj -> string
    /// The text-to-value-model pass the argument seam decodes through —
    /// the candidate decoder reads what this produces.
    Read: string -> Result<JsonValue, DecodeError>
    /// The oracle's own typed decode of the same text. Must bypass the
    /// decoder table: consulting it would compare a candidate with
    /// itself.
    Decode: Type -> string -> Result<obj, DecodeError>
    /// Where `Decode` is known to lose what the writer wrote. Empty means
    /// every difference from the oracle is a divergence.
    Losses: JsonOracleLoss list
}

/// Phase 840 — one draw on which the candidate and the oracle differed
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

/// Phase 840 — the JSON gate's record of an agreeing verification: the
/// shared `DecoderVerification` (its `Divergence` is `None`), beside every
/// draw that differed from the oracle only by a declared loss.
type JsonDecoderVerification = {
    /// Which type, how many draws, under which seed.
    Verification: DecoderVerification
    /// The draws the oracle's declared losses account for, in draw order.
    DeclaredDifferences: JsonDeclaredDifference list
}

/// Phase 885 — the BROWSER's writer, as a .NET mirror.
///
/// The bytes a server decodes from a browser are not written by the
/// server's System.Text.Json converter set. A reflective client proxy
/// writes its arguments with `Fable.SimpleJson`'s `Convert.serialize`
/// (3.24, as Fable 5 transpiles it), which differs from the converter
/// set exactly where a registered decoder can be wrong without the 840
/// gate seeing it: a `decimal` is a QUOTED string, an `int64` a quoted
/// string with no `+`, a `TimeSpan` the host's millisecond double in
/// JavaScript's number spelling (`90000`, not `90000.0`), a `DateTime`
/// the browser's `toISOString` (millisecond precision), a `byte[]` an
/// array of numbers, a record's members separated by `", "`.
///
/// The gate cannot run under Fable, so this is the writer ported: a
/// byte-exact mirror of `Convert.serialize` over the shapes a decoder can
/// be for, pinned against the TRANSPILED writer's own output by a fixture
/// both test packs compile (`BrowserWriterFixture`, Phase 885.A) — the
/// Fable pack holds `Convert.serialize` to each pinned text, the .NET pack
/// holds this mirror to the same text, so the mirror cannot drift from the
/// writer it stands in for without one of the two going red.
///
/// A value the browser cannot hold at the precision .NET does is written
/// as the browser WOULD hold it: a `DateTime` truncated to the millisecond
/// (a JavaScript `Date`), a `TimeOnly` likewise. `Local` and
/// `Unspecified` `DateTime`s are written in THIS machine's time zone,
/// which stands in for the browser's — the one part of the mirror no
/// literal can pin, so the fixture pins `Utc` and `Unspecified` only.
[<RequireQualifiedAccess>]
module BrowserJsonWriter =

    open System.Globalization
    open System.Text
    open Microsoft.FSharp.Reflection

    let private invariant = CultureInfo.InvariantCulture

    let private isGenericOf (definition: Type) (t: Type) =
        t.IsGenericType && t.GetGenericTypeDefinition() = definition

    /// `quote.js` — the escape set `Convert.serialize` applies to a
    /// string: the quote, the backslash, the C0 and C1 controls, and the
    /// invisible/format code units its regex names; the five with a short
    /// form get it, every other one is `\u` + four LOWERCASE hex digits.
    /// Code units, not code points: a surrogate pair passes through.
    let private quote (text: string) : string =
        let escapable (c: char) =
            let n = int c

            c = '\\'
            || c = '"'
            || n <= 0x1f
            || (n >= 0x7f && n <= 0x9f)
            || n = 0xad
            || (n >= 0x600 && n <= 0x604)
            || n = 0x70f
            || n = 0x17b4
            || n = 0x17b5
            || (n >= 0x200c && n <= 0x200f)
            || (n >= 0x2028 && n <= 0x202f)
            || (n >= 0x2060 && n <= 0x206f)
            || n = 0xfeff
            || n >= 0xfff0

        let sb = StringBuilder(text.Length + 2)
        sb.Append '"' |> ignore

        for c in text do
            if escapable c then
                match c with
                | '\b' -> sb.Append "\\b" |> ignore
                | '\t' -> sb.Append "\\t" |> ignore
                | '\n' -> sb.Append "\\n" |> ignore
                | '\012' -> sb.Append "\\f" |> ignore
                | '\r' -> sb.Append "\\r" |> ignore
                | '"' -> sb.Append "\\\"" |> ignore
                | '\\' -> sb.Append "\\\\" |> ignore
                | c -> sb.Append("\\u").Append((int c).ToString("x4", invariant)) |> ignore
            else
                sb.Append c |> ignore

        sb.Append '"' |> ignore
        sb.ToString()

    let private betweenQuotes (text: string) = "\"" + text + "\""

    /// JavaScript's `Number.prototype.toString()` (ECMA-262
    /// Number::toString, radix 10): the shortest digits that round-trip
    /// — the same digits .NET's `"R"` finds — laid out by JavaScript's
    /// rules: plain notation from 1e-6 up to 1e21, exponent form (`1e+21`,
    /// `1.5e-7`) outside it, no `.0` on an integral value, `-0` as `0`.
    let private jsNumber (value: float) : string =
        if Double.IsNaN value then
            "NaN"
        elif Double.IsPositiveInfinity value then
            "Infinity"
        elif Double.IsNegativeInfinity value then
            "-Infinity"
        elif value = 0.0 then
            "0"
        else
            let sign = if value < 0.0 then "-" else ""
            let r = (abs value).ToString("R", invariant)

            let mantissa, exponent =
                match r.IndexOfAny [| 'E'; 'e' |] with
                | -1 -> r, 0
                | i -> r.Substring(0, i), int (r.Substring(i + 1))

            let whole, fraction =
                match mantissa.IndexOf '.' with
                | -1 -> mantissa, ""
                | i -> mantissa.Substring(0, i), mantissa.Substring(i + 1)

            // value = 0.d1d2…dk × 10^n, with d1 ≠ 0 and dk ≠ 0.
            let allDigits = whole + fraction
            let leading = allDigits.Length - allDigits.TrimStart('0').Length
            let digits = allDigits.TrimStart('0').TrimEnd('0')
            let n = whole.Length + exponent - leading
            let k = digits.Length

            let body =
                if k <= n && n <= 21 then
                    digits + String('0', n - k)
                elif 0 < n && n <= 21 then
                    digits.Substring(0, n) + "." + digits.Substring n
                elif -6 < n && n <= 0 then
                    "0." + String('0', -n) + digits
                else
                    let e = n - 1
                    let expText = (if e < 0 then "-" else "+") + string (abs e)

                    if k = 1 then
                        digits + "e" + expText
                    else
                        digits.Substring(0, 1) + "." + digits.Substring 1 + "e" + expText

            sign + body

    /// A `decimal` as Fable's decimal prints it: its digits and its scale,
    /// exactly as .NET's invariant `ToString` does (`1234.50`, `0.00`) —
    /// measured on the transpiled writer (`BrowserWriterFixture`), not
    /// assumed: Fable's decimal keeps the scale big.js alone would drop.
    let private decimalText (value: decimal) : string = value.ToString(invariant)

    let private pad (width: int) (n: int) =
        n.ToString(String('0', width), invariant)

    let private fields (d: DateTime) =
        sprintf
            "%s-%s-%sT%s:%s:%s.%s"
            (pad 4 d.Year)
            (pad 2 d.Month)
            (pad 2 d.Day)
            (pad 2 d.Hour)
            (pad 2 d.Minute)
            (pad 2 d.Second)
            (pad 3 d.Millisecond)

    let private offsetText (offset: TimeSpan) =
        let ms = offset.TotalMilliseconds
        let magnitude = abs ms
        let hours = int (magnitude / 3600000.0)
        let minutes = int ((magnitude % 3600000.0) / 60000.0)
        (if ms < 0.0 then "-" else "+") + pad 2 hours + ":" + pad 2 minutes

    let private toMillisecond (d: DateTime) =
        DateTime(d.Ticks - d.Ticks % TimeSpan.TicksPerMillisecond, d.Kind)

    /// `DateTime.ToString("O")` as Fable's `Date.js` prints it: a `Utc`
    /// value through `toISOString` (`…T10:30:00.123Z`), a `Local` one as
    /// its local fields plus the zone's offset, an `Unspecified` one as its
    /// fields alone — each at the millisecond a JavaScript `Date` holds.
    let private dateTimeText (value: DateTime) : string =
        let d = toMillisecond value

        match d.Kind with
        | DateTimeKind.Utc -> fields d + "Z"
        | DateTimeKind.Local -> fields d + offsetText (TimeZoneInfo.Local.GetUtcOffset d)
        | _ -> fields d

    /// `DateTimeOffset.ToString("O")` as Fable prints it: the clock time
    /// at the offset, to the millisecond, then the offset.
    let private dateTimeOffsetText (value: DateTimeOffset) : string =
        fields (toMillisecond value.DateTime) + offsetText value.Offset

    /// Fable.SimpleJson's `isPrimitive`: the key types whose `Map` is
    /// written as an OBJECT (every other key type makes it an array of
    /// `[key, value]` pairs).
    let private isPrimitive (t: Type) =
        t = typeof<unit>
        || t = typeof<string>
        || t = typeof<uint16>
        || t = typeof<uint32>
        || t = typeof<uint64>
        || t = typeof<int>
        || t = typeof<bool>
        || t = typeof<float32>
        || t = typeof<float>
        || t = typeof<decimal>
        || t = typeof<int16>
        || t = typeof<int64>
        || t = typeof<byte>
        || t = typeof<DateTime>
        || t = typeof<DateTimeOffset>
        || t = typeof<DateOnly>
        || t = typeof<TimeOnly>
        || t = typeof<Numerics.BigInteger>
        || t = typeof<Guid>
        || isGenericOf typedefof<option<_>> t

    let private flags =
        Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic

    /// A union whose cases all carry no fields (`enumUnion`).
    let private isEnumUnion (t: Type) =
        FSharpType.IsUnion(t, flags)
        && not (isGenericOf typedefof<option<_>> t)
        && not (isGenericOf typedefof<list<_>> t)
        && FSharpType.GetUnionCases(t, flags)
           |> Array.forall (fun case -> Array.isEmpty (case.GetFields()))

    let private items (value: obj) : obj list = [ for item in (value :?> Collections.IEnumerable) -> item ]

    /// `Convert.serialize value typeInfo` for the `TypeInfo` Fable builds
    /// from `target`. Throws on a shape it does not mirror (the gate
    /// turns a throw into `DecoderUndrawable`, never a pass).
    let rec serialize (target: Type) (value: obj) : string =
        let t = target

        if t = typeof<string> then
            if isNull value then "null" else quote (unbox<string> value)
        elif t = typeof<unit> then
            "null"
        elif t = typeof<float> || t = typeof<float32> then
            let v =
                if t = typeof<float> then
                    unbox<float> value
                else
                    float (unbox<float32> value)

            if Double.IsNaN v then quote "NaN" else jsNumber v
        elif t = typeof<char> then
            quote (string (unbox<char> value))
        elif t = typeof<byte> then
            string (unbox<byte> value)
        elif t = typeof<sbyte> then
            string (unbox<sbyte> value)
        elif t = typeof<uint16> then
            string (unbox<uint16> value)
        elif t = typeof<uint32> then
            string (unbox<uint32> value)
        elif t = typeof<int16> then
            string (unbox<int16> value)
        elif t = typeof<int> then
            string (unbox<int> value)
        elif t.IsEnum then
            Convert.ToInt64(value, invariant).ToString(invariant)
        elif t = typeof<TimeSpan> then
            // Fable's TimeSpan IS a millisecond double.
            jsNumber (float (unbox<TimeSpan> value).Ticks / 10000.0)
        elif t = typeof<int64> then
            betweenQuotes ((unbox<int64> value).ToString(invariant))
        elif t = typeof<uint64> then
            betweenQuotes ((unbox<uint64> value).ToString(invariant))
        elif t = typeof<Numerics.BigInteger> then
            betweenQuotes ((unbox<Numerics.BigInteger> value).ToString(invariant))
        elif t = typeof<decimal> then
            betweenQuotes (decimalText (unbox<decimal> value))
        elif t = typeof<bool> then
            if unbox<bool> value then "true" else "false"
        elif t = typeof<Guid> then
            betweenQuotes ((unbox<Guid> value).ToString())
        elif t = typeof<Uri> then
            betweenQuotes ((unbox<Uri> value).ToString())
        elif t = typeof<DateTime> then
            betweenQuotes (dateTimeText (unbox<DateTime> value))
        elif t = typeof<DateTimeOffset> then
            betweenQuotes (dateTimeOffsetText (unbox<DateTimeOffset> value))
        elif t = typeof<DateOnly> then
            string (unbox<DateOnly> value).DayNumber
        elif t = typeof<TimeOnly> then
            let ticks = (unbox<TimeOnly> value).Ticks
            betweenQuotes (string (ticks - ticks % TimeSpan.TicksPerMillisecond))
        elif isGenericOf typedefof<option<_>> t then
            if isNull value then
                "null"
            else
                let _, fields = FSharpValue.GetUnionFields(value, t, flags)
                serialize (t.GetGenericArguments().[0]) fields.[0]
        elif t.IsArray then
            let element = t.GetElementType()
            "[" + (items value |> List.map (serialize element) |> String.concat ", ") + "]"
        elif
            isGenericOf typedefof<list<_>> t
            || isGenericOf typedefof<Set<_>> t
            || isGenericOf typedefof<ResizeArray<_>> t
            || isGenericOf typedefof<Collections.Generic.HashSet<_>> t
            || isGenericOf typedefof<seq<_>> t
        then
            let element = t.GetGenericArguments().[0]
            "[" + (items value |> List.map (serialize element) |> String.concat ", ") + "]"
        elif isGenericOf typedefof<Map<_, _>> t then
            let args = t.GetGenericArguments()
            let keyType, valueType = args.[0], args.[1]
            let asObject = isPrimitive keyType || isEnumUnion keyType

            let pairType =
                typedefof<Collections.Generic.KeyValuePair<_, _>>.MakeGenericType args

            let keyProp = pairType.GetProperty "Key"
            let valueProp = pairType.GetProperty "Value"

            let entries =
                items value
                |> List.map (fun pair ->
                    let key = serialize keyType (keyProp.GetValue pair)
                    let entry = serialize valueType (valueProp.GetValue pair)

                    if asObject then
                        let key =
                            if key.StartsWith "\"" && key.EndsWith "\"" then
                                key
                            else
                                quote key

                        key + ": " + entry
                    else
                        "[" + key + ", " + entry + "]")
                |> String.concat ", "

            if asObject then
                "{" + entries + "}"
            else
                "[" + entries + "]"
        elif FSharpType.IsTuple t then
            let elements = FSharpType.GetTupleElements t
            let values = FSharpValue.GetTupleFields value

            "[" + (Array.map2 serialize elements values |> String.concat ", ") + "]"
        elif FSharpType.IsRecord(t, flags) then
            let members =
                FSharpType.GetRecordFields(t, flags)
                |> Array.map (fun field ->
                    sprintf "\"%s\": %s" field.Name (serialize field.PropertyType (field.GetValue value)))

            "{" + String.concat ", " members + "}"
        elif FSharpType.IsUnion(t, flags) then
            let case, values = FSharpValue.GetUnionFields(value, t, flags)
            let caseTypes = case.GetFields() |> Array.map _.PropertyType

            if isEnumUnion t || Array.isEmpty caseTypes then
                betweenQuotes case.Name
            elif caseTypes.Length = 1 then
                "{" + betweenQuotes case.Name + ": " + serialize caseTypes.[0] values.[0] + "}"
            else
                "{"
                + betweenQuotes case.Name
                + ": ["
                + (Array.map2 serialize caseTypes values |> String.concat ", ")
                + "] }"
        else
            failwithf "the browser writer mirror does not model %s" t.FullName
#endif

/// Phase 799 — the JSON twin of `RemotingDecoders`: the process-wide
/// table of algebra decoders the server's ARGUMENT seam consults before
/// falling back to System.Text.Json's typed deserialise.
///
/// Same shape, same rules, same key (`RemotingDecoders.keyFor`, so a
/// type is identified identically on both wires): registration is
/// explicit and idempotent; a miss is the STJ path, never an error; the
/// table is written at composition and read per request. One table per
/// wire rather than one table with a wire axis, because the two decoders
/// for a type are different functions over different value models and
/// nothing ever asks for "the decoder for `'T`" without knowing which
/// wire it is standing on.
///
/// Phase 839 — the key widens from a bare wire-type key to
/// `(API record name option) * (wire-type key)`. `register` keeps
/// registering UNSCOPED (record = `None`), exactly as before this
/// phase — so nothing registered before Phase 839 changes meaning.
/// `registerFor` registers a decoder scoped to one named API record;
/// the seam (`tryGet`) tries the record-scoped key first and falls
/// back to the unscoped one, so a caller with no record to name (a
/// test, a non-dispatch path) passes `None` and gets exactly the
/// bare-type lookup it always had.
[<RequireQualifiedAccess>]
module JsonDecoders =

    let mutable private table: Map<string option * string, RegisteredJsonDecoder> =
        Map.empty

    /// Register an already-erased decoder under a wire-type key, scoped
    /// to `recordName` (`None` = unscoped, consulted for every record).
    /// The non-inline half of `register` / `registerFor`, and the only
    /// writer of the table.
    let registerByKey (recordName: string option) (key: string) (decoder: RegisteredJsonDecoder) : unit =
        table <- Map.add (recordName, key) decoder table

    /// Register the JSON algebra decoder for `'T`, UNSCOPED — consulted
    /// for every API record's arguments of this type, exactly as before
    /// Phase 839. Idempotent. `inline` for the reason
    /// `RemotingDecoders.register` is: Fable erases generics, so
    /// `typeof<'T>` must resolve at the call site.
    let inline register<'T> (decoder: JsonDecoder<'T>) : unit =
        registerByKey None (RemotingDecoders.keyFor typeof<'T>) (fun value -> decoder value |> Result.map box)

    /// Phase 839 — register the JSON algebra decoder for `'T`, scoped to
    /// arguments of the named API record ONLY. A registration for
    /// `(Some "IPresenceApi", key)` is never consulted for another
    /// record's arguments of the same type — a strict decoder registered
    /// for one record does not leak into another's arguments of the same
    /// wire type, which is the isolation this phase exists to establish.
    /// Idempotent, `inline` for the same reason `register` is.
    let inline registerFor<'T> (recordName: string) (decoder: JsonDecoder<'T>) : unit =
        registerByKey (Some recordName) (RemotingDecoders.keyFor typeof<'T>) (fun value ->
            decoder value |> Result.map box)

    /// The JSON decoder for `wireType`, scoped to `recordName` — the
    /// record-scoped registration first, then the unscoped (bare-type)
    /// one, or `None` for the STJ path. `recordName = None` looks up
    /// only the unscoped registration: exactly the pre-839 lookup.
    let tryGet (recordName: string option) (wireType: Type) : RegisteredJsonDecoder option =
        let key = RemotingDecoders.keyFor wireType

        match recordName with
        | Some _ ->
            match Map.tryFind (recordName, key) table with
            | Some decoder -> Some decoder
            | None -> Map.tryFind (None, key) table
        | None -> Map.tryFind (None, key) table

    /// Whether `wireType` decodes through the JSON algebra for
    /// `recordName` (record-scoped or the unscoped fallback) in this
    /// process.
    let isRegistered (recordName: string option) (wireType: Type) : bool = (tryGet recordName wireType).IsSome

    /// Every registered `(record, type)` key, ordered. `fst` is `None`
    /// for an unscoped registration.
    let registered () : (string option * string) list = table |> Map.toList |> List.map fst

    /// How many `(record, type)` pairs decode through the JSON algebra
    /// in this process.
    let count () : int = Map.count table

    /// Test-only: empty the table. See `RemotingDecoders.resetForTests`
    /// for why this is public.
    let resetForTests () : unit = table <- Map.empty

#if !FABLE_COMPILER
    // ─── Phase 840 — the registration gate ───────────────────────────
    //
    // Phase 801's rule, on this wire: a registered decoder WINS over the
    // converter set, so a wrong one is worse than none — it accepts the
    // text and yields a well-typed value that is not what the client
    // sent. Nothing is registered through `registerVerifiedWith` until it
    // has been run beside the oracle over seeded draws of its own type.
    //
    // **Where the two decoders may differ, and how each difference is
    // said rather than papered over.** Two kinds, both declared:
    //
    //   1. STRICTNESS — the converter set is more lenient than the
    //      algebra: a quoted number at a width the writer emits bare, an
    //      absent member read as null, `null` for a record. Every draw is
    //      written by the SHIPPED writer, which never produces those texts
    //      (it quotes only the widths whose algebra arms read the quoted
    //      form, writes every member with `None` as `null`, and never
    //      writes `null` for a record), so strictness is unreachable from
    //      a draw and needs no exemption here. It is declared where it is
    //      exercised: the refuse-path table in the test pack
    //      (`JsonDecoderAlgebraTests.algebraOutcomes`).
    //   2. ORACLE LOSS — the converter set reads a value the writer wrote
    //      exactly into something else (its `TimeSpan` read goes through
    //      a double and can land a tick short, Phase 784; the algebra
    //      reads the millisecond text exactly, 785.F). Reachable from a
    //      draw, so the oracle DECLARES it (`JsonDecoderOracle.Losses`),
    //      and a draw on which the candidate recovers the written value
    //      exactly, the oracle does not, and the type reaches a declared
    //      loss is recorded as a `JsonDeclaredDifference` — said in the
    //      verification record, not refused and not silently passed. A
    //      candidate that differs from the written value is refused
    //      whatever the oracle declares.
    //
    // .NET only, for the MessagePack gate's reason: drawing a value of an
    // arbitrary type needs `FSharp.Reflection`, and the oracle is .NET's.
    // The Fable client registers with plain `register` / `registerFor`,
    // what a build gate already verified.

    /// Whether `target`'s shape reaches `loss` anywhere: itself, an array
    /// element, a generic argument (option, list, map, set, …), a record
    /// field, a union case field or a tuple element. Cycle-safe.
    let private reaches (loss: Type) (target: Type) : bool =
        let seen = System.Collections.Generic.HashSet<Type>()

        let rec go (t: Type) =
            if t = loss then
                true
            elif isNull t || not (seen.Add t) then
                false
            else
                let flags =
                    System.Reflection.BindingFlags.Public
                    ||| System.Reflection.BindingFlags.NonPublic

                let children = [
                    if t.IsArray then
                        yield t.GetElementType()
                    if t.IsGenericType then
                        yield! t.GetGenericArguments()
                    if FSharp.Reflection.FSharpType.IsRecord(t, flags) then
                        for f in FSharp.Reflection.FSharpType.GetRecordFields(t, flags) do
                            yield f.PropertyType
                    if FSharp.Reflection.FSharpType.IsUnion(t, flags) then
                        for case in FSharp.Reflection.FSharpType.GetUnionCases(t, flags) do
                            for f in case.GetFields() do
                                yield f.PropertyType
                ]

                List.exists go children

        go target

    /// Phase 840 — the differential gate for the JSON wire. Draw `draws`
    /// values of `target` (`DecoderShapes.draw`, deterministically from
    /// `seed`), write each with `oracle.Write`, then decode the text
    /// through the candidate (`oracle.Read`, then `decoder`) AND through
    /// `oracle.Decode`, comparing outcome class, value and runtime type
    /// (`RemotingDecoders.renderDecode`, the MessagePack gate's rendering).
    ///
    /// The first disagreement not accounted for by a declared oracle loss
    /// (see the block comment above) is `DecoderDiverges` naming the type
    /// and the draw; on this wire the divergence's `Reflection` member
    /// carries the ORACLE's decode (the record is shared with the
    /// MessagePack gate so one refusal vocabulary serves both wires). A
    /// type that cannot be drawn, or a draw the writer refuses, is
    /// `DecoderUndrawable` — a gate that could not run is not a gate that
    /// passed.
    ///
    /// The erased form, over a `Type` and an already-erased decoder — what
    /// a table-shaped caller (a test pack verifying a list of
    /// registrations) can hold. `verifyWith` is the typed form.
    let verifyByTypeWith
        (oracle: JsonDecoderOracle)
        (draws: int)
        (seed: int)
        (target: Type)
        (decoder: RegisteredJsonDecoder)
        : Result<JsonDecoderVerification, DecoderRefusal> =
        let wireType = RemotingDecoders.keyFor target
        let rng = Random seed
        let reachable = oracle.Losses |> List.filter (fun loss -> reaches loss.Type target)

        let record divergence = {
            WireType = wireType
            Draws = draws
            Seed = seed
            Divergence = divergence
        }

        let rec go (i: int) (declared: JsonDeclaredDifference list) =
            if i >= draws then
                Ok {
                    Verification = record None
                    DeclaredDifferences = List.rev declared
                }
            else
                match DecoderShapes.draw rng DecoderShapes.DefaultDepth target with
                | Error reason -> Error(DecoderUndrawable(wireType, reason))
                | Ok drawn ->
                    // The writer may refuse a shape by throwing: that
                    // refusal belongs in the gate's vocabulary, not on the
                    // stack — 801's lazy-writer lesson, applied per draw.
                    let written =
                        try
                            Ok(oracle.Write target drawn)
                        with ex ->
                            Error(sprintf "the shipped writer refused a draw: %s: %s" (ex.GetType().Name) ex.Message)

                    match written with
                    | Error reason -> Error(DecoderUndrawable(wireType, reason))
                    | Ok text ->
                        let candidate =
                            RemotingDecoders.renderDecode (fun () -> oracle.Read text |> Result.bind decoder)

                        let reference = RemotingDecoders.renderDecode (fun () -> oracle.Decode target text)

                        if candidate = reference then
                            go (i + 1) declared
                        else
                            let original = RemotingDecoders.renderDecode (fun () -> Ok drawn)

                            match reachable with
                            | loss :: _ when candidate = original && reference <> original ->
                                go
                                    (i + 1)
                                    ({
                                        Draw = i
                                        Loss = loss
                                        Candidate = candidate
                                        Oracle = reference
                                     }
                                     :: declared)
                            | _ ->
                                Error(
                                    DecoderDiverges(
                                        record (
                                            Some {
                                                Draw = i
                                                Candidate = candidate
                                                Reflection = reference
                                            }
                                        )
                                    )
                                )

        go 0 []

    /// Phase 840 — `verifyByTypeWith` for `'T`: the gate over a typed
    /// decoder, draws taken of `'T` itself.
    let verifyWith<'T>
        (oracle: JsonDecoderOracle)
        (draws: int)
        (seed: int)
        (decoder: JsonDecoder<'T>)
        : Result<JsonDecoderVerification, DecoderRefusal> =
        verifyByTypeWith oracle draws seed typeof<'T> (fun value -> decoder value |> Result.map box)

    // ─── Phase 885 — the browser's writer as a second reference ──────
    //
    // 840's oracle writes every draw with the SERVER's writer, but the
    // text a server decodes from a browser is the browser's. So the gate
    // runs twice: once over the oracle as given, once over its browser
    // twin — the same reader and the same reference decode, the draw
    // written by `BrowserJsonWriter` instead. A candidate that disagrees
    // with the reference on EITHER writer's text is refused; a difference
    // either run attributes to a declared loss is said, exactly as 840
    // says it, and the browser run's losses name their writer.

    /// The prefix a declared loss carries on the browser twin, so a
    /// `JsonDeclaredDifference` says which writer's text it was drawn on.
    [<Literal>]
    let BrowserLossPrefix = "on the browser's writer (Fable.SimpleJson): "

    /// Phase 885 — `server`'s browser twin: the draw written as a
    /// reflective Fable client writes it (`BrowserJsonWriter.serialize`),
    /// then read and referenced exactly as `server` reads and references
    /// it. `server`'s declared losses are properties of its reference
    /// decode, so the twin declares the same ones, each relabelled with
    /// `BrowserLossPrefix`.
    let browserOracle (server: JsonDecoderOracle) : JsonDecoderOracle = {
        Write = BrowserJsonWriter.serialize
        Read = server.Read
        Decode = server.Decode
        Losses =
            server.Losses
            |> List.map (fun loss -> {
                loss with
                    Reason = BrowserLossPrefix + loss.Reason
            })
    }

    /// Phase 885 — `verifyByTypeWith` over BOTH writers: `oracle` as
    /// given, then `browserOracle oracle`. The first refusal wins (the
    /// server writer's run is taken first, so a decoder wrong on both is
    /// refused exactly where 840 refused it); on agreement the record is
    /// the server run's, with the browser run's declared differences
    /// appended after its own (each naming its writer through its loss's
    /// `Reason`).
    let verifyBothByTypeWith
        (oracle: JsonDecoderOracle)
        (draws: int)
        (seed: int)
        (target: Type)
        (decoder: RegisteredJsonDecoder)
        : Result<JsonDecoderVerification, DecoderRefusal> =
        match verifyByTypeWith oracle draws seed target decoder with
        | Error refusal -> Error refusal
        | Ok server ->
            match verifyByTypeWith (browserOracle oracle) draws seed target decoder with
            | Error refusal -> Error refusal
            | Ok browser ->
                Ok {
                    server with
                        DeclaredDifferences = server.DeclaredDifferences @ browser.DeclaredDifferences
                }

    /// Phase 885 — `verifyBothByTypeWith` for `'T`: what the registration
    /// gate and the generated `verifyAll` run.
    let verifyBothWith<'T>
        (oracle: JsonDecoderOracle)
        (draws: int)
        (seed: int)
        (decoder: JsonDecoder<'T>)
        : Result<JsonDecoderVerification, DecoderRefusal> =
        verifyBothByTypeWith oracle draws seed typeof<'T> (fun value -> decoder value |> Result.map box)

    /// Phase 840 — a refusal rendered for a boot log or a test failure,
    /// naming the ORACLE this wire compares against (the shared
    /// `DecoderRefusal.describe` names the MessagePack wire's reflection
    /// reader). Names the type and, for a divergence, the draw and both
    /// decodes.
    let describeRefusal (refusal: DecoderRefusal) : string =
        match refusal with
        | DecoderDiverges v ->
            match v.Divergence with
            | Some d ->
                sprintf
                    "JSON decoder for %s refused: draw %d of %d (seed %d) decodes differently from the STJ converter set — candidate: %s; converter set: %s"
                    v.WireType
                    d.Draw
                    v.Draws
                    v.Seed
                    d.Candidate
                    d.Reflection
            | None -> sprintf "JSON decoder for %s refused (no divergence recorded)" v.WireType
        | DecoderUndrawable(wireType, reason) ->
            sprintf "JSON decoder for %s refused: no draw of the type could be produced — %s" wireType reason

    /// Phase 840 — register the JSON decoder for `'T`, UNSCOPED, ONLY if
    /// it agrees with `oracle` over `RemotingDecoders.DefaultDraws` draws
    /// (seed `RemotingDecoders.DefaultSeed` — the MessagePack gate's
    /// pair). A refusal leaves the table untouched, so the type keeps the
    /// converter-set path it had. Plain `register` stays unverified, as it
    /// does on the MessagePack wire: the Fable client has no oracle and
    /// registers what a build gate verified.
    ///
    /// Phase 885 — agreement is over BOTH writers (`verifyBothWith`): the
    /// server's, and the browser's that `oracle`'s twin writes with.
    let registerVerifiedWith<'T>
        (oracle: JsonDecoderOracle)
        (decoder: JsonDecoder<'T>)
        : Result<JsonDecoderVerification, DecoderRefusal> =
        verifyBothWith<'T> oracle RemotingDecoders.DefaultDraws RemotingDecoders.DefaultSeed decoder
        |> Result.map (fun verification ->
            registerByKey None (RemotingDecoders.keyFor typeof<'T>) (fun value -> decoder value |> Result.map box)
            verification)

    /// Phase 840 — the record-scoped form of `registerVerifiedWith`: the
    /// verified twin of `registerFor`, registering for `recordName`'s
    /// arguments only, and only on agreement (over both writers, Phase 885).
    let registerVerifiedForWith<'T>
        (oracle: JsonDecoderOracle)
        (recordName: string)
        (decoder: JsonDecoder<'T>)
        : Result<JsonDecoderVerification, DecoderRefusal> =
        verifyBothWith<'T> oracle RemotingDecoders.DefaultDraws RemotingDecoders.DefaultSeed decoder
        |> Result.map (fun verification ->
            registerByKey (Some recordName) (RemotingDecoders.keyFor typeof<'T>) (fun value ->
                decoder value |> Result.map box)

            verification)
#endif