// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ConversationExportAuditTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Infra
open ToolUp.Platform.Audit

// ─── ConversationExported audit round-trip ──────────────────────
//
// The AI companion declares `ConversationExported` in its own family, so
// this pack — which reaches the audit part and nothing above it — sees the
// row as Core does: an `Opaque` event carrying its kind and payload. What
// is held here is the log's half: the row round-trips byte-for-byte through
// the production `EventStoreAuditLog` path, and the event-type filter on
// `GetAuditTrail` targets it. The typed half (the AI family re-encodes the
// same bytes) is the wire corpus's, in `AuditEventRegistryTests`. The
// client-side export sanitisation (stripping `ToolCalls`) lives in Fable
// code in `ConversationPanel.fs` and is verified at the Fable layer.

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

/// An export row as the AI family writes it.
let private exported (conversationId: string) (includeToolDetails: bool) (exportedBy: string) : AuditEvent =
    Opaque {
        Kind = "ConversationExported"
        Payload =
            sprintf
                "{\"ConversationId\":\"%s\",\"IncludeToolDetails\":%s,\"ExportedBy\":\"%s\"}"
                conversationId
                (if includeToolDetails then "true" else "false")
                exportedBy
    }

let tests =
    testList "ConversationExportAudit" [
        testCaseAsync "sanitised export → ConversationExported round-trips losslessly"
        <| async {
            let auditLog = workingChain ()
            let row = exported "11111111-1111-1111-1111-111111111111" false "alice"

            do!
                auditLog.Record(AuditScope.shard "user-alice", AuditSubject.system "test", row)
                |> AuditRow.orRefuse

            let! trail = auditLog.GetAuditTrail(AuditScope.shard "user-alice", None, Some "ConversationExported")
            Expect.equal trail [ row ] "the export row reads back byte-for-byte, IncludeToolDetails=false"
        }

        testCaseAsync "full-detail export → IncludeToolDetails=true round-trips"
        <| async {
            let auditLog = workingChain ()
            let row = exported "22222222-2222-2222-2222-222222222222" true "bob"

            do!
                auditLog.Record(AuditScope.shard "user-bob", AuditSubject.system "test", row)
                |> AuditRow.orRefuse

            let! trail = auditLog.GetAuditTrail(AuditScope.shard "user-bob", None, None)
            Expect.equal trail [ row ] "the opt-in row reads back byte-for-byte, IncludeToolDetails=true"
        }

        testCaseAsync "event-type filter isolates ConversationExported"
        <| async {
            let auditLog = workingChain ()

            do!
                auditLog.Record(
                    AuditScope.shard "user-carol",
                    AuditSubject.system "test",
                    exported "33333333-3333-3333-3333-333333333333" false "carol"
                )
                |> AuditRow.orRefuse

            let! unrelated = auditLog.GetAuditTrail(AuditScope.shard "user-carol", None, Some "UserLoggedIn")
            Expect.isEmpty unrelated "the filter must not return ConversationExported under another type"

            let! matched = auditLog.GetAuditTrail(AuditScope.shard "user-carol", None, Some "ConversationExported")
            Expect.equal (List.length matched) 1 "one export event under its own type"
        }
    ]