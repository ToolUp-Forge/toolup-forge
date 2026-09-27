// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 885.A — the browser's writer, pinned as data from both hosts.
///
/// The JSON registration gate (Phase 840) compares a decoder against the
/// STJ converter set over text the SERVER's writer produced. A browser's
/// text is written by the Fable client instead — `Fable.SimpleJson`'s
/// `Convert.serialize` on a reflective proxy, `JsonEncode` on a generated
/// one — and the gate's second reference (`JsonDecoders.browserOracle`)
/// writes it with `BrowserJsonWriter`, a .NET mirror of the former. The
/// gate cannot run under Fable, so the mirror is held to the transpiled
/// writer the Phase 817 / 843 / 853 way: ONE literal per case, compiled
/// into both packs. The Fable pack holds `Convert.serialize` to each
/// `Browser` text, the .NET pack holds the mirror to the same text, so
/// neither the mirror nor the writer it stands in for can move without a
/// red case naming the shape.
///
/// The cases walk every shape a decoder can be for, and linger on the
/// three Phase 885 decided: a `decimal` (QUOTED, its scale kept), a
/// `TimeSpan` (the host's millisecond double, JavaScript's spelling), a
/// `DateTime` (`toISOString`, to the millisecond). `Local` `DateTime`s are
/// not pinned — their text carries the machine's zone offset.
///
/// `Booking` is the acceptance case: a consumer API argument carrying a
/// `decimal`, a `TimeSpan` and a `DateTime`, sent from the browser through
/// the reflective proxy (`ReflectiveBody`, captured off the wire by the
/// Fable pack) and through a generated-style encoder (`EncodedBody`, as
/// the transpiled `JsonEncode` writes it), and decoded by the server's
/// argument seam to the value on the .NET side.
module ToolUp.Platform.Tests.Remoting.BrowserWriterFixture

open System
open ToolUp.Remoting
open ToolUp.Remoting.Json

type BrowserColour =
    | Red
    | Green

type BrowserShape =
    | Dot
    | Circle of radius: float
    | Rect of width: float * height: float

type BrowserRecord = {
    Name: string
    Count: int
    Tags: string list
    Maybe: int option
}

/// One pinned case: a value, its type, and the text the transpiled
/// `Convert.serialize` writes for it.
type BrowserCase = {
    Name: string
    ValueType: Type
    Value: obj
    /// What `Fable.SimpleJson`'s `Convert.serialize` writes in the
    /// browser, byte for byte.
    Browser: string
}

let inline private case (name: string) (browser: string) (value: 'T) : BrowserCase = {
    Name = name
    ValueType = typeof<'T>
    Value = box value
    Browser = browser
}

/// The pinned cases.
let cases: BrowserCase list = [
    case
        "a string: quote.js's escapes, a raw non-ASCII letter, a raw surrogate pair"
        "\"café \\\"q\\\" \\\\ \\n\\t\\u0001\\u2028 \U0001F600\""
        "café \"q\" \\ \n\t\u0001  \U0001F600"
    case "a char" "\"x\"" 'x'
    case "unit" "null" ()
    case "a bool" "true" true
    case "an int" "-7" -7
    case "an int16" "-300" -300s
    case "an sbyte" "-3" -3y
    case "a byte" "255" 255uy
    case "a uint16" "65535" 65535us
    case "a uint32" "4294967295" 4294967295u
    case "an enum: its number" "5" DayOfWeek.Friday
    case "an int64: a quoted string, no plus sign" "\"9007199254740993\"" 9007199254740993L
    case "a negative int64" "\"-5\"" -5L
    case "a uint64: a quoted string" "\"18446744073709551615\"" UInt64.MaxValue
    case "an integral float: no .0" "1" 1.0
    case "a float" "-2.5" -2.5
    case "a float that is not a short binary fraction" "0.1" 0.1
    case "a float at 1e21: exponent form" "1e+21" 1e21
    case "a float just under 1e21: plain" "100000000000000000000" 1e20
    case "a small float: exponent form" "1.5e-7" 1.5e-7
    case "a float at 1e-6: plain" "0.000001" 1e-6
    case "NaN: a quoted name" "\"NaN\"" nan
    case "a float32" "0.125" 0.125f
    case "a decimal: QUOTED, its scale kept" "\"1234.50\"" 1234.50M
    case "a decimal: Decimal.MaxValue" "\"79228162514264337593543950335\"" 79228162514264337593543950335M
    case "a decimal: the smallest positive" "\"0.0000000000000000000000000001\"" 0.0000000000000000000000000001M
    case "a negative decimal" "\"-0.5\"" -0.5M
    case "a zero decimal with scale" "\"0.00\"" 0.00M
    case "an integral decimal" "\"100\"" 100M
    case "a Guid" "\"0f8fad5b-d9cb-469f-a165-70867728950e\"" (Guid "0f8fad5b-d9cb-469f-a165-70867728950e")
    case "a TimeSpan: fractional milliseconds" "1.5" (TimeSpan.FromMilliseconds 1.5)
    case "a TimeSpan: whole milliseconds, no .0" "90000" (TimeSpan.FromSeconds 90.0)
    case "a negative TimeSpan" "-250.25" (TimeSpan.FromMilliseconds(-250.25))
    case "a one-tick TimeSpan" "0.0001" (TimeSpan.FromMilliseconds 0.0001)
    case "a zero TimeSpan" "0" TimeSpan.Zero
    case
        "a UTC DateTime: toISOString, to the millisecond"
        "\"2026-09-27T10:30:00.123Z\""
        (DateTime(2026, 9, 27, 10, 30, 0, 123, DateTimeKind.Utc))
    case
        "an Unspecified DateTime: its fields, no offset"
        "\"2026-01-15T08:05:09.007\""
        (DateTime(2026, 1, 15, 8, 5, 9, 7))
    case
        "a DateTimeOffset: the clock at the offset, then the offset"
        "\"2026-09-27T10:30:00.123+02:00\""
        (DateTimeOffset(2026, 9, 27, 10, 30, 0, 123, TimeSpan.FromHours 2.0))
    case "a byte array: an array of numbers" "[1, 2, 255]" [| 1uy; 2uy; 255uy |]
    case "Some" "3" (Some 3)
    case "None" "null" (None: int option)
    case "a list" "[\"a\", \"b\"]" [ "a"; "b" ]
    case "an empty list" "[]" ([]: int list)
    case "an array" "[1, 2]" [| 1; 2 |]
    case "a set, in order" "[1, 3]" (Set.ofList [ 3; 1 ])
    case "a string-keyed map: an object" "{\"a\": 1, \"b\": 2}" (Map.ofList [ "b", 2; "a", 1 ])
    case "an int-keyed map: an object, keys quoted" "{\"2\": \"x\"}" (Map.ofList [ 2, "x" ])
    case "an enum-union-keyed map: an object" "{\"Red\": 1}" (Map.ofList [ Red, 1 ])
    case "a tuple-keyed map: an array of pairs" "[[[1, 2], \"p\"]]" (Map.ofList [ (1, 2), "p" ])
    case "a tuple" "[1, \"a\", true]" (1, "a", true)
    case
        "a record: members separated by comma-space"
        "{\"Name\": \"n\", \"Count\": 2, \"Tags\": [\"t\"], \"Maybe\": null}"
        {
            Name = "n"
            Count = 2
            Tags = [ "t" ]
            Maybe = None
        }
    case "an enum-like union case" "\"Green\"" Green
    case "a field-less case" "\"Dot\"" Dot
    case "a one-field case" "{\"Circle\": 1.5}" (Circle 1.5)
    case "a several-field case: note the space before the brace" "{\"Rect\": [2, 3] }" (Rect(2.0, 3.0))
    case "a Result" "{\"Ok\": 1}" (Ok 1: Result<int, string>)
]

