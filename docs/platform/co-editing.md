# Co-editing — soft locks vs CRDT documents

Two people open the same record. What happens?

The SDK ships **two answers**, and picking the wrong one is the mistake this page exists to prevent. They are not competing implementations of one feature; they are the right answers to two different questions about the *content*.

| | **Soft-lock mode** (Phase 442) | **Co-edit mode** (Phase 535) |
|---|---|---|
| Substrate | `IEntityLockStore` (+ `IPresenceTracker`) | `ICrdtDocumentStore` (+ `IPresenceTracker`) |
| Opt-in | `ServerConfig.Presence = EnabledPresence` | `ServerConfig.CrdtDocuments = EnabledCrdtDocuments` |
| Concurrency | One editor at a time; others read-only | Everyone edits at once |
| Conflict | Prevented — the second editor is told who holds it | Merged — the CRDT resolves it, always |
| Needs | Nothing beyond the SDK | A CRDT library in the client bundle |
| Right for | Content whose format cannot merge | Text and structured content whose encoding is commutative |
| Wrong for | A shared document people want to type into together | A binary artefact, or a record with cross-field invariants |

**The discriminator is mergeability, not importance.** A CRDT will merge two concurrent edits to an invoice's line items into a document that satisfies neither party's intent about the total — it merges *representations*, and it has no idea your fields constrain each other. Where a rule spans fields, take a soft lock. Where the content is prose, notes, a rich-text body, a whiteboard, a list people reorder — co-edit, and never make one person wait.

The two **compose**. A co-edited document may sit beside a non-mergeable attachment guarded by a soft lock, in the same view, over the same scope, on the same notification channel.

## What the SDK does and does not do

The store is a **relay with a memory**. Every payload crossing `ICrdtDocumentStore` is opaque bytes: the SDK cannot merge two updates, cannot tell you what a document says, and does not validate a payload beyond its length. The merge maths lives entirely in the client's CRDT library.

That is the design, not a limitation:

- **The server carries no CRDT library and no npm dependency.** It builds, tests and deploys with nothing added to its graph (GP 1).
- **The client library is the consuming app's dependency.** [Yjs](https://github.com/yjs/yjs) (MIT, free — GP 2) is the reference choice, declared in the app's own `package.json`. `ToolUp.Platform.Client` imports nothing from it; the library arrives as a parameter (`CrdtSyncClient.IYjs`), so a deployment that never co-edits ships no vendor bundle weight (GP 13), and one that prefers a different update-encoding CRDT implements four functions over its own library and changes nothing else.
- **Nothing to re-negotiate later.** Swapping the CRDT is a client-side change. The log, the cursor, the scope isolation and the fan-out are unaffected.

What the SDK owns instead is everything a CRDT library does *not* give you, and that every consumer would otherwise improvise incompatibly: durability, catch-up, scope isolation, fan-out, and compaction.

## The seam

```fsharp skip=fragment
type ICrdtDocumentStore =
    abstract Append: ref: CrdtDocRef * payload: byte[] * originSession: string -> Async<CrdtUpdate>
    abstract GetStateVector: ref: CrdtDocRef -> Async<StateVector>
    abstract GetDiff: ref: CrdtDocRef * since: StateVector -> Async<CrdtUpdate list>
    abstract Snapshot: ref: CrdtDocRef -> Async<CrdtSnapshot>
    abstract Compact: ref: CrdtDocRef * merged: byte[] * covers: StateVector -> Async<CrdtSnapshot>
```

`CrdtDocRef` is `{ Scope; DocId }` — **the scope is part of the key, not a filter**. Two scopes naming the same `DocId` address two structurally distinct documents, so a cross-tenant read is an impossible lookup rather than a forgotten `WHERE` clause (GP 4). As everywhere else in the SDK, the scope is resolved from the caller's authenticated request and never accepted from the wire.

It is also the **shard key**: ordering is promised within one document and nowhere else. Two documents' `Sequence` values are incomparable, by design.

### `StateVector` is an opaque cursor

`StateVector` carries `byte[]` and no caller may parse, construct, order or reinterpret it. The bytes are the issuing store's own encoding — a log watermark in the in-memory default, a database LSN in some other implementation — and a client simply hands back what it last received. Three laws every implementation honours:

