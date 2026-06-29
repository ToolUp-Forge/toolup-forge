module ToolUp.KnowledgeBase.Server.KnowledgeBaseLifecycle

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerInventory

// ─── Phase 54d — first-party Knowledge Base lifecycle hook ───────────
//
// On `OnDeprovisioned`, purges the offboarded scope's Knowledge Base —
// every document blob, the index, extracted artefacts, and the
// process-local ingestion-status / progress / inventory caches — so a
// tenant's uploaded documents don't outlive the tenant. Lives in the
// companion package (GP 1: the KB store is the companion's, outside the
// core's reach), registered by the KB compose root only when
// `TenantLifecycle = EnabledTenantLifecycle`.
//
// Resolves `IBlobStorage` from DI per call (stateless between
// invocations, GP 12 rule 4) and deletes every blob under the scope
// container's `knowledge/` prefix — the index (`knowledge/index.json`),
// raw uploads, and extracted text all live there. Vector chunks are NOT
// touched here: those are RAG-substrate and the sibling
// `rag-vector-store` hook owns their purge, keeping each hook's blast
// radius to one substrate.
//
//   * `IBlobStorage` present — list + delete the `knowledge/` blobs,
//     evict the scope's docs from the status / progress caches, and
//     invalidate the inventory cache. `Completed` (or `Failed` listing
//     the per-blob delete errors).
//   * no storage registered — `Skipped` (the KB substrate writes through
//     `IBlobStorage`; without it there is nothing composed to purge).
//
// **Idempotency.** A second run finds no `knowledge/` blobs and the
// delete loop is a no-op `Completed`. `IBlobStorage.Delete` is itself
// idempotent (deleting an absent blob is not an error in the contract).
//
// **Scope container (GP 4).** Mirrors `AccessContext`'s convention: a
// team scope's container is `team-{id}`, a user scope's `user-{id}`. The
// offboard `scopeId` is the `StorageScope.ScopeId`; like the core
// `membership-cache` / `data-erasure` hooks, a bare id is treated as a
// team id (the tenant unit), and an already-prefixed id is taken as the
// container verbatim.
//
// `OnProvisioned` is a no-op `Skipped`: documents are ingested on demand,
// so provisioning has nothing to seed.

/// Resolve the KB blob container for the offboarded scope, mirroring
/// `AccessContext`'s `team-{id}` / `user-{id}` convention. A `user-`
/// prefix is honoured verbatim; everything else (bare id or `team-{id}`)
/// resolves to the team container.
let private containerOf (scopeId: string) =
    if scopeId.StartsWith("user-", StringComparison.Ordinal) then
        scopeId
    elif scopeId.StartsWith("team-", StringComparison.Ordinal) then
        scopeId
    else
        "team-" + scopeId

type KnowledgeBaseLifecycle(services: IServiceProvider) =
    interface ITenantLifecycle with
        member _.Name = "knowledge-base"

        member _.OnProvisioned(_scopeId, _actorUserId) = async {
            return LifecycleHookResult.Skipped "no provisioning action — documents are ingested on demand"
        }

        member _.OnDeprovisioned(scopeId, _actorUserId) = async {
            match services.GetService(typeof<IBlobStorage>) with
            | :? IBlobStorage as storage ->
                let container = containerOf scopeId

                // Snapshot the index before deleting so the status /
                // progress caches can be evicted for exactly this scope's
                // docs. A missing index is an empty list, not an error.
                let! docs = loadIndex storage container

                let! blobs = storage.List(container, "knowledge/")
                let errors = ResizeArray<string>()

                for blobName in blobs do
                    match! storage.Delete(container, blobName) with
                    | Ok() -> ()
                    | Error msg -> errors.Add(sprintf "%s: %s" blobName msg)

                for doc in docs do
                    statusCache.TryRemove doc.Id |> ignore
                    progressCache.TryRemove doc.Id |> ignore

                invalidateInventoryCache container

                if errors.Count > 0 then
                    return LifecycleHookResult.Failed(String.Join("; ", errors))
                else
                    return LifecycleHookResult.Completed
            | _ ->
                return LifecycleHookResult.Skipped "no IBlobStorage registered (KnowledgeBase substrate not composed)"
        }

/// Construct the first-party Knowledge Base lifecycle hook. Resolves the
/// active `IBlobStorage` from `services` on every call.
let create (services: IServiceProvider) : ITenantLifecycle =
    KnowledgeBaseLifecycle(services) :> ITenantLifecycle