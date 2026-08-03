# Migration — Phase 310: long-running peer call terminal-outcome audit

**Status:** one net-new audit event plus two widened `ToolUp.InterPlatform` records. **Source-breaking for any consumer that constructs `PeerJobFusion` or `PeerJobPayload`, or that matches exhaustively on `AuditEvent`** — see §3. Every runtime default is unchanged: a deployment that does not host long-running peer contract methods emits nothing new, and a deployment that does gains one extra audit row per finished job.

## Why

The receiver's audit trail stopped at dispatch for a long-running call. A `LongRunning` contract method (`… -> Async<PeerJobHandle<'T>>`) returns from `peer.Handle` as soon as the backing job is *scheduled*, so `JsonRpcPeerHost.auditPeerCall` recorded `Succeeded = true, Outcome = "ok"` however the background computation later ended. The real terminal status was written only to the `IPeerJobResultStore`, with no audit emission at all.

That made the Phase 18a audit-transparency contract ("I asked for X — confirm what you logged") report every long-running call as `ok`, including the ones that failed — and made its `FailuresOnly = true` query answer *empty* for a peer whose every long-running call had been refused. The trail was blind to the outcome that mattered.

## 1. New audit event — `PeerJobCompleted`

`ToolUp.Platform.AuditEvent` gains a case:

```fsharp
| PeerJobCompleted of PeerJobCompletedPayload

