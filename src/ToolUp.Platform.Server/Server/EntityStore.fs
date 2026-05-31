module ToolUp.Platform.EntityStore

open System
open System.Collections.Concurrent
open System.Text
open Microsoft.FSharp.Reflection
open Newtonsoft.Json
open ToolUp.Remoting.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.SecondaryIndex
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Platform.IEntityStore

// ─── Phase 19 — BlobEntityStore default implementation ─────────────
//
// Default `IEntityStore` over `IDataObjectStore` (Phase 7) and
// `BlobIndex` (Phase 9f). Scope-isolated by construction — every
// method takes `scopeId` and the implementation derives all blob
// paths from it.
//
// Blob layout:
//
//   {scopeId-container}/objects/_entity:{type}:{id}/v{N}.json    ← versioned (via IDataObjectStore)
//   {scopeId-container}/entities/_indexes/{type}/{index}/{value}/{entityId}.ref
//
// `_entity:{type}:{id}` is the IDataObjectStore ObjectId; `_entity:{type}`
// is the dataType field (lets `IDataObjectStore.ListObjects` filter
// by entity type cheaply for `Count` / `ListAll` / index-rebuild).
//
// Versioning: `IDataObjectStore.Save(... policy = Versioned)` — every
// save bumps the version; previous versions remain available for
// audit / rollback. The store overwrites the entity record's
// `Version` field with the assigned version before serialisation so
// the in-blob copy matches the metadata.
//
// Indexes: per `(entityType, indexName)`, a `BlobIndex<string, EntityId>`
// is constructed lazily on first use and cached in a
// `ConcurrentDictionary` for the store's lifetime. Save updates every
// declared index for the entity type; Delete drops every entry.

let private auditJsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

let private serialise (value: 'T) =
    JsonConvert.SerializeObject(value, auditJsonSettings)

let private deserialise<'T> (json: string) : 'T =
    JsonConvert.DeserializeObject<'T>(json, auditJsonSettings)

[<Literal>]
let private EntityObjectIdPrefix = "_entity__"

[<Literal>]
let private EntityObjectIdSeparator = "__"

[<Literal>]
let private EntityIndexPrefix = "entities/_indexes"

/// `IDataObjectStore.parseObjectId` reads up to the first `/` — so the
/// objectId can't contain a slash. Colons are illegal in Windows
/// filenames. Double-underscore is the safest cross-platform separator
/// (matches the existing IResultStore convention `{moduleName}__{resultType}`).
/// Format: `_entity__{EntityType}__{EntityId}`.
///
/// Constraint: `EntityId` must not contain `__` (would confuse the
/// reverse parse). User-supplied IDs typically don't; documented.
let private objectIdFor (entityType: string) (entityId: EntityId) : string =
    sprintf "%s%s%s%s" EntityObjectIdPrefix entityType EntityObjectIdSeparator entityId

/// `dataType` is stored in a JSON metadata field, not a path, so
/// colons here are fine and help the type stand out in audit /
/// `/dev/inspect` dumps.
let private dataTypeFor (entityType: string) : string = sprintf "_entity:%s" entityType

let private indexPrefixFor (entityType: string) (indexName: string) : string =
    sprintf "%s/%s/%s" EntityIndexPrefix entityType indexName

/// Replace the `Version` field on a record. Reflection-based —
/// returns a new instance because F# records are immutable.
let private withVersion<'T> (entity: 'T) (newVersion: int) : 'T =
    let t = typeof<'T>

    if not (FSharpType.IsRecord t) then
        // Should never happen; tryGetEntityFields validates this
        // first. Fail loud if it does.
        failwith "withVersion: not a record"

    let fields = FSharpType.GetRecordFields t
    let values = FSharpValue.GetRecordFields entity

    let updatedValues =
        Array.zip fields values
        |> Array.map (fun (f, v) -> if f.Name = "Version" then box newVersion else v)

    FSharpValue.MakeRecord(t, updatedValues) :?> 'T

