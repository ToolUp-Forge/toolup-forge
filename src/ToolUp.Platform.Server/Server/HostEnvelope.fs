// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FSharp.Reflection
open ToolUp.Remoting.Json.SystemTextJson

// ─── Host-envelope descriptor (HostEnvelope) ─────────────────────────
//
// Three descriptors, three questions. `CompositionManifest` (Phase 280)
// answers *what did this app compose*. `ComposableSurface` (Phase 293)
// answers *what can forge compose at all*. `ModuleSurface` (Phase 581)
// answers *what does one module provide and need*. None of them answers
// the question a module author targeting an EXISTING deployment must ask
// first: **what can my module rely on here?**
//
// `HostEnvelope.describe` is that answer — one read-only descriptor of a
// specific composition's **offer surface**:
//
//   * the composed capability layers (how many modules / companions /
//     data types / tools / metrics / subjects this deployment carries,
//     with their `ComponentId`s);
//   * every companion slot forge can compose, marked filled or open —
//     an OPEN slot is exactly what a module may NOT rely on;
//   * every composed module's `ModuleSurface` — the data-type ids and
//     their wire `TypeName`s, the query-bus keys already answered, the
//     tool names already taken, the substrate each module implies;
//   * every composition-shaping config knob, with both its admissible
//     value set and the value THIS deployment resolved;
//   * the occupied route prefixes and exact routes, attributed to their
//     owning module, with the admit set guarding each;
//   * the platform assembly + version the envelope was derived under.
//
// The envelope is the **type of the module-shaped hole**: an external
// authoring tool reads it, sees which keys are taken and which substrate
// is present, and emits a module that fits — without linking the server
// assembly, because `HostEnvelope.toJson` projects the whole thing to
// canonical JSON and `HostEnvelope.stampOf` stamps it, so a snapshot
// pinned days ago can be checked against a running app for staleness.
//
// **Derived, never hand-listed.** Every part comes from a derivation
// that already exists and is already drift-guarded: the capability
// layers group the live `CompositionManifest` (and are SEEDED from the
// `ComponentKind` union, so a newly-shipped kind surfaces as a layer with
// no edit here); the slot universe is `ComposableSurface.slots ()`,
// reflected off the `ServerApp` record's interface-typed fields, joined
// against the manifest's composed slots; the knob schemas are
// `ComposableSurface.configKnobs ()`, reflected off `ServerConfig`'s
// enum-like union fields, with the resolved value read reflectively off
// the live `ServerConfig`; the module surfaces are `ModuleSurface
// .describeWith`; and the route offers are FILTERED from those same
// module surfaces rather than re-derived, so the two can never disagree.
// A composition that gains a slot, a knob, a kind or a module surfaces
// here with zero changes to this file.
//
// **Generic substrate (GP 9); zero cost when unused (GP 13).** The shape
// carries no vendor and no domain type — only `ComponentId`s, interface
// names, and strings drawn from the deployment's own registrations — and
// nothing is built until a caller asks. A deployment that never
// introspects is byte-for-byte unchanged (GP 11).

/// Whether a composable companion slot is filled in this deployment. An
/// `OpenSlot` is the load-bearing half: it is precisely what a module
/// targeting this deployment may NOT rely on.
type HostSlotState =
    /// At least one implementation is composed into the slot.
    | FilledSlot
    /// forge can compose the slot, but this deployment did not.
    | OpenSlot

/// One companion slot as this deployment offers it: the slot forge can
/// compose, and whether — and by what — it is filled here.
type HostSlotOffer = {
    /// The Phase 279 slot id (`companion:<interface>`).
    OfferSlot: ComponentId
    /// The companion interface name (`IAuthProvider`, `IAuditSink`, …).
    OfferInterface: string
    OfferCardinality: SlotCardinality
    OfferState: HostSlotState
    /// The impl sub-ids composed into a multi-impl slot (sink `Name` /
    /// `Kind`), sorted. Empty for an open slot and for a filled
    /// single-impl slot, which carries no sub-id.
    OfferImpls: string list
}

