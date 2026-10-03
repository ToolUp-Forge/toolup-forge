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
value, inside a pipeline that runs through `Async.Parallel` and is then discarded — ended by
`Async.Ignore` or bound by `let! _ =` (Phase 966 added the second). Collect the results and fail on
any `Error`, naming the blobs that did not go; the ones that went stay gone, and because the call is
idempotent a re-run finishes the job. The same best-effort marker admits a fan-out that is cleanup
by design.

**An operation's own delete is propagated; cleanup is best-effort (Phase 966).** The `let! _ =`
shape hid six `Delete` fan-outs on the tree, and they are not all one kind of thing:

```fsharp skip=fragment
// The operation's own delete — `IDataObjectStore.Delete`, `Evict` and `DeleteIfVersion` remove an
// object's version blobs. A refusal is the answer: collected, returned as `StorageFailure` naming
// the version blobs still in the container, and the object left addressable so a re-run finishes.
let! removal = removeVersionBlobs container versions
return removal |> Result.mapError StorageFailure

// Cleanup — a blob no record names, re-found by the next pass. Said once, on the line.
let! _ = // best-effort-write: orphan reclamation — an orphan left behind is re-collected by the next pass
    toDelete
    |> List.map (fun name -> blobStorage.Delete(container, name))
    |> Async.Parallel
```

The line the gate cannot draw for you is whether the delete IS the operation. Where a refused delete
means the caller was told something is gone that is still at rest — a data-subject's blob, an
object the caller just deleted — it is the operation: collect. Where the blob it leaves is by
construction recoverable by a pass that already exists, it is cleanup, and the marker states which
pass. The three cleanup sites the tree has are the worked example:

| Site | What the leftover is | Why it is recoverable |
|---|---|---|
| `DataObjectStore` orphaned-content reclamation | a content blob no metadata names | the next in-band pass and the scheduled orphan sweep both collect it |
| `DefaultAssetStore` derivative cleanup after an asset delete | a render-cache entry keyed by content hash | never served for a deleted record, and still valid if the same bytes return; the record's own delete is propagated before it runs |
| `WebhookRegistry` delivery-log `Prune` | a delivery row past retention | the next `Prune` matches the same age predicate |

**A single-call `Delete` is held to the same line (Phase 967).** `let! _ = store.Delete(…)` and
`do! store.Delete(…) |> Async.Ignore` drop one result instead of N, and that is the whole difference
— it is the same decision, made once per call. The rule is stated once: **a delete that is the
operation's own effect propagates; a delete that is cleanup is marked, and the pass that re-sweeps it
is named.** Four single calls under `src/` were the operation's own effect, and each reported
success (or a clean conflict) over a blob still in the container. They are the worked example, and
each has a different answer to "what is the caller told?":

| Site | The delete is | A refusal is reported as |
|---|---|---|
| `DataObjectStore.SaveIfVersion` — undo of the just-written `v{N+1}` when the head compare fails | the save's own effect: the write must not stand while the caller reads a clean conflict | `SaveFailed(StorageFailure …)` naming the blob still in the container, instead of `VersionConflict` |
| `DataObjectStore.DeleteIfVersion` — release of the claim slot | the delete's own debris: the object is gone, the claim is not | on the success path `DeleteFailed(StorageFailure …)` naming the claim blob; on a path that already failed, that failure stays the answer and the stuck claim is logged at `Warn` |
| `DefaultAssetStore.Delete` — the shared original, when no other record references the hash | the asset's own bytes at rest | `AssetDeleteError.StorageError` naming the original blob, **with the record kept**: the original is removed before the record, which is what a re-run finds the asset by |
| `BlobPeerRegistry.Remove` — a peer's registration blob | the removal itself | a raise: `IPeerRegistry.Remove` is `Async<unit>` and has no failure channel, so returning normally would claim a registered, callable peer is gone |

Two of those are not the answer the sites' first reading suggests, and both are the rule above
meeting the signature or the order of the code rather than the other way round. The asset store
deleted the record first and checked for other references afterwards, so "propagate and keep the
record" needed the original to go first (and the reference check to leave the record under delete
out of the count) — otherwise the re-run finds no asset and the original is orphaned for good. And a
void-returning interface raises, exactly as `IEventStore.Write` does below.

```fsharp skip=fragment
// The undo is the operation: a refusal is not a conflict.
let! undo = blobStorage.Delete(container, blobName)

match undo with
| Ok _ -> return Error(ConditionalSaveError.VersionConflict(expectedVersion, 0))
| Error e -> return Error(SaveFailed(StorageFailure $"… {blobName} could not be removed ({e}) …"))
```

