# 1062 — Audit failure is data at the seam, and the permission store answers a `Result`

## What changes

| Seam | Before | After |
|---|---|---|
| `IAuditLog.Record` (Core) | `scopeId: string * AuditEvent -> Async<unit>`; raised under `RefuseAction`, swallowed otherwise | `AuditScope * actor: AuditSubject * AuditEvent -> Async<Result<unit, AuditWriteError>>`; never raises. `Error` under `RefuseAction` only (`StoreUnavailable`, `ChainHeadConflict`); `Ok` under `LogAndContinue` / `DegradeToFile`; `NoOpAuditLog` (now in Core) answers `Ok` |
| `GetAuditTrail` | `scopeId: string` | `AuditScope` |
| The scope | a string | `AuditScope.team` / `platform` / `ofKey`, `StorageScope.auditScope`, `ResolvedScope.auditScope`; `AuditScope.shard key` for a string-keyed store's shard (rendered verbatim, never parsed) |
| The actor | derived by the replicator from the stream key (`AuditSubject.fromScopeId`, deleted) | passed to `Record` and persisted with the row: `AuditSubject.fromSubject` of the request's principal, else `AuditSubject.system "<component>"` (new `SystemAudit` case). Rows at rest without one decode unchanged, as `AuditSubject.unattributed` |
| Call sites | `do! log.Record(s, e)`, a `try … with _ -> ()`, or a `schedule` parameter | `AuditRow.emit log scope actor e` (propagate), `… \|> AuditRow.orRefuse` (no error channel: raises `AuditWriteRefusedException`), `… \|> AuditRow.bestEffort` (the row records a decision already taken) |
| `IAuditLog option` (every parameter, field, `BlobStorageAuthAudit.record`) | `Some log` / `None` | `IAuditLog`; pass `NoOpAuditLog()` for "no audit"; resolve with `Background.resolveAuditLog services` |
| `AuditEnvelope.fromScopeId`, `AuditStreamKey` | | `AuditEnvelope.create actor scopeId at event`; `AuditScope.streamKey` |
| `IPermissionStore` reads | raised `PermissionStoreUnavailableException` (deleted) | answer `Async<Result<_, PermissionStoreError>>`; absent is `Ok TeamPermissions.empty`, decided in the store; `UpdateTeamPermissions(teamId, change)` is one guarded read-modify-write |
| Grant / consent / dual-control ceremonies, `ClassificationGate` | `schedule` parameter; `Async<Map<…>>` | no `schedule`; `redactFields` / `recordWrites` answer a `Result` (a read whose row fails is refused under `RefuseAction`) |
| `EventStoreAuditLog` | permission chain head derived from the stream per write | `chainHeadStorage` keeps it at `_platform/audit-chain/{streamKey}.json`, one compare-and-swap per row |

## The diff, per shape

```fsharp
member _.Record(scopeId, audit) = async { do! write scopeId audit }                  // before
member _.Record(AuditScope.StreamKey scopeId, actor, audit) = async {                // after
    do! write scopeId actor audit
    return Ok() }                                                                     // Error on a lost row

do! log.Record(team.Id, event)                                                        // before
do! AuditRow.emit log (AuditScope.team teamId) (AuditSubject.fromSubject ctx.Subject) event
    |> AuditRow.orRefuse                                                              // after

let store = BlobEntityStore(dos, blob, registry, None)                                // before
let store = BlobEntityStore(dos, blob, registry, NoOpAuditLog())                      // after

member _.GetTeamPermissions teamId = async { return doc }                             // before
member _.GetTeamPermissions teamId = async { return Ok doc }                          // after
```

`dotnet run --project Build.fsproj -- Codemod --phase 1062` rewrites an implementer's `Record` / `GetAuditTrail`
parameters, its unit return, `IAuditLog option`, `AuditEnvelope.fromScopeId` and the `Some auditLog` / `schedule`
arguments, and wraps a custom `IPermissionStore`'s reads in `Ok`; it reports what a person decides: each call's
scope and actor, and whether it propagates, `orRefuse`s or `bestEffort`s.

## Verification

`ToolUp.Platform.Tests.Audit` (`AuditEmitTests`: each mode, a classified read refused under `RefuseAction`, the actor
round-trip and a pre-change row decoding unchanged; `PermissionAuditChainTests`: a mixed-era stream verifies),
`ToolUp.Platform.Tests.Scope` (`IPermissionStoreContract` pins absent / unreadable / corrupt on every binding).

## Rollback

Revert the phase's commits. Rows written with an actor carry it as an `_actor` property after the event's own
fields, which every earlier reader ignores; permission documents decode unchanged, and an `audit-chain/` head
document is ignored by the earlier reader.