1. `StateVector.empty` means "I have nothing" for every implementation, and is the one vector a caller may construct.
2. A diff against the vector a store just returned is empty.
3. Appending the same set of updates in any order yields the same vector.

An **unrecognised** vector — one issued by a different store, or by the same store before a restart — is never an error. The store returns the whole document instead. Re-applying an update a client already holds is free (CRDT updates are idempotent), and losing one is not, so the safe direction is always "send more".

## The join / catch-up / live protocol

A client runs the same three steps whether it is joining for the first time, reconnecting after a blip, or resuming after a day offline. There is deliberately **no separate resume path** to get wrong:

1. `GetDiff(ref, cursor)` — with the retained cursor, or `StateVector.empty` on a cold start. Apply every returned payload, in any order. Retain the new cursor.
2. Subscribe to `_platform.crdt` on the scope, and apply each `CrdtUpdateEvent` whose `OriginSession` is not this session's own.
3. `Append` each local update as the CRDT library emits it.

A missed live event is a latency problem, not a correctness one — the next `GetDiff` recovers it. That is precisely why the cursor exists rather than the fan-out being made reliable.

Fan-out rides the shipped `INotificationChannel` (Phase 6a) — **no new transport**, the same per-scope SSE pipeline presence, locks and every other server-driven event already use. A publish failure is swallowed rather than retried or surfaced, because the update is already durable in the store by then and every co-editor recovers it on its next catch-up.

### Client wiring

```fsharp skip=fragment
// In the consuming app — the one line that names the vendor.
let yjs: CrdtSyncClient.IYjs = importAll "yjs"
let ydoc: obj = createNew (import "Doc" "yjs") ()

let session =
    CrdtSyncClient.start yjs (unbox<CrdtSyncClient.IYDoc> ydoc) transport sessionId

// Relayed updates from the SSE subscription; the session drops its own echo.
session.ApplyRemote update

// Reconnect, visibility change, or a periodic re-anchor.
session.Resync() |> Async.StartImmediate
```

`sessionId` is the echo-suppression key, so it identifies a **tab**, not a user — two tabs open by one person are two co-editors.

The runnable reference is [`samples/MinimalClient/CrdtCoEditSample.fs`](../../samples/MinimalClient/CrdtCoEditSample.fs): a shared text area, including the prefix/suffix delta that keeps a whole-value `onChange` from clobbering a co-editor's concurrent edit.

**The cursor advances on `Resync`, not on a live update.** A relayed update carries no cursor, and an opaque value cannot be advanced by inference. The consequence is benign, and is documented here so nobody "fixes" it: an update applied live may be delivered again by the next `Resync`, which is a no-op in the CRDT. Inferring cursor positions client-side would couple the client to one implementation's encoding to save a few duplicated bytes.

## Awareness rides presence, not the log

Cursors and selections do **not** go in the update log. They are ephemeral, per-participant, and worthless a second after they move — putting them in a durable log grows it without bound for data nobody will ever read again.

They ride the Phase 442 presence location descriptor instead, which already fans out on `_platform.presence` and expires a peer that stops beating:

```fsharp skip=fragment
CrdtAwareness.location "notes" docRef (Some "para-14")
|> presenceApi.Heartbeat
```

## Compaction

An append-only log grows forever. `Compact` replaces the prefix a cursor covers with a single merged payload:

```fsharp skip=fragment
// From a participant that holds the document:
let merged = CrdtSyncClient.mergedState yjs ydoc
let covers = session.Cursor()          // read immediately after a Resync
do! store.Compact(ref, merged, covers)
```

**Compaction is client-attested, and that is structural rather than a shortcut.** The store cannot merge opaque payloads, so the merged base must come from something that holds the document. The base is stored under the reserved `CrdtDocument.CompactionOrigin`, sorts where the covered prefix sat, and reaches a joiner as an ordinary update — so a client already past the compaction point is not re-sent a base it does not need.

