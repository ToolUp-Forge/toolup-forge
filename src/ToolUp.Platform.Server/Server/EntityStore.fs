module ToolUp.Platform.EntityStore

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open Microsoft.FSharp.Reflection
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.SecondaryIndex
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.ISparseIndex

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
//
// Phase 973 — an index ref is derived data and the head is authoritative:
// `FindByIndex` re-runs the index's `Extract` over every head a ref
// resolves to and answers only with the heads that still carry the
// looked-up value. A ref whose entity is gone, or whose head carries
// another value (a refused `Remove`, an unreadable previous version), is
// STALE: never an answer, counted in `IndexDriftSnapshot`, and dropped
// on the spot through `BlobIndex.Vacuum`. `VacuumIndex` runs the same
// reclaim over a whole index.
//
// Phase 974 — `Query` answers by the same rule. Its executor context's
// `LookupByIndex` is the lookup above (one head read per listed ref, the
// stale ones counted and reclaimed), so `Eq`/`In` never return, and
// `Ne`/`Not` never exclude, an entity whose head does not carry the value;
// and `AllIndexKeys` hands the range predicates the DECODED keys, so a value
// with a character outside `[A-Za-z0-9_-]` is compared, and looked up, as
// the value it is rather than as its path segment.

let private auditJsonOptions = FableConverters.create ()

