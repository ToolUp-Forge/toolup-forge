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
/// `team-` prefix; everything else is deployment/platform shared).
let scopeOf (scopeId: string) : VectorScope =
    if scopeId = "platform" || scopeId = "_platform" then
        Platform
    elif scopeId = "deployment" || scopeId = "_deployment" then
        Deployment
    elif scopeId.StartsWith "team-" then
        Team(scopeId.Substring "team-".Length)
    else
        Team scopeId

type VectorStoreErasureHandler(vectorStore: IVectorStore, embeddingCache: IEmbeddingCache) =
    interface IErasureHandler with
        member _.Name = HandlerName

        member _.Erase(scopeId, subjectUserId, policy) = async {
            let! result = vectorStore.Erase(scopeOf scopeId, subjectUserId, policy, false)
            // Flush derived embeddings so an erased chunk's vector
            // can't be served from cache afterwards.
            do! embeddingCache.Clear()
            return result
        }

        member _.Preview(scopeId, subjectUserId, policy) = async {
            let! result = vectorStore.Erase(scopeOf scopeId, subjectUserId, policy, true)

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
/// point — no composition-root edit).
let erasureHandler (vectorStore: IVectorStore) (embeddingCache: IEmbeddingCache) : IErasureHandler =
    VectorStoreErasureHandler(vectorStore, embeddingCache) :> IErasureHandler