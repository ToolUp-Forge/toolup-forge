# KB original-document retention on `IDataObjectStore` (Phase 105)

> **Status: SHIPPED.** Opt-in — a deployment that does not call
> `withObjectStoreRetention` is byte-for-byte its pre-105 self, so no
> consumer action is required unless you want the new behaviour.

## What changes

KB originals can now be saved through `IDataObjectStore` instead of
being written as a raw `IBlobStorage` blob at the convention path
`knowledge/{docId}/{filename}`. The object store already provides
content-addressable dedup at rest, a metadata envelope, a version
chain, and the Phase 9h `Erase` surface.

**Opt in with one call:**

```fsharp
app
|> KnowledgeBase.Server.withObjectStoreRetention true
```

### It is opt-in, and the reason is not stylistic

The design note this page carried while the phase was PLANNED said
`None` (no `IDataObjectStore` registered) would leave every path at its
pre-105 behaviour. **That was wrong, and it is the one thing worth
reading twice.** `IDataObjectStore` is registered by
`ComposeRuntimeServices` for *every* composed deployment — it is core
substrate, not an optional companion — so a DI probe would have been
true everywhere. Shipping that would have moved every existing
deployment's originals to a new location on upgrade, leaving all
previously-uploaded documents served only by the compatibility
fallback, with nothing in the release notes to say so.

So the gate is an explicit `KnowledgeObjectRetentionPolicy`, resolved
per request, and the store singleton is resolved **behind** it. An
un-opted-in deployment makes no store call on any path (GP 11 / GP 13).

### The objectId is the docId — `KnowledgeDocument` did not change

`(scopeId, objectId)` is the store's identity, and `docId` is already
unique within the scope, already stamped into chunk metadata (Phase 103
`stampOriginalRefs`), and already the key `GetOriginalDocument`
receives. Deriving `objectId = docId` makes retrieval a direct
`Get(scopeId, docId)` with **no wire change and no persisted-record
widening**.

The operator ruled on 2026-08-04 that widening a record is permitted
where it is the correct design, so this was re-decided on the merits
rather than inherited from the earlier ban — and deriving is still
right, for a reason that has nothing to do with migration cost:

> Phase 510 supersedes a document lineage **in place**, under the same
> `Id`. The object store's own version chain is keyed by `objectId`. So
> `objectId = docId` makes version N of the lineage *be* version N of
> the object — the two version axes agree by construction. A separate
> objectId would force a choice between breaking the lineage (a new
> objectId per version) and reproducing the docId with extra
> indirection.

An explicit `ObjectId: string option` remains possible later if a
backend ever needs an objectId that is not the docId. Nothing today
asks for one.

### Effective files

Note the path: the handlers live under `Server/Api/`, not
`Server/Documents.fs` — the phase's original `key_files` predated that
split.

| File | Change |
|---|---|
| `src/ToolUp.KnowledgeBase.Core/Shared/SharedTypes.fs` | New `KnowledgeObjectRetentionPolicy` (+ `disabled` / `enabled` / `ObjectDataType`). |
| `src/ToolUp.KnowledgeBase.Server/Server/UploadPolicy.fs` | New `withObjectStoreRetention`. |
| `src/ToolUp.KnowledgeBase.Server/Server/Server.fs` | Re-export beside the other compose hooks. |
| `src/ToolUp.KnowledgeBase.Server/Server/Api/Deps.fs` | `KnowledgeApiDeps` gains `DataObjectStore: IDataObjectStore option`, resolved only when the policy opted in. |
| `src/ToolUp.KnowledgeBase.Server/Server/Api/Documents.fs` | `saveOriginal` / `readOriginalBytes` / `deleteOriginal` / `resolveOriginal` / `storeHoldsOriginal`; wired into `persistAndIngest`, `archiveSupersededVersion`, `deleteDocument`, `getOriginalDocument`, `getOriginalDelivery`. |

`IOriginalSourceResolver` was **not** widened. Its signature is
`(storage, container, doc)` with no `scopeId`, so a store-aware branch
could not have been expressed there without a breaking change to a
public seam that external implementations already satisfy. The
store-first branch lives in `Documents.fs` and delegates to the
resolver for every case it does not serve — which also keeps a
deployment's custom resolver authoritative for the kinds it was
registered for.

### Save

`Versioned`, **not** `StrictlyVersioned`: the policy is sticky for the
object's lifetime, and `StrictlyVersioned` makes `Delete` return
`DeleteForbidden` — which would break the KB delete cascade outright.
Retention that must survive deletion is a deployment policy, not a KB
default.

The envelope carries `kb.fileName` / `kb.fileType` / `kb.sourceKind` /
`kb.uploadedBy` / `kb.contentHash`, and `createdBy = deps.UserId`. A
`Save` failure falls back to the convention blob rather than failing an
upload that has already been admitted.

### Read (backward compatibility)

`resolveOriginal` tries `store.Get(scopeId, docId)` and, on any error,
falls through to the Phase 104 resolver — whose `UploadedFile` branch
downloads `knowledge/{docId}/{filename}`.

