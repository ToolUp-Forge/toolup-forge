// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DataSubject.Tests.InProcess.BackgroundExportStoreTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.IDataExporter
open ToolUp.Platform.DataSubjectRequestApiHandler
open ToolUp.Platform.DataSubject.Tests.Support.Doubles
open ToolUp.Platform.DataSubject.Tests.Contracts

let private factory () : IBackgroundExportStore =
    BlobBackedBackgroundExportStore.create (InMemoryBlobStorage())

let private implTests =
    testList "BlobBackedBackgroundExportStore — TTL + jobs" [
        testCaseAsync "An expired-TTL ticket reports Expired and refuses Download"
        <| async {
            // Negative TTL → expiresAt is already in the past at Begin.
            let store =
                BlobBackedBackgroundExportStore.createWithTtl (InMemoryBlobStorage()) (TimeSpan.FromSeconds -1.0)

            let! ticket = store.BeginExport("team-1", sampleRequest "u1")
            do! store.Complete(ticket, Text.Encoding.UTF8.GetBytes "x")
            let! status = store.GetStatus ticket
            Expect.equal status ExportStatus.Expired "past-TTL ticket reports Expired"
            let! dl = store.Download ticket

            match dl with
            | Error ExportStatus.Expired -> ()
            | other -> failtestf "Download of an expired ticket must Error Expired; got %A" other
        }

        testCaseAsync "DSRExportJobHandler assembles the envelope and Completes the ticket (sync-identical shape)"
        <| async {
            let blobs = InMemoryBlobStorage()
            let store = BlobBackedBackgroundExportStore.create blobs
            let audit = CapturingAudit()
            let request = sampleRequest "u1"
            let! ticket = store.BeginExport("team-1", request)

            let exporters: IDataExporter list = [ FakeExporter "kb" :> IDataExporter; FakeExporter "rag" ]

            let handler =
                DSRExportJobHandler.create store exporters audit.Callback (NoOpLogger())

            let payload = DsrJobPayload.serialise ticket request
            let! result = (handler :> IJobHandler).Execute(jobContext "team-1" payload)

            Expect.equal result Success "export job succeeds"

            let! status = store.GetStatus ticket

            match status with
            | ExportStatus.Ready _ -> ()
            | other -> failtestf "ticket should be Ready; got %A" other

            // The async envelope must byte-match what the synchronous
            // RequestExport path would produce for the same segments.
            let expectedSegments = [
                {
                    Name = "kb"
                    MimeType = "application/json"
                    Body = Text.Encoding.UTF8.GetBytes "kb:u1"
                }
                {
                    Name = "rag"
                    MimeType = "application/json"
                    Body = Text.Encoding.UTF8.GetBytes "rag:u1"
                }
            ]

            let expectedEnvelope = serialiseSegments expectedSegments
            let! dl = store.Download ticket

            match dl with
            | Ok bytes -> Expect.equal bytes expectedEnvelope "downloaded envelope matches the canonical sync shape"
            | Error s -> failtestf "Download must succeed; got %A" s

            let completed = audit.Events |> List.filter (fun e -> e.Kind = ExportCompleted)

            Expect.equal completed.Length 1 "one ExportCompleted audit event"
            Expect.equal completed[0].Properties["segmentCount"] "2" "segment count recorded"
        }

        testCaseAsync "DSRExportJobHandler Fails the ticket + returns TransientFailure when an exporter throws"
        <| async {
            let blobs = InMemoryBlobStorage()
            let store = BlobBackedBackgroundExportStore.create blobs
            let audit = CapturingAudit()
            let request = sampleRequest "u1"
            let! ticket = store.BeginExport("team-1", request)

            let throwingExporter =
                { new IDataExporter with
                    member _.Name = "boom"
                    member _.Export(_, _) = async { return failwith "exporter down" }
                }

            let handler =
                DSRExportJobHandler.create store [ throwingExporter ] audit.Callback (NoOpLogger())

            let payload = DsrJobPayload.serialise ticket request
            let! result = (handler :> IJobHandler).Execute(jobContext "team-1" payload)

            match result with
            | TransientFailure _ -> ()
            | other -> failtestf "a throwing exporter should yield TransientFailure (retryable); got %A" other

            let! status = store.GetStatus ticket

            match status with
            | ExportStatus.Failed _ -> ()
            | other -> failtestf "ticket should be Failed; got %A" other
        }

        testCaseAsync "DSRExportJobHandler returns PermanentFailure for a malformed payload"
        <| async {
            let store = factory ()

            let handler =
                DSRExportJobHandler.create store [] (CapturingAudit()).Callback (NoOpLogger())

            let! result = (handler :> IJobHandler).Execute(jobContext "team-1" "not-json")

            match result with
            | PermanentFailure _ -> ()
            | other -> failtestf "malformed payload should be PermanentFailure (no retry); got %A" other
        }

        testCaseAsync "DSRErasureJobHandler runs erasure and emits ErasureCompleted audit"
        <| async {
            let audit = CapturingAudit()

            let handlers: IErasureHandler list = [
                FakeErasureHandler("kb", 5) :> IErasureHandler
                FakeErasureHandler("rag", 3)
            ]

            let handler = DSRErasureJobHandler.create handlers audit.Callback (NoOpLogger())

            let request = {
                sampleRequest "u1" with
                    Kind = DataSubjectRequestKind.Erase
            }

            let payload = DsrJobPayload.serialise "" request
            let! result = (handler :> IJobHandler).Execute(jobContext "team-1" payload)

            Expect.equal result Success "erasure job succeeds"

            let completed = audit.Events |> List.filter (fun e -> e.Kind = ErasureCompleted)

            Expect.equal completed.Length 1 "one ErasureCompleted audit event"
            Expect.equal completed[0].Properties["totalAffected"] "8" "total affected across handlers"
        }
    ]

[<Tests>]
let tests =
    testList "ToolUp.Platform.DataSubject" [
        IBackgroundExportStoreContract.tests "BlobBackedBackgroundExportStore" factory
        implTests
    ]