Two consequences to weigh **before exposing this on a wire**: a caller supplying a base that is not a faithful merge silently rewrites history for later joiners (existing editors are unaffected — their local documents already hold the prefix), and a caller supplying a stale `covers` simply compacts less than it could. Neither can cross a scope boundary. The recommended posture is to compact from a trusted headless participant on a schedule, or to restrict the operation to an authenticated editor of that document — never to expose it unauthenticated. `Compact` against `StateVector.empty` is refused outright, because it would append a second copy of the document rather than compact it.

## Offline interplay

CRDT updates are the one payload class that merges cleanly on reconnect, which is what makes an offline queue over this seam tractable where a general write queue needs a conflict policy:

- `Append` is order-insensitive — a backlog replayed in any order converges, so the queue needs no ordering guarantee beyond its own.
- An update is idempotent — a replay that duplicates a send costs a byte, not a corruption.
- `GetDiff` from the retained cursor is the catch-up half, which the queue does not have to implement.

What a queue must still own is **retention**: an update a client never managed to send exists only in that client's local document until it does.

## Composition

```fsharp skip=fragment
{ ServerConfig.defaults with
    CrdtDocuments = EnabledCrdtDocuments
    Presence = EnabledPresence }          // awareness, optional but wanted
```

`EnabledCrdtDocuments` registers the single-instance in-memory log wrapped in the notification relay. The default is `NoCrdtDocuments`: no store in DI, no allocation, no `_platform.crdt` fan-out — an existing deployment that upgrades is byte-for-byte unchanged until it opts in (GP 11 + GP 13).

**The in-memory default is dev / single-instance and not durable across a restart.** A production or multi-instance deployment supplies an implementation over a real log (append-only blob, database table, event store) with no change to consuming code, and inherits fan-out unchanged because the relay is a decorator over the seam rather than folded into the log.

The substrate is **seam-first**: the SDK registers the store and mounts no route for it. A deployment exposes its own module-owned API over the resolved service, exactly as Phase 442's presence substrate was consumed before a platform API was mounted over it.

## Six-rule portability audit (GP 12)

`ICrdtDocumentStore` is held to the [six portability rules](portability-rules.md); the audit also lives in the seam's own file header, so a reader of either finds it.

| Rule | How the seam satisfies it |
|---|---|
| **1 — Identity by value** | `CrdtDocRef` / `StateVector` / `CrdtUpdate` are records of strings, `byte[]`, `int64` and `DateTime`. No live handle, stream or session object crosses the seam, so a client resuming on a different node hands back the same cursor value and gets the same answer. |
| **2 — Async at every boundary** | All five methods return `Async<_>`. No compose-time-only carve-out is claimed. |
| **3 — Retry / supervision as data** | The seam declares no retry policy and takes no failure callback, because it supervises no background work — every operation is caller-driven. Fan-out failure is deliberately not a retry concern: `GetDiff` is the durable recovery path. |
| **4 — Stateless between invocations** | Nothing is remembered about a caller between calls. The resumable cursor is the *caller's* state, handed in on every `GetDiff` — which is why it is a value rather than a server-held session, and what lets successive calls land on different nodes. |
| **5 — No cross-shard ordering** | `CrdtDocRef` is the shard key. `Sequence` is monotonic within one ref and meaningless across refs. Within a ref the log is ordered — which the CRDT does not need, but catch-up does. |
| **6 — Precision at the lower bound** | No timing promise is made. `AppendedAt` is for retention and diagnostics, **never a merge input** — convergence must not depend on clocks, because a distributed implementation's nodes disagree about the time and the CRDT is what resolves concurrency. Fan-out inherits `INotificationChannel`'s stated bound (near-real-time, not sub-second guaranteed). |

The executable bar is `ICrdtDocumentStoreContract` in `ToolUp.Platform.Tests` — the cursor laws, compaction, scope isolation, and the convergence property (any permutation of the same update set yields the same state vector and delivers the same payload set). Any external implementation runs the same tests.

## See also

- [`portability-rules.md`](portability-rules.md) — the six rules in full.
- [`sse-deployment.md`](sse-deployment.md) — the fan-out transport under load.
- [`live-sessions.md`](live-sessions.md) — the server-authoritative sibling: a server-held tree pushed as patches, for surfaces where the server owns the state rather than merging clients' views of it.
