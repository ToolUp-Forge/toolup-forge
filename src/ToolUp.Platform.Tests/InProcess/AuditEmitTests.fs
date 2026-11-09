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

let private ctx = AccessContext.unrestricted (AuthenticatedUser "u1")

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
                match! (logUnder RefuseAction).Record("team-acme", row) with
                | Error(AuditWriteError.StoreUnavailable _) -> ()
                | other -> failtestf "expected StoreUnavailable; got %A" other
            }

            testCaseAsync "LogAndContinue answers Ok"
            <| async {
                let! answer = (logUnder LogAndContinue).Record("team-acme", row)
                Expect.equal answer (Ok()) "the action completes"
            }

            testCaseAsync "DegradeToFile answers Ok (nothing to spill to is logged as lost)"
            <| async {
                let! answer = (logUnder DegradeToFile).Record("team-acme", row)
                Expect.equal answer (Ok()) "the action completes"
            }

            testCaseAsync "NoOpAuditLog answers Ok"
            <| async {
                let! answer = (AuditLog.NoOpAuditLog() :> IAuditLog).Record("team-acme", row)
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

                let! answer = log.Record("team-acme", row)
                Expect.equal answer (Ok()) "a persisted row is Ok"
            }
        ]

        testList "the call-site shapes" [
            testCaseAsync "orRefuse turns a refused row into AuditWriteRefusedException"
            <| async {
                let! outcome =
                    AuditRow.emit (logUnder RefuseAction) "team-acme" row
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
            <| async { do! AuditRow.emit (logUnder RefuseAction) "team-acme" row |> AuditRow.bestEffort }
        ]
    ]