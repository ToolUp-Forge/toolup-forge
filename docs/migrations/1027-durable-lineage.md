# Durable lineage: a lineage link survives a lineage-store outage

**Ships in:** ToolUp.Platform.Core (`LineageDurabilityMode`, `ServerConfig.LineageDurability`, the
`TOOLUP_LINEAGE_DURABILITY` config key), ToolUp.Platform.Server (`ServerApp.withDurableLineage`,
`ResultStore.LineageOutbox`, `ResultStore.LineageIntent`, `ResultStore.LineageDeadLetter`,
`ResultStore.LineageOutboxDefaults`, `EntityOutboxRelayService.OutboxRelayService`), and the
`withDurableLineage` twins on `AIServerApp` and `FormsServerApp`. Ships in 0.28.0 (Phase 1027).

**Who is affected:**
- Code that builds a `ServerConfig` as a full record literal. The record has a new field, so such code fails with
  FS0764. Start from `ServerConfig.defaults` with a `with` expression instead.
- Deployments that record lineage (`Lineage = EnabledLineageStore` with a result store) and want a lineage-store
  outage to leave no hole in provenance.

Every other deployment sees no change. Durable lineage is off by default.

## What changes

**Off (the default):** a lineage link the store fails to record is logged at Warning, with every field needed to
re-record it by hand, and dropped. That is exactly the behaviour Phase 1025 shipped.

**On:** a result save's lineage links ride the lineage outbox and are retried until they are recorded:
1. Before the save, every link of the save is staged in ONE intent blob under `_platform/lineage-outbox/`. The intent
   carries the result's next version as a witness. The in-memory result store stages after its save, without a
   witness, because that save cannot fail and does not survive a restart anyway.
2. The result is saved. If the save fails, the intent is withdrawn. If an intent is left behind, the relay discards
   it, unrecorded, once it outlives the abandon window without the witness. A link never points at a result that
   never landed.
3. The links are recorded at once, as before. If all are recorded, the intent is deleted. If any fails, Phase 1025's
   Warning is logged for each failed link and the intent keeps its attempt count, last error and next attempt time.
   The relay retries it from there.

If the staging write itself fails, the save still succeeds. Its links take the off path (one attempt, logged and
dropped on failure), and the staging failure is logged at Error. Lineage never takes down a result save.

**Delivery is at least once, deduplicated by link id.** Before each retry, the relay reads
`ILineageStore.GetAncestors` for the result once and records only the links whose `LinkId` is not already among its
edges. A partly delivered intent, or a crash between recording a link and deleting its intent, therefore never doubles
a link. This works over any `ILineageStore`, and the interface is unchanged. Links of one intent are recorded in input
order. There is **no ordering promise across intents or across scopes** (portability rule 5).

**Retry is data:** a `RetryPolicy` whose count includes the inline first attempt, with exponential backoff.
`LineageOutboxDefaults.retryPolicy` allows 50 attempts, backing off from 1 minute to 1 hour, which rides out about two
days of outage. The settle and abandon windows are Phase 599's (30 s and 10 min). To change any of them, construct
`LineageOutbox` with its full constructor.

**Dead letters.** An intent that exhausts the budget is copied to `_platform/lineage-outbox-dead/` with its attempts,
last error and time, then removed from staging. Each of its links is logged at Error, carrying Phase 1025's
reconstructable text. Nothing is dropped silently. Resolve `ResultStore.LineageOutbox` from DI to read them:
`PendingCount()`, `DeadLetterCount()` and `DeadLetters()`. `/dev/inspect` shows both counts in a "Lineage durability"
panel. Re-drive is an operator act: move a dead letter's intent back under `lineage-outbox/`. Any link already in the
graph is skipped.

**Retention.** A staged intent stays until it is recorded, abandoned (its save never landed, after the abandon window)
or dead-lettered (after the retry budget). A dead letter stays until an operator removes it. A corrupt intent is
quarantined under `_platform/lineage-outbox-poison/`.

**The relay shares the entity outbox's process-profile gate.** The lineage relay runs as a second host of the same
drain as the Phase 599 entity-outbox relay, gated as `EntityOutboxRelaySubsystem`. **A process profile that skips that
subsystem (`WebOnly`, `DispatcherOnly`, `ServerlessHost`) retries no staged lineage link either.** On such a process
the inline first attempt still runs; the retries happen on a process that runs the subsystem (`AllInOne` /
`WorkerOnly`). `/dev/inspect` lists the row as "Outbox relays (entity, lineage)". Durable lineage is independent of
`withEntityOutbox`: it needs neither the entity store nor the entity outbox.

## How to adopt

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withDurableLineage true
```

Or set `TOOLUP_LINEAGE_DURABILITY=enabled`. It needs `Lineage = EnabledLineageStore` and a result store. If either is
missing, compose logs a Warning and builds no outbox, so links stay log-and-drop.

## Verification

- `/dev/inspect` shows a "Lineage durability" panel with `Pending` and `DeadLettered`.
- With the lineage store unavailable, save a result that has inputs. The link's Warning is followed by an
  `event=link_deferred` line, and `Pending` reads 1. Once the store is back, the next relay pass logs
  `event=relayed` and `Pending` returns to 0.

## Rollback

Turn it off (`withDurableLineage false`, or remove the env var). The relay stops, and intents still staged stay in
blob storage untouched. Turning it back on resumes them.
