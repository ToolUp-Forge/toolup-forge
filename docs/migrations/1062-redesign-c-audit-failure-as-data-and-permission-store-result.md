# 1062 — Audit failure is data at the seam, and the permission store answers a `Result`

## What changes

| Seam | Before | After |
|---|---|---|
| `IAuditLog.Record` (Core) | `Async<unit>`; raised `AuditLog.AuditWriteRefusedException` under `RefuseAction`, swallowed otherwise | `Async<Result<unit, AuditWriteError>>`; never raises. `Error` under `RefuseAction` only (`StoreUnavailable`, or `ChainHeadConflict` for a permission row); `Ok` under `LogAndContinue` / `DegradeToFile`; `NoOpAuditLog` answers `Ok` |
| Call sites | `do! log.Record(s, e)`, a `try … with _ -> ()`, or a `schedule` parameter | `AuditRow.emit log s e` (propagate the `Result`), `… \|> AuditRow.orRefuse` (no error channel: raises `AuditWriteRefusedException`, now in Core), `… \|> AuditRow.bestEffort` (the row records a decision already taken) |
| `Background.startAudit` over a row | `Async<unit>` | `Background.startAuditRow` (a refused row reaches `warn`) |
| `IPermissionStore` reads | raised `PermissionStoreUnavailableException` (deleted) | `GetTeamPermissions` / `GetEffectivePermissions` / `GetModuleExposure` answer `Async<Result<_, PermissionStoreError>>` (`Unreadable \| Quarantined \| Unavailable`); an absent document is `Ok TeamPermissions.empty`, decided in the store |
| `IPermissionStore` writes | read, then `Set*` | new `UpdateTeamPermissions(teamId, change)`: one guarded read-modify-write; the decorators validate inside it, with no second read |
| `GrantPolicyGuard.guardDispatch`, `GrantConsentStore.guardDispatchWithConsent` / `resolveLive` / `GrantConsents.*`, `AdminMutationApproval.propose/approve/reject/listPending` | `auditLog: IAuditLog option` and `schedule: Async<unit> -> unit` | `auditLog: IAuditLog`, no `schedule` (pass `NoOpAuditLog` for "no audit") |
| `GrantPolicyPermissionStore`, `DualControlPermissionStore`, `StoreCounterpartyConsentOracle` | `?auditLog` | `auditLog: IAuditLog` (required) |
| `GrantNotificationObserver` | last argument `schedule` | removed; the fan-out is awaited after the commit and contains its own failures |
| `ClassificationGate.redactFields` / `recordWrites` | `Async<Map<…>>` / `Async<unit>` | `Async<Result<Map<…>, AuditWriteError>>` / `Async<Result<unit, AuditWriteError>>`: under `RefuseAction` a read whose row fails is refused |
| `EventStoreAuditLog` | head of the permission chain derived from the stream per write | optional `chainHeadStorage: IBlobStorage` keeps the head at `_platform/audit-chain/{streamKey}.json`, one compare-and-swap per permission row; seeded once from the stream |

## The diff, per shape

```fsharp
// A custom IAuditLog
member _.Record(scopeId, audit) = async { do! write audit }            // before
member _.Record(scopeId, audit) = async { do! write audit; return Ok() } // after (Error on a lost row)

// A call site
do! log.Record(scopeId, event)                                            // before
do! AuditRow.emit log scopeId event |> AuditRow.orRefuse                  // after (or propagate the Result)

// A custom IPermissionStore
member _.GetTeamPermissions teamId = async { return doc }                 // before
member _.GetTeamPermissions teamId = async { return Ok doc }              // after (Error when unreadable)
member _.UpdateTeamPermissions(teamId, change) = async {                  // new
    match change (current teamId) with Ok next -> return! write teamId next | Error e -> return Error e }

// A ceremony call
approve store apply (Some log) Async.Start now teamId requestId approver  // before
approve store apply log now teamId requestId approver                     // after
```

`dotnet run --project Build.fsproj -- Codemod --phase 1062` rewrites the `Record` implementations' unit returns,
`Some auditLog` / `None` arguments and the `schedule` argument, and wraps a custom `IPermissionStore`'s reads in `Ok`;
it reports the sites a person must decide (`do! log.Record`: propagate, `orRefuse` or `bestEffort`).

## Verification

`ToolUp.Platform.Tests.Audit` (`AuditEmitTests`: each mode, and a classified read refused under `RefuseAction`;
`PermissionAuditChainTests`: a mixed-era stream verifies), `ToolUp.Platform.Tests.Scope` (`IPermissionStoreContract`
pins absent / unreadable / corrupt on every binding; `grantModuleAccess` answers `StoreUnavailable`, never throws).

## Rollback

Revert the phase's commits. No document at rest changes shape: permission documents and audit envelopes decode
unchanged, and an `audit-chain/` head document left behind is ignored by the earlier reader, which derives the head
from the stream as before.
