# Migration — Phase 9h.A: `IBackgroundExportStore` + job-backed DSR

**Status:** new substrate in `ToolUp.Platform.Server`. Additive — no
consumer is required to act. The synchronous Phase 9h `RequestExport`
path is unchanged; this adds the building blocks for running DSR
export/erasure on `IJobScheduler` for large tenants.

## What's shipped

- `IBackgroundExportStore` + `BlobBackedBackgroundExportStore` (default,
  `IBlobStorage`-backed, blob tickets with TTL).
- `DSRExportJobHandler` / `DSRErasureJobHandler` (`IJobHandler`s for
  `_platform.datasubject.export` / `.erasure`).
- `serialiseSegments` lifted to a public module-level function so an
  async export envelope is byte-identical to the synchronous one.

## How to use (manual wiring — the compose helper is a follow-up)

```fsharp
let store = BlobBackedBackgroundExportStore.create blobStorage
scheduler.RegisterHandler(
    DsrJobs.ExportHandler,
    DSRExportJobHandler.create store exporters auditOnDsr logger)
scheduler.RegisterHandler(
    DsrJobs.ErasureHandler,
    DSRErasureJobHandler.create erasureHandlers auditOnDsr logger)

// On an export request:
let! ticket = store.BeginExport(scopeId, request)
let! _ = scheduler.Schedule { /* Handler = DsrJobs.ExportHandler;
                                 Payload = DsrJobPayload.serialise ticket request; ... */ }
// client polls:
let! status = store.GetStatus ticket   // Preparing → Ready size
let! bytes  = store.Download ticket     // Ok envelope | Error status
```

## Verification

- `dotnet build` clean; the `ToolUp.Platform.DataSubject.Tests` pack is 11
  tests green (contract round-trip, TTL expiry, job-handler envelope
  matching the sync shape, failure classification, erasure audit).

## Async routing (shipped — Wave 8 close-out)

The deferred wiring has landed. `DataSubjectRequestMode.Enabled` now carries
a `DataSubjectRequestConfig` record (`{ Policy; Async }`); set `Async = true`
to route exports through the background substrate:

```fsharp
// before (synchronous-only, Phase 9h):
{ config with DataSubjectRequests = DataSubjectRequestMode.Enabled ErasurePolicy.Tombstone }

// after (Phase 9h.A — record payload; Async opts into the job path):
{ config with
    DataSubjectRequests =
        DataSubjectRequestMode.Enabled(DataSubjectRequestConfig.background ErasurePolicy.Tombstone) }
// or the synchronous-equivalent record form:
//   DataSubjectRequestConfig.sync ErasurePolicy.Tombstone   ({ Policy = …; Async = false })
```

**What `Async = true` adds:**
- Four new `IDataSubjectRequestApi` methods: `RequestExportAsync`
  (returns an `ExportTicket` immediately), `GetExportStatus`,
  `DownloadExport`, `CancelExport`. The synchronous `RequestExport` is
  retained and unchanged.
- `ComposeJobs.registerDataSubjectRequestJobs` registers the blob-backed
  `IBackgroundExportStore` singleton + the `DSRExportJobHandler` /
  `DSRErasureJobHandler` on the scheduler (via a startup `IHostedService`
  so the registered exporter / erasure-handler lists are resolvable).
  Requires `JobScheduler = InProcessJobScheduler` (or a distributed
  companion); `NoJobScheduler` logs a `Warn` and the async methods return
  `Error` (the synchronous path is unaffected).
- Coarse progress notifications under
  `DsrNotifications.ExportProgressKey` (`Preparing → Ready / Failed /
  Cancelled`) via `INotificationChannel`.
- `DataSubjectRequestAdminUI` Export tab: a "Run as a background job"
  toggle that submits async, polls until ready, downloads, and offers a
  mid-run cancel.

**Migration note:** `DataSubjectRequestMode.Enabled` changed payload from
`ErasurePolicy` to `DataSubjectRequestConfig`. A consumer that constructed
`Enabled ErasurePolicy.Tombstone` updates to
`Enabled(DataSubjectRequestConfig.sync ErasurePolicy.Tombstone)` (byte-for-byte
behaviour preserved by `Async = false`). `ExportTicket` + `ExportStatus`
moved from `IBackgroundExportStore.fs` to Core (`DataSubjectTypes.fs`) so
the Fable-crossing contract can name them — no consumer-visible namespace
change (`ToolUp.Platform`).

## Rollback

Remove the handler registrations + the `IBackgroundExportStore` usage. No
persisted state beyond the ticket blobs under
`_platform/data-subject-requests/`, which TTL-expire.