let private serialise (value: 'T) =
    JsonSerializer.Serialize(value, auditJsonOptions)

let private deserialise<'T> (json: string) : 'T =
    JsonSerializer.Deserialize<'T>(json, auditJsonOptions)

[<Literal>]
let private EntityObjectIdPrefix = "_entity__"

[<Literal>]
let private EntityObjectIdSeparator = "__"

[<Literal>]
let private EntityIndexPrefix = "entities/_indexes"

/// `DataObjectStore.parseObjectId` reads up to the first `/` — so the
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

/// Phase 19b — synthetic `VectorScope` under which a single injected
/// `ISparseIndex` isolates every `(scopeId, entityType, fieldName)` triple.
/// The whole triple is baked into the scope key, so team isolation is by
/// construction: a full-text hit can never cross scopes, and one BM25
/// instance serves every entity type + field without sharing posting
/// lists. `Team` (not a real team) because Team/User scopes are
/// lazy-loaded — never eagerly read at index construction. The `entity-ft:`
/// prefix keeps these keys clear of any real RAG team scope when the same
/// `ISparseIndex` is shared with document retrieval.
let private fullTextScope (scopeId: string) (entityType: string) (fieldName: string) : VectorScope =
    VectorScope.Team(sprintf "entity-ft:%s:%s:%s" scopeId entityType fieldName)

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
    // Phase 19b — declared full-text field names per entity type, captured
    // at Register time when `'T` is known. The untyped `Delete` path needs
    // the field names (to drop the entity from each field's sparse index)
    // but can't unbox `EntityRegistration<'T>` without `'T`, so the names
    // are projected to plain strings here.
    let fullTextFieldNames = ConcurrentDictionary<string, string list>()
    // Phase 973 — the same projection for the declared secondary indexes:
    // per index, its name and its `Extract` composed with the read of a
    // persisted head, closed over at Register time where `'T` is known. The
    // untyped `Delete` and `VacuumIndex` paths resolve it by type name.
    let indexKeyReaders =
        ConcurrentDictionary<string, (string * (string -> string)) list>()

    member _.Register<'T>(reg: EntityRegistration<'T>) : unit =
        registrations[reg.EntityType] <- box reg
        fullTextFieldNames[reg.EntityType] <- reg.FullTextFields |> List.map fst

        indexKeyReaders[reg.EntityType] <-
            reg.Indexes
            |> List.map (fun index -> index.Name, (fun (json: string) -> index.Extract(deserialise<'T> json)))

    /// Phase 973 — the declared secondary indexes of an entity type, each
    /// paired with the reader of its key from a persisted head's JSON (the
    /// registered `Extract` over the head read as the registered type; it
    /// raises on JSON that does not read as that type). Empty when the type
    /// declares none, or isn't registered.
    member _.IndexKeyReaders(entityType: string) : (string * (string -> string)) list =
        match indexKeyReaders.TryGetValue entityType with
        | true, readers -> readers
        | false, _ -> []

    /// Declared full-text field names for an entity type (empty when the
    /// type declares none, or isn't registered).
    member _.FullTextFieldNames(entityType: string) : string list =
        match fullTextFieldNames.TryGetValue entityType with
        | true, names -> names
        | false, _ -> []

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

/// Phase 973 — what canonical state says about one index ref: the entity
/// `id` listed under `key`.
type private RefState =
    /// The head carries `key`; it answers the lookup at this version.
    | Backed of version: int
    /// The entity is gone, or its head carries another key whose ref path
    /// cannot be this one: never an answer, and safe to reclaim.
    | Stale
    /// No verdict — the head could not be read or could not be keyed, or it
    /// carries a key whose path differs from this one only in case (the
    /// same ref on a case-insensitive file system). Never an answer, and
    /// never reclaimed.
    | Unjudged

type BlobEntityStore
    (
        dataObjectStore: IDataObjectStore,
        blobStorage: IBlobStorage,
        registry: EntityRegistry,
        auditLog: IAuditLog option,
        // Phase 19b — optional BM25 sparse index backing `FullText`
        // predicates. `None` (the default via the 4-arg constructor) leaves
        // the store byte-identical to its pre-19b behaviour: no full-text
        // indexing on Save/Delete and `FullText` predicates resolve to the
        // empty set. A deployment declaring no full-text fields pays zero
        // extra cost whether or not an index is wired (GP 13).
        sparseIndex: ISparseIndex option
    ) =

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
                    // Entity index keys flow from user-supplied
                    // extractors via `EntityRegistration.withIndex` /
                    // `withCompoundIndex` and may carry any character —
                    // including `|` (the compound-index join), `:`
                    // (Author-tag prefixes), spaces, etc. Percent-encode
                    // any char outside [A-Za-z0-9_-] so the path
                    // segment survives Windows NTFS path validation as
                    // well as every cloud blob store. Alphanumeric
                    // keys pay no overhead.
                    BlobIndex.pathSafeSegment
                    // EntityIds are operator-controlled; convention is
                    // GUIDs ("N" format) or DNS-safe slugs — both
                    // path-safe by construction. Pass through verbatim
                    // so `valueParser` round-trips to the canonical id
                    // without a paired decoder.
                    id
                    Some
        )

    // Phase 973 — read-time index drift per `(scopeId, entityType,
    // indexName)`: the refs `FindByIndex` resolved, those whose head carried
    // the looked-up value, and the stale ones. Counted since the store
    // started; read by `IndexDriftSnapshot`.
    let readDrift = ConcurrentDictionary<string * string * string, int * int * int>()

    let recordReadDrift scopeId entityType indexName (seen: int) (consistent: int) (stale: int) =
        if seen > 0 then
            readDrift.AddOrUpdate(
                (scopeId, entityType, indexName),
                (seen, consistent, stale),
                fun _ (s, c, st) -> (s + seen, c + consistent, st + stale)
            )
            |> ignore

    /// Phase 973 — judge the ref `entityId` under `key` against the head:
    /// `keyOf` reads the index's key from the head's JSON (the registered
    /// `Extract` over the head read as the registered type).
    let refStateOf
        (scopeId: string)
        (entityType: string)
        (keyOf: string -> string)
        (key: string)
        (entityId: EntityId)
        : Async<RefState> =
        async {
            let! head = dataObjectStore.Get(scopeId, objectIdFor entityType entityId)

            match head with
            | Error DataObjectError.NotFound -> return Stale
            | Error _ -> return Unjudged
            | Ok(dataObject, bytes) ->
                let carried =
                    try
                        Some(keyOf (Encoding.UTF8.GetString bytes))
                    with _ ->
                        None

                match carried with
                | None -> return Unjudged
                | Some k when k = key -> return Backed dataObject.Version
                | Some k when
                    String.Equals(
                        BlobIndex.pathSafeSegment k,
                        BlobIndex.pathSafeSegment key,
                        StringComparison.OrdinalIgnoreCase
                    )
                    ->
                    return Unjudged
                | Some _ -> return Stale
        }

    /// Phase 973 — reclaim the stale refs under `key` in one index. Only a
    /// ref `candidate` admits is judged (a lookup passes the ones it just
    /// saw stale; a whole-index vacuum admits every ref), and `Vacuum`
    /// re-judges it after the delete, so a concurrent save that re-indexed
    /// it keeps its ref. Returns the number of refs removed.
    let vacuumKey
        (scopeId: string)
        (entityType: string)
        (indexName: string)
        (keyOf: string -> string)
        (key: string)
        (candidate: EntityId -> bool)
        : Async<int> =
        let blobIndex = getIndex scopeId entityType indexName

        blobIndex.Vacuum key (fun entityId -> async {
            if not (candidate entityId) then
                return false
            else
                match! refStateOf scopeId entityType keyOf key entityId with
                | Stale -> return true
                | Backed _
                | Unjudged -> return false
        })

    /// Every key segment present under one index's prefix — the path
    /// segments as written, still `pathSafeSegment`-encoded.
    let indexKeySegments (scopeId: string) (entityType: string) (indexName: string) = async {
        let prefix = indexPrefixFor entityType indexName + "/"
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

    /// Phase 974 — every key present in one index, DECODED: the value each
    /// ref was filed under, not its path segment. A segment is the key
    /// `pathSafeSegment`-encoded, and every character outside its safe set
    /// (`%` included) is escaped, so unescaping recovers the key.
    let indexKeys (scopeId: string) (entityType: string) (indexName: string) = async {
        let! segments = indexKeySegments scopeId entityType indexName
        return segments |> List.map Uri.UnescapeDataString |> List.distinct
    }

    /// Phase 973/974 — the entities listed under `key` in one index whose
    /// head still carries `key`, each with its head version. A ref is
    /// derived; the head is authoritative: each listed entity's head is read
    /// and `keyOf` (the index's own `Extract` over the head) re-run on it. A
    /// ref whose entity is gone, or whose head carries another value (a
    /// refused `Remove`, an unreadable previous version at save), is stale —
    /// never an answer, counted in the drift snapshot, and reclaimed on the
    /// spot, best-effort (a failed reclaim costs the next lookup a re-read,
    /// never this answer). One head read per listed ref, in parallel.
    let backedRefs
        (scopeId: string)
        (entityType: string)
        (indexName: string)
        (keyOf: string -> string)
        (key: string)
        : Async<(EntityId * int) list> =
        async {
            let blobIndex = getIndex scopeId entityType indexName
            let! lookupResults = blobIndex.Lookup key

            // Entity refs carry no payload; it is ignored.
            let! states =
                lookupResults
                |> List.map (fun (entityId, _payload) -> async {
                    let! state = refStateOf scopeId entityType keyOf key entityId
                    return entityId, state
                })
                |> Async.Parallel

            let backed =
                states
                |> Array.choose (fun (entityId, state) ->
                    match state with
                    | Backed version -> Some(entityId, version)
                    | Stale
                    | Unjudged -> None)
                |> Array.toList

            let stale =
                states
                |> Array.choose (fun (entityId, state) ->
                    match state with
                    | Stale -> Some entityId
                    | Backed _
                    | Unjudged -> None)
                |> Set.ofArray

            recordReadDrift scopeId entityType indexName states.Length backed.Length stale.Count

            // Heal the folder this lookup read: reclaim the stale refs it just
            // met (each re-judged before and after its delete).
            if not stale.IsEmpty then
                try
                    let! _reclaimed = vacuumKey scopeId entityType indexName keyOf key stale.Contains
                    ()
                with _ ->
                    ()

            return backed
        }

    let entityIdFromObjectId (entityType: string) (objectId: string) : EntityId option =
        let prefix =
            sprintf "%s%s%s" EntityObjectIdPrefix entityType EntityObjectIdSeparator

        if objectId.StartsWith prefix then
            Some(objectId.Substring prefix.Length)
        else
            None

    // ─── Phase 753 — the save pipeline, shared by `Save` and `SaveIfVersion` ──

    /// Steps 1–2 of a save: extract the required fields and validate the
    /// shape, then look the entity type up in the registry — it must be
    /// registered and its `Type` field must match the registration.
    let validateForSave (entity: 'T) : Result<EntityFieldsCore * EntityRegistration<'T>, EntityError> =
        match tryGetEntityFields entity with
        | Error msg -> Error(InvalidEntityShape msg)
        | Ok core ->
            match registry.TryGet<'T>(core.Type) with
            | None -> Error(EntityError.UnknownEntityType core.Type)
            | Some reg ->
                if reg.EntityType <> core.Type then
                    Error(
                        InvalidEntityShape(
                            sprintf "Entity Type field '%s' doesn't match registered type '%s'" core.Type reg.EntityType
                        )
                    )
                else
                    Ok(core, reg)

    /// Head version from a version list — `0` when the entity has never
    /// been written (the value `SaveIfVersion` / `DeleteIfVersion`
    /// callers state for "must not exist yet").
    let headOf (versions: DataObject list) : int =
        if versions.IsEmpty then
            0
        else
            versions |> List.map _.Version |> List.max

    /// Phase 806 — the lifecycle row for `version` of an entity, stamped
    /// from the actor on the call and nothing else: `UserId` is the
    /// principal, `OnBehalfOf` the delegation, `Replay` the offline
    /// provenance. No branch of this store writes a placeholder actor.
    let lifecyclePayload
        (actor: EntityPrincipal)
        (entityType: string)
        (entityId: EntityId)
        (version: int)
        : EntityLifecycleEventPayload =
        {
            UserId = actor.Principal
            OnBehalfOf = actor.OnBehalfOf
            EntityType = entityType
            EntityId = entityId
            Version = version
            Replay = actor.Replay
        }

    /// Everything that follows a successful version write (steps 6, 6b
    /// and the audit emission): maintain each declared index — removing
    /// the previous head's key when the indexed value changed —
    /// re-index the declared full-text fields, and emit the lifecycle
    /// audit event stamped from `actor`. `previousHead` is `0` on a
    /// create.
    let afterVersionWrite
        (scopeId: string)
        (actor: EntityPrincipal)
        (core: EntityFieldsCore)
        (reg: EntityRegistration<'T>)
        (entityWithVersion: 'T)
        (previousHead: int)
        (newVersion: int)
        (savedObject: DataObject)
        : Async<EntityRef<'T>> =
        async {
            let objectId = objectIdFor core.Type core.Id

            // Step 6: maintain indexes. For each declared index, compute
            // the new key, compare against the previous version's key
            // (if any), and update.
            for index in reg.Indexes do
                let newKey = index.Extract entityWithVersion
                let blobIndex = getIndex scopeId core.Type index.Name

                // If a previous version existed, we need to remove its
                // index entry under the OLD key (when the indexed value
                // changed).
                if previousHead > 0 then
                    // Read the previous version to compute its index key.
                    let! prevResult = dataObjectStore.GetVersion(scopeId, objectId, previousHead)

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

            // Step 6b — maintain full-text indexes (Phase 19b). Each
            // declared full-text field re-indexes the entity's current
            // text into its per-(entityType, field) BM25 sparse index,
            // keyed by entity id. Upsert is idempotent, so a re-save
            // replaces the prior text without an explicit remove. Empty
            // when no full-text fields are declared — zero extra writes
            // (GP 13); a deployment with no `ISparseIndex` wired skips
            // it entirely.
            match sparseIndex with
            | Some idx when not reg.FullTextFields.IsEmpty ->
                for fieldName, extractor in reg.FullTextFields do
                    let content = extractor entityWithVersion
                    let scope = fullTextScope scopeId core.Type fieldName

                    let chunk: TextChunk = {
                        Content = content
                        Metadata = Map.empty
                    }

                    do! idx.Upsert scope core.Id chunk
            | _ -> ()

            // Emit audit event. Best-effort — exceptions swallowed so
            // audit emission never fails the primary write.
            match auditLog with
            | Some log ->
                try
                    let payload = lifecyclePayload actor core.Type core.Id newVersion

                    let evt =
                        if newVersion = 1 then
                            EntityCreated payload
                        else
                            EntityUpdated payload

                    do! log.Record(scopeId, evt)
                with _ ->
                    ()
            | None -> ()

            return refFromDataObject<'T> savedObject core.Id core.Type
        }

    /// `Delete` and `DeleteIfVersion` share one pipeline; `expected` is
    /// the version the caller requires the head to be at, or `None` for
    /// the unconditional delete.
    ///
    /// Phase 973 — the entity's index refs go with it. `Delete` carries no
    /// `'T`, so the registry's per-type projection supplies each declared
    /// index's `Extract` (closed over the typed registration at `Register`
    /// time); the head's keys are read BEFORE the delete and each ref is
    /// removed AFTER it succeeds. Best-effort: a refused removal — or a
    /// head the read could not key — leaves a stale ref, which
    /// `FindByIndex` never answers with and reclaims when it meets it, and
    /// which `VacuumIndex` reclaims across the whole index.
    ///
    /// Phase 806 — the conditional form routes through
    /// `ConditionalDataObjectStore.deleteIfVersion`, so over the default
    /// `DataObjectStore` the compare and the removal are one act (the
    /// next version slot is claimed before anything is removed — see
    /// that store's header); a save racing the delete at the same
    /// expectation is refused, or refuses the delete, never both. Over a
    /// data-object store without the capability the helper compares then
    /// deletes, with the one-round-trip window documented on it. The
    /// unconditional form is the plain `Delete`, last-writer-wins as
    /// before. Every audit row here is stamped from `actor`.
    let deleteEntity
        (scopeId: string)
        (actor: EntityPrincipal)
        (entityType: string)
        (entityId: EntityId)
        (expected: int option)
        =
        async {
            if not (registry.Knows entityType) then
                return Error(EntityError.UnknownEntityType entityType)
            else
                let objectId = objectIdFor entityType entityId
                // Capture the head version before the delete so the audit
                // event records what got removed. Best-effort: on the
                // conditional form the seam re-checks it atomically.
                let! versionsBefore = dataObjectStore.ListVersions(scopeId, objectId)
                let headVersion = headOf versionsBefore

                // Phase 973 — the head's key in every declared index, read
                // while the head is still there. A type that declares no
                // index reads nothing (GP 13).
                let indexReaders = registry.IndexKeyReaders entityType

                let! headKeys = async {
                    if indexReaders.IsEmpty || headVersion = 0 then
                        return []
                    else
                        match! dataObjectStore.Get(scopeId, objectId) with
                        | Ok(_, bytes) ->
                            let json = Encoding.UTF8.GetString bytes

                            return
                                indexReaders
                                |> List.choose (fun (indexName, keyOf) ->
                                    try
                                        Some(indexName, keyOf json)
                                    with _ ->
                                        None)
                        | Error _ -> return []
                }

                let! deleteResult =
                    match expected with
                    | Some e -> async {
                        let! conditional = ConditionalDataObjectStore.deleteIfVersion dataObjectStore scopeId objectId e

                        return
                            match conditional with
                            | Ok() -> Ok()
                            | Error(ConditionalDeleteError.VersionConflict(exp, actual)) ->
                                Error(Choice1Of2(EntityError.VersionConflict(entityType, entityId, exp, actual)))
                            | Error(DeleteFailed doErr) -> Error(Choice2Of2 doErr)
                      }
                    | None -> async {
                        let! unconditional = dataObjectStore.Delete(scopeId, objectId)
                        return unconditional |> Result.mapError Choice2Of2
                      }

                match deleteResult with
                | Ok() ->
                    // Phase 973 — drop the entity's ref in every declared
                    // index (`BlobIndex.Remove` is the marked best-effort
                    // write; see the pipeline doc above for what reclaims a
                    // ref it leaves). Nothing here fails the delete, which
                    // has already happened.
                    for indexName, key in headKeys do
                        try
                            do! (getIndex scopeId entityType indexName).Remove key entityId
                        with _ ->
                            ()

                    // Phase 19b — drop the entity from every declared
                    // full-text field's sparse index. Field names come from
                    // the registry's non-generic projection (Delete carries
                    // no `'T`). Best-effort; even were a stale entry to
                    // survive, the query load step drops it (the same drift
                    // contract the secondary indexes rely on). Empty list or
                    // no sparse index ⇒ zero calls (GP 13).
                    match sparseIndex with
                    | Some idx ->
                        for fieldName in registry.FullTextFieldNames entityType do
                            let scope = fullTextScope scopeId entityType fieldName
                            do! idx.DeleteChunk scope entityId
                    | None -> ()

                    if headVersion > 0 then
                        match auditLog with
                        | Some log ->
                            try
                                do!
                                    log.Record(
                                        scopeId,
                                        EntityDeleted(lifecyclePayload actor entityType entityId headVersion)
                                    )
                            with _ ->
                                ()
                        | None -> ()

                    return Ok()
                | Error(Choice1Of2 conflict) -> return Error conflict
                | Error(Choice2Of2 DataObjectError.NotFound) -> return Ok() // idempotent
                | Error(Choice2Of2 err) ->
                    return Error(StorageFailure(sprintf "IDataObjectStore.Delete failed: %s" (string err)))
        }

    /// Source-compatible 4-arg constructor — the pre-19b shape. Delegates
    /// to the primary constructor with no sparse index (no full-text).
    new
        (
            dataObjectStore: IDataObjectStore,
            blobStorage: IBlobStorage,
            registry: EntityRegistry,
            auditLog: IAuditLog option
        ) =
        BlobEntityStore(dataObjectStore, blobStorage, registry, auditLog, None)

    /// Phase 973 — reclaim every stale ref in one `(entityType, indexName)`
    /// index for `scopeId`: each ref whose entity is gone or whose head no
    /// longer carries the key it is filed under. The separate operation the
    /// `BlobIndex.Rebuild` doc names; `FindByIndex` runs the same reclaim on
    /// the refs it reads. Idempotent and safe beside writes (see
    /// `BlobIndex.Vacuum`). Returns the number of refs removed; a refused
    /// delete is not counted and stays for the next run.
    member _.VacuumIndex(scopeId: string, entityType: string, indexName: string) : Async<Result<int, EntityError>> = async {
        if not (registry.Knows entityType) then
            return Error(EntityError.UnknownEntityType entityType)
        else
            match
                registry.IndexKeyReaders entityType
                |> List.tryFind (fun (name, _) -> name = indexName)
            with
            | None -> return Error(InvalidIndex indexName)
            | Some(_, keyOf) ->
                let! keys = indexKeys scopeId entityType indexName

                let! removed =
                    keys
                    |> List.map (fun key -> vacuumKey scopeId entityType indexName keyOf key (fun _ -> true))
                    |> Async.Sequential

                return Ok(Array.sum removed)
    }

    /// Phase 973 — the entity store's index-drift snapshot for `scopeId`,
    /// one `/dev/inspect` entry per index a `FindByIndex` has read since the
    /// store started (`StoreName = "entities"`, `IndexName =
    /// "{entityType}/{indexName}"`). The counts are of refs met AT READ TIME,
    /// not a sample: `SampleSize` refs resolved, `ConsistentEntries` whose
    /// head carried the looked-up value, `OrphanedIndexEntries` stale (the
    /// entity gone, or its head carrying another value) — each of which the
    /// lookup dropped from its answer and reclaimed through the vacuum.
    /// `UnindexedCanonicals` is always 0: a lookup cannot see a ref that is
    /// missing.
    member _.IndexDriftSnapshot(scopeId: string) : IndexConsistencyEntry list =
        readDrift
        |> Seq.choose (fun kv ->
            let scope, entityType, indexName = kv.Key
            let seen, consistent, stale = kv.Value

            if scope <> scopeId then
                None
            else
                Some {
                    StoreName = "entities"
                    IndexName = $"{entityType}/{indexName}"
                    SampleSize = seen
                    ConsistentEntries = consistent
                    OrphanedIndexEntries = stale
                    UnindexedCanonicals = 0
                })
        |> Seq.sortBy _.IndexName
        |> List.ofSeq

    interface IEntityStore with

        member _.Save<'T>
            (scopeId: string, actor: EntityPrincipal, entity: 'T)
            : Async<Result<EntityRef<'T>, EntityError>> =
            async {
                match validateForSave entity with
                | Error err -> return Error err
                | Ok(core, reg) ->
                    // Step 3: determine the next version. Read ListVersions;
                    // if empty, version = 1; else version = max + 1.
                    // Last-writer-wins by design — a caller that carried a
                    // version through a round trip uses `SaveIfVersion`.
                    let objectId = objectIdFor core.Type core.Id
                    let! existingVersions = dataObjectStore.ListVersions(scopeId, objectId)
                    let previousHead = headOf existingVersions
                    let newVersion = previousHead + 1

                    // Step 4: replace the entity's Version with the assigned
                    // version, then serialise.
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
                            actor.Principal,
                            Map.empty,
                            Versioned
                        )

                    match saveResult with
                    | Error doErr ->
                        return Error(StorageFailure(sprintf "IDataObjectStore.Save failed: %s" (string doErr)))
                    | Ok savedObject ->
                        let! entityRef =
                            afterVersionWrite
                                scopeId
                                actor
                                core
                                reg
                                entityWithVersion
                                previousHead
                                newVersion
                                savedObject

                        return Ok entityRef
            }

        // Phase 753 — compare-and-set save. The compare and the claim
        // are one act inside `ConditionalDataObjectStore.saveIfVersion`:
        // over the default `DataObjectStore` the version slot
        // `v{expected+1}.json` is written `IfAbsent` through the Phase
        // 600 seam, so two racers at the same expectation see exactly
        // one `Ok`; over a data-object store without the capability the
        // helper compares then saves, with the one-round-trip lost-update
        // window documented on it. Nothing here touches an index until
        // the version write has succeeded, so a refused expectation
        // leaves every index exactly as it was.
        member _.SaveIfVersion<'T>
            (scopeId: string, actor: EntityPrincipal, entity: 'T, expectedVersion: int)
            : Async<Result<EntityRef<'T>, EntityError>> =
            async {
                match validateForSave entity with
                | Error err -> return Error err
                | Ok(core, reg) ->
                    let objectId = objectIdFor core.Type core.Id
                    let newVersion = expectedVersion + 1
                    let entityWithVersion = withVersion entity newVersion
                    let bytes = Encoding.UTF8.GetBytes(serialise entityWithVersion)

                    let! saveResult =
                        ConditionalDataObjectStore.saveIfVersion
                            dataObjectStore
                            scopeId
                            objectId
                            bytes
                            (dataTypeFor core.Type)
                            actor.Principal
                            Map.empty
                            Versioned
                            expectedVersion

                    match saveResult with
                    | Error(ConditionalSaveError.VersionConflict(expected, actual)) ->
                        return Error(EntityError.VersionConflict(core.Type, core.Id, expected, actual))
                    | Error(SaveFailed doErr) ->
                        return Error(StorageFailure(sprintf "IDataObjectStore.SaveIfVersion failed: %s" (string doErr)))
                    | Ok savedObject ->
                        let! entityRef =
                            afterVersionWrite
                                scopeId
                                actor
                                core
                                reg
                                entityWithVersion
                                expectedVersion
                                newVersion
                                savedObject

                        return Ok entityRef
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

        member _.Delete(scopeId: string, actor: EntityPrincipal, entityType: string, entityId: EntityId) =
            deleteEntity scopeId actor entityType entityId None

        // Phase 753 / 806 — compare-and-set delete; see `deleteEntity`
        // for how the compare and the removal are made one act.
        member _.DeleteIfVersion
            (scopeId: string, actor: EntityPrincipal, entityType: string, entityId: EntityId, expectedVersion: int)
            =
            deleteEntity scopeId actor entityType entityId (Some expectedVersion)

        member _.FindByIndex<'T>(scopeId: string, entityType: string, indexName: string, value: string) = async {
            match registry.TryGet<'T>(entityType) with
            | None -> return Error(EntityError.UnknownEntityType entityType)
            | Some reg ->
                match EntityRegistration.tryFindIndex indexName reg with
                | None -> return Error(InvalidIndex indexName)
                | Some index ->
                    // Phase 973 — a ref is derived; the head is authoritative:
                    // only a head that still carries `value` answers (see
                    // `backedRefs` for the stale-ref count and reclaim).
                    let keyOf (json: string) = index.Extract(deserialise<'T> json)
                    let! backed = backedRefs scopeId entityType indexName keyOf value

                    return
                        Ok(
                            backed
                            |> List.map (fun (entityId, version) ->
                                {
                                    Id = entityId
                                    Type = entityType
                                    Version = version
                                }
                                : EntityRef<'T>)
                        )
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
                    // Build the executor context. AllEntityIds / LoadEntity go
                    // through IDataObjectStore.
                    //
                    // Phase 974 — LookupByIndex is `FindByIndex`'s lookup: the
                    // cached BlobIndex lists the candidates, and only those whose
                    // head still carries `value` (the index's own `Extract` over
                    // the head) answer; a stale ref is counted in the drift
                    // snapshot and reclaimed. So `Eq`/`In` never return, and
                    // `Ne`/`Not` never exclude, an entity on a ref alone.
                    // AllIndexKeys enumerates the index's keys via
                    // IBlobStorage.List and DECODES each path segment, so the
                    // range predicates compare the values themselves and hand
                    // LookupByIndex a value it encodes exactly once.
                    let lookupByIndex (indexName: string) (value: string) = async {
                        match EntityRegistration.tryFindIndex indexName reg with
                        | None -> return []
                        | Some index ->
                            let keyOf (json: string) = index.Extract(deserialise<'T> json)
                            let! backed = backedRefs scopeId query.EntityType indexName keyOf value
                            return backed |> List.map fst
                    }

                    let allIndexKeys (indexName: string) =
                        indexKeys scopeId query.EntityType indexName

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
                            use doc = JsonDocument.Parse(json)

                            match doc.RootElement.TryGetProperty(fieldName) with
                            | true, prop -> Some(prop.ToString())
                            | false, _ -> None
                        with _ ->
                            None

                    // Phase 19b — resolve a `FullText` leaf via the field's
                    // BM25 sparse index within this scope's synthetic
                    // full-text scope. `topK = MaxValue` returns every
                    // matching id (BM25 only scores docs holding at least
                    // one query term, so the pool is naturally bounded)
                    // before intersection/union with sibling predicates. No
                    // sparse index wired ⇒ the empty list.
                    let fullTextSearch (fieldName: string) (queryText: string) = async {
                        match sparseIndex with
                        | None -> return []
                        | Some idx ->
                            let scope = fullTextScope scopeId query.EntityType fieldName
                            let! matches = idx.Search [ scope ] queryText System.Int32.MaxValue
                            return matches |> List.map _.ChunkId
                    }

                    let ctx: EntityQueryExecutor.ExecutorContext = {
                        LookupByIndex = lookupByIndex
                        AllIndexKeys = allIndexKeys
                        AllEntityIds = allEntityIds
                        LoadEntity = loadEntity
                        ReadFieldString = readFieldString
                        FullTextSearch = fullTextSearch
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