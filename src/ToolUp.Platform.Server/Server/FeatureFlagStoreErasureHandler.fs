module ToolUp.Platform.FeatureFlagStoreErasureHandler

open ToolUp.Platform
open ToolUp.Platform.IDataExporter

// ─── Phase 9h — feature-flag-store DSR adapter ───────────────────────
//
// Bridges `IFeatureFlagStore.Erase` into the orchestrator's
// IErasureHandler extension point. No IDataExporter: flags are
// operational config (a flag may *reference* a user id in a targeting
// variant, but it is not the subject's personal record) and the store
// exposes no scope-enumeration surface.

[<Literal>]
let private HandlerName = "feature-flags"

type FeatureFlagStoreErasureHandler(flagStore: IFeatureFlagStore) =
    interface IErasureHandler with
        member _.Name = HandlerName

        member _.Erase(scopeId, subjectUserId, policy) =
            flagStore.Erase(scopeId, subjectUserId, policy, false)

        member _.Preview(scopeId, subjectUserId, policy) = async {
            let! result = flagStore.Erase(scopeId, subjectUserId, policy, true)

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
let erasureHandler (flagStore: IFeatureFlagStore) : IErasureHandler =
    FeatureFlagStoreErasureHandler flagStore :> IErasureHandler