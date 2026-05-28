module ToolUp.Platform.LineageStoreErasureHandler

open ToolUp.Platform
open ToolUp.Platform.IDataExporter

// ─── Phase 9h — lineage-store DSR adapter ────────────────────────────
//
// Bridges `ILineageStore.Erase` into the orchestrator's IErasureHandler
// extension point. No IDataExporter: lineage links are `ModuleEvent`s
// (SourceModule "_platform.lineage"), so they are already carried by
// the event-store exporter's segment — a separate lineage exporter
// would duplicate bytes. The lineage handler's role is the
// lineage-scoped, structurally-aware participant (reports link impact,
// refuses under RetainPerCompliance); byte erasure of the link events
// is owned by the event-store erasure handler.

[<Literal>]
let private HandlerName = "lineage"

type LineageStoreErasureHandler(lineageStore: ILineageStore) =
    interface IErasureHandler with
        member _.Name = HandlerName

        member _.Erase(scopeId, subjectUserId, policy) =
            lineageStore.Erase(scopeId, subjectUserId, policy, false)

        member _.Preview(scopeId, subjectUserId, policy) = async {
            let! result = lineageStore.Erase(scopeId, subjectUserId, policy, true)

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
let erasureHandler (lineageStore: ILineageStore) : IErasureHandler =
    LineageStoreErasureHandler lineageStore :> IErasureHandler