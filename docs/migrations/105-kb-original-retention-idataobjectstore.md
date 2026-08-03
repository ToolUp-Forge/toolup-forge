# KB original-document retention on `IDataObjectStore` (Phase 105)

> **Status: PLANNED — not yet shipped.** No consumer action is required yet.
> This page is the design contract the implementing session follows; it is
> published ahead of the code because the retention seam is shared with
> Phase 14x and the shape had to be agreed before either side rewrote it.
> The "What changes" section is the intended end state, not a landed one.

## What changes

KB originals stop being written as a raw `IBlobStorage` blob at the convention
path `knowledge/{docId}/{filename}` and are saved through `IDataObjectStore`
instead, which already provides content-addressable dedup at rest, a metadata
envelope, versioning, and the Phase 9h `Erase` surface.

**The objectId is the KB `docId` — `KnowledgeDocument` does not gain a field.**
`(scopeId, objectId)` is the store's identity, `docId` is already unique within
the scope, already carried in chunk metadata (Phase 103 `stampOriginalRefs`),
and already the key `getOriginalDocument` receives. Reusing it makes retrieval a
direct `Get(scopeId, docId)` — the phase's "direct lookup, not a convention
rebuild" goal — with **no wire change, no persisted-record widening, and
therefore none of the additive-field hazards** (`FableConverters` deserialising a
missing list/`Map` to `null`; `Fable.SimpleJson` `parseAs` throwing on a missing
field and resetting the client record; a retyped member failing the Phase 175
public-API approval baseline). An explicit `ObjectId: string option` on
`KnowledgeDocument` remains possible if a future backend needs an objectId that
is not the docId — see [Open decision](#open-decision).

Effective files (note the path: the handlers live under `Server/Api/`, not
`Server/Documents.fs` — the phase's `key_files` predates that split):

| File | Change |
|---|---|
| `src/ToolUp.KnowledgeBase.Server/Server/Api/Deps.fs` | `KnowledgeApiDeps` gains `DataObjectStore: IDataObjectStore option`, resolved with the same probe-with-default shape as `AuditLog` / `NarrativeStore`. |
| `src/ToolUp.KnowledgeBase.Server/Server/Api/Documents.fs` | `persistAndIngest` saves through the store; `deleteDocument` deletes through it. |
| `src/ToolUp.KnowledgeBase.Server/Server/OriginalSourceResolver.fs` | `UploadedFile` branch reads the store first, falls back to the convention blob. |

`None` (no `IDataObjectStore` registered) leaves every path byte-for-byte at its
pre-105 behaviour (GP 11 / GP 13).

### Save

```fsharp
// in persistAndIngest, replacing: deps.Storage.Upload(container, rawBlobName, bytes)
match deps.DataObjectStore with
| Some store ->
    let metadata =
        Map [ "kb.fileName", safeName
              "kb.fileType", ext
              "kb.sourceKind", "UploadedFile"
              "kb.uploadedBy", deps.UserId
              yield! (contentHash |> Option.map (fun h -> "kb.contentHash", h) |> Option.toList) ]
    let! _ = store.Save(deps.Scope.ScopeId, docId, bytes, "knowledge-document", deps.UserId, metadata, Versioned)
    ()
| None ->
    let! _ = deps.Storage.Upload(deps.Scope.Container, sprintf "knowledge/%s/%s" docId safeName, bytes)
    ()
```

`Versioned`, **not** `StrictlyVersioned`: the policy is sticky for the object's
lifetime, and `StrictlyVersioned` makes `Delete` return `DeleteForbidden` — which
would break the KB delete cascade. Retention that must survive deletion is a
deployment policy, not a KB default.

### Read (backward compatibility)

The `UploadedFile` branch of `DefaultOriginalSourceResolver.Resolve` tries
`store.Get(scopeId, docId)` and, on `Error NotFound`, falls back to downloading
`knowledge/{docId}/{filename}`. That fallback **is** the migration: existing
convention-stored documents stay retrievable forever, and there is no data
backfill step. The migration is one-way — a document saved through the store is
not written to the convention path, so a rollback after new uploads leaves those
uploads unreadable (see [Rollback](#rollback)).

### Delete and erasure

`deleteDocument` replaces `Storage.Delete(container, "knowledge/{docId}/{name}")`
with `store.Delete(scopeId, docId)`, which also garbage-collects the content blob
once no other object references it. The Phase 9h right-to-be-forgotten path needs
**no KB-specific work**: `Save` records `createdBy = deps.UserId`, so
`IDataObjectStore.Erase(scopeId, subjectUserId, policy, dryRun)` already matches
the subject's KB originals by `CreatedBy`. Chunk removal stays with the Phase 115
`IIndexLifecycle` seam, unchanged — bytes and chunks are erased by their own
owners.

### Relationship to Phase 14x (the two dedup layers)

They are complementary and touch disjoint regions of `Documents.fs`, so neither
phase rewrites the other's code:

- **14x** dedups *before anything is persisted*: a scope-local content-hash →
  docId index short-circuits `uploadDocument` and returns the existing document,
  so the object store is never called for a duplicate. It owns the branch
  **above** `persistAndIngest`.
- **105** dedups *bytes at rest*: identical content within a scope collapses onto
  one `_content/{hash}.data`. It owns the **inside** of `persistAndIngest`.

With `withDocumentDedup false` (14x opted out) every upload still ingests as its
own document, but the object store collapses the bytes — the opt-out keeps its
documented meaning ("separate documents"), while storage stops duplicating. The
two layers agree because they answer different questions: 14x decides whether to
*ingest*, 105 decides how many *copies of the bytes* exist.

## Verification

1. `dotnet build ToolUp.Forge.sln --nologo` — clean.
2. `dotnet run --project Build.fsproj -- VerifyAll` — twelve packs `PASS`.
3. New cases in `src/ToolUp.Platform.Tests/InProcess/` covering: re-upload of
   identical bytes with `withDocumentDedup false` stores one content blob;
   `getOriginalDocument` returns bytes via the store; a document written at the
   legacy convention path (no store object) still resolves through the fallback;
   `deleteDocument` removes bytes and chunks; `Erase HardDelete` removes both.
4. No `api-baselines/*.approved.txt` change is expected — the recommended design
   adds no public member and retypes none.

## Rollback

Revert the commit. Deployments that never registered an `IDataObjectStore` are
unaffected. Deployments that did: documents uploaded while Phase 105 was active
live only in the object store and become unreadable after revert — export them
first (`ListObjects` filtered to `DataType = "knowledge-document"`, writing each
to `knowledge/{objectId}/{kb.fileName}`), or roll forward instead.

## Open decision

Task 2 of the phase says "store the returned `objectId` on `KnowledgeDocument`".
This page recommends **not** doing so, deriving `objectId = docId` instead. The
operator should confirm before implementation: the explicit field costs a
persisted-record migration on both tiers and buys the ability to point a KB
document at an object whose id is not its docId, which no current requirement
asks for.

## See also

- [`idataobjectstore-getcontent.md`](idataobjectstore-getcontent.md) — the
  content-by-hash fast path the resolver can use once it holds a `DataObject`.
