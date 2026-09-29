// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Json

open System
open System.Globalization
open System.Text

// ─── Phase 853 — the JSON algebra's WRITING half ─────────────────────
//
// `JsonDecode` reads the wire the System.Text.Json converter set writes;
// this module writes it. A generated client proxy (ToolUp.Remoting.Generator
// `client-proxies`) builds a request's argument array from typed arguments
// through these combinators and nothing else: no `TypeInfo`, no boxed
// argument array, no reflective walk — the per-call `Convert.serialize` the
// reflective proxy runs is exactly what they replace.
//
// Each encoder reproduces the WRITER's convention for its shape (the
// converter named beside it), because that is the shape the server's
// argument seam reads on both of its paths: the registered algebra decoder
// (`PlatformJsonDecoders`, Phase 841) and the converter set's own reader.
// The two halves are one contract read off one set of metadata — a record
// by its member NAMES, a union by its CASE NAME, the signed `int64` string.
//
// Like the decoders, the combinators are PURE and REFLECTION-FREE: an
// encoder is a function from a value to the closed `JsonValue` model, and
// `toText` is one pass over that model. They are total over every value
// the wire can carry; a value the wire cannot carry is named where it is
// written (a non-finite float, which the writer itself cannot express as a
// JSON number — see `float`).

/// Phase 853 — a JSON encoder for `'T`: pure, reflection-free, the
/// writer's convention for the shape.
type JsonEncoder<'T> = 'T -> JsonValue

