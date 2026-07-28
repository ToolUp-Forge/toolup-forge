// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform.ConfigValidation

// ─── Event-topology manifest (EventTopology) ─────────────────────────
//
// GP 10 says modules communicate by events, never by imports — and the
// resulting pub/sub graph is the one part of a composition that has been
// invisible. `CompositionManifest` (Phase 280) enumerates *what* was
// composed; this enumerates *who talks to whom*: per `ComponentId`, the
// event kinds it EMITS and the kinds it SUBSCRIBES to, plus the two
// questions the graph exists to answer — which topic is emitted and
// consumed by nobody (a dead topic), and which subscriber is listening
// for something no composed component emits (an orphan subscription).
//
// **Derived from live registrations, never hand-declared** (the Phase 293
// rule). Three seams, each already present in the SDK, each attributable
// to a `ComponentId` without a new field on any existing record:
//
//   1. **Job triggers → subscriptions.** A module's own
//      `ServerModule.JobHandlers` entry whose `Trigger` is
//      `OnEvent eventType` fires "whenever a `ModuleEvent` with this
//      `EventType` is written to `IEventStore` in the same scope" — i.e.
//      it IS an `IEventStore` subscription, declared at compose time and
//      owned by the declaring module. Attribution is exact: the module's
//      explicit `ComponentId`, or its `Name`-derived one (GP 11).
//   2. **Tool action declarations → emissions (+ their intended
//      subscriber).** `AIToolDefinition.EmitsActions` declares the
//      `(ModuleId, ActionKey)` pairs a tool publishes as a
//      `Notification.ModuleAction` over the notification channel. The
//      emitter is the tool (`ComponentId.forTool`); the subscriber is
//      the named module — but ONLY when that module is actually
//      composed. A declaration aimed at a module this deployment does
//      not compose is precisely the "undeclared decoders drop silently"
//      failure the tool contract admits to, and it surfaces here as a
//      dead topic.
//   3. **`IEventStore` writes → emissions (opt-in, runtime).**
//      `ObservingEventStore` is an `IEventStore` decorator that records
//      `(SourceModule, EventType)` on every successful `Write`. Every
//      `ModuleEvent` already carries its own `SourceModule`, so the
//      attribution is the event's own declaration — a module that starts
//      writing a new event type surfaces in the topology with no change
//      to this file. Reads pass through untouched.
//
// **The additive-attribution rule.** Seam 3 is the registration site
// that could not name a `ComponentId` today, and the attribution arrives
// as a constructor parameter on a NEW type rather than as a field on an
// existing record: adding a field to an F# record changes its
// constructor signature, which is a break for every consumer that
// constructs one (and a removal under the public-API baseline gate) —
// the reason Phase 585 gave for `ClassifiedCompositionRule` and Phase 432
// for `SlotRequirementSet`. `ObservingEventStore`'s default attribution
// (`EventTopology.moduleAttribution`) reproduces the existing
// `ComponentId.ofModule` convention exactly, so a deployment that passes
// nothing gets the historical identity (GP 11).
//
// **Transports are namespaced.** An `IEventStore` event type and a
// module-action key are different transports with independent name
// spaces, so a kind carries its transport (`event:AnalysisCompleted`,
// `action:orders/apply-budget`) the way a `ComponentId` carries its slot.
// Without that, an action key colliding with an event type would fabricate
// an edge between two components that never speak.
//
// **Generic substrate (GP 1); zero cost when unused (GP 11 / GP 13).**
// The shape carries no vendor or domain type — only `ComponentId`s and
// strings drawn from the registrations themselves — and nothing is built
// until a caller asks (`EventTopology.ofModules`). A deployment that
// never introspects composes byte-for-byte what it did before: no
// decorator, no observer, no validator (see
// `EventTopologyPreflight.serviceRegistration`, which registers nothing
// for an empty topology).

/// One event kind on the messaging graph, namespaced by its transport —
/// `event:<EventType>` for an `IEventStore` `ModuleEvent`,
/// `action:<moduleId>/<actionKey>` for a notification-channel module
/// action. The prefix is what keeps two independent name spaces from
/// fabricating an edge between components that never speak.
type EventKind = EventKind of string

/// One component's participation in the event graph: everything it
/// emits and everything it subscribes to, as sets (so a repeated
/// registration is idempotent and ordering is irrelevant).
type EventTopologyEntry = {
    /// The stable Phase 279 identity of the participating component.
    Component: ComponentId
    /// The kinds this component publishes.
    Emits: Set<EventKind>
    /// The kinds this component listens for.
    SubscribesTo: Set<EventKind>
}