**That fallback IS the migration.** Documents uploaded before the
opt-in stay retrievable forever and there is no backfill step. It also
covers the reverse direction: an upload whose store `Save` failed
landed at the convention path and still reads back.

### Delete and erasure

`deleteDocument` sweeps **both** locations unconditionally — a scope
that ran for a while un-opted-in and was then opted in holds documents
from both eras, and a delete clearing only the composed one would leave
bytes at rest after reporting the document deleted. `Delete` is
idempotent on both sides.

The Phase 9h right-to-be-forgotten path needs **no KB-specific work**:
`Save` records `createdBy`, so
`IDataObjectStore.Erase(scopeId, subjectUserId, policy, dryRun)`
already matches the subject's KB originals. Chunk removal stays with
the Phase 115 `IIndexLifecycle` seam — bytes and chunks are erased by
their own owners.

**This is the phase's real delta, and it is worth stating plainly: a
convention-path original was *structurally invisible* to a data-subject
sweep.** `Erase` matches on `CreatedBy` and a raw blob write records no
such field. The test pack asserts a retained original IS matched and,
as a control, that an un-retained one is NOT — with its bytes still
sitting at rest after the sweep.

### Relationship to Phase 14x (the two dedup layers)

Complementary, and they touch disjoint regions of `Documents.fs`:

- **14x** dedups *before anything is persisted*: a scope-local
  content-hash → docId index short-circuits `uploadDocument`. It owns
  the branch **above** `persistAndIngest`.
- **105** dedups *bytes at rest*: identical content within a scope
  collapses onto one `objects/_content/{hash}.data`. It owns the
  **inside** of `persistAndIngest`.

With `withDocumentDedup false` every upload still ingests as its own
document, but the object store collapses the bytes — the opt-out keeps
its documented meaning ("separate documents") while storage stops
duplicating. That configuration is exactly where the two layers could
have disagreed, so it is the one the reconciliation test runs.

## Two known interactions, both deliberate

Neither is a defect, but both are places where composing 105 with
another opt-in changes observable behaviour, so they are recorded here
rather than left to be discovered.

**1. Signed original URLs (Phase 108) degrade to inline for retained
documents.** A store-held original has no signable `IBlobStorage` key —
its bytes live in the content-addressed pool under a hash, and the seam
signs a blob name. Left alone, `ResolveMetadata` would describe the
convention path, `GetMetadata` would miss, and the caller would get
`NoOriginalAvailable` for a perfectly retrievable document. So
`getOriginalDelivery` checks whether the store actually holds the
object and, if so, serves the Phase 102 inline result and logs the
interaction at Info. The decision is **per document**, not per
deployment: documents predating the opt-in still sign normally.
Degraded in byte-efficiency, never in correctness, and never silently.
*Follow-up:* an object-store signing surface would reunite the two.

**2. Versioning (Phase 510) still writes its archive copy.**
`archiveSupersededVersion` now READS the outgoing bytes through
`readOriginalBytes`, so it works when the live original is in the
store — without that it would have written version records claiming
preserved bytes that were never copied. It still WRITES the archive to
`knowledge/{docId}/versions/{n}/{fileName}`, because
`KnowledgeDocumentVersion.OriginalBlobName` is a wire-visible blob
handle that consumers and the delete cascade's prefix sweep already
resolve against `IBlobStorage`, and an object-store version is not
addressable by blob name. Re-pointing it would be a second, breaking
change riding along in an additive phase. **Cost:** a deployment
composing BOTH retention and versioning holds a superseded version's
bytes twice. *Follow-up:* retiring the copy needs an object-store
locator field on `KnowledgeDocumentVersion`.

## Verification

1. `dotnet build ToolUp.Forge.sln --nologo` — clean.
2. `src/ToolUp.Platform.Tests/InProcess/KbObjectRetentionTests.fs` — 7
   cases: store-backed save (and the convention blob NOT written), the
   14x reconciliation, the legacy read fallback, delete removing bytes
   AND chunks, the erasure-coverage delta against an un-retained
   control, the GP 11 un-opted-in pin, and the 510 reconciliation.
3. **Falsified, not assumed.** Removing the read fallback turns exactly
   ONE case red — the legacy-retrievability case — and nothing else.
   Routing each document's original into its own store scope (the
   dedup-agreement bug shape) turns 5 red including the "ONE content
   blob" assertion, while the two cases that do not touch the store
   write path stay green.
4. `api-baselines/ToolUp.KnowledgeBase.Core.approved.txt` and
   `…Server.approved.txt` regenerated. The `KnowledgeApiDeps`
   constructor retype is the expected consequence of the new field and
   is reviewed in the same commit.

## Rollback

Set `withObjectStoreRetention false` (or drop the call). New uploads
return to the convention path immediately, and documents already in the
store become unreadable — the read fallback only covers the other
direction. Export them first (`ListObjects` filtered to
`DataType = "knowledge-document"`, writing each to
`knowledge/{objectId}/{kb.fileName}`), or roll forward.

Reverting the commit outright is safe for any deployment that never
opted in.

## See also

- [`idataobjectstore-getcontent.md`](idataobjectstore-getcontent.md) —
  the content-by-hash fast path the resolver can use once it holds a
  `DataObject`.
