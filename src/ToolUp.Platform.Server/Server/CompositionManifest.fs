// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Introspectable composition manifest (CompositionManifest) ───────
//
// A generic, machine-readable description of *what an application
// composed* — every module, companion slot, datatype, and tool by its
// stable `ComponentId`, plus the config knobs that shaped composition.
//
// This widens the startup snapshot the config-drift detector already
// takes (it hashes the active companion-assembly set + records the
// resolved `ServerConfig`) into a full, queryable enumeration of the
// composed surface, keyed by the stable `ComponentId` values. The
// manifest is **derived from the live registry** — the accumulators
// `addModule` and the `with*` builders populate on `ServerApp` — never a
// separately-declared list, so it cannot drift from what was actually
// composed (no drift-vs-reflection gap).
//
// **On demand + zero cost when unused (GP 13).** The manifest is built
// only when a caller asks for it (`ServerApp.compositionManifest`); a
// deployment that never introspects pays nothing — no manifest is
// constructed at compose or at startup. An app that adds the call is
// byte-for-byte unchanged until it does (GP 11).
//
// **Generic substrate (GP 1).** The shape carries no vendor type and no
// domain type — only `ComponentId`, a kind discriminator, a display
// label, and a string config value. It powers config-validator
// preflight, health-check reporting, admin/ops dashboards, and GitOps
// drift detection without any of them depending on a concrete companion.

/// The kind of composed unit a `ComponentEntry` describes.
type ComponentKind =
    | ModuleComponent
    | CompanionComponent
    | DataTypeComponent
    | ToolComponent
    /// Phase 526 — a registered grounding metric (Phase 519).
    | MetricComponent
    /// Phase 526 — a registered grounding subject hierarchy (Phase 519).
    | SubjectComponent
    /// Phase 592 — a composition-declared disclosure purpose (the
    /// "declared why" facet of the disclosure vocabulary).
    | PurposeComponent

/// One composed unit enumerated in a `CompositionManifest`: its stable
/// `ComponentId`, what kind of unit it is, a human-readable label, and —
/// for a multi-impl companion slot — the implementing sub-id.
type ComponentEntry = {
    /// The stable Phase 279 identity of this composed unit.
    Id: ComponentId
    Kind: ComponentKind
    /// Display label: the module `Name`, the companion interface slot
    /// name, the datatype id, or the tool name.
    Label: string
    /// For a companion slot filled by a specific implementation in a
    /// multi-impl list (audit sinks, notification sinks, health checks,
    /// config validators, smoke tests), the impl's own sub-id (sink
    /// `Name` / `Kind`). `None` for a single-impl slot and for modules /
    /// datatypes / tools.
    Impl: string option
}

/// A single composition-shaping configuration knob — a `ServerConfig`
/// switch whose value changes *what* gets composed (drift detection,
/// usage metering, rate limiting, …). Name + rendered value only; the
/// full `ServerConfig` is not duplicated here.
type ConfigKnob = { Name: string; Value: string }

/// Phase 694 — one metric's declared **canonical-method selector**: which
/// method's lineage a method-less query over that metric resolves to
/// (Phase 566 / D19).
///
/// **Why this is a field of its own rather than the metric entry's unused
/// `Impl` slot.** Phase 684 recorded the argument and it is the reason
/// this record exists: `metricEntry` records a metric as its id with
/// `Impl = None`, and the boot comparison folds an absent `Impl` into the
/// entry's `Label`. Moving the selector into that slot would change every
/// already-sealed deployment's recorded composition the moment it
/// upgraded, and the Phase 657 preflight would report the upgrade itself
/// as drift on every one of them (GP 11). A separate, *versioned* field
/// leaves the entry projection byte-identical and lets a binding sealed
/// before this phase be read as what it is — silent on the selector,
/// which is not the same as recording that nothing changed.
type MetricCanonicalMethod = {
    /// The declaring metric's registry id (`MetricDefinition.Id`) — the
    /// same string `metricEntry` carries as its `Label`.
    MetricId: string
    /// The declared selector, matched against method-identity strings per
    /// `Grounding.CanonicalMethod.matches`.
    Selector: string
}

