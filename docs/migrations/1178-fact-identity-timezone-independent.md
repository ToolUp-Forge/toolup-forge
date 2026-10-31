# A fact's identity does not depend on the timezone of the machine that wrote it

**Ships in:** ToolUp.Facts.Core, ToolUp.Facts.Server and ToolUp.FactStores.Postgres 0.38.0.

**Affected:** only data written, migrated or re-keyed on a machine whose local timezone is not UTC. Facts a UTC host
wrote, which covers every deployed server, keep their ids and lineages byte for byte. No code has to change.

## What was wrong

A fact's content address and its lineage key render each period endpoint as an ISO-8601 instant. They used to get
that instant through `DateTime.ToUniversalTime()`. Periods arrive from ingestion, and come back from every payload round
trip, with `DateTimeKind.Unspecified`, and `ToUniversalTime()` reads an `Unspecified` value as the running machine's
local time. So the same fact had one key on a UTC server and another on a machine elsewhere: in Europe/London during
British Summer Time, midnight on 9 September 2024 rendered as `2024-09-08T23:00:00Z`.

A key computed away from the server therefore disagreed with the server, silently. That covers a migration, a CLI, a
developer's machine and a replica in another region. A consumer's migration run on a non-UTC machine copied every row
with a `lineage_hash` the server never computes. Supersession by lineage found nothing, so a grounded report refused
every reference that needed a chain. The migration's own verification passed, because it computed both sides on the same
machine.

## What changes

1. **One rule for an instant.** `Fact.instant` reads a `Utc` value as it is, an `Unspecified` value AS UTC, and
   converts a `Local` value. The content address, the lineage key, the blob store's instant matching and the coverage
   narrative's date labels all go through it. On a UTC host it returns exactly what `ToUniversalTime()` returned, so
   nothing a UTC host computed moves.
2. **The Postgres row hashes what it stores.** `lineage_hash` is now the hash of the lineage key of the fact the row's
   `payload` reads back as, like every other column, rather than of the in-memory fact handed to the write.
3. **The migration's verification is machine-independent.** `toolup facts migrate --verify`, and the check `migrate`
   runs after its copy, compare each row's `lineage_hash` with the hash recomputed from that row's own payload. That is
   the value the server computes. A row keyed an hour off is named as a `lineage_hash` difference.
4. **`--ids` re-keys lineages too.** After moving fact ids onto their recomputed value (as before), the `--ids` mode
   moves every stored `lineage_hash` that differs from the hash recomputed from its row's payload.

## How to tell whether you are affected

Run the read-only plan against the deployment's composition root. It writes nothing:

```powershell
toolup facts migrate --dll ./publish/App.dll --all-scopes --ids --dry-run
```

With a Postgres store composed, each scope prints three lines. The `postgres lineages` line reads `would move the lineage hash of N rows in M
lineages`. Zero rows means the table already carries the values the server computes. The `blob` and `postgres` lines
report fact ids that would move. A non-zero count there means facts were written on a non-UTC host with `Unspecified`
periods.

If the original blob store is still available, `toolup facts migrate --all-scopes --verify` gives the same answer for
a migrated table, naming each fact whose `lineage_hash` differs.

## How to re-key

Stop the deployment's writers, then run the same command without `--dry-run`:

```powershell
toolup facts migrate --dll ./publish/App.dll --all-scopes --ids
```

For each scope, the lineage re-key runs in one transaction under the scope's exclusive write lock, and is guarded the
whole way:

- It is planned again under the lock, over the rows the transaction sees.
- It is refused, writing nothing, when a payload does not read back, when the rows of one stored lineage would move to
  two lineages, or when two stored lineages would move to one (or onto a hash a row that is not moving already
  carries). Each of those would change which facts share a lineage, which is not a re-key.
- Every move must touch exactly the rows it counted.
- Each move goes through a staging value, so no two lineages ever share a current head mid-move.
- Before commit the scope is re-read and every row must carry its recomputed hash, or nothing is kept.

A second run plans nothing. The exit code is `0` when every scope moved (or had nothing to move), and `1` when any
scope was refused. The refusal names the scope and the reason.

## The blob store

The blob store's index leaf names record each endpoint's kind and raw ticks, so they do not depend on the zone, and
its lineage matching now reads an `Unspecified` endpoint as UTC through the same rule. What a non-UTC host could have
written wrongly is the fact id itself, for a fact whose period was `Unspecified`. `--ids` moves those fact ids in the
blob store, and in the Postgres table when one is composed, exactly as it moves ids written before the canonical frame.

## Rollback

There is no rollback to the old rule. It produced a different key on every machine zone, so no single value could be
restored. A re-key moves only rows whose stored hash disagrees with the hash recomputed from their own payload, and it
refuses before writing anything that would merge or split a lineage.