/// One composed capability layer — a `ComponentKind` and everything this
/// deployment composed under it. Seeded from the `ComponentKind` union,
/// so a kind with nothing composed still surfaces (count `0`), which is
/// the honest answer to "does this deployment offer any grounding
/// metrics?".
type HostCapabilityLayer = {
    /// The kind label (`module` / `companion` / `datatype` / `tool` /
    /// `metric` / `subject`), derived from the union case name.
    LayerKind: string
    LayerCount: int
    /// The composed `ComponentId`s under this kind, as wire strings,
    /// sorted.
    LayerIds: string list
}

/// One composition-shaping config knob as this deployment offers it: the
/// values it COULD take (the schema an authoring tool renders) and the
/// value it DID take here.
type HostKnobOffer = {
    /// The `ServerConfig` field name.
    KnobName: string
    /// The admissible values — the union case names.
    KnobAdmissible: string list
    /// The value this deployment resolved, read off the live
    /// `ServerConfig`.
    KnobResolved: string
}

/// One occupied route, attributed to the module that declared it — the
/// prefix space a new module must NOT collide with.
type HostRouteOffer = {
    /// The declared route prefix, or `"<METHOD> <path>"` for an exact
    /// per-route declaration.
    RouteKey: string
    /// The declaring module's registration `Name`.
    RouteOwner: string
    /// The admit-set label of the `SurfaceRequirement` guarding it.
    RouteAdmits: string
    /// `true` for an exact `RouteSurfaceRequirements` declaration,
    /// `false` for a `RoutePrefixes` prefix claim.
    RouteExact: bool
}

/// What a deployment offers a module: its composed capability layers,
/// its filled and open companion slots, every composed module's surface,
/// its config-knob schemas with resolved values, its occupied routes, and
/// the platform build it was derived under. Produced on demand by
/// `HostEnvelope.describe` from the live composition — never a
/// separately-declared list.
type HostEnvelope = {
    /// The envelope shape's own version — bumped when a field is added
    /// or removed, so a consumer can reject a snapshot it cannot read.
    EnvelopeSchemaVersion: int
    /// The platform assembly + version the envelope was derived under
    /// (resolved through Phase 288's total provenance reader, so a
    /// metadata-less load context yields `unknown` rather than throwing).
    EnvelopePlatform: ComponentProvenance
    EnvelopeCapabilities: HostCapabilityLayer list
    EnvelopeSlots: HostSlotOffer list
    EnvelopeModules: ModuleSurface list
    EnvelopeKnobs: HostKnobOffer list
    EnvelopeRoutes: HostRouteOffer list
}

/// The identity of one envelope — what a consumer pins beside a
/// generated module so it can tell, later, whether the deployment moved
/// underneath it.
///
/// A sidecar record rather than a field on `HostEnvelope`, for two
/// reasons: the hash is taken OVER the envelope's canonical JSON, so it
/// cannot live inside the thing it hashes without a self-reference; and
/// (the Phase 585 / 432 reason) adding a field to an F# record changes
/// its constructor signature, which is breaking for every consumer that
/// constructs one.
type HostEnvelopeStamp = {
    StampSchemaVersion: int
    /// The platform assembly version the stamped envelope was derived
    /// under.
    StampPlatformVersion: string
    /// SHA-256 (lowercase hex) over the envelope's canonical JSON.
    StampContentHash: string
}

