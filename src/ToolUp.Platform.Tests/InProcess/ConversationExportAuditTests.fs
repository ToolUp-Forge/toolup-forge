// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ConversationExportAuditTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Infra
open ToolUp.Platform.Audit

// ─── Phase 6h.A — ConversationExported audit round-trip ─────────
//
// Covers the .NET-verifiable half of 6h.A: the `ConversationExported`
// AuditEvent serialises + decodes losslessly through the production
// `EventStoreAuditLog` path (STJ + `FableConverters`), and the
// event-type filter on `GetAuditTrail` targets it. The client-side
// export sanitisation (stripping `ToolCalls`) lives in Fable code in
// `ConversationPanel.fs` and is verified separately at the Fable layer.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private workingChain () =
    let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let auditLog = AuditLog.EventStoreAuditLog(store, silentLogger) :> IAuditLog
    auditLog

let tests =
    testList "ConversationExportAudit" [
        testCaseAsync "sanitised export → ConversationExported round-trips losslessly"
        <| async {
            let auditLog = workingChain ()

            let payload = {
                ConversationId = "11111111-1111-1111-1111-111111111111"
                IncludeToolDetails = false
                ExportedBy = "alice"
            }

            do!
                auditLog.Record(AuditScope.shard "user-alice", AuditSubject.system "test", ConversationExported payload)
                |> AuditRow.orRefuse

            let! trail = auditLog.GetAuditTrail(AuditScope.shard "user-alice", None, Some "ConversationExported")

            match trail with
            | [ ConversationExported p ] ->
                Expect.equal p.ConversationId payload.ConversationId "ConversationId round-trips"
                Expect.isFalse p.IncludeToolDetails "sanitised export records IncludeToolDetails=false"
                Expect.equal p.ExportedBy "alice" "ExportedBy round-trips"
            | other -> failtestf "expected exactly one ConversationExported; got %A" other
        }

        testCaseAsync "full-detail export → IncludeToolDetails=true round-trips"
        <| async {
            let auditLog = workingChain ()

            do!
                auditLog.Record(
                    AuditScope.shard "user-bob",
                    AuditSubject.system "test",
                    ConversationExported {
                        ConversationId = "22222222-2222-2222-2222-222222222222"
                        IncludeToolDetails = true
                        ExportedBy = "bob"
                    }
                )
                |> AuditRow.orRefuse

            let! trail = auditLog.GetAuditTrail(AuditScope.shard "user-bob", None, None)

            match trail with
            | [ ConversationExported p ] -> Expect.isTrue p.IncludeToolDetails "opt-in records IncludeToolDetails=true"
            | other -> failtestf "expected exactly one ConversationExported; got %A" other
        }

        testCaseAsync "event-type filter isolates ConversationExported"
        <| async {
            let auditLog = workingChain ()

            do!
                auditLog.Record(
                    AuditScope.shard "user-carol",
                    AuditSubject.system "test",
                    ConversationExported {
                        ConversationId = "33333333-3333-3333-3333-333333333333"
                        IncludeToolDetails = false
                        ExportedBy = "carol"
                    }
                )
                |> AuditRow.orRefuse

            let! unrelated = auditLog.GetAuditTrail(AuditScope.shard "user-carol", None, Some "UserLoggedIn")
            Expect.isEmpty unrelated "the filter must not return ConversationExported under another type"

            let! matched = auditLog.GetAuditTrail(AuditScope.shard "user-carol", None, Some "ConversationExported")
            Expect.equal (List.length matched) 1 "one export event under its own type"
        }
    ]