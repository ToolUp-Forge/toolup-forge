module ToolUp.Platform.Tests.InProcess.EventTopologyTests

open System
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 431 — event-topology manifest ──────────────────────────────
//
// Covers the acceptance shape: a two-module in-memory composition yields
// the expected per-ComponentId emit / subscribe sets, derived from the
// live registrations (an `OnEvent` job trigger, a tool's `EmitsActions`
// declaration, and observed `IEventStore` writes) with nothing
// hand-listed; a dead topic and an orphan subscription are each detected;
// the preflight rule fires at the configured severity; and a composition
// that never reads the topology registers nothing and is byte-for-byte
// unchanged (GP 11 / GP 13).
//
// The "derived, not hand-declared" property is asserted directly: the
// same derivation function, over a module that grew one more `OnEvent`
// declaration, yields one more subscription — no code in
// `EventTopology.fs` names any of these event types.

// ── fixtures ──────────────────────────────────────────────────────────

/// A job handler that is never dispatched — the topology reads the
/// declaration's `Trigger`, never the handler.
type private StubJobHandler() =
    interface IJobHandler with
        member _.Execute(_) = async { return JobResult.Success }

let private stubTool
    (name: string)
    (sourceModule: string)
    (emits: ActionDeclaration list option)
    : AIToolDefinition * (HttpContext -> string -> Async<string>) =
    {
        Name = name
        Description = ""
        Parameters = []
        SourceModule = sourceModule
        EmitsActions = emits
        Location = ServerResident
        Surface = Both
    },
    (fun _ _ -> async { return "" })

let private action (moduleId: string) (actionKey: string) : ActionDeclaration = {
    ModuleId = moduleId
    ActionKey = actionKey
    Description = ""
    PayloadSchema = None
}

let private ordersId = ComponentId.ofModule "orders-service"
let private inventoryId = ComponentId.ofModule "inventory-service"

/// Two composed modules:
///   * `Orders` exposes a tool declaring a client action aimed at
///     `Inventory` (an emission whose subscriber IS composed), and one
///     aimed at `Reporting` (which is not composed — a dead topic).
///   * `Inventory` declares an `OnEvent` job on `OrderPlaced` (a
///     subscription) and another on `NeverEmitted` (an orphan, once the
///     topology has emitter evidence on the `event:` transport).
let private ordersModule =
    ServerModule.create "Orders"
    |> ServerModule.withComponentId "orders-service"
    |> ServerModule.withAITools [
        stubTool "orders.run" "Orders" (Some [ action "Inventory" "reserve-stock"; action "Reporting" "refresh" ])
    ]

let private inventoryModule =
    ServerModule.create "Inventory"
    |> ServerModule.withComponentId "inventory-service"
    |> ServerModule.withJobHandler ("inventory.on-order", StubJobHandler(), OnEvent "OrderPlaced")
    |> ServerModule.withJobHandler ("inventory.on-ghost", StubJobHandler(), OnEvent "NeverEmitted")

let private composedModules = [ ordersModule; inventoryModule ]

let private entryFor (componentId: ComponentId) (topology: EventTopology) =
    topology.Participants |> List.tryFind (fun e -> e.Component = componentId)

let private kindStrings (kinds: Set<EventKind>) =
    kinds |> Set.toList |> List.map EventKind.value |> List.sort

let private moduleEvent (sourceModule: string) (eventType: string) : ModuleEvent = {
    Id = Guid.NewGuid()
    OccurredAt = DateTime.UtcNow
    ScopeId = "team-1"
    SourceModule = sourceModule
    EventType = eventType
    Payload = "{}"
}

// ── 431.A — derivation from the live registrations ────────────────────

