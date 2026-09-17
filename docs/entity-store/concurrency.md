# Concurrency in the Entity Store — `Save` vs `SaveIfVersion`

`IEntityStore` offers two ways to write a record, and they answer different questions.

| | `Save` | `SaveIfVersion` / `DeleteIfVersion` |
|---|---|---|
| The question it answers | "Store this." | "Store this **if nobody else has since I read it**." |
| Version assigned | `head + 1`, whatever the head is now | `expectedVersion + 1`, or nothing |
| When the head moved under you | your write wins; the other edit is silently superseded | `Error (VersionConflict (type, id, expected, actual))`; nothing written, no index touched |
| Two racing writers | both succeed; the later one's metadata overwrites the earlier one's | exactly one succeeds; the other is told, with both versions named |
| Cost | one head read + one write | the same, plus the atomic claim (see below) |

`Save` is **last-writer-wins by design** and stays that way (GP 11): every existing call site keeps
its behaviour byte for byte. `SaveIfVersion` is the additive surface (Phase 753) for the cases
where a version was carried through a round trip and the write is only correct if that version is
still the head.

## When to state a version

State one whenever the write is an **edit of something the user read earlier** — the record went to
a client, sat there, and came back changed. That is the entire class where last-writer-wins loses
work silently:

- a form the user opened, edited for a while, and submitted;
- an offline queue replaying edits made against a version the server may have moved past
  (`ToolUp.Offline`'s replay handler is the reference consumer — see below);
- a background job that read a record, computed something from it, and writes the result back;
- "create only if it does not exist yet" — `expectedVersion = 0` means exactly that, so two clients
  racing to create the same id see one `Ok` and one `VersionConflict (0, 1)`.

Do **not** state one when the write is not an edit of a read: a log-shaped append where each save is
self-contained, an idempotent upsert of an externally-owned fact (a sync from a system of record,
where the latest import *should* win), or a write from a process that is the sole writer of that
record by construction. There, `Save` is correct and the conflict path would only be a retry loop
that always resolves the same way.

The pattern, in a handler:

```fsharp
let saveEdit () = async {
    match! store.Get<Note>(scopeId, "Note", noteId) with
    | Error err -> return Error err
    | Ok current ->
        let edited = { current with Body = editedBody }

        match! store.SaveIfVersion<Note>(scopeId, EntityPrincipal.ofPrincipal caller.UserId, edited, current.Version) with
        | Ok saved -> return Ok saved
        | Error(EntityError.VersionConflict(_, _, expected, actual)) ->
            // Someone wrote version `actual` after we read `expected`.
            // Re-read, merge (or ask the user), and state `actual`.
            return Error(EntityError.VersionConflict("Note", noteId, expected, actual))
        | Error err -> return Error err
}
```

The record's own `Version` field is what you state — the store rewrote it on the way out of `Get`,
so `current.Version` is the head you read. The `EntityPrincipal` beside it is the caller the handler
resolved (`caller` is its `AccessContext`): since Phase 806 every mutating member takes one, and it is
what the lifecycle audit row records. On success the returned `EntityRef.Version` is the new
head, which is what the next edit states.

## What the store guarantees, and where

Two guarantees, at two layers.

**The compare.** Every implementation compares the head against `expectedVersion` and refuses a
mismatch before writing anything. A refused save writes nothing and touches no index — the
contract pack (`IEntityStoreContract`) pins that with a stale write against an indexed field and
asserts the index still points at the head's value.

**The claim.** Where the wrapped storage can express an atomic "create only if absent", the version
slot itself is claimed atomically, so the compare-then-write has no window:

- `BlobEntityStore` over the default `DataObjectStore` claims the version's metadata blob
  (`objects/{objectId}/v{N+1}.json`) with the Phase 600 `IConditionalBlobStorage.UploadWithETag`
  `IfAbsent` condition. The metadata blob is a save's commit point, so two racers that both read
  head = N and both try to create `v{N+1}.json` see exactly one success by the blob store's own
  atomicity — no lock, no lease, no sweeper. Every shipped blob backend implements the seam
  (`LocalFileStorage`, the three cloud companions, the in-memory test double).
- `PostgresEntityStore` inserts row `(scope, type, id, expected + 1)`; the table's primary key
  includes the version, so the second racer's insert is a unique violation and comes back as
  `VersionConflict`.

The data-object layer exposes the claim as its own capability, `IConditionalDataObjectStore`,
probed through `ConditionalDataObjectStore.saveIfVersion` — the same non-widening shape Phase 600
chose for the blob seam, because six in-tree stores implement `IDataObjectStore` and a new abstract
member there would be a consumer-facing break.

### The residual window, and its size

Two compositions cannot make the claim, and in both the compare still runs but the write is
unconditional:

- an `IDataObjectStore` implementation that does not implement `IConditionalDataObjectStore`
  (a custom store that predates Phase 753) — `ConditionalDataObjectStore.saveIfVersion` falls back
  to `ListVersions` → compare → `Save`;
- the default `DataObjectStore` over an `IBlobStorage` that does not implement
  `IConditionalBlobStorage` (a custom backend that predates Phase 600).

In both, the window is **one round trip**: between the head read and the metadata upload. Two racers
that both read head = N inside it both pass the compare and both write `v{N+1}.json`, and the later
upload overwrites the earlier one's metadata — exactly what two unconditional `Save` calls do. A
racer that reads after the other's write lands is still refused. The window is documented on the
fallback rather than papered over with a lock, because a lock would have to be held across the
whole read-modify-write and released on every failure path, and the seam already has a primitive
that closes the window for every shipped backend.

`DeleteIfVersion` closes its window the same way since Phase 806. The data-object layer exposes a
conditional delete beside the conditional save (`IConditionalDataObjectStore.DeleteIfVersion`, probed
through `ConditionalDataObjectStore.deleteIfVersion`), and the default `DataObjectStore` decides it
with the same blob a save would: it claims the NEXT version slot (`v{N+1}.json`, `IfAbsent`) before
removing anything, so a save and a delete racing at the same expectation are refused by each other's
claim, and releases the slot once the versions are gone. The one race a released slot re-opens — a
saver that read the head before the delete began and writes after it finished — is closed from the
saver's side: holding its claim, `SaveIfVersion` checks the version it expected still exists, and
undoes the claim with `VersionConflict(expected, 0)` when it does not. `PostgresEntityStore` folds
the compare into the `DELETE` statement's predicate, which evaluates it against the statement's own
snapshot. The same two fallback compositions as above keep the one-round-trip window on the delete.

## Offline and co-editing — which mechanism, when

`SaveIfVersion` **detects** a concurrent edit; it does not merge one. What happens next depends on
the payload.

- **Offline queue (`ToolUp.Offline`).** The replay handler hands each queued mutation's
  `BaseVersion` to `SaveIfVersion` / `DeleteIfVersion` and turns a `VersionConflict` into a
  `Conflict` outcome carrying both documents, which `OfflineConflictResolver` shows side by side —
  keep mine (rebase and replay), keep theirs, or merge by hand. The replay adapter
  (`OfflineEntityReplay.ofJson`) is where the expectation is stated; an adapter that saves through
  the unconditional `Save` has opted its entity type out of conflict detection. Details in the
  [Offline technical guide](../../src/ToolUp.Offline/TECHNICAL_GUIDE.md).
- **Merge-free payloads.** When the record is one people edit *together* — a shared text body, a
  collaborative list — a conflict prompt is the wrong outcome, because there is nothing for the
  user to choose between: both edits are wanted. That is Phase 535's CRDT co-editing substrate
  ([co-editing](../platform/co-editing.md)): the document is an operation log that converges on
  reconnect, and the entity store holds the compacted snapshot rather than the live document.
  Use `SaveIfVersion` for the record *around* the document (title, owner, status); use the CRDT
  document for the field people co-edit.
- **Soft locks** (Phase 442, `IEntityLockStore`) are the UI-level third option: tell the second
  editor up front that someone else is in the record. Advisory only — a lock is awareness, not a
  correctness barrier — so a locked record is still written with `SaveIfVersion`, and the lock
  reduces how often the conflict path is taken rather than replacing it.

## See also

- [`relationships.md`](relationships.md) — declarative edges and the implied index.
- [`../platform/storage.md`](../platform/storage.md#data-object-versioning) — the data-object layer
  the blob-backed store sits on.
- [`../platform/portability-rules.md`](../platform/portability-rules.md) — the six-rule audit the
  new members satisfy (identity by value, async at the boundary, failure as `EntityError` data,
  stateless between calls, single-shard ordering, no precision surface).
