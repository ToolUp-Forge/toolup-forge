// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FastPathBeaconOwnershipTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.AI
open ToolUp.AI.FastPathBeaconHandler

// ─── Phase 6j.D — conversation-ownership gate ────────────────────
//
// Covers the .NET-verifiable half of 6j.D: the pure `checkOwnership`
// function and the `BeaconRejected` audit round-trip through
// `EventStoreAuditLog`. The HTTP handler wiring (StorageScope
// resolution, IBlobStorage I/O, Giraffe response codes) is structural
// glue around these two units — wedging it under an in-process
// Giraffe harness would add far more test scaffolding than the gate's
// behaviour warrants. The pure gate tests guarantee the security-
// critical decision (accept / refuse); the audit round-trip guarantees
// the forensic trail.

let private msg (createdBy: string) : ConversationMessage = {
    Id = Guid.NewGuid()
    ConversationId = Guid.NewGuid()
    Participant = User
    Content = "set country to UK"
    Timestamp = DateTime.UtcNow
    ToolCalls = []
    RetrievedSources = []
    Parts = []
    CreatedBy = createdBy
    BeaconId = ""
    Verification = None
}

let private gateTests =
    testList "checkOwnership — Phase 6j.D conversation-ownership gate" [
        testCase "empty existing — new conversation, establishes creator implicitly"
        <| fun _ ->
            // First persisted message of a conversation has not yet
            // been written; the caller about to write it becomes the
            // recorded owner. The gate accepts because there's no
            // prior owner to clash with.
            let result = checkOwnership [] "alice"
            Expect.equal result (Ok()) "empty existing accepts any caller"

        testCase "same caller owns first message — accept"
        <| fun _ ->
            let existing = [ msg "alice"; msg "alice" ]
            let result = checkOwnership existing "alice"
            Expect.equal result (Ok()) "same caller as owner is accepted"

        testCase "cross-user in shared container — refuse with owner surfaced"
        <| fun _ ->
            // In Team / MultiTeam mode the container is `team-{teamId}`
            // and both alice and bob see the same blobs. The gate must
            // refuse bob's attempt to append to alice's conversation
            // and surface alice's id so the audit row records who the
            // attempt was against.
            let existing = [ msg "alice" ]
            let result = checkOwnership existing "bob"
            Expect.equal result (Error "alice") "cross-user surfaces owner id back to caller"

        testCase "legacy blob with empty CreatedBy — accept (backwards-compat)"
        <| fun _ ->
            // Pre-6j.D conversation blobs don't carry CreatedBy. Newly
            // deserialised they surface as "" (or null → empty under
            // F# string semantics). Locking these conversations out
            // would break every existing chat on upgrade; the gate
            // accepts so legacy history remains usable, and the next
            // server-written message backfills the field via the
            // ownership-aware code path (eventually shifting the
            // first-message owner to whoever next appends).
            let existing = [ msg "" ]
            let result = checkOwnership existing "bob"
            Expect.equal result (Ok()) "legacy empty CreatedBy is treated as unowned"

        testCase "only the first message's CreatedBy is authoritative"
        <| fun _ ->
            // The gate samples `existing[0].CreatedBy`. A later
            // synthetic-assistant message recording a different
            // CreatedBy (or an empty one — assistant turns aren't
            // owners) must not unlock the conversation for a stranger.
            let existing = [ msg "alice"; msg ""; msg "carol" ]
            let result = checkOwnership existing "bob"
            Expect.equal result (Error "alice") "first message wins; later non-owner entries don't shift ownership"

        testCase "anonymous caller against anonymous-owned conversation — accept"
        <| fun _ ->
            // In Anonymous-mode deployments every caller resolves to a
            // per-session container; cross-user injection is
            // structurally impossible. The gate still runs and trivially
            // matches `"anonymous" = "anonymous"` so the path stays
            // identical across modes.
            let existing = [ msg "anonymous" ]
            let result = checkOwnership existing "anonymous"
            Expect.equal result (Ok()) "anonymous matches anonymous owner"

        testCase "empty caller against owned conversation — refuse"
        <| fun _ ->
            // Defence-in-depth: the handlers refuse callers with no
            // resolved UserId at the entry point (401), but if a future
            // code path were to call checkOwnership with an empty
            // caller against a properly-owned conversation, the gate
            // must still refuse rather than treat "" as a wildcard.
            let existing = [ msg "alice" ]
            let result = checkOwnership existing ""
            Expect.equal result (Error "alice") "empty caller does not match a non-empty owner"
    ]

// ─── BeaconRejected audit round-trip ─────────────────────────────

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private workingAuditLog () =
    let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    AuditLog.EventStoreAuditLog(store, silentLogger) :> IAuditLog

let private auditTests =
    testList "BeaconRejected — Phase 6j.D audit round-trip" [
        testCaseAsync "beacon-surface rejection round-trips losslessly through EventStoreAuditLog"
        <| async {
            let auditLog = workingAuditLog ()
            let convId = Guid.NewGuid()

            let payload: BeaconRejectedPayload = {
                ConversationId = convId
                Caller = "bob"
                Owner = "alice"
                Surface = BeaconSurfaceLabel
            }

            do! auditLog.Record("team-acme", BeaconRejected payload)

            let! trail = auditLog.GetAuditTrail("team-acme", None, Some "BeaconRejected")

            match trail with
            | [ BeaconRejected p ] ->
                Expect.equal p.ConversationId convId "ConversationId round-trips"
                Expect.equal p.Caller "bob" "Caller round-trips"
                Expect.equal p.Owner "alice" "Owner round-trips"
                Expect.equal p.Surface "beacon" "Surface round-trips"
            | other -> failtestf "expected exactly one BeaconRejected; got %A" other
        }

        testCaseAsync "submit-surface rejection records Surface=submit"
        <| async {
            // The symmetric SubmitMessage path records the same audit
            // case with `Surface = "submit"` so operators can filter
            // BeaconRejected events by surface in incident triage.
            let auditLog = workingAuditLog ()

            do!
                auditLog.Record(
                    "team-acme",
                    BeaconRejected {
                        ConversationId = Guid.NewGuid()
                        Caller = "bob"
                        Owner = "alice"
                        Surface = "submit"
                    }
                )

            let! trail = auditLog.GetAuditTrail("team-acme", None, Some "BeaconRejected")

            match trail with
            | [ BeaconRejected p ] -> Expect.equal p.Surface "submit" "submit-surface label survives the round-trip"
            | other -> failtestf "expected exactly one BeaconRejected; got %A" other
        }

        testCaseAsync "BeaconRejected event-type filter isolates the case"
        <| async {
            let auditLog = workingAuditLog ()

            do!
                auditLog.Record(
                    "team-acme",
                    BeaconRejected {
                        ConversationId = Guid.NewGuid()
                        Caller = "bob"
                        Owner = "alice"
                        Surface = BeaconSurfaceLabel
                    }
                )

            let! unrelated = auditLog.GetAuditTrail("team-acme", None, Some "ConversationExported")
            Expect.isEmpty unrelated "the filter must not return BeaconRejected under another type"

            let! matched = auditLog.GetAuditTrail("team-acme", None, Some "BeaconRejected")
            Expect.equal (List.length matched) 1 "one rejection event under its own type"
        }
    ]

let tests = testList "FastPathBeaconOwnership" [ gateTests; auditTests ]