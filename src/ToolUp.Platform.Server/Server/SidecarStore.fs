// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── SidecarStore — records over IDataObjectStore, read by an index ──
//
// A store that keeps records as data objects and mirrors each record's
// queryable coordinates into the object's metadata sidecar. Reading by a
// coordinate used to mean `ListObjects` — one metadata download for EVERY
// object in the scope, of every type — and then a content download per
// match: a read cost that grows with the scope, not with the answer.
//
// `SidecarStore` keeps an index for the sidecar keys it is constructed
// with, so `QueryBy key value` reads the index and then one object per
// match. A pattern over `IDataObjectStore`, not a new storage contract:
// the index is itself data objects in the same scope.
//
// **Layout.** Per `(dataType, key, value)` a BUCKET object holds the ids of
// the records filed under that value; per `dataType` a MANIFEST object
// names the keys whose buckets are complete. Both ids are taken over the
// canonical frame, so no value can collide with another. Buckets are per
// value rather than one object per key because the store versions every
// write: a per-key object would rewrite the whole key's index on every
// save, which grows with the square of the record count. A bucket's write
// grows with the records sharing its value — so declare HIGH-CARDINALITY
// coordinates (an artifact hash, a run id), and read a low-cardinality one
// (a status, a verdict) with `Where`, whose matches are most of the scope
// anyway.
//
// **The head is authoritative; the index only narrows.** Every id a bucket
// lists is re-read and kept only when the record's latest version still
// carries the value, so a stale entry (a failed save, a coordinate that
// changed) is never an answer. A MISSING entry would be a silent miss, so
// three rules keep the buckets of a manifest-listed key complete:
//
//   * a save of a record adds it to its buckets BEFORE the record is
//     written (a failed index write fails the save with nothing persisted);
//   * a bucket is written only through a version claim
//     (`ConditionalDataObjectStore.saveIfVersion`), so two writers cannot
//     lose each other's entries — and a store that cannot claim atomically
//     is never indexed at all: every read scans, exactly as before;
//   * a key is listed in the manifest only by a backfill, which lists the
//     scope, fills the buckets, lists the key, and lists the scope AGAIN —
//     a save that completed before the manifest named the key is in the
//     second listing, and one that saw the key named added itself.
//
// **Every write of the type goes through `Save`.** A record written to the
// data type behind the store's back — by another component, or by an
// earlier version during a rolling upgrade — is not filed, and a read by a
// complete key would miss it. Evicting the type's manifest
// (`SidecarStore.manifestObjectId`) makes the next read rebuild every key.
//
// **Backfill on first read.** A key the manifest does not list is
// backfilled by the first `QueryBy` on it — one-time, two listings, and
// that read is answered from the listing. A deployment that never reads
// by a key pays nothing for it (GP 13).
//
// **A corrupt index is rebuilt, never trusted.** An unreadable bucket is
// rebuilt from a listing; an unreadable manifest names no key, so every
// key is backfilled again. Any other index failure answers the read by
// scanning, which is what the read cost before there was an index.
//
// Stateless between calls (GP 12 rule 4): nothing is cached; the index is
// read from the store on every call.

/// What a `SidecarStore` stores and which sidecar keys it indexes.
type SidecarStoreSpec<'T> = {
    /// The data type every record is saved under. The store reads only
    /// objects of this type.
    DataType: string
    /// The versioning policy every record is saved under.
    Policy: VersioningPolicy
    /// The sidecar keys `QueryBy` reads through an index. Declare immutable,
    /// high-cardinality coordinates; read anything else with `Where`.
    Keys: string list
    /// Decode a record from its latest version's metadata and content. An
    /// `Error` record is skipped by every multi-record read.
    Decode: DataObject -> byte[] -> Result<'T, string>
}

/// How one index object read.
[<RequireQualifiedAccess>]
type internal SidecarIndexRead =
    | Absent
    | Present of version: int * entries: string list
    | Corrupt of version: int

