# Phase 5h — `IPendingInviteStore` interface seam (consumer adoption)

**What changes.** SDK promotes the email-keyed pending-invitation substrate (shipped under Phase 3d as the `PendingInviteStore` module-functions impl over `_platform/pending-invites.json`) behind an `IPendingInviteStore` interface seam. The default-and-only impl remains the single-instance blob+lock+cache — now exposed as `InMemoryPendingInviteStore` and adopting the interface explicitly. The single-instance correctness story is unchanged.

The seam exists so the future `BlobPendingInviteStore` (deferred until Phase 9c half-2's `IBlobStorage.UploadWithETag` substrate ships) can drop in via `ServerApp.withPendingInviteStore` without touching call sites, and so an optional `RedisPendingInviteCache` decorator can bind to the same interface for cross-process cache invalidation under high multi-instance read load.

**Default unchanged (GP 13).** A consumer using `ServerConfig.defaults` and the SDK's automatic registration carries on with the single-instance default — zero migration work. The single-instance correctness window is unchanged: `PendingInviteStoreInstanceValidator` (Phase 3d / Cluster A4) still emits `Warning` when `ReplicaCount > 1` and the InMemory store is in use, and `AcceptPendingInviteStoreInMultiInstance = true` remains the explicit-opt-in escape hatch.

**Scope of THIS phase shipment.** Substrate seam only. The four `[ ]` items in the phase body's *ETag-based blob default* sub-section + the optional Redis cache companion + the `PendingInviteStoreInstanceValidator` gate update stay deferred until forge Phase 9c half-2 lands `IBlobStorage.UploadWithETag`; they drop in atomically against this interface seam without further consumer-side migration work.

## Consumer-side changes (zero migration for default consumers)

A consumer that does NOTHING continues to receive the single-instance default. The new `ServerApp.PendingInviteStore` field defaults to `None`; `compose` constructs `InMemoryPendingInviteStore(resolvedBlobStorage)` and registers it as the `IPendingInviteStore` DI singleton.

## In-tree changes the SDK applied (informational — for callers that previously used the module-function surface)

- `ToolUp.Platform.Teams.PendingInviteStore.upsert` / `.remove` / `.listAll` / `.tryConsumeForEmail` / `.sweepExpired` module functions are preserved as a **backward-compat shim** in `InMemoryPendingInviteStore.fs`. Call sites compile unchanged.
- `TeamInvitationHandler`'s `IssuePendingInviteByEmail` / `ListPendingInvitesByEmail` / `RevokePendingInviteByEmail` paths now resolve `IPendingInviteStore` from `HttpContext.RequestServices` and call its members directly. The pre-Phase-5h pattern (resolve `IBlobStorage` from `RequestServices`, call `PendingInviteStore.upsert storage email pending`) was removed.
- `TeamInvitationHandler.tryConsumePendingForUser` signature changed: first parameter is now `IPendingInviteStore` instead of `IBlobStorage`. Callers (today: `ScopeResolutionMiddleware`'s pending-invite probe) resolve `IPendingInviteStore` from `RequestServices` and pass it.

## Opt-in adoption (consumers wanting to swap the impl)

1. **Custom implementation.** Register via `ServerApp.withPendingInviteStore`:

   ```fsharp
   ServerApp.create ()
   |> ServerApp.withConfig cfg
   |> ServerApp.withStorage blobStorage
   |> ServerApp.withPendingInviteStore (MyCustomPendingInviteStore.create blobStorage)
   |> ServerApp.addModules myModules
   |> ServerApp.run
   ```

   The supplied store replaces the SDK default for `IPendingInviteStore` DI resolution.

2. **Future `BlobPendingInviteStore`.** Once Phase 9c half-2 ships `IBlobStorage.UploadWithETag`, the SDK ships a `BlobPendingInviteStore` that uses ETag-based optimistic concurrency for multi-instance correctness. Adoption then becomes:

   ```fsharp
   |> ServerApp.withPendingInviteStore (BlobPendingInviteStore.create blobStorage)
   ```

   The single-instance-only warning from `PendingInviteStoreInstanceValidator` clears when the resolved store is `BlobPendingInviteStore` (gate update lands with that follow-up phase).

3. **Optional `RedisPendingInviteCache` decorator.** Decorates a `BlobPendingInviteStore` with a Redis-backed pub/sub-invalidated cache for cross-process read amplification. Useful only when multi-instance Team-mode read load against the pending-invite blob becomes load-bearing.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — `IPendingInviteStoreContract` passes for `InMemoryPendingInviteStore` (10 tests; substrate Upsert / Remove / ListAll / TryConsumeForEmail / SweepExpired).
- Existing `TeamInvitationTests` pass — pending-invite-by-email flows continue to work end-to-end through the API + middleware paths under the new DI-resolved interface.
- `PendingInviteStoreInstanceValidator` continues to surface `Warning` when `ReplicaCount > 1` AND `AcceptPendingInviteStoreInMultiInstance = false` AND the resolved store is the InMemory default. Single-instance deployments and explicit-opt-in multi-instance deployments are unaffected.

## Rollback

Revert the SDK phase commit. The interface, the rename, the DI registration, and the migration to `IPendingInviteStore` in `TeamInvitationHandler` are atomic in one commit; the backward-compat shim means any external call site against `ToolUp.Platform.Teams.PendingInviteStore.upsert` etc. is unaffected by the rollback either way.

## What's deferred to a follow-up

Tracked in the phase body — all four ETag-based blob-default sub-tasks + the optional Redis cache companion + the validator gate update wait for Phase 9c half-2's `IBlobStorage.UploadWithETag` substrate. The interface seam shipped here is the load-bearing piece for that drop-in; the rest is mechanical once the ETag substrate exists.

## Blob default is now real (2026-09-18 follow-up)

**What changed.** `BlobPendingInviteStore` shipped — the ETag-based multi-instance implementation over the Phase 600 `IConditionalBlobStorage` seam (the surface lives there, not on `IBlobStorage` as the seam half assumed). Same blob (`_platform/pending-invites.json`) and codec as the in-memory store, so a deployment that grows from one replica to several keeps its pending entries; no process-local cache; every mutation is read-with-ETag → transform → `UploadWithETag IfMatch` with a bounded retry (`BlobPendingInviteStoreOptions`: 3 retries at 100 / 200 / 400 ms by default) that surfaces `PendingInviteStoreError.Conflict` when exhausted. The store is silent on the audit log except for the `TeamInviteExpired` rows a sweep owns, exactly like the in-memory store.

**When it is auto-selected.** `compose` picks it when **both** hold: `ServerConfig.ReplicaCount > 1`, and the resolved `IBlobStorage` implements `IConditionalBlobStorage` (the local, Azure, S3 and GCS backends all do, as do the encrypting / resilient decorators). One replica keeps `InMemoryPendingInviteStore` (its read cache is a real throughput win there). A `ServerApp.withPendingInviteStore` override still wins over the auto-selection in every case.

**What it requires.** A conditional-write-capable blob backend. A consumer-supplied `IBlobStorage` that does not implement `IConditionalBlobStorage` cannot host it, and the SDK does **not** degrade to a racy download-modify-upload: it falls back to the in-memory store and `PendingInviteStoreInstanceValidator` says so at preflight. That validator now fires only when the RESOLVED store is `InMemoryPendingInviteStore` under `ReplicaCount > 1` — a deployment on the blob store (auto-selected or supplied) is clean regardless of replica count.

**How to opt out.** Either supply the in-memory store explicitly and set `AcceptPendingInviteStoreInMultiInstance = true` (`TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE=1`) — the escape hatch is unchanged — or supply any custom `IPendingInviteStore`:

```fsharp skip=fragment
|> ServerApp.withPendingInviteStore (InMemoryPendingInviteStore(blobStorage, logger) :> IPendingInviteStore)
```

To tune the retry budget rather than opt out, construct the blob store yourself over a conditional backend:

```fsharp skip=fragment
|> ServerApp.withPendingInviteStore (
    BlobPendingInviteStore(conditionalStorage, logger, Some auditLog, None, { MaxRetries = 5; InitialBackoff = TimeSpan.FromMilliseconds 50.0 })
    :> IPendingInviteStore)
```

**Verification.** `IPendingInviteStoreContract` now runs against both stores (`InProcess/BlobPendingInviteStoreTests.fs`), plus concurrency cases — two replicas upserting both land, two replicas consuming one entry admit exactly one, N forced conflicts then success, N+1 forced conflicts surface `Conflict` — and the validator gate cases. The Phase 259 conformance ratchet no longer lists `IPendingInviteStoreContract` as single-bound.

**Still deferred.** The optional `RedisPendingInviteCache` decorator (separate sub-phase, only if multi-instance read load becomes load-bearing).
