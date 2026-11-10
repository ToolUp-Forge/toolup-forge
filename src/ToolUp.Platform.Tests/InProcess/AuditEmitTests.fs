// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AuditEmitTests

// ─── Phase 1062 — audit failure is data at the seam ──────────────────
//
// `IAuditLog.Record` returns `Async<Result<unit, AuditWriteError>>` and the
// composed `AuditFailurePolicy` decides AT THE SEAM: `RefuseAction` leaves
// the `Error`, `LogAndContinue` and `DegradeToFile` warn and answer `Ok`.
// The red fixture this pack opened with: under `RefuseAction`, a classified
// read whose audit row could not be written went ahead, because the gate
// wrapped `Record` in `try … with _ -> ()` — a refusal policy that could not
// refuse. Each mode is pinned here, and the call-site shapes beside it.

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Audit
open ToolUp.Platform.Infra

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// An event store whose every write reports failure.
type private RefusingEventStore() =
    interface IEventStore with
        member _.Write evt = async {
            return Error(EventStoreError.WriteFailed(evt.ScopeId, evt.Id, "pinned", "simulated outage"))
        }

        member _.ReadAll _ = async { return [] }
        member _.ReadByType(_, _) = async { return [] }
        member _.ReadBySource(_, _) = async { return [] }
        member _.ListScopes() = async { return [] }

        member _.Erase(_, _, _, _) = async { return Ok(Unchecked.defaultof<ErasureSummary>) }

let private classifier =
    DefaultFieldClassifier.create [
        FieldClassification.create "Customer" "Email" Pii
        |> FieldClassification.withAuditOnRead
    ]

let private ctx = AccessContexts.forSubject (AuthenticatedUser "u1")

let private logUnder policy =
    AuditLog.EventStoreAuditLog(RefusingEventStore(), silentLogger, failurePolicy = policy) :> IAuditLog

let private row =
    UserLoggedIn {
        UserId = UserId "alice"
        AuthProvider = "Header"
    }

let private read policy =
    ClassificationGate.redactFields
        classifier
        ClassificationGate.defaultPolicy
        (logUnder policy)
        ctx
        "Customer"
        (Map.ofList [ "Email", "ceo@acme.example" ])

