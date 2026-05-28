module ToolUp.Platform.IVectorStore

open System
open ToolUp.Platform.VectorKnowledgeTypes

/// Persistent storage for embedded text chunks. Implementations are companion
/// packages under `src/VectorStores/`; the interface carries no store-specific
/// types or dependencies.
///
/// All operations are scope-aware — chunks in one scope are never accessible
/// from another. `IRetrievalPipeline` validates `AccessContext` permissions
/// before calling any method; direct callers are responsible for their own
/// access enforcement.
///
/// **Soft-delete contract (Phase 14h).** `DeleteChunk` is a tombstone write
/// — it stamps the chunk's metadata with `_deletedAt` (ISO 8601 UTC) but
/// keeps the underlying entry. `Search` filters tombstoned chunks; `ListChunks`
/// honours an explicit include-deleted flag for re-embedding workflows;
/// `RestoreChunk` removes the tombstone within the retention window;
/// `Vacuum` hard-deletes entries whose tombstones predate `olderThan`.
/// `DeleteByScope` continues to hard-delete every chunk in the scope —
/// the scope-level operation is a configuration-grade reset, not a
/// recoverable one.
///
/// Satisfies Phase 9c portability rules:
/// 1. Identity by value — `chunkId: string`, `scope: VectorScope`
/// 2. Async at every boundary
/// 3. No callback/supervision hooks
/// 4. Stateless between calls
/// 5. No cross-scope ordering promises (results ranked by score)
type IVectorStore =
    /// Store or replace a chunk. Idempotent on `(scope, chunkId)` — re-upserting
    /// with the same id replaces the previous vector and metadata. If the
    /// chunk was previously tombstoned, `Upsert` clears the tombstone — the
    /// new content supersedes the old whether or not it's been vacuumed.
    abstract Upsert: scope: VectorScope -> chunkId: string -> vector: float32 array -> chunk: TextChunk -> Async<unit>

    /// Find the nearest chunks to `query` within the given scopes. Results are
    /// returned in descending score order. When multiple scopes are provided,
    /// results from all scopes are merged before ranking. Tombstoned chunks
    /// are filtered out.
    abstract Search: scopes: VectorScope list -> query: float32 array -> topK: int -> Async<VectorMatch list>

    /// Enumerate every chunk in `scope` for administrative workloads
    /// (re-embedding, vacuum, audit). Returns `(chunkId, TextChunk)` pairs.
    /// `includeDeleted = false` skips tombstoned chunks (default for re-embed
    /// scans); `true` returns them so callers can decide what to do based on
    /// `Metadata["_deletedAt"]`.
    abstract ListChunks: scope: VectorScope -> includeDeleted: bool -> Async<(string * TextChunk) list>

    /// Tombstone-delete a single chunk by its stable id within a scope.
    /// The chunk's metadata gains `_deletedAt = <ISO 8601 UTC>`; the entry
    /// remains in the store, filtered from `Search`, until `Vacuum`
    /// hard-removes it past the retention window.
    abstract DeleteChunk: scope: VectorScope -> chunkId: string -> Async<unit>

    /// Remove the `_deletedAt` tombstone from a chunk so it is once again
    /// visible to `Search`. Call this within the configured retention
    /// window — past `Vacuum`, the entry is gone and `RestoreChunk` is
    /// a no-op.
    abstract RestoreChunk: scope: VectorScope -> chunkId: string -> Async<unit>

    /// Hard-delete every chunk in `scope` whose `_deletedAt` tombstone is
    /// older than `olderThan`. Returns the number of entries purged.
    /// Non-tombstoned chunks are untouched.
    abstract Vacuum: scope: VectorScope -> olderThan: DateTimeOffset -> Async<int>

    /// Hard-delete all chunks belonging to `scope`. Used when a team's
    /// knowledge base is cleared or a team is deleted. Bypasses tombstone
    /// semantics — there is no recovery from `DeleteByScope`.
    abstract DeleteByScope: scope: VectorScope -> Async<unit>

    /// List the scopes the store currently has chunks for (visible or
    /// tombstoned). Used by `ReembeddingService` to enumerate work after
    /// a model swap; allows the service to operate without requiring the
    /// composing app to enumerate scopes itself. Implementations that lazy-
    /// load scopes (e.g. `InMemoryVectorStore`) return only the scopes
    /// loaded so far — eager enumeration of cold scopes is not required.
    abstract ListScopes: unit -> Async<VectorScope list>

    /// Phase 9h — GDPR Article 17 erasure surface. Erase (or
    /// tombstone) every chunk in `scope` whose `Content` or a
    /// `Metadata` value names `subjectUserId`, per `policy`:
    ///
    ///  - `HardDelete` — `DeleteChunk` each match then `Vacuum`
    ///    (`olderThan = now`) to physically purge it. The
    ///    scope-wide vacuum also purges any other tombstones already
    ///    past the threshold — acceptable, since those were already
    ///    soft-deleted; documented collateral of using the store's
    ///    own retention primitive.
    ///  - `Tombstone` / `RetainPerCompliance` — `DeleteChunk` each
    ///    match (soft-delete: filtered from `Search`, purged at the
    ///    store's normal vacuum cadence). Vector chunks are derived
    ///    KB content, not the audit fabric, so neither refuses.
    ///
    /// **Subject match.** A chunk names the subject when its
    /// `Content` or any `Metadata` value contains `subjectUserId`.
    /// Blank subject is a zero-count no-op.
    ///
    /// **Scope isolation (GP 4).** Only chunks in the supplied
    /// `scope` are touched — another scope's chunks are unreachable.
    ///
    /// `dryRun = true` counts matches without mutating. Default
    /// implementations delegate to `IVectorStore.eraseSubject`
    /// (ListChunks + DeleteChunk / Vacuum over this same interface).
    /// Portability audit (GP 12): identity by value, async at
    /// boundary, failure as `ErasureError`, stateless, single-scope,
    /// precision declared above.
    abstract Erase:
        scope: VectorScope * subjectUserId: string * policy: ToolUp.Platform.ErasurePolicy * dryRun: bool ->
            Async<Result<ToolUp.Platform.ErasureSummary, ToolUp.Platform.ErasureError>>

