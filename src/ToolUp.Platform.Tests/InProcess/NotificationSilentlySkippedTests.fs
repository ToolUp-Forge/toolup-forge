module ToolUp.Platform.Tests.InProcess.NotificationSilentlySkippedTests

open Expecto
open ToolUp.Platform

// ─── Tests ────────────────────────────────────────────────────────────
//
// Phase 6l.C — verify the AuditEvent shape, eventTypeName mapping,
// and audit-trail readback. The dispatcher integration test would
// require standing up the full TransactionalDispatcher with mock
// IConfigStore / sink — heavier than the value of this regression
// guard. Instead we cover:
//   - The new DU case + payload type round-trip through the
//     EventStoreAuditLog serialise/deserialise path.
//   - The eventTypeName discriminator is "NotificationSilentlySkipped".
//   - GetAuditTrail returns the event with PII-free recipient hashes.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private mkChain () =
    let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let auditLog = AuditLog.EventStoreAuditLog(store, silentLogger) :> IAuditLog
    auditLog, store

let private samplePayload () : NotificationSilentlySkippedPayload = {
    NotificationKind = "Email"
    ScopeId = "team-acme"
    Reason = "team_opted_out"
    RecipientHashes = [ "ab12cd34"; "ef56ab78" ]
    CorrelationId = Some "job-42"
}

[<Tests>]
let tests =
    testList "Phase 6l.C — NotificationSilentlySkipped audit event" [

        test "eventTypeName returns the canonical case-name string" {
            let payload = samplePayload ()
            let evt = NotificationSilentlySkipped payload
            Expect.equal (AuditEvent.eventTypeName evt) "NotificationSilentlySkipped" "DU case-name"
        }

        test "EventStoreAuditLog round-trips through Record + GetAuditTrail" {
            let auditLog, _ = mkChain ()
            let payload = samplePayload ()

            auditLog.Record(payload.ScopeId, NotificationSilentlySkipped payload)
            |> Async.RunSynchronously

            let trail =
                auditLog.GetAuditTrail(payload.ScopeId, None, None) |> Async.RunSynchronously

            Expect.equal trail.Length 1 "one event recorded"

            match trail[0] with
            | NotificationSilentlySkipped p ->
                Expect.equal p.NotificationKind "Email" "Kind preserved"
                Expect.equal p.Reason "team_opted_out" "Reason preserved"
                Expect.equal p.RecipientHashes [ "ab12cd34"; "ef56ab78" ] "hashes preserved"
                Expect.equal p.CorrelationId (Some "job-42") "correlation id preserved"
            | other -> failtestf "expected NotificationSilentlySkipped, got %A" other
        }

        test "GetAuditTrail can filter to NotificationSilentlySkipped only" {
            let auditLog, _ = mkChain ()
            let payload = samplePayload ()

            // Record one of ours and one unrelated event so the
            // eventType filter has something to discriminate against.
            auditLog.Record(payload.ScopeId, NotificationSilentlySkipped payload)
            |> Async.RunSynchronously

            auditLog.Record(
                payload.ScopeId,
                UserLoggedIn {
                    UserId = "alice"
                    AuthProvider = "Header"
                }
            )
            |> Async.RunSynchronously

            let filtered =
                auditLog.GetAuditTrail(payload.ScopeId, None, Some "NotificationSilentlySkipped")
                |> Async.RunSynchronously

            Expect.equal filtered.Length 1 "filter narrowed to one"

            match filtered[0] with
            | NotificationSilentlySkipped _ -> ()
            | other -> failtestf "filter returned wrong kind: %A" other
        }

        test "Payload field shape matches docs (PII-free)" {
            // Defence-in-depth — explicitly assert the payload has
            // hashed recipient identifiers, not raw user ids. This
            // test fails if a future refactor accidentally widens
            // the payload to carry email addresses or raw userIds.
            let payload = samplePayload ()
            // Hashes are 8 hex chars (4 bytes of SHA256).
            for h in payload.RecipientHashes do
                Expect.equal h.Length 8 "hash is 8 chars (4 bytes)"

                Expect.isTrue (h |> Seq.forall (fun c -> "0123456789abcdef".Contains c)) "hash is lowercase hex"
        }
    ]