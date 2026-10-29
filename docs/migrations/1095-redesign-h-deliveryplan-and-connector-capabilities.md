# A connector declares what it can do, and a declared delivery is a plan

**Ships in:** ToolUp.Platform.Server and ToolUp.DataSources.Common 0.38.0, the next minor after the tagged 0.37.0.
The change is breaking-class: a new abstract member on `IDataSource`, a seam that moves namespace, and two new
refusal cases.

**Affected:** every `IDataSource` implementation (a class or an object expression), code that names
`ToolUp.DataSources.Common.IWatchableFileSource` or `WatchedObject` fully qualified, code that type-probes a
connector for `IWatchableFileSource` or `IEmitsNativePayload`, and an exhaustive `match` over
`DeclaredDeliveryRefusal`. A deployment that only composes the shipped connectors and calls `seed` /
`withDeclaredDataSources` changes nothing: every shipped connector declares what its probes answered before.

## What changes

1. **`IDataSource.Capabilities: ConnectorCapabilities`.** The record is `{ Watchable: IWatchableFileSource option;
   Native: IEmitsNativePayload option }`, with `ConnectorCapabilities.none`. Consumers read it through
   `ConnectorCapabilities.watchable` / `native` / `require`, which refuse by name (`CapabilityNotDeclared`) rather
   than probe the connector's type. A decorator forwards its inner connector's record, so a wrapped watchable
   connector stays watchable — under the probe it read as "not watchable".
2. **`IWatchableFileSource` and `WatchedObject` move to `ToolUp.Platform`** (in `IDataSource.fs`, beside the record
   that names them). `IEmitsNativePayload` keeps its namespace and moves file only.
3. **A declared delivery is a value.** `DeclaredDelivery.observe` reads (resolutions, watched-location reads, the
   ledger, live jobs, current configs); `DeclaredDelivery.plan declared observed` returns a `DeliveryPlan` — `Seed`,
   `Register`, `Restamp`, `Cancel`, `Retire`, `LedgerBefore` / `LedgerAfter` — or every refusal, with no effect;
   `DeclaredDelivery.execute` performs it in that order and is the one effectful step. `seed` is the three in order.
4. **A refusal the plan can see writes nothing.** A kept job that carries no scope on a scheduler that cannot
   re-issue one is now `UnscopedJob(sourceId, jobKey)`, refused before any source is seeded (it used to surface as
   `SeedFailed` after the other sources' jobs were scheduled). `ObservationFailed` names a failed read of the live
   jobs or configs.

## What to do

- Run the codemod: `dotnet run --project Build.fsproj -- Codemod <your source dir>`. Rule
  `1095-capabilities-member` adds, after each `interface IDataSource with` / `{ new IDataSource with`:

  ```fsharp skip=fragment
  member _.Capabilities = ConnectorCapabilities.none
  ```

  or, where the same type or object expression implements a capability interface, the record naming it:

  ```fsharp skip=fragment
  member this.Capabilities = { ConnectorCapabilities.none with Watchable = Some(this :> IWatchableFileSource) }
  ```

  Rule `1095-watch-seam-namespace` rewrites a fully-qualified `ToolUp.DataSources.Common.IWatchableFileSource` /
  `WatchedObject` to `ToolUp.Platform.…`. Review rule `1095-capability-probe` lists every `:? IWatchableFileSource`
  / `:? IEmitsNativePayload`: replace it with `ConnectorCapabilities.watchable source` / `native source`.
- A decorator you wrote forwards the record: `member _.Capabilities = inner.Capabilities`.
- An exhaustive `match` over `DeclaredDeliveryRefusal` adds the `UnscopedJob` and `ObservationFailed` arms
  (`DeclaredDeliveryRefusal.describe` already renders both).
- Construct a record from `ConnectorCapabilities.none` with `with`, never as a full literal, so a capability a later
  version adds reads as "not declared" in your connector without an edit.

## Verify

Build. Bind your connector to `IDataSourceContract.tests` (or `IDataSourceContract.capabilities name expected
factory` for a connector the seeded pack does not fit): the capability laws check that what the record declares is
what the readers return, that an undeclared capability is refused naming your `Kind`, and that a forwarding decorator
declares exactly your connector's capabilities. In this repository, `ToolUp.DataSources.Tests` → "Connector
declarations" → "ConnectorCapabilities bindings" pins every shipped connector, and "Phase 1095 — the delivery is a
plan" pins the plan laws.

## Rollback

Pin the previous package version and revert the added members. No stored data changes shape: the ledger, the job
tags and the idempotency keys (`declared-data-delivery-{sourceId}-{jobKey}@{cadence}`) are unchanged.
