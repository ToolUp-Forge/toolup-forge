module ToolUp.Platform.PersistentEventStore

open System
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.SecondaryIndex

// ─── Storage layout ──────────────────────────────────────────────
//
// Container: always `_platform`.
// Canonical event blob: `events/{scopeId}/{ts}-{eventId:N}.json`
// Secondary indexes (Phase 9f):
//   `events/{scopeId}/_by-type/{eventType}/{eventId:N}.ref`     — payload = canonical blob name
//   `events/{scopeId}/_by-source/{sourceModule}/{eventId:N}.ref` — payload = canonical blob name
//
// - One blob per event: avoids append-on-blob concurrency pitfalls
//   (blob stores offer no cross-provider atomic append); every writer
//   produces a fresh unique blob name.
// - Timestamp prefix is lexicographically sortable, ISO-ordered, and
//   uses hyphens in the time component so every blob backend accepts it.
// - Event id suffix disambiguates writes that happen in the same
//   100-ns tick (possible under real load on Windows's DateTime.UtcNow
//   resolution).
// - Scope ID lives in the path prefix so `List(container, prefix)`
//   is a cheap per-scope read and cross-scope leakage is structurally
//   impossible — a bug in the deserialiser cannot make a scope-B blob
//   show up under a scope-A prefix query. The same prefix discipline
//   applies to the secondary indexes — `_by-type` / `_by-source` live
//   under `events/{scopeId}/` so a `List` against one scope's index
//   cannot leak refs from another.
//
// The `.ref` payload stores the canonical blob name so `ReadByType`
// can resolve to the canonical event without round-tripping the
// timestamp prefix. The canonical name format `{ts}-{eventId:N}.json`
// is currently derivable only with `OccurredAt` in hand; storing the
// name in the index avoids a second indirection.

[<Literal>]
let private MaintenanceSourceModule = "_platform.maintenance"

let private platformContainer = "_platform"

let private scopePrefix (scopeId: string) = $"events/{scopeId}/"

let private byTypePrefix (scopeId: string) = $"events/{scopeId}/_by-type"

let private bySourcePrefix (scopeId: string) = $"events/{scopeId}/_by-source"

let private timestampPart (occurredAt: DateTime) =
    occurredAt.ToUniversalTime().ToString("yyyy-MM-ddTHH-mm-ss-fffffffZ")

let private blobName (event: ModuleEvent) =
    $"{scopePrefix event.ScopeId}{timestampPart event.OccurredAt}-{event.Id:N}.json"

/// True when the blob name belongs to a canonical event (filters out
/// `_by-type/...` / `_by-source/...` index refs that share the
/// `events/{scope}/` prefix).
let private isCanonicalEventBlob (name: string) =
    name.EndsWith ".json"
    && not (name.Contains "/_by-type/")
    && not (name.Contains "/_by-source/")
    && not (name.Contains "\\_by-type\\")
    && not (name.Contains "\\_by-source\\")

// ─── JSON serialisation ──────────────────────────────────────────

/// Camel-case JSON with sane defaults. Mirrors TeamManagement's
/// serialiser so admins who inspect blobs across platform features
/// see a consistent shape.
let private jsonOptions =
    let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    opts.Converters.Add(JsonStringEnumConverter())
    opts

let private serialize (event: ModuleEvent) : byte[] =
    let dto = {|
        id = event.Id
        occurredAt = event.OccurredAt.ToUniversalTime().ToString("o")
        scopeId = event.ScopeId
        sourceModule = event.SourceModule
        eventType = event.EventType
        payload = event.Payload
    |}

    JsonSerializer.Serialize(dto, jsonOptions) |> Encoding.UTF8.GetBytes

let private deserialize (bytes: byte[]) : ModuleEvent =
    let doc = JsonDocument.Parse(Encoding.UTF8.GetString bytes)
    let root = doc.RootElement

    {
        Id = root.GetProperty("id").GetGuid()
        OccurredAt =
            DateTime.Parse(
                root.GetProperty("occurredAt").GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind
            )
        ScopeId = root.GetProperty("scopeId").GetString()
        SourceModule = root.GetProperty("sourceModule").GetString()
        EventType = root.GetProperty("eventType").GetString()
        Payload = root.GetProperty("payload").GetString()
    }

// ─── Concurrency helpers ─────────────────────────────────────────

