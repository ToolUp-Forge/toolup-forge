// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 593 — composition inspector surface ───────────────────────
//
// The platform can already PROVE a great deal about itself: the
// composition manifest (Phase 280), the invariant rule manifest and its
// preflight verdicts (Phase 294 / 585), the grounding + disclosure
// declarations (Phase 526 / 592), and the provenance graph (Phase 524 /
// 648). Every one of those proofs is developer-shaped — a JSON export,
// a validator code, a test assertion. `ICompositionInspectorApi` is the
// read surface that makes the same declarations legible to a reviewer
// who will never run `dotnet test`.
//
// **Every view here is a PROJECTION of a server-tier type, never the
// type itself**, for the reason `AuditEventView` is one: the source
// types (`CompositionManifest`, `CompositionRuleDescriptor`,
// `GroundingEnvelope`) live in `ToolUp.Platform.Server`, which the Fable
// client cannot see, and moving them Core-side to share them would drag
// the composition machinery into the client tier. The projections carry
// the declared strings and nothing else, so the wire shape is stable
// across additions to the server-tier records.
//
// **Read-only by construction.** No method returns `unit` and none takes
// a value that could be written back; the panel set is a window onto
// what the deployment declared at compose time. A shipped test asserts
// the no-`unit`-return property over this record's fields, the way
// `IProvenanceQueryApi` does, so a write path cannot be added here
// without the assertion failing.
//
// **Scope + gate.** Owner/Admin (`TeamRoles.canWriteTeamConfig`) in team
// mode; any authenticated caller in the single-scope modes; anonymous
// refused outright. The composition story names the deployment's
// companions, its config knobs, and what it may disclose to whom — a
// reconnaissance gift to an unauthenticated reader.
//
// **What this surface deliberately does NOT carry: the provenance
// walk.** The shard that specified this phase asked for a "provenance
// walk by id" as a sixth method here. `IProvenanceQueryApi` (Phase 648)
// already is that contract, and it carries a `FactExport` disclosure
// door and declared depth/node caps. A second walk on this record would
// be a second door over the same graph, which is how the two stop
// agreeing — so the inspector reports whether the provenance substrate
// is COMPOSED (`GetProvenance`) and the module's Provenance panel walks
// through the existing contract. One door, named in one place.

/// One component the deployment composes, as the manifest records it.
type InspectedComponent = {
    /// The stable `ComponentId` string.
    Id: string
    /// The component's kind, as the manifest's own lowercase label
    /// (`module` / `companion-slot` / `data-type` / `tool` / `metric` /
    /// `subject` / `purpose`). A string rather than a mirrored DU: the
    /// kind set grows, and a client one release behind should render an
    /// unfamiliar kind rather than fail to decode the page.
    Kind: string
    /// Human-readable label the manifest recorded.
    Label: string
    /// The composed implementation, where the entry names one (a
    /// companion slot's impl sub-id). `None` for an entry that is a
    /// declaration rather than a binding.
    Impl: string option
}

/// One `ServerConfig` switch the manifest records, rendered.
type InspectedKnob = {
    [<PiiSafe>]
    Name: string
    [<PiiSafe>]
    Value: string
}

/// A metric's declared canonical-method selector (Phase 694) — the one
/// grounding declaration that changes what an already-enumerated metric
/// MEANS.
type InspectedCanonicalMethod = {
    [<PiiSafe>]
    MetricId: string
    [<PiiSafe>]
    Selector: string
}

/// The **Composition** panel: what this deployment composes.
type CompositionView = {
    /// The manifest's own schema version, echoed so a reader can tell a
    /// pre-canonical-method manifest from a current one.
    SchemaVersion: int
    Modules: InspectedComponent list
    CompanionSlots: InspectedComponent list
    DataTypes: InspectedComponent list
    Tools: InspectedComponent list
    Metrics: InspectedComponent list
    Subjects: InspectedComponent list
    Purposes: InspectedComponent list
    ConfigKnobs: InspectedKnob list
    CanonicalMethods: InspectedCanonicalMethod list
}

