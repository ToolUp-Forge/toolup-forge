// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Cross-version composition upgrade planner ────────────────────────
//
// "What does moving to the next SDK version mean for **this** app?",
// answered from two data artefacts, offline, before a package pin is
// touched.
//
// [Phase 286] diffs two `CompositionManifest`s — two *instances* of the
// same vocabulary. This is that same id-keyed structural diff applied
// **across versions**: one side is the composed instance (its [Phase 280]
// manifest + its [Phase 288] package/version provenance), the other is the
// [Phase 293] `ComposableSurface` of a NEWER forge / companion version set.
// The output is a typed `CompositionUpgradePlan`: which composed slots'
// contracts moved, which [Phase 292] descriptor schema migrations the jump
// crosses and in what order, and — where one exists — the migration doc
// that covers each step.
//
// **The target is DATA, not binaries.** `ComposableSurface` is a plain
// record the newer forge can serialise and export, so planning never
// requires the new packages restored, loaded, or even downloaded. A file
// emitted by the target version is enough — which is the whole point: the
// question is asked *before* the pin moves, when the new binaries are by
// definition not present.
//
// **The Phase 286 core does the pairing.** Rather than re-implement
// id-keyed matching, each side's slots are projected into the
// `ComponentEntry` shape — `Id` = the [Phase 279] slot id, `Impl` = the
// slot's CONTRACT fingerprinted to a string — and handed to
// `CompositionDiff.diff`. A slot that vanished lands in
// `CompanionSlotsRemoved`; a slot whose cardinality or substrate
// requirements moved lands in `CompanionSlotsChanged` as an `ImplDelta`.
// The classification (widened vs changed) then reads the two contracts
// directly. Config knobs ride the same core, keyed by knob name.
//
// **Restricted to what this app actually composed.** Both projections are
// filtered to the companion interfaces and knob names the instance's
// manifest enumerates, so a slot the app never composes cannot make its
// plan non-empty. Empty plan ⇔ no composed slot is affected.
//
// **Blocking is named per composed component, not per slot.** A multi-impl
// slot with three composed sinks yields three steps, each carrying its own
// `ComponentId` and its own [Phase 288] provenance, because "which of my
// components breaks" is the question an operator is actually asking.
//
// **Honest about what it cannot judge.** A composed companion interface
// that neither surface declares as a slot (a companion that wraps the
// composition rather than filling a `ServerApp` field) is reported in
// `PlanUnjudgedComponents` rather than silently counted as unaffected —
// and, being neither a step nor a claim of safety, it does not make the
// plan non-empty.
//
// **Pure + zero cost when unused (GP 11 / GP 13).** Nothing here registers
// into DI, decorates a pipeline, or runs at compose. A consumer that never
// plans an upgrade composes byte-for-byte what it did before; the planner
// is a function that is not invoked.
//
// **Generic substrate (GP 1).** No vendor type, no domain type — only
// `ComponentId`, the manifest / surface records, and strings.

/// A [Phase 293] `ComposableSurface` stamped with the forge version set it
/// was exported from — the artefact the planner's *target* side is. Data
/// end to end: a newer forge serialises this, an older one plans against
/// it without loading a single new assembly.
type ComposableSurfaceSnapshot = {
    /// The forge version this surface was exported from (`0.9.4`), used
    /// verbatim in the plan header and as the fallback migration-doc slug.
    SnapshotVersion: string
    /// The [Phase 292] `CompositionDescriptor.CurrentSchemaVersion` of the
    /// stamped version set — the schema version stored descriptors are
    /// migrated *to* when the pin moves.
    SnapshotDescriptorSchemaVersion: int
    /// The composable vocabulary itself.
    SnapshotSurface: ComposableSurface
}

/// Everything one upgrade plan is computed from (435.A). Build it with
/// `CompositionUpgradePlan.input` and narrow with the `with*` combinators
/// — every field beyond the two required ones has a defaulted value, so
/// the minimal call is `input manifest targetSnapshot |> plan`.
type UpgradePlanInput = {
    /// The composed instance's [Phase 280] manifest — what this app
    /// composed, which is what the plan is restricted to.
    PlanCurrentManifest: CompositionManifest
    /// [Phase 288] provenance keyed by the SAME `ComponentId` the manifest
    /// carries, so each step can name the package/version a composed
    /// component came from. Empty is fine — steps then carry no provenance.
    PlanCurrentProvenance: Map<ComponentId, ComponentProvenance>
    /// The surface the instance is composed against TODAY. Defaults to the
    /// running forge (`CompositionUpgradePlan.currentSnapshot ()`); supply
    /// an exported snapshot instead to plan a jump between two versions
    /// neither of which is the one doing the planning.
    PlanCurrentSurface: ComposableSurfaceSnapshot
    /// The surface of the version set being moved TO.
    PlanTargetSurface: ComposableSurfaceSnapshot
    /// The [Phase 292] schema version of the descriptor this instance
    /// stores. Defaults to the current forge's version — override it when
    /// planning for a descriptor written by an older one.
    PlanDescriptorSchemaVersion: int
    /// Migration-doc slugs that actually exist (a listing of
    /// `docs/migrations/`, with or without the `.md` suffix). A step's
    /// pointer is populated only when its convention-derived slug appears
    /// here — 435.C's "where one exists".
    PlanAvailableMigrationDocs: string list
    /// Per-component migration-doc slugs a caller already knows about,
    /// consulted ahead of the naming convention. Keyed by the composed
    /// `ComponentId` a step is attributed to.
    PlanMigrationDocSlugs: Map<ComponentId, string>
}

