module ToolUp.RAG.RagVectorStoreLifecycle

open System
open ToolUp.Platform
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Phase 54d — first-party RAG vector-store lifecycle hook ─────────
//
// On `OnDeprovisioned`, hard-purges every embedded chunk in the
// offboarded scope so RAG retrieval can never surface a dead tenant's
// documents. Lives in the RAG companion (GP 1: the vector store is the
// companion's substrate), registered by `composeRAG` only when
// `TenantLifecycle = EnabledTenantLifecycle`.
//
// Resolves the active `IVectorStore` from DI per call (stateless between
// invocations, GP 12 rule 4) and uses the portable scope-level purge
// (`DeleteByScope`) — a configuration-grade reset that bypasses
// tombstone semantics (no recovery), exactly the offboard guarantee
// wanted. It works against the in-process default and any companion
// (HNSW, future distributed stores) without an interface extension:
//   * `IVectorStore` present — `DeleteByScope (Team teamId)`. KB / RAG
//     in-app authoring always lands chunks in the team scope; the
//     `Platform` / `Deployment` scopes are cross-tenant shared knowledge,
//     never purged on a single-tenant offboard.
//   * no store registered — `Skipped` (RAG pipeline not composed).
//
// **Idempotency.** `DeleteByScope` on an already-empty scope is a no-op,
// so a re-run is a clean `Completed`.
//
// **Scope mapping (GP 4).** The offboard `scopeId` is the
// `StorageScope.ScopeId`; a `team-` prefix is stripped to the bare team
// id (mirroring the core `membership-cache` hook), and a bare id is
// taken as the team id directly.
//
// `OnProvisioned` is a no-op `Skipped`: vector chunks are written on
// document ingestion, so provisioning has nothing to do.

/// Strip the `team-` container prefix if present so the hook accepts
/// either the bare team id or the `team-{id}` container form.
let private teamIdOf (scopeId: string) =
    if scopeId.StartsWith("team-", StringComparison.Ordinal) then
        scopeId.Substring 5
    else
        scopeId

type RagVectorStoreLifecycle(services: IServiceProvider) =
    interface ITenantLifecycle with
        member _.Name = "rag-vector-store"

        member _.OnProvisioned(_scopeId, _actorUserId) = async {
            return
                LifecycleHookResult.Skipped "no provisioning action — vector chunks are written on document ingestion"
        }

        member _.OnDeprovisioned(scopeId, _actorUserId) = async {
            match services.GetService(typeof<IVectorStore>) with
            | :? IVectorStore as store ->
                do! store.DeleteByScope(Team(teamIdOf scopeId))
                return LifecycleHookResult.Completed
            | _ -> return LifecycleHookResult.Skipped "no IVectorStore registered (RAG pipeline not composed)"
        }

/// Construct the first-party RAG vector-store lifecycle hook. Resolves
/// the active `IVectorStore` from `services` on every call.
let create (services: IServiceProvider) : ITenantLifecycle =
    RagVectorStoreLifecycle(services) :> ITenantLifecycle