let private derivation =
    testList "derivation" [

        test "a two-module composition yields the expected emit / subscribe sets" {
            let topology = EventTopology.ofModules composedModules

            let inventory =
                entryFor inventoryId topology
                |> Option.defaultWith (fun () -> failtest "the Inventory module must participate in the topology")

            Expect.equal
                (kindStrings inventory.SubscribesTo)
                [ "action:Inventory/reserve-stock"; "event:NeverEmitted"; "event:OrderPlaced" ]
                "Inventory subscribes to both OnEvent triggers plus the action aimed at it"

            Expect.isEmpty inventory.Emits "Inventory declares no emission"

            let tool =
                entryFor (ComponentId.forTool "orders.run") topology
                |> Option.defaultWith (fun () -> failtest "the tool must participate as the emitter of its actions")

            Expect.equal
                (kindStrings tool.Emits)
                [ "action:Inventory/reserve-stock"; "action:Reporting/refresh" ]
                "the tool emits exactly the actions it declares"
        }

        test "observed IEventStore writes attribute their emission by SourceModule" {
            let observer = EventTopologyObserver()

            let store =
                ObservingEventStore(InMemoryEventStore.InMemoryEventStore(), observer) :> IEventStore

            store.Write(moduleEvent "Orders" "OrderPlaced") |> Async.RunSynchronously
            store.Write(moduleEvent "Orders" "OrderPlaced") |> Async.RunSynchronously

            store.Write(moduleEvent "_platform" "HealthStateChanged")
            |> Async.RunSynchronously

            let observed = observer.Snapshot()

            let orders =
                entryFor (ComponentId.ofModule "Orders") observed
                |> Option.defaultWith (fun () -> failtest "the writing module must be attributed")

            Expect.equal
                (kindStrings orders.Emits)
                [ "event:OrderPlaced" ]
                "a repeated write records one emission — the topology asks whether, not how often"

            let platform =
                entryFor EventTopology.platformComponent observed
                |> Option.defaultWith (fun () -> failtest "a reserved source attributes to the platform component")

            Expect.equal
                (kindStrings platform.Emits)
                [ "event:HealthStateChanged" ]
                "a '_'-prefixed reserved source is attributed to _platform, not to a consumer module"
        }

        test "reads pass through the observing decorator untouched" {
            let inner = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = ObservingEventStore(inner, EventTopologyObserver()) :> IEventStore
            let evt = moduleEvent "Orders" "OrderPlaced"

            store.Write evt |> Async.RunSynchronously

            let read = store.ReadByType("team-1", "OrderPlaced") |> Async.RunSynchronously
            Expect.equal (read |> List.map _.Id) [ evt.Id ] "the decorator returns the inner store's reads verbatim"

            let scopes = store.ListScopes() |> Async.RunSynchronously
            Expect.equal scopes [ "team-1" ] "ListScopes passes through"
        }

        test "a new subscription surfaces with no EventTopology change" {
            let before = EventTopology.ofModules composedModules

            let grown =
                inventoryModule
                |> ServerModule.withJobHandler ("inventory.on-restock", StubJobHandler(), OnEvent "StockReplenished")

            let after = EventTopology.ofModules [ ordersModule; grown ]

            let delta = EventTopology.diff before after

            Expect.equal
                (delta.SubscriptionsAdded |> List.map (snd >> EventKind.value))
                [ "event:StockReplenished" ]
                "adding one OnEvent declaration adds exactly one subscription — nothing in EventTopology.fs names it"

            Expect.isEmpty delta.EmissionsAdded "the growth is a subscription, not an emission"
        }

        test "an action aimed at an uncomposed module registers no subscriber" {
            let topology = EventTopology.ofModules composedModules
            let reporting = EventKind.forModuleAction "Reporting" "refresh"

            Expect.isEmpty
                (EventTopology.subscribersOf reporting topology)
                "an action targeting a module this deployment does not compose reaches nobody"
        }

        test "derivation is order-independent and idempotent under merge" {
            let a = EventTopology.ofModules composedModules
            let b = EventTopology.ofModules (List.rev composedModules)

            Expect.isTrue
                (EventTopology.isEmptyDelta (EventTopology.diff a b))
                "registration order does not change the topology"

            Expect.isTrue
                (EventTopology.isEmptyDelta (EventTopology.diff a (EventTopology.merge a a)))
                "merging a topology with itself is a no-op"
        }
    ]

// ── 431.B — the topology queries ──────────────────────────────────────

/// The compose-time derivation plus emitter evidence on the `event:`
/// transport — the shape a deployment that composes the observing store
/// actually has.
let private observedTopology =
    EventTopology.merge
        (EventTopology.ofModules composedModules)
        (EventTopology.ofObservedEmissions [ ordersId, EventKind.forEvent "OrderPlaced" ])

