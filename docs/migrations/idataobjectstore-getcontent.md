# `IDataObjectStore.GetContent` — content-by-hash fast-path (breaking for custom stores)

**Forge commit:** `d93d003` (`perf(files): add IDataObjectStore.GetContent; hydrate by content hash`)

## What changes

`IDataObjectStore` gains a new **abstract member**:

```fsharp
abstract GetContent: scopeId: string * contentHash: string -> Async<Result<byte[], DataObjectError>>
```

Content blobs are content-addressable and deduplicated per scope, so a caller
that already holds a `DataObject` (e.g. every entry from `ListObjects`) can read
its bytes with a single blob download — skipping the version-chain listing and
the metadata round-trip that `Get` / `GetVersion` pay to resolve the latest
version and its `ContentHash` first. `SessionFileStore.loadPersistedFiles` uses
it to hydrate a scope's files (and entry sidecars) from the metadata its
`ListObjects` sweep already returned. Returns `StorageFailure` when no content
blob with that hash exists in the scope — list-then-read callers treat a vanished
blob as "skip", the same as a failed `Get`.

Portability audit (GP 12): identity by value (`string` scopeId + contentHash),
async at the boundary, failure as `DataObjectError` data, stateless between
calls, single-scope (no cross-shard ordering claim), no precision surface.

> ⚠️ **This is the one breaking change in the 0.6.0 minor bump.** F# interfaces
> cannot carry a default member body, so adding an abstract member to
> `IDataObjectStore` breaks **every existing implementation that does not add
> `GetContent`** — including external/custom stores. (Contrast the sibling
> `IObjectCounter` from Phase 171, which was deliberately kept as a *separate
> optional* interface precisely to avoid this — a count the catalog can derive.
> `GetContent` is not derivable cheaply, so it lands on the interface itself.)

The SDK's own implementations are updated in-tree:
`DataObjectStore` (default in-process) implements the fast-path; the blob-backed
default reads the content blob directly by hash.

## Diff to apply

**Consumers using the SDK's default `IDataObjectStore` need no change** — the
default impl ships the member.

**A consumer with a *custom* `IDataObjectStore` implementation MUST add the
member.** Minimal correct shape — read the content blob directly by hash, and
mirror your existing `Get` failure handling:

```fsharp
type MyDataObjectStore(...) =
    // ...existing members...

    interface IDataObjectStore with
        // ...existing members...

        member _.GetContent(scopeId, contentHash) = async {
            match! myBlobBackend.TryRead(scopeId, contentHash) with
            | Some bytes -> return Ok bytes
            | None -> return Error (StorageFailure $"no content blob {contentHash} in scope {scopeId}")
        }
```

If your backend has no cheaper path, a correct (non-optimised) fallback is to
resolve the object by hash through your existing list/get and return its bytes —
the member only needs to be *correct*; the perf win is a bonus where the backend
can read content-addressably.

## Verification steps

1. `dotnet build` your consumer solution — a custom store that omits the member
   fails to compile with the missing-member error; that compile error **is** the
   migration signal.
2. `dotnet build ToolUp.Forge.sln` — the in-tree default store builds green.
3. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj`
   — the `IDataObjectStoreContract` pack pins the `GetContent` behaviour
   (present-hash → `Ok bytes`; absent-hash → `Error`); validate any custom store
   against the same contract.

## Rollback

Reverting `d93d003` removes the member and restores the prior interface. A custom
store that added `GetContent` keeps compiling against the narrower interface (an
extra member is harmless), so rollback is safe in either direction.
