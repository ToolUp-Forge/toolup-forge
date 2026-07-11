namespace ToolUp.Platform

open System

// ─── IDataObjectStore ────────────────────────────────────────────
//
// Server-side abstraction for versioned, immutable data object
// storage (Phase 7). Built on top of `IBlobStorage` — implementations
// route all I/O through that interface so swapping the blob backend
// (Azure / S3 / GCS / local disk) automatically propagates here.
//
// Versioning behaviour is selected per-object via `VersioningPolicy`,
// recorded in v1's metadata, and enforced for the object's lifetime
// (sticky policy). Content is deduplicated by SHA-256 within the same
// scope — identical bytes across versions of the same object share a
// single `_content/{hash}.data` blob.
//
// **Ordering contract.** Versions are linearly numbered within a
// single `(scopeId, objectId)` — the shard. Ordering across different
// `objectId`s or scopes is not promised (Phase 9c Rule 5). Concurrent
// saves to the same object resolve by version-collision retry; saves
// to different objects have no ordering relationship.
//
// **Stateless contract.** No method assumes in-memory state survives
// between calls. Every operation derives its result from parameters +
// `IBlobStorage`. An Orleans grain or Akka actor implementing this
// could deactivate / restart between any two calls and behave
// identically (Phase 9c Rule 4).