**Enforced by default (Phase 971).** `VerifyUploadResults` reports a single-call `Delete` discarded
where it is made — one-line `let! _ =`, the call on the next lines, or its own `|> Async.Ignore` —
with no flag; a call whose result is matched on is not a site. Every remaining site on the tree was
judged one way or the other first, module by module, and the split is the rule above applied:

| The single call deletes… | Disposition | Examples |
|---|---|---|
| a record or registration the operation exists to remove | propagated through the operation's own error, or raised where the signature is `Async<unit>` | a config document's or provider profile's `Clear`, a webhook or report subscription, a data-source config, a module-visibility profile, a round's state, a calendar link, a lifecycle done-set |
| bytes at rest the caller is told are gone | propagated, **with the record a re-run finds them by deleted last** | a media item's original and renditions, a knowledge document's original, note body and prior versions, a retention purge's raw blob, a conversation's turns (the manifest goes last) |
| a pointer or membership a purge or removal must not leave behind | propagated, the pointer cleared **before** the row a retry re-reads | the active-team pointer in `RemoveMember`, `PurgeUser`, `PurgeTeam` and `SetArchived` |
| scratch, a cache entry, a probe or sentinel, an expired entry a later pass re-reaps | best-effort: `Warn` where a logger is in reach, else the marker naming the pass | preflight sentinels, the render cache's expired entry, TTL reaps in the idempotency and peer-job stores, the outbox's intents, retention eviction, upload-session scratch on commit |

Two findings from that triage are worth keeping. A discard can hide that a delete NEVER happens:
conversation turns are written strictly versioned, so `IDataObjectStore.Delete` refused every one
of them and the conversation delete answered `Ok` over every turn still at rest — the turns now go
through `Evict`, the retention owner's removal. And a few cleanup sites have no pass that reclaims
a leftover: a fact-table run's staged rows and an ended run's kept provenance (read by nothing once
the run is recorded terminal). Their markers say so in as many words, so a reader meets a known
storage leak, not an oversight. A stale secondary-index ref (`BlobIndex`) was the third until
Phase 973 gave it a reclaimer: `BlobIndex.Vacuum`, which the entity store runs on every lookup that
meets a stale ref and over a whole index through `BlobEntityStore.VacuumIndex`.

**History — why Phase 967 shipped it opt-in.** With the four sites above fixed, the walk still
reported 69 unmarked sites across 43 files — probe and sentinel deletes, cache and retention sweeps,
membership and subscription records, outbox intents — that nobody had yet judged as the operation's
own effect or as cleanup. A default scan that is red on the tree gates nothing, so 967 shipped it
behind `--single-delete` and Phase 971 made it the default once those were triaged. The flag is
accepted as a no-op for one release.

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
| `DataObjectStore` | `Delete`, `Evict` and `DeleteIfVersion`'s version-blob removal (Phase 966) | every result collected; a refusal returns `StorageFailure` (`DeleteFailed(StorageFailure …)` from `DeleteIfVersion`) naming the version blobs still in the container. v1 is removed last and only once the rest went, so the object stays addressable for a re-run; the blobs that went stay gone and only their content is reclaimed. A `DeleteIfVersion` re-run states the head the store now reports |
| `DataObjectStore` orphan reclamation, `DefaultAssetStore` derivative cleanup, `WebhookRegistry` delivery-log `Prune` | cleanup deletes, fanned out | best-effort by design, each carrying `// best-effort-write:` naming the pass that recovers a leftover — the next sweep, a cache that is never served stale, the next `Prune` |

The rest of the tree was swept to the same rule in Phase 863. The patterns, by what the write is:

| The write is… | Disposition | Examples |
|---|---|---|
| the record a caller or a later read depends on, with a `Result` (or status) to carry it | returned as that failure | an active-team switch, a feature-flag erasure, a knowledge note or narrative (source persisted first, so a refusal moves nothing else), an upload whose original or archived predecessor did not land (`UploadRejected`) |
| the same, behind an `Async<unit>` it cannot change | raised | the export ticket's status and envelope, the lifecycle ledger, a peer job result or group binding, a round's state, the KB index, the membership doctor's save |
| one of two writes where the second destroys the first's source | the second runs only if the first landed | outbox and pending-invite quarantine: copy, then delete or heal |
| a cache, a derived index, a resumable cursor, or a copy of a record held elsewhere | best-effort: `Warn`, or the marker where no logger is in reach | the render cache, `BlobIndex` refs, the audit-replicator cursor, the ingestion-run history, the compute memo, the IndexNow state |