/// How loudly one plan step speaks.
type UpgradeStepSeverity =
    /// Additive — the target's contract admits everything the current one
    /// did. Nothing to do; recorded so the jump is legible.
    | UpgradeInformational
    /// The contract moved in a way a consumer should read before pinning.
    | UpgradeAttention
    /// The jump breaks this composition as it stands. Named per composed
    /// component.
    | UpgradeBlocking

/// What one step of an upgrade plan is *about*.
type UpgradePlanStepKind =
    /// A composed slot whose contract only gained: cardinality widened
    /// `SingleImpl` → `MultiImpl`, a substrate requirement dropped, or the
    /// interface became a first-class composable slot in the target.
    | ComposedSlotWidened
    /// A composed slot whose contract narrowed or gained a requirement —
    /// cardinality `MultiImpl` → `SingleImpl`, or a new substrate
    /// interface an implementation's `create` must now receive.
    | ComposedSlotChanged
    /// A composed slot the target surface does not declare at all. The
    /// composition cannot move as it stands.
    | ComposedSlotRemoved
    /// A composition-shaping config knob whose admissible values only
    /// gained, or which became composition-shaping in the target.
    | ComposedKnobWidened
    /// A knob whose admissible values lost members other than the one this
    /// instance set.
    | ComposedKnobChanged
    /// A knob the target no longer declares as composition-shaping.
    | ComposedKnobRemoved
    /// The value this instance sets is no longer admissible in the target.
    | ComposedKnobValueUnavailable
    /// One [Phase 292] descriptor schema hop the stored descriptor crosses
    /// on the way to the target's current schema version. Emitted one per
    /// hop, in ascending version order.
    | DescriptorSchemaMigration
    /// The stored descriptor cannot be migrated to the target at all —
    /// its schema version is newer than the target understands, or it is
    /// not a known version.
    | DescriptorSchemaRejected
    /// The [Phase 293] `ModuleContractShape` moved: the four-file
    /// convention, or — far more seriously — a `ComponentId` slot prefix
    /// every composed id is keyed under.
    | ModuleContractChanged

/// One thing the jump means for this composition. Always returned, never
/// thrown — a plan reports every consequence in one pass.
type UpgradePlanStep = {
    StepKind: UpgradePlanStepKind
    StepSeverity: UpgradeStepSeverity
    /// The stable, greppable code (`upgrade-slot-removed`, …) — the token
    /// a CI gate or a changelog generator matches on.
    StepCode: string
    /// What the step is about in the surface's own vocabulary: the
    /// companion interface name, the knob name, or the schema hop label.
    StepSubject: string
    /// The composed component this step is named against. `None` only for
    /// steps that are about the composition as a whole (a schema hop, the
    /// module contract).
    StepComponent: ComponentId option
    /// The [Phase 288] provenance of `StepComponent`, when the input
    /// carried one — "which nupkg, at what version, provides the thing
    /// that breaks".
    StepProvenance: ComponentProvenance option
    /// Readable, actionable detail naming what moved and what to do.
    StepDetail: string
    /// 435.C — the migration doc covering this step, as a repo-relative
    /// `docs/migrations/<slug>.md` path, when one exists. A path
    /// convention, never a hard dependency: `None` simply means no doc was
    /// listed for it.
    StepMigrationDoc: string option
}

/// Can this composition move to the target version?
type UpgradePlanVerdict =
    /// No composed slot, knob, or schema version is affected. The pin can
    /// move on this evidence.
    | UpgradeClean
    /// Steps to read, none blocking.
    | UpgradeReviewable
    /// At least one blocking step — the composition does not move as it
    /// stands.
    | UpgradeBlocked

