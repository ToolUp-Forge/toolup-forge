// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DataSubject.Tests.InProcess.AsyncExportApiTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.IDataExporter
open ToolUp.Platform.DataSubjectRequestApi
open ToolUp.Platform.DataSubjectRequestApiHandler
open ToolUp.Platform.DataSubject.Tests.Support.Doubles

// ─── Phase 9h.A — async RequestExportAsync end-to-end ───────────────────
//
// Drives the `IDataSubjectRequestApi` async export surface through a
// minimal in-line fake `IJobScheduler` that runs the registered handler
// synchronously on `TriggerOnce` — so the export completes during the
// `RequestExportAsync` call and the subsequent `GetStatus` / `Download`
// observe a `Ready` ticket. Pins the acceptance contract: a downloaded
// async ticket is byte-identical to the synchronous `RequestExport`
// envelope.

/// Fake scheduler: registers handlers, and on `TriggerOnce` runs the
/// scheduled job's handler in-line with a synthesised `JobContext`. Only
/// the members the async export path exercises are meaningful; the rest
/// return inert defaults.
type private InlineScheduler() =
    let handlers = ConcurrentDictionary<string, IJobHandler>()
    let jobs = ConcurrentDictionary<JobId, string * string>() // jobId -> (handler, payload)

    member _.Register(name: string, handler: IJobHandler) = handlers[name] <- handler

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = handlers[name] <- handler

        member _.RegisterHandlerAsync(name, handler) = async {
            handlers[name] <- handler
            return Ok()
        }

        member _.Schedule(registration) = async {
            let jobId = Guid.NewGuid()
            jobs[jobId] <- (registration.Handler, registration.Payload)
            return Ok jobId
        }

        member _.TriggerOnce(scopeId, jobId, _byUserId) = async {
            match jobs.TryGetValue jobId with
            | true, (handlerName, payload) ->
                match handlers.TryGetValue handlerName with
                | true, handler ->
                    let! _ = handler.Execute(jobContext scopeId payload)
                    return Ok()
                | false, _ -> return Error $"handler {handlerName} not registered"
            | false, _ -> return Error $"job {jobId} unknown"
        }

        member _.Cancel(_, _) = async { return () }
        member _.Disable(_, _) = async { return () }
        member _.Enable(_, _) = async { return () }
        member _.Get(_, _) = async { return None }
        member _.ListJobs(_) = async { return [] }
        member _.GetRecentRuns(_, _, _) = async { return [] }
        member _.NotifyEventWritten(_, _, _) = async { return () }

/// Phase 229 — the DSR endpoints gate on Platform-Admin authority
/// (`AccessContext.canModifyPlatformConfig`). These tests drive the admin
/// happy path, so the actor's context carries `PlatformRole.PlatformAdmin`.
let private adminContext: AccessContext = {
    AccessContext.unrestricted (AuthenticatedUser "admin") with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

let private mkAsyncApi (exporters: IDataExporter list) =
    let store = BlobBackedBackgroundExportStore.create (InMemoryBlobStorage())
    let scheduler = InlineScheduler()
    let audit = CapturingAudit()

    scheduler.Register(DsrJobs.ExportHandler, DSRExportJobHandler.create store exporters audit.Callback (NoOpLogger()))

    let deps: DsrAsyncDeps = {
        Store = store
        Scheduler = scheduler
        Notify = fun _ _ -> async { return () }
    }

    let api =
        DataSubjectRequestApiHandler.create
            exporters
            []
            ErasurePolicy.Tombstone
            "team-1"
            "admin"
            adminContext
            audit.Callback
            (Some deps)

    api, store, audit

let private input: ExportRequestInput = {
    SubjectUserId = "u1"
    TeamId = Some "team-1"
    Reason = "Article 15 export"
}

[<Tests>]
let tests =
    testList "Phase 9h.A — async RequestExportAsync" [
        testCaseAsync "RequestExportAsync returns a ticket; the job runs; status Ready; download = sync shape"
        <| async {
            let exporters: IDataExporter list = [ FakeExporter "kb" :> IDataExporter; FakeExporter "rag" ]
            let api, store, _ = mkAsyncApi exporters

            let! ticketResult = api.RequestExportAsync input

            let ticket =
                match ticketResult with
                | Ok t -> t
                | Error e -> failtestf "RequestExportAsync must succeed; got %s" e

            // The InlineScheduler ran the handler during TriggerOnce, so
            // the ticket is already Ready.
            let! statusResult = api.GetExportStatus ticket

            match statusResult with
            | Ok(ExportStatus.Ready _) -> ()
            | other -> failtestf "ticket should be Ready; got %A" other

            let expectedEnvelope =
                serialiseSegments [
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

            let! dl = api.DownloadExport ticket

            match dl with
            | Ok bytes ->
                Expect.equal bytes expectedEnvelope "async download matches the synchronous RequestExport envelope"
            | Error e -> failtestf "DownloadExport must succeed; got %s" e
        }

        testCaseAsync "Cancel before the job runs leaves the ticket Cancelled (job skips Complete)"
        <| async {
            // Drive the store + handler directly so we can cancel between
            // Begin and the handler running.
            let store = BlobBackedBackgroundExportStore.create (InMemoryBlobStorage())
            let audit = CapturingAudit()
            let request = sampleRequest "u1"
            let! ticket = store.BeginExport("team-1", request)

            // Operator cancels mid-flight.
            do! store.Cancel ticket

            let exporters: IDataExporter list = [ FakeExporter "kb" :> IDataExporter ]

            let handler =
                DSRExportJobHandler.create store exporters audit.Callback (NoOpLogger())

            let payload = DsrJobPayload.serialise ticket request
            let! result = (handler :> IJobHandler).Execute(jobContext "team-1" payload)

            Expect.equal result Success "a cancelled ticket's job completes as a no-op (Success)"

            let! status = store.GetStatus ticket

            Expect.equal
                status
                ExportStatus.Cancelled
                "ticket stays Cancelled — the job does not overwrite it with Ready"
        }

        testCaseAsync "Async methods Error when no async deps are composed"
        <| async {
            let exporters: IDataExporter list = [ FakeExporter "kb" :> IDataExporter ]

            let api =
                DataSubjectRequestApiHandler.create
                    exporters
                    []
                    ErasurePolicy.Tombstone
                    "team-1"
                    "admin"
                    adminContext
                    (CapturingAudit()).Callback
                    None

            let! r = api.RequestExportAsync input

            match r with
            | Error _ -> ()
            | Ok _ -> failtest "RequestExportAsync must Error when async DSR is not enabled"
        }
    ]