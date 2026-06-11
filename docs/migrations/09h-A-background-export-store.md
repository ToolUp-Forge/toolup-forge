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

## Deferred wiring

`ServerConfig.DataSubjectRequests.Async`, the async `IDataSubjectRequestApi`
routing (`202 Accepted` + poll), the compose registration helper, and the
admin-UI tickets tab land when the shared composition surface is quiet —
they touch `SDK.Shared.fs` + `BuildRouteHandlers.fs`. The substrate they
drive is shipped and tested.

## Rollback

Remove the handler registrations + the `IBackgroundExportStore` usage. No
persisted state beyond the ticket blobs under
`_platform/data-subject-requests/`, which TTL-expire.