/// The typed answer to "what does moving to vNext mean for this app":
/// every consequence the two data artefacts imply, attributed per composed
/// component wherever attribution is derivable.
type CompositionUpgradePlan = {
    /// The version stamp planned FROM (the current surface's).
    PlanFromVersion: string
    /// The version stamp planned TO (the target surface's).
    PlanToVersion: string
    PlanVerdict: UpgradePlanVerdict
    /// Every step, deterministically ordered: descriptor schema hops first
    /// in ascending version order (they gate everything else), then the
    /// remaining steps by severity (blocking, attention, informational)
    /// and, within a severity, by code then subject then component id.
    PlanSteps: UpgradePlanStep list
    /// Composed components whose slot was found present and
    /// contract-identical in the target — the positive evidence behind an
    /// empty plan.
    PlanUnaffectedComponents: ComponentId list
    /// Composed components the plan has nothing to say about: their
    /// companion interface is not a composable slot in EITHER surface (a
    /// companion that wraps the composition rather than filling a
    /// `ServerApp` field). Reported rather than silently counted as safe.
    PlanUnjudgedComponents: ComponentId list
    /// Slots the target declares that this instance does not compose —
    /// what the jump makes newly available. Informational; excluded from
    /// the plan's emptiness.
    PlanNewSlots: ComposableSlot list
}

