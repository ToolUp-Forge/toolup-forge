# Peer job-result retention / TTL

**Ships in:** ToolUp.InterPlatform (Phase 316).

## What changes

`BlobPeerJobResultStore` used to write one document per finished long-running
peer call and never delete it. Beyond unbounded storage growth, those documents
hold the *typed* federated result of the call, so an unbounded store is a
data-retention exposure for a clean-room or privacy-sensitive federation — and
it compounds [Phase 308](308-peer-job-poll-caller-ownership-scoping.md): without
a bound, a stale result stays readable by its owner forever.

Retention is now a value, `PeerJobRetentionPolicy`:

```fsharp
type PeerJobRetentionPolicy = {
    Ttl: TimeSpan option      // expiry from the terminal write; None = keep forever
    DeleteOnRead: bool        // reclaim once read, rather than waiting out Ttl
    GraceWindow: TimeSpan     // how long a read record stays readable (delete-on-read only)
}
```

`IPeerJobResultStore` gains an `abstract Retention: PeerJobRetentionPolicy`, so
an alternative (distributed) store declares the same contract rather than
re-deriving its own, and `TryGetResult` documents that a retired record reads as
`None` — indistinguishable from *never existed*, deliberately, matching Phase
308's non-disclosure posture.

Enforcement is **lazy, on read**: an expired document is reported absent and its
blob deleted in the same call. No background sweeper, no hosted service, no
scheduler dependency (GP 13). A deployment wanting eager reclamation fuses its
own sweep onto `IJobScheduler` over the `peers/jobs/` prefix — the stamps it
needs are in the stored document.

**The one behavioural change:** the composed default is now
`PeerJobRetentionPolicy.default'` — a 30-day TTL, no delete-on-read. A poll cycle
is a minutes-scale conversation between two peers, so 30 days is several orders
of magnitude of headroom; a deployment that never thinks about retention keeps
every result far longer than any caller could still be polling for it (GP 11),
and stops accumulating them forever. Documents written **before** this phase
carry no expiry stamp and are kept indefinitely, so the upgrade retires nothing
that already exists.

## Diff to apply

**Nothing**, for a deployment that composes the peer substrate via
`PeerServerApp.run`. `BlobPeerJobResultStore(blobs)` is unchanged — the policy
and the clock arrive as constructor **overloads**, not as a widened record or an
optional-argument rewrite of the existing signature, so the surface diff is
purely additive at both the source and the binary level (GP 11):

```fsharp
// Unchanged — picks up PeerJobRetentionPolicy.default'
BlobPeerJobResultStore(blobs)

// Opt out entirely (pre-316 behaviour, byte for byte)
BlobPeerJobResultStore(blobs, PeerJobRetentionPolicy.keepForever)

// A tighter clean-room posture: 24h ceiling, reclaimed 5 minutes after the
// owner's first successful poll
BlobPeerJobResultStore(
    blobs,
    PeerJobRetentionPolicy.keepForever
    |> PeerJobRetentionPolicy.withTtl (TimeSpan.FromHours 24.0)
    |> PeerJobRetentionPolicy.withDeleteOnRead (TimeSpan.FromMinutes 5.0)
)
```

The only break is code that **implements `IPeerJobResultStore`** (typically a
test double). Add one data-only member:

```fsharp
type MyResultStore() =
    interface IPeerJobResultStore with
+       member _.Retention = PeerJobRetentionPolicy.keepForever
        member _.SaveResult(scopeId, jobId, ownerPeerId, status) = async { … }
        member _.TryGetResult(scopeId, jobId) = async { … }
```

A store that genuinely applies retention reports the policy it honours instead
of `keepForever`.

## Consumer impact (SDK-adoption)

No consumer in the matrix implements `IPeerJobResultStore` — the interface is a
fusion seam registered by `PeerCompose`, and every in-tree implementer
(`BlobPeerJobResultStore`, the `PeerSurface` probe stub, the test double in
`IPlatformPeerContract`) moves in this commit. A consumer that composes the peer
substrate needs no source change; it inherits the 30-day default. The
`SDK-ADOPTION.md` matrix is generated from each consumer's own
`sdk-adoption.json` — it is not hand-edited, and this phase adds no row until a
consumer declares one.

## Deferred

Compose-level configuration (`PeerServerApp.withJobRetention …`) is **not** in
this phase: it lands in `PeerCompose.fs`, outside this phase's declared key-file
footprint and inside the serialised `PeerCompose` track. Until it ships, a
deployment overriding the policy registers its own `IPeerJobResultStore`
constructed with the desired `PeerJobRetentionPolicy`.

## Verification

- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 316"`
  — TTL expiry + blob reclamation, delete-on-read grace (in-window retry
  resolves; the window does not slide), the TTL backstop for a never-polled
  delete-on-read record, `keepForever` parity with pre-316, a pre-316 document
  surviving the upgrade, and expired-vs-never-existed indistinguishability.
- `dotnet build ToolUp.Forge.sln` — the `Retention` member is required of every
  implementer, so a missed test double fails here, not at runtime.

## Rollback

Revert the Phase 316 commit. Documents written under it carry two extra fields
(`ExpiresAt` / `ReadExpiresAt`); the pre-316 reader deserialises a
`PeerJobRecord` from the same JSON and ignores them, so parked results stay
readable and no result is lost. Retention simply stops being enforced.