/// Heterogeneous registry of entity types. Erases `EntityRegistration<'T>`
/// to `obj` for storage, casts back when consumers ask. Singleton
/// across the deployment — registrations are read-only after compose
/// finishes, so the dictionary doesn't need locking on the read path.
type EntityRegistry() =
    let registrations = ConcurrentDictionary<string, obj>()

    member _.Register<'T>(reg: EntityRegistration<'T>) : unit =
        registrations[reg.EntityType] <- box reg

    member _.TryGet<'T>(entityType: string) : EntityRegistration<'T> option =
        match registrations.TryGetValue entityType with
        | true, boxed ->
            try
                Some(unbox<EntityRegistration<'T>> boxed)
            with _ ->
                None
        | false, _ -> None

    member _.Knows(entityType: string) : bool = registrations.ContainsKey entityType

    member _.AllRegisteredTypes() : string list = registrations.Keys |> List.ofSeq

/// Build an `EntityRef<'T>` from the persisted DataObject metadata.
let private refFromDataObject<'T> (dataObject: DataObject) (entityId: EntityId) (entityType: string) : EntityRef<'T> = {
    Id = entityId
    Type = entityType
    Version = dataObject.Version
}

/// Build the per-entity-type index map. Each call returns a closure
/// over the supplied `BlobIndex` instances; no per-Save allocation.
type private IndexHandle<'T> = {
    Index: BlobIndex<string, EntityId>
    Definition: EntityIndex<'T>
}