/// Cross-version upgrade planning: the input model (435.A), the plan
/// computation over the [Phase 286] diff core (435.B), migration-doc
/// linkage (435.C), and the human-readable rendering.
module CompositionUpgradePlan =

    // ── Step codes (stable, greppable) ──────────────────────────────────

    [<Literal>]
    let SlotWidenedCode = "upgrade-slot-widened"

    [<Literal>]
    let SlotChangedCode = "upgrade-slot-changed"

    [<Literal>]
    let SlotRemovedCode = "upgrade-slot-removed"

    [<Literal>]
    let KnobWidenedCode = "upgrade-knob-widened"

    [<Literal>]
    let KnobChangedCode = "upgrade-knob-changed"

    [<Literal>]
    let KnobRemovedCode = "upgrade-knob-removed"

    [<Literal>]
    let KnobValueUnavailableCode = "upgrade-knob-value-unavailable"

    [<Literal>]
    let DescriptorMigrationCode = "upgrade-descriptor-migration"

    [<Literal>]
    let DescriptorRejectedCode = "upgrade-descriptor-rejected"

    [<Literal>]
    let ModuleContractCode = "upgrade-module-contract"

    // ── 435.A — the input model ─────────────────────────────────────────

    /// The surface snapshot of the RUNNING forge: its composable
    /// vocabulary, its descriptor schema version, and its assembly version
    /// (resolved through [Phase 288]'s total provenance reader, so a
    /// metadata-less load context yields `unknown` rather than throwing).
    /// This is both the planner's default "from" side and the artefact a
    /// forge exports for an older one to plan against.
    let currentSnapshot () : ComposableSurfaceSnapshot = {
        SnapshotVersion = (ComponentProvenance.forType typeof<CompositionManifest>).Version
        SnapshotDescriptorSchemaVersion = CompositionDescriptor.CurrentSchemaVersion
        SnapshotSurface = ComposableSurface.describe ()
    }

    /// The minimal input: this instance's manifest and the target surface
    /// snapshot. Provenance is empty, the "from" side is the running
    /// forge, the descriptor schema version is the running forge's, and no
    /// migration docs are listed — narrow any of those with the `with*`
    /// combinators below.
    let input (manifest: CompositionManifest) (target: ComposableSurfaceSnapshot) : UpgradePlanInput = {
        PlanCurrentManifest = manifest
        PlanCurrentProvenance = Map.empty
        PlanCurrentSurface = currentSnapshot ()
        PlanTargetSurface = target
        PlanDescriptorSchemaVersion = CompositionDescriptor.CurrentSchemaVersion
        PlanAvailableMigrationDocs = []
        PlanMigrationDocSlugs = Map.empty
    }

    let withProvenance
        (provenance: Map<ComponentId, ComponentProvenance>)
        (planInput: UpgradePlanInput)
        : UpgradePlanInput =
        {
            planInput with
                PlanCurrentProvenance = provenance
        }

    let withCurrentSurface (snapshot: ComposableSurfaceSnapshot) (planInput: UpgradePlanInput) : UpgradePlanInput = {
        planInput with
            PlanCurrentSurface = snapshot
    }

    let withDescriptorSchemaVersion (version: int) (planInput: UpgradePlanInput) : UpgradePlanInput = {
        planInput with
            PlanDescriptorSchemaVersion = version
    }

    let withAvailableMigrationDocs (slugs: string list) (planInput: UpgradePlanInput) : UpgradePlanInput = {
        planInput with
            PlanAvailableMigrationDocs = slugs
    }

    let withMigrationDocSlugs (slugs: Map<ComponentId, string>) (planInput: UpgradePlanInput) : UpgradePlanInput = {
        planInput with
            PlanMigrationDocSlugs = slugs
    }

    // ── 435.C — migration-doc linkage ───────────────────────────────────

    /// The repo-relative path convention for a migration doc slug.
    let migrationDocPath (slug: string) : string = sprintf "docs/migrations/%s.md" slug

    /// Normalise a listed doc entry to its slug: a bare slug, a file name,
    /// and a full path all reduce to the same token, so a caller can hand
    /// over a raw directory listing.
    ///
    /// Only a trailing `.md` is stripped — deliberately NOT
    /// `Path.GetFileNameWithoutExtension`, which reads a version slug like
    /// `1.0.0` as a file with a `.0` extension and silently truncates it to
    /// `1.0`. Version stamps are the default slug here, so that shape is the
    /// common case, not an edge one.
    let private docSlug (entry: string) : string =
        if String.IsNullOrWhiteSpace entry then
            ""
        else
            let trimmed = entry.Trim().Replace('\\', '/')
            let name = trimmed.Substring(trimmed.LastIndexOf('/') + 1)

            if name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) then
                name.Substring(0, name.Length - 3)
            else
                name

    /// The first candidate slug that is actually available, as a path.
    /// `None` when none is — a pointer is a convention, never a claim that
    /// the file exists.
    let private resolveDoc (available: Set<string>) (candidates: string list) : string option =
        candidates
        |> List.tryFind (fun slug -> not (String.IsNullOrWhiteSpace slug) && available.Contains slug)
        |> Option.map migrationDocPath

    // ── Contract fingerprinting (the Phase 286 projection) ──────────────

    let private cardinalityLabel (cardinality: SlotCardinality) =
        match cardinality with
        | SingleImpl -> "single"
        | MultiImpl -> "multi"

    /// A slot's contract as one comparable string — cardinality plus its
    /// sorted substrate requirements. Carried in the projected entry's
    /// `Impl` field so the [Phase 286] core surfaces a contract move as an
    /// `ImplDelta` without knowing anything about slots.
    let private contractFingerprint (slot: ComposableSlot) : string =
        let requirements =
            slot.SubstrateRequirements
            |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
            |> String.concat ","

        sprintf "%s|%s" (cardinalityLabel slot.Cardinality) requirements

    let private slotEntry (slot: ComposableSlot) : ComponentEntry = {
        Id = slot.Slot
        Kind = CompanionComponent
        Label = slot.Interface
        Impl = Some(contractFingerprint slot)
    }

    /// Is the move from `before` to `after` purely additive for a
    /// consumer? Cardinality may widen (`SingleImpl` → `MultiImpl`) but
    /// never narrow, and the target may drop substrate requirements but
    /// never add one.
    let private isWidening (before: ComposableSlot) (after: ComposableSlot) : bool =
        let cardinalityOk =
            match before.Cardinality, after.Cardinality with
            | MultiImpl, SingleImpl -> false
            | _ -> true

        let beforeRequirements = before.SubstrateRequirements |> Set.ofList
        let afterRequirements = after.SubstrateRequirements |> Set.ofList

        cardinalityOk && Set.isSubset afterRequirements beforeRequirements

    /// The composed companion interfaces, in first-seen order, paired with
    /// the composed entries that address each. A composed id that is not a
    /// companion id (a module, datatype, tool, metric, subject) is not a
    /// slot and never appears.
    let private composedInterfaces (manifest: CompositionManifest) : (string * ComponentEntry list) list =
        manifest.CompanionSlots
        |> List.choose (fun entry ->
            CompositionDryRun.slotInterface entry.Id
            |> Option.map (fun iface -> iface, entry))
        |> List.groupBy fst
        |> List.map (fun (iface, pairs) -> iface, pairs |> List.map snd)
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

    // ── 435.B — plan computation ────────────────────────────────────────

    let private severityRank (severity: UpgradeStepSeverity) =
        match severity with
        | UpgradeBlocking -> 0
        | UpgradeAttention -> 1
        | UpgradeInformational -> 2

    /// Descriptor schema hops (or the refusal), in ascending version
    /// order. Reuses [Phase 292]'s own error rendering, so the planner and
    /// the loader say the same words about the same version gap.
    let private schemaSteps (planInput: UpgradePlanInput) (available: Set<string>) : UpgradePlanStep list =
        let stored = planInput.PlanDescriptorSchemaVersion
        let target = planInput.PlanTargetSurface.SnapshotDescriptorSchemaVersion

        let rejection (error: DescriptorMigrationError) = [
            {
                StepKind = DescriptorSchemaRejected
                StepSeverity = UpgradeBlocking
                StepCode = DescriptorRejectedCode
                StepSubject = sprintf "descriptor-schema-v%d" stored
                StepComponent = None
                StepProvenance = None
                StepDetail =
                    sprintf
                        "%s The stored descriptor cannot be migrated to the target's schema version %d, so this composition does not move as data."
                        (CompositionDescriptorVersion.renderMigrationError error)
                        target
                StepMigrationDoc = resolveDoc available [ planInput.PlanTargetSurface.SnapshotVersion ]
            }
        ]

        if stored < 0 then
            rejection (UnknownDescriptorVersion stored)
        elif stored > target then
            rejection (DescriptorTooNew(stored, target))
        elif stored = target then
            []
        else
            [
                for hop in (stored + 1) .. target ->
                    {
                        StepKind = DescriptorSchemaMigration
                        StepSeverity = UpgradeAttention
                        StepCode = DescriptorMigrationCode
                        StepSubject = sprintf "descriptor-schema-v%d" hop
                        StepComponent = None
                        StepProvenance = None
                        StepDetail =
                            sprintf
                                "The stored CompositionDescriptor migrates from schema version %d to %d on load. Apply this hop before the ones after it — CompositionDescriptorVersion.migrate folds the forward steps in ascending order, so a descriptor several versions behind crosses each in turn."
                                (hop - 1)
                                hop
                        StepMigrationDoc =
                            resolveDoc available [
                                sprintf "descriptor-schema-v%d" hop
                                planInput.PlanTargetSurface.SnapshotVersion
                            ]
                    }
            ]

    /// The [Phase 293] module-contract move, where there is one. A slot
    /// prefix change is blocking: every composed `ComponentId` is keyed
    /// under one, so a prefix that moved invalidates the instance's whole
    /// id space.
    let private moduleContractSteps (planInput: UpgradePlanInput) (available: Set<string>) : UpgradePlanStep list =
        let before = planInput.PlanCurrentSurface.SnapshotSurface.ModuleContract
        let after = planInput.PlanTargetSurface.SnapshotSurface.ModuleContract

        if before = after then
            []
        else
            let prefixesMoved =
                before.ModuleSlotPrefix <> after.ModuleSlotPrefix
                || before.DataTypeSlotPrefix <> after.DataTypeSlotPrefix
                || before.ToolSlotPrefix <> after.ToolSlotPrefix

            [
                {
                    StepKind = ModuleContractChanged
                    StepSeverity = if prefixesMoved then UpgradeBlocking else UpgradeAttention
                    StepCode = ModuleContractCode
                    StepSubject = "module-contract"
                    StepComponent = None
                    StepProvenance = None
                    StepDetail =
                        if prefixesMoved then
                            sprintf
                                "The module ComponentId slot prefixes moved (module '%s' -> '%s', datatype '%s' -> '%s', tool '%s' -> '%s'). Every composed id in this instance's manifest is keyed under the old prefixes, so stored descriptors, leases, and telemetry correlations do not resolve against the target until they are re-keyed."
                                before.ModuleSlotPrefix
                                after.ModuleSlotPrefix
                                before.DataTypeSlotPrefix
                                after.DataTypeSlotPrefix
                                before.ToolSlotPrefix
                                after.ToolSlotPrefix
                        else
                            sprintf
                                "The consumer module file convention moved (%s -> %s). Existing modules keep compiling; new ones follow the target's shape."
                                (before.Files |> String.concat ", ")
                                (after.Files |> String.concat ", ")
                    StepMigrationDoc = resolveDoc available [ planInput.PlanTargetSurface.SnapshotVersion ]
                }
            ]

    /// Per-composed-component steps for one slot verdict. A multi-impl
    /// slot with three composed sinks yields three steps, each named
    /// against its own `ComponentId` and carrying its own [Phase 288]
    /// provenance.
    let private fanOut
        (planInput: UpgradePlanInput)
        (available: Set<string>)
        (entries: ComponentEntry list)
        (kind: UpgradePlanStepKind)
        (severity: UpgradeStepSeverity)
        (code: string)
        (subject: string)
        (detail: string)
        : UpgradePlanStep list =
        entries
        |> List.map (fun entry -> {
            StepKind = kind
            StepSeverity = severity
            StepCode = code
            StepSubject = subject
            StepComponent = Some entry.Id
            StepProvenance = Map.tryFind entry.Id planInput.PlanCurrentProvenance
            StepDetail = detail
            StepMigrationDoc =
                resolveDoc available [
                    match Map.tryFind entry.Id planInput.PlanMigrationDocSlugs with
                    | Some slug -> docSlug slug
                    | None -> ()
                    planInput.PlanTargetSurface.SnapshotVersion
                ]
        })

    /// Compute the upgrade plan (435.B). Pure: two data artefacts in, one
    /// typed plan out, nothing loaded and nothing touched.
    let plan (planInput: UpgradePlanInput) : CompositionUpgradePlan =
        let available =
            planInput.PlanAvailableMigrationDocs
            |> List.map docSlug
            |> List.filter (String.IsNullOrWhiteSpace >> not)
            |> Set.ofList

        let currentSlots =
            planInput.PlanCurrentSurface.SnapshotSurface.Slots
            |> List.map (fun slot -> slot.Interface, slot)
            |> Map.ofList

        let targetSlots =
            planInput.PlanTargetSurface.SnapshotSurface.Slots
            |> List.map (fun slot -> slot.Interface, slot)
            |> Map.ofList

        let composed = composedInterfaces planInput.PlanCurrentManifest
        let composedNames = composed |> List.map fst |> Set.ofList

        // The Phase 286 core does the pairing: project both sides'
        // composed slots into the manifest shape (contract fingerprinted
        // into `Impl`) and diff them by id.
        let restrict (slots: Map<string, ComposableSlot>) =
            composed
            |> List.choose (fun (iface, _) -> Map.tryFind iface slots |> Option.map slotEntry)

        let composedKnobs =
            planInput.PlanCurrentManifest.ConfigKnobs
            |> List.map (fun knob -> knob.Name, knob.Value)
            |> Map.ofList

        let knobProjection (schemas: ConfigKnobSchema list) =
            schemas
            |> List.filter (fun schema -> composedKnobs.ContainsKey schema.Name)
            |> List.map (fun schema -> {
                Name = schema.Name
                Value = schema.Values |> String.concat ","
            })

        let before = {
            CompositionManifest.empty with
                CompanionSlots = restrict currentSlots
                ConfigKnobs = knobProjection planInput.PlanCurrentSurface.SnapshotSurface.ConfigKnobs
        }

        let after = {
            CompositionManifest.empty with
                CompanionSlots = restrict targetSlots
                ConfigKnobs = knobProjection planInput.PlanTargetSurface.SnapshotSurface.ConfigKnobs
        }

        let delta = CompositionDiff.diff before after

        let removedIds = delta.CompanionSlotsRemoved |> List.map _.Id |> Set.ofList
        let addedIds = delta.CompanionSlotsAdded |> List.map _.Id |> Set.ofList
        let changedIds = delta.CompanionSlotsChanged |> List.map _.Id |> Set.ofList

        // ── Slot steps, one per affected composed component ──
        let slotSteps =
            composed
            |> List.collect (fun (iface, entries) ->
                let slotId = ComponentId.forCompanionSlot iface

                if removedIds.Contains slotId then
                    fanOut
                        planInput
                        available
                        entries
                        ComposedSlotRemoved
                        UpgradeBlocking
                        SlotRemovedCode
                        iface
                        (sprintf
                            "Companion slot '%s' is composed by this application but is not a composable slot in %s. The target declares no field for it, so the composition does not move as it stands — drop the companion, or hold the pin until a replacement slot is identified."
                            iface
                            planInput.PlanTargetSurface.SnapshotVersion)
                elif addedIds.Contains slotId then
                    fanOut
                        planInput
                        available
                        entries
                        ComposedSlotWidened
                        UpgradeInformational
                        SlotWidenedCode
                        iface
                        (sprintf
                            "Companion interface '%s' is composed by this application and becomes a first-class composable slot in %s (it is not one in %s). Nothing to do — the composition keeps working and gains descriptor / dry-run / surface coverage it did not have."
                            iface
                            planInput.PlanTargetSurface.SnapshotVersion
                            planInput.PlanCurrentSurface.SnapshotVersion)
                elif changedIds.Contains slotId then
                    match Map.tryFind iface currentSlots, Map.tryFind iface targetSlots with
                    | Some currentSlot, Some targetSlot ->
                        let widened = isWidening currentSlot targetSlot

                        let detail =
                            sprintf
                                "Companion slot '%s' changed contract between %s and %s: cardinality %s -> %s, substrate requirements [%s] -> [%s]. %s"
                                iface
                                planInput.PlanCurrentSurface.SnapshotVersion
                                planInput.PlanTargetSurface.SnapshotVersion
                                (cardinalityLabel currentSlot.Cardinality)
                                (cardinalityLabel targetSlot.Cardinality)
                                (currentSlot.SubstrateRequirements |> String.concat ", ")
                                (targetSlot.SubstrateRequirements |> String.concat ", ")
                                (if widened then
                                     "The move is additive — the target admits everything the current contract did."
                                 else
                                     "The move is not additive: the target either admits fewer implementations or requires substrate the current contract did not. Review the implementation's create signature and its registration before pinning.")

                        fanOut
                            planInput
                            available
                            entries
                            (if widened then ComposedSlotWidened else ComposedSlotChanged)
                            (if widened then UpgradeInformational else UpgradeAttention)
                            (if widened then SlotWidenedCode else SlotChangedCode)
                            iface
                            detail
                    | _ -> []
                else
                    [])

        // ── Config-knob steps ──
        let knobStep (name: string) (kind: UpgradePlanStepKind) (severity: UpgradeStepSeverity) code detail = {
            StepKind = kind
            StepSeverity = severity
            StepCode = code
            StepSubject = name
            StepComponent = None
            StepProvenance = None
            StepDetail = detail
            StepMigrationDoc = resolveDoc available [ planInput.PlanTargetSurface.SnapshotVersion ]
        }

        let knobRemovedSteps =
            delta.ConfigKnobsRemoved
            |> List.map (fun knob ->
                knobStep
                    knob.Name
                    ComposedKnobRemoved
                    UpgradeAttention
                    KnobRemovedCode
                    (sprintf
                        "Config knob '%s' (set to '%s' by this composition) is no longer a composition-shaping enum in %s. Either the knob was withdrawn, or it stopped being a closed value set — read the target's ServerConfig before pinning."
                        knob.Name
                        (composedKnobs |> Map.tryFind knob.Name |> Option.defaultValue "?")
                        planInput.PlanTargetSurface.SnapshotVersion))

        let knobAddedSteps =
            delta.ConfigKnobsAdded
            |> List.map (fun knob ->
                knobStep
                    knob.Name
                    ComposedKnobWidened
                    UpgradeInformational
                    KnobWidenedCode
                    (sprintf
                        "Config knob '%s' becomes a composition-shaping enum in %s, admitting [%s]. This composition already sets it to '%s'."
                        knob.Name
                        planInput.PlanTargetSurface.SnapshotVersion
                        knob.Value
                        (composedKnobs |> Map.tryFind knob.Name |> Option.defaultValue "?")))

        let knobChangedSteps =
            delta.ConfigKnobsChanged
            |> List.collect (fun knob ->
                let currentValue = composedKnobs |> Map.tryFind knob.Name |> Option.defaultValue ""
                let targetValues = knob.After.Split(',') |> Array.toList

                let beforeValues = knob.Before.Split(',') |> Set.ofArray
                let afterValues = knob.After.Split(',') |> Set.ofArray

                if not (List.contains currentValue targetValues) then
                    [
                        knobStep
                            knob.Name
                            ComposedKnobValueUnavailable
                            UpgradeBlocking
                            KnobValueUnavailableCode
                            (sprintf
                                "Config knob '%s' is set to '%s' by this composition, which %s no longer admits (its values are [%s]). The composition does not move until the knob is re-valued."
                                knob.Name
                                currentValue
                                planInput.PlanTargetSurface.SnapshotVersion
                                knob.After)
                    ]
                elif Set.isSubset beforeValues afterValues then
                    [
                        knobStep
                            knob.Name
                            ComposedKnobWidened
                            UpgradeInformational
                            KnobWidenedCode
                            (sprintf
                                "Config knob '%s' gained admissible values in %s ([%s] -> [%s]). The value this composition sets ('%s') is unaffected."
                                knob.Name
                                planInput.PlanTargetSurface.SnapshotVersion
                                knob.Before
                                knob.After
                                currentValue)
                    ]
                else
                    [
                        knobStep
                            knob.Name
                            ComposedKnobChanged
                            UpgradeAttention
                            KnobChangedCode
                            (sprintf
                                "Config knob '%s' lost admissible values in %s ([%s] -> [%s]). The value this composition sets ('%s') survives, but any other deployment of the same descriptor may not."
                                knob.Name
                                planInput.PlanTargetSurface.SnapshotVersion
                                knob.Before
                                knob.After
                                currentValue)
                    ])

        let steps =
            schemaSteps planInput available
            @ moduleContractSteps planInput available
            @ (slotSteps @ knobRemovedSteps @ knobAddedSteps @ knobChangedSteps
               |> List.sortWith (fun a b ->
                   match compare (severityRank a.StepSeverity) (severityRank b.StepSeverity) with
                   | 0 ->
                       match String.CompareOrdinal(a.StepCode, b.StepCode) with
                       | 0 ->
                           match String.CompareOrdinal(a.StepSubject, b.StepSubject) with
                           | 0 ->
                               String.CompareOrdinal(
                                   a.StepComponent |> Option.map ComponentId.value |> Option.defaultValue "",
                                   b.StepComponent |> Option.map ComponentId.value |> Option.defaultValue ""
                               )
                           | other -> other
                       | other -> other
                   | other -> other))

        let judged (iface: string) =
            currentSlots.ContainsKey iface || targetSlots.ContainsKey iface

        let unaffected =
            composed
            |> List.filter (fun (iface, _) ->
                let slotId = ComponentId.forCompanionSlot iface

                judged iface
                && not (removedIds.Contains slotId)
                && not (addedIds.Contains slotId)
                && not (changedIds.Contains slotId))
            |> List.collect snd
            |> List.map _.Id

        let unjudged =
            composed
            |> List.filter (fst >> judged >> not)
            |> List.collect snd
            |> List.map _.Id

        let newSlots =
            planInput.PlanTargetSurface.SnapshotSurface.Slots
            |> List.filter (fun slot -> not (composedNames.Contains slot.Interface))

        let verdict =
            if List.isEmpty steps then
                UpgradeClean
            elif steps |> List.exists (fun step -> step.StepSeverity = UpgradeBlocking) then
                UpgradeBlocked
            else
                UpgradeReviewable

        {
            PlanFromVersion = planInput.PlanCurrentSurface.SnapshotVersion
            PlanToVersion = planInput.PlanTargetSurface.SnapshotVersion
            PlanVerdict = verdict
            PlanSteps = steps
            PlanUnaffectedComponents = unaffected
            PlanUnjudgedComponents = unjudged
            PlanNewSlots = newSlots
        }

    /// Plan the jump for a LIVE composed app: its [Phase 280] manifest and
    /// [Phase 288] provenance are read off the app, the "from" side is the
    /// running forge, and `target` is the snapshot the newer version
    /// exported. The offline half (`input` + `plan`) is the one that
    /// matters — this is the ergonomic call for an admin endpoint or a
    /// preflight that already holds the app.
    let forApp (app: ServerApp) (target: ComposableSurfaceSnapshot) : CompositionUpgradePlan =
        input (ServerApp.compositionManifest app) target
        |> withProvenance (ComponentProvenance.forApp app)
        |> plan

    /// `true` when nothing this application composed is affected by the
    /// jump — no slot, no knob, no schema hop. New slots the instance does
    /// not compose and components the plan could not judge are excluded by
    /// construction: neither is a consequence for this composition.
    let isEmpty (upgradePlan: CompositionUpgradePlan) : bool = List.isEmpty upgradePlan.PlanSteps

    /// `true` when at least one step blocks the jump.
    let isBlocked (upgradePlan: CompositionUpgradePlan) : bool =
        upgradePlan.PlanVerdict = UpgradeBlocked

    /// Every blocking step, in plan order — the "what stops me pinning"
    /// projection, each named against its composed component.
    let blockingSteps (upgradePlan: CompositionUpgradePlan) : UpgradePlanStep list =
        upgradePlan.PlanSteps
        |> List.filter (fun step -> step.StepSeverity = UpgradeBlocking)

    // ── Rendering ───────────────────────────────────────────────────────

    let private severityLabel (severity: UpgradeStepSeverity) =
        match severity with
        | UpgradeBlocking -> "blocking"
        | UpgradeAttention -> "attention"
        | UpgradeInformational -> "additive"

    let private renderStep (step: UpgradePlanStep) : string =
        let attribution =
            match step.StepComponent with
            | Some id -> sprintf " (%s)" (ComponentId.value id)
            | None -> ""

        let provenance =
            match step.StepProvenance with
            | Some p -> sprintf " [%s %s]" p.Package p.Version
            | None -> ""

        let doc =
            match step.StepMigrationDoc with
            | Some path -> sprintf "\n      see %s" path
            | None -> ""

        sprintf
            "  [%s/%s]%s%s %s%s"
            step.StepCode
            (severityLabel step.StepSeverity)
            attribution
            provenance
            step.StepDetail
            doc

    /// A deterministic, human-readable rendering of the plan — the text an
    /// upgrade preflight prints, and the failure message a "can we pin
    /// vNext?" gate raises. An empty plan renders to a single line naming
    /// both versions, so "nothing to do" is as legible as a finding.
    let render (upgradePlan: CompositionUpgradePlan) : string =
        let header =
            match upgradePlan.PlanVerdict with
            | UpgradeClean ->
                sprintf
                    "Upgrade plan %s -> %s: CLEAN — no composed slot, knob, or descriptor schema version is affected (%d composed component(s) verified unaffected)."
                    upgradePlan.PlanFromVersion
                    upgradePlan.PlanToVersion
                    (List.length upgradePlan.PlanUnaffectedComponents)
            | UpgradeReviewable ->
                sprintf
                    "Upgrade plan %s -> %s: REVIEW — %d step(s), none blocking."
                    upgradePlan.PlanFromVersion
                    upgradePlan.PlanToVersion
                    (List.length upgradePlan.PlanSteps)
            | UpgradeBlocked ->
                sprintf
                    "Upgrade plan %s -> %s: BLOCKED — %d step(s), %d blocking."
                    upgradePlan.PlanFromVersion
                    upgradePlan.PlanToVersion
                    (List.length upgradePlan.PlanSteps)
                    (upgradePlan |> blockingSteps |> List.length)

        let unjudged =
            match upgradePlan.PlanUnjudgedComponents with
            | [] -> []
            | ids -> [
                sprintf
                    "  (not judged: %s — composed companions that are not a composable slot in either surface)"
                    (ids |> List.map ComponentId.value |> String.concat ", ")
              ]

        match upgradePlan.PlanSteps, unjudged with
        | [], [] -> header
        | steps, extra -> header + "\n" + ((steps |> List.map renderStep) @ extra |> String.concat "\n")