/// Phase 592 — a composition-declared disclosure purpose (the "declared
/// why" facet), as the generic strings the manifest projects (GP 1: the
/// typed taxonomy + enforcement live in the facts companion; the
/// platform carries only the introspectable declaration). Accumulated on
/// `ServerApp.RegisteredPurposes` by the facts companion's purpose
/// compose and projected into the manifest beside the Phase 526
/// grounding entries — so the whole purpose regime is readable before
/// any data flows.
type RegisteredPurpose = {
    /// The stable purpose id from the declared taxonomy.
    PurposeId: string
    /// Human-readable description of what the purpose covers.
    Description: string
    /// The taxonomy version the purpose was declared under.
    TaxonomyVersion: string
    /// Canonical egress-surface names this purpose is allowed to serve
    /// (the per-route allowed sets, inverted per purpose).
    AllowedSurfaces: string list
}

/// A read-only, machine-readable description of what an application
/// composed: every module, companion slot, datatype, and tool by stable
/// `ComponentId`, plus the config knobs that shaped composition. Produced
/// on demand by `ServerApp.compositionManifest` from the live registry —
/// never a separately-declared list (no drift-vs-reflection gap), and
/// never built unless a caller asks for it (GP 13).
type CompositionManifest = {
    /// Phase 694 — the manifest schema this value was projected under.
    ///
    /// **Read it, never assume it.** A manifest deserialised from a
    /// binding sealed before Phase 694 carries no such field, so it
    /// arrives as `0`; `CompositionManifest.effectiveSchemaVersion` reads
    /// that as the pre-694 schema rather than as a version zero. The
    /// version is what lets a reader tell "this manifest declares no
    /// canonical methods" from "this manifest is too old to say" — a
    /// distinction no amount of inspecting `CanonicalMethods` can recover,
    /// because both render as an empty list.
    SchemaVersion: int
    Modules: ComponentEntry list
    CompanionSlots: ComponentEntry list
    DataTypes: ComponentEntry list
    Tools: ComponentEntry list
    /// Phase 526 — registered grounding metrics, keyed by
    /// `ComponentId.forMetric`; empty when no module declared any (a
    /// grounding-free composition, byte-identical to pre-526).
    Metrics: ComponentEntry list
    /// Phase 526 — registered grounding subject hierarchies.
    Subjects: ComponentEntry list
    /// Phase 592 — composition-declared disclosure purposes, keyed by
    /// `ComponentId.forPurpose`; empty when no taxonomy is declared (a
    /// purpose-free composition, byte-identical to pre-592). The
    /// per-surface allowed sets ride `ConfigKnobs` as
    /// `DisclosurePurposes.<Surface>` entries.
    Purposes: ComponentEntry list
    ConfigKnobs: ConfigKnob list
    /// Phase 694 — the canonical-method selector each registered metric
    /// declared, when it declared one. Empty at `SchemaVersion` 2+ means
    /// *no metric declares one*; at the pre-694 schema it means *this
    /// manifest never carried them*, which is why the two are told apart
    /// by the version above and not by this list.
    ///
    /// This is the one grounding declaration that changes what an already
    /// recorded number MEANS without changing anything else the manifest
    /// enumerates, which is why the boot seal was blind to exactly it.
    CanonicalMethods: MetricCanonicalMethod list
}

