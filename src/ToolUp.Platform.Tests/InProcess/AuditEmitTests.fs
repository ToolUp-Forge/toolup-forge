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
// refuse.

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

[<Tests>]
let tests =
    testList "Phase 1062 — audit failure is data at the seam" [
        testCaseAsync "RefuseAction: a classified read whose audit row fails is refused, not served"
        <| async {
            let! outcome =
                ClassificationGate.redactFields
                    classifier
                    ClassificationGate.defaultPolicy
                    (logUnder RefuseAction)
                    ctx
                    "Customer"
                    (Map.ofList [ "Email", "ceo@acme.example" ])
                |> Async.Catch

            match outcome with
            | Choice1Of2 served -> failtestf "the gated read proceeded under RefuseAction: %A" served
            | Choice2Of2 _ -> ()
        }
    ]