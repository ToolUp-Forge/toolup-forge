// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI.Wire

#if FABLE_COMPILER
open Fable.Core
open Fable.Core.JsInterop
#else
open System.Text.Json
#endif

/// The host-bridged serialization seam for `JsonValue`.
///
/// **`parse` is host-bridged** (`#if FABLE_COMPILER`): under Fable it
/// reifies the result of the browser's `JSON.parse`; on .NET it walks a
/// `System.Text.Json` document. Parsing JSON correctly (escapes, numbers,
/// unicode) is exactly the work to delegate to a battle-tested host engine.
///
/// **`serialize` is a single portable writer** shared by both hosts — it is
/// *not* `#if`-split. Two different host JSON engines do not guarantee
/// identical number formatting, string escaping, or object key order, and
/// **byte-stable output is the load-bearing property the parity gate
/// depends on**. A canonical hand-rolled writer — the same F# compiled to
/// both hosts — makes byte-parity true by construction rather than by luck
/// of two engines agreeing. (This is the "hand-rolled pretty-printer"
/// idiom the phase cites.) Object members emit in `JObject` list order;
/// output is compact (no insignificant whitespace).
[<RequireQualifiedAccess>]
module JsonHost =

    // ─── serialize (portable canonical writer) ───────────────────────

    /// Lower-case `\uXXXX` escape, hand-built from nibbles so the output is
    /// identical on .NET and Fable (a numeric `ToString("x4")` format
    /// specifier is not guaranteed to compile to the same text under Fable).
    let private unicodeEscape (code: int) : string =
        let digits = "0123456789abcdef"

        let nibble shift =
            string digits.[(code >>> shift) &&& 0xF]

        "\\u" + nibble 12 + nibble 8 + nibble 4 + nibble 0

    let private writeString (sb: System.Text.StringBuilder) (s: string) : unit =
        sb.Append('"') |> ignore

        for ch in s do
            match ch with
            | '"' -> sb.Append("\\\"") |> ignore
            | '\\' -> sb.Append("\\\\") |> ignore
            | '\b' -> sb.Append("\\b") |> ignore
            | '\f' -> sb.Append("\\f") |> ignore
            | '\n' -> sb.Append("\\n") |> ignore
            | '\r' -> sb.Append("\\r") |> ignore
            | '\t' -> sb.Append("\\t") |> ignore
            // Remaining C0 control characters have no short escape — emit the
            // 6-char \u form. Everything else (including non-ASCII) is emitted
            // literally, matching both JSON.stringify and System.Text.Json's
            // relaxed encoder.
            | c when int c < 0x20 -> sb.Append(unicodeEscape (int c)) |> ignore
            | c -> sb.Append(c) |> ignore

        sb.Append('"') |> ignore

    /// Canonical number formatting. JSON has no NaN / ±∞ (they become
    /// `null`, as `JSON.stringify` does). Integral values in a safe range
    /// emit without a decimal point via int64 formatting (identical on both
    /// hosts); other values fall back to the shortest round-trip string.
    let private writeNumber (sb: System.Text.StringBuilder) (f: float) : unit =
        if System.Double.IsNaN f || System.Double.IsInfinity f then
            sb.Append("null") |> ignore
        elif f = floor f && abs f < 1e15 then
            sb.Append(string (int64 f)) |> ignore
        else
            sb.Append(string f) |> ignore

    let rec private writeValue (sb: System.Text.StringBuilder) (v: JsonValue) : unit =
        match v with
        | JNull -> sb.Append("null") |> ignore
        | JBool b -> sb.Append(if b then "true" else "false") |> ignore
        | JNumber f -> writeNumber sb f
        | JString s -> writeString sb s
        | JArray items ->
            sb.Append('[') |> ignore

            items
            |> List.iteri (fun i item ->
                if i > 0 then
                    sb.Append(',') |> ignore

                writeValue sb item)

            sb.Append(']') |> ignore
        | JObject members ->
            sb.Append('{') |> ignore

            members
            |> List.iteri (fun i (key, value) ->
                if i > 0 then
                    sb.Append(',') |> ignore

                writeString sb key
                sb.Append(':') |> ignore
                writeValue sb value)

            sb.Append('}') |> ignore

    /// Serialize a `JsonValue` to a compact JSON string. Byte-stable: object
    /// members emit in `JObject` order, and the writer is the same code on
    /// every host.
    let serialize (v: JsonValue) : string =
        let sb = System.Text.StringBuilder()
        writeValue sb v
        sb.ToString()

    // ─── parse (host-bridged) ────────────────────────────────────────

#if FABLE_COMPILER
    [<Emit("JSON.parse($0)")>]
    let private jsonParse (s: string) : obj = jsNative

    [<Emit("$0 === null")>]
    let private isJsNull (o: obj) : bool = jsNative

    [<Emit("typeof $0")>]
    let private jsTypeOf (o: obj) : string = jsNative

    [<Emit("Array.isArray($0)")>]
    let private isJsArray (o: obj) : bool = jsNative

    [<Emit("Object.keys($0)")>]
    let private jsKeys (o: obj) : string[] = jsNative

    let rec private reify (o: obj) : JsonValue =
        if isJsNull o then
            JNull
        else
            match jsTypeOf o with
            | "boolean" -> JBool(unbox o)
            | "number" -> JNumber(unbox o)
            | "string" -> JString(unbox o)
            | _ ->
                if isJsArray o then
                    JArray [ for item in (unbox<obj[]> o) -> reify item ]
                else
                    // Object.keys preserves source order for string keys (the
                    // integer-like-key reordering JS performs does not arise
                    // for wire-mapping field names).
                    JObject [ for k in jsKeys o -> k, reify (o?(k)) ]

    /// Parse a JSON string into a `JsonValue`, or `None` when the input is
    /// not valid JSON. Never throws.
    let parse (s: string) : JsonValue option =
        try
            Some(reify (jsonParse s))
        with _ ->
            None
#else
    let rec private fromElement (el: JsonElement) : JsonValue =
        match el.ValueKind with
        | JsonValueKind.Null
        | JsonValueKind.Undefined -> JNull
        | JsonValueKind.True -> JBool true
        | JsonValueKind.False -> JBool false
        | JsonValueKind.Number -> JNumber(el.GetDouble())
        | JsonValueKind.String -> JString(el.GetString())
        | JsonValueKind.Array -> JArray [ for item in el.EnumerateArray() -> fromElement item ]
        | JsonValueKind.Object -> JObject [ for prop in el.EnumerateObject() -> prop.Name, fromElement prop.Value ]
        | _ -> JNull

    /// Parse a JSON string into a `JsonValue`, or `None` when the input is
    /// not valid JSON. Never throws. The value is fully materialised before
    /// the document is disposed, so no `JsonElement` escapes the seam.
    let parse (s: string) : JsonValue option =
        try
            use doc = JsonDocument.Parse(s)
            Some(fromElement doc.RootElement)
        with _ ->
            None
#endif