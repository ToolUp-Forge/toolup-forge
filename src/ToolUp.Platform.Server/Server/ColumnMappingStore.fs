// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Default `IColumnMappingStore` — persists `ColumnMapping` records as
/// JSON sidecars in an `IDataObjectStore`, one object per fingerprint.
/// Mirrors the `ProcessedEntryStore` pattern: namespaced object id, the
/// `_columnmapping` internal data-type tag, `Unversioned` policy
/// (overwrite on re-save), `FableConverters` JSON.
///
/// The fingerprint is hashed (SHA-256 hex) into the object id so an
/// arbitrary CSV header set never produces a path-hostile blob key; the
/// raw fingerprint lives inside the persisted record, so `List` recovers
/// it without reversing the hash.
module ToolUp.Platform.ColumnMappingStore

open System.Text
open System.Security.Cryptography
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ColumnMappingTypes

[<Literal>]
let private Prefix = "_columnmapping__"

/// Internal data-type tag recorded on the persisted blob — the leading
/// underscore matches the `_processed_entry` / `_entity` idiom and lets
/// `List` filter `ListObjects` down to mapping sidecars.
[<Literal>]
let DataType = "_columnmapping"

let private jsonOptions = FableConverters.create ()

let private keyFor (fingerprint: string) : string =
    use sha = SHA256.Create()

    sha.ComputeHash(Encoding.UTF8.GetBytes fingerprint)
    |> Array.map (fun b -> b.ToString("x2"))
    |> String.concat ""

/// Build the `IDataObjectStore` object id for a mapping's fingerprint.
let objectIdFor (fingerprint: string) : string = Prefix + keyFor fingerprint

let private tryDeserialise (bytes: byte[]) : ColumnMapping option =
    try
        Some(JsonSerializer.Deserialize<ColumnMapping>(Encoding.UTF8.GetString bytes, jsonOptions))
    with _ ->
        None

/// Create the default store over an `IDataObjectStore`.
let create (store: IDataObjectStore) : IColumnMappingStore =
    { new IColumnMappingStore with
        member _.Save(scopeId, mapping) = async {
            try
                let bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mapping, jsonOptions))

                match!
                    store.Save(
                        scopeId,
                        objectIdFor mapping.Fingerprint,
                        bytes,
                        DataType,
                        mapping.CreatedBy,
                        Map.empty,
                        Unversioned
                    )
                with
                | Ok _ -> return Ok()
                | Error e -> return Error(string e)
            with ex ->
                return Error ex.Message
        }

        member _.Get(scopeId, fingerprint) = async {
            match! store.Get(scopeId, objectIdFor fingerprint) with
            | Ok(_, bytes) -> return tryDeserialise bytes
            | Error _ -> return None
        }

        member _.List(scopeId) = async {
            let! objects = store.ListObjects scopeId

            let! results =
                objects
                |> List.filter (fun o -> o.DataType = DataType)
                |> List.map (fun o -> async {
                    match! store.Get(scopeId, o.ObjectId) with
                    | Ok(_, bytes) -> return tryDeserialise bytes
                    | Error _ -> return None
                })
                |> Async.Sequential

            return results |> Array.toList |> List.choose id
        }

        member _.Delete(scopeId, fingerprint) = async {
            match! store.Delete(scopeId, objectIdFor fingerprint) with
            | Ok _ -> return Ok()
            | Error NotFound -> return Ok() // idempotent: forgetting an absent mapping is success
            | Error e -> return Error(string e)
        }
    }