/// Versioned data object store. All methods are scope-isolated by
/// `scopeId`; cross-scope reads or writes are structurally impossible
/// because the implementation derives its container path from
/// `scopeId`.
type IDataObjectStore =
    /// Save content under `(scopeId, objectId)`. Returns the new
    /// `DataObject` record (which carries the assigned `Version`,
    /// `CreatedAt`, and `ContentHash`). On first save the supplied
    /// `policy` is recorded in v1's metadata and becomes sticky;
    /// subsequent saves with a different `policy` return
    /// `PolicyMismatch`.
    ///
    /// `Unversioned` overwrites v1 on each call. `Versioned` and
    /// `StrictlyVersioned` increment the version. Content blobs are
    /// deduplicated by SHA-256 within the scope: a save whose content
    /// matches an existing version's `ContentHash` writes only the
    /// metadata sidecar.
    abstract Save:
        scopeId: string *
        objectId: string *
        content: byte[] *
        dataType: string *
        createdBy: string *
        metadata: Map<string, string> *
        policy: VersioningPolicy ->
            Async<Result<DataObject, DataObjectError>>

    /// Fetch the latest version of `objectId` in `scopeId`. Returns
    /// the metadata + content bytes. `NotFound` if the object has
    /// never been saved or has been deleted.
    abstract Get: scopeId: string * objectId: string -> Async<Result<DataObject * byte[], DataObjectError>>

    /// Fetch a specific version. `VersionNotFound version` if the
    /// version does not exist; `NotFound` if the object itself does
    /// not exist.
    abstract GetVersion:
        scopeId: string * objectId: string * version: int -> Async<Result<DataObject * byte[], DataObjectError>>

    /// Fetch content bytes directly by `contentHash` within a scope.
    /// Content blobs are content-addressable and deduplicated per
    /// scope, so a caller that already holds a `DataObject` (e.g. every
    /// entry from `ListObjects`) can read its bytes with a single blob
    /// download — skipping the version-chain listing and the metadata
    /// round-trip that `Get` / `GetVersion` pay to resolve the latest
    /// version and its `ContentHash` first. Used by
    /// `SessionFileStore.loadPersistedFiles` to hydrate a scope's files
    /// (and entry sidecars) from the metadata its `ListObjects` sweep
    /// already returned. `StorageFailure` when no content blob with that
    /// hash exists in the scope — list-then-read callers treat a
    /// vanished blob as "skip", the same as a failed `Get`.
    ///
    /// Portability audit (GP 12): identity by value (string `scopeId` +
    /// `contentHash`), async at the boundary, failure as
    /// `DataObjectError` data, stateless between calls, single-scope (no
    /// cross-shard ordering claim), no precision surface.
    abstract GetContent: scopeId: string * contentHash: string -> Async<Result<byte[], DataObjectError>>

    /// Return all versions' metadata for an object, sorted ascending
    /// by `Version`. Empty list when the object does not exist —
    /// callers that need to distinguish "no versions" from "exists
    /// with one version" should check `List.length`.
    abstract ListVersions: scopeId: string * objectId: string -> Async<DataObject list>

    /// Return the latest version's metadata for every object in the
    /// scope. Used by `SessionFileStore.loadPersistedFiles` to
    /// enumerate uploads on server restart, and by Phase 7a's data
    /// catalog (which filters this list by `DataType`). The order is
    /// not specified; callers that need an ordering should sort
    /// themselves. Reserved internal entries (the `_content` dedup
    /// pool) are filtered out.
    abstract ListObjects: scopeId: string -> Async<DataObject list>

    /// Restore a prior version's content as the new latest version.
    /// History is never rewritten: this writes a fresh metadata blob
    /// at `max + 1` whose `ContentHash` points to the recovered
    /// version's content. The new version's `CreatedBy` is the
    /// recoverer (passed in here), not the original author — the
    /// original author is preserved on the immutable
    /// `v{originalVersion}.json` blob. The recovered version's
    /// `Metadata` is preserved verbatim, with
    /// `_recovered_from = "v{originalVersion}"` added to the new
    /// version's map.
    abstract Recover:
        scopeId: string * objectId: string * version: int * createdBy: string ->
            Async<Result<DataObject, DataObjectError>>

    /// Delete all versions of `objectId` in `scopeId`. Returns
    /// `DeleteForbidden` for `StrictlyVersioned` objects. Idempotent
    /// — deleting an object that does not exist returns `Ok ()`.
    /// Garbage-collects any `_content/{hash}.data` blobs that are no
    /// longer referenced by any other object in the scope.
    abstract Delete: scopeId: string * objectId: string -> Async<Result<unit, DataObjectError>>

    /// Evict all versions of `objectId` in `scopeId`, **bypassing the
    /// `StrictlyVersioned` delete protection that `Delete` enforces.**
    /// Unlike `Delete` (which refuses `StrictlyVersioned`), `Evict` is
    /// an explicit single-object lifecycle operation for callers that
    /// own the object's retention — e.g. a cache layer over
    /// `IResultStore` that has decided the audit-retention guarantee
    /// does not apply to its entries. This is the same deliberate-
    /// operator-choice reasoning that lets `Purge` (scope-wide) and
    /// `Erase HardDelete` (subject-wide) override the same guard; this
    /// completes the set with the per-object axis. Idempotent —
    /// evicting an absent object returns `Ok ()`. Garbage-collects any
    /// `_content/{hash}.data` blobs no longer referenced by any other
    /// object in the scope.
    ///
    /// Portability audit (GP 12): identity by value (string `scopeId`
    /// + `objectId`), async at the boundary, failure as
    /// `DataObjectError` data (no callback), stateless between calls,
    /// single-scope (no cross-shard ordering claim), no precision
    /// surface. Callers that require strict immutability simply do not
    /// call `Evict` — the `Delete` guard remains the default.
    abstract Evict: scopeId: string * objectId: string -> Async<Result<unit, DataObjectError>>

    /// Remove every object and dedup blob under `scopeId`. Used by
    /// `SessionFileStore`'s ephemeral-scope eviction path. Idempotent.
    /// Bypasses `StrictlyVersioned` delete protection — eviction is a
    /// scope-lifecycle operation, not a per-object one. Callers that
    /// require strict immutability must not place those objects in
    /// ephemeral scopes (anonymous / authenticated-ephemeral modes).
    abstract Purge: scopeId: string -> Async<Result<unit, DataObjectError>>

    /// Phase 9h — GDPR Article 17 erasure surface. Erase (or redact)
    /// every object in `scopeId` that names `subjectUserId`,
    /// interpreting `policy` per the data-object store's semantics:
    ///
    ///  - `HardDelete` — delete every version of every matched object
    ///    and GC its now-orphaned content. Bypasses the
    ///    `StrictlyVersioned` delete protection: a verified Article 17
    ///    demand under an explicit `HardDelete` policy is a deliberate
    ///    operator choice that overrides the retention guarantee (the
    ///    same scope-lifecycle reasoning as `Purge`).
    ///  - `Tombstone` — rewrite every version's metadata sidecar:
    ///    redact `CreatedBy` + any matching metadata value to
    ///    `Erasure.TombstoneMarker`, and repoint `ContentHash` at a
    ///    tombstone content blob (the user's bytes are removed; old
    ///    content GC'd). Version numbers + chain preserved; the
    ///    erasure is discoverable.
    ///  - `RetainPerCompliance` — redact the structured identifiers
    ///    the store *can* see (`CreatedBy` + matching metadata
    ///    values) but leave content + version chain intact. For
    ///    regimes where the record must be retained but identifying
    ///    fields redacted "where possible".
    ///
    /// **Subject match.** Content bytes are opaque (possibly binary)
    /// and are NOT scanned. An object "names the subject" when any of
    /// its versions has `CreatedBy = subjectUserId` or a `Metadata`
    /// value containing `subjectUserId`. Declared precision:
    /// structured-fields only. A blank `subjectUserId` is a
    /// zero-count no-op.
    ///
    /// **Scope isolation (GP 4).** The container is derived from
    /// `scopeId`; another scope's objects are structurally
    /// unreachable.
    ///
    /// `dryRun = true` counts affected objects without mutating.
    /// Portability audit (GP 12): identity by value, async at
    /// boundary, failure as `ErasureError`, stateless between calls,
    /// single-scope, precision declared above.
    abstract Erase:
        scopeId: string * subjectUserId: string * policy: ErasurePolicy * dryRun: bool ->
            Async<Result<ErasureSummary, ErasureError>>