/// Shared default for `IVectorStore.Erase`. Expressed purely in terms
/// of `ListChunks` + `DeleteChunk` / `Vacuum` so every concrete store
/// (in-memory, HNSW, future companions) delegates to it in one line
/// and gets identical, contract-correct behaviour.
let eraseSubject
    (store: IVectorStore)
    (scope: VectorScope)
    (subjectUserId: string)
    (policy: ToolUp.Platform.ErasurePolicy)
    (dryRun: bool)
    : Async<Result<ToolUp.Platform.ErasureSummary, ToolUp.Platform.ErasureError>> =
    async {
        if ToolUp.Platform.Erasure.isBlankSubject subjectUserId then
            return
                Result.Ok {
                    HandlerName = "vector-store"
                    RecordsAffected = 0
                    Note = Some "blank subject — no-op"
                }
        else
            let! chunks = store.ListChunks scope false

            let namesSubject (c: TextChunk) =
                c.Content.Contains subjectUserId
                || (c.Metadata |> Map.exists (fun _ v -> v.Contains subjectUserId))

            let matched =
                chunks
                |> List.choose (fun (cid, c) -> if namesSubject c then Some cid else None)

            if dryRun || List.isEmpty matched then
                return
                    Result.Ok {
                        HandlerName = "vector-store"
                        RecordsAffected = matched.Length
                        Note = Some(sprintf "%d chunk(s) would be erased in scope" matched.Length)
                    }
            else
                for cid in matched do
                    do! store.DeleteChunk scope cid

                match policy with
                | ToolUp.Platform.ErasurePolicy.HardDelete ->
                    // Physically purge the just-tombstoned chunks. The
                    // scope-wide vacuum also reaps other tombstones past
                    // the threshold — they were already soft-deleted.
                    let! _ = store.Vacuum scope (DateTimeOffset.UtcNow.AddSeconds 1.0)
                    ()
                | _ -> ()

                let verb =
                    match policy with
                    | ToolUp.Platform.ErasurePolicy.HardDelete -> "purged"
                    | _ -> "tombstoned"

                return
                    Result.Ok {
                        HandlerName = "vector-store"
                        RecordsAffected = matched.Length
                        Note = Some(sprintf "%d chunk(s) %s in scope" matched.Length verb)
                    }
    }