// ─── The acceptance case: a consumer API from the browser ────────────

/// A consumer argument carrying all three of Phase 885's shapes.
type Booking = {
    Price: decimal
    Duration: TimeSpan
    At: DateTime
    Note: string option
}

/// The consumer API the browser calls.
type BookingApi = { Book: Booking -> Async<unit> }

let booking: Booking = {
    Price = 1234.50M
    Duration = TimeSpan.FromMilliseconds 5400000.25
    At = DateTime(2026, 9, 27, 10, 30, 0, 123, DateTimeKind.Utc)
    Note = Some "window seat"
}

/// The argument decoder the generator would emit for `Booking`.
let bookingDecoder: JsonDecoder<Booking> =
    JsonDecode.succeed (fun price duration at note -> {
        Price = price
        Duration = duration
        At = at
        Note = note
    })
    |> JsonDecode.apply (JsonDecode.field "Price" JsonDecode.asDecimal)
    |> JsonDecode.apply (JsonDecode.field "Duration" JsonDecode.asTimeSpan)
    |> JsonDecode.apply (JsonDecode.field "At" JsonDecode.asDateTime)
    |> JsonDecode.apply (JsonDecode.optionalField "Note" JsonDecode.asString)

/// The argument encoder a generated proxy would emit for `Booking`.
let bookingEncoder: JsonEncoder<Booking> =
    fun b ->
        JsonEncode.record [
            "Price", JsonEncode.decimal b.Price
            "Duration", JsonEncode.timeSpan b.Duration
            "At", JsonEncode.dateTime b.At
            "Note", JsonEncode.option JsonEncode.string b.Note
        ]

/// The request body the REFLECTIVE proxy sends for `Book booking`, as the
/// Fable pack captures it off the wire: `Fable.SimpleJson`, the one
/// argument wrapped in the argument array.
[<Literal>]
let ReflectiveBody =
    "[{\"Price\": \"1234.50\", \"Duration\": 5400000.25, \"At\": \"2026-09-27T10:30:00.123Z\", \"Note\": \"window seat\"}]"

/// The request body a GENERATED proxy sends for `Book booking` in the
/// browser: `JsonEncode` as Fable transpiles it (the decimal's digits,
/// the millisecond double with the writer's `.0` rule, `toISOString`).
[<Literal>]
let EncodedBody =
    "[{\"Price\":1234.50,\"Duration\":5400000.25,\"At\":\"2026-09-27T10:30:00.123Z\",\"Note\":\"window seat\"}]"

/// The same encoder on the .NET host — the server writer's spelling.
[<Literal>]
let ServerBody =
    "[{\"Price\":1234.50,\"Duration\":5400000.25,\"At\":\"2026-09-27T10:30:00.1230000Z\",\"Note\":\"window seat\"}]"