/// The derived event topology of a composition: every participating
/// component's emit / subscribe sets, keyed by stable `ComponentId` and
/// held in a deterministic order (sorted by id), so two compositions that
/// registered the same things in a different order produce the same
/// topology.
///
/// The field is `Participants`, not `Components`, deliberately: a
/// single-field record named `Components` already ships in this namespace
/// (`ModuleSbom`), and F#'s last-declared-wins field inference would have
/// silently re-pointed every unannotated `{ Components = … }` in a
/// consumer's source at whichever type compiled later. A distinct field
/// name is a cheaper guarantee than an annotation every caller must
/// remember.
type EventTopology = {
    Participants: EventTopologyEntry list
}

/// One directed component-to-component adjacency edge: `From` emits
/// `Kind` and `To` subscribes to it. A component that both emits and
/// subscribes to the same kind yields a self-edge — real, and reported
/// rather than filtered, because a module reacting to its own emission
/// is a legitimate (and easy-to-miss) shape.
type EventTopologyEdge = {
    From: ComponentId
    To: ComponentId
    Kind: EventKind
}

/// A kind that is emitted but consumed by nobody in this composition.
/// Not automatically a defect — a topic can legitimately be published
/// for an external consumer (a webhook subscriber, an audit archive) —
/// but it is always worth an operator's eye, because the common cause is
/// a subscriber that was renamed or never composed.
type DeadTopic = {
    Kind: EventKind
    /// Every component that publishes it.
    EmittedBy: ComponentId list
}

/// A subscription for a kind no composed component emits. The usual
/// cause is a renamed event type or a producer module that this
/// deployment does not compose — either way the subscriber will never
/// fire, silently.
type OrphanSubscription = {
    Kind: EventKind
    /// Every component listening for it.
    SubscribedBy: ComponentId list
}

/// The structural difference between two topologies, keyed by
/// `(ComponentId, EventKind)` — never by list position, so re-ordering
/// registrations diffs to empty. Feeds the composition golden-file CI
/// gate: a new edge or a dropped subscriber is a reviewable change.
///
/// A separate delta type rather than fields grown onto `CompositionDelta`
/// (Phase 286), for the reason recorded on `ClassifiedCompositionRule`:
/// growing a shipped F# record breaks its constructor. The two deltas
/// join on `ComponentId`, which is the same key both already carry.
type EventTopologyDelta = {
    ComponentsAdded: ComponentId list
    ComponentsRemoved: ComponentId list
    EmissionsAdded: (ComponentId * EventKind) list
    EmissionsRemoved: (ComponentId * EventKind) list
    SubscriptionsAdded: (ComponentId * EventKind) list
    SubscriptionsRemoved: (ComponentId * EventKind) list
}

/// The topology projected to plain strings — the shape a golden file or
/// an external tool persists, so neither depends on the F# union / set
/// representations round-tripping through a serialiser. The kind fields
/// are named apart from `EventTopologyEntry`'s for the same
/// field-inference reason recorded on `EventTopology.Participants`: two
/// records sharing a full field-name set make every unannotated
/// construction ambiguous.
type EventTopologyWireEntry = {
    Component: string
    EmittedKinds: string list
    SubscribedKinds: string list
}

/// Constructors + accessors for `EventKind`. Every kind is namespaced by
/// its transport at construction, so a caller cannot accidentally mint an
/// unprefixed kind that would compare equal across transports.
module EventKind =

    [<Literal>]
    let private EventTransport = "event"

    [<Literal>]
    let private ActionTransport = "action"

    /// The raw, transport-prefixed identity string.
    let value (EventKind v) : string = v

    /// An `IEventStore` `ModuleEvent.EventType` as a kind
    /// (`event:AnalysisCompleted`). Raises on a blank event type — an
    /// unnamed topic is never valid.
    let forEvent (eventType: string) : EventKind =
        if String.IsNullOrWhiteSpace eventType then
            invalidArg "eventType" "An event kind requires a non-empty EventType."

        EventKind(EventTransport + ":" + eventType.Trim())

    /// A notification-channel module action as a kind
    /// (`action:<moduleId>/<actionKey>`). Raises when either half is
    /// blank — an action nobody can address is not a topic.
    let forModuleAction (moduleId: string) (actionKey: string) : EventKind =
        if String.IsNullOrWhiteSpace moduleId then
            invalidArg "moduleId" "A module-action kind requires a non-empty target ModuleId."

        if String.IsNullOrWhiteSpace actionKey then
            invalidArg "actionKey" "A module-action kind requires a non-empty ActionKey."

        EventKind(ActionTransport + ":" + moduleId.Trim() + "/" + actionKey.Trim())

    /// Round-trip a persisted kind string verbatim (trimmed). Used by
    /// the wire projection; prefer `forEvent` / `forModuleAction` when
    /// minting a kind from a registration.
    let create (raw: string) : EventKind =
        if String.IsNullOrWhiteSpace raw then
            invalidArg "raw" "EventKind cannot be null, empty, or whitespace."

        EventKind(raw.Trim())

    /// The transport half (`event` / `action`), or `""` for an
    /// unprefixed kind round-tripped from a foreign source.
    let transport (kind: EventKind) : string =
        let v = value kind

        match v.IndexOf ':' with
        | i when i > 0 -> v.Substring(0, i)
        | _ -> ""

    /// The topic half — the event type, or `<moduleId>/<actionKey>`.
    let topic (kind: EventKind) : string =
        let v = value kind

        match v.IndexOf ':' with
        | i when i >= 0 && i + 1 <= v.Length -> v.Substring(i + 1)
        | _ -> v