let private queries =
    testList "queries" [

        test "a dead topic is detected" {
            let dead =
                EventTopology.deadTopics observedTopology
                |> List.map (fun d -> EventKind.value d.Kind)

            Expect.contains dead "action:Reporting/refresh" "an action nothing consumes is a dead topic"

            Expect.isFalse
                (List.contains "action:Inventory/reserve-stock" dead)
                "an action a composed module receives is live"

            Expect.isFalse (List.contains "event:OrderPlaced" dead) "an emitted-and-subscribed event is live"
        }

        test "a dead topic names its emitters" {
            let topic =
                EventTopology.deadTopics observedTopology
                |> List.find (fun d -> EventKind.value d.Kind = "action:Reporting/refresh")

            Expect.equal
                (topic.EmittedBy |> List.map ComponentId.value)
                [ "tool:orders.run" ]
                "the dead topic names the tool that publishes it"
        }

        test "an orphan subscription is detected" {
            let orphans =
                EventTopology.orphanSubscriptions observedTopology
                |> List.map (fun o -> EventKind.value o.Kind)

            Expect.contains orphans "event:NeverEmitted" "a subscription no composed component feeds is an orphan"
            Expect.isFalse (List.contains "event:OrderPlaced" orphans) "a fed subscription is not an orphan"
        }

        test "an orphan subscription names its subscribers" {
            let orphan =
                EventTopology.orphanSubscriptions observedTopology
                |> List.find (fun o -> EventKind.value o.Kind = "event:NeverEmitted")

            Expect.equal
                (orphan.SubscribedBy |> List.map ComponentId.value)
                [ "module:inventory-service" ]
                "the orphan names the module that will never fire"
        }

        test "edges give the component-to-component adjacency" {
            let rendered =
                EventTopology.edges observedTopology
                |> List.map (fun e ->
                    sprintf
                        "%s -> %s (%s)"
                        (ComponentId.value e.From)
                        (ComponentId.value e.To)
                        (EventKind.value e.Kind))

            Expect.equal
                rendered
                [
                    "module:orders-service -> module:inventory-service (event:OrderPlaced)"
                    "tool:orders.run -> module:inventory-service (action:Inventory/reserve-stock)"
                ]
                "one edge per (emitter, subscriber, kind), deterministically ordered"
        }

        test "an empty topology answers every query emptily" {
            Expect.isEmpty (EventTopology.deadTopics EventTopology.empty) "no topics"
            Expect.isEmpty (EventTopology.orphanSubscriptions EventTopology.empty) "no subscriptions"
            Expect.isEmpty (EventTopology.edges EventTopology.empty) "no edges"
        }
    ]

// ── 431.C — the preflight rule ────────────────────────────────────────

let private preflight =
    testList "preflight" [

        test "an orphan subscription warns by default and names the component" {
            let validator =
                EventTopologyPreflight.EventTopologyValidator(
                    observedTopology,
                    EventTopologyPreflight.defaultOrphanSeverity
                )
                :> IConfigValidator

            match validator.Validate() |> Async.RunSynchronously with
            | Warning message ->
                Expect.stringContains message "event:NeverEmitted" "the warning names the orphaned kind"
                Expect.stringContains message "module:inventory-service" "the warning names the subscriber"

                Expect.stringContains
                    message
                    EventTopologyPreflight.OrphanSubscriptionRule
                    "the warning is tagged with its rule code"
            | other -> failtestf "expected a Warning, got %A" other
        }

        test "the orphan severity is configurable to Error" {
            let defects = EventTopologyPreflight.defects DefectError observedTopology

            let orphanSeverities =
                defects
                |> List.filter (fun d -> d.RuleCode = EventTopologyPreflight.OrphanSubscriptionRule)
                |> List.map _.Severity

            Expect.equal orphanSeverities [ DefectError ] "the configured severity is what the rule reports"

            match EventTopologyPreflight.toValidationResult defects with
            | Error message ->
                Expect.stringContains message "event:NeverEmitted" "an Error aborts startup naming the kind"
            | other -> failtestf "expected an Error at DefectError severity, got %A" other
        }

        test "an orphan is not reported on a transport with no emitter evidence" {
            // The compose-time-only derivation has no `event:` emitter at
            // all, so `event:NeverEmitted` is unknown, not orphaned.
            let composeTimeOnly = EventTopology.ofModules composedModules

            let orphanCodes =
                EventTopologyPreflight.defects DefectError composeTimeOnly
                |> List.filter (fun d -> d.RuleCode = EventTopologyPreflight.OrphanSubscriptionRule)

            Expect.isEmpty orphanCodes "absence of evidence on a transport is unknown, not empty"

            Expect.isNonEmpty
                (EventTopology.orphanSubscriptions composeTimeOnly)
                "the literal query still reports them — only the rule is evidence-gated"
        }

        test "a well-formed topology yields Ok" {
            let clean =
                EventTopology.empty
                |> EventTopology.withEmission ordersId (EventKind.forEvent "OrderPlaced")
                |> EventTopology.withSubscription inventoryId (EventKind.forEvent "OrderPlaced")

            Expect.equal
                (EventTopologyPreflight.toValidationResult (EventTopologyPreflight.defects DefectWarning clean))
                Ok
                "every emission consumed and every subscription fed is a clean topology"
        }

        test "the rule manifest and its classification agree" {
            let codes = EventTopologyPreflight.ruleManifest |> List.map _.Code

            Expect.equal
                codes
                [
                    EventTopologyPreflight.OrphanSubscriptionRule
                    EventTopologyPreflight.DeadTopicRule
                ]
                "both shipped rules are exported for an external pre-build checker"

            Expect.equal
                (EventTopologyPreflight.classifiedRuleManifest |> List.map _.Code)
                codes
                "the classified projection covers the same rules in the same order"

            Expect.all
                EventTopologyPreflight.classifiedRuleManifest
                (fun r -> r.Class = StructuralRule)
                "both rules are pure in-memory sweeps, so both are structural-class"
        }

        test "an empty topology registers no validator (GP 11 / GP 13)" {
            let services = ServiceCollection() :> IServiceCollection
            let before = services.Count

            let after =
                EventTopologyPreflight.serviceRegistrationWithDefaults EventTopology.empty services

            Expect.equal
                after.Count
                before
                "a deployment that never derives a topology composes a byte-for-byte identical services"
        }

        test "a non-empty topology registers exactly one structural-class validator" {
            let services = ServiceCollection() :> IServiceCollection

            EventTopologyPreflight.serviceRegistrationWithDefaults observedTopology services
            |> ignore

            let registered =
                services
                |> Seq.filter (fun d -> d.ServiceType = typeof<IConfigValidator>)
                |> Seq.map (fun d -> d.ImplementationInstance :?> IConfigValidator)
                |> List.ofSeq

            Expect.equal
                (registered |> List.map _.Name)
                [ EventTopologyPreflight.ValidatorName ]
                "one validator, under its stable name"

            Expect.all
                registered
                (fun v -> (v :> obj) :? IStructuralClassValidator)
                "the topology check is structural-class, so SkipPreflight does not bypass it"
        }
    ]

