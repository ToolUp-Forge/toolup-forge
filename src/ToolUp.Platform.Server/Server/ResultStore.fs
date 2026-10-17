module ToolUp.Platform.ResultStore

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── ResultStore implementations ────────────────────────────────
//
// Two implementations satisfy `IResultStore`:
//
// - `InMemoryResultStore` — `ConcurrentDictionary` keyed on
//   `(scopeId, objectId)`, mapped to a per-object version list.
//   Suitable for tests and short-lived dev environments. **Stateful
//   between calls** — documented dev-only exception to Phase 9c
//   Rule 4 (same shape as `LocalEmbeddingProvider`).
//
// - `PersistentResultStore` — delegates to `IDataObjectStore` with
//   `StrictlyVersioned` policy. Stateless between calls. Emits
//   `AnalysisCompleted` events to `IEventStore` and (when an
//   `ILineageStore` is available) auto-emits `LineageLink`s for
//   declared inputs.
//
// Both variants emit `AnalysisCompleted` events when an
// `IEventStore` is available — events are an audit-trail concern
// independent of result durability.

// ─── JSON diff ───────────────────────────────────────────────────
//
// Walks two `JsonDocument` trees in parallel and emits
// `FieldChange` records for every leaf-level difference. Object
// keys not present on one side surface as
// `{ From = None; To = Some _ }` (added) or
// `{ From = Some _; To = None }` (removed). Arrays are diffed by
// index — element re-ordering produces every-element changes
// (acceptable for the MVP; richer array-diff is a follow-up).

module private Diff =

    let private joinPath (parent: string) (segment: string) =
        if parent = "" then segment else parent + "." + segment

    let private joinIndex (parent: string) (index: int) =
        if parent = "" then $"[{index}]" else $"{parent}[{index}]"

    let private valueText (element: JsonElement) =
        if element.ValueKind = JsonValueKind.Undefined then
            None
        else
            Some(element.GetRawText())

    let rec private walk (path: string) (from: JsonElement option) (to_: JsonElement option) : FieldChange list =
        match from, to_ with
        | None, None -> []
        | Some f, None -> [
            {
                Path = path
                From = valueText f
                To = None
            }
          ]
        | None, Some t -> [
            {
                Path = path
                From = None
                To = valueText t
            }
          ]
        | Some f, Some t when f.ValueKind = JsonValueKind.Object && t.ValueKind = JsonValueKind.Object ->
            // Union of keys; recurse per key.
            let fromKeys = f.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

            let toKeys = t.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

            Set.union fromKeys toKeys
            |> Seq.sort
            |> Seq.collect (fun key ->
                let fromChild =
                    if fromKeys.Contains key then
                        Some(f.GetProperty key)
                    else
                        None

                let toChild =
                    if toKeys.Contains key then
                        Some(t.GetProperty key)
                    else
                        None

                walk (joinPath path key) fromChild toChild)
            |> Seq.toList
        | Some f, Some t when f.ValueKind = JsonValueKind.Array && t.ValueKind = JsonValueKind.Array ->
            let fromItems = f.EnumerateArray() |> Seq.toArray
            let toItems = t.EnumerateArray() |> Seq.toArray
            let maxLen = max fromItems.Length toItems.Length

            [
                for i in 0 .. maxLen - 1 do
                    let fromChild = if i < fromItems.Length then Some fromItems[i] else None

                    let toChild = if i < toItems.Length then Some toItems[i] else None

                    yield! walk (joinIndex path i) fromChild toChild
            ]
        | Some f, Some t ->
            // Leaf comparison — strings, numbers, booleans, null.
            // Use raw text so numerics keep their canonical form.
            if f.GetRawText() = t.GetRawText() then
                []
            else
                [
                    {
                        Path = path
                        From = valueText f
                        To = valueText t
                    }
                ]

    /// Compute the field-level diff between two JSON documents.
    /// Throws `JsonException` (caught by the caller) if either side
    /// is not valid JSON.
    let compute (from: byte[]) (to_: byte[]) : FieldChange list =
        use fromDoc = JsonDocument.Parse(ReadOnlyMemory(from))
        use toDoc = JsonDocument.Parse(ReadOnlyMemory(to_))
        walk "" (Some fromDoc.RootElement) (Some toDoc.RootElement)

// ─── Event emission ──────────────────────────────────────────────

