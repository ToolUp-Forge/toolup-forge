module ToolUp.Cloud.Parity.Tests.AuditSinkParityTests

open Expecto
open ToolUp.Platform.Tests.Contracts
open ToolUp.Cloud.Parity.Tests.EmulatorLegs

// ─── Phase 193 — IAuditSink row of the parity matrix ──────────────────
//
// The three archive sinks (`AzureBlobArchive` / `S3Archive` / `GcsArchive`)
// each take an `IBlobStorage`, so this row rides on the blob row: each
// cloud's sink is bound over that same cloud's emulator-backed storage.
// That is why the audit seam is emulator-testable at all without a fourth
// emulator — and why a blob-leg skip correctly propagates here rather than
// reporting a coverage this row does not have.
//
// The pack's `verifyDelivered` callback checks the archive actually
// received one blob per non-empty batch (see `EmulatorLegs`), so a sink
// that returns `Ok` without writing fails the pack on every cloud alike.

let private legTests leg =
    match auditSinkBinding leg with
    | Ok(factory, verifyDelivered) ->
        IAuditSinkContract.tests $"%s{CloudLeg.name leg} — IAuditSink" factory verifyDelivered
    | Error skip -> testList $"%s{CloudLeg.name leg} — IAuditSink" [ ptestCase (LegSkip.describe skip) <| fun _ -> () ]

[<Tests>]
let tests = testList "Cloud parity — IAuditSink" (CloudLeg.all |> List.map legTests)