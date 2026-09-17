# Migration — `EntityPrincipal` on `IEntityStore`, and a conditional delete on the data-object seam

> **Naming note.** The principal record is spelled `EntityPrincipal` throughout this document: Phase 814 renamed it (from the working name 806 landed it under) before the 0.23.0 draft was released, so no released consumer ever names the earlier spelling and this document reads under the released name. The rename and the four seam retypes that followed it are in [`814-seam-principals.md`](814-seam-principals.md).

Every mutating member of `IEntityStore` — `Save`, `SaveIfVersion`, `Delete`, `DeleteIfVersion` — now takes an `EntityPrincipal`: the principal performing the write, an optional on-behalf-of subject, and the optional offline-replay provenance. Both shipped stores stamp the lifecycle audit row (`EntityCreated` / `EntityUpdated` / `EntityDeleted`) and the stored version's `CreatedBy` from it, and neither writes a placeholder actor any more: before this phase every row said `UserId = "system"` because the seam carried no caller, and the offline replay path worked around that with an ambient `AsyncLocal` scope that a new implementation had to remember to read and that went silently missing across any async boundary that did not flow the execution context.

In the same seam revision the compare-then-delete window on `DeleteIfVersion` is closed: `IConditionalDataObjectStore` gains `DeleteIfVersion`, the twin of `SaveIfVersion`, and `BlobEntityStore` routes through it.

**This is BREAKING**, for two audiences, and deliberately so: the point of a parameter over an ambient scope is that an implementation which ignores it is visibly ignoring a parameter, and an out-of-tree implementation gets a compile error rather than a wrong audit row.

## What you have to change

### 1. Every `IEntityStore` call site — pass the actor

The actor is the second argument, between the scope and the payload. Build it from the caller you have:

```diff
- store.Save<Note>(scopeId, note)
+ store.Save<Note>(scopeId, EntityPrincipal.ofPrincipal accessContext.UserId, note)

- store.SaveIfVersion<Note>(scopeId, note, expected)
+ store.SaveIfVersion<Note>(scopeId, EntityPrincipal.ofPrincipal accessContext.UserId, note, expected)

- store.Delete(scopeId, "Note", noteId)
+ store.Delete(scopeId, EntityPrincipal.ofPrincipal accessContext.UserId, "Note", noteId)

- store.DeleteIfVersion(scopeId, "Note", noteId, expected)
+ store.DeleteIfVersion(scopeId, EntityPrincipal.ofPrincipal accessContext.UserId, "Note", noteId, expected)
```

Three constructors cover every case:

```fsharp skip=fragment
// A handler: the authenticated caller, acting for itself.
let actor = EntityPrincipal.ofPrincipal accessContext.UserId

// A delegated write: an admin editing a member's record.
let delegated = EntityPrincipal.ofPrincipal adminId |> EntityPrincipal.onBehalfOf memberId

// A write no principal made: a boot-time seed, a migration, a sweep no user
// triggered. NEVER a default — a handler has a caller, a job has the
// principal that scheduled it. The store never substitutes it; the row
// carries exactly the actor on the call.
let host = EntityPrincipal.system
```

`OnBehalfOf` lands on the row as `EntityLifecycleEventPayload.OnBehalfOf` (new, `None` on every pre-806 row — it deserialises by the same null-is-`None` mechanism as `Replay`, so no migration). `Replay` is for the offline sync handler; a live write leaves it `None`.

### 2. Every `IEntityStore` implementation — take the actor, stamp it

```diff
  interface IEntityStore with
-     member _.Save<'T>(scopeId, entity) = async {
+     member _.Save<'T>(scopeId, actor, entity) = async {
          ...
          do! auditLog.Record(scopeId, EntityCreated {
-             UserId = "system"
+             UserId = actor.Principal
+             OnBehalfOf = actor.OnBehalfOf
              EntityType = core.Type
              EntityId = core.Id
              Version = newVersion
-             Replay = None
+             Replay = actor.Replay
          })
```

The contract pack pins it: `IEntityStoreContract.auditTests` binds your store with a capturing `IAuditLog` and asserts that the actor on the call is the actor on the row, and that `"system"` reaches a row only when a caller passed `EntityPrincipal.system`. Bind it beside `IEntityStoreContract.tests`.

### 3. Every `IConditionalDataObjectStore` implementation — add `DeleteIfVersion`

```fsharp skip=fragment
abstract DeleteIfVersion:
    scopeId: string * objectId: string * expectedVersion: int -> Async<Result<unit, ConditionalDeleteError>>
```

Same bar as `SaveIfVersion`: of a save and a delete stating the same `expectedVersion`, exactly one succeeds. `expectedVersion = 0` on an absent object is an idempotent `Ok`; a `StrictlyVersioned` object is `DeleteFailed DeleteForbidden`. Over blob storage the deciding write is the NEXT version slot — the default `DataObjectStore` claims `v{expected+1}.json` `IfAbsent` before it removes anything, releases it last, and `SaveIfVersion` checks the version it expected still exists once it holds its own claim. Consumers reach it through `ConditionalDataObjectStore.deleteIfVersion`, which falls back to compare-then-`Delete` (one-round-trip window, documented) over a store without the capability. A decorator that forwards `SaveIfVersion` through the probe forwards this one the same way.

