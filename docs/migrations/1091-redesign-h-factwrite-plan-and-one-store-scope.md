# The write of a fact is one plan, executed by every store

**Ships in:** ToolUp.Facts.Core, ToolUp.Facts.Server and ToolUp.FactStores.Postgres 0.38.0, the next minor after
the tagged 0.37.0. Breaking-class at the BINARY level only: the types a write is described in moved assembly.
Source that opens `ToolUp.Facts` recompiles unchanged, and nothing persisted changes shape.

**Affected:** a consumer that compiled against `ToolUp.Facts.Server` and is loaded against the new packages without
recompiling (rebuild it), and a consumer implementing `IFactStore` itself (none is known beyond the Postgres
companion), which can now execute the shared plan instead of restating the write core.

## What changes

1. **`FactWrite.plan` decides what asserting drafts means** (`ToolUp.Facts.Core`, `Shared/FactWrite.fs`).
   `FactWrite.prepare shape drafts` pre-flights a request (`WriteShape.BatchAssert` refuses a batch with a
   malformed draft, naming each by position; `WriteShape.ScalarAssert` never refuses) and addresses each draft.
   `FactWrite.plan now isStored lineageFacts request` returns a `WritePlan`: the facts to store with their ids,
   `AsOf` and `Supersedes`, the supersession edges, the head moves (`IndexDelta`), the audit rows
   (`FactAuditEvent`), the unchanged addresses and the receipt. `BlobFactStore` and `PostgresFactStore` execute
   it; a delegated store's writes are its inner store's.
2. **Moved, unchanged, same namespace:** `FactDraft` (and `FactDraft.defects`), `BatchAssertOutcome`,
   `BatchAssertReceipt`, `FactEvents`, `FactAssertedEvent`, `FactSupersededEvent` and `FactBatchAssertedEvent`
   now live in `ToolUp.Facts.Core` rather than `ToolUp.Facts.Server`.
3. **Added:** `FactWrite`, `WritePlan`, `WriteRequest`, `WriteShape`, `AddressedDraft`, `IndexDelta`,
   `FactStoreError`, `FactAuditEvent`, `FactCompetition` (Facts.Core); `FactAudit.write` / `FactAudit.payload`
   and `BlobFactStore.SurfaceFaults` (Facts.Server).
4. **Behaviour that changes:**
   - A blob store's failed surface-maintenance step after a write is counted on `BlobFactStore.SurfaceFaults`
     and logged (warning) through the store's logger. It still never fails the write.
   - A delegated point read whose retirement of a removed row fails now fails the read (a source-unavailable
     refusal) rather than discarding the failure and serving the stale head as current.
   - A store reads its clock once per write rather than once per draft. Every `AsOf` rule holds (strictly
     increasing within a lineage); a batch's new facts in different lineages share one `AsOf`.

Nothing on the wire or at rest moves: the audit rows, the receipt and its digest, the fact ids and the stored
facts are byte-for-byte what they were.

## The code change

```diff
- // a custom IFactStore restating the write core
- let dispositions = (* derive ids, heads, AsOf, supersession by hand *)
+ match FactWrite.prepare WriteShape.BatchAssert drafts with
+ | Error offenders -> Error(FactWrite.renderOffenders (List.length drafts) offenders)
+ | Ok request ->
+     let plan = FactWrite.plan (clock ()) isStored lineageFacts request
+     // persist plan.Facts, apply plan.IndexDeltas, then:
+     do! FactAudit.write events scopeId plan.Audit
+     Ok plan.Receipt
```

No codemod is needed for this part: no call site changes.

## Verification

Run the `IFactStore` contract pack over your store (`IFactStoreContract.tests "<name>" factory`). Its write-plan
law asserts that a batch stores exactly the facts `FactWrite.plan` derives over what the store held, and answers
with the plan's receipt, and that a scalar assert stores the plan's fact.

## Rollback

Pin the 0.37.0 packages. No data migration was made, so nothing needs undoing.

## Not in this release: one scope form

The redesign also removes the seven `string` overloads of `IFactStore`, so that `ResolvedScope` is the only store
scope, through a string-to-scope adapter at the API edge and a codemod. That part has NOT shipped. A
string-to-scope adapter is a public way to mint a `ResolvedScope` from any string, which the scope type exists to
prevent; whether and where to offer one is an open decision. Until it is made, both forms of every member stay,
reads raise a storage fault rather than typing it, and the answer planner keeps its two scope forms.
