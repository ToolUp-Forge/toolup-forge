# ToolUp.Platform Technical Guide — 04. Data & Storage Substrate

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 3. Authentication, Secrets & Encryption](03-authentication-secrets-and-encryption.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 5. Audit, Health & Metrics →](05-audit-health-and-metrics.md)

---

## Persistent Event Store

`IEventStore` has two shipped implementations. Apps choose via `ServerConfig.EventStore`:

```fsharp
type EventStoreMode =
    | InMemoryOnly                                  // default
    | PersistentBlobBacked of EventRetentionPolicy
```

### Storage layout

`PersistentEventStore` writes one JSON blob per event under a fixed path in the reserved `_platform` container:

```
_platform/events/{scopeId}/{yyyy-MM-ddTHH-mm-ss-fffffffZ}-{eventId:N}.json
```

The timestamp prefix makes blob listing lexicographically sortable by `OccurredAt`. The event-id suffix disambiguates writes that land in the same 100-ns tick (real under load on Windows's `DateTime.UtcNow` resolution). Colons are replaced by hyphens so every blob backend accepts the name.

Scope isolation is **structural**: reads list `events/{scopeId}/` only, so scope-B events cannot be returned by a scope-A query even if a deserialiser bug loses the in-blob `ScopeId` field. The reserved `_platform` scope holds SDK-level events that span all tenants (audit, health).

### Concurrent writes

Blob stores offer no cross-provider atomic append. Every event writes to a fresh unique blob name (timestamp + GUID), so concurrent writers cannot collide. Reads download blobs in parallel via `Async.Parallel` and skip individual download failures — a partial read is preferable to a total failure because event history is advisory, not critical-path.

### Retention

```fsharp
type EventRetentionPolicy = {
    MaxAge: TimeSpan option
    MaxCountPerScope: int option
}
```

Both dimensions are optional and independent — either triggers pruning. `unlimited` = retain forever (suitable when the event log is the audit trail). `ninetyDays` = 90-day age cap. `byCount n` = keep the most recent `n` per scope.

Pruning is **not automatic**. Writes never block on retention checks. Apps schedule `store.PruneScope scopeId` / `store.PruneScopes scopeIds` calls from:

- **Startup**: prune once during server bring-up (cheap if no sweep is overdue).
- **Phase 9b background jobs**: run periodically against `TeamStore.ListAllTeamIds()` + `_platform`.
- **Bundle pipeline**: include a prune step in `dotnet run -- Bundle` to keep dev environments trim.

### Replay

```fsharp skip=signature
module EventReplay =
    val foldScope:
        store:IEventStore -> scopeId:string
            -> initialState:'S -> folder:('S -> ModuleEvent -> 'S)
            -> Async<'S>

    val foldScopeOfType:
        store:IEventStore -> scopeId:string -> eventType:string
            -> initialState:'S -> folder:('S -> ModuleEvent -> 'S)
            -> Async<'S>
```

`IEventStore.ReadAll` returns reverse-chronological (latest first) for convenient display. Replay wants chronological — `EventReplay` re-sorts ascending by `OccurredAt` before folding, so consumers write a natural left-fold over past events.

### Where this will go next

- Phase 8 (result persistence) layers a typed `IResultStore` on top of `IEventStore`, emitting `ResultSaved` events when modules persist analytical outputs.
- Phase 8a (lineage) joins events and results into a traversable lineage graph — "where did this number come from?" — keyed on events emitted during result persistence.

## Data Object Store (Phase 7)

`IDataObjectStore` is the versioned-storage substrate for everything that needs immutability or history: `SessionFileStore` uploads (with `Unversioned` policy), Phase 8 analytical results (with `StrictlyVersioned`), future audit artefacts. It is **a thin layer over `IBlobStorage`** — there is no separate persistence mechanism — and it is **stateless between calls** so a distributed implementation (Orleans grain, Akka actor) can deactivate / restart between any pair of operations and behave identically.

```fsharp
type DataObject = {
    ObjectId: string
    Version: int
    CreatedAt: DateTime
    CreatedBy: string
    ScopeId: string
    DataType: string
    ContentHash: string
    Policy: VersioningPolicy
    Metadata: Map<string, string>
}

type VersioningPolicy =
    | Unversioned         // overwrite-on-save (legacy file-upload semantics)
    | Versioned           // append-only, Delete allowed
    | StrictlyVersioned   // append-only, Delete forbidden
```

### Storage layout

```
{container}/objects/{objectId}/v{N}.json     # metadata sidecar (DataObject record, JSON)
{container}/objects/_content/{hash}.data     # content bytes, deduplicated by SHA-256 hex
```

The container is scope-derived (`team-{teamId}` / `user-{userId}` / `session-{sessionId}`) — not a global pool. Identical content uploaded by team A and team B is stored twice, once in each team's container. **Team isolation is structural**, not defensive: `_content/` lives inside each scope's container, and the only way a save in scope B could read scope A's bytes is if `IBlobStorage.Download(team-A, _)` were called from a scope-B handler — which the per-scope container resolution prevents.

The reserved object id `_content` cannot be saved by callers (`Save` returns `StorageFailure`).

### Sticky policy

Policy is recorded on v1's metadata and fixed for the object's lifetime. A `Save` whose `policy` parameter doesn't match v1's policy returns `Error (PolicyMismatch (recorded, supplied))`. Policy is also written to **every subsequent version's metadata** so a single-blob read of any version answers "is this object immutable?" — Phase 7a's catalog and admin UIs lean on this.

### Content-addressable dedup

Save flow:

1. `hash = SHA-256-hex(content)`.
2. Validate sticky policy by reading v1.
3. Determine next version: `Unversioned` → always 1; `Versioned` / `StrictlyVersioned` → max(existing) + 1.
4. **Idempotent content upload**: `Exists`-probe `_content/{hash}.data`; only `Upload` when missing. Identical content across versions writes only the metadata sidecar.
5. Upload metadata sidecar at `objects/{objectId}/v{N}.json`.

`Get` reads the latest metadata, then downloads `_content/{ContentHash}.data` — two blob reads per fetch. The size cost of the sidecar (a few hundred bytes) is mild; the dedup win on identical-content versions is large for typical file-upload workloads.

### Recovery semantics

`Recover(scopeId, objectId, version, createdBy)` restores a prior version's content as the new latest version:

- The recovered version's metadata is preserved verbatim — the **immutable v{originalVersion}.json blob is never modified**, including its original `CreatedBy`.
- The new version's `ContentHash` reuses the recovered version's hash (no new content blob written — already in `_content/`).
- The new version's `CreatedBy` is the recoverer (the parameter), not the original author.
- The new version's `Metadata` is the recovered version's `Metadata` plus `_recovered_from = "v{originalVersion}"`.

This shape is what regulated-sector audits care about: "this is the current state, this is who restored it, the previous state is intact, the original author's record is unchanged."

### Delete and orphan GC

`Delete(scopeId, objectId)`:

- Reads v1's policy. `StrictlyVersioned` → `Error DeleteForbidden`.
- Otherwise lists every `v*.json`, deletes them in parallel.
- **Orphan GC**: enumerates surviving `v*.json` across the entire scope, collects referenced `ContentHash` values, deletes any `_content/{hash}.data` not referenced. Runs at the end of every successful Delete. O(total objects in scope) per Delete — acceptable given the typical scope size; can be hoisted to a background sweep if it becomes a bottleneck.
- Idempotent: deleting a non-existent object returns `Ok()`.

`Purge(scopeId)` wipes the entire scope (every `objects/*/v*` and every `_content/*`). Bypasses `StrictlyVersioned` protection — purge is a scope-lifecycle operation (user/team destruction), not a per-object one. Callers requiring strict immutability must not place those objects in ephemeral scopes; the documentation on `IDataObjectStore.Purge` makes this explicit.

### Concurrency

Concurrent saves to the same `(scopeId, objectId)` race on "max version + 1": both threads list `v*.json`, both compute the same `N+1`, both write `v{N+1}.json`. The loser's metadata is overwritten but the loser's content is dedup'd into `_content/`, so **no bytes are lost** — they're just unreferenced by any metadata. Single-writer-per-object is the common case.

This is **within-shard ordering only** (Phase 9c Rule 5). Concurrent saves to different objects, or in different scopes, have no ordering relationship.

### Migration of `SessionFileStore`

Pre-Phase 7, `SessionFileStore` persisted at `{container}/{fileName}` via `IBlobStorage.Upload`. Phase 7 (full migration) routes persistence exclusively through `IDataObjectStore.Save(scope.Container, fileName, bytes, dataType, createdBy, Map.empty, Unversioned)`:

- HTTP API surface (`UploadFile` / `ListFiles` / `GetFileContent` / `DeleteFile`) is unchanged.
- Blob layout under the hood becomes `{container}/objects/{fileName}/v1.json + .data` — visible to Phase 7a's catalog at exactly the same path Phase 8's `IResultStore` will use.
- Existing test suite (119 tests, including all `SessionFileStore`-adjacent paths) passes unchanged.
- `AddFile` gained a `createdBy: string` parameter so the audit trail records the actual uploader; the single external caller (`fileManagementApi`) passes the resolved `userId`.
- Eviction (`evictExpiredStores`) preserves existing semantics — ephemeral scopes never had disk persistence in the first place (`scope.Persist = false` → `dataObjectStore = None` at construction), so nothing on disk to clean. `IDataObjectStore.Purge` exists for future scope-deletion flows where disk artifacts need wiping.

### Where this will go next

- ~~Phase 7a (data catalog) wraps `ListObjects(scopeId)` with type-filtering~~ — shipped, see "Data Catalog (Phase 7a)" below.
- Phase 8 (analytical results) layers `IResultStore` over `IDataObjectStore` with `StrictlyVersioned` as the default policy and `ResultSaved` events emitted via `IEventStore`.
- Phase 8a (lineage) emits `LineageLink` events naming `(objectId, version)` tuples — the immutable versioned identity is what makes the graph traversable post-mortem.

## Data Catalog (Phase 7a)

`IDataCatalog` is the discoverability layer over the data types a deployment supports. It answers four questions: "what types are registered?", "what columns does this type have?", "which modules produce this type?", and "what objects of this type exist in this scope?". Surfaces in `DataCatalogApi.GetDataCatalog` for admin UIs and (via DI resolution) AI tools that enumerate available data shapes.

```fsharp
type DataTypeInfo = {
    Id: DataTypeId
    DisplayName: string
    Schema: DataTypeSchema option   // Phase 7a — optional per module
}

type DataTypeSchema = { Description: string; Columns: DataTypeColumn list }
type DataTypeColumn = { Name: string; Type: ColumnType; Required: bool; Description: string option }
type ColumnType = StringColumn | NumberColumn | DateColumn | BooleanColumn

type IDataCatalog =
    abstract ListTypes: unit -> Async<DataTypeInfo list>
    abstract GetSchema: typeId: DataTypeId -> Async<DataTypeSchema option>
    abstract GetProducers: typeId: DataTypeId -> Async<string list>
    abstract ListObjects: scopeId: string * typeId: DataTypeId -> Async<DataObject list>
```

### How it gets the registrations

`ServerApp.addModule` accumulates `DataTypeRegistrations: (string * DataType) list` — one pair per data type each module declares, paired with the module's `Name`. `ServerApp.run` passes the list to `compose`, which constructs the `DataCatalog` and registers it in DI alongside `IDataObjectStore`. `composeWithAI` (`ToolUp.AI`) and `composeWithRAG` (`ToolUp.RAG`) thread the same registrations through to `compose`.

The registration set is fixed for the server's lifetime — modules are loaded once at startup and never added later. The catalog snapshots the `(moduleName, DataType) list` at construction; queries derive results from the snapshot + `IDataObjectStore`. **Stateless between calls** (Phase 9c Rule 4): a distributed implementation could deactivate / restart between any two operations.

### Deduplication and multi-producer types

If two modules declare a `DataType` with the same `Id`, `ListTypes` returns one `DataTypeInfo` (the first declared) and `GetProducers(id)` returns both module names. This supports cross-module shared shapes (e.g., a `MarketingMix` type produced by both `MediaOptimisation` and `BudgetForecast`). `GetSchema` resolves to the first non-`None` schema across all declarations — modules with overlapping ids should agree on schema, but the catalog tolerates only one of them publishing.

### Schema is opt-in

Modules ship `Schema = None` until they document their column shape. The catalog returns `None` from `GetSchema` for those types — admin UIs and AI tools render a "schema not published" placeholder. Adding a real schema later is a non-breaking change: every other call site already handles the `option`.

### `ListObjects` integration with `IDataObjectStore`

`ListObjects(scopeId, typeId)` is a thin wrapper: `IDataObjectStore.ListObjects(scopeId)` returns every latest-version metadata blob in the scope; the catalog filters to `obj.DataType = typeId`. Scope isolation is preserved end-to-end (the catalog never reads cross-scope). For `typeId` values that aren't registered, the filter still applies — but the catalog doesn't validate `typeId` against `ListTypes()`; an unknown type id legitimately returns an empty list (zero-cost discoverability for "is anything stored as X?").

### `DataCatalogApi.GetDataCatalog` shape

```fsharp
type DataTypeCatalogEntry = { Info: DataTypeInfo; Producers: string list }
type DataCatalogResponse = { Types: DataTypeCatalogEntry list }
```

The handler resolves `IDataCatalog` from `ctx.RequestServices` per request (rather than capturing it in a closure) so a `ComposeExtensions.ServiceConfig` could swap in a richer implementation without touching the handler. Producers are queried in parallel via `Async.Parallel`.

`ListObjects` is intentionally NOT exposed via `DataCatalogApi.GetDataCatalog` — no current external consumer needs it. AI tools and server-side handlers that want it resolve `IDataCatalog` from DI directly.

## Result Store and Lineage (Phases 8 / 8a)

Phases 8 (analytical-result persistence) and 8a (lineage tracking) are **opt-in and deployment-configurable**. Apps that don't want either feature pay nothing — no DI registration, no event emission, no API surface added. The toggle is on `ServerConfig`:

```fsharp
type ResultStoreMode =
    | NoResultStore           // default — interface unregistered
    | InMemoryResultStore     // dev/test — in-process dictionary
    | PersistentResultStore   // production — wraps IDataObjectStore

type LineageStoreMode =
    | NoLineageStore          // default — interface unregistered
    | EnabledLineageStore     // queries over IEventStore + auto-emit on SaveResult

type ServerConfig = {
    // ...
    ResultStore: ResultStoreMode    // default NoResultStore
    Lineage: LineageStoreMode       // default NoLineageStore
}
```

`compose` reads these and registers conditionally. Modules that want results call `ctx.RequestServices.GetService<IResultStore>()` — `null` resolution means the deployment opted out, so they fall back to local computation.

### `IResultStore` shape

```fsharp skip=fragment
type IResultStore =
    abstract SaveResult:
        scopeId * moduleName * resultType * content * createdBy * inputs: string list
            -> Async<Result<DataObject, DataObjectError>>
    abstract GetLatest: scopeId * moduleName * resultType
        -> Async<Result<DataObject * byte[], DataObjectError>>
    abstract ListResults: scopeId * moduleName * dateRange: (DateTime * DateTime) option
        -> Async<DataObject list>
    abstract CompareVersions: scopeId * objectId * v1 * v2
        -> Async<Result<ResultDiff, DataObjectError>>
```

**ObjectId convention.** `ResultObjectId.make moduleName resultType = "{moduleName}__{resultType}"`. The `__` separator (not `/`) is deliberate — `IDataObjectStore`'s blob layout (`{container}/objects/{objectId}/v{N}.json`) treats slashes as path segments, so an objectId containing `/` would leak into the blob path and break `parseObjectId`. The `__` separator is alphanumeric-safe across every blob backend.

The same module repeatedly saving the same result type produces a single versioned object whose history is the audit trail. Modules that need multiple distinct results of the same type use different `resultType` values (`"q1-sales"`, `"q2-sales"`).

### Two backends

`InMemoryResultStore` uses a `ConcurrentDictionary<(scopeId, objectId), version-list>` with a per-key lock around the append to keep version numbers monotonic under concurrent saves. **Stateful between calls** — documented dev-only Rule 4 exception (same shape as `LocalEmbeddingProvider`).

`PersistentResultStore` is stateless. Every `SaveResult` calls `IDataObjectStore.Save` with `StrictlyVersioned` policy hardcoded; metadata gains `["moduleName", "resultType"]` keys so admin UIs and catalog queries can introspect result origin without parsing the ObjectId. `GetLatest` / `ListResults` / `CompareVersions` delegate down through `IDataObjectStore.Get` / `ListObjects` / `ListVersions` / `GetVersion`.

### `AnalysisCompleted` events

Both backends emit `AnalysisCompleted` to `IEventStore` after every successful save:

```fsharp skip=fragment
{
    Id = Guid.NewGuid()
    OccurredAt = DateTime.UtcNow
    ScopeId = scopeId
    SourceModule = moduleName
    EventType = ResultEventType.AnalysisCompleted
    Payload = """{"objectId":"…","version":N,"moduleName":"…","createdBy":"…"}"""
}
```

Emission is **fire-and-forget** via `Async.Start` — a slow event store doesn't block the result-save path. Errors logged inside `IEventStore` itself.

### JSON diff

`CompareVersions` parses both versions' content as `System.Text.Json.JsonDocument` and walks them in parallel:

- Object: union of keys, recurse per key. Missing on one side surfaces as `From = None` (added) or `To = None` (removed).
- Array: index-by-index. Reordering produces every-element changes (acceptable MVP; richer array-diff is a follow-up).
- Leaves: compare raw text (so numerics keep their canonical form).

Paths are JSON-pointer-like dotted: `summary.revenue`, `rows[2].region`. Non-JSON content returns `Error (StorageFailure "diff requires JSON content: ...")`.

### Lineage as a query layer

`ILineageStore` has no dedicated persistence. Every `Record` call writes a `ModuleEvent` to `IEventStore` with `EventType = "LineageLink"` and `SourceModule = "_platform.lineage"` (constants in `Shared/LineageTypes.fs`). Queries fan out from `IEventStore.ReadByType(scopeId, "LineageLink")`, deserialize payloads (the STJ `ToolUp.Remoting.Json.SystemTextJson.FableConverters` options for lossless `LinkType` DU round-trip), and walk the in-memory edge list:

- `GetAncestors` / `GetDescendants`: BFS from the root in the chosen orientation. Visited-link set prevents cycles. Producer `ModuleName` is recorded against the node on the link's `ToObjectId`; upstream-only nodes get `ModuleName = None`.
- `GetPath`: BFS along outgoing edges (`From -> To`), returning the shortest edge-path or `None`.

**Stateless between calls** — every method derives its result from `IEventStore` reads. No in-memory cache, no shared mutable state. Cross-scope leakage structurally impossible because `IEventStore.ReadByType` is per-scope.

### Auto-emit on `SaveResult`

Both `IResultStore` backends accept an `inputs: string list` parameter. The `Events.recordLineage` helper checks whether `ILineageStore` is registered:

- **`EnabledLineageStore`**: emit one `LineageLink` per input via `ILineageStore.Record` after the save succeeds. The link's `ModuleName` is the saver; `LinkType` defaults to `Derived`.
- **`NoLineageStore`**: silently no-op.

Modules call `SaveResult(... inputs)` the same way regardless of deployment config. The `inputs` parameter is ergonomic at the module's call site even when nothing consumes it.

### When modules call `IResultStore`

```fsharp
// Server-side module handler
let resultStore =
    match ctx.RequestServices.GetService(typeof<IResultStore>) with
    | :? IResultStore as s -> Some s
    | _ -> None

let! analysisResult = computeAnalysis input
match resultStore with
| None -> () // deployment opted out
| Some store ->
    let bytes = JsonSerializer.SerializeToUtf8Bytes analysisResult
    let inputs = [ inputFileObjectId ]  // produces a LineageLink when 8a is enabled
    let! _ = store.SaveResult(scopeId, "SalesAnalysis", "QuarterlyReport", bytes, userId, inputs)
    ()

return analysisResult
```

The `null` branch is the cost of opt-in. Modules that want guaranteed availability can require it via DI throw rather than null-check; that's a per-module decision.

## Blob conditional writes — the ETag seam (Phase 600)

`IConditionalBlobStorage` (in `ToolUp.Platform.Core`, beside `IBlobStorage`) is the opt-in optimistic-concurrency capability for blob backends: `DownloadWithETag` returns content plus an **opaque** etag token; `UploadWithETag` writes under `IfMatch etag` (read-modify-write guard) or `IfAbsent` (create-only / first-writer-wins), refusing with a typed `ETagMismatch currentETag` that leaves the stored blob untouched. It is deliberately a *separate capability interface*, not new `IBlobStorage` members — thirteen in-tree implementers (plus consumer-side custom stores) would otherwise face a breaking sweep. Consumers probe:

```fsharp skip=fragment
match blobStorage with
| :? IConditionalBlobStorage as cas -> // ETag-guarded CAS path
| _ -> // fallback: per-key serialisation (the standing interim guard)
```

In-tree support: `LocalFileStorage` (content-hash etags; CAS under per-path lock striping — single-process, matching its DevOnly posture), the shared `InMemoryBlobStorage` test double, and both decorators (`EncryptedBlobStorage` forwards with envelope encryption — etags are over ciphertext, still opaque; `ResilientBlobStorage` forwards but deliberately does **not** retry conditional uploads, since a retry after an ambiguous failure can observe its own write and false-conflict). All three cloud companions implement the seam natively: `AwsS3Storage` via S3 conditional PUT (`If-Match` / `If-None-Match: *`; note S3-compatible stores vary in support — the env-gated contract arm is the per-endpoint check), `AzureBlobStorage` via `BlobRequestConditions` ETag preconditions, and `GoogleCloudStorage` via generation-match preconditions (its opaque token is the object generation number, not an HTTP ETag — tokens are opaque per-provider, so callers never notice). A refused vendor precondition (412 / 409) maps to `ETagMismatch` with the live etag recovered by a follow-up metadata read; `ETagMismatch None` is reported only when the blob is absent. The `IConditionalBlobStorageContract` pack is the conformance bar for any external implementation; the cloud bindings run env-gated (same credentials as the `IBlobStorage` cloud arms) and skip clean without credentials.

## Entity-write outbox (Phase 599)

An entity save and the `IEventStore` events it implies are two writes with no transaction between them — a crash in the gap leaves state and event log divergent (and, for `OnEvent`-triggered jobs, a lost trigger). `ServerConfig.EntityOutbox = EnabledEntityOutbox` (fluent: `ServerApp.withEntityOutbox true`; env: `TOOLUP_ENTITY_OUTBOX`; requires `EntityStore = EnabledEntityStore`) registers `EntityOutbox.OutboxEntityStore` in DI for mutations whose events must not be lost:

```fsharp skip=fragment
let outbox = ctx.RequestServices.GetRequiredService<EntityOutbox.OutboxEntityStore>()
let! result = outbox.SaveWithEvents(scopeId, "StockMovement", movementId, movement, [ movedEvent ])
```

**Mechanism — write-ahead intent + version witness.** `SaveWithEvents` (1) stages an intent blob — the events plus `MinVersionAfterSave = head + 1` observed before the save — in a single atomic blob write under `_platform/entity-outbox/{scope}/`; (2) saves the entity through the ordinary `IEntityStore` (payload byte-identical to a plain save — no envelope, no read-path stripping); (3) publishes the events and deletes the intent, best-effort. `EntityOutboxRelayService` (60-second cadence, gated as `EntityOutboxRelaySubsystem`) recovers both crash shapes: an intent whose entity head version reached the witness is published (the save committed — late, not lost); an unwitnessed intent older than a 10-minute abandon window is discarded *unpublished* — no ghost events for saves that never happened. A save that fails outright withdraws its intent immediately; a staging failure refuses the whole mutation rather than silently re-opening the dual-write gap.

**Semantics.** Events are at-least-once (a crash between publish and intent delete re-publishes; consumers dedupe by `ModuleEvent.Id`). The witness discrimination assumes the application serialises writes per entity id — the store's standing pre-CAS assumption. Corrupt intents quarantine under `entity-outbox-poison/` and never wedge the drain.


---

> [← Prev: 3. Authentication, Secrets & Encryption](03-authentication-secrets-and-encryption.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 5. Audit, Health & Metrics →](05-audit-health-and-metrics.md)
