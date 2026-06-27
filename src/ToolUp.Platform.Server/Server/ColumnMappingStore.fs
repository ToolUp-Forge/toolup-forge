// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Default `IConversionStore` — persists `Conversion` recipes and
/// `ConversionRecord` provenance as JSON sidecars in an `IDataObjectStore`.
/// Mirrors the `ProcessedEntryStore` pattern: namespaced object id, an
/// internal data-type tag, `Unversioned` policy (overwrite on re-save),
/// `FableConverters` JSON.
///
/// Recipes are keyed by `(fingerprint, targetTypeId)` so a column-structure
/// can hold several (a single file → several data objects); provenance
/// records are keyed by produced file name. Keys are hashed (SHA-256 hex)
/// into the object id so arbitrary CSV headers / file names never produce a
/// path-hostile blob key; the raw fields live inside the persisted record.
module ToolUp.Platform.ColumnMappingStore

open System.Text
open System.Security.Cryptography
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ColumnMappingTypes

[<Literal>]
let private RecipePrefix = "_conversion__"

[<Literal>]
let private RecordPrefix = "_conversionrecord__"

/// Internal data-type tag for conversion recipes.
[<Literal>]
let RecipeDataType = "_conversion"

/// Internal data-type tag for per-object provenance records.
[<Literal>]
let RecordDataType = "_conversionrecord"

let private jsonOptions = FableConverters.create ()

let private sha (material: string) : string =
    use h = SHA256.Create()

    h.ComputeHash(Encoding.UTF8.GetBytes material)
    |> Array.map _.ToString("x2")
    |> String.concat ""

/// Object id for a recipe (fingerprint, target type) pair.
let objectIdFor (fingerprint: string) (targetTypeId: string) : string =
    // space-separate the two parts so distinct pairs never collide.
    RecipePrefix + sha (fingerprint + " " + targetTypeId)

/// Object id for a provenance record (keyed by produced file name).
let recordObjectIdFor (producedFile: string) : string = RecordPrefix + sha producedFile

/// Warn through the threaded structured logger, falling back to stderr
/// only when none was resolved (test harnesses that bypass compose).
let private warn (logger: ILogger option) (msg: string) =
    match logger with
    | Some l -> l.Warn msg
    | None -> eprintfn "%s" msg

let inline private deserialise<'T> (logger: ILogger option) (bytes: byte[]) : 'T option =
    try
        Some(JsonSerializer.Deserialize<'T>(Encoding.UTF8.GetString bytes, jsonOptions))
    with ex ->
        // A recipe/record that fails to deserialise silently stops applying —
        // surface it so a saved conversion that mysteriously stopped working
        // is diagnosable.
        warn
            logger
            (sprintf "ColumnMappingStore: a %s sidecar could not be deserialised; skipping it. %O" (typeof<'T>.Name) ex)

        None

/// Read + deserialise every sidecar of one data-type tag in a scope.
let private readAllOf<'T>
    (store: IDataObjectStore)
    (logger: ILogger option)
    (tag: string)
    (scopeId: string)
    : Async<'T list> =
    async {
        let! objects = store.ListObjects scopeId

        let! results =
            objects
            |> List.filter (fun o -> o.DataType = tag)
            |> List.map (fun o -> async {
                match! store.Get(scopeId, o.ObjectId) with
                | Ok(_, bytes) -> return deserialise<'T> logger bytes
                | Error e ->
                    warn logger (sprintf "ColumnMappingStore: a '%s' sidecar could not be read; skipping it. %A" tag e)
                    return None
            })
            // Bounded parallel, not serial: `GetByFingerprint` runs on every CSV
            // upload (to find a reusable recipe), so N serial blob round-trips on a
            // remote store would be on the upload hot path. 16 matches the
            // startup-hydration fan-out convention.
            |> fun xs -> Async.Parallel(xs, 16)

        return results |> Array.toList |> List.choose id
    }

let private saveObjectIn (store: IDataObjectStore) scopeId objectId tag createdBy value = async {
    try
        let bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, jsonOptions))

        match! store.Save(scopeId, objectId, bytes, tag, createdBy, Map.empty, Unversioned) with
        | Ok _ -> return Ok()
        | Error e -> return Error(string e)
    with ex ->
        return Error ex.Message
}

/// Create the default store over an `IDataObjectStore`. `logger` routes
/// the deserialise / read-failure warnings to the structured log; `None`
/// (test harnesses) falls back to stderr.
/// Coerce a deserialised recipe's `Derived` field from `null` to `[]`. A
/// pre-Phase-219 sidecar lacks the field, which the record deserialiser
/// fills with `null` (F# `[]` is a real object, not null); normalising here
/// keeps every recipe handed to the handler / sent over the wire safe to
/// iterate, so an old recipe re-imports as "no derived columns".
let private normaliseConversion (c: Conversion) : Conversion =
    if isNull (box c.Derived) then
        { c with Derived = [] }
    else
        c

let create (store: IDataObjectStore) (logger: ILogger option) : IConversionStore =
    { new IConversionStore with
        member _.Save(scopeId, conversion) =
            saveObjectIn
                store
                scopeId
                (objectIdFor conversion.Fingerprint conversion.TargetTypeId)
                RecipeDataType
                conversion.CreatedBy
                conversion

        member _.GetByFingerprint(scopeId, fingerprint) = async {
            let! all = readAllOf<Conversion> store logger RecipeDataType scopeId

            return
                all
                |> List.filter (fun c -> c.Fingerprint = fingerprint)
                |> List.map normaliseConversion
        }

        member _.List(scopeId) = async {
            let! all = readAllOf<Conversion> store logger RecipeDataType scopeId
            return all |> List.map normaliseConversion
        }

        member _.Delete(scopeId, fingerprint, targetTypeId) = async {
            match! store.Delete(scopeId, objectIdFor fingerprint targetTypeId) with
            | Ok _ -> return Ok()
            | Error NotFound -> return Ok() // idempotent
            | Error e -> return Error(string e)
        }

        member _.SaveRecord(scopeId, record) =
            saveObjectIn store scopeId (recordObjectIdFor record.ProducedFile) RecordDataType record.ConvertedBy record

        member _.ListRecords(scopeId) =
            readAllOf<ConversionRecord> store logger RecordDataType scopeId
    }