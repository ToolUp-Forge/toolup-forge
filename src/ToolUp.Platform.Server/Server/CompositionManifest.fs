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

/// A read-only, machine-readable description of what an application
/// composed: every module, companion slot, datatype, and tool by stable
/// `ComponentId`, plus the config knobs that shaped composition. Produced
/// on demand by `ServerApp.compositionManifest` from the live registry —
/// never a separately-declared list (no drift-vs-reflection gap), and
/// never built unless a caller asks for it (GP 13).
type CompositionManifest = {
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
    ConfigKnobs: ConfigKnob list
}

/// Element constructors + assembly for `CompositionManifest`. Each
/// constructor namespaces the unit's identity under the matching
/// `ComponentId` slot (Phase 279), so the manifest's ids line up exactly
/// with the ids every telemetry / introspection surface correlates
/// against.
module CompositionManifest =

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
            Modules = modules
            CompanionSlots = companionSlots
            DataTypes = dataTypes
            Tools = tools
            Metrics = []
            Subjects = []
            ConfigKnobs = configKnobs
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

    /// The empty manifest — what a pipeline that composed nothing (or was
    /// never introspected) projects to.
    let empty: CompositionManifest = build [] [] [] [] []

    /// Every component entry across all kinds, in one flat list —
    /// convenient for a uniqueness sweep or a flat dump. (`ConfigKnob`s
    /// are not `ComponentEntry`s and are excluded.)
    let allComponents (m: CompositionManifest) : ComponentEntry list =
        m.Modules @ m.CompanionSlots @ m.DataTypes @ m.Tools @ m.Metrics @ m.Subjects