/// Phase 853 — the combinators that build a `JsonValue` from a typed
/// value, and the text writer over the model.
[<RequireQualifiedAccess>]
module JsonEncode =

    // ─── The text writer ─────────────────────────────────────────────

    let private hex = "0123456789abcdef"

    /// Escape exactly as `StringConverter.Write` does: the quote, the
    /// backslash and the C0 controls (short forms where JSON has one,
    /// `\u00xx` otherwise); every other character is written as itself.
    ///
    /// Runs of characters that need no escape are appended whole, not a
    /// character at a time: on the Fable host a `StringBuilder` append is
    /// an array push, so per-character appends dominate a request's cost.
    let private appendString (sb: StringBuilder) (text: string) =
        sb.Append '"' |> ignore
        let mutable run = 0

        for i in 0 .. text.Length - 1 do
            let c = text.[i]

            if c = '"' || c = '\\' || int c <= 0x1F then
                if i > run then
                    sb.Append(text.Substring(run, i - run)) |> ignore

                match c with
                | '"' -> sb.Append "\\\"" |> ignore
                | '\\' -> sb.Append "\\\\" |> ignore
                | '\b' -> sb.Append "\\b" |> ignore
                | '\012' -> sb.Append "\\f" |> ignore
                | '\n' -> sb.Append "\\n" |> ignore
                | '\r' -> sb.Append "\\r" |> ignore
                | '\t' -> sb.Append "\\t" |> ignore
                | c ->
                    let code = int c

                    sb.Append("\\u00").Append(hex.[(code >>> 4) &&& 0xF]).Append(hex.[code &&& 0xF])
                    |> ignore

                run <- i + 1

        if run = 0 then
            sb.Append text |> ignore
        elif run < text.Length then
            sb.Append(text.Substring run) |> ignore

        sb.Append '"' |> ignore

    let rec private append (sb: StringBuilder) (value: JsonValue) =
        match value with
        | JsonValue.Null -> sb.Append "null" |> ignore
        | JsonValue.Bool true -> sb.Append "true" |> ignore
        | JsonValue.Bool false -> sb.Append "false" |> ignore
        | JsonValue.Number lexical -> sb.Append lexical |> ignore
        | JsonValue.String text -> appendString sb text
        | JsonValue.Array items ->
            sb.Append '[' |> ignore
            let mutable first = true

            for item in items do
                if not first then
                    sb.Append ',' |> ignore

                first <- false
                append sb item

            sb.Append ']' |> ignore
        | JsonValue.Object members ->
            sb.Append '{' |> ignore
            let mutable first = true

            for name, item in members do
                if not first then
                    sb.Append ',' |> ignore

                first <- false
                appendString sb name
                sb.Append ':' |> ignore
                append sb item

            sb.Append '}' |> ignore

    /// The compact JSON text of a value — no whitespace, members in model
    /// order, numbers exactly as their lexical form, strings escaped as
    /// the writer escapes them.
    let toText (value: JsonValue) : string =
        let sb = StringBuilder()
        append sb value
        sb.ToString()

    /// A request body: the argument ARRAY the server's argument seam
    /// parses (`[a1, a2, …]`, one element per argument in declaration
    /// order), as text.
    let arguments (values: JsonValue list) : string =
        toText (JsonValue.Array(List.toArray values))

    // ─── Scalars ─────────────────────────────────────────────────────

    /// A JSON boolean.
    let bool: JsonEncoder<bool> = JsonValue.Bool

    /// `unit` is `null`.
    let unit: JsonEncoder<unit> = fun () -> JsonValue.Null

    /// A JSON string. A `null` string (a .NET value no F# declaration
    /// produces) is `null`, as the writer emits it.
    let string: JsonEncoder<string> =
        fun text ->
            if isNull text then
                JsonValue.Null
            else
                JsonValue.String text

    /// A one-character string.
    let char: JsonEncoder<char> = fun c -> JsonValue.String(String(c, 1))

    /// `int32` — a number token.
    let int32: JsonEncoder<int> =
        fun n -> JsonValue.Number(n.ToString(CultureInfo.InvariantCulture))

    /// `int16` — a number token.
    let int16: JsonEncoder<int16> =
        fun n -> JsonValue.Number(n.ToString(CultureInfo.InvariantCulture))

    /// `sbyte` — a number token.
    let sbyte: JsonEncoder<sbyte> =
        fun n -> JsonValue.Number(n.ToString(CultureInfo.InvariantCulture))

    /// `byte` — a number token.
    let byte: JsonEncoder<byte> =
        fun n -> JsonValue.Number(n.ToString(CultureInfo.InvariantCulture))

    /// `uint16` — a number token.
    let uint16: JsonEncoder<uint16> =
        fun n -> JsonValue.Number(n.ToString(CultureInfo.InvariantCulture))

    /// `uint32` — a number token.
    let uint32: JsonEncoder<uint32> =
        fun n -> JsonValue.Number(n.ToString(CultureInfo.InvariantCulture))

    /// `int64` — the signed STRING the writer emits (`Int64Converter.Write`,
    /// `"+42"` / `"-42"`), so a JavaScript reader cannot mistake it for a
    /// double and round it.
    let int64: JsonEncoder<int64> =
        fun n ->
            let digits = n.ToString(CultureInfo.InvariantCulture)
            JsonValue.String(if n >= 0L then "+" + digits else digits)

    /// `uint64` — the digit STRING the writer emits (`UInt64Converter.Write`).
    let uint64: JsonEncoder<uint64> =
        fun n -> JsonValue.String(n.ToString(CultureInfo.InvariantCulture))

    /// The writer's double form (`DoubleFormat.newtonsoftStyle`): the
    /// shortest round-trip text, with `.0` appended to an integral one.
    let private doubleText (v: float) : string =
#if FABLE_COMPILER
        let s = v.ToString()
#else
        let s = v.ToString("R", CultureInfo.InvariantCulture)