// ─── IObjectCounter (Phase 171) ──────────────────────────────────
//
// Optional capability a data-object store MAY implement to count
// objects of a given `dataType` within a scope **without
// materialising them** (a SQL `COUNT`, a blob-index size, a
// maintained counter). `IDataCatalog.CountObjects` uses it when the
// underlying store implements it, and otherwise falls back to
// `ListObjects |> List.length` — correct-by-default, efficient-where-
// possible (GP 12).
//
// Kept as a *separate optional* interface rather than a new abstract
// member on `IDataObjectStore`: F# interfaces cannot carry a default
// body, so widening `IDataObjectStore` would break every existing
// implementation (including external ones) for a count that the
// catalog can already derive. Stores that can count cheaply opt in;
// everyone else is unaffected (GP 11).

/// Optional fast-path counting capability. A store implements this
/// when it can count objects of a type without enumerating them.
type IObjectCounter =
    /// Number of stored objects whose `DataType` matches `dataType`
    /// in `scopeId`. Scope-isolated by the same structural derivation
    /// as `IDataObjectStore.ListObjects` (GP 4).
    abstract CountObjects: scopeId: string * dataType: string -> Async<int>

// ─── IContentRangeReader (Phase 448.D follow-on) ─────────────────
//
// Optional capability a data-object store MAY implement to read a
// bounded byte range of a content blob **without materialising the
// whole blob** (a native blob range request / a file seek — the
// Phase 455 `IBlobStorage.DownloadRange` primitive). The blob-backed
// dataset store uses it — together with a streaming-capable codec —
// to page large dataset vintages without downloading the full
// content; callers whose store lacks it, or whose backing storage
// refuses ranged reads (the whole-blob-AES-GCM encryption decorator),
// fall back to `GetContent` — correct-by-default, efficient-where-
// possible, the same shape as `IObjectCounter` above.
//
// Kept as a *separate optional* interface rather than new abstract
// members on `IDataObjectStore` for the same reason as
// `IObjectCounter`: F# interfaces cannot carry a default body, so
// widening `IDataObjectStore` would break every existing
// implementation (including external ones) for reads that
// `GetContent` already serves correctly (GP 11).

/// Optional bounded ranged-read capability over the content-addressed
/// dedup pool. A store implements this when its backing storage
/// serves byte ranges natively (`IBlobStorage.DownloadRange`).
type IContentRangeReader =
    /// Size in bytes of the content blob addressed by `contentHash`
    /// in `scopeId`, without downloading it. `StorageFailure` when no
    /// such content blob exists. Over an encrypting blob decorator
    /// the reported size is the *ciphertext* size — callers treat the
    /// decorator's `ReadContentRange` refusal as the signal to fall
    /// back to `GetContent`, never this size alone.
    abstract GetContentSize: scopeId: string * contentHash: string -> Async<Result<int64, DataObjectError>>

    /// Read at most `length` bytes starting at byte `offset` from the
    /// content blob addressed by `contentHash`. Semantics mirror
    /// `IBlobStorage.DownloadRange` (contract-tested there):
    /// `offset < 0` / `length <= 0` → error; past-EOF → `Ok [||]`;
    /// the result may be shorter than `length` at the end of the
    /// blob; concatenating consecutive ranges byte-equals
    /// `GetContent`. Scope-isolated by the same structural container
    /// derivation as `GetContent` (GP 4). Portability audit (GP 12):
    /// identity by value, async at the boundary, failure as
    /// `DataObjectError` data, stateless between calls, single-scope,
    /// no precision surface.
    abstract ReadContentRange:
        scopeId: string * contentHash: string * offset: int64 * length: int -> Async<Result<byte[], DataObjectError>>