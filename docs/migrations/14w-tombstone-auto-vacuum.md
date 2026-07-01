# Phase 14w — Tombstone auto-vacuum scheduler

**Ships in:** `ToolUp.RAG.Server` (`RAGVacuumJobHandler`, `RAGCompose`/`RAGServerApp`,
`RagConfigValidator`). **Additive / opt-in — no consumer change required to stay on the new SDK
version.**

## What changes

`IVectorStore.DeleteChunk` is a soft-delete (Phase 14h) — it stamps `_deletedAt` and keeps the
entry so `RestoreChunk` can un-delete within the retention window. `Vacuum` hard-removes tombstones
past the window, but until this phase it was only ever driven by an operator's own admin path, so a
long-running replica accumulated soft-deleted chunks indefinitely and grew toward OOM.

`RAGServerApp.withVacuumSchedule` closes that gap: it registers a `RAGVacuumJobHandler` on the
`IJobScheduler` that, on a cron (default **daily 03:00 UTC**), enumerates every scope via
`IVectorStore.ListScopes()` and calls `Vacuum(scope, now - retention)` per scope. Every scope that
purges anything emits a `KnowledgeVacuumCompleted` audit event with `(ScopeKey, ChunksRemoved,
BytesReclaimed, DurationMs)`.

### New surface (all additive)

- `RAGServerApp.withTombstoneRetention (retention: TimeSpan)` — tombstone window (default 7 days,
  floored at 1 minute).
- `RAGServerApp.withVacuumSchedule` — enable on the default daily-03:00-UTC cron.
- `RAGServerApp.withVacuumScheduleCron (cron: string)` — enable on a custom 5-field cron.
- New module `ToolUp.RAG.RAGVacuumJobHandler` (`VacuumHandlerName`, `DefaultVacuumCron`,
  `RAGVacuumDeps`, `create`).
- New `RagConfigValidator.VacuumScheduleValidator` — warns when a schedule is set without a
  scheduler, or when a persistent deployment has no schedule at all.
- Two new `RAGServerApp` record fields (`TombstoneRetention`, `VacuumSchedule`) — construct via
  `RAGServerApp.create` / `createFrom` + the `with*` helpers (unchanged), not the positional ctor.

## How to adopt (opt-in)

No change is needed to stay on the new SDK version. To enable the auto-vacuum:

```fsharp
RAGServerApp.create factory providerProfile embedder
|> RAGServerApp.withConfig { config with JobScheduler = InProcessJobScheduler }  // required
|> RAGServerApp.withTombstoneRetention (TimeSpan.FromDays 14.0)                  // optional (default 7 days)
|> RAGServerApp.withVacuumSchedule                                              // daily 03:00 UTC
|> RAGServerApp.run
```

**Requires `ServerConfig.JobScheduler = InProcessJobScheduler`** (or a distributed scheduler
companion). With the schedule but no scheduler, the sweep never fires; with neither, tombstones are
reclaimed only by a manual `IVectorStore.Vacuum` call. The `VacuumScheduleValidator` warns at
startup in both cases (HealthMonitorUI admin tab / `/dev/inspect` Validators panel).

## Verification

- `dotnet build src/ToolUp.RAG.Server/ToolUp.RAG.Server.fsproj`.
- `dotnet run --project src/ToolUp.Platform.Tests -- --filter-test-list "14w"` (the
  `RAGVacuumJobHandler` pack: a sweep drops tombstones to zero and emits `KnowledgeVacuumCompleted`;
  a no-tombstone scope stays silent).

## Rollback

Drop the `withVacuumSchedule` / `withVacuumScheduleCron` call — no vacuum job is registered and
behaviour reverts to the pre-14w manual-`Vacuum` contract. The two record fields default to
`TombstoneRetention = 7 days` / `VacuumSchedule = None`, so an app that never calls the helpers is
byte-for-byte unaffected.
