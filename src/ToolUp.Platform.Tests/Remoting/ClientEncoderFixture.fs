// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 853.D — the cross-host fixture, compiled into BOTH packs.
///
/// Each case is a value of a platform ARGUMENT type, the text the GENERATED
/// client encoder (`PlatformClientProxies`) writes for it on the .NET host,
/// and a check that a text decodes back to the value through the Phase 841
/// generated decoder the server's argument seam registers for it. The .NET
/// pack holds the .NET encoder to the pinned text and the server's seam to
/// the value; the Fable pack holds the TRANSPILED encoder — the one a
/// browser runs — to the same value, and to the same text where the two
/// hosts format identically (`HostStable`; a `DateTime` is the exception,
/// the Fable host writing fewer fractional digits of the same instant).
module ToolUp.Platform.Tests.Remoting.ClientEncoderFixture

open System
open ToolUp.Platform
open ToolUp.Remoting
open ToolUp.Remoting.Json
open ToolUp.Remoting.Client

/// One pinned case.
type EncoderCase = {
    Name: string
    /// The API record whose argument this is (the server scopes the
    /// decoder to it).
    Record: string
    /// The argument's type.
    ValueType: Type
    /// The value, boxed (for the .NET pack's server-seam check).
    Value: obj
    /// The text the generated encoder writes on the .NET host.
    Pinned: string
    /// Whether the Fable host writes the identical text.
    HostStable: bool
    /// Encode the value with the generated encoder.
    Encode: unit -> string
    /// Decode a text through the Phase 841 generated decoder and compare
    /// the result with the value — as INSTANTS where the value carries a
    /// `DateTime`. Until Phase 885 the Fable host's `asDateTime` read the
    /// writer's `…Z` text as the same instant in a non-UTC kind, which F#
    /// equality on that host did not equate with the UTC value; it now
    /// keeps the text's kind, and the instant comparison stays as the
    /// weaker claim this fixture needs.
    DecodesToValue: string -> Result<unit, string>
}

let inline private case
    (name: string)
    (record: string)
    (pinned: string)
    (hostStable: bool)
    (value: 'T)
    (normalise: 'T -> 'T)
    (encode: JsonEncoder<'T>)
    (decode: JsonDecoder<'T>)
    : EncoderCase =
    {
        Name = name
        Record = record
        ValueType = typeof<'T>
        Value = box value
        Pinned = pinned
        HostStable = hostStable
        Encode = fun () -> JsonEncode.toText (encode value)
        DecodesToValue =
            fun text ->
                match JsonText.tryParse text |> Result.bind decode with
                | Ok decoded when normalise decoded = normalise value -> Ok()
                | Ok decoded -> Error(sprintf "decoded a different value: %A" decoded)
                | Error error -> Error(DecodeError.render error)
    }

let private conversion: ColumnMappingTypes.Conversion = {
    Fingerprint = "fp-1"
    TargetTypeId = "sales"
    Mapping = Map.ofList [ "Region", "region"; "Amount", "amount" ]
    Remediation =
        Map.ofList [
            "Amount",
            [
                ColumnMappingTypes.CellTransform.Trim
                ColumnMappingTypes.CellTransform.StripCurrency "£"
                ColumnMappingTypes.CellTransform.BlankNullMarkers [ "n/a"; "-" ]
                ColumnMappingTypes.CellTransform.ParseDateToIso ColumnMappingTypes.DateOrder.DayFirst
            ]
        ]
    SourceHeaders = [ "Region"; "Amount"; "Note \"quoted\"" ]
    Derived = [
        ({
            Field = "label"
            Expr =
                ColumnMappingTypes.ColumnExpr.Concat(
                    [
                        ColumnMappingTypes.ColumnExpr.SourceColumn "Region"
                        ColumnMappingTypes.ColumnExpr.SplitTake(ColumnMappingTypes.ColumnExpr.Constant "a/b", "/", 1)
                    ],
                    " - "
                )
        }
        : ColumnMappingTypes.DerivedColumn)
    ]
    CreatedBy = "ops@example.test"
    CreatedAt = DateTime(2026, 9, 27, 10, 30, 0, DateTimeKind.Utc)
}

let private auditQuery: AuditTrailQuery = {
    From = None
    To = None
    EventType = Some "login"
    Actor = None
    Cursor = Some "c\"1\n"
    PageSize = 50
}

/// The cases.
let cases: EncoderCase list = [
    case
        "a Conversion: maps, lists, a recursive union's several-field cases, a payload case, a UTC DateTime"
        "IConversionApi"
        """{"Fingerprint":"fp-1","TargetTypeId":"sales","Mapping":{"Amount":"amount","Region":"region"},"Remediation":{"Amount":["Trim",{"StripCurrency":"£"},{"BlankNullMarkers":["n/a","-"]},{"ParseDateToIso":"DayFirst"}]},"SourceHeaders":["Region","Amount","Note \"quoted\""],"Derived":[{"Field":"label","Expr":{"Concat":[[{"SourceColumn":"Region"},{"SplitTake":[{"Constant":"a/b"},"/",1]}]," - "]}}],"CreatedBy":"ops@example.test","CreatedAt":"2026-09-27T10:30:00.0000000Z"}"""
        false
        conversion
        (fun c -> {
            c with
                CreatedAt = c.CreatedAt.ToUniversalTime()
        })
        PlatformClientProxies.encodeConversion
        PlatformJsonDecoders.conversion
    case
        "an AuditTrailQuery: None members written null, an escaped string, an int"
        "IAuditViewApi"
        """{"From":null,"To":null,"EventType":"login","Actor":null,"Cursor":"c\"1\n","PageSize":50}"""
        true
        auditQuery
        id
        PlatformClientProxies.encodeAuditTrailQuery
        PlatformJsonDecoders.auditTrailQuery
    case
        "a tupled argument: an array of its elements"
        "IConversionApi"
        """["fp-1","sales"]"""
        true
        ("fp-1", "sales")
        id
        (JsonEncode.tuple2 JsonEncode.string JsonEncode.string)
        (JsonDecode.tuple2 JsonDecode.asString JsonDecode.asString)
]