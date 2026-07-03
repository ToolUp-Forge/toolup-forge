# Migration 271 — neutral tree-patch transport envelope

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

Phase 112's `ILiveChannel` pushes **opaque** frames over scope-isolated SSE, and Phase 264 ships the
read-side, but there was no neutral way to stream **structural diffs** (insert / remove / move /
update) *reliably* — a consumer wanting ordered incremental updates had to hand-roll sequencing, gap
detection, and resync. This phase layers that reliability contract over Phase 112.

New surface in `src/ToolUp.Platform.Server/Server/Notifications/TreePatchChannel.fs` (namespace
`ToolUp.Platform`):

- `TreePatchEnvelope` — `{ Seq: int64; BaseSeq: int64 option; Payload: string }`. A monotonic
  per-session `Seq`; `BaseSeq = Some prev` for an incremental diff, `None` for a full **snapshot** (a
  resync reply that re-bases the stream). `Payload` is the tree language's own opaque diff wire format
  (GP 1). `TreePatchEnvelope.{isSnapshot, encode, decode}` frame it onto the `ILiveChannel` wire as a
  JSON string (the `FableConverters` STJ shape a browser client decodes).
- `TreePatchReceipt` — `Accept of newLastSeq | Duplicate | Gap of expected * got`. The client's
  decision after classifying an incoming envelope.
- `TreePatchReceiver.classify : lastSeen -> envelope -> TreePatchReceipt` — a **pure** gap-detector
  (GP 12 rule 4 — stateless; the caller threads its durable watermark). A snapshot is always accepted;
  an incremental is accepted only at exactly `lastSeen + 1`, a lower `Seq` is a `Duplicate`, a higher
  one a `Gap`.
- `ITreePatchChannel` — `Push : payload -> Async<TreePatchEnvelope>` (assigns the next `Seq`, frames
  it, pushes it), `Ack : seq -> Async<unit>` (advances the acked watermark), `RequestResync : unit ->
  Async<TreePatchEnvelope>` (pushes a full snapshot from the snapshot source and resumes), plus
  `LastSeq` / `AckedThrough` diagnostics.
- `InMemoryTreePatchChannel` — the in-process, single-node default (the same posture as
  `InMemoryLiveSessionHost`). A distributed implementation owns a durable per-session sequence + a
  retained-patch buffer trimmed to the acked watermark.
- `TreePatchChannel.createInMemory` / `TreePatchChannel.forSession` — wrap a resolved `ILiveChannel`
  (the latter resolves + wraps via `ILiveSessionHost.TryGetChannel`, so a patch channel is **never**
  resolvable across scopes — GP 4).

**Scope isolation is inherited, not re-checked.** The channel wraps one `ILiveChannel`, already
resolved within a single `(scopeId, sessionId)` partition, so a patch to scope A can never reach
scope B.

## How to adopt (opt-in)

```fsharp
// Server side — one channel per live session, from the authoritative tree.
match! TreePatchChannel.forSession host scopeId sessionId (fun () -> serialiseFullTree ()) with
| Some channel ->
    // Stream incremental diffs; each Push assigns the next monotonic Seq.
    let! _ = channel.Push (serialiseDiff opsSinceLast)
    ...
| None -> ()  // no such session in this scope

// Client side — pure gap detection; thread your durable watermark.
match TreePatchReceiver.classify lastSeen (TreePatchEnvelope.decode frame) with
| TreePatchReceipt.Accept newSeq -> applyPatch env.Payload; lastSeen <- newSeq
| TreePatchReceipt.Duplicate     -> ()                       // redelivery — ignore
| TreePatchReceipt.Gap _         -> requestResync ()         // → snapshot, re-base, resume
```

A deployment that never constructs a `TreePatchChannel` pays nothing (GP 13) and is byte-for-byte
unchanged (GP 11).

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "TreePatchChannel"
```

Server-tier only — no Fable-client surface to verify.

## Rollback

Delete `TreePatchChannel.fs` + its `<Compile>` entry, `InProcess/TreePatchChannelTests.fs` + its
`<Compile>` and `Program.fs` registration. No runtime impact on any deployment that never streamed
tree patches.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in server-driven incremental-patch
transport. No current matrix consumer streams typed-tree diffs; a deployment that composes no channel
is byte-for-byte unchanged (GP 11/13).