/// One descriptor from the surface-descriptor family (Phase 581
/// `ModuleSurface` / 588 `HostEnvelope` / 590 `PeerSurface`), carried as
/// its own canonical JSON rather than as a mirrored record.
///
/// JSON rather than a projection, deliberately: the three descriptors
/// have entirely different shapes, all three already define a canonical
/// serialisation that other tooling pins by content hash, and a fourth
/// mirrored shape here would be a fourth thing to keep in step with
/// three moving records. The panel renders the JSON and offers it for
/// export, which is what a reviewer wants from a descriptor anyway.
type InspectedSurface = {
    /// `module` / `host` / `peer`.
    Family: string
    /// What the descriptor is about — a module id, the deployment, or a
    /// peer id.
    Subject: string
    /// The descriptor's canonical JSON, exactly as its own `toJson`
    /// produces it.
    Json: string
}

/// The **Surfaces** panel.
///
/// `Note` carries a structural reason for an empty `Descriptors` list —
/// "this deployment captured none", which is different from "this
/// deployment composes none" and must not read as the latter. See
/// `CompositionInspectorHandler` for why no descriptor is captured by
/// the SDK's own composition today.
type SurfacesView = {
    Descriptors: InspectedSurface list
    Note: string option
}

/// One composition invariant rule, as the rule manifest declares it —
/// the declaration, never the executable predicate.
type InspectedRule = {
    Code: string
    /// `error` / `warning`, as the manifest's severity renders.
    Severity: string
    /// `structural` / `external-probe` (Phase 585's rule class).
    RuleClass: string
    Description: string
}

/// One defect the preflight evaluation found. An empty defect list
/// against a non-empty rule list is the panel's pass state, and it is
/// the whole point of the panel: a reviewer reads which rules ran, not
/// merely that nothing failed.
type InspectedDefect = {
    Code: string
    Severity: string
    Message: string
}

/// The **Rules** panel: the rule manifest plus the deployment's own
/// preflight verdict over it.
type RulesView = {
    Rules: InspectedRule list
    Defects: InspectedDefect list
    /// When the snapshot was taken — compose time, which is when the
    /// preflight this reproduces actually ran. Echoed so a reviewer
    /// reading an exported artifact knows which boot it describes.
    CapturedAtUtc: DateTime
}

/// One grounding / disclosure declaration the composition made.
type InspectedDeclaration = {
    /// The declaration's facet, as `GroundingFacet.label` renders it:
    /// `metric-registration` / `subject-registration` /
    /// `purpose-declaration` / `canonical-method` / `disclosure-policy`.
    Facet: string
    /// The declaration's subject — a metric id, a subject-hierarchy id,
    /// a purpose id, or an egress-surface name.
    Subject: string
    /// The declared value, rendered.
    Value: string
}

/// The **Disclosure** panel: the grounding envelope this composition
/// projects, and the digest a boot seal binds it under.
type DisclosureView = {
    SchemaVersion: int
    Declarations: InspectedDeclaration list
    /// SHA-256 (lowercase hex) over the envelope's canonical form — the
    /// same digest `GroundingEnvelope.digest` produces, so a reviewer
    /// can match an exported panel against a recorded boot seal without
    /// re-deriving anything.
    Digest: string
}

/// The **Provenance** panel's substrate report.
///
/// Availability rather than an answer: the walk itself is served by
/// `IProvenanceQueryApi` (see this file's header). What the panel needs
/// from the inspector is whether that contract has anything behind it,
/// so an empty result reads as "this deployment composes no provenance
/// graph" rather than as "nothing was found".
type ProvenanceView = {
    /// An `IProvenanceGraph` is registered.
    GraphComposed: bool
    /// An `IFactEvidenceSource` is registered — fact roots resolve.
    FactEvidenceComposed: bool
    /// An `IArtifactProvenanceSource` is registered — model-artifact
    /// roots resolve.
    ArtifactProvenanceComposed: bool
}

