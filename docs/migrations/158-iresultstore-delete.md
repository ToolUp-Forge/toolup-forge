# Phase 158 — `IResultStore` delete-by-key + eviction surface (consumer adoption)

**What changes.** `IResultStore` gains two methods — `DeleteResult` and
`DeleteByPrefix` — giving the result store a real eviction surface. Before
this, the store was append-only (`SaveResult` / `GetLatest` / `ListResults` /
`CompareVersions`), so a cache layer built on top of it (the Forms analyser
cache) could only *report* a would-be-cleared count and rely on
overwrite-on-next-write; dead versions accumulated in the underlying
blob/entity store. The new methods hard-delete.

```fsharp
abstract DeleteResult:
    scopeId: string * moduleName: string * resultType: string -> Async<Result<unit, DataObjectError>>

abstract DeleteByPrefix:
    scopeId: string * moduleName: string * resultTypePrefix: string -> Async<Result<unit, DataObjectError>>
```

Both are **idempotent** — deleting an absent result, or a prefix that matches
nothing, returns `Ok ()`. `DeleteResult` removes every version of the result
identified by `(moduleName, resultType)`; `DeleteByPrefix` removes every result
under `moduleName` whose `resultType` starts with `resultTypePrefix`.

**Design — direct delete (route a), not tombstone+sweep (route b).** Idempotent
direct delete is portability-clean (identity-by-value keys, async boundary,
failure-as-`Result` data, stateless, no cross-shard ordering claim, no timing
surface — the full six-rule audit sits in the `IResultStore` doc-comment) and
does **not** pull in the unshipped `IJobScheduler`-backed sweep. A distributed
store satisfies the same contract by making its underlying delete idempotent; it
may choose tombstone semantics internally without changing this surface.

**Supporting primitive — `IDataObjectStore.Evict`.** The blob-backed
`PersistentResultStore` writes results under `StrictlyVersioned` policy, and
`IDataObjectStore.Delete` refuses to delete `StrictlyVersioned` objects (the
audit-retention guard). A new additive `IDataObjectStore.Evict(scopeId,
objectId)` performs the same blob-removal path as `Delete` but bypasses that
guard — the same deliberate-operator-choice reasoning that already lets `Purge`
(scope-wide) and `Erase HardDelete` (subject-wide) override it; `Evict`
completes the set with the per-object axis. Audit-tracked deployments that must
never evict a result simply never call `DeleteResult` / `DeleteByPrefix` —
`Delete`'s `StrictlyVersioned` guard remains the default path.

## Diff to apply

### Additive interface methods — existing implementations grow the method

The change is **additive**. Any in-tree consumer that constructs the SDK
defaults (`InMemoryResultStore` / `PersistentResultStore`) gets the new behaviour
for free — no source change. The only consumers that must add code are those
that ship their **own** `IResultStore` or `IDataObjectStore` implementation (none
exist in the tree today).

A consumer with a hand-rolled `IResultStore` adds:

```fsharp
member _.DeleteResult(scopeId, moduleName, resultType) = async {
    // remove every version of (moduleName, resultType); absent ⇒ Ok ()
    return Ok()
}

member _.DeleteByPrefix(scopeId, moduleName, resultTypePrefix) = async {
    // remove every result under moduleName whose resultType starts with the
    // prefix; no match ⇒ Ok ()
    return Ok()
}
```

A consumer with a hand-rolled `IDataObjectStore` adds an `Evict` mirroring its
`Delete` minus the `StrictlyVersioned` guard:

```fsharp
member _.Evict(scopeId, objectId) = async {
    // same removal path as Delete, no StrictlyVersioned refusal; absent ⇒ Ok ()
    return Ok()
}
```

### Forms analyser cache — internal, no consumer action

`IAnalyserCache.MarkStale` is rewired from a no-op count-reporter to a real
hard-delete via the new surface. This is internal to `ToolUp.Forms.Server`; the
`MarkStale` signature and read-through semantics are unchanged, so Forms
consumers need no action. Eviction is best-effort (a transient delete failure
leaves stale entries the next compute-and-store overwrites), so it never faults
the caller.

## Verification steps

- `dotnet build ToolUp.Forge.sln` — clean (the additive methods compile against
  every existing impl + the two test stubs that fully implement the interfaces).
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` —
  the `IResultStoreContract` pack now includes a delete section (round-trip,
  delete-absent-is-`Ok`, prefix delete, scope + module isolation) bound to both
  `InMemoryResultStore` and `PersistentResultStore`.
- `dotnet run --project src/ToolUp.Forms.Tests/ToolUp.Forms.Tests.fsproj` — the
  `AnalyserCache` pack now includes `ResultStoreAnalyserCache` real-eviction
  tests (`MarkStale` removes prior versions; `ListResults`/`TryLookup` then
  miss; version-scoped and schema-wide eviction; zero-count no-op).

## Rollback

Revert the commit. The methods are additive and unreferenced by any external
consumer, so removal is safe; the Forms cache reverts to the
count-reporter-plus-overwrite behaviour. No persisted data shape changed —
`Evict` deletes the same blobs `Delete` would, so there is no migration to undo.