/// Derivation, queries, and diff over a composition's event topology.
/// Every function here is pure over already-collected registrations —
/// nothing is built until a caller asks (GP 13).
module EventTopology =

    /// The reserved identity SDK-internal / platform-owned emissions are
    /// attributed to. Mirrors the reserved `_platform` scope the rest of
    /// the SDK already uses for events that belong to no consumer module
    /// (`CompositionValidator`'s reserved-source rule exempts exactly
    /// these names).
    let platformComponent: ComponentId = ComponentId.ofModule "_platform"

    /// The topology of a composition with no event participation at all —
    /// what a deployment that registers no event-driven job and declares
    /// no tool action projects to.
    let empty: EventTopology = { Participants = [] }

    // ── assembly ──────────────────────────────────────────────────────

    let private ordered (entries: EventTopologyEntry list) : EventTopologyEntry list =
        entries
        |> List.sortWith (fun a b -> String.CompareOrdinal(a.Component.Value, b.Component.Value))

    /// Fold a flat list of `(component, kind, isEmission)` participations
    /// into per-component entries. The single assembly point every
    /// derivation funnels through, so all of them share one ordering and
    /// one de-duplication rule.
    let private assemble (participations: (ComponentId * EventKind * bool) list) : EventTopology =
        let entries =
            participations
            |> List.groupBy (fun (componentId, _, _) -> componentId)
            |> List.map (fun (componentId, items) ->
                // Annotated because `EventTopologyWireEntry` declares the
                // same three field NAMES; last-declared would otherwise win.
                {
                    Component = componentId
                    Emits =
                        items
                        |> List.choose (fun (_, kind, isEmission) -> if isEmission then Some kind else None)
                        |> Set.ofList
                    SubscribesTo =
                        items
                        |> List.choose (fun (_, kind, isEmission) -> if isEmission then None else Some kind)
                        |> Set.ofList
                }
                : EventTopologyEntry)

        { Participants = ordered entries }

    /// Flatten a topology back to its participation list — the inverse of
    /// `assemble`, used by `merge` and the diff.
    let private participations (topology: EventTopology) : (ComponentId * EventKind * bool) list = [
        for entry in topology.Participants do
            for kind in entry.Emits -> entry.Component, kind, true
            for kind in entry.SubscribesTo -> entry.Component, kind, false
    ]

    /// Union two topologies. Emit / subscribe sets union per component;
    /// a component present in only one side carries through unchanged.
    /// Associative and commutative (set union under a stable ordering),
    /// so merge order never changes the result.
    let merge (a: EventTopology) (b: EventTopology) : EventTopology =
        assemble (participations a @ participations b)

    /// `merge` over a list — the shape a composition root uses to fold the
    /// compose-time derivation together with an observer's runtime
    /// snapshot.
    let mergeAll (topologies: EventTopology list) : EventTopology =
        topologies |> List.collect participations |> assemble

    /// Record one emission against a component. Additive — everything
    /// already in the topology is preserved.
    let withEmission (componentId: ComponentId) (kind: EventKind) (topology: EventTopology) : EventTopology =
        assemble ((componentId, kind, true) :: participations topology)

    /// Record one subscription against a component.
    let withSubscription (componentId: ComponentId) (kind: EventKind) (topology: EventTopology) : EventTopology =
        assemble ((componentId, kind, false) :: participations topology)

    /// A topology built from observed emissions alone — the shape an
    /// `EventTopologyObserver` snapshot takes before it is merged with the
    /// compose-time derivation.
    let ofObservedEmissions (observed: (ComponentId * EventKind) list) : EventTopology =
        observed
        |> List.map (fun (componentId, kind) -> componentId, kind, true)
        |> assemble

    // ── derivation from the live module registrations (431.A) ─────────

    /// A module's resolved identity: its explicit `ComponentId` when it
    /// declares one, else the `Name`-derived id (GP 11 — byte-for-byte
    /// the identity every other introspection surface already uses).
    let componentIdOf (serverModule: ServerModule) : ComponentId =
        serverModule.ComponentId
        |> Option.defaultValue (ComponentId.ofModule serverModule.Name)

    /// Derive the topology of a set of composed modules from their own
    /// registrations. `attribute` resolves a module to the identity its
    /// participations are recorded under; the default `componentIdOf`
    /// reproduces the existing convention exactly, and it is a parameter
    /// only so a composition root that keys components differently (a
    /// multi-tenant shell, a test harness) can say so without this file
    /// learning about it.
    ///
    /// Nothing here is a hand-listed table: a module that adds an
    /// `OnEvent` job declaration, or a tool that adds an
    /// `EmitsActions` entry, surfaces with no change to this function.
    let ofModulesWith (attribute: ServerModule -> ComponentId) (modules: ServerModule list) : EventTopology =
        // A module action is only *subscribed* to when its target module
        // is actually composed — an action aimed at a module this
        // deployment does not compose is delivered to nobody, which is
        // the dead topic we want reported rather than papered over.
        let composedByName =
            modules |> List.map (fun m -> m.Name, attribute m) |> Map.ofList

        let perModule (serverModule: ServerModule) = [
            let owner = attribute serverModule

            // Seam 1 — `OnEvent` job triggers are `IEventStore`
            // subscriptions declared at compose time.
            for declaration in serverModule.JobHandlers do
                match declaration.Trigger with
                | OnEvent eventType when not (String.IsNullOrWhiteSpace eventType) ->
                    owner, EventKind.forEvent eventType, false
                | _ -> ()

            // Seam 2 — a tool's declared client-side actions: the tool
            // emits, the named module subscribes (when composed).
            for definition, _ in serverModule.AITools do
                match definition.EmitsActions with
                | None -> ()
                | Some declarations ->
                    for declaration in declarations do
                        if
                            not (String.IsNullOrWhiteSpace declaration.ModuleId)
                            && not (String.IsNullOrWhiteSpace declaration.ActionKey)
                        then
                            let kind = EventKind.forModuleAction declaration.ModuleId declaration.ActionKey
                            ComponentId.forTool definition.Name, kind, true

                            match Map.tryFind declaration.ModuleId composedByName with
                            | Some target -> target, kind, false
                            | None -> ()
        ]

        modules |> List.collect perModule |> assemble

    /// `ofModulesWith` under the default attribution — the call a
    /// composition root makes.
    let ofModules (modules: ServerModule list) : EventTopology = ofModulesWith componentIdOf modules

    /// The default attribution for an observed `ModuleEvent.SourceModule`:
    /// a consumer module's name maps to its `Name`-derived id, and a
    /// reserved / platform-owned source (empty, `_`-prefixed, or the SDK's
    /// own `ToolUp.Platform`) maps to `platformComponent`. Matching the
    /// reserved-source contract `CompositionValidator` and
    /// `ServerApp.componentIdForModule` already use keeps the topology's
    /// ids joinable against the manifest's.
    ///
    /// A composition whose modules declare explicit ids passes
    /// `fun source -> ServerApp.componentIdForModule source app |>
    /// Option.defaultValue EventTopology.platformComponent` instead, so a
    /// renamed module still resolves to its declared identity.
    let moduleAttribution (sourceModule: string) : ComponentId =
        if
            String.IsNullOrWhiteSpace sourceModule
            || sourceModule.StartsWith "_"
            || sourceModule = "ToolUp.Platform"
        then
            platformComponent
        else
            ComponentId.ofModule sourceModule

    // ── queries (431.B) ───────────────────────────────────────────────

    /// Every kind any component emits.
    let emittedKinds (topology: EventTopology) : Set<EventKind> =
        topology.Participants |> List.map _.Emits |> List.fold Set.union Set.empty

    /// Every kind any component subscribes to.
    let subscribedKinds (topology: EventTopology) : Set<EventKind> =
        topology.Participants
        |> List.map _.SubscribesTo
        |> List.fold Set.union Set.empty

    /// The components emitting a given kind, in id order.
    let emittersOf (kind: EventKind) (topology: EventTopology) : ComponentId list =
        topology.Participants
        |> List.filter (fun e -> e.Emits.Contains kind)
        |> List.map _.Component

    /// The components subscribing to a given kind, in id order.
    let subscribersOf (kind: EventKind) (topology: EventTopology) : ComponentId list =
        topology.Participants
        |> List.filter (fun e -> e.SubscribesTo.Contains kind)
        |> List.map _.Component

    /// The transports (`event` / `action`) on which this topology has
    /// observed at least one emission. Used by the preflight rule to tell
    /// "nothing emits this kind" from "we have no emitter evidence on this
    /// transport at all" — see `EventTopologyPreflight`.
    let observedTransports (topology: EventTopology) : Set<string> =
        emittedKinds topology |> Set.map EventKind.transport

    /// Kinds emitted by at least one component and subscribed to by none.
    /// Deterministic: sorted by kind, each emitter list in id order.
    let deadTopics (topology: EventTopology) : DeadTopic list =
        let subscribed = subscribedKinds topology

        emittedKinds topology
        |> Set.toList
        |> List.filter (subscribed.Contains >> not)
        |> List.sortWith (fun a b -> String.CompareOrdinal(EventKind.value a, EventKind.value b))
        |> List.map (fun kind -> {
            Kind = kind
            EmittedBy = emittersOf kind topology
        })

    /// Kinds subscribed to by at least one component and emitted by none.
    /// The literal query — the preflight rule applies a narrower,
    /// evidence-gated reading of it.
    let orphanSubscriptions (topology: EventTopology) : OrphanSubscription list =
        let emitted = emittedKinds topology

        subscribedKinds topology
        |> Set.toList
        |> List.filter (emitted.Contains >> not)
        |> List.sortWith (fun a b -> String.CompareOrdinal(EventKind.value a, EventKind.value b))
        |> List.map (fun kind -> {
            Kind = kind
            SubscribedBy = subscribersOf kind topology
        })

    /// The component-to-component adjacency: one edge per
    /// (emitter, subscriber, kind) triple. Deterministic ordering, so a
    /// tool rendering the graph gets a stable layout.
    let edges (topology: EventTopology) : EventTopologyEdge list =
        [
            for kind in emittedKinds topology do
                for from in emittersOf kind topology do
                    for target in subscribersOf kind topology ->
                        {
                            From = from
                            To = target
                            Kind = kind
                        }
        ]
        |> List.sortWith (fun a b ->
            match String.CompareOrdinal(a.From.Value, b.From.Value) with
            | 0 ->
                match String.CompareOrdinal(a.To.Value, b.To.Value) with
                | 0 -> String.CompareOrdinal(EventKind.value a.Kind, EventKind.value b.Kind)
                | c -> c
            | c -> c)

    // ── diff (431.D) ──────────────────────────────────────────────────

    /// The empty delta — what an identical pair (or a topology diffed
    /// against itself) projects to.
    let emptyDelta: EventTopologyDelta = {
        ComponentsAdded = []
        ComponentsRemoved = []
        EmissionsAdded = []
        EmissionsRemoved = []
        SubscriptionsAdded = []
        SubscriptionsRemoved = []
    }

    let private sortedPairs (pairs: (ComponentId * EventKind) list) : (ComponentId * EventKind) list =
        pairs
        |> List.sortWith (fun (ca, ka) (cb, kb) ->
            match String.CompareOrdinal(ca.Value, cb.Value) with
            | 0 -> String.CompareOrdinal(EventKind.value ka, EventKind.value kb)
            | c -> c)

    let private pairsOf (emissions: bool) (topology: EventTopology) : (ComponentId * EventKind) list =
        participations topology
        |> List.filter (fun (_, _, isEmission) -> isEmission = emissions)
        |> List.map (fun (componentId, kind, _) -> componentId, kind)

    /// Structurally diff two topologies. `before` is the reference (or
    /// prior) composition, `after` the candidate — so "added" means
    /// present in `after` and not in `before`. Keyed throughout by
    /// `(ComponentId, EventKind)`, so the result never depends on
    /// registration order.
    let diff (before: EventTopology) (after: EventTopology) : EventTopologyDelta =
        let componentsOf (t: EventTopology) =
            t.Participants |> List.map _.Component |> Set.ofList

        let beforeComponents = componentsOf before
        let afterComponents = componentsOf after

        let sortedIds (ids: Set<ComponentId>) =
            ids
            |> Set.toList
            |> List.sortWith (fun a b -> String.CompareOrdinal(a.Value, b.Value))

        let delta (emissions: bool) =
            let b = pairsOf emissions before |> Set.ofList
            let a = pairsOf emissions after |> Set.ofList
            sortedPairs (Set.difference a b |> Set.toList), sortedPairs (Set.difference b a |> Set.toList)

        let emissionsAdded, emissionsRemoved = delta true
        let subscriptionsAdded, subscriptionsRemoved = delta false

        {
            ComponentsAdded = sortedIds (Set.difference afterComponents beforeComponents)
            ComponentsRemoved = sortedIds (Set.difference beforeComponents afterComponents)
            EmissionsAdded = emissionsAdded
            EmissionsRemoved = emissionsRemoved
            SubscriptionsAdded = subscriptionsAdded
            SubscriptionsRemoved = subscriptionsRemoved
        }

    /// `true` when two topologies are structurally identical.
    let isEmptyDelta (d: EventTopologyDelta) : bool =
        List.isEmpty d.ComponentsAdded
        && List.isEmpty d.ComponentsRemoved
        && List.isEmpty d.EmissionsAdded
        && List.isEmpty d.EmissionsRemoved
        && List.isEmpty d.SubscriptionsAdded
        && List.isEmpty d.SubscriptionsRemoved

    /// A deterministic, human-readable rendering of a delta — the message
    /// a composition golden-file CI gate prints on a topology mismatch.
    let renderDelta (d: EventTopologyDelta) : string =
        let pairLines marker (pairs: (ComponentId * EventKind) list) =
            pairs
            |> List.map (fun (componentId, kind) ->
                sprintf "  %s %s %s" marker (ComponentId.value componentId) (EventKind.value kind))

        let section title (added: string list) (removed: string list) =
            if List.isEmpty added && List.isEmpty removed then
                []
            else
                [ sprintf "%s:" title; yield! added; yield! removed ]

        if isEmptyDelta d then
            "(no event-topology differences)"
        else
            [
                yield!
                    section
                        "Components"
                        (d.ComponentsAdded |> List.map (fun c -> "  + " + ComponentId.value c))
                        (d.ComponentsRemoved |> List.map (fun c -> "  - " + ComponentId.value c))
                yield! section "Emissions" (pairLines "+" d.EmissionsAdded) (pairLines "-" d.EmissionsRemoved)
                yield!
                    section "Subscriptions" (pairLines "+" d.SubscriptionsAdded) (pairLines "-" d.SubscriptionsRemoved)
            ]
            |> String.concat "\n"

    // ── wire projection ───────────────────────────────────────────────

    /// Project a topology to plain strings for persistence (a golden
    /// file, an external viewer). Deterministic — components in id order,
    /// kinds sorted ordinally within each set.
    let toWire (topology: EventTopology) : EventTopologyWireEntry list =
        topology.Participants
        |> List.map (fun entry ->
            let kinds (set: Set<EventKind>) =
                set
                |> Set.toList
                |> List.map EventKind.value
                |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

            {
                Component = ComponentId.value entry.Component
                EmittedKinds = kinds entry.Emits
                SubscribedKinds = kinds entry.SubscribesTo
            })

    /// Read a persisted wire projection back into a topology. Round-trips
    /// `toWire` exactly, which is what lets the golden-file gate compare a
    /// committed baseline against a live derivation through `diff`.
    let ofWire (entries: EventTopologyWireEntry list) : EventTopology =
        entries
        |> List.collect (fun entry ->
            let componentId = ComponentId.create entry.Component

            [
                for kind in entry.EmittedKinds -> componentId, EventKind.create kind, true
                for kind in entry.SubscribedKinds -> componentId, EventKind.create kind, false
            ])
        |> assemble

