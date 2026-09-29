// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 843 — the cross-host contract of the client's JSON response
/// decode, pinned as data.
///
/// A response is written on the server by the remoting STJ converter set
/// and, when its type has a registered JSON decoder, read in the browser
/// by `JsonText` + the `JsonDecode` combinators. Neither host can run the
/// other's writer, so the agreement is held the way Phase 817 held its
/// envelope: ONE literal, asserted from both sides. The .NET pack holds
/// the encoder to this text and `JsonRead` (System.Text.Json) to the
/// model; the Fable pack holds `JsonText` to the same model and the
/// decoder to the same value. Compiled into both packs, so the sample,
/// the decoder, the literal and the model cannot drift apart.
///
/// The sample carries the three values Phase 785.F named as the ones a
/// `float` carrier loses — `int64` past 2^53, a `decimal` at full
/// precision, a `TimeSpan` whose last tick the millisecond text only
/// just holds — plus the shapes every record has: an escaped string, an
/// option, a list.
module ToolUp.Platform.Tests.Remoting.JsonClientDecodeFixture

open System
open ToolUp.Remoting.Json

type LedgerLine = {
    Id: int64
    Amount: decimal
    Tiny: decimal
    Elapsed: TimeSpan
    Label: string
    Note: string option
    Tags: string list
}

/// 2^53 + 1, `Decimal.MaxValue`, the smallest positive decimal, and the
/// TimeSpan whose tick the STJ reader was pinned losing (Phase 784).
let sample: LedgerLine = {
    Id = 9007199254740993L
    Amount = 79228162514264337593543950335M
    Tiny = 0.0000000000000000000000000001M
    Elapsed = TimeSpan.FromTicks 512112158396L
    Label = "caf\u00e9 \"quoted\" \\ tab\t"
    Note = Some "exact"
    Tags = [ "a"; "b" ]
}

/// The algebra decoder for `LedgerLine`, in the shape the generator emits.
let decoder: JsonDecoder<LedgerLine> =
    JsonDecode.succeed (fun id amount tiny elapsed label note tags -> {
        Id = id
        Amount = amount
        Tiny = tiny
        Elapsed = elapsed
        Label = label
        Note = note
        Tags = tags
    })
    |> JsonDecode.apply (JsonDecode.field "Id" JsonDecode.asInt64)
    |> JsonDecode.apply (JsonDecode.field "Amount" JsonDecode.asDecimal)
    |> JsonDecode.apply (JsonDecode.field "Tiny" JsonDecode.asDecimal)
    |> JsonDecode.apply (JsonDecode.field "Elapsed" JsonDecode.asTimeSpan)
    |> JsonDecode.apply (JsonDecode.field "Label" JsonDecode.asString)
    |> JsonDecode.apply (JsonDecode.optionalField "Note" JsonDecode.asString)
    |> JsonDecode.apply (JsonDecode.field "Tags" (JsonDecode.list JsonDecode.asString))

/// What the remoting STJ converter set writes for `sample`, byte for byte.
[<Literal>]
let SamplePayload =
    "{\"Id\":\"+9007199254740993\",\"Amount\":79228162514264337593543950335,\"Tiny\":0.0000000000000000000000000001,\"Elapsed\":51211215.8396,\"Label\":\"café \\\"quoted\\\" \\\\ tab\\t\",\"Note\":\"exact\",\"Tags\":[\"a\",\"b\"]}"

/// The value model of `SamplePayload`, as both hosts must read it.
/// Note what the writer chose: `int64` as a SIGNED STRING, the two
/// decimals and the TimeSpan's milliseconds as number tokens.
let sampleModel: JsonValue =
    JsonValue.Object [|
        "Id", JsonValue.String "+9007199254740993"
        "Amount", JsonValue.Number "79228162514264337593543950335"
        "Tiny", JsonValue.Number "0.0000000000000000000000000001"
        "Elapsed", JsonValue.Number "51211215.8396"
        "Label", JsonValue.String "café \"quoted\" \\ tab\t"
        "Note", JsonValue.String "exact"
        "Tags", JsonValue.Array [| JsonValue.String "a"; JsonValue.String "b" |]
    |]