/// Which panel an export is asked for. A closed set rather than a
/// string, so an export cannot name a panel that does not exist.
type InspectorPanel =
    | CompositionPanel
    | SurfacesPanel
    | RulesPanel
    | DisclosurePanel
    | ProvenancePanel

module InspectorPanel =
    /// Every panel, in the order the module lists them.
    let all: InspectorPanel list = [
        CompositionPanel
        SurfacesPanel
        RulesPanel
        DisclosurePanel
        ProvenancePanel
    ]

    /// Stable lowercase slug — the panel's route segment and the stem of
    /// its exported filename. Wire tokens, never localised.
    let slug =
        function
        | CompositionPanel -> "composition"
        | SurfacesPanel -> "surfaces"
        | RulesPanel -> "rules"
        | DisclosurePanel -> "disclosure"
        | ProvenancePanel -> "provenance"

/// Read-only admin surface over the deployment's own governance
/// declarations. See this file's header for the gate, the projection
/// rationale, and why the provenance walk is not a method here.
type ICompositionInspectorApi = {
    /// The composition manifest, projected.
    [<RequiresClaim "scope">]
    GetComposition: unit -> Async<Result<CompositionView, string>>

    /// The surface-descriptor family, where the composition captured
    /// any.
    [<RequiresClaim "scope">]
    GetSurfaces: unit -> Async<Result<SurfacesView, string>>

    /// The invariant rule manifest plus this deployment's preflight
    /// verdict over it.
    [<RequiresClaim "scope">]
    GetRules: unit -> Async<Result<RulesView, string>>

    /// The grounding / disclosure envelope and its digest.
    [<RequiresClaim "scope">]
    GetDisclosure: unit -> Async<Result<DisclosureView, string>>

    /// Whether the provenance substrate is composed. The walk is
    /// `IProvenanceQueryApi`'s.
    [<RequiresClaim "scope">]
    GetProvenance: unit -> Async<Result<ProvenanceView, string>>

    /// The canonical JSON of one panel — the artifact the reviewer takes
    /// away.
    ///
    /// Returns the JSON of the SAME projected view the panel's own
    /// getter returns, serialised with the SDK's canonical converter
    /// set, so an export round-trips back into the view type by
    /// construction rather than by a second rendering that could drift.
    ///
    /// `[<Audit "DataExported">]` for the reason `IAuditViewApi.ExportCsv`
    /// carries it: the remoting dispatcher's audit interceptor writes the
    /// `RemotingMethodAudited` row, so an export of the governance story
    /// records itself without any emission code here that could be
    /// forgotten.
    [<RequiresClaim "scope">]
    [<Audit "DataExported">]
    ExportPanel: InspectorPanel -> Async<Result<string, string>>
}

module CompositionInspectorApi =
    /// The version of the projected view shapes in this file. Bumped
    /// when a view record gains or loses a field, so a client can refuse
    /// a snapshot it cannot read rather than decoding half of one.
    [<Literal>]
    let ViewSchemaVersion = 1

    /// Fable.Remoting route builder. Mirrors `AuditViewApi` /
    /// `UsageQueryApi`'s `/api/_platform/<area>/<method>` shape, so the
    /// platform's admin surfaces stay discoverable by path alone.
    let routeBuilder (_typeName: string) (methodName: string) =
        sprintf "/api/_platform/composition/%s" methodName

    /// Suggested filename for an exported panel — `composition.json`,
    /// `rules.json`, … Declared here rather than in the module so the
    /// name an operator receives is part of the contract a test can
    /// pin, not a client-side detail.
    let exportFileName (panel: InspectorPanel) : string =
        sprintf "%s.json" (InspectorPanel.slug panel)