### 4. `OfflineEntityReplay.Apply` and `OfflineSyncOptions`

The handler builds one actor per mutation — the caller, replaying with the mutation's origination time, application time and queue mutation id — and hands it to the adapter, which passes it to the seam. `Apply` gains the parameter:

```diff
- Apply: OfflineReplayContext -> int -> byte[] -> Async<Result<byte[] * int, OfflineReplayError>>
+ Apply: OfflineReplayContext -> EntityPrincipal -> int -> byte[] -> Async<Result<byte[] * int, OfflineReplayError>>
```

An adapter built with `OfflineEntityReplay.ofJson` needs no change. A hand-written one passes the actor on its `SaveIfVersion` call and nothing else — substituting its own actor opts its entity type out of the provenance.

Because the store now records the provenance-carrying row itself, the handler no longer holds an audit log: `OfflineSyncOptions.AuditLog`, `OfflineSyncOptions.withAuditLog` and `OfflineSyncOptions.withAuditEventStore` are **removed**, and `EntityAuditReplayScope` (the ambient suppression scope) no longer exists. A deployment that composed the handler with an audit log composes the ENTITY STORE with it instead — `BlobEntityStore`'s `auditLog` argument, or the SQL companion's `createWithAudit` — and gets the same one row per replayed version, carrying the same `UserId` and `Replay`. A deployment whose store has no audit log now records nothing for a replay, exactly as it records nothing for a live write (GP 13).

```diff
- let options = OfflineSyncOptions.defaults |> OfflineSyncOptions.withReplays replays |> OfflineSyncOptions.withAuditLog auditLog
+ let options = OfflineSyncOptions.defaults |> OfflineSyncOptions.withReplays replays
- let store = BlobEntityStore(dos, blob, registry, None)
+ let store = BlobEntityStore(dos, blob, registry, Some auditLog)
```

### 5. `EntityReplayProvenance` moved

It now lives in the `EntityTypes` module beside the actor that carries it (`ToolUp.Platform.EntityTypes.EntityReplayProvenance`, was `ToolUp.Platform.EntityReplayProvenance`). Shape unchanged; a file that constructs one and does not already `open ToolUp.Platform.EntityTypes` adds the open. Unreleased before this phase (it landed in the 0.23.0 draft), so no tagged consumer names the old location.

### 6. Other retyped entry points

Each gained an `actor` parameter for the same reason, in the same position:

| Before | After |
|---|---|
| `OutboxEntityStore.SaveWithEvents(scopeId, entityType, entityId, entity, events)` | `SaveWithEvents(scopeId, actor, entityType, entityId, entity, events)` |
| `ContentAdminApiImpl.create store` | `ContentAdminApiImpl.create store actor` (the compose root resolves the admin from the request) |
| `ContentLifecycle.runScheduledPublishSweep store now` | `runScheduledPublishSweep store actor now` (the job's principal) |
| `PublicPageRevisions.restore store slug version` | `restore store actor slug version` |

## Verification

1. Build. Every call site and implementation the retype reaches fails to compile until it passes the actor — that is the migration surfacing itself; there is no runtime path that silently keeps the old row.
2. Bind `IEntityStoreContract.auditTests` to your store (if you implement one) and `IConditionalDataObjectStoreContract.tests` to your data-object store (if you implement one). The second includes the forced race — a save and a delete parked on the same head read — which is red without the slot claim.
3. Read one lifecycle row from your audit trail after a handler write: `UserId` is the caller, not `"system"`.

## Rollback

Revert the phase's commits. Rows written under it carry `OnBehalfOf` and a real `UserId`; a pre-806 reader ignores the extra field and reads the principal as before.

## Version notes

Breaking (SemVer-on-0.x: minor) — retyped abstract members on a shipped interface with two production implementations, a new abstract member on `IConditionalDataObjectStore`, removed `OfflineSyncOptions.AuditLog` / `withAuditLog` / `withAuditEventStore` / `EntityAuditReplayScope`, retyped `OutboxEntityStore.SaveWithEvents`, `ContentAdminApiImpl.create`, `runScheduledPublishSweep`, `PublicPageRevisions.restore`, `OfflineEntityReplay.Apply`; widened `EntityLifecycleEventPayload` (breaks full-record literals; wire-additive); relocated `EntityReplayProvenance`. Additive: `EntityPrincipal` + its module, `ConditionalDeleteError`, `ConditionalDataObjectStore.deleteIfVersion`, `IEntityStoreContract.auditTests`. Landed against the frozen 0.23.0 draft without moving `<Version>`; the operator classes the next cut.
