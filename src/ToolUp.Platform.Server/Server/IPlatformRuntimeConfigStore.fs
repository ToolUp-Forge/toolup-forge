namespace ToolUp.Platform

// ─── IPlatformRuntimeConfigStore ──────────────────────────────────────
//
// Phase 4b deferred follow-up — runtime mutation surface for
// `ServerConfig` fields amenable to per-deployment tuning. v1 covers
// `PlatformKnowledgeBase` only; the same interface accommodates any
// future field by extending the contract additively.
//
// The store layers on top of `ServerConfig`:
//   - `ServerConfig.PlatformKnowledgeBase` is the BOOT-TIME default
//     (deployed by the operator's build).
//   - The runtime store persists the OVERRIDE (set by Platform Admins
//     via `PlatformAdminApi.SetPlatformKnowledgeBase`). When present,
//     the override wins; when absent, the boot-time default applies.
//   - Persisted at `_platform/runtime-config.json`. Survives restarts.
//
// **Hot-path read.** `Snapshot` returns the current value synchronously
// from an in-memory cell (initialised at startup, updated on `Set`).
// `RetrievalPipeline.authorisedScopes` reads via `Snapshot` per request
// — no I/O on the hot path. The async `GetPlatformKnowledgeBase`
// accessor exists for API handlers and tests; production hot paths
// always use `Snapshot`.
//
// **Phase 9c portability rules** (all six honoured):
//
//   1. Identity by value. The store carries no live handles — pure
//      configuration values.
//   2. Async at every boundary. `Get` / `Set` return `Async<_>`;
//      `Snapshot` is the documented sync exception (in-memory cell read,
//      same precedent as `IMetricsSink.Increment` from Phase 9e).
//   3. Retry / supervision as data. Failures returned as `Result`.
//   4. Stateless handlers between invocations. Reads re-resolve the
//      in-memory cell; distributed companions back the cell with a
//      pub/sub mechanism so other nodes see updates within an
//      eventual-consistency window.
//   5. No cross-shard ordering promises. Single deployment-wide
//      configuration; no shard concept.
//   6. Precision N/A — config carries no temporal contract.
//
// **Single-instance limitation.** The default `BlobBackedPlatformRuntimeConfigStore`
// is single-instance: a `Set` call updates the local cell + the blob,
// but other replicas reading from their own cells don't see the
// update until they restart or read the blob. Same constraint as
// every other Phase 4b store — flagged for the planned Phase 9c half
// 2 distributed companion.

type IPlatformRuntimeConfigStore =
    /// Synchronous read of the current `PlatformKnowledgeBase` mode.
    /// Used by `RetrievalPipeline.authorisedScopes` on the request hot
    /// path — no I/O. Returns the in-memory cell value, which is kept
    /// in sync with the persisted blob by `SetPlatformKnowledgeBase`.
    abstract Snapshot: unit -> PlatformKnowledgeBaseMode

    /// Async read of the current `PlatformKnowledgeBase` mode. Same
    /// value as `Snapshot` — exists for consistency with the API
    /// contract (`PlatformAdminApi.GetPlatformKnowledgeBase` returns
    /// `Async<_>` because Fable.Remoting requires it).
    abstract GetPlatformKnowledgeBase: unit -> Async<PlatformKnowledgeBaseMode>

    /// Update the runtime `PlatformKnowledgeBase` mode. Persists to
    /// blob and updates the in-memory cell. Permission checks happen
    /// at the API handler level — the store is unconditional. Returns
    /// `Result.Error` on persistence failure (the in-memory cell is
    /// only updated on successful save, so a failed `Set` leaves the
    /// previous value intact).
    abstract SetPlatformKnowledgeBase: mode: PlatformKnowledgeBaseMode -> Async<Result<unit, string>>