module ToolUp.Platform.VectorStoreErasureHandler

open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IEmbeddingCache
open ToolUp.Platform.IDataExporter

// ─── Phase 9h — vector-store DSR adapter ─────────────────────────────
//
// Bridges `IVectorStore.Erase` into the orchestrator's IErasureHandler
// extension point. The orchestrator speaks (scopeId, subjectUserId);
// this adapter maps scopeId -> VectorScope and, after erasing the
// matching chunks, flushes the embedding cache (which is hash-keyed
// and cannot target a subject — a full flush is the privacy-correct
// response so a derived embedding of erased content can't be served).
//
// No IDataExporter: KB chunks are derived/embedded artefacts of the
// subject's source documents; the source records are exported by
// their owning store (data objects / blobs). A vector dump would be
// an unreadable float array, not an Article-15 "copy of your data".

[<Literal>]
let private HandlerName = "vector-store"

/// Map the orchestrator's scope-id string to a `VectorScope`. Mirrors
/// the convention used by `RAGCompose` (team containers carry the
/// `team-` prefix, per-user containers the `user-` prefix; the
/// reserved literals map to the deployment/platform shared scopes).
///
/// The `user-` branch is load-bearing for DSR completeness: a non-team
/// caller's KB chunks live in `User {id}` (the GAP-1 fix), so an erasure
/// pass over that caller's `user-{id}` container must target the `User`
/// vector scope — mapping it to `Team` would scan the wrong namespace and
/// silently leave the subject's chunks at rest.
let scopeOf (scopeId: string) : VectorScope =
    if scopeId = "platform" || scopeId = "_platform" then
        Platform
    elif scopeId = "deployment" || scopeId = "_deployment" then
        Deployment
    elif scopeId.StartsWith "team-" then
        Team(scopeId.Substring "team-".Length)
    elif scopeId.StartsWith "user-" then
        User(scopeId.Substring "user-".Length)
    else
        Team scopeId

type VectorStoreErasureHandler
    (vectorStore: IVectorStore, embeddingCache: IEmbeddingCache, ?sparseIndex: ToolUp.Platform.ISparseIndex.ISparseIndex)
    =

    // Phase 115 — erase across BOTH retrieval legs. Pre-115 this
    // handler reached the vector store only, so a hybrid deployment's
    // BM25 sparse index (full chunk text, persisted to blob storage)
    // kept serving — and storing at rest — content the DSR had erased.
    let eraseOnce (scopeId: string) subjectUserId policy dryRun = async {
        let scope = scopeOf scopeId
        let! vectorResult = vectorStore.Erase(scope, subjectUserId, policy, dryRun)

        let! sparseResult =
            match sparseIndex with
            | Some sparse -> sparse.Erase(scope, subjectUserId, policy, dryRun)
            | None ->
                async.Return(
                    Result.Ok {
                        HandlerName = HandlerName
                        RecordsAffected = 0
                        Note = None
                    }
                )

        return
            match vectorResult, sparseResult with
            | Result.Ok v, Result.Ok s ->
                Result.Ok {
                    HandlerName = HandlerName
                    RecordsAffected = v.RecordsAffected + s.RecordsAffected
                    Note =
                        match v.Note, s.Note with
                        | Some vn, Some sn -> Some(sprintf "%s; sparse: %s" vn sn)
                        | Some vn, None -> Some vn
                        | None, Some sn -> Some(sprintf "sparse: %s" sn)
                        | None, None -> None
                }
            | Result.Error err, _ -> Result.Error err
            | _, Result.Error err -> Result.Error err
    }

    interface IErasureHandler with
        member _.Name = HandlerName

        member _.Erase(scopeId, subjectUserId, policy) = async {
            let! result = eraseOnce scopeId subjectUserId policy false
            // Flush derived embeddings so an erased chunk's vector
            // can't be served from cache afterwards.
            do! embeddingCache.Clear()
            return result
        }

        member _.Preview(scopeId, subjectUserId, policy) = async {
            let! result = eraseOnce scopeId subjectUserId policy true

            return
                match result with
                | Result.Ok summary -> summary
                | Result.Error err -> {
                    HandlerName = HandlerName
                    RecordsAffected = 0
                    Note = Some(ErasureError.toMessage err)
                  }
        }

/// Compose-time registration helper (the IErasureHandler extension
/// point — no composition-root edit). Vector-only deployments keep this
/// shape; hybrid deployments use `erasureHandlerHybrid` so DSR erasure
/// covers the sparse leg too (Phase 115).
let erasureHandler (vectorStore: IVectorStore) (embeddingCache: IEmbeddingCache) : IErasureHandler =
    VectorStoreErasureHandler(vectorStore, embeddingCache) :> IErasureHandler

/// Phase 115 — hybrid-deployment registration helper: erasure fans out
/// across vector store + sparse index, then flushes the embedding
/// cache.
let erasureHandlerHybrid
    (vectorStore: IVectorStore)
    (embeddingCache: IEmbeddingCache)
    (sparseIndex: ToolUp.Platform.ISparseIndex.ISparseIndex)
    : IErasureHandler =
    VectorStoreErasureHandler(vectorStore, embeddingCache, sparseIndex) :> IErasureHandler