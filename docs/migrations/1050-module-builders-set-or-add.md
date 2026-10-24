# Phase 1050 — a module builder says whether it sets or adds

**What changes for you: nothing, unless you hand-append today.** No existing builder changed behaviour. Six
builders were added beside the ones that REPLACE a module's list, so a call site can say which it means:

| Replaces the list (unchanged) | Adds to it (new) |
|---|---|
| `ServerModule.withDataTypes` | `ServerModule.addDataTypes` |
| `ServerModule.withVectorisation` | `ServerModule.addVectorisation` |
| `ClientModule.withDataTypes` | `ClientModule.addDataTypes` |
| `ClientModule.withQueryHandlers` | `ClientModule.addQueryHandlers` |
| `ClientModule.withFeatureFlags` | `ClientModule.addFeatureFlags` |
| `ClientModule.withQueryTargets` | `ClientModule.addQueryTargets` |

**Why.** The `with` prefix meant two things. `ServerModule.withHandlers` appends, while `withDataTypes` two
lines away replaces, so a module whose types are registered from two places (a generated module plus an
authored addition) silently lost the first set. Nothing failed at build time; the module lost data types at
runtime. Every list builder's doc comment on `ServerModule`, `ClientModule` and `ServerApp` now says
**Sets** or **Adds**.

**Migrate** — replace a hand-written append with the builder:

```diff
 ServerModule.create "Trading"
 |> ServerModule.withDataTypes generatedTypes
-|> fun m -> { m with DataTypes = m.DataTypes @ [ authoredType ] }
+|> ServerModule.addDataTypes [ authoredType ]
```

The same shape applies to the client tier (`ClientModule.addDataTypes`) and to the other four pairs.

**Two orderings to check while you are there:**

- `ClientModule.withQueryHandlers` replaces the client handler list, **including handlers `withQueryContract`
  added**. A module that calls `withQueryContract` and then `withQueryHandlers` has lost its contract handlers;
  use `addQueryHandlers`. (The server twin, `ServerModule.withQueryHandlers`, appends.)
- `ServerApp.withConfig` and `ServerApp.withExtensions` set a whole record that other builders append into
  (`withAlertRule`, `withNotificationCategory`, `withPeerRoutePrefix`; `withPreMiddleware`,
  `withCspContributor`, `withDeliveryCompleted`, …). Call them first, before the builders that add to them.

**Verify.** Register a type from each source and check the module carries both — on the server,
`(ServerModule.create "M" |> ServerModule.withDataTypes [ a ] |> ServerModule.addDataTypes [ b ]).DataTypes`
lists `a` then `b`. In a running deployment both types appear in the data manager.

**Rollback.** Go back to the hand-written append. The new builders are additive surface only; nothing else
changed.