module private Events =
    /// STJ + FableConverters — same pattern as
    /// `ConfigStore` / `DataObjectStore`. `LineageLink` payloads
    /// round-trip the `LinkType` DU losslessly, which
    /// `System.Text.Json` does not.
    let private options = FableConverters.create ()

    let private serialize (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    /// Phase 1025 — the write a failed `AnalysisCompleted` emission's
    /// warning names.
    let analysisCompletedWrite (scopeId: string) (moduleName: string) (objectId: string) (version: int) =
        sprintf
            "AnalysisCompleted event (scope %s, module %s, object %s, version %d)"
            scopeId
            moduleName
            objectId
            version

    /// Phase 1025 — the write a failed lineage link's warning names: every
    /// field the link was made of, so an operator can re-record a lost link
    /// by hand from the log line alone. Lineage feeds provenance, and a
    /// missing link is otherwise invisible in the graph.
    let lineageWrite (scopeId: string) (link: LineageLink) =
        sprintf
            "lineage link (scope %s, %s -> %s, module %s, type %A, link %O, at %s)"
            scopeId
            link.FromObjectId
            link.ToObjectId
            link.ModuleName
            link.LinkType
            link.LinkId
            (link.Timestamp.ToString "O")

    /// Best-effort: callers start this through `Background.startAudit`
    /// (Phase 1025), so a failing `IEventStore` loses the one event and is
    /// logged, never surfaced to the result-save path.
    let recordAnalysisCompleted
        (eventStore: IEventStore option)
        (scopeId: string)
        (moduleName: string)
        (objectId: string)
        (version: int)
        (createdBy: string)
        : Async<unit> =
        async {
            match eventStore with
            | None -> return ()
            | Some store ->
                let payload =
                    serialize {|
                        objectId = objectId
                        version = version
                        moduleName = moduleName
                        createdBy = createdBy
                    |}

                do!
                    store.Write {
                        Id = Guid.NewGuid()
                        OccurredAt = DateTime.UtcNow
                        ScopeId = scopeId
                        SourceModule = moduleName
                        EventType = ResultEventType.AnalysisCompleted
                        Payload = payload
                    }
        }

    /// Emit one `LineageLink` per input. No-op when
    /// `lineageStore = None`.
    ///
    /// Phase 1025 — each link is recorded on its own: a link the store
    /// refuses (an `Error` result, which this used to drop silently) or
    /// throws on is handed to `warn` under `lineageWrite`'s name, and the
    /// remaining links are still recorded. The loss is bounded to the
    /// failed links and each is reconstructable from its warning.
    let recordLineage
        (warn: string -> exn -> unit)
        (lineageStore: ILineageStore option)
        (scopeId: string)
        (moduleName: string)
        (toObjectId: string)
        (inputs: string list)
        : Async<unit> =
        async {
            match lineageStore with
            | None -> return ()
            | Some store ->
                for inputId in inputs do
                    let link = {
                        LinkId = Guid.NewGuid()
                        FromObjectId = inputId
                        ToObjectId = toObjectId
                        ModuleName = moduleName
                        LinkType = Derived
                        Timestamp = DateTime.UtcNow
                    }

                    try
                        match! store.Record(scopeId, link) with
                        | Ok() -> ()
                        | Error reason ->
                            warn
                                (lineageWrite scopeId link)
                                (InvalidOperationException(sprintf "ILineageStore.Record refused the link: %s" reason))
                    with ex ->
                        warn (lineageWrite scopeId link) ex
        }

// ─── In-memory implementation ───────────────────────────────────

/// Per-`(scopeId, objectId)` slot held by `InMemoryResultStore`.
/// Versions accumulate; the latest is the last-written. Concurrent
/// saves to the same key serialise via the lock around `lock` — the
/// dictionary itself is concurrent, but the per-slot append needs
/// linearisation to keep version numbers monotonic.
type private InMemoryResultEntry = {
    mutable Versions: (DataObject * byte[]) list
    Lock: obj
}

/// Phase 1025 — the `AnalysisCompleted` event and the lineage links a save
/// emits are started through `Background.startAudit` with `warn`: a failing
/// event or lineage store loses those writes and logs a Warning naming each,
/// and never takes the host down or changes the save's result. The internal
/// primary constructor carries the logger composition hands it; the public
/// one logs to the SDK's default `ConsoleLogger`.
type InMemoryResultStore internal (eventStore: IEventStore option, lineageStore: ILineageStore option, logger: ILogger)
    =
    let entries = ConcurrentDictionary<string * string, InMemoryResultEntry>()

    let warn =
        Background.writeFailureLogger "InMemoryResultStore" (Some logger) "" "the save's result"

    let getOrCreate (scopeId, objectId) =
        entries.GetOrAdd((scopeId, objectId), (fun _ -> { Versions = []; Lock = obj () }))

    /// The public constructor: the default `ConsoleLogger` (see the type's doc).
    new(?eventStore: IEventStore, ?lineageStore: ILineageStore) =
        InMemoryResultStore(eventStore, lineageStore, ConsoleLogger.ConsoleLogger() :> ILogger)

    interface IResultStore with
        member _.SaveResult(scopeId, moduleName, resultType, content, createdBy, inputs) = async {
            let objectId = ResultObjectId.make moduleName resultType
            let entry = getOrCreate (scopeId, objectId)

            let dataObject =
                lock entry.Lock (fun () ->
                    let nextVersion = entry.Versions.Length + 1

                    let obj = {
                        ObjectId = objectId
                        Version = nextVersion
                        CreatedAt = DateTime.UtcNow
                        CreatedBy = createdBy
                        ScopeId = scopeId
                        DataType = resultType
                        ContentHash = ""
                        Policy = StrictlyVersioned
                        Metadata = Map.empty
                    }

                    entry.Versions <- entry.Versions @ [ obj, content ]
                    obj)

            // Best-effort event + lineage emission. Fire-and-forget
            // so a slow event store doesn't block the caller; a failure
            // is logged under the write's name (Phase 1025).
            Events.recordAnalysisCompleted eventStore scopeId moduleName objectId dataObject.Version createdBy
            |> Background.startAudit warn (Events.analysisCompletedWrite scopeId moduleName objectId dataObject.Version)

            Events.recordLineage warn lineageStore scopeId moduleName objectId inputs
            |> Background.startAudit warn (sprintf "lineage links into %s (scope %s)" objectId scopeId)

            return Ok dataObject
        }

        member _.GetLatest(scopeId, moduleName, resultType) = async {
            let objectId = ResultObjectId.make moduleName resultType

            match entries.TryGetValue((scopeId, objectId)) with
            | true, entry ->
                match entry.Versions |> List.tryLast with
                | Some(obj, bytes) -> return Ok(obj, bytes)
                | None -> return Error NotFound
            | false, _ -> return Error NotFound
        }

        member _.ListResults(scopeId, moduleName, dateRange) = async {
            let prefix = ResultObjectId.modulePrefix moduleName

            let inRange (obj: DataObject) =
                match dateRange with
                | None -> true
                | Some(fromTs, toTs) -> obj.CreatedAt >= fromTs && obj.CreatedAt <= toTs

            let results =
                entries
                |> Seq.filter (fun kvp ->
                    let (scope, objectId) = kvp.Key
                    scope = scopeId && objectId.StartsWith(prefix))
                |> Seq.collect (fun kvp -> kvp.Value.Versions |> List.map fst)
                |> Seq.filter inRange
                |> Seq.sortByDescending _.CreatedAt
                |> Seq.toList

            return results
        }

        member _.CompareVersions(scopeId, objectId, v1, v2) = async {
            match entries.TryGetValue((scopeId, objectId)) with
            | false, _ -> return Error NotFound
            | true, entry ->
                let pickVersion v =
                    entry.Versions |> List.tryFind (fun (obj, _) -> obj.Version = v)

                match pickVersion v1, pickVersion v2 with
                | None, _ -> return Error(VersionNotFound v1)
                | _, None -> return Error(VersionNotFound v2)
                | Some(_, fromBytes), Some(_, toBytes) ->
                    try
                        let changes = Diff.compute fromBytes toBytes

                        return
                            Ok {
                                ObjectId = objectId
                                FromVersion = v1
                                ToVersion = v2
                                Changes = changes
                            }
                    with :? System.Text.Json.JsonException as ex ->
                        return Error(StorageFailure $"diff requires JSON content: {ex.Message}")
        }

        member _.DeleteResult(scopeId, moduleName, resultType) = async {
            let objectId = ResultObjectId.make moduleName resultType
            // Idempotent: TryRemove on an absent key is a no-op.
            entries.TryRemove((scopeId, objectId)) |> ignore
            return Ok()
        }

        member _.DeleteByPrefix(scopeId, moduleName, resultTypePrefix) = async {
            let objectPrefix = ResultObjectId.make moduleName resultTypePrefix

            let toRemove =
                entries.Keys
                |> Seq.filter (fun (scope, objectId) -> scope = scopeId && objectId.StartsWith(objectPrefix))
                |> Seq.toList

            for key in toRemove do
                entries.TryRemove(key) |> ignore

            return Ok()
        }

// ─── Persistent implementation ──────────────────────────────────

/// Wraps `IDataObjectStore` with a Phase 8-shaped surface:
/// derives `ObjectId` from `(moduleName, resultType)`, hardcodes
/// `StrictlyVersioned` policy, and emits `AnalysisCompleted` /
/// `LineageLink` events on every successful save. Stateless between
/// calls — every method derives its result from the parameters +
/// `IDataObjectStore`.
/// Phase 1025 — the `AnalysisCompleted` event and the lineage links a save
/// emits are started through `Background.startAudit` with `warn`: a failing
/// event or lineage store loses those writes and logs a Warning naming each,
/// and never takes the host down or changes the save's result. The internal
/// primary constructor carries the logger composition hands it; the public
/// one logs to the SDK's default `ConsoleLogger`.
type PersistentResultStore
    internal
    (objectStore: IDataObjectStore, eventStore: IEventStore option, lineageStore: ILineageStore option, logger: ILogger)
    =

    let warn =
        Background.writeFailureLogger "PersistentResultStore" (Some logger) "" "the save's result"

    /// The public constructor: the default `ConsoleLogger` (see the type's doc).
    new(objectStore: IDataObjectStore, ?eventStore: IEventStore, ?lineageStore: ILineageStore) =
        PersistentResultStore(objectStore, eventStore, lineageStore, ConsoleLogger.ConsoleLogger() :> ILogger)

    interface IResultStore with
        member _.SaveResult(scopeId, moduleName, resultType, content, createdBy, inputs) = async {
            let objectId = ResultObjectId.make moduleName resultType

            let metadata = Map [ "moduleName", moduleName; "resultType", resultType ]

            let! result =
                objectStore.Save(scopeId, objectId, content, resultType, createdBy, metadata, StrictlyVersioned)

            match result with
            | Error err -> return Error err
            | Ok dataObject ->
                Events.recordAnalysisCompleted eventStore scopeId moduleName objectId dataObject.Version createdBy
                |> Background.startAudit
                    warn
                    (Events.analysisCompletedWrite scopeId moduleName objectId dataObject.Version)

                Events.recordLineage warn lineageStore scopeId moduleName objectId inputs
                |> Background.startAudit warn (sprintf "lineage links into %s (scope %s)" objectId scopeId)

                return Ok dataObject
        }

        member _.GetLatest(scopeId, moduleName, resultType) =
            let objectId = ResultObjectId.make moduleName resultType
            objectStore.Get(scopeId, objectId)

        member _.ListResults(scopeId, moduleName, dateRange) = async {
            let prefix = ResultObjectId.modulePrefix moduleName

            let inRange (obj: DataObject) =
                match dateRange with
                | None -> true
                | Some(fromTs, toTs) -> obj.CreatedAt >= fromTs && obj.CreatedAt <= toTs

            let! allObjects = objectStore.ListObjects scopeId

            // For each module-prefixed object, pull its full version
            // history. `ListObjects` returns latest-only, so we need
            // a per-object `ListVersions` fan-out for the date-range
            // filter to apply over the full history.
            let moduleObjects =
                allObjects |> List.filter (fun obj -> obj.ObjectId.StartsWith(prefix))

            let! versionsByObject =
                moduleObjects
                |> List.map (fun obj -> objectStore.ListVersions(scopeId, obj.ObjectId))
                |> Async.Parallel

            return
                versionsByObject
                |> Array.toList
                |> List.collect id
                |> List.filter inRange
                |> List.sortByDescending _.CreatedAt
        }

        member _.CompareVersions(scopeId, objectId, v1, v2) = async {
            let! fromResult = objectStore.GetVersion(scopeId, objectId, v1)
            let! toResult = objectStore.GetVersion(scopeId, objectId, v2)

            match fromResult, toResult with
            | Error err, _ -> return Error err
            | _, Error err -> return Error err
            | Ok(_, fromBytes), Ok(_, toBytes) ->
                try
                    let changes = Diff.compute fromBytes toBytes

                    return
                        Ok {
                            ObjectId = objectId
                            FromVersion = v1
                            ToVersion = v2
                            Changes = changes
                        }
                with :? System.Text.Json.JsonException as ex ->
                    return Error(StorageFailure $"diff requires JSON content: {ex.Message}")
        }

        member _.DeleteResult(scopeId, moduleName, resultType) =
            let objectId = ResultObjectId.make moduleName resultType
            // `Evict` bypasses the `StrictlyVersioned` guard the store
            // applies on `SaveResult` — see `IResultStore` /
            // `IDataObjectStore.Evict` doc-comments. Idempotent.
            objectStore.Evict(scopeId, objectId)

        member _.DeleteByPrefix(scopeId, moduleName, resultTypePrefix) = async {
            let objectPrefix = ResultObjectId.make moduleName resultTypePrefix

            let! allObjects = objectStore.ListObjects scopeId

            let matching =
                allObjects |> List.filter (fun obj -> obj.ObjectId.StartsWith(objectPrefix))

            let! results =
                matching
                |> List.map (fun obj -> objectStore.Evict(scopeId, obj.ObjectId))
                |> Async.Parallel

            // First error wins; all-Ok (or empty) is Ok.
            return
                match
                    results
                    |> Array.tryPick (function
                        | Error e -> Some e
                        | Ok _ -> None)
                with
                | Some err -> Error err
                | None -> Ok()
        }