# Fact ids move onto one canonical frame, once

**Ships in:** ToolUp.Platform.Core, ToolUp.Platform.Server, ToolUp.Facts.Core, ToolUp.Facts.Server and
ToolUp.FactStores.Postgres 0.38.0, the next minor after the tagged 0.37.0. The change is breaking-class twice
over: a public helper is removed (`CountersignatureCanonical.sha256Hex`), and every
**fact id** changes value.

**Affected:** every deployment with persisted facts (a `BlobFactStore` scope or a `PostgresFactStore` table) — its
stored ids must be migrated once, below. Code that called one of the removed helpers moves to `Sha256.hex` /
`Sha256.hexOfUtf8`. Code that computed a fact id itself (none is known) moves to `Fact.compute`. A deployment with
no fact tier changes nothing.

## What changes

1. **One digest helper and one frame.** `ToolUp.Platform.Sha256.hex : byte[] -> string` and `hexOfUtf8 : string ->
   string` (lowercase hex), and `ToolUp.Platform.CanonicalFrame` — `text` / `frame` / `digest : string list -> …`,
   each field written `<length>:<field>` (length in UTF-16 code units), the framing the deployment evidence chain
   and the grounding-envelope seal already used. Both of those keep their bytes exactly; `CanonicalFrameTests` pins
   them.
2. **The fact id.** `Fact.compute` is `CanonicalFrame.digest ["fact"; subject; metric; period; method identity;
   …each input hash, ordinal-sorted]`. Before 0.38.0 it was SHA-256 over the same five values joined by newlines
   with the inputs comma-joined — a join a field value could re-cut. The field grammar is documented in
   [`docs/platform/facts.md`](../platform/facts.md#content-ids-one-frame-phase-1089). The lineage key,
   `Fact.valueHash` and `InputHash.isContentHash` do not change.
3. **Four derived digests move onto the frame, with nothing at rest to migrate:** a batch receipt's `Digest`
   (returned to the caller, not stored), a fact-table snapshot's digest (a watermark's `ContentDigest`; existing
   watermarks keep theirs, the next commit stamps the new form, and the delegate cache keyed on it misses once), a
   newly issued grounding certificate's node hashes (an issued certificate keeps its signed bytes and still
   verifies), and the narrative trigger's change key (see the note below).
4. **Unchanged, deliberately:** a team publication's signed manifest and a promoted artifact's signing input keep
   their bytes — signatures over them are at rest, and the second is fixed by the federation wire specification.
   The model envelope ids (fit composite key, evaluation / fold / comparison / registration ids, promotion
   decision id) keep their recipes and are pinned byte for byte; they move together with the sidecar store that
   holds them, in a later release, with their own re-key.

## Upgrade — migrate each scope once

Stop the deployment's writers (the migration plans from a snapshot), and **take a snapshot of the store first** —
a copy of the scope's blob container, or a `pg_dump` of the facts table. Then, for each scope, from a host that
composes the store (an admin job, a one-off console app, a test harness against the live configuration):

```fsharp skip=fragment
// The blob store: `store` is the deployment's BlobFactStore.
let! dry = store.MigrateIds(scopeId, true)      // reads and plans, writes nothing
let! run = store.MigrateIds(scopeId, false)

// The Postgres companion: `target` is the deployment's PostgresFactStore.
let! dry = FactStoreMigration.migrateIds target scopeId true
let! run = FactStoreMigration.migrateIds target scopeId false

// Either result renders as one line per scope:
match run with
| Ok report -> printfn "%s" (FactIdMigration.render report)
| Error refusal -> eprintfn "%s" refusal
```

The dry run prints the count and the first divergent id (`FactIdMigrationReport.FirstDivergent`). The run re-keys
every fact whose stored id is not `Fact.computeFor` of its own fields: the blob store writes each fact under its new
id with its `Supersedes` link mapped, writes its point-read index leaf, and only then deletes the old blob and leaf,
and drops the metric surfaces of every metric that moved (they rebuild on the next read); the Postgres companion
updates `fact_id`, `supersedes` and the payload in one transaction under the scope's write lock. A run interrupted
anywhere is finished by running it again. A scope holding a blob that does not read as its own fact, or a row whose
payload names another fact, is refused before anything is written, naming each one.

**What is not rewritten.** Ids a fact store does not own keep the values they were written with: the audit trail's
`FactAsserted` events, citations inside stored narratives and conversation messages, and the roots of grounding
certificates already issued. A certificate issued before the upgrade still verifies as a document, but its root names
a pre-0.38.0 id, so the fact import door of a 0.38.0 deployment — which re-derives the id — refuses it as a root
mismatch; re-issue the certificates a peer still needs after migrating. Peers exchanging certificates should upgrade
together for the same reason.

**The narrative trigger key.** A grounded-narrative job registered just before the upgrade and the same change
re-registered just after it carry different idempotency keys, so one narrative may run twice at the upgrade — once.

## Verification

- The dry run, run again after the migration, reports `re-keyed 0, relinked 0`.
- `FactStoreTests` pins the new id of a fixed fact (`40727a35…`) and the id the same fact had before
  (`c05cdc0b…`), both computed outside the code base; the `IFactStoreContract` migration law — every pre-migration
  query answers by content under the recomputed ids, the supersession chain re-keyed, a second run changes nothing —
  passes for the blob store and the Postgres companion.
- `BlobFactStore.IndexConsistencyCheck` reports no orphaned and no unindexed entries after the run.

## Rollback

Restore the snapshot taken before the run, and pin the deployment back to 0.37.x. There is no in-place reverse: the
pre-0.38.0 recipe is not shipped (a second id scheme kept alive is a second recipe), so a store migrated forward is
only taken back by its snapshot.
