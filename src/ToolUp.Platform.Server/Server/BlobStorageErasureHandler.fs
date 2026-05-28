module ToolUp.Platform.BlobStorageErasureHandler

open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IDataExporter

// ─── Phase 9h — blob-storage DSR adapter ─────────────────────────────
//
// Bridges `IBlobStorage.Erase` (prefix + dry-run + policy) into the
// orchestrator's IErasureHandler extension point. The orchestrator
// speaks (scopeId, subjectUserId); this adapter maps that to
// container = scopeId, prefix = subjectUserId.
//
// **Prefix convention.** This erases blobs whose name starts with the
// subject id within the scope container. It is for deployments that
// store per-subject blobs under a subject-prefixed name. Typed stores
// (events / data objects / config / flags) own their own erasure
// handlers and partition; this handler is the catch-all for raw
// per-subject blob trees. A blank subject is a no-op (the underlying
// `eraseByPrefix` refuses a blank prefix).
//
// No IDataExporter: raw blobs are exported by their owning typed
// store's exporter; a blanket prefix exporter would duplicate bytes
// and risk over-reach across unrelated blobs.

[<Literal>]
let private HandlerName = "blobs"

type BlobStorageErasureHandler(storage: IBlobStorage) =
    interface IErasureHandler with
        member _.Name = HandlerName

        member _.Erase(scopeId, subjectUserId, policy) =
            storage.Erase(scopeId, subjectUserId, policy, false)

        member _.Preview(scopeId, subjectUserId, policy) = async {
            let! result = storage.Erase(scopeId, subjectUserId, policy, true)

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
let erasureHandler (storage: IBlobStorage) : IErasureHandler =
    BlobStorageErasureHandler storage :> IErasureHandler