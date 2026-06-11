// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DataSubject.Tests.Contracts.IBackgroundExportStoreContract

open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.DataSubject.Tests.Support.Doubles

/// Phase 9h.A — contract assertions every `IBackgroundExportStore` must
/// satisfy. The factory returns a fresh store sharing one backing blob
/// substrate.
let tests (name: string) (factory: unit -> IBackgroundExportStore) =
    let scope = "team-1"

    testList $"{name} — IBackgroundExportStore contract" [
        testCaseAsync "BeginExport records Preparing"
        <| async {
            let store = factory ()
            let! ticket = store.BeginExport(scope, sampleRequest "u1")
            let! status = store.GetStatus ticket
            Expect.equal status ExportStatus.Preparing "fresh ticket is Preparing"
        }

        testCaseAsync "Complete → Ready + Download round-trips the envelope"
        <| async {
            let store = factory ()
            let! ticket = store.BeginExport(scope, sampleRequest "u1")
            let envelope = Encoding.UTF8.GetBytes "the-envelope-bytes"
            do! store.Complete(ticket, envelope)
            let! status = store.GetStatus ticket
            Expect.equal status (ExportStatus.Ready(int64 envelope.Length)) "Ready carries the size"
            let! dl = store.Download ticket

            match dl with
            | Ok bytes -> Expect.equal bytes envelope "downloaded bytes match the stored envelope"
            | Error s -> failtestf "Download must succeed; got %A" s
        }

        testCaseAsync "Fail → Failed + Download refuses"
        <| async {
            let store = factory ()
            let! ticket = store.BeginExport(scope, sampleRequest "u1")
            do! store.Fail(ticket, "exporter blew up")
            let! status = store.GetStatus ticket
            Expect.equal status (ExportStatus.Failed "exporter blew up") "Failed carries the reason"
            let! dl = store.Download ticket

            match dl with
            | Error(ExportStatus.Failed _) -> ()
            | other -> failtestf "Download of a failed ticket must Error Failed; got %A" other
        }

        testCaseAsync "Cancel → Cancelled + Download refuses"
        <| async {
            let store = factory ()
            let! ticket = store.BeginExport(scope, sampleRequest "u1")
            do! store.Cancel ticket
            let! status = store.GetStatus ticket
            Expect.equal status ExportStatus.Cancelled "Cancelled status"
            let! dl = store.Download ticket

            match dl with
            | Error ExportStatus.Cancelled -> ()
            | other -> failtestf "Download of a cancelled ticket must Error Cancelled; got %A" other
        }

        testCaseAsync "Unknown ticket → Unknown status + Download refuses"
        <| async {
            let store = factory ()
            let! status = store.GetStatus "dsr.bogus.ticket"
            Expect.equal status ExportStatus.Unknown "unknown ticket reports Unknown"
            let! dl = store.Download "dsr.bogus.ticket"

            match dl with
            | Error ExportStatus.Unknown -> ()
            | other -> failtestf "Download of an unknown ticket must Error Unknown; got %A" other
        }

        testCaseAsync "Tickets are scope-opaque but per-ticket isolated"
        <| async {
            let store = factory ()
            let! t1 = store.BeginExport(scope, sampleRequest "u1")
            let! t2 = store.BeginExport("team-2", sampleRequest "u2")
            do! store.Complete(t1, Encoding.UTF8.GetBytes "one")
            let! s1 = store.GetStatus t1
            let! s2 = store.GetStatus t2
            Expect.equal s1 (ExportStatus.Ready 3L) "t1 completed"
            Expect.equal s2 ExportStatus.Preparing "t2 untouched by t1's completion"
        }
    ]