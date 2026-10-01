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
`src/` (test projects excluded — a fixture that seeds a blob is not a store), and CI runs it in the
`source-citations` job. It knows two shapes of discard, for `….Upload(` and for the conditional
`….UploadWithETag(`, whose result also carries the precondition, and names each site.

**The single call** — `let! _ = ….Upload(`, in both layouts Fantomas produces (the one-line form
and the form with the call on the next line), or the same call ended by `|> Async.Ignore`.

**The fan-out** — a list of uploads run through `Async.Parallel` and then discarded. Every result
is dropped at once, and no line of it binds `_` to an upload, so it reads as harmless:

```fsharp skip=fragment
do!
    matched
    |> List.map (fun (name, doc) -> storage.Upload(container, name, redact doc))
    |> Async.Parallel
    |> Async.Ignore   // one refused write of N reads as N successes
```

Two data-subject erasure paths shipped exactly this until Phase 960: a refused redaction was
dropped with the rest, the erasure reported success, and the erasure ledger recorded completion
over a blob that still named the subject. The gate walks the whole discarded expression — the
pipeline an `Async.Ignore` ends, or everything a multi-line `let! _ =` binds — and reports an upload
call inside it whose result is the element's value. A fan-out that handles each result where it is
made (`let! r = storage.Upload(…)` then a `match`, or `match! storage.Upload(…) with`) discards
nothing and is not a site. Collect the results instead, and act on the refusals:

```fsharp skip=fragment
let! outcomes =
    matched
    |> List.map (fun (name, doc) -> async {
        let! result = storage.Upload(container, name, redact doc)
        return name, result
    })
    |> Async.Parallel

let refused = outcomes |> Array.choose (fun (name, r) -> if Result.isOk r then None else Some name)
```

**A `Delete` fan-out is held to the same rule (Phase 965).** `IBlobStorage.Delete` is idempotent —
deleting a blob that does not exist returns `Ok`, and `IBlobStorageContract` pins that on every
bound backend — so an `Error` from it is a refusal, never a not-found. A hard-delete erasure that
fanned its deletes out through `Async.Parallel |> Async.Ignore` reported a subject's blobs gone
while a refused one was still in the container, and the erasure ledger recorded completion. The
gate reports a `Delete` call in the fan-out shape, under `src/`: the call must be the element's
value, inside a pipeline that runs through `Async.Parallel` and ends in `Async.Ignore`. Collect the
results and fail on any `Error`, naming the blobs that did not go; the ones that went stay gone,
and because the call is idempotent a re-run finishes the job. The same best-effort marker admits a
fan-out that is cleanup by design.

The `Delete` walk is narrower than the upload walk, on purpose. A single `Delete` whose result is
discarded (`do! store.Delete(…) |> Async.Ignore`, or `let! _ =`) is **not** a site, and neither is a
`let! _ =` that binds a `Delete` fan-out: a delete that is cleanup — orphaned content, a derivative
cache, a pruned delivery log — is recoverable by the next pass, so the gate leaves it to review. The
line it draws is erasure: where a refused delete means a data-subject's blob is still at rest,
collect.

The gate is textual. A result dropped some other way — through a helper that returns the
upload's `Result` under another name, say — is the same defect, and a reviewer holds it to the
same rule.

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
| `BlobConfigStore`, `DataObjectStore` | an erasure's redactions, fanned out | every result collected; a refusal fails the erasure with `HandlerPartialFailure` naming the blobs not redacted (those that landed stay redacted, so a re-run finishes), and the erasure ledger records `ErasureFailed`. The tombstone content is written before any metadata names it |
| `BlobConfigStore`, `DataObjectStore` | a hard-delete erasure's deletes, fanned out | every result collected; a refusal fails the erasure with `HandlerPartialFailure` naming the blobs not deleted (those that went stay gone, so a re-run finishes), and the erasure ledger records `ErasureFailed`. `DataObjectStore` reclaims orphaned content only for version blobs that went, so no content is reclaimed from under metadata that still names it |

The rest of the tree was swept to the same rule in Phase 863. The patterns, by what the write is:

| The write is… | Disposition | Examples |
|---|---|---|
| the record a caller or a later read depends on, with a `Result` (or status) to carry it | returned as that failure | an active-team switch, a feature-flag erasure, a knowledge note or narrative (source persisted first, so a refusal moves nothing else), an upload whose original or archived predecessor did not land (`UploadRejected`) |
| the same, behind an `Async<unit>` it cannot change | raised | the export ticket's status and envelope, the lifecycle ledger, a peer job result or group binding, a round's state, the KB index, the membership doctor's save |
| one of two writes where the second destroys the first's source | the second runs only if the first landed | outbox and pending-invite quarantine: copy, then delete or heal |
| a cache, a derived index, a resumable cursor, or a copy of a record held elsewhere | best-effort: `Warn`, or the marker where no logger is in reach | the render cache, `BlobIndex` refs, the audit-replicator cursor, the ingestion-run history, the compute memo, the IndexNow state |