/// A thread-safe recorder of observed emissions. Deliberately a class
/// with instance state rather than module-level mutable: it is a registry
/// with an explicit lifetime, created only by a deployment that opts into
/// observation, and discarded with it.
type EventTopologyObserver() =
    // A set, not a counter — the topology asks "does this component emit
    // this kind", never "how often". Concurrent because `IEventStore
    // .Write` is called from every request thread.
    let observed = ConcurrentDictionary<ComponentId * EventKind, unit>()

    /// Record that `componentId` emitted `kind`. Idempotent.
    member _.RecordEmission(componentId: ComponentId, kind: EventKind) =
        observed.TryAdd((componentId, kind), ()) |> ignore

    /// Every distinct emission observed so far, as a topology. A snapshot
    /// — later writes do not mutate a value already returned.
    member _.Snapshot() : EventTopology =
        observed.Keys |> List.ofSeq |> EventTopology.ofObservedEmissions

/// An `IEventStore` decorator that records the emit half of the topology
/// from the writes themselves: every `ModuleEvent` already declares its
/// own `SourceModule` and `EventType`, so the attribution is the event's,
/// not a hand-maintained list — a module that starts writing a new event
/// type surfaces with no change to `EventTopology.fs`.
///
/// Every read path passes straight through, and `Write` records only
/// AFTER the inner write succeeds, so an observation can never claim an
/// emission that did not persist. Opt-in: a deployment that does not wrap
/// its store composes byte-for-byte what it did before and pays nothing
/// (GP 11 / GP 13).
///
/// `attribute` maps `ModuleEvent.SourceModule` to the identity the
/// emission is recorded under. It is the additive attribution parameter
/// this seam lacked — a constructor parameter on a new type rather than a
/// field on `ModuleEvent`, whose shape is a persisted wire format and a
/// public record constructor. The default,
/// `EventTopology.moduleAttribution`, reproduces the existing
/// name-derived convention exactly.
type ObservingEventStore(inner: IEventStore, observer: EventTopologyObserver, attribute: string -> ComponentId) =

    /// The default attribution — a deployment whose modules declare no
    /// explicit `ComponentId` needs nothing more.
    new(inner: IEventStore, observer: EventTopologyObserver) =
        ObservingEventStore(inner, observer, EventTopology.moduleAttribution)

    interface IEventStore with
        member _.Write(evt) = async {
            do! inner.Write evt

            if not (String.IsNullOrWhiteSpace evt.EventType) then
                observer.RecordEmission(attribute evt.SourceModule, EventKind.forEvent evt.EventType)
        }

        member _.ReadAll(scopeId) = inner.ReadAll scopeId

        member _.ReadByType(scopeId, eventType) = inner.ReadByType(scopeId, eventType)

        member _.ReadBySource(scopeId, sourceModule) =
            inner.ReadBySource(scopeId, sourceModule)

        member _.ListScopes() = inner.ListScopes()

        member _.Erase(scopeId, subjectUserId, policy, dryRun) =
            inner.Erase(scopeId, subjectUserId, policy, dryRun)

