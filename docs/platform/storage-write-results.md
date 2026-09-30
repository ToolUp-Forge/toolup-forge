# Storage writes report failure

`IBlobStorage.Upload` returns `Async<Result<string, string>>`, and every shipped blob store reports
a failed write as `Error` — not as an exception. So a caller that discards the result has written a
store whose writes cannot fail as far as anyone above it knows:

```fsharp skip=fragment
let! _ = storage.Upload(container, name, bytes)   // a failed write reads as a success
```

Two defaults shipped exactly that shape until Phase 863. The event store's `Write` discarded it, so
under the default composition a lost audit row was counted as written: the audit log detects a
failed write by the exception its store raises, and there was none — no counter, no refusal under
`RefuseAction`, no spill under `DegradeToFile`. The blob idempotency store discarded it, so a
failed memoisation write let a retry re-execute a call the store had claimed to remember.

## The rule

**A write that did not happen reaches its caller.** Every `Upload` result is either propagated or
handled, and the only discard that survives is one that says why on its own line.

`dotnet run --project Build.fsproj -- VerifyUploadResults` enforces it over production code under
`src/` (test projects excluded — a fixture that seeds a blob is not a store). It fails on
`let! _ = ….Upload(` in both layouts Fantomas produces, the one-line form and the form with the
call on the next line, and names each site.

## Choosing: propagate, or declare best-effort

**Propagate** when the write IS the operation — the canonical record, the thing a later read or
retry depends on. Match on the result:

- where the enclosing signature carries failure (`Result`, a `…Error` DU), return it;
- where it does not (`Async<unit>` on an interface you cannot change), **raise**. The exception is
  the one channel such a signature has, and a normal return is a claim the write happened.
  `IEventStore.Write` and `IIdempotencyStore.Store` are both this case, and the rule is written on
  `IEventStore.Write` so an implementation of it knows. A caller that must not fail on the raise —
  the remoting dispatcher, whose answer has already been sent when it memoises — catches it and
  reports the call as NOT memoised rather than letting it pass as success.

```fsharp skip=fragment
match! blobStorage.Upload(container, name, bytes) with
| Ok _ -> return ()
| Error storageError -> return raise (StoreWriteException(container, name, storageError))
```

**Declare best-effort** only when losing the write loses nothing that is not recovered elsewhere —
a cache fill the next read repeats, a derived index that `Rebuild` regenerates and a consistency
check reports as drift. Best-effort still does not mean silent: match the `Error` and log it at
`Warn`, so an operator can see a store that has stopped accepting writes.

```fsharp skip=fragment
match! storage.Upload(container, name, bytes) with
| Ok _ -> ()
| Error e -> logger.Warn(sprintf "render cache fill failed for %s: %s — next read re-renders" name e)
```

A discard that must stay a discard — no logger in reach, recovery provably elsewhere — carries the
marker on the discarding line, and the gate admits it:

```fsharp skip=fragment
let! _ = storage.Upload(container, name, bytes) // best-effort-write: <why losing this loses nothing>
```

The marker is a claim a reviewer can check, not an escape hatch: if you cannot write the reason in
one line, the write is not best-effort.

## What the default stores do now

| Store | Write | On a failed write |
|---|---|---|
| `PersistentEventStore` | canonical event blob | raises `EventStoreWriteException`; the audit log's failure policy acts on it |
| `PersistentEventStore` | `_by-type` / `_by-source` index refs | best-effort by design — canonical is authoritative, drift shows in `IndexConsistencyCheck` and `Rebuild` repairs it |
| `BlobIdempotencyStore` | memoised response | raises; the dispatcher answers the call, logs it as NOT memoised, and still emits the method's audit event |