type BlobEntityStore
    (dataObjectStore: IDataObjectStore, blobStorage: IBlobStorage, registry: EntityRegistry, auditLog: IAuditLog option)
    =
    // Per-(scopeId, entityType, indexName) BlobIndex cache. Keyed by
    // tuple-as-string for ConcurrentDictionary's IEquatable<TKey>
    // requirement.
    let indexCache = ConcurrentDictionary<string, BlobIndex<string, EntityId>>()

    let getIndex (scopeId: string) (entityType: string) (indexName: string) : BlobIndex<string, EntityId> =
        let cacheKey = sprintf "%s|%s|%s" scopeId entityType indexName

        indexCache.GetOrAdd(
            cacheKey,
            fun _ ->
                BlobIndex.create
                    blobStorage
                    scopeId
                    (indexPrefixFor entityType indexName)
                    id // keyToSegment: the key IS the segment
                    id // valueToSegment: EntityId IS the segment
                    Some
        )

    let entityIdFromObjectId (entityType: string) (objectId: string) : EntityId option =
        let prefix =
            sprintf "%s%s%s" EntityObjectIdPrefix entityType EntityObjectIdSeparator

        if objectId.StartsWith prefix then
            Some(objectId.Substring prefix.Length)
        else
            None

    interface IEntityStore with

        member _.Save<'T>(scopeId: string, entity: 'T) : Async<Result<EntityRef<'T>, EntityError>> = async {
            // Step 1: extract required fields, validate shape.
            match tryGetEntityFields entity with
            | Error msg -> return Error(InvalidEntityShape msg)
            | Ok core ->
                // Step 2: registration lookup — the entity type must
                // be registered, and its `Type` field must match.
                match registry.TryGet<'T>(core.Type) with
                | None -> return Error(EntityError.UnknownEntityType core.Type)
                | Some reg ->
                    if reg.EntityType <> core.Type then
                        return
                            Error(
                                InvalidEntityShape(
                                    sprintf
                                        "Entity Type field '%s' doesn't match registered type '%s'"
                                        core.Type
                                        reg.EntityType
                                )
                            )
                    else
                        // Step 3: determine the next version. Read
                        // ListVersions; if empty, version = 1; else
                        // version = max + 1.
                        let objectId = objectIdFor core.Type core.Id
                        let! existingVersions = dataObjectStore.ListVersions(scopeId, objectId)

                        let newVersion =
                            if existingVersions.IsEmpty then
                                1
                            else
                                (existingVersions |> List.map _.Version |> List.max) + 1

                        // Step 4: replace the entity's Version with
                        // the assigned version, then serialise.
                        let entityWithVersion = withVersion entity newVersion
                        let json = serialise entityWithVersion
                        let bytes = Encoding.UTF8.GetBytes json

                        // Step 5: persist via IDataObjectStore.
                        let! saveResult =
                            dataObjectStore.Save(
                                scopeId,
                                objectId,
                                bytes,
                                dataTypeFor core.Type,
                                // `IEntityStore.Save` doesn't carry caller identity — actor-level
                                // attribution lives one layer up at the API handler, where
                                // `AccessContext.UserId` is available. "system" matches the existing
                                // `IDataObjectStore` convention for non-end-user writes.
                                "system",
                                Map.empty,
                                Versioned
                            )

                        match saveResult with
                        | Error doErr ->
                            return Error(StorageFailure(sprintf "IDataObjectStore.Save failed: %s" (string doErr)))
                        | Ok savedObject ->
                            // Step 6: maintain indexes. For each
                            // declared index, compute the new key,
                            // compare against the previous version's
                            // key (if any), and update.
                            let! _ = async {
                                for index in reg.Indexes do
                                    let newKey = index.Extract entityWithVersion
                                    let blobIndex = getIndex scopeId core.Type index.Name

                                    // If a previous version existed,
                                    // we need to remove its index
                                    // entry under the OLD key (when
                                    // the indexed value changed).
                                    if not existingVersions.IsEmpty then
                                        // Read the previous version
                                        // to compute its index key.
                                        let prevVersion = existingVersions |> List.map _.Version |> List.max

                                        let! prevResult = dataObjectStore.GetVersion(scopeId, objectId, prevVersion)

                                        match prevResult with
                                        | Ok(_, prevBytes) ->
                                            let prevJson = Encoding.UTF8.GetString prevBytes
                                            let prevEntity = deserialise<'T> prevJson
                                            let prevKey = index.Extract prevEntity

                                            if prevKey <> newKey then
                                                let! _ = blobIndex.Remove prevKey core.Id
                                                ()
                                        | Error _ -> () // best-effort; if we can't read prev, just add the new one

                                    let! _ = blobIndex.Add newKey core.Id None
                                    ()
                            }

                            // Emit audit event. Best-effort — exceptions
                            // swallowed so audit emission never fails the
                            // primary write.
                            match auditLog with
                            | Some log ->
                                try
                                    let payload = {
                                        UserId = "system"
                                        EntityType = core.Type
                                        EntityId = core.Id
                                        Version = newVersion
                                    }

                                    let evt =
                                        if newVersion = 1 then
                                            EntityCreated payload
                                        else
                                            EntityUpdated payload

                                    do! log.Record(scopeId, evt)
                                with _ ->
                                    ()
                            | None -> ()

                            return Ok(refFromDataObject<'T> savedObject core.Id core.Type)
        }

        member _.Get<'T>(scopeId: string, entityType: string, entityId: EntityId) = async {
            if not (registry.Knows entityType) then
                return Error(EntityError.UnknownEntityType entityType)
            else
                let objectId = objectIdFor entityType entityId
                let! result = dataObjectStore.Get(scopeId, objectId)

                match result with
                | Error DataObjectError.NotFound -> return Error(EntityError.NotFound(entityType, entityId))
                | Error err -> return Error(StorageFailure(sprintf "IDataObjectStore.Get failed: %s" (string err)))
                | Ok(_, bytes) ->
                    let json = Encoding.UTF8.GetString bytes
                    return Ok(deserialise<'T> json)
        }

        member _.GetVersion<'T>(scopeId: string, entityType: string, entityId: EntityId, version: int) = async {
            if not (registry.Knows entityType) then
                return Error(EntityError.UnknownEntityType entityType)
            else
                let objectId = objectIdFor entityType entityId
                let! result = dataObjectStore.GetVersion(scopeId, objectId, version)

                match result with
                | Error DataObjectError.NotFound -> return Error(EntityError.NotFound(entityType, entityId))
                | Error(VersionNotFound _) -> return Error(EntityError.NotFound(entityType, entityId))
                | Error err ->
                    return Error(StorageFailure(sprintf "IDataObjectStore.GetVersion failed: %s" (string err)))
                | Ok(_, bytes) ->
                    let json = Encoding.UTF8.GetString bytes
                    return Ok(deserialise<'T> json)
        }

        member _.ListVersions<'T>(scopeId: string, entityType: string, entityId: EntityId) = async {
            if not (registry.Knows entityType) then
                return []
            else
                let objectId = objectIdFor entityType entityId
                let! versions = dataObjectStore.ListVersions(scopeId, objectId)

                return
                    versions
                    |> List.map (fun v ->
                        ({
                            Id = entityId
                            Type = entityType
                            Version = v.Version
                        }
                        : EntityRef<'T>))
        }

        member _.Delete(scopeId: string, entityType: string, entityId: EntityId) = async {
            // v1 simplification: delete the entity blob; rely on
            // Phase 9f's index drift contract — soft-miss-on-read
            // gracefully handles stale index entries that point at a
            // deleted entity. A future commit can introduce a
            // typed-Delete that walks the registered Extract
            // closures via the typed registration to clean up
            // indexes proactively, but that requires preserving 'T
            // at the Delete call site (reflection-recovered Extract
            // can't be unboxed without the original 'T parameter).
            if not (registry.Knows entityType) then
                return Error(EntityError.UnknownEntityType entityType)
            else
                let objectId = objectIdFor entityType entityId
                // Capture head version before delete so the audit
                // event records what got removed. Best-effort.
                let! versionsBefore = dataObjectStore.ListVersions(scopeId, objectId)

                let headVersion =
                    if versionsBefore.IsEmpty then
                        0
                    else
                        versionsBefore |> List.map _.Version |> List.max

                let! deleteResult = dataObjectStore.Delete(scopeId, objectId)

                match deleteResult with
                | Ok() ->
                    if headVersion > 0 then
                        match auditLog with
                        | Some log ->
                            try
                                do!
                                    log.Record(
                                        scopeId,
                                        EntityDeleted {
                                            UserId = "system"
                                            EntityType = entityType
                                            EntityId = entityId
                                            Version = headVersion
                                        }
                                    )
                            with _ ->
                                ()
                        | None -> ()

                    return Ok()
                | Error DataObjectError.NotFound -> return Ok() // idempotent
                | Error err -> return Error(StorageFailure(sprintf "IDataObjectStore.Delete failed: %s" (string err)))
        }

        member _.FindByIndex<'T>(scopeId: string, entityType: string, indexName: string, value: string) = async {
            match registry.TryGet<'T>(entityType) with
            | None -> return Error(EntityError.UnknownEntityType entityType)
            | Some reg ->
                match EntityRegistration.tryFindIndex indexName reg with
                | None -> return Error(InvalidIndex indexName)
                | Some _ ->
                    let blobIndex = getIndex scopeId entityType indexName
                    let! lookupResults = blobIndex.Lookup value

                    // BlobIndex.Lookup returns (EntityId * byte[] option) list.
                    // We don't store payload alongside the index entries (yet)
                    // — strip the payload and resolve each match's head
                    // version via ListVersions in parallel. Soft-miss on
                    // stale index entries (Phase 9f drift contract).
                    let! refs =
                        lookupResults
                        |> List.map (fun (entityId, _payload) -> async {
                            let! versions = dataObjectStore.ListVersions(scopeId, objectIdFor entityType entityId)

                            return
                                if versions.IsEmpty then
                                    None
                                else
                                    let head = versions |> List.maxBy _.Version

                                    Some(
                                        {
                                            Id = entityId
                                            Type = entityType
                                            Version = head.Version
                                        }
                                        : EntityRef<'T>
                                    )
                        })
                        |> Async.Parallel

                    return Ok(refs |> Array.choose id |> Array.toList)
        }

        member _.Count(scopeId: string, entityType: string) = async {
            let! allObjects = dataObjectStore.ListObjects scopeId

            return
                allObjects
                |> List.filter (fun obj -> obj.DataType = dataTypeFor entityType)
                |> List.length
        }

        member _.Query<'T>(scopeId: string, query: EntityQuery<'T>) = async {
            match registry.TryGet<'T>(query.EntityType) with
            | None -> return Error(EntityError.UnknownEntityType query.EntityType)
            | Some reg ->
                // Validate query against the registered indexes. Non-
                // indexed predicate leaves / sort fields fail here.
                // The validate result is annotated so F# doesn't try
                // to unify the IndexValidationError pattern with the
                // surrounding EntityError context.
                let validateResult: Result<EntityQuery<'T>, IndexValidationError> =
                    EntityQuery.validate query reg

                match validateResult with
                | Result.Error(IndexValidationError.NonIndexedField f) -> return Error(InvalidIndex f)
                | Result.Error(IndexValidationError.NonIndexedSortField f) -> return Error(InvalidIndex f)
                | Result.Error(IndexValidationError.UnknownEntityType t) ->
                    return Error(EntityError.UnknownEntityType t)
                | Result.Ok validated ->
                    // Build the executor context. LookupByIndex calls
                    // through to the cached BlobIndex; AllIndexKeys
                    // enumerates segments via IBlobStorage.List.
                    // AllEntityIds / LoadEntity go through
                    // IDataObjectStore.
                    let lookupByIndex (indexName: string) (value: string) = async {
                        let blobIndex = getIndex scopeId query.EntityType indexName
                        let! results = blobIndex.Lookup value
                        return results |> List.map fst
                    }

                    let allIndexKeys (indexName: string) = async {
                        let prefix = indexPrefixFor query.EntityType indexName + "/"
                        let! blobs = blobStorage.List(scopeId, prefix)
                        // Each blob path is `{prefix}/{keySegment}/{valueId}.ref`.
                        // LocalFileStorage on Windows returns backslash-separated
                        // paths via Path.GetRelativePath; normalise to forward
                        // slashes so the prefix match works cross-platform.
                        return
                            blobs
                            |> List.map (fun b -> b.Replace('\\', '/'))
                            |> List.choose (fun b ->
                                if b.StartsWith prefix then
                                    let tail = b.Substring(prefix.Length)
                                    let slash = tail.IndexOf '/'
                                    if slash > 0 then Some(tail.Substring(0, slash)) else None
                                else
                                    None)
                            |> List.distinct
                    }

                    let allEntityIds () = async {
                        let! allObjects = dataObjectStore.ListObjects scopeId

                        return
                            allObjects
                            |> List.filter (fun o -> o.DataType = dataTypeFor query.EntityType)
                            |> List.choose (fun o -> entityIdFromObjectId query.EntityType o.ObjectId)
                    }

                    let loadEntity (entityId: EntityId) = async {
                        let objectId = objectIdFor query.EntityType entityId
                        let! result = dataObjectStore.Get(scopeId, objectId)

                        match result with
                        | Ok(_, bytes) -> return Some(Encoding.UTF8.GetString bytes)
                        | Error _ -> return None
                    }

                    let readFieldString (fieldName: string) (json: string) =
                        try
                            let jObj = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json)
                            let token = jObj[fieldName]
                            if isNull token then None else Some(token.ToString())
                        with _ ->
                            None

                    let ctx: EntityQueryExecutor.ExecutorContext = {
                        LookupByIndex = lookupByIndex
                        AllIndexKeys = allIndexKeys
                        AllEntityIds = allEntityIds
                        LoadEntity = loadEntity
                        ReadFieldString = readFieldString
                    }

                    let! results = EntityQueryExecutor.execute<'T> ctx validated
                    return Ok results
        }

        member _.ListAll<'T>(scopeId: string, entityType: string, skip: int, take: int) = async {
            if not (registry.Knows entityType) then
                return []
            else
                let! allObjects = dataObjectStore.ListObjects scopeId

                let entityObjects =
                    allObjects
                    |> List.filter (fun obj -> obj.DataType = dataTypeFor entityType)
                    |> List.sortBy _.ObjectId
                    |> List.skip (min skip allObjects.Length)
                    |> List.truncate take

                return
                    entityObjects
                    |> List.choose (fun obj ->
                        match entityIdFromObjectId entityType obj.ObjectId with
                        | None -> None
                        | Some id ->
                            Some(
                                {
                                    Id = id
                                    Type = entityType
                                    Version = obj.Version
                                }
                                : EntityRef<'T>
                            ))
        }