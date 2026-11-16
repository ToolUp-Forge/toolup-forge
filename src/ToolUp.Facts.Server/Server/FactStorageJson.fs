// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open Fuaran.Facts
open System
open System.Text.Json
open System.Text.Json.Serialization
open ToolUp.Remoting.Json.SystemTextJson

// ─── The stored JSON shape of a fact's members ───────────────────────
//
// Fact-table row images, staged batches, run options and the metric
// surface's value column hold a fact's period and value as JSON. That JSON
// was written while a period's endpoints were `DateTime`s and a value's
// numbers were `decimal`s, and the stored documents are not rewritten. The
// platform converters would write the instant and decimal-text types in a
// shape of their own and refuse the stored one, so the documents that embed
// them are written and read through these options instead:
//
//   - an `Instant` is the date-time string the platform writes for a
//     `DateTime` (always UTC); a stored string read back with an
//     `Unspecified` kind is read as UTC, the instant's own rule;
//   - a `FactValue` is one member named for its case, with every number a
//     JSON number token holding the exact decimal text. A series is its
//     address string, as before; a series carrying a locator, which the
//     stored shape cannot hold, is an [address, locator] pair.
//
// A persisted `Fact` is not written through these options: it is written in
// the specification's encoding (`FactWire.writeFact`) and read with
// `FactStoreCodec.read`.

/// JSON options for stored documents that embed a fact's period or value.
module internal FactStorageJson =

    type private InstantConverter() =
        inherit JsonConverter<Instant>()

        override _.Write(writer: Utf8JsonWriter, value: Instant, _: JsonSerializerOptions) =
            JsonSerializer.Serialize(writer, Instant.toDateTime value, PlatformJson.options)

        override _.Read(reader: byref<Utf8JsonReader>, _: Type, _: JsonSerializerOptions) =
            JsonSerializer.Deserialize<DateTime>(&reader, PlatformJson.options)
            |> Instant.ofDateTime

    let private number (e: JsonElement) =
        if e.ValueKind <> JsonValueKind.Number then
            raise (JsonException "a stored fact value's number is not a JSON number")

        DecimalText.parse (e.GetRawText())

    let private address (e: JsonElement) =
        match SeriesAddress.tryParse (e.GetString()) with
        | Some a -> a
        | None -> raise (JsonException "a stored series address does not parse")

    let private pair (e: JsonElement) =
        e.ValueKind = JsonValueKind.Array && e.GetArrayLength() = 2

    /// A stored value, from its JSON element.
    let private valueOf (element: JsonElement) : FactValue =
        let members =
            if element.ValueKind = JsonValueKind.Object then
                List.ofSeq (element.EnumerateObject())
            else
                []

        match members with
        | [ p ] ->
            let v = p.Value

            match p.Name, v.ValueKind with
            | "Scalar", _ -> Scalar(number v)
            | "Interval", _ when pair v -> Interval(number v[0], number v[1])
            | "Series", JsonValueKind.String -> Series(address v, None)
            | "Series", _ when pair v -> Series(address v[0], Some(v[1].GetString()))
            | "Distribution", JsonValueKind.Object ->
                v.EnumerateObject()
                |> Seq.map (fun b -> b.Name, number b.Value)
                |> Map.ofSeq
                |> Distribution
            | "Categorical", JsonValueKind.String -> Categorical(v.GetString())
            | "Absent", JsonValueKind.String -> Absent(v.GetString())
            | name, _ -> raise (JsonException(sprintf "a stored fact value has an unreadable case '%s'" name))
        | _ -> raise (JsonException "a stored fact value is not a one-member object")

    type private FactValueConverter() =
        inherit JsonConverter<FactValue>()

        override _.Write(w: Utf8JsonWriter, value: FactValue, _: JsonSerializerOptions) =
            let number (d: DecimalText) = w.WriteRawValue d.Text
            w.WriteStartObject()

            match value with
            | Scalar d ->
                w.WritePropertyName "Scalar"
                number d
            | Interval(low, high) ->
                w.WritePropertyName "Interval"
                w.WriteStartArray()
                number low
                number high
                w.WriteEndArray()
            | Series(seriesAddress, None) -> w.WriteString("Series", seriesAddress.Text)
            | Series(seriesAddress, Some locator) ->
                w.WritePropertyName "Series"
                w.WriteStartArray()
                w.WriteStringValue seriesAddress.Text
                w.WriteStringValue locator
                w.WriteEndArray()
            | Distribution buckets ->
                w.WritePropertyName "Distribution"
                w.WriteStartObject()

                for KeyValue(bucket, v) in buckets do
                    w.WritePropertyName bucket
                    number v

                w.WriteEndObject()
            | Categorical label -> w.WriteString("Categorical", label)
            | Absent reason -> w.WriteString("Absent", reason)

            w.WriteEndObject()

        override _.Read(reader: byref<Utf8JsonReader>, _: Type, _: JsonSerializerOptions) =
            use document = JsonDocument.ParseValue(&reader)
            valueOf document.RootElement

    /// The platform converter set, with an `Instant` and a `FactValue` in
    /// their stored shapes ahead of it.
    let options: JsonSerializerOptions =
        let o = FableConverters.create ()
        o.Converters.Insert(0, InstantConverter())
        o.Converters.Insert(0, FactValueConverter())
        o

/// The one read of a stored fact that does not verify its id: the export an
/// id migration plans from. A fact stored under an id an earlier identity
/// rule minted (before the canonical frame) is in the legacy saved shape and
/// is refused by `FactStoreCodec.read`, which is right for every read but the
/// one that exists to re-key it. Nothing else may call this.
[<RequireQualifiedAccess>]
module FactIdMigrationRead =

    /// The fact a stored text holds: through `FactStoreCodec.read` when it
    /// reads, else — for the legacy saved shape only — as stored, its id
    /// unverified.
    let read (text: string) : Result<Fact, Refusal> =
        match FactStoreCodec.read text with
        | Ok f -> Ok f
        | Error _ when not (isNull text) && text.Contains "\"FactId\"" ->
            try
                Ok(JsonSerializer.Deserialize<Fact>(text, FactStorageJson.options))
            with ex ->
                Error(Refusal.make RefusalClass.ShapeInvalid "$" ex.Message)
        | Error refusal -> Error refusal