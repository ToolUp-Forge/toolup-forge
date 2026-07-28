# Migration — Phase 431 event-topology manifest (`EventTopology`)

**Status:** net-new, opt-in, purely additive. No existing type, function, or default changed. A deployment that does not call anything below composes byte-for-byte what it did before and pays nothing at runtime (GP 11 / GP 13). **No consumer action is required to upgrade.**

## Why

Modules communicate by events, never by imports (GP 10) — and until now the resulting pub/sub graph was the one part of a composition nobody could look at. `CompositionManifest` (Phase 280) says *what* was composed; nothing said *who talks to whom*. Two failures follow directly from that blind spot and both are silent:

* a **dead topic** — something is published and consumed by nobody, usually because the subscriber was renamed or never composed;
* an **orphan subscription** — something is listened for that no composed component emits, so the subscriber simply never fires.

`EventTopology` derives that graph from registrations the SDK already has, and adds the two queries plus a preflight rule and a CI gate over them.

## What it derives from (nothing is hand-declared)

| Seam | Direction | Read from | Attributed to |
|---|---|---|---|
| `IEventStore` job trigger | subscribe | `ServerModule.JobHandlers` entry whose `Trigger` is `OnEvent <eventType>` | the module's explicit `ComponentId`, else its `Name`-derived one |
| notification-channel module action | emit | `AIToolDefinition.EmitsActions` | `ComponentId.forTool <tool name>` |
| the same declaration | subscribe | `ActionDeclaration.ModuleId` — **only when that module is composed** | the target module's `ComponentId` |
| `IEventStore` write (opt-in, runtime) | emit | `ModuleEvent.SourceModule` × `EventType`, via `ObservingEventStore` | `EventTopology.moduleAttribution` (overridable) |

A module that adds an `OnEvent` declaration, a tool that adds an `EmitsActions` entry, or a module that starts writing a new event type all surface in the topology with **no change to `EventTopology.fs`**.

Kinds are namespaced by transport — `event:OrderPlaced`, `action:Inventory/reserve-stock` — so an action key that happens to match an event type cannot fabricate an edge between components that never speak.

## Adopting it

Nothing is required. To use it:

```fsharp
// 1. Derive the compose-time half from the modules you compose.
let topology = EventTopology.ofModules [ orders; inventory ]

// 2. (Optional) observe the emit half off the live event store.
let observer = EventTopologyObserver()
// wrap whatever IEventStore the deployment composes:
let store = ObservingEventStore(innerStore, observer) :> IEventStore
// later, e.g. from a diagnostics endpoint:
let live = EventTopology.merge topology (observer.Snapshot())

// 3. Ask the questions.
EventTopology.deadTopics live            // emitted, consumed by nobody
EventTopology.orphanSubscriptions live   // subscribed, emitted by nobody
EventTopology.edges live                 // component-to-component adjacency
```

A deployment whose modules declare explicit `ComponentId`s passes its own attribution so a renamed module still resolves to its declared identity:

```fsharp
ObservingEventStore(
    innerStore,
    observer,
    fun source ->
        ServerApp.componentIdForModule source app
        |> Option.defaultValue EventTopology.platformComponent)
```

### Preflight rule (opt-in)

```fsharp
// Default: an orphan subscription is a Warning.
let register = EventTopologyPreflight.serviceRegistrationWithDefaults topology

// Or make it fatal:
let register = EventTopologyPreflight.serviceRegistration DefectError topology
```

Fold the returned `IServiceCollection -> IServiceCollection` closure into your composition root's extension `ServiceConfig` hook, exactly as you would `CompositionValidator.serviceRegistration` or `ComponentRequirementsPreflight.serviceRegistration`. **An empty topology registers nothing at all**, so the base case adds no service descriptor.

## Design notes worth knowing before you extend it

**It is not a `CompositionValidator` rule, deliberately.** A Phase 281 `CompositionRule` is `CompositionManifest -> CompositionReferences -> string list`, and the topology is reachable from neither — it comes from job triggers, tool action declarations, and observed writes. Putting it there would mean growing `CompositionReferences`, and growing a shipped F# record changes its constructor signature: a break for every consumer that constructs one, and a removal under the public-API baseline gate. So it follows the opt-in `serviceRegistration` closure pattern Phase 432 settled on. The rules are still declared data and are still exported in the Phase 294 shape — `EventTopologyPreflight.ruleManifest` (`CompositionRuleDescriptor list`) and `classifiedRuleManifest` (`ClassifiedCompositionRule list`) — so an external pre-build checker reads one vocabulary for both rule families.

**Both rules are structural-class (Phase 585).** They are pure in-memory sweeps over sets already derived: no socket, no dependency, microseconds. `ServerConfig.SkipPreflight` therefore does not bypass them. At the default `DefectWarning` severity the orphan rule cannot block a boot at all; an operator who raises it to `DefectError` has deliberately made a well-formed messaging graph a boot gate, which is exactly the class of invariant an emergency boot must not silently switch off.

**The rule is evidence-gated; the query is not.** `EventTopologyPreflight.defects` reports an orphan only on a transport the topology has actually observed an emission on. A composition that derives only its compose-time half has no `event:` emitter evidence at all, so every `OnEvent` subscription would otherwise read as an orphan — an artefact of what was measured, not a defect in what was composed. Absence of evidence on a transport is *unknown*, not *empty*. `EventTopology.orphanSubscriptions` remains the literal, unfiltered reading for a caller that wants it.

**The diff is a sidecar, not a widened `CompositionDelta`.** `EventTopologyDelta` + `EventTopology.diff` / `isEmptyDelta` / `renderDelta` mirror the Phase 286 shapes rather than growing them, for the same constructor-break reason recorded on `ClassifiedCompositionRule` and `SlotRequirementSet`. The two deltas join on the `ComponentId`s they both already key against.

## CI gate

The Phase 287 golden-file gate gains a second, sibling baseline: `composition-baselines/event-topology-baseline.json`, holding the reference composition's topology in its plain-string wire projection (`EventTopology.toWire`, so the golden file never depends on `Set` / single-case-union serialisation). A new edge, a removed subscriber, or a dropped emitter fails CI with a rendered `EventTopology.renderDelta`.

Both baselines are approved by the same flag, so accepting a composition change accepts its topology consequence in the same act:

```powershell
$env:TOOLUP_APPROVE_COMPOSITION = "1"
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
$env:TOOLUP_APPROVE_COMPOSITION = $null
```

## Rollback

Delete the call sites. There is nothing to unwind: no decorator is installed unless you install it, no validator is registered unless you register it, and no manifest is built until a caller asks. Removing `EventTopology.ofModules` from a composition root restores the previous composition exactly.

## Known non-derivable halves

Recorded honestly rather than guessed at, in the `ModuleSurface` tradition:

* **Client-side `ClientModule.withEventSubscription` topics** are declared (`ErasedModule.EventSubscriptions` keys) but their *publishers* are ordinary runtime calls on the client event bus with no registration to read — so including the subscriptions alone would report every one of them as an orphan. Left out until a publication registration exists.
* **Webhook subscriptions** (`WebhookSubscription.EventTypes`) are per-scope persisted runtime data, not a compose-time registration, so they are not part of the derived topology. They are one reason a dead topic can be legitimate: the consumer may be outside the process entirely.