/// Download many blobs in parallel, tolerating individual failures by
/// skipping them. A partial read is preferable to a total failure —
/// event history is advisory, not critical path. Callers that need
/// strict consistency should not be using a blob-backed store.
let private downloadAll (blobStorage: IBlobStorage) (blobNames: string list) : Async<ModuleEvent list> = async {
    let! results =
        blobNames
        |> List.map (fun name -> async {
            let! result = blobStorage.Download(platformContainer, name)

            return
                match result with
                | Ok bytes ->
                    try
                        Some(deserialize bytes)
                    with _ ->
                        None
                | Error _ -> None
        })
        |> Async.Parallel

    return results |> Array.choose id |> Array.toList
}

/// Resolve a list of `(eventId, canonicalBlobName)` pairs (from an
/// index lookup) to the canonical events. Soft-misses on individual
/// resolves — drift between index and canonical is a recoverable
/// bug class, not a hard error.
let private resolveCanonicals (blobStorage: IBlobStorage) (entries: (Guid * string) list) : Async<ModuleEvent list> = async {
    let! results =
        entries
        |> List.map (fun (_, canonicalName) -> async {
            let! result = blobStorage.Download(platformContainer, canonicalName)

            return
                match result with
                | Ok bytes ->
                    try
                        Some(deserialize bytes)
                    with _ ->
                        None
                | Error _ -> None
        })
        |> Async.Parallel

    return results |> Array.choose id |> Array.toList
}

// ─── Store ───────────────────────────────────────────────────────