#endif
        if s.Contains "." || s.Contains "e" || s.Contains "E" then
            s
        else
            s + ".0"

    /// `float` — a number token in the writer's double form. A non-finite
    /// value has no JSON number form; it is written as the STRING of its
    /// name (`"NaN"`, `"Infinity"`, `"-Infinity"`), which the converter
    /// set's reader admits and the algebra's `asFloat` refuses by name.
    let float: JsonEncoder<float> =
        fun v ->
            if Double.IsNaN v then
                JsonValue.String "NaN"
            elif Double.IsPositiveInfinity v then
                JsonValue.String "Infinity"
            elif Double.IsNegativeInfinity v then
                JsonValue.String "-Infinity"
            else
                JsonValue.Number(doubleText v)

    /// `float32` — its shortest round-trip text as a number token (the
    /// declared width is the reader's to apply, as `asFloat32` does).
    let float32: JsonEncoder<float32> =
        fun v ->
            if Single.IsNaN v || Single.IsInfinity v then
                float (Operators.float v)
            else
#if FABLE_COMPILER
                JsonValue.Number(doubleText (Operators.float v))
#else
                JsonValue.Number(v.ToString("R", CultureInfo.InvariantCulture))
#endif

    /// `decimal` — its digits as written, exactly.
    let decimal: JsonEncoder<decimal> =
        fun d -> JsonValue.Number(d.ToString(CultureInfo.InvariantCulture))

    /// A GUID in its hyphenated form.
    let guid: JsonEncoder<Guid> = fun g -> JsonValue.String(g.ToString())

    /// `TimeSpan` — total milliseconds in the writer's double form
    /// (`TimeSpanConverter.Write`).
    let timeSpan: JsonEncoder<TimeSpan> =
        fun t -> JsonValue.Number(doubleText t.TotalMilliseconds)

    /// `DateTime` — the ISO-8601 round-trip string, a local time written
    /// as universal (`DateTimeConverter.Write`).
    let dateTime: JsonEncoder<DateTime> =
        fun d ->
            let universal =
                if d.Kind = DateTimeKind.Local then
                    d.ToUniversalTime()
                else
                    d

            JsonValue.String(universal.ToString("O", CultureInfo.InvariantCulture))

    /// `DateTimeOffset` — the ISO-8601 round-trip string, offset preserved.
    let dateTimeOffset: JsonEncoder<DateTimeOffset> =
        fun d -> JsonValue.String(d.ToString("O", CultureInfo.InvariantCulture))

#if !FABLE_COMPILER
    /// `DateOnly` — the day number (`DateOnlyConverter.Write`). .NET only,
    /// as `JsonDecode.asDateOnly` is.
    let dateOnly: JsonEncoder<DateOnly> =
        fun d -> JsonValue.Number(d.DayNumber.ToString(CultureInfo.InvariantCulture))

    /// `TimeOnly` — its ticks as a STRING (`TimeOnlyConverter.Write`).
    /// .NET only, as `JsonDecode.asTimeOnly` is.
    let timeOnly: JsonEncoder<TimeOnly> =
        fun t -> JsonValue.String(t.Ticks.ToString(CultureInfo.InvariantCulture))
