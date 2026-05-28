module ToolUp.Platform.ConfigStoreErasureHandler

open ToolUp.Platform
open ToolUp.Platform.IDataExporter

// ─── Phase 9h — config-store DSR adapter ─────────────────────────────
//
// Bridges `IConfigStore.Erase` into the orchestrator's IErasureHandler
// extension point. No IDataExporter: `IConfigStore` exposes no
// enumeration surface (it is keyed by `(scope, moduleKey)`), and
// config is operational settings rather than the subject's personal
// records — the GDPR-relevant action here is redaction/erasure of any
// setting that embeds the subject, not Article-15 export.

[<Literal>]
let private HandlerName = "config"

type ConfigStoreErasureHandler(configStore: IConfigStore) =
    interface IErasureHandler with
        member _.Name = HandlerName

        member _.Erase(scopeId, subjectUserId, policy) =
            configStore.Erase(scopeId, subjectUserId, policy, false)

        member _.Preview(scopeId, subjectUserId, policy) = async {
            let! result = configStore.Erase(scopeId, subjectUserId, policy, true)

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
let erasureHandler (configStore: IConfigStore) : IErasureHandler =
    ConfigStoreErasureHandler configStore :> IErasureHandler