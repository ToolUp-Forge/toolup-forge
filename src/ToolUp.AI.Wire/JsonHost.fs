// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI.Wire

open System.Text
open ToolUp.Remoting.Json

/// The text seam for the one JSON value model (`ToolUp.Remoting.Json.JsonValue`):
/// the tree's ONE JSON writer, and the parse a provider mapper reads a
/// response through.
///
/// **`serialize` is a single portable writer** shared by both hosts — the
/// same F# compiled to .NET and to Fable, never `System.Text.Json` or
/// `JSON.stringify`. Two host engines do not guarantee identical number
/// formatting, string escaping or member order, and **byte-stable output is
/// the load-bearing property the parity gate depends on**. Object members
/// emit in model order; a number emits as the token it carries; output is
/// compact (no insignificant whitespace). `JsonEncode.toText` is this
/// writer, so a provider request body and a remoting argument array are
/// written by one function, and `escape` is its string escape for a caller
/// that writes JSON by hand.
///
/// **`parse` is the portable text parser** (`JsonText.tryParse`) on both
/// hosts — not the browser's `JSON.parse`, which reads every number as a
/// double and keeps only the last of a duplicated key, and not a
/// `System.Text.Json` document walked to `float`s. A number reaches the
/// mapper with the digits it was written with.
[<RequireQualifiedAccess>]
module JsonHost =

    let private hex = "0123456789abcdef"

    /// Whether `c` must be escaped inside a JSON string: the quote, the
    /// backslash and the C0 controls. Nothing else is — non-ASCII and lone
    /// surrogates pass through as themselves.
    let inline private needsEscape (c: char) = c = '"' || c = '\\' || int c <= 0x1F

    let private appendEscape (sb: StringBuilder) (c: char) =
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

    /// The index of the first character of `text` at or after `start` that
    /// needs an escape, or `text.Length`.
    let private nextEscape (text: string) (start: int) =
        let mutable i = start

        while i < text.Length && not (needsEscape text.[i]) do
            i <- i + 1

        i

    /// Append `text`'s escaped content (no quotes). Scan-then-copy: each run
    /// of characters that need no escape is appended whole, never a
    /// character at a time — on the Fable host a `StringBuilder` append is
    /// an array push, so per-character appends dominate a request's cost —
    /// and a string with nothing to escape is appended as itself.
    let private appendEscaped (sb: StringBuilder) (text: string) =
        let first = nextEscape text 0

        if first = text.Length then
            sb.Append text |> ignore
        else
            if first > 0 then
                sb.Append(text.Substring(0, first)) |> ignore

            let mutable i = first

            while i < text.Length do
                appendEscape sb text.[i]
                let runEnd = nextEscape text (i + 1)

                if runEnd > i + 1 then
                    sb.Append(text.Substring(i + 1, runEnd - i - 1)) |> ignore

                i <- runEnd

    let private appendString (sb: StringBuilder) (text: string) =
        sb.Append '"' |> ignore
        appendEscaped sb text
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

            for i in 0 .. items.Length - 1 do
                if i > 0 then
                    sb.Append ',' |> ignore

                append sb items[i]

            sb.Append ']' |> ignore
        | JsonValue.Object members ->
            sb.Append '{' |> ignore

            for i in 0 .. members.Length - 1 do
                if i > 0 then
                    sb.Append ',' |> ignore

                let name, item = members[i]
                appendString sb name
                sb.Append ':' |> ignore
                append sb item

            sb.Append '}' |> ignore

    /// `text` escaped for the inside of a JSON string literal, without the
    /// surrounding quotes, exactly as `serialize` escapes a string: the
    /// quote, the backslash and the C0 controls (short forms where JSON has
    /// one, lower-case `\u00xx` otherwise). The one escaper for a caller
    /// that writes JSON by hand; `null` escapes to the empty string.
    let escape (text: string) : string =
        if isNull text then
            ""
        elif nextEscape text 0 = text.Length then
            text
        else
            let sb = StringBuilder(text.Length + 8)
            appendEscaped sb text
            sb.ToString()

    /// Serialize a `JsonValue` to compact JSON text. Byte-stable: members
    /// emit in model order and the writer is the same code on every host.
    let serialize (value: JsonValue) : string =
        let sb = StringBuilder()
        append sb value
        sb.ToString()

    /// Parse JSON text into a `JsonValue`, numbers as their lexical tokens,
    /// or `None` when the input is not valid JSON (RFC 8259, under
    /// `JsonText`'s default bounds). Never throws.
    let parse (text: string) : JsonValue option =
        match JsonText.tryParse text with
        | Ok value -> Some value
        | Error _ -> None