#endif

    /// `byte[]` — base64 (`ByteArrayConverter.Write`).
    let bytes: JsonEncoder<byte[]> =
        fun b ->
            if isNull b then
                JsonValue.Null
            else
                JsonValue.String(Convert.ToBase64String b)

    // ─── Structure ───────────────────────────────────────────────────

    /// `option` — `None` is `null`, `Some v` is `v`'s own form
    /// (`FSharpOptionConverter`).
    let option (inner: JsonEncoder<'T>) : JsonEncoder<'T option> =
        function
        | None -> JsonValue.Null
        | Some v -> inner v

    /// A list, as an array of its elements.
    let list (element: JsonEncoder<'T>) : JsonEncoder<'T list> =
        fun items -> JsonValue.Array(items |> List.map element |> List.toArray)

    /// An array, as an array of its elements.
    let array (element: JsonEncoder<'T>) : JsonEncoder<'T[]> =
        fun items -> JsonValue.Array(Array.map element items)

    /// A set, as an array of its elements in the set's order.
    let set<'T when 'T: comparison> (element: JsonEncoder<'T>) : JsonEncoder<Set<'T>> =
        fun items -> JsonValue.Array(items |> Set.toArray |> Array.map element)

    /// A map key's member name — the inverse of `JsonDecode.Key`.
    type KeyEncoder<'K> = 'K -> string

    /// The key writers `map` takes.
    [<RequireQualifiedAccess>]
    module Key =
        /// The member name itself.
        let string: KeyEncoder<string> = id

        /// An `int32`'s digits.
        let int32: KeyEncoder<int> = fun n -> n.ToString(CultureInfo.InvariantCulture)

        /// An `int64`'s digits.
        let int64: KeyEncoder<int64> = fun n -> n.ToString(CultureInfo.InvariantCulture)

        /// A GUID's hyphenated form.
        let guid: KeyEncoder<Guid> = fun g -> g.ToString()

    /// A map, as an object whose member names are the keys in the map's
    /// order (`FSharpMapStringKeyConverter`; the name forms
    /// `JsonDecode.Key` reads for the other key types).
    let map<'K, 'V when 'K: comparison> (key: KeyEncoder<'K>) (value: JsonEncoder<'V>) : JsonEncoder<Map<'K, 'V>> =
        fun m -> JsonValue.Object(m |> Map.toArray |> Array.map (fun (k, v) -> key k, value v))

    /// Phase 899 — a map whose key is not string-representable, as an
    /// array of `[key, value]` pairs in the map's order, each key in its
    /// own JSON form: what `Fable.SimpleJson` writes for such a key, and
    /// the inverse of `JsonDecode.asMapOf`.
    let mapOf<'K, 'V when 'K: comparison> (key: JsonEncoder<'K>) (value: JsonEncoder<'V>) : JsonEncoder<Map<'K, 'V>> =
        fun m ->
            JsonValue.Array(
                m
                |> Map.toArray
                |> Array.map (fun (k, v) -> JsonValue.Array [| key k; value v |])
            )

    /// A pair, as an array of two (`FSharpTupleConverter`).
    let tuple2 (first: JsonEncoder<'A>) (second: JsonEncoder<'B>) : JsonEncoder<'A * 'B> =
        fun (a, b) -> JsonValue.Array [| first a; second b |]

    /// A triple, as an array of three.
    let tuple3 (first: JsonEncoder<'A>) (second: JsonEncoder<'B>) (third: JsonEncoder<'C>) : JsonEncoder<'A * 'B * 'C> =
        fun (a, b, c) -> JsonValue.Array [| first a; second b; third c |]

    /// A quadruple, as an array of four.
    let tuple4
        (first: JsonEncoder<'A>)
        (second: JsonEncoder<'B>)
        (third: JsonEncoder<'C>)
        (fourth: JsonEncoder<'D>)
        : JsonEncoder<'A * 'B * 'C * 'D> =
        fun (a, b, c, d) -> JsonValue.Array [| first a; second b; third c; fourth d |]

    /// A record, as an object of its members in declaration order
    /// (`FSharpRecordConverter`). The generated encoder supplies each
    /// member's name and its value's encoding.
    let record (members: (string * JsonValue) list) : JsonValue = JsonValue.Object(List.toArray members)

    /// A union case carrying no fields: its NAME as a string.
    let case0 (name: string) : JsonValue = JsonValue.String name

    /// A union case carrying ONE field: `{"Case": field}`.
    let payload (name: string) (field: JsonValue) : JsonValue = JsonValue.Object [| name, field |]

    /// A union case carrying SEVERAL fields: `{"Case": [f1, f2, …]}`.
    let fields (name: string) (values: JsonValue list) : JsonValue =
        JsonValue.Object [| name, JsonValue.Array(List.toArray values) |]

    /// `Result`, the ordinary two-case union: `{"Ok": v}` / `{"Error": e}`.
    let result (ok: JsonEncoder<'T>) (error: JsonEncoder<'E>) : JsonEncoder<Result<'T, 'E>> =
        function
        | Ok v -> payload "Ok" (ok v)
        | Error e -> payload "Error" (error e)