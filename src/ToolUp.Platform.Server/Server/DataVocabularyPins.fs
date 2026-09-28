// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// Phase 880 — the server half of `DataVocabulary` (Phase 594): the SHA-256
// hash, the System.Text.Json loader and the pin that carries the hash. They
// lived in ToolUp.Platform.Core behind `#if !FABLE_COMPILER`, with a Fable
// arm of `pin` that returned an empty hash nothing could verify. Every caller
// is server-tier, so they compile here. The pure half (governance, drift,
// canonical JSON) stays in Core; F# resolves a name under `DataVocabulary`
// across both modules, so a site that references the server tier reads as
// before.

/// The server half of `DataVocabulary`: hash, loader and pin.
module DataVocabulary =

    /// Lowercase-hex SHA-256 over the pack's canonical JSON. Server-only
    /// (BCL crypto); the canonical determinism above is what makes it a
    /// stable pin fingerprint.
    let hash (pack: DataVocabularyPack) : string =
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(DataVocabulary.canonicalJson pack)
        )
        |> System.Convert.ToHexString
        |> _.ToLowerInvariant()

    /// Load a pack from its canonical JSON. Server-only (`System.Text.Json`
    /// is not Fable-compatible). Returns `Error` with a descriptive reason
    /// on malformed input or an unknown field value-type — a pinned pack that
    /// does not parse is a compose-time defect, not a silent skip.
    let load (json: string) : Result<DataVocabularyPack, string> =
        try
            use doc = System.Text.Json.JsonDocument.Parse json
            let root = doc.RootElement

            let getString (el: System.Text.Json.JsonElement) (name: string) : Result<string, string> =
                match el.TryGetProperty name with
                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String -> Ok(v.GetString())
                | _ -> Error(sprintf "missing or non-string property '%s'" name)

            let getInt (el: System.Text.Json.JsonElement) (name: string) : Result<int, string> =
                match el.TryGetProperty name with
                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.Number -> Ok(v.GetInt32())
                | _ -> Error(sprintf "missing or non-numeric property '%s'" name)

            let optString (el: System.Text.Json.JsonElement) (name: string) : string =
                match el.TryGetProperty name with
                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String -> v.GetString()
                | _ -> ""

            let resultList (items: Result<'a, string> list) : Result<'a list, string> =
                (Ok [], items)
                ||> List.fold (fun acc item ->
                    match acc, item with
                    | Ok xs, Ok x -> Ok(xs @ [ x ])
                    | Error e, _ -> Error e
                    | _, Error e -> Error e)

            let parseField (el: System.Text.Json.JsonElement) : Result<VocabularyField, string> =
                getString el "name"
                |> Result.bind (fun name ->
                    getString el "type"
                    |> Result.bind (fun typeToken ->
                        match DataVocabulary.fieldTypeOfWire typeToken with
                        | None -> Error(sprintf "field '%s' has unknown value-type '%s'" name typeToken)
                        | Some fieldType ->
                            let unit =
                                match el.TryGetProperty "unit" with
                                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String ->
                                    Some(v.GetString())
                                | _ -> None

                            Ok {
                                Name = name
                                Type = fieldType
                                Unit = unit
                                Description = optString el "description"
                            }))

            let parseEntry (el: System.Text.Json.JsonElement) : Result<VocabularyEntry, string> =
                getString el "typeName"
                |> Result.bind (fun typeName ->
                    let fields =
                        match el.TryGetProperty "fields" with
                        | true, arr when arr.ValueKind = System.Text.Json.JsonValueKind.Array ->
                            arr.EnumerateArray() |> Seq.map parseField |> List.ofSeq |> resultList
                        | _ -> Ok []

                    fields
                    |> Result.map (fun fs -> {
                        TypeName = typeName
                        Fields = fs
                        Description = optString el "description"
                    }))

            getString root "id"
            |> Result.bind (fun id ->
                getString root "namespace"
                |> Result.bind (fun ns ->
                    match root.TryGetProperty "version" with
                    | true, ver ->
                        getInt ver "major"
                        |> Result.bind (fun major ->
                            getInt ver "minor"
                            |> Result.bind (fun minor ->
                                let entries =
                                    match root.TryGetProperty "entries" with
                                    | true, arr when arr.ValueKind = System.Text.Json.JsonValueKind.Array ->
                                        arr.EnumerateArray() |> Seq.map parseEntry |> List.ofSeq |> resultList
                                    | _ -> Ok []

                                entries
                                |> Result.map (fun es -> {
                                    Id = id
                                    Namespace = ns
                                    Version = { Major = major; Minor = minor }
                                    Entries = es
                                })))
                    | _ -> Error "missing 'version' object"))
        with ex ->
            Error(sprintf "malformed vocabulary pack JSON: %s" ex.Message)

    /// The pinnable `(Id, Version, Hash)` of a pack. `Hash` is the canonical
    /// hash, so a counterparty can detect an in-place mutation.
    let pin (pack: DataVocabularyPack) : VocabularyPackPin = {
        PackId = pack.Id
        Version = pack.Version
        Hash = hash pack
    }