/// Blob-backed `IEventStore`. One JSON blob per event, stored in the
/// reserved `_platform` container under `events/{scopeId}/`.
///
/// Every read path filters by `scopeId` via the blob-name prefix —
/// scope isolation is structural, not defensive. Distributed
/// implementations replacing this class should preserve the
/// prefix-isolation property.
///
/// **Secondary indexes (Phase 9f).** `Write` additionally writes a
/// `_by-type/{eventType}/{eventId}.ref` and a
/// `_by-source/{sourceModule}/{eventId}.ref` entry per event, with
/// the canonical blob name as the payload. `ReadByType` /
/// `ReadBySource` resolve via the index instead of scanning every
/// blob in the scope. Drift between index and canonical is a soft-
/// miss on read, recoverable via `Rebuild`.
///
/// The retention policy governs eligibility for `pruneScope` /
/// `pruneScopes` — it does NOT cause writes to trigger pruning.
/// Scheduled pruning is the caller's responsibility (call from a
/// background job or startup routine). Pruning deletes the canonical
/// blob and both index refs in a single batch.
type PersistentEventStore(blobStorage: IBlobStorage, retentionPolicy: EventRetentionPolicy) =

    // Per-scope index helpers. Constructed on demand to keep `indexPrefix`
    // scope-pinned (GP4 — team isolation is structural; we can't bake one
    // scope into a single shared index instance because each `Write` may
    // target a different scope).
    let typeIndexFor (scopeId: string) : BlobIndex<string, Guid> =
        BlobIndex.create
            blobStorage
            platformContainer
            (byTypePrefix scopeId)
            id
            (fun (g: Guid) -> g.ToString "N")
            (fun s ->
                match Guid.TryParseExact(s, "N") with
                | true, g -> Some g
                | false, _ -> None)

    let sourceIndexFor (scopeId: string) : BlobIndex<string, Guid> =
        BlobIndex.create
            blobStorage
            platformContainer
            (bySourcePrefix scopeId)
            id
            (fun (g: Guid) -> g.ToString "N")
            (fun s ->
                match Guid.TryParseExact(s, "N") with
                | true, g -> Some g
                | false, _ -> None)

    let writeIndexEntries (event: ModuleEvent) = async {
        let canonicalNameBytes = blobName event |> Encoding.UTF8.GetBytes
        let typeIndex = typeIndexFor event.ScopeId
        let sourceIndex = sourceIndexFor event.ScopeId

        do!
            [
                typeIndex.Add event.EventType event.Id (Some canonicalNameBytes)
                sourceIndex.Add event.SourceModule event.Id (Some canonicalNameBytes)
            ]
            |> Async.Parallel
            |> Async.Ignore
    }

    let removeIndexEntries (event: ModuleEvent) = async {
        let typeIndex = typeIndexFor event.ScopeId
        let sourceIndex = sourceIndexFor event.ScopeId

        do!
            [
                typeIndex.Remove event.EventType event.Id
                sourceIndex.Remove event.SourceModule event.Id
            ]
            |> Async.Parallel
            |> Async.Ignore
    }

    /// Resolve `(value, payload)` index entries to canonical events,
    /// dropping any entries whose canonical blob no longer exists
    /// (drift soft-miss).
    let resolveIndexEntries (entries: (Guid * byte[] option) list) = async {
        let pairs =
            entries
            |> List.choose (fun (id, payload) ->
                match payload with
                | Some bytes ->
                    let name = Encoding.UTF8.GetString bytes
                    Some(id, name)
                | None -> None)

        return! resolveCanonicals blobStorage pairs
    }

    /// Compute the set of events eligible for pruning under the
    /// current retention policy. Returns blob names, not events,
    /// because the caller will only delete them.
    let eligibleForPruning (events: (string * ModuleEvent) list) : string list =
        // Age-eligible: events older than MaxAge. When MaxAge is None
        // NOTHING is age-eligible — a missing policy dimension is
        // permissive, not exhaustive.
        let byAge =
            match retentionPolicy.MaxAge with
            | None -> []
            | Some maxAge ->
                let cutoff = DateTime.UtcNow - maxAge
                events |> List.filter (fun (_, e) -> e.OccurredAt < cutoff)

        // Count-eligible: if a count cap is set and exceeded, the
        // OLDEST (events.Length - maxCount) events are eligible.
        // Independent of age — either dimension triggers pruning.
        let byCount =
            match retentionPolicy.MaxCountPerScope with
            | None -> []
            | Some maxCount ->
                if events.Length <= maxCount then
                    []
                else
                    events
                    |> List.sortBy (fun (_, e) -> e.OccurredAt)
                    |> List.take (events.Length - maxCount)

        (byAge @ byCount) |> List.map fst |> List.distinct

    /// Prune a single scope under the configured retention policy.
    /// Returns the number of events pruned. A no-op if the policy is
    /// `unlimited`.
    member _.PruneScope(scopeId: string) : Async<int> = async {
        if retentionPolicy = EventRetentionPolicy.unlimited then
            return 0
        else
            let! names = blobStorage.List(platformContainer, scopePrefix scopeId)
            let canonicalNames = names |> List.filter isCanonicalEventBlob
            // Download metadata-by-payload (we need OccurredAt to decide
            // eligibility and we already round-trip OccurredAt in the
            // event body).
            let! events = downloadAll blobStorage canonicalNames
            // Pair back with blob names. Relies on the timestamp prefix
            // making the blob name predictable from the event — the
            // same function that writes also derives the read key.
            let pairs = events |> List.map (fun e -> (blobName e, e))

            let toDelete = eligibleForPruning pairs

            let toDeleteSet = Set.ofList toDelete

            let prunedEvents =
                pairs
                |> List.choose (fun (n, e) -> if toDeleteSet.Contains n then Some e else None)

            // Delete canonical blobs and their index refs in parallel.
            // Index deletes may double-up if `Rebuild` re-adds an entry
            // mid-prune — `Delete` is idempotent so that's fine.
            do!
                [
                    yield!
                        toDelete
                        |> List.map (fun name -> async {
                            let! _ = blobStorage.Delete(platformContainer, name)
                            return ()
                        })
                    yield! prunedEvents |> List.map removeIndexEntries
                ]
                |> Async.Parallel
                |> Async.Ignore

            return toDelete.Length
    }

    /// Prune multiple scopes in one call. Returns a map from scopeId
    /// to events pruned. Callers typically pass the list of active
    /// team scope IDs plus `_platform`.
    member this.PruneScopes(scopeIds: string seq) : Async<Map<string, int>> = async {
        let! results =
            scopeIds
            |> Seq.map (fun scope -> async {
                let! count = this.PruneScope scope
                return (scope, count)
            })
            |> Async.Parallel

        return results |> Map.ofArray
    }

    /// Re-write the by-type and by-source indexes for `scopeId` from
    /// canonical state. Idempotent — safe to re-run; safe to run
    /// concurrently with `Write`. Does NOT delete pre-existing index
    /// entries (vacuum is a separate operation, deferred). Returns
    /// the count of canonical events processed. Emits one
    /// `_platform.maintenance` event of type `IndexRebuilt` on
    /// completion (only when `selfStore` is supplied — `Rebuild` from
    /// outside the store passes `Some this`).
    member this.Rebuild(scopeId: string) : Async<int> = async {
        let! names = blobStorage.List(platformContainer, scopePrefix scopeId)
        let canonicalNames = names |> List.filter isCanonicalEventBlob
        let! events = downloadAll blobStorage canonicalNames

        do! events |> List.map writeIndexEntries |> Async.Parallel |> Async.Ignore

        // Record the rebuild itself as a maintenance event so audit
        // trails surface drift recovery.
        let payload =
            JsonSerializer.Serialize(
                {|
                    store = "events"
                    scope = scopeId
                    entryCount = events.Length
                    indexes = [ "_by-type"; "_by-source" ]
                |}
            )

        let evt = Events.create scopeId MaintenanceSourceModule "IndexRebuilt" payload
        do! (this :> IEventStore).Write evt

        return events.Length
    }

    /// Sample canonical events and index entries; verify each
    /// canonical has matching index refs and each index ref resolves
    /// to a canonical event. Returns one
    /// `IndexConsistencyEntry` per index this store maintains
    /// (`_by-type`, `_by-source`). Drift > 0 in any dimension
    /// flags a recoverable bug class — surface in `/dev/inspect`
    /// without alerting on every transient miss.
    member _.IndexConsistencyCheck(scopeId: string, sampleSize: int) : Async<IndexConsistencyEntry list> = async {
        let! names = blobStorage.List(platformContainer, scopePrefix scopeId)
        let canonicalNames = names |> List.filter isCanonicalEventBlob

        let sampleNames = canonicalNames |> List.sortDescending |> List.truncate sampleSize

        let! events = downloadAll blobStorage sampleNames
        let typeIndex = typeIndexFor scopeId
        let sourceIndex = sourceIndexFor scopeId

        let checkOneIndex (indexName: string) (keyFor: ModuleEvent -> string) (index: BlobIndex<string, Guid>) = async {
            // Canonical → index existence check
            let! canonicalChecks =
                events
                |> List.map (fun e -> async {
                    let! entries = index.Lookup(keyFor e)
                    return entries |> List.exists (fun (id, _) -> id = e.Id)
                })
                |> Async.Parallel

            let unindexedCanonicals =
                canonicalChecks |> Array.filter (fun ok -> not ok) |> Array.length

            let consistent = canonicalChecks |> Array.filter id |> Array.length

            // Sample distinct keys → canonical resolve
            let distinctKeys =
                events |> List.map keyFor |> List.distinct |> List.truncate sampleSize

            let! indexEntries = distinctKeys |> List.map (fun k -> index.Lookup k) |> Async.Parallel

            let allRefs =
                indexEntries |> Array.toList |> List.collect id |> List.truncate sampleSize

            let! resolvedRefs = resolveIndexEntries allRefs
            let orphanedIndexEntries = allRefs.Length - resolvedRefs.Length

            return {
                StoreName = "events"
                IndexName = indexName
                SampleSize = events.Length
                ConsistentEntries = consistent
                OrphanedIndexEntries = orphanedIndexEntries
                UnindexedCanonicals = unindexedCanonicals
            }
        }

        let! byType = checkOneIndex "_by-type" _.EventType typeIndex
        let! bySource = checkOneIndex "_by-source" _.SourceModule sourceIndex

        return [ byType; bySource ]
    }

    interface IEventStore with
        member _.Write(event) = async {
            let bytes = serialize event
            let! _ = blobStorage.Upload(platformContainer, blobName event, bytes)
            // Index writes are best-effort — a failure here leaves
            // canonical authoritative and surfaces as drift in
            // IndexConsistencyCheck. Don't propagate.
            try
                do! writeIndexEntries event
            with _ ->
                ()

            return ()
        }

        member _.ReadAll(scopeId) = async {
            let! names = blobStorage.List(platformContainer, scopePrefix scopeId)
            let canonicalNames = names |> List.filter isCanonicalEventBlob
            let! events = downloadAll blobStorage canonicalNames
            // Contract: reverse-chronological by OccurredAt.
            return events |> List.sortByDescending _.OccurredAt
        }

        member _.ReadByType(scopeId, eventType) = async {
            let typeIndex = typeIndexFor scopeId
            let! entries = typeIndex.Lookup eventType
            return! resolveIndexEntries entries
        }

        member _.ReadBySource(scopeId, sourceModule) = async {
            let sourceIndex = sourceIndexFor scopeId
            let! entries = sourceIndex.Lookup sourceModule
            return! resolveIndexEntries entries
        }

        member _.ListScopes() = async {
            // List every blob under `events/`. Each name has shape
            // `events/{scopeId}/{timestamp}-{eventId:N}.json` for canonical
            // events, `events/{scopeId}/_by-type/...` and
            // `events/{scopeId}/_by-source/...` for index refs. Either way
            // the second path segment is the scope id. Listing everything
            // and deduplicating in memory is cheaper than two-pass listing.
            //
            // `LocalFileStorage` returns names with the OS-native path
            // separator (backslash on Windows); cloud stores return forward
            // slashes. Split on both so the same implementation works
            // against any backend. Mirrors `BlobJobStore.ListScopesWithJobs`.
            let! names = blobStorage.List(platformContainer, "events/")

            return
                names
                |> List.choose (fun name ->
                    let parts = name.Split([| '/'; '\\' |])

                    if parts.Length >= 3 && parts[1] <> "" then
                        Some parts[1]
                    else
                        None)
                |> List.distinct
        }

        member _.Erase(scopeId, subjectUserId, policy, dryRun) = async {
            if Erasure.isBlankSubject subjectUserId then
                return
                    Result.Ok {
                        HandlerName = "events"
                        RecordsAffected = 0
                        Note = Some "blank subject — no-op (would otherwise match every event)"
                    }
            else
                match policy with
                | ErasurePolicy.RetainPerCompliance ->
                    return
                        Result.Error(
                            HandlerRefused(
                                "events",
                                "event-log retention legally overrides erasure under RetainPerCompliance"
                            )
                        )
                | _ ->
                    // Scope isolation is structural here — the blob-name
                    // prefix `events/{scopeId}/` means a List against one
                    // scope can never surface another scope's events.
                    let! names = blobStorage.List(platformContainer, scopePrefix scopeId)
                    let canonicalNames = names |> List.filter isCanonicalEventBlob
                    let! events = downloadAll blobStorage canonicalNames

                    let matched = events |> List.filter (fun e -> e.Payload.Contains subjectUserId)

                    if dryRun || List.isEmpty matched then
                        let verb =
                            match policy with
                            | ErasurePolicy.HardDelete -> "removed"
                            | _ -> "tombstoned"

                        return
                            Result.Ok {
                                HandlerName = "events"
                                RecordsAffected = matched.Length
                                Note = Some(sprintf "%d event(s) would be %s in scope %s" matched.Length verb scopeId)
                            }
                    else
                        let! outcomes =
                            matched
                            |> List.map (fun e -> async {
                                match policy with
                                | ErasurePolicy.HardDelete ->
                                    let! del = blobStorage.Delete(platformContainer, blobName e)

                                    match del with
                                    | Ok() ->
                                        // Index removal is best-effort —
                                        // canonical is authoritative; a
                                        // stale ref surfaces as drift in
                                        // IndexConsistencyCheck, not a
                                        // failed erasure.
                                        try
                                            do! removeIndexEntries e
                                        with _ ->
                                            ()

                                        return true
                                    | Error _ -> return false
                                | _ ->
                                    // Tombstone — same blob name (derived
                                    // from unchanged ScopeId/OccurredAt/Id),
                                    // index keys unchanged, so by-type /
                                    // by-source refs stay valid.
                                    let redacted = {
                                        e with
                                            Payload = Erasure.TombstoneMarker
                                    }

                                    let! up =
                                        blobStorage.Upload(platformContainer, blobName redacted, serialize redacted)

                                    return
                                        match up with
                                        | Ok _ -> true
                                        | Error _ -> false
                            })
                            |> Async.Parallel

                        let succeeded = outcomes |> Array.filter id |> Array.length
                        let failed = outcomes.Length - succeeded

                        let verb =
                            match policy with
                            | ErasurePolicy.HardDelete -> "removed"
                            | _ -> "tombstoned"

                        let summary = {
                            HandlerName = "events"
                            RecordsAffected = succeeded
                            Note = Some(sprintf "%d event(s) %s in scope %s" succeeded verb scopeId)
                        }

                        if failed = 0 then
                            return Result.Ok summary
                        else
                            return
                                Result.Error(
                                    HandlerPartialFailure(
                                        "events",
                                        summary,
                                        sprintf "%d of %d blob op(s) failed" failed outcomes.Length
                                    )
                                )
        }