/// The opt-in well-formedness preflight over a derived event topology
/// (431.C): orphan subscriptions and dead topics, as declared rules,
/// wired through the Phase 9m `IConfigValidator` aggregator.
///
/// **Why not a `CompositionValidator` rule.** A Phase 281 `CompositionRule`
/// is `CompositionManifest -> CompositionReferences -> string list`, and
/// the topology is reachable from neither: it is derived from job
/// triggers, tool action declarations, and observed writes, none of which
/// the manifest or the reference set carries. Adding it to
/// `CompositionReferences` would mean growing that record — a constructor
/// break under the public-API baseline gate. So this follows the same
/// opt-in `serviceRegistration` closure pattern
/// `ComponentRequirementsPreflight` (Phase 432) settled on: the rules are
/// still declared data, still exported as a Phase 294 rule manifest, and
/// still run at the same startup gate as every other `IConfigValidator` —
/// they simply reach the aggregator through their own registration rather
/// than through `CompositionValidator.rules`.
///
/// **Class (Phase 585): structural.** The check is a pure in-memory sweep
/// over sets already derived — no socket, no dependency, microseconds —
/// so `ServerConfig.SkipPreflight` has nothing useful to skip. At the
/// default `DefectWarning` severity the rule cannot block a boot at all;
/// an operator who raises it to `DefectError` has deliberately made a
/// well-formed messaging graph a boot gate, which is precisely the class
/// of invariant an emergency boot must not silently switch off.
module EventTopologyPreflight =

    /// Stable `IConfigValidator.Name` for the topology check.
    [<Literal>]
    let ValidatorName = "event-topology-well-formedness"

    /// Stable rule code for a subscription no composed component feeds.
    [<Literal>]
    let OrphanSubscriptionRule = "event-topology-orphan-subscription"

    /// Stable rule code for a topic nothing in the composition consumes.
    [<Literal>]
    let DeadTopicRule = "event-topology-dead-topic"

    /// The default severity of an orphan subscription: a warning. A
    /// subscriber that never fires is a real defect, but it is also the
    /// shape a deployment legitimately takes while a producing module is
    /// staged behind it — so the default reports and continues, and a
    /// deployment that wants it fatal passes `DefectError`.
    let defaultOrphanSeverity: CompositionDefectSeverity = DefectWarning

    /// Phase 294 — the introspectable rule manifest for the topology
    /// rules, in the same `CompositionRuleDescriptor` shape
    /// `CompositionValidator.ruleManifest` exports, so an external
    /// pre-build checker reads one vocabulary for both rule families.
    /// The orphan rule's severity is the default; a deployment that
    /// raises it says so at registration.
    let ruleManifest: CompositionRuleDescriptor list = [
        {
            Code = OrphanSubscriptionRule
            Severity = defaultOrphanSeverity
            Description =
                "A component subscribing to an event kind that no composed component emits will never fire; the usual cause is a renamed event type or a producer module this deployment does not compose."
        }
        {
            Code = DeadTopicRule
            Severity = DefectWarning
            Description =
                "An event kind emitted by a composed component and consumed by none. Legitimate when the consumer is external (a webhook, an archive); otherwise a subscriber was renamed or never composed."
        }
    ]

    /// Phase 585 — the same rules with their class. Both are structural:
    /// pure in-memory sweeps over the derived topology, with no external
    /// dependency to be down.
    let classifiedRuleManifest: ClassifiedCompositionRule list =
        ruleManifest
        |> List.map (fun rule -> {
            Code = rule.Code
            Severity = rule.Severity
            Description = rule.Description
            Class = StructuralRule
        })

    /// The topology defects of a composition, at the configured orphan
    /// severity.
    ///
    /// **Evidence-gated.** An orphan is only reported on a transport the
    /// topology has actually observed an emission on. A composition that
    /// derives only its compose-time half has no `event:` emitter
    /// evidence at all — every `OnEvent` subscription would read as an
    /// orphan, which is an artefact of what was measured, not a defect in
    /// what was composed. Absence of evidence on a transport is `unknown`,
    /// not `empty`; the literal query (`EventTopology.orphanSubscriptions`)
    /// remains available for a caller that wants the unfiltered reading.
    let defects (orphanSeverity: CompositionDefectSeverity) (topology: EventTopology) : CompositionDefect list =
        let transports = EventTopology.observedTransports topology

        let orphans =
            EventTopology.orphanSubscriptions topology
            |> List.filter (fun orphan -> transports.Contains(EventKind.transport orphan.Kind))
            |> List.map (fun orphan -> {
                RuleCode = OrphanSubscriptionRule
                Severity = orphanSeverity
                Message =
                    sprintf
                        "Event kind '%s' is subscribed to by %s but emitted by no composed component. The subscription will never fire. Compose the producing component, correct the subscribed kind, or drop the subscription."
                        (EventKind.value orphan.Kind)
                        (orphan.SubscribedBy
                         |> List.map (ComponentId.value >> sprintf "'%s'")
                         |> String.concat ", ")
            })

        let dead =
            EventTopology.deadTopics topology
            |> List.map (fun topic -> {
                RuleCode = DeadTopicRule
                Severity = DefectWarning
                Message =
                    sprintf
                        "Event kind '%s' is emitted by %s and consumed by no composed component. This is expected when the consumer is external (a webhook subscription, an audit archive); otherwise a subscriber was renamed or never composed."
                        (EventKind.value topic.Kind)
                        (topic.EmittedBy
                         |> List.map (ComponentId.value >> sprintf "'%s'")
                         |> String.concat ", ")
            })

        orphans @ dead

    let private renderDefects (defects: CompositionDefect list) : string =
        defects
        |> List.map (fun d -> sprintf "[%s] %s" d.RuleCode d.Message)
        |> String.concat "\n"

    /// Translate topology defects into a `ValidationResult`: any
    /// `DefectError` aborts, a clean topology is `Ok`, warnings-only
    /// surface as `Warning`.
    let toValidationResult (defects: CompositionDefect list) : ValidationResult =
        let errors = defects |> List.filter (fun d -> d.Severity = DefectError)

        if not errors.IsEmpty then
            Error(renderDefects errors)
        else
            match defects with
            | [] -> Ok
            | warnings -> Warning(renderDefects warnings)

    /// The structural-class `IConfigValidator` that runs the topology
    /// rules at preflight.
    type EventTopologyValidator(topology: EventTopology, orphanSeverity: CompositionDefectSeverity) =
        interface IConfigValidator with
            member _.Name = ValidatorName
            member _.Timeout = IConfigValidator.defaultTimeout
            member _.Validate() = async { return toValidationResult (defects orphanSeverity topology) }

        interface IStructuralClassValidator

    /// The opt-in registration: folds the topology validator into the
    /// Phase 9m aggregator, returned as the same
    /// `IServiceCollection -> IServiceCollection` closure
    /// `CompositionValidator.serviceRegistration` and
    /// `ComponentRequirementsPreflight.serviceRegistration` return, so a
    /// composition root folds it into the extension `ServiceConfig` hook
    /// identically.
    ///
    /// **Nothing is registered for a topology with no participants**
    /// (GP 13) — including the `ServerApp.empty |> ServerApp.run` base
    /// case — so a deployment that never derives a topology composes a
    /// byte-for-byte identical `services` (GP 11).
    let serviceRegistration
        (orphanSeverity: CompositionDefectSeverity)
        (topology: EventTopology)
        : IServiceCollection -> IServiceCollection =
        fun services ->
            if not (List.isEmpty topology.Participants) then
                services.AddSingleton<IConfigValidator>(
                    EventTopologyValidator(topology, orphanSeverity) :> IConfigValidator
                )
                |> ignore

            services

    /// `serviceRegistration` at the default orphan severity (warning) —
    /// the shape a composition root uses unless it wants an orphan
    /// subscription to abort startup.
    let serviceRegistrationWithDefaults (topology: EventTopology) : IServiceCollection -> IServiceCollection =
        serviceRegistration defaultOrphanSeverity topology