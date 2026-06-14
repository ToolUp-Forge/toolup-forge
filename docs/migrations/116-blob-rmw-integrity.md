# Phase 116 — Blob read-modify-write integrity (consumer migration)

**What changes.** Three blob-backed read-modify-write paths that could silently destroy data are now fail-closed and (within one process) serialised:

1. **Pending invites** (`InMemoryPendingInviteStore`). A present-but-unparseable `_platform/pending-invites.json` blob previously decoded to `Map.empty` with no log; combined with the full-blob-overwrite write path, the next `IssuePendingInviteByEmail` persisted empty-plus-one and **irreversibly erased every other pending invite**. The blob now decodes fail-closed: corruption quarantines the blob aside (`pending-invites.json.corrupt-<utc-timestamp>`), emits `logger.Error`, and fails the triggering operation with `StorageFailed` — no map derived from a failed decode is ever written back. The store self-heals to empty on the next read (the corrupt bytes survive in the quarantine blob for recovery).
2. **Share-token use count** (`BlobShareTokenStore.MarkUsed` / `Revoke`). The read → increment → write was non-atomic, so N concurrent public-form submits all read `UsedCount = k` and wrote `k+1`, bypassing `UseLimit = 1`. Both RMW paths now run under an in-process `SemaphoreSlim`. The public-form handler previously discarded the `MarkUsed` result (`let! _ = …`), so a storage error, a token revoked mid-flight, or a use-limit lost to a concurrent submitter all returned success silently; the result is now logged and surfaced as a submission error.
3. **KB `index.json` additive writers** (`uploadDocument`, `addNote`, `updateNote`, `ingestNarrative`). Each did a bare `loadIndex → append → saveIndex`; two concurrent uploads to the same container lost one document from the index while its blob + chunks persisted orphaned. The index RMW now routes through `IndexStorage.upsertIndexEntry`, which holds the existing per-container lock across load + save.

All three are the **interim single-instance** guards (process-local lock). The cross-replica fix is ETag-conditional-write CAS on `IBlobStorage.UploadWithETag` (Phase 9c half-2), deferred — the corresponding Phase 116 ETag-gated tasks stay open.

**Scope.** `ToolUp.Platform.Server` (`InMemoryPendingInviteStore`, `ShareTokenStore`), `ToolUp.Forms.Server` (`PublicFormApiHandler`), `ToolUp.KnowledgeBase.Server` (`IndexStorage` + three API writers). No client change. No wire change.

**Backward compatibility.**

- **Stock consumers:** nothing to do. The defaults are wired in `compose`; behaviour is identical except a corrupt pending-invites blob now fails loudly instead of erasing invites, and a doomed public-form submit now returns an error instead of a false `Ok`.
- **Behavioural — corrupt pending-invites blob:** `IPendingInviteStore` operations return `Error (StorageFailed "pending-invites blob was corrupt (quarantined to …)")` instead of behaving as if there were zero invites. Consumers with custom recovery flows around `_platform/pending-invites.json` should expect the quarantine blob and the typed error.
- **Behavioural — public-form submit:** `IPublicFormApi.SubmitWithToken` now returns `Error` when the post-persist `MarkUsed` fails (e.g. use-limit lost to a concurrent submitter). The submission is still durable; the response reflects that the use slot was not granted.
- **Source-breaking — direct `InMemoryPendingInviteStore` construction:** the constructor gains a second parameter, `logger: ILogger`. Stock consumers never construct it directly (they use `ServerApp.withPendingInviteStore` or the default). Test fixtures / custom wiring that call `InMemoryPendingInviteStore(storage)` must pass a logger.

## Diff to apply

Stock consumers: none. Direct-construction sites only:

```fsharp
// Before:
InMemoryPendingInviteStore(storage) :> IPendingInviteStore

// After (pass any ILogger — ConsoleLogger, a silent object expression in tests, etc.):
InMemoryPendingInviteStore(storage, logger) :> IPendingInviteStore
```

## Verification

1. `dotnet build ToolUp.Forge.sln` (clean tree) — green.
2. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — `IPendingInviteStoreContract` + share-token packs green.
3. `dotnet run --project src/ToolUp.Forms.Tests/ToolUp.Forms.Tests.fsproj` — green.
4. Manual: write a malformed `_platform/pending-invites.json`, call any pending-invite operation → typed `StorageFailed`, a `.corrupt-<timestamp>` sibling blob exists, the original name is freed, and a follow-up operation starts from empty without erasing a freshly-written invite.
5. Manual: fire 10 concurrent submits at a `UseLimit = 1` share token → exactly one `MarkUsed` succeeds (single instance); the others surface an error and are logged.
6. Manual: two concurrent KB uploads to the same container → both documents appear in `index.json`; no orphaned blob/chunks.

## Rollback

Revert the Phase 116 forge commit(s). No data migration: quarantine blobs (`*.corrupt-*`) are inert and can be deleted or hand-recovered; the share-token / KB-index changes are behavioural only (no on-disk format change).
