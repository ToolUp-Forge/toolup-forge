# Migration — Phase 583 module-graph composition rules

**Status:** four net-new composition well-formedness rules, plus one new optional field on `ServerConfig` and on `ClientConfig`. Three of the four rules can now **fail a preflight that previously passed** — see "What newly fails" below; the fourth is dormant until you declare it. No consumer action is required to upgrade a composition that is already well-formed.

## Why

The Phase 281 composition preflight checked composed *identities* (duplicate `ComponentId`, companion-slot legality) and one *reference* edge (a tool's `SourceModule`). It checked nothing about the **module graph** — which is where a specific class of defect lives, and lives *invisibly*, because the manifest the rules read is keyed by `ComponentId` and that keying **collapses** exactly the collisions in question. Two modules registering a data type under the same wire `TypeName` resolve to one `ComponentId`, so the manifest projector emits one entry (it even `List.distinct`s them) and `duplicate-component-id` has nothing to flag.

The four rules read the **pre-collapse registration edges** instead, supplied on the new `CompositionReferences.ModuleGraph`. All four are exported through the Phase 294 rule manifest (and Phase 585's classified manifest, and Phase 597's versioned/stamped manifest), so an external pre-build checker consumes them without re-encoding.

## The rules

| Code | Severity | Class |
|---|---|---|
| `duplicate-query-handler-key` | error | structural |
| `duplicate-datatype-typename` | error | structural |
| `unsatisfied-needs-data` | **warning** | structural |
| `client-server-module-parity` | error (dormant undeclared) | structural |

All four are declared in `CompositionValidator.structuralRules`, so they run even under `ServerConfig.SkipPreflight` (Phase 585) — they are pure in-memory sweeps over lists already in hand, with no socket and no external dependency, so an emergency boot loses nothing by running them.

### `duplicate-query-handler-key`

Two collisions, one code:

* the same `(module Name, QueryKey)` registered more than once — the bus registry is `Map<module, Map<queryKey, handler>>`, so all but one handler is shadowed;
* two registry-distinct composed modules **sharing a registration `Name`** while that name owns query handlers — their bus namespaces merge, so a query addressed to one module can be answered by the other's handler.

The first case is *already* fatal at compose time via `ModuleQueryRegistry.build`'s `failwith`. Declaring it here is not redundant: that rejection is reachable only by composing the app, whereas the rule manifest lets an external pre-build checker reach the same conclusion without running a composition. The second case is not reachable there at all — by the time `build` groups by module name, the two modules' handlers are already one bucket.

### `duplicate-datatype-typename`

Two data-type registrations sharing a `DataType.Id`. **This is the rule that changes behaviour for existing compositions.** A `DataType.Id` *is* the wire `TypeName` — `DataType.Process` stamps it onto every `ProcessedData` it emits — and nothing in the registry rejected a collision: registrations accumulate flatly, and the manifest collapses them. So a composition that has carried a live duplicate `TypeName` (order-dependent `Detect`, an unknowable producer for the emitted payload, ambiguous vectorisation dispatch) has been booting silently, and **now surfaces it at preflight**, naming both declaring modules and enumerating every registered `TypeName`.

### `unsatisfied-needs-data`

A data-type need **declared by name** that no composed module registers. Warning, not error, for two reasons: the shape is legitimate while a producing module is staged behind a consuming one, and the need is only *partly* enumerable.

That partiality is deliberate and is the honest scope of the rule. The client-side `ErasedModule.NeedsData` is `(DataTypeId -> bool) -> bool` — a *predicate* over the available ids, not a declared set — so the ids it would accept cannot be listed without evaluating it against a candidate universe. (Phase 581's `ModuleSurface` reports exactly this on its own `Opaque` surface, as `client:NeedsData`.) A rule cannot check a need it cannot enumerate, so the rule checks the needs that *are* declared by name, and says so in its description; a rule that sees only part of the picture must not be able to block a boot on the part it sees. Today the enumerable source is `ServerModule.VectorisationHandlers`, whose `DataTypeId` must match a registered data type for the handler to ever fire. A second name-declaring registration field joins by appending to `CompositionReferences.ModuleGraph.DataNeeds` — no rule change.

### `client-server-module-parity`

Dormant unless you declare it. See below.

## New optional fields

```fsharp
// ServerConfig (ToolUp.Platform.Core) and ClientConfig (ToolUp.Platform.Client)
ExpectedModules: string list option   // default None
```

`None` — the default — leaves the rule dormant: the server rule evaluates one `match` and yields nothing, and the client validator returns `Ok` after one `match`. An existing deployment is byte-for-byte unchanged (GP 11 / GP 13).

Declare the **same list on both roots** to turn on the check:

```fsharp
// Server composition root
let serverConfig =
    { ServerConfig.defaults with
        ExpectedModules = Some [ "SkuAnalysis"; "Forecasting" ] }

// Client composition root
let clientConfig =
    { ClientConfig.create handlers with
        ExpectedModules = Some [ "SkuAnalysis"; "Forecasting" ] }
```

**How one declared list makes two roots agree.** The composition validator runs server-side and can only see the server's composed modules; the client root's list is a separate compilation unit the Server tier does not (and must not) reference. So parity is transitive: both roots are measured against the same declared list — `CompositionValidator`'s `client-server-module-parity` rule at server preflight, `ModuleParityValidator` at client boot — and two sets equal to the same set are equal to each other.

**The entries are module IDS, not display names.** The cross-tier identity law on `ModuleIdentity.componentIdOf` states that the server's `ServerModule.Name` and the client's `ModuleDefinition.Id` are one token. A client module registered as `Name = "Sku Analysis"` has id `SkuAnalysis` (spaces stripped) unless it chains `ClientModule.withId`.

**Which client module list.** The consumer's own input to `Client.run` / `program`, before `prepareModules` injects SDK built-ins — the same list `ModuleGroupingValidator` is given, and for the same reason: the built-ins' presence is decided by `ClientConfig`'s `No*` switches, not by your module list, and they have no server-side `ServerModule` counterpart to be at parity with.

**Why `ServerConfig.ModuleNames` is not reused** even though it is also a hand-declared module list: it means something else — the RBAC-visible set the permission system reports and filters on — and deployments legitimately declare a subset of what they compose. Promoting it to a parity assertion would fail compositions that are correct today.

`Some []` is a real declaration ("this root composes no modules"), distinct from `None`, and is checked like any other.

## What newly fails

| You have | You now see |
|---|---|
| Two modules registering a `DataType` with the same `Id` | **Preflight error** `duplicate-datatype-typename` naming both modules |
| Two modules composed under the same `Name`, at least one with query handlers | **Preflight error** `duplicate-query-handler-key` |
| A `VectorisationHandler` whose `DataTypeId` no module registers | **Preflight warning** `unsatisfied-needs-data` (boot continues) |
| Nothing of the above | Unchanged |

The fix in each case is named in the defect message, which enumerates the alternatives (registered `TypeName`s / bus keys / modules). If a duplicate `TypeName` is genuinely intended, register the data type once and share it, rather than registering it twice.

## Breaking-change note (record constructors)

Three shipped records grew a field, so their generated constructors changed and the public-API baselines were regenerated deliberately:

* `ServerConfig` — `ExpectedModules`
* `ClientConfig` — `ExpectedModules`
* `CompositionReferences` — `ModuleGraph : ModuleGraphReferences`

Copy-and-update construction (`{ ServerConfig.defaults with … }`, `{ ClientConfig.create handlers with … }`, `{ CompositionReferences.empty with … }`) — the documented and universal shape — is unaffected. Code that builds any of these three by **positional constructor** must add the new argument; for `CompositionReferences` the new value is `ModuleGraphReferences.empty` (which makes all four rules no-ops), and for the two configs it is `None`.

Server composition roots need no change at all: `ServerApp.compositionReferences` (new, shared with `CompositionDryRun`) derives the whole reference set — including the module-graph edges — from the live registry.

## Verification

* `dotnet build ToolUp.Forge.sln` clean.
* `dotnet run --project Build.fsproj -- VerifyAll` — all packs green.
* New coverage: `src/ToolUp.Platform.Tests/InProcess/ModuleGraphRuleTests.fs` (each rule fires on a synthetic bad composition; every rule silent on a well-formed reference composition; the parity rule dormant undeclared; the client validator agrees with the server rule on the same declared list) and four new fixtures in `InvariantRuleManifestTests.fs` (the manifest-code ⇔ runtime `checkWith` bijection).
* Baselines regenerated in the same commit: `composition-baselines/rule-manifest-baseline.json` (the four new rules, each seeded at version `1.0.0`) and the three `api-baselines/*.approved.txt` above.

## Rollback

Every piece is additive. Drop the four rule records from `CompositionValidator.structuralRules` to disable the checks while leaving the reference plumbing in place; remove `ExpectedModules` from both configs and the `ModuleParityValidator` call in `Client.boot` to remove the parity seam. Regenerate the rule-manifest and API baselines after either.