module HostEnvelope =

    /// The current `HostEnvelope` shape version. Bump on any change to
    /// the record's fields; a consumer holding a snapshot with a
    /// different value knows to re-derive rather than misread.
    [<Literal>]
    let CurrentSchemaVersion = 1

    // ── capability layers ─────────────────────────────────────────────

    /// The wire label for a `ComponentKind` case, derived from the case
    /// name (`DataTypeComponent` → `datatype`). Derived rather than
    /// matched, so a newly-shipped kind gets a label with no edit here.
    let private kindLabel (kindName: string) : string =
        let trimmed =
            if kindName.EndsWith("Component", StringComparison.Ordinal) then
                kindName.Substring(0, kindName.Length - "Component".Length)
            else
                kindName

        trimmed.ToLowerInvariant()

    /// Every `ComponentKind` case label, in union-declaration order — the
    /// seed set, so a layer with nothing composed still surfaces.
    let private declaredKindLabels () : string list =
        FSharpType.GetUnionCases typeof<ComponentKind>
        |> Array.map (fun case -> kindLabel case.Name)
        |> Array.toList

    let private capabilityLayers (manifest: CompositionManifest) : HostCapabilityLayer list =
        let composed =
            CompositionManifest.allComponents manifest
            |> List.groupBy (fun entry -> kindLabel (string entry.Kind))
            |> Map.ofList

        declaredKindLabels ()
        |> List.map (fun label ->
            let entries = composed |> Map.tryFind label |> Option.defaultValue []

            let ids =
                entries
                |> List.map (fun entry -> ComponentId.value entry.Id)
                |> List.distinct
                |> List.sort

            {
                LayerKind = label
                LayerCount = ids.Length
                LayerIds = ids
            })
        |> List.sortBy _.LayerKind

    // ── companion slots: the universe, marked filled or open ──────────

    let private slotOffers (manifest: CompositionManifest) : HostSlotOffer list =
        // A composed companion entry's `Label` IS the interface name for
        // both the single-impl (`companionSlotEntry`) and multi-impl
        // (`companionImplEntry`) shapes, so this join needs no per-slot
        // knowledge.
        let composedByInterface =
            manifest.CompanionSlots |> List.groupBy _.Label |> Map.ofList

        ComposableSurface.slots ()
        |> List.map (fun slot ->
            let composed =
                composedByInterface |> Map.tryFind slot.Interface |> Option.defaultValue []

            {
                OfferSlot = slot.Slot
                OfferInterface = slot.Interface
                OfferCardinality = slot.Cardinality
                OfferState = if composed.IsEmpty then OpenSlot else FilledSlot
                OfferImpls = composed |> List.choose _.Impl |> List.distinct |> List.sort
            })
        |> List.sortBy _.OfferInterface

    // ── config knobs: the schema, plus what THIS deployment resolved ──

    /// Read one `ServerConfig` field's rendered value off the live config
    /// by reflection. Total — an absent field or a null value renders
    /// empty rather than throwing.
    let private resolvedKnobValue (config: ServerConfig) (fieldName: string) : string =
        FSharpType.GetRecordFields typeof<ServerConfig>
        |> Array.tryFind (fun field -> field.Name = fieldName)
        |> Option.map (fun field ->
            match field.GetValue(box config) with
            | null -> ""
            | value -> string value)
        |> Option.defaultValue ""

    let private knobOffers (config: ServerConfig) : HostKnobOffer list =
        ComposableSurface.configKnobs ()
        |> List.map (fun schema -> {
            KnobName = schema.Name
            KnobAdmissible = schema.Values
            KnobResolved = resolvedKnobValue config schema.Name
        })
        |> List.sortBy _.KnobName

    // ── routes: filtered from the module surfaces, not re-derived ─────

    /// The route-bearing `ModuleSurface` entry kinds, as `ModuleSurface`
    /// itself emits them: a declared prefix claim and an exact per-route
    /// requirement override.
    [<Literal>]
    let private RoutePrefixKind = "route-prefix"

    [<Literal>]
    let private ExactRouteKind = "route"

    let private routeOffers (surfaces: ModuleSurface list) : HostRouteOffer list =
        surfaces
        |> List.collect (fun surface ->
            surface.Provides
            |> List.choose (fun entry ->
                if entry.Kind = RoutePrefixKind || entry.Kind = ExactRouteKind then
                    Some {
                        RouteKey = entry.Key
                        RouteOwner = surface.Module
                        RouteAdmits = entry.Label
                        RouteExact = entry.Kind = ExactRouteKind
                    }
                else
                    None))
        |> List.sortBy (fun offer -> offer.RouteKey, offer.RouteOwner)

    // ── the descriptor ────────────────────────────────────────────────

    /// Describe what a deployment offers a module, given the composed
    /// `ServerApp` and the modules composed onto it — each optionally
    /// paired with its erased client registration (`ErasedModule`, passed
    /// as `obj`: the Server tier does not reference the Client tier), so
    /// the page / feature-flag / event-topic side of each module's
    /// surface is derivable too.
    ///
    /// The modules are passed rather than read back off the app because
    /// `addModule` FANS a `ServerModule` into the app's accumulators and
    /// keeps no registration record — the same reason the Phase 431 / 433
    /// / 438 derived lenses take a `ServerModule list`. Everything else
    /// comes from the app.
    ///
    /// Pure + on demand: nothing is built until a caller asks (GP 13).
    let describeWith (app: ServerApp, modules: (ServerModule * obj option) list) : HostEnvelope =
        let manifest = ServerApp.compositionManifest app

        let surfaces =
            modules
            |> List.map ModuleSurface.describeWith
            |> List.sortBy (fun surface -> surface.Module, ComponentId.value surface.Component)

        {
            EnvelopeSchemaVersion = CurrentSchemaVersion
            // The manifest type is forge's own — its assembly IS the
            // platform assembly, resolved totally (Phase 288 / 435).
            EnvelopePlatform = ComponentProvenance.forType typeof<CompositionManifest>
            EnvelopeCapabilities = capabilityLayers manifest
            EnvelopeSlots = slotOffers manifest
            EnvelopeModules = surfaces
            EnvelopeKnobs = knobOffers app.Config
            EnvelopeRoutes = routeOffers surfaces
        }

    /// Describe a deployment's offer surface from its server-side
    /// registrations alone. The pages / feature-flag / event-topic side of
    /// each module's surface needs the client registration — see
    /// `describeWith`.
    let describe (app: ServerApp, modules: ServerModule list) : HostEnvelope =
        describeWith (app, modules |> List.map (fun m -> m, None))

    // ── canonical JSON + the staleness stamp ──────────────────────────

    /// The canonical wire serialiser — the same converter set the rest of
    /// the SDK's non-Remoting JSON uses.
    let private jsonOptions = FableConverters.create ()

    /// Project the envelope to canonical JSON so an external authoring
    /// tool can pin it without linking the server assembly.
    ///
    /// Deterministic: every list on the descriptor is emitted in a stable
    /// sort order (and each embedded `ModuleSurface` in the order Phase
    /// 581 already sorts), and record fields serialise in declaration
    /// order — the same composition always yields byte-identical JSON,
    /// which is what makes the content hash below meaningful.
    let toJson (envelope: HostEnvelope) : string =
        JsonSerializer.Serialize(envelope, jsonOptions)

    /// SHA-256 (lowercase hex) over the canonical JSON.
    let contentHash (envelope: HostEnvelope) : string =
        use sha = SHA256.Create()

        sha.ComputeHash(Encoding.UTF8.GetBytes(toJson envelope))
        |> Array.map (sprintf "%02x")
        |> String.concat ""

    /// Stamp an envelope — the sidecar a consumer pins beside a generated
    /// module.
    let stampOf (envelope: HostEnvelope) : HostEnvelopeStamp = {
        StampSchemaVersion = envelope.EnvelopeSchemaVersion
        StampPlatformVersion = envelope.EnvelopePlatform.Version
        StampContentHash = contentHash envelope
    }

    /// `describe` + `toJson` in one call.
    let describeJson (app: ServerApp, modules: ServerModule list) : string = describe (app, modules) |> toJson

    // ── staleness: is a pinned snapshot still true of this app? ────────

    /// Why a pinned stamp no longer matches a live envelope. Empty when
    /// the snapshot is still current; otherwise one stable reason code per
    /// axis that moved, so a consumer can distinguish "regenerate, the
    /// deployment changed" from "upgrade your tool, the shape changed".
    [<Literal>]
    let SchemaVersionMovedReason = "envelope-schema-version-moved"

    [<Literal>]
    let PlatformVersionMovedReason = "platform-version-moved"

    [<Literal>]
    let ContentChangedReason = "envelope-content-changed"

    /// Compare a pinned stamp against a freshly-derived envelope. `[]`
    /// means the snapshot is still exactly true of this deployment.
    let staleness (stamp: HostEnvelopeStamp) (live: HostEnvelope) : string list =
        let liveStamp = stampOf live

        [
            if stamp.StampSchemaVersion <> liveStamp.StampSchemaVersion then
                SchemaVersionMovedReason
            if stamp.StampPlatformVersion <> liveStamp.StampPlatformVersion then
                PlatformVersionMovedReason
            if stamp.StampContentHash <> liveStamp.StampContentHash then
                ContentChangedReason
        ]

    /// `true` when a pinned stamp is still exactly true of a live
    /// envelope.
    let isCurrent (stamp: HostEnvelopeStamp) (live: HostEnvelope) : bool = (staleness stamp live).IsEmpty