type PeerJobCompletedPayload = {
    ContractId: string
    MethodName: string
    CallerPeerId: string      // the peer that SCHEDULED the call
    RootRequestId: string     // same correlation id as the schedule-time row
    JobId: System.Guid        // the id the caller polls with
    Succeeded: bool
    Outcome: string           // "ok", or the PeerError DU case name
    OccurredAt: System.DateTimeOffset
}
```

Emitted once per finished job by `PeerJobHandler.Execute`, under `SourceModule = "_platform.peer"` and the `_platform` scope — the same family and scope as `PeerCallCompleted`. Wire `EventType` discriminator is `"PeerJobCompleted"`; the codec-registry row is in `ToolUp.Platform.AuditLog.auditEventCodecs`.

PII-free on the same terms as the rest of the peer family: peer ids, a correlation id, a job id and a short outcome label. The computed result never travels.

**A distinct case rather than a marker field on `PeerCallCompletedPayload`.** The two rows answer different questions ("the receiver accepted the call" vs "the receiver's computation finished like this") and land minutes apart. A phase marker on the existing payload would have changed the wire shape of every *immediate* call's row for the sake of the minority that are long-running; a new case leaves `PeerCallCompletedPayload` byte-for-byte identical (GP 11).

**Expiry is deliberately not audited.** Phase 316's retention can retire a parked result before anyone polls it, but that is the lifetime of the *record*, not the outcome of the *call*: this row is written when the computation finishes, so the trail stays truthful whether or not the result is ever collected. Auditing expiry would also mean emitting from `IPeerJobResultStore.TryGetResult` — a write inside the poll route's read path, which is the shape the estate's post-response side-effect hazard lives in. (The emission this phase *does* add runs on the scheduler's own async, with no HTTP response started, so that hazard does not apply to it.)

## 2. Phase 18a transparency now answers with both rows

`PeerAuditContractHost.project` folds `PeerCallCompleted` **and** `PeerJobCompleted` into `PeerAuditEntry`, and `registration`'s dispatch makes two `GetAuditTrail` reads (one per event type — the interface takes a single event-type filter, and dropping it would drag every `_platform` audit row through the projection).

**No wire change to `IPeerAuditApi` / `PeerAuditQuery` / `PeerAuditEntry`.** A calling peer sees the two rows for one long-running call joined by `RootRequestId`; the terminal row is the later `OccurredAt`. Terminal rows are caller-scoped by the same `CallerPeerId` check as schedule-time rows, so a peer can no more read another's terminal outcomes than its accepted calls.

The practical effect for a caller: `FailuresOnly = true` now returns exactly the terminal failures, counted once. It previously returned nothing for a long-running call however badly it had gone.

## 3. Breaking changes — two record constructors

Both are `ToolUp.InterPlatform` records that gain a field:

| Type | Was | Now |
|---|---|---|
| `PeerJobFusion` | `{ Scheduler; ResultStore }` | `+ AuditLog: IAuditLog option` |
| `PeerJobPayload` | `{ OwnerPeerId; ArgsJson }` | `+ RootRequestId: string` |

**Who is affected.** `PeerJobFusion` is *composed by the SDK* and handed to a `withContract` builder — a consumer receives it and passes it through, and only a consumer that builds one by hand (typically in a test double) has to change. `PeerJobPayload` is constructed by `scheduleDispatch` and read by `PeerJobHandler`; a consumer touching it is inspecting the substrate's internal job envelope.

```fsharp
// If you construct a fusion in a test host:
let fusion: PeerJobFusion = {
    Scheduler = myScheduler
    ResultStore = myStore
    AuditLog = None          // ← add; Some log to capture terminal rows
}
```

`AuditLog = None` is the byte-for-byte prior behaviour: no terminal row is recorded, exactly as before this phase. `PeerCompose` resolves the registered `IAuditLog` when there is one, so a normally-composed deployment gets the emission with no code change.

**`PeerJobHandler`'s original 4-argument constructor is preserved** as an explicit secondary constructor (the same technique `BlobPeerJobResultStore` used for its Phase 316 overloads), so no call site of it needs to change; it degrades to no audit emission.

**Exhaustive `AuditEvent` matches.** A consumer with an exhaustive `match` over `AuditEvent` — an audit sink, formatter or replicator — gains an unhandled case and will not compile until it handles `PeerJobCompleted`. Forge's own sinks match on the wire `EventType` string, so none needed a change.

**Binary compatibility.** Adding a DU case to a persisted audit type is binary-breaking for a package already extracted into a NuGet cache. On the shared local feed this surfaces as a runtime `InvalidCastException` from a stale consumer package rather than a compile error — repack the affected projects (`pack-all.ps1 -ClearCache`) rather than reading the cast failure as a real type error. No SDK `<Version>` bump lands with this phase; the batch it belongs to is cut as one release, and that cut is a **minor** bump under the SemVer-on-`0.x` policy.

## 4. In-flight jobs across the upgrade

A job scheduled before this phase carries a payload with no `RootRequestId`. A missing JSON field is *absence*, not a parse failure, so it deserialises to `null`; `PeerJobHandler.Execute` normalises it to `""`. Such a job still records its terminal row — an uncorrelated terminal outcome beats a lost one — and the field is never `null` on the wire. No drain or restart is required before upgrading.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — 5,546 passed / 18 ignored / 0 failed (12 net-new cases).
- New coverage in `Contracts/IPlatformPeerContract.fs`: the correlation id threading onto the scheduled payload; the terminal row's full content on a completed job; the specific `PeerError` case name on a refused job (asserted as `"PeerUnauthorized"`, not merely "not ok"); the schedule-time↔terminal correlation end to end; and three controls — a host with no audit log records nothing, a throwing audit store never changes the job result, and a pre-310 payload still records.
- New coverage in `Contracts/IPeerAuditTransparencyContract.fs`: both rows returned for one long-running call; the terminal row's outcome; `FailuresOnly` surfacing a failure the schedule-time row called `ok`; cross-peer scoping of terminal rows; and the host's second `GetAuditTrail` read.
- Each emission probe was verified to go red with the emission removed, paired against the controls above, which stayed green.

## Rollback

Set `AuditLog = None` on the composed `PeerJobFusion` (or register no `IAuditLog`): the handler records nothing and behaviour is byte-for-byte pre-310. The `PeerJobCompleted` case and the transparency read stay in place harmlessly — with no rows of that type, `project` folds nothing.