[<RequireQualifiedAccess>]
module SidecarStore =

    /// The data type the index objects are saved under.
    [<Literal>]
    let IndexDataType = "toolup.sidecarindex"

    /// The actor recorded on index writes.
    [<Literal>]
    let IndexActor = "_platform.sidecarstore"

    /// Version claims a single index write attempts before it gives up.
    [<Literal>]
    let MaxClaimAttempts = 8

    let private options = FableConverters.create ()

    /// The SDK-standard JSON encoding of a record (`FableConverters`).
    let encode (record: 'T) : byte[] =
        JsonSerializer.SerializeToUtf8Bytes(record, options)

    /// Decode a record written by `encode`. `Error` carries the detail.
    let decode<'T> (bytes: byte[]) : Result<'T, string> =
        try
            match JsonSerializer.Deserialize<'T>(bytes, options) with
            | value when isNull (box value) -> Error "the record is null"
            | value -> Ok value
        with ex ->
            Error ex.Message

    /// A spec whose records are the `encode` JSON of `'T`.
    let json<'T> (dataType: string) (policy: VersioningPolicy) (keys: string list) : SidecarStoreSpec<'T> = {
        DataType = dataType
        Policy = policy
        Keys = keys
        Decode = fun _ bytes -> decode<'T> bytes
    }

    /// The object id of the manifest naming `dataType`'s complete keys.
    let manifestObjectId (dataType: string) : string =
        "_sidecar." + CanonicalFrame.digest [ "sidecar-manifest"; dataType ]

    /// The object id of the bucket filing `dataType` records under
    /// `key = value`.
    let bucketObjectId (dataType: string) (key: string) (value: string) : string =
        "_sidecar." + CanonicalFrame.digest [ "sidecar-bucket"; dataType; key; value ]

/// Records of one data type over `IDataObjectStore`, read by declared,
/// indexed sidecar keys. Construct with a `SidecarStoreSpec`.
type SidecarStore<'T>(dataObjects: IDataObjectStore, spec: SidecarStoreSpec<'T>) =

    let indexed =
        not (List.isEmpty spec.Keys) && ConditionalDataObjectStore.isAtomic dataObjects

    let manifestId = SidecarStore.manifestObjectId spec.DataType

    let bucketId (key: string) (value: string) =
        SidecarStore.bucketObjectId spec.DataType key value

    let readIndex (scopeId: string) (objectId: string) : Async<Result<SidecarIndexRead, DataObjectError>> = async {
        match! dataObjects.Get(scopeId, objectId) with
        | Error DataObjectError.NotFound -> return Ok SidecarIndexRead.Absent
        | Error e -> return Error e
        | Ok(dobj, bytes) ->
            match SidecarStore.decode<string list> bytes with
            | Ok entries when dobj.DataType = SidecarStore.IndexDataType ->
                return Ok(SidecarIndexRead.Present(dobj.Version, entries))
            | _ -> return Ok(SidecarIndexRead.Corrupt dobj.Version)
    }

    /// Rewrite one index object through a version claim. `change` reads
    /// the current state and returns the entries to write, or `None` to
    /// leave it as it is; a lost claim re-reads and asks again.
    let mutate
        (scopeId: string)
        (objectId: string)
        (metadata: Map<string, string>)
        (change: SidecarIndexRead -> string list option)
        : Async<Result<unit, DataObjectError>> =
        let rec attempt (n: int) = async {
            match! readIndex scopeId objectId with
            | Error e -> return Error e
            | Ok current ->
                match change current with
                | None -> return Ok()
                | Some entries ->
                    let expected =
                        match current with
                        | SidecarIndexRead.Absent -> 0
                        | SidecarIndexRead.Present(version, _)
                        | SidecarIndexRead.Corrupt version -> version

                    let content = SidecarStore.encode (entries |> List.distinct |> List.sort)

                    match!
                        ConditionalDataObjectStore.saveIfVersion
                            dataObjects
                            scopeId
                            objectId
                            content
                            SidecarStore.IndexDataType
                            SidecarStore.IndexActor
                            metadata
                            Versioned
                            expected
                    with
                    | Ok _ -> return Ok()
                    | Error(ConditionalSaveError.SaveFailed e) -> return Error e
                    | Error(ConditionalSaveError.VersionConflict _) when n < SidecarStore.MaxClaimAttempts ->
                        return! attempt (n + 1)
                    | Error(ConditionalSaveError.VersionConflict(expected, actual)) ->
                        return
                            Error(
                                DataObjectError.StorageFailure
                                    $"sidecar index '{objectId}' lost {SidecarStore.MaxClaimAttempts} version claims (last expected v{expected}, found v{actual})"
                            )
        }

        attempt 1

    let manifestMetadata = Map [ "sidecar.dataType", spec.DataType ]

    let bucketMetadata (key: string) =
        Map [ "sidecar.dataType", spec.DataType; "sidecar.key", key ]

    /// The keys the manifest lists as complete. An absent or corrupt
    /// manifest lists none.
    let completeKeys (scopeId: string) : Async<Result<string list, DataObjectError>> = async {
        match! readIndex scopeId manifestId with
        | Error e -> return Error e
        | Ok(SidecarIndexRead.Present(_, keys)) -> return Ok keys
        | Ok _ -> return Ok []
    }

    /// Union `ids` into the bucket. A corrupt bucket is replaced: the caller
    /// holds an authoritative listing of what it should contain.
    let mergeBucket (scopeId: string) (key: string) (value: string) (ids: string list) =
        mutate scopeId (bucketId key value) (bucketMetadata key) (fun current ->
            match current with
            | SidecarIndexRead.Present(_, entries) when ids |> List.forall (fun id -> List.contains id entries) -> None
            | SidecarIndexRead.Present(_, entries) -> Some(entries @ ids)
            | SidecarIndexRead.Absent
            | SidecarIndexRead.Corrupt _ -> Some ids)

    /// Drop `key` from the manifest, so the next read backfills it.
    let invalidate (scopeId: string) (key: string) =
        mutate scopeId manifestId manifestMetadata (fun current ->
            match current with
            | SidecarIndexRead.Present(_, keys) when List.contains key keys -> Some(keys |> List.filter ((<>) key))
            | SidecarIndexRead.Present _
            | SidecarIndexRead.Absent -> None
            | SidecarIndexRead.Corrupt _ -> Some [])

    /// File one record under `key = value` ahead of its write. A corrupt
    /// bucket cannot be trusted to hold the others, so the key is dropped
    /// from the manifest and rebuilt by the next read.
    let addEntry (scopeId: string) (key: string) (value: string) (objectId: string) = async {
        match! readIndex scopeId (bucketId key value) with
        | Ok(SidecarIndexRead.Corrupt _) -> return! invalidate scopeId key
        | _ -> return! mergeBucket scopeId key value [ objectId ]
    }

    /// The latest version of every record of the store's type.
    let listOfType (scopeId: string) = async {
        let! all = dataObjects.ListObjects scopeId
        return all |> List.filter (fun d -> d.DataType = spec.DataType)
    }

    /// Materialise the listed records — one content download each.
    let materialise (scopeId: string) (listed: DataObject list) = async {
        let results = ResizeArray<'T>()

        for dobj in listed do
            match! dataObjects.GetContent(scopeId, dobj.ContentHash) with
            | Ok bytes ->
                match spec.Decode dobj bytes with
                | Ok record -> results.Add record
                | Error _ -> ()
            | Error _ -> ()

        return List.ofSeq results
    }

    let hasValue (key: string) (value: string) (dobj: DataObject) =
        Map.tryFind key dobj.Metadata = Some value

    /// Fill every bucket of `key` from a listing, name the key in the
    /// manifest, and list again to catch a save the first listing missed.
    /// Returns the second listing, from which the triggering read is
    /// answered. Idempotent: a second backfill writes nothing.
    let backfill (scopeId: string) (key: string) : Async<Result<DataObject list, DataObjectError>> =
        let fill (listed: DataObject list) =
            let groups =
                listed
                |> List.choose (fun d -> Map.tryFind key d.Metadata |> Option.map (fun v -> v, d.ObjectId))
                |> List.groupBy fst
                |> List.map (fun (value, pairs) -> value, pairs |> List.map snd)

            let rec loop remaining = async {
                match remaining with
                | [] -> return Ok()
                | (value, ids) :: rest ->
                    match! mergeBucket scopeId key value ids with
                    | Error e -> return Error e
                    | Ok() -> return! loop rest
            }

            loop groups

        async {
            let! first = listOfType scopeId

            match! fill first with
            | Error e -> return Error e
            | Ok() ->
                let! named =
                    mutate scopeId manifestId manifestMetadata (fun current ->
                        match current with
                        | SidecarIndexRead.Present(_, keys) when List.contains key keys -> None
                        | SidecarIndexRead.Present(_, keys) -> Some(key :: keys)
                        | SidecarIndexRead.Absent
                        | SidecarIndexRead.Corrupt _ -> Some [ key ])

                match named with
                | Error e -> return Error e
                | Ok() ->
                    let! second = listOfType scopeId

                    let seen =
                        first
                        |> List.map (fun d -> d.ObjectId, Map.tryFind key d.Metadata)
                        |> Set.ofList

                    let late =
                        second
                        |> List.filter (fun d -> not (Set.contains (d.ObjectId, Map.tryFind key d.Metadata) seen))

                    match! fill late with
                    | Error e -> return Error e
                    | Ok() -> return Ok second
        }

    /// Every record whose latest sidecar satisfies `sidecarMatches` — one
    /// listing of the scope, then one content download per match.
    member _.Where(scopeId: string, sidecarMatches: Map<string, string> -> bool) : Async<'T list> = async {
        let! listed = listOfType scopeId
        return! materialise scopeId (listed |> List.filter (fun d -> sidecarMatches d.Metadata))
    }

    /// Every record of the store's type in the scope — one listing, then one
    /// content download per record.
    member this.Sweep(scopeId: string) : Async<'T list> = this.Where(scopeId, fun _ -> true)

    /// Every record whose latest sidecar carries `key = value`. For a
    /// declared key over a store that claims versions atomically: the
    /// manifest and the bucket, then one object read per filed id. Any
    /// other key, any other store, and any index failure read by scanning.
    member this.QueryBy(scopeId: string, key: string, value: string) : Async<'T list> = async {
        if not indexed || not (List.contains key spec.Keys) then
            return! this.Where(scopeId, (fun m -> Map.tryFind key m = Some value))
        else
            let! complete = completeKeys scopeId

            let! bucket =
                match complete with
                | Ok keys when List.contains key keys -> readIndex scopeId (bucketId key value)
                | _ -> async { return Ok SidecarIndexRead.Absent }

            match complete, bucket with
            | Ok keys, Ok SidecarIndexRead.Absent when List.contains key keys -> return []
            | Ok keys, Ok(SidecarIndexRead.Present(_, ids)) when List.contains key keys ->
                let results = ResizeArray<'T>()

                for id in ids do
                    match! dataObjects.Get(scopeId, id) with
                    | Ok(dobj, bytes) when dobj.DataType = spec.DataType && hasValue key value dobj ->
                        match spec.Decode dobj bytes with
                        | Ok record -> results.Add record
                        | Error _ -> ()
                    | _ -> ()

                return List.ofSeq results
            | _ ->
                // The key is not yet complete, or its bucket is corrupt or
                // unreadable: rebuild from a listing and answer from it.
                match! backfill scopeId key with
                | Ok listed -> return! materialise scopeId (listed |> List.filter (hasValue key value))
                | Error _ -> return! this.Where(scopeId, (fun m -> Map.tryFind key m = Some value))
    }

    /// The latest version of one record. A record that does not decode is a
    /// `StorageFailure` naming why.
    member _.Get(scopeId: string, objectId: string) : Async<Result<'T, DataObjectError>> = async {
        match! dataObjects.Get(scopeId, objectId) with
        | Error e -> return Error e
        | Ok(dobj, bytes) ->
            return
                spec.Decode dobj bytes
                |> Result.mapError (fun detail ->
                    DataObjectError.StorageFailure $"stored record is unreadable: {detail}")
    }

    /// Save a record under `objectId`, filing it in the bucket of every
    /// complete key its sidecar carries before the record is written. The
    /// returned `DataObject` is the version the store assigned.
    member _.Save
        (scopeId: string, objectId: string, content: byte[], createdBy: string, sidecar: Map<string, string>)
        : Async<Result<DataObject, DataObjectError>> =
        let write () =
            dataObjects.Save(scopeId, objectId, content, spec.DataType, createdBy, sidecar, spec.Policy)

        let fileUnder (keys: string list) =
            let rec loop remaining = async {
                match remaining with
                | [] -> return Ok()
                | key :: rest ->
                    match Map.tryFind key sidecar with
                    | None -> return! loop rest
                    | Some value ->
                        match! addEntry scopeId key value objectId with
                        | Error e -> return Error(key, e)
                        | Ok() -> return! loop rest
            }

            loop keys

        async {
            if not indexed then
                return! write ()
            else
                match! completeKeys scopeId with
                | Error e -> return Error e
                | Ok before ->
                    let ahead = spec.Keys |> List.filter (fun k -> List.contains k before)

                    match! fileUnder ahead with
                    | Error(_, e) -> return Error e
                    | Ok() ->
                        match! write () with
                        | Error e -> return Error e
                        | Ok saved when List.length ahead = List.length spec.Keys -> return Ok saved
                        | Ok saved ->
                            // A backfill may have named a key while this save
                            // ran; a save it did not list files itself.
                            let! after = completeKeys scopeId

                            let late =
                                match after with
                                | Ok keys ->
                                    spec.Keys
                                    |> List.filter (fun k -> List.contains k keys && not (List.contains k ahead))
                                | Error _ -> spec.Keys |> List.filter (fun k -> not (List.contains k ahead))

                            match! fileUnder late with
                            | Ok() -> return Ok saved
                            | Error(key, _) ->
                                match! invalidate scopeId key with
                                | Ok() -> return Ok saved
                                | Error e ->
                                    return
                                        Error(
                                            DataObjectError.StorageFailure
                                                $"record '{objectId}' was saved but its '{key}' index could neither be written nor marked for rebuild: %A{e}"
                                        )
        }