/// Texts the two hosts must read identically — each with the model both
/// must produce, or `None` where both must refuse. Chosen at the edges
/// where a JSON reader most plausibly differs: number spelling, member
/// order and duplicates, escapes, surrogates, whitespace, and the
/// constructs System.Text.Json's defaults refuse.
let agreementCases: (string * string * JsonValue option) list = [
    "numbers stay as written",
    "[1.0, -0, 1e400, 0.10000000000000000555, 9007199254740993]",
    Some(
        JsonValue.Array [|
            JsonValue.Number "1.0"
            JsonValue.Number "-0"
            JsonValue.Number "1e400"
            JsonValue.Number "0.10000000000000000555"
            JsonValue.Number "9007199254740993"
        |]
    )

    "array-index keys keep wire order",
    """{"b":1,"10":2,"2":3}""",
    Some(
        JsonValue.Object [|
            "b", JsonValue.Number "1"
            "10", JsonValue.Number "2"
            "2", JsonValue.Number "3"
        |]
    )

    "a duplicate key stays visible",
    """{"Ok":1,"Ok":2}""",
    Some(JsonValue.Object [| "Ok", JsonValue.Number "1"; "Ok", JsonValue.Number "2" |])

    "every escape, and a surrogate pair",
    "[\"a\\\"b\\\\c\\/d\\b\\f\\n\\r\\t\", \"\\u00e9\\u00C9\", \"\\uD83D\\uDE00\"]",
    Some(
        JsonValue.Array [|
            JsonValue.String "a\"b\\c/d\b\f\n\r\t"
            JsonValue.String "\u00e9\u00c9"
            JsonValue.String "\uD83D\uDE00"
        |]
    )

    // An unpaired surrogate ESCAPE is not valid UTF-16, and
    // `JsonElement.GetString` refuses it rather than substituting U+FFFD.
    "an unpaired high-surrogate escape is refused", "[\"\\uD800x\"]", None
    "a high surrogate followed by another is refused", "\"\\uD800\\uD800\"", None
    "an unpaired low-surrogate escape is refused", "\"\\uDC00\"", None

    "literals, empty containers, and the four whitespace characters",
    " \t\r\n{ \"t\" : true , \"f\":false,\"n\":null,\"a\":[ ],\"o\":{ } } \n",
    Some(
        JsonValue.Object [|
            "t", JsonValue.Bool true
            "f", JsonValue.Bool false
            "n", JsonValue.Null
            "a", JsonValue.Array [||]
            "o", JsonValue.Object [||]
        |]
    )

    "a trailing comma is refused", "[1,]", None
    "a leading plus is refused", "+1", None
    "a byte-order mark is refused", "\uFEFF[1]", None
    "an empty text is refused", "", None
    "whitespace alone is refused", "  ", None
    "trailing content is refused", """{"a":1}x""", None
    "a raw tab inside a string is refused", "\"a\tb\"", None
    "an unknown escape is refused", "\"\\x\"", None
    "a leading zero is refused", "01", None
    "a bare fraction point is refused", "1.", None
    "an unterminated string is refused", "\"abc", None
    "a single-quoted string is refused", "'a'", None
    "a comment is refused", "[1 /* c */]", None
    "a vertical tab is not whitespace", "\u000B1", None
    "a truncated literal is refused", "tru", None
    "sixty-four nested containers are read",
    String.replicate 64 "[" + String.replicate 64 "]",
    Some(List.fold (fun inner _ -> JsonValue.Array [| inner |]) (JsonValue.Array [||]) [ 1..63 ])
    "sixty-five nested containers are refused", String.replicate 65 "[" + String.replicate 65 "]", None
]