[<Tests>]
let tests =
    testList "Phase 1062 — audit failure is data at the seam" [
        testCaseAsync "RefuseAction: a classified read whose audit row fails is refused, not served"
        <| async {
            match! read RefuseAction with
            | Error(AuditWriteError.StoreUnavailable _) -> ()
            | other -> failtestf "the gated read must be refused under RefuseAction; got %A" other
        }

        testCaseAsync "LogAndContinue: the same read is served, its row counted and logged as lost"
        <| async {
            match! read LogAndContinue with
            | Ok fields -> Expect.equal fields["Email"] ClassificationGate.RedactedPlaceholder "served, redacted"
            | Error e -> failtestf "LogAndContinue must not refuse the read; got %A" e
        }

        testList "the seam decides, per mode" [
            testCaseAsync "RefuseAction answers StoreUnavailable"
            <| async {
                match!
                    (logUnder RefuseAction).Record(AuditScope.shard "team-acme", AuditSubject.system "test", row)
                with
                | Error(AuditWriteError.StoreUnavailable _) -> ()
                | other -> failtestf "expected StoreUnavailable; got %A" other
            }

            testCaseAsync "LogAndContinue answers Ok"
            <| async {
                let! answer =
                    (logUnder LogAndContinue).Record(AuditScope.shard "team-acme", AuditSubject.system "test", row)

                Expect.equal answer (Ok()) "the action completes"
            }

            testCaseAsync "DegradeToFile answers Ok (nothing to spill to is logged as lost)"
            <| async {
                let! answer =
                    (logUnder DegradeToFile).Record(AuditScope.shard "team-acme", AuditSubject.system "test", row)

                Expect.equal answer (Ok()) "the action completes"
            }

            testCaseAsync "NoOpAuditLog answers Ok"
            <| async {
                let! answer =
                    (NoOpAuditLog() :> IAuditLog).Record(AuditScope.shard "team-acme", AuditSubject.system "test", row)

                Expect.equal answer (Ok()) "a log with no substrate never refuses"
            }

            testCaseAsync "a write that lands answers Ok under RefuseAction"
            <| async {
                let log =
                    AuditLog.EventStoreAuditLog(
                        InMemoryEventStore.InMemoryEventStore(),
                        silentLogger,
                        failurePolicy = RefuseAction
                    )
                    :> IAuditLog

                let! answer = log.Record(AuditScope.shard "team-acme", AuditSubject.system "test", row)
                Expect.equal answer (Ok()) "a persisted row is Ok"
            }
        ]

        testList "the call-site shapes" [
            testCaseAsync "orRefuse turns a refused row into AuditWriteRefusedException"
            <| async {
                let! outcome =
                    AuditRow.emit
                        (logUnder RefuseAction)
                        (AuditScope.shard "team-acme")
                        (AuditSubject.system "test")
                        row
                    |> AuditRow.orRefuse
                    |> Async.Catch

                match outcome with
                | Choice2Of2(:? AuditWriteRefusedException as ex) ->
                    match ex.Error with
                    | AuditWriteError.StoreUnavailable _ -> ()
                    | other -> failtestf "the exception carries the seam's answer; got %A" other
                | other -> failtestf "expected AuditWriteRefusedException; got %A" other
            }

            testCaseAsync "bestEffort discards a refused row"
            <| async {
                do!
                    AuditRow.emit
                        (logUnder RefuseAction)
                        (AuditScope.shard "team-acme")
                        (AuditSubject.system "test")
                        row
                    |> AuditRow.bestEffort
            }
        ]

        testList "the row carries its actor" [
            testCaseAsync "a recorded row persists its actor, and the replicator's envelope reads it back"
            <| async {
                let store = InMemoryEventStore.InMemoryEventStore()
                let log = AuditLog.EventStoreAuditLog(store, silentLogger) :> IAuditLog
                let actor = TeamAudit(UserId "alice", TeamId "acme")

                let! answer = log.Record(AuditScope.team (TeamId "acme"), actor, row)
                Expect.equal answer (Ok()) "the row lands"

                let! persisted = (store :> IEventStore).ReadBySource("acme", AuditSourceModule.value)

                match persisted with
                | [ evt ] ->
                    Expect.equal (AuditLog.decodeActor evt.Payload) actor "the actor rides the row"
                    Expect.equal (AuditLog.decodeAuditEvent evt) (Some row) "the event decodes unchanged"

                    let envelope =
                        AuditEnvelope.create (AuditLog.decodeActor evt.Payload) evt.ScopeId evt.OccurredAt row

                    Expect.equal envelope.Subject actor "the sink sees the actor, not a guess from the key"
                | rows -> failtestf "expected one persisted row; got %d" (List.length rows)
            }

            testCase "a system row names its emitter"
            <| fun _ ->
                let payload = AuditLog.withActor (AuditSubject.system "BootstrapTeam") "{\"A\":1}"
                Expect.equal (AuditLog.decodeActor payload) (SystemAudit "BootstrapTeam") "the emitter round-trips"

            testCase "a row written before rows carried an actor decodes unchanged, as unattributed"
            <| fun _ ->
                // The bytes a row was persisted with before this phase: the
                // event's own payload, and nothing else.
                let atRest = "{\"UserId\":\"alice\",\"AuthProvider\":\"Header\"}"

                Expect.equal (AuditLog.serialiseAuditEvent row) atRest "today's event encoding is the encoding at rest"
                Expect.equal (AuditLog.tryDecodeAuditEvent "UserLoggedIn" atRest) (Ok row) "it decodes unchanged"
                Expect.equal (AuditLog.decodeActor atRest) AuditSubject.unattributed "no actor is guessed from the key"

            testCase "the actor is appended after the event's bytes, which are kept verbatim"
            <| fun _ ->
                let eventJson = AuditLog.serialiseAuditEvent row
                let persisted = AuditLog.withActor (UserAudit(UserId "alice")) eventJson

                Expect.stringStarts persisted (eventJson.TrimEnd('}')) "the event's bytes are a prefix of the row"

                Expect.equal
                    (AuditLog.tryDecodeAuditEvent "UserLoggedIn" persisted)
                    (Ok row)
                    "a reader ignores the actor"
        ]
    ]