// ── 431.D — diff + wire projection (the golden-file gate's mechanism) ─

let private diffing =
    testList "diff" [

        test "a topology diffs clean against itself" {
            Expect.isTrue
                (EventTopology.isEmptyDelta (EventTopology.diff observedTopology observedTopology))
                "an identical pair is the empty delta"

            Expect.equal
                (EventTopology.renderDelta EventTopology.emptyDelta)
                "(no event-topology differences)"
                "the empty delta renders to one line"
        }

        test "a removed subscriber surfaces in the delta and the rendering" {
            let after = EventTopology.ofModules [ ordersModule ]
            let delta = EventTopology.diff (EventTopology.ofModules composedModules) after

            Expect.contains
                (delta.ComponentsRemoved |> List.map ComponentId.value)
                "module:inventory-service"
                "dropping the subscribing module removes its component"

            Expect.contains
                (delta.SubscriptionsRemoved |> List.map (snd >> EventKind.value))
                "event:OrderPlaced"
                "its subscriptions are removed with it"

            let rendered = EventTopology.renderDelta delta
            Expect.stringContains rendered "Subscriptions" "the readable failure names the Subscriptions section"
            Expect.stringContains rendered "event:OrderPlaced" "and the specific kind that moved"
        }

        test "the wire projection round-trips losslessly" {
            let back = EventTopology.ofWire (EventTopology.toWire observedTopology)

            Expect.isTrue
                (EventTopology.isEmptyDelta (EventTopology.diff observedTopology back))
                "toWire -> ofWire preserves the topology structurally"
        }

        test "the wire projection is deterministic" {
            let once = EventTopology.toWire observedTopology

            let twice =
                EventTopology.toWire (EventTopology.merge EventTopology.empty observedTopology)

            Expect.equal twice once "the same topology always projects to the same wire shape"
        }
    ]

// ── EventKind vocabulary ──────────────────────────────────────────────

let private kinds =
    testList "EventKind" [

        test "transports are namespaced so two name spaces cannot collide" {
            let asEvent = EventKind.forEvent "refresh"
            let asAction = EventKind.forModuleAction "Reporting" "refresh"

            Expect.notEqual asEvent asAction "an event type and an action key never compare equal"
            Expect.equal (EventKind.transport asEvent) "event" "the event transport is readable off the kind"
            Expect.equal (EventKind.transport asAction) "action" "so is the action transport"
            Expect.equal (EventKind.topic asAction) "Reporting/refresh" "the topic half excludes the transport"
        }

        test "a blank kind is refused" {
            Expect.throws (fun () -> EventKind.forEvent "  " |> ignore) "an unnamed topic is never valid"

            Expect.throws
                (fun () -> EventKind.forModuleAction "Orders" "" |> ignore)
                "an action nobody can address is not a topic"
        }
    ]

let tests =
    testList "EventTopology" [ derivation; queries; preflight; diffing; kinds ]