/// Element constructors + assembly for `CompositionManifest`. Each
/// constructor namespaces the unit's identity under the matching
/// `ComponentId` slot (Phase 279), so the manifest's ids line up exactly
/// with the ids every telemetry / introspection surface correlates
/// against.
module CompositionManifest =

    // ─── Schema version (Phase 694) ──────────────────────────────────

    /// The schema every manifest this binary projects is stamped with.
    [<Literal>]
    let SchemaVersion = 2

    /// The schema of every manifest projected before Phase 694 — the
    /// shape that carried no version field at all, and therefore
    /// deserialises its version as `0`.
    [<Literal>]
    let PreCanonicalMethodSchemaVersion = 1

    /// The schema at which canonical-method selectors began to be
    /// recorded. Separate from `SchemaVersion` deliberately: the next
    /// field to join the manifest advances `SchemaVersion` and must NOT
    /// move this, or every binding sealed between the two would be read
    /// as silent about selectors it did in fact record.
    [<Literal>]
    let CanonicalMethodSchemaVersion = 2

    /// The schema a manifest was projected under, reading an absent or
    /// non-positive field as the pre-694 shape.
    ///
    /// `0` is what a JSON round-trip yields for a field the document does
    /// not carry, and it is the ONLY value a legacy manifest can present.
    /// Mapping it to the pre-694 schema here — once — is what keeps every
    /// other reader from having to know that.
    let effectiveSchemaVersion (manifest: CompositionManifest) : int =
        if manifest.SchemaVersion <= 0 then
            PreCanonicalMethodSchemaVersion
        else
            manifest.SchemaVersion

    /// Whether this manifest is new enough for its `CanonicalMethods` to
    /// be evidence. `false` means the manifest is SILENT on the selectors,
    /// which a comparison must report as unrecorded rather than resolve as
    /// unchanged.
    let recordsCanonicalMethods (manifest: CompositionManifest) : bool =
        effectiveSchemaVersion manifest >= CanonicalMethodSchemaVersion

    /// The recorded canonical-method selectors, in a canonical order, with
    /// the null-list coercion every manifest read path needs (a manifest
    /// deserialised from a document predating the field carries `null`,
    /// and a null F# list faults on the first list operation).
    let canonicalMethods (manifest: CompositionManifest) : MetricCanonicalMethod list =
        (if isNull (box manifest.CanonicalMethods) then
             []
         else
             manifest.CanonicalMethods)
        |> List.distinctBy _.MetricId
        |> List.sortBy _.MetricId

    /// **THE derivation** of canonical-method selectors from the metric
    /// registry a composition accumulated.
    ///
    /// One function, called by both readers that need the answer: the
    /// manifest projection (`ServerApp.compositionManifest`) and Phase
    /// 684's grounding envelope, which now reads the selectors back OUT of
    /// the manifest rather than deriving them a second time. Two
    /// derivations that happen to agree today are two derivations that can
    /// stop agreeing, and the seal either side of them would keep
    /// verifying while they did.
    let canonicalMethodsOf (metrics: Grounding.MetricRegistration list) : MetricCanonicalMethod list =
        (if isNull (box metrics) then [] else metrics)
        |> List.choose (fun r ->
            r.Definition.CanonicalMethod
            |> Option.map (fun selector -> {
                MetricId = r.Definition.Id
                Selector = selector
            }))
        |> List.distinct
        |> List.sortBy _.MetricId

    /// A registered module, identified by the resolved id `addModule`
    /// accumulated onto `ServerApp.ModuleComponentIds` (explicit when
    /// declared via `ServerModule.withComponentId`, else name-derived).
    let moduleEntry (name: string, id: ComponentId) : ComponentEntry = {
        Id = id
        Kind = ModuleComponent
        Label = name
        Impl = None
    }

    /// A registered datatype, keyed by its declared `DataType.Id`.
    let dataTypeEntry (dataTypeId: string) : ComponentEntry = {
        Id = ComponentId.forDataType dataTypeId
        Kind = DataTypeComponent
        Label = dataTypeId
        Impl = None
    }

    /// A registered AI tool, keyed by its declared `AIToolDefinition.Name`.
    let toolEntry (toolName: string) : ComponentEntry = {
        Id = ComponentId.forTool toolName
        Kind = ToolComponent
        Label = toolName
        Impl = None
    }

    /// A single-impl companion slot, keyed by its interface name (one slot
    /// per interface — `IAuthProvider`, `IBlobStorage`, …).
    let companionSlotEntry (interfaceName: string) : ComponentEntry = {
        Id = ComponentId.forCompanionSlot interfaceName
        Kind = CompanionComponent
        Label = interfaceName
        Impl = None
    }

    /// One implementation within a multi-impl companion slot, keyed by the
    /// interface name composed with the impl's own sub-id (sink `Name` /
    /// `Kind`) — never its position in the list (Phase 279 rule).
    let companionImplEntry (interfaceName: string) (implSubId: string) : ComponentEntry = {
        Id = ComponentId.forCompanionImpl interfaceName implSubId
        Kind = CompanionComponent
        Label = interfaceName
        Impl = Some implSubId
    }

    /// A registered grounding metric, keyed by its declared registry id.
    let metricEntry (metricId: string) : ComponentEntry = {
        Id = ComponentId.forMetric metricId
        Kind = MetricComponent
        Label = metricId
        Impl = None
    }

    /// A registered grounding subject hierarchy, keyed by its registry id.
    let subjectEntry (subjectId: string) : ComponentEntry = {
        Id = ComponentId.forSubject subjectId
        Kind = SubjectComponent
        Label = subjectId
        Impl = None
    }

    /// Phase 592 — a composition-declared disclosure purpose, keyed by
    /// `ComponentId.forPurpose`. `Impl` carries the taxonomy version so
    /// the manifest records which vocabulary the purpose belongs to.
    let purposeEntry (p: RegisteredPurpose) : ComponentEntry = {
        Id = ComponentId.forPurpose p.PurposeId
        Kind = PurposeComponent
        Label = p.PurposeId
        Impl = Some p.TaxonomyVersion
    }

    let knob (name: string) (value: string) : ConfigKnob = { Name = name; Value = value }

    /// Assemble a manifest from the enumerated entries. Pure projection —
    /// the caller (`ServerApp.compositionManifest`) supplies lists derived
    /// from the live registry.
    let build
        (modules: ComponentEntry list)
        (companionSlots: ComponentEntry list)
        (dataTypes: ComponentEntry list)
        (tools: ComponentEntry list)
        (configKnobs: ConfigKnob list)
        : CompositionManifest =
        {
            // Phase 694 — a manifest this binary projects is stamped with
            // the current schema whether or not any metric declares a
            // selector, because "I record selectors and there are none" is
            // exactly the claim a legacy manifest cannot make.
            SchemaVersion = SchemaVersion
            Modules = modules
            CompanionSlots = companionSlots
            DataTypes = dataTypes
            Tools = tools
            Metrics = []
            Subjects = []
            Purposes = []
            ConfigKnobs = configKnobs
            CanonicalMethods = []
        }

    /// Phase 526 — attach registered grounding metric / subject entries to
    /// a built manifest. Kept separate from `build` so every existing
    /// `build` call stays source-stable; a grounding-free composition never
    /// calls this and its manifest carries empty `Metrics` / `Subjects`
    /// (byte-identical to pre-526).
    let withGrounding
        (metrics: ComponentEntry list)
        (subjects: ComponentEntry list)
        (m: CompositionManifest)
        : CompositionManifest =
        {
            m with
                Metrics = metrics
                Subjects = subjects
        }

    /// Phase 592 — attach composition-declared disclosure-purpose entries
    /// to a built manifest, same additive shape as `withGrounding`: every
    /// existing `build` call stays source-stable, and a purpose-free
    /// composition never calls this (byte-identical to pre-592).
    let withPurposes (purposes: ComponentEntry list) (m: CompositionManifest) : CompositionManifest = {
        m with
            Purposes = purposes
    }

    /// Phase 694 — record the canonical-method selectors, same additive
    /// shape as `withGrounding` / `withPurposes`.
    ///
    /// It stamps `SchemaVersion` as well as the list, and that is the
    /// whole mechanism: recording the selectors is what makes a manifest
    /// one that SPEAKS about them. A manifest that went through this call
    /// with an empty list is claiming "no metric declares one"; a manifest
    /// that never did is claiming nothing at all.
    let withCanonicalMethods (methods: MetricCanonicalMethod list) (m: CompositionManifest) : CompositionManifest = {
        m with
            SchemaVersion = SchemaVersion
            CanonicalMethods = methods
    }

    /// The empty manifest — what a pipeline that composed nothing (or was
    /// never introspected) projects to.
    let empty: CompositionManifest = build [] [] [] [] []

    /// Every component entry across all kinds, in one flat list —
    /// convenient for a uniqueness sweep or a flat dump. (`ConfigKnob`s
    /// are not `ComponentEntry`s and are excluded.)
    let allComponents (m: CompositionManifest) : ComponentEntry list =
        m.Modules
        @ m.CompanionSlots
        @ m.DataTypes
        @ m.Tools
        @ m.Metrics
        @ m.Subjects
        @ m.Purposes