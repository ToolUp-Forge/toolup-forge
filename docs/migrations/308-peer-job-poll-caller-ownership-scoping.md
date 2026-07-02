# Peer job-poll caller-ownership scoping

**Ships in:** ToolUp.InterPlatform (Phase 308).

## What changes

The long-running peer-call poll route (`GET /peer/v1/{contractId}/jobs/{jobId}`)
now binds a parked result to the peer that scheduled it. Previously any
*validated* peer that knew (or guessed) a `jobId` could read another peer's
completed federated computation result — possession of the server-minted Guid
was the only barrier. Now:

- The dispatch side stamps the validated scheduling caller's `PeerId` into the
  job payload (`PeerJobPayload`), and the job handler stamps it onto the parked
  record (`PeerJobRecord`).
- The poll route compares the recorded owner against the polling principal's
  `PeerId`. A mismatch returns `PeerUnauthorized` (HTTP 401, no result body).
- Owner semantics are byte-unchanged: an absent record (not finished, or never
  existed) still reports `Pending`; the owner still reads its terminal status.
  An unauthenticated poll stays 401.

## Diff to apply

Nothing, for a deployment that composes the peer substrate via
`PeerServerApp.run` and only ever polls its own jobs — the default
`BlobPeerJobResultStore` is upgraded in place and the client transport
(`HttpPeerClient.PollJob` / `PeerJobHandle`) is unchanged on the wire.

The only break is code that **implements or calls `IPeerJobResultStore`
directly** (typically test doubles):

```fsharp
// Before
abstract SaveResult: scopeId: string * jobId: PeerJobId * status: PeerJobStatus<string> -> Async<unit>
abstract TryGetResult: scopeId: string * jobId: PeerJobId -> Async<PeerJobStatus<string> option>

// After — SaveResult gains the owner; TryGetResult returns the owner-stamped record
abstract SaveResult: scopeId: string * jobId: PeerJobId * ownerPeerId: string * status: PeerJobStatus<string> -> Async<unit>
abstract TryGetResult: scopeId: string * jobId: PeerJobId -> Async<PeerJobRecord option>
// where: type PeerJobRecord = { OwnerPeerId: string; Status: PeerJobStatus<string> }
```

## Upgrade-window edge

A long-running job scheduled *before* the upgrade and still in flight *after*
it carries the old raw-args payload. The handler degrades gracefully (the args
still execute) but parks the record owner-unknown, which the poll route
refuses to everyone (fail closed). Re-issue the call after upgrading if this
window matters; poll-driven peer jobs are typically seconds-lived.

## Verification

- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 308"`
  — the scheduling peer reads its own terminal status; a different validated
  peer is refused with no payload; an unauthenticated poll stays 401.
- `--filter-test-list "IPlatformPeerContract"` — dispatch→handler owner
  threading (owner stamped into the scheduled payload and onto the parked
  record).

## Rollback

Revert the Phase 308 commit. Records written in the new `PeerJobRecord` shape
fail to parse under the old reader and report `Pending` — the same
upgrade-window behaviour in reverse, with the same re-issue remedy.
