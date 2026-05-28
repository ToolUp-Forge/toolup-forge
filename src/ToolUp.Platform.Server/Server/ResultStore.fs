module ToolUp.Platform.ResultStore

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open Newtonsoft.Json
open Fable.Remoting.Json
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
    /// Newtonsoft + FableJsonConverter — same pattern as
    /// `ConfigStore` / `DataObjectStore`. `LineageLink` payloads
    /// round-trip the `LinkType` DU losslessly, which
    /// `System.Text.Json` does not.
    let private settings =
        let s = JsonSerializerSettings()
        s.Converters.Add(FableJsonConverter())
        s

    let private serialize (value: 'T) : string =
        JsonConvert.SerializeObject(value, settings)

    /// Best-effort: callers `Async.Start` this. Any failure is the
    /// `IEventStore`'s problem to log; we don't surface it back to
    /// the result-save path.
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
    let recordLineage
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

                    let! _ = store.Record(scopeId, link)
                    ()
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

type InMemoryResultStore(?eventStore: IEventStore, ?lineageStore: ILineageStore) =
    let entries = ConcurrentDictionary<string * string, InMemoryResultEntry>()

    let getOrCreate (scopeId, objectId) =
        entries.GetOrAdd((scopeId, objectId), (fun _ -> { Versions = []; Lock = obj () }))

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
            // so a slow event store doesn't block the caller; errors
            // are logged inside the respective stores.
            Events.recordAnalysisCompleted eventStore scopeId moduleName objectId dataObject.Version createdBy
            |> Async.Start

            Events.recordLineage lineageStore scopeId moduleName objectId inputs
            |> Async.Start

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

// ─── Persistent implementation ──────────────────────────────────

/// Wraps `IDataObjectStore` with a Phase 8-shaped surface:
/// derives `ObjectId` from `(moduleName, resultType)`, hardcodes
/// `StrictlyVersioned` policy, and emits `AnalysisCompleted` /
/// `LineageLink` events on every successful save. Stateless between
/// calls — every method derives its result from the parameters +
/// `IDataObjectStore`.
type PersistentResultStore(objectStore: IDataObjectStore, ?eventStore: IEventStore, ?lineageStore: ILineageStore) =

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
                |> Async.Start

                Events.recordLineage lineageStore scopeId moduleName objectId inputs
                |> Async.Start

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