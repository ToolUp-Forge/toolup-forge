// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ConversationEraseHandler

open ToolUp.Platform
open ToolUp.Platform.IDataExporter

// ─── Phase 53 — `IErasureHandler` adapter for IConversationStore ─
//
// Thin adapter exposing `IConversationStore.Erase` as an
// `IErasureHandler` for the DSR pipeline (Phase 9h). The DSR
// orchestrator (`ErasurePipeline`) walks every registered
// `IErasureHandler` and aggregates results into the per-request
// audit row; this adapter is one of those contributors.
//
// The conversation-substrate-specific audit row
// (`ConversationErased`) is emitted by `IConversationStore.Erase`
// itself — this adapter does not duplicate it.

[<Literal>]
let private HandlerName = "conversations"

/// Registered via `ServerApp.withErasureHandler` when
/// `ServerConfig.ConversationStore = EnabledConversationStore _`
/// AND `ServerConfig.DataSubjectRequests = Enabled _`. The
/// adapter is a no-op when either gate is off (DSR pipeline
/// never resolves it). The conversation-substrate-specific audit
/// row (`ConversationErased`) is emitted by `IConversationStore.Erase`
/// itself — this adapter does not duplicate it.
type ConversationEraseHandler(store: IConversationStore) =
    interface IErasureHandler with
        member _.Name = HandlerName

        member _.Erase(scopeId, subjectUserId, policy) =
            store.Erase(scopeId, subjectUserId, policy, false)

        member _.Preview(scopeId, subjectUserId, policy) = async {
            let! result = store.Erase(scopeId, subjectUserId, policy, true)

            return
                match result with
                | Result.Ok summary -> summary
                | Result.Error err -> {
                    HandlerName = HandlerName
                    RecordsAffected = 0
                    Note = Some(ErasureError.toMessage err)
                  }
        }

/// Compose-time registration helper. Pair with `withErasureHandler`
/// when DSR coverage of conversations is desired:
/// `ServerApp.withErasureHandler (ConversationEraseHandler.erasureHandler conversationStore) app`.
let erasureHandler (store: IConversationStore) : IErasureHandler =
    ConversationEraseHandler store :> IErasureHandler