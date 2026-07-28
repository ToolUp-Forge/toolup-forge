module ToolUp.Platform.Tests.InProcess.CompositionUpgradePlanTests

open System
open System.IO
open System.Reflection
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 435 — cross-version composition upgrade planner ────────────
//
// Covers the acceptance shape: planning against an identical surface
// yields the empty plan (proved twice — once on a fixture, once on the
// Phase 287 golden-file baseline manifest); a fixture target with one
// widened and one removed slot yields the expected steps at the expected
// severities, each blocking finding NAMED per composed component; and a
// descriptor several Phase 292 schema versions behind sequences its hops
// in ascending order.

// ── Fixtures ──────────────────────────────────────────────────────────

let private slot (iface: string) (cardinality: SlotCardinality) (requirements: string list) : ComposableSlot = {
    Slot = ComponentId.forCompanionSlot iface
    Interface = iface
    Cardinality = cardinality
    SubstrateRequirements = requirements
}

let private grounding: GroundingSurface = {
    FactStoreSlot = ComponentId.forCompanionSlot "IFactStore"
    FactStoreInterface = "IFactStore"
    FactStoreModes = [ "NoFactStore" ]
    MetricSlotPrefix = "metric"
    SubjectSlotPrefix = "subject"
    DisclosureDefaults = []
}

let private surfaceOf (slots: ComposableSlot list) (knobs: ConfigKnobSchema list) : ComposableSurface = {
    Slots = slots
    ConfigKnobs = knobs
    ModuleContract = ComposableSurface.moduleContract
    Grounding = grounding
}

let private snapshotOf
    (version: string)
    (schemaVersion: int)
    (slots: ComposableSlot list)
    (knobs: ConfigKnobSchema list)
    : ComposableSurfaceSnapshot =
    {
        SnapshotVersion = version
        SnapshotDescriptorSchemaVersion = schemaVersion
        SnapshotSurface = surfaceOf slots knobs
    }

/// The reference "current" surface: a single-impl blob-storage slot, a
/// multi-impl audit-sink slot requiring a secret store, an uncomposed
/// auth slot, and one enum-like knob.
let private currentSlots = [
    slot "IAuthProvider" SingleImpl []
    slot "IAuditSink" MultiImpl [ "ISecretStore" ]
    slot "IBlobStorage" SingleImpl []
]

let private currentKnobs = [
    {
        Name = "ProcessProfile"
        Values = [ "AllInOne"; "ApiOnly" ]
    }
]

let private currentSnapshot = snapshotOf "0.9.0" 1 currentSlots currentKnobs

/// The composed instance: one blob-storage slot, two audit-sink impls,
/// one module, one knob set to `AllInOne`.
let private composedManifest: CompositionManifest = {
    CompositionManifest.empty with
        Modules = [
            CompositionManifest.moduleEntry ("Orders", ComponentId.ofModule "orders-service")
        ]
        CompanionSlots = [
            CompositionManifest.companionSlotEntry "IBlobStorage"
            CompositionManifest.companionImplEntry "IAuditSink" "primary"
            CompositionManifest.companionImplEntry "IAuditSink" "secondary"
        ]
        ConfigKnobs = [ CompositionManifest.knob "ProcessProfile" "AllInOne" ]
}

let private planFrom (target: ComposableSurfaceSnapshot) =
    CompositionUpgradePlan.input composedManifest target
    |> CompositionUpgradePlan.withCurrentSurface currentSnapshot
    |> CompositionUpgradePlan.plan

let private stepsOfCode (code: string) (plan: CompositionUpgradePlan) =
    plan.PlanSteps |> List.filter (fun step -> step.StepCode = code)

let private componentValues (steps: UpgradePlanStep list) =
    steps |> List.choose _.StepComponent |> List.map ComponentId.value |> List.sort

// ── Phase 287 golden baseline ─────────────────────────────────────────

let private jsonOptions = FableConverters.create ()

/// Repo root (toolup-forge), derived the same way the Phase 175 / 287
/// guards derive it: bin/<Config>/net10.0/…Tests.dll → up 5.
let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private goldenBaselineManifest () : CompositionManifest =
    let path =
        Path.Combine(repoRoot (), "composition-baselines", "composition-baseline.json")

    JsonSerializer.Deserialize<CompositionManifest>(File.ReadAllText path, jsonOptions)

// ── Tests ─────────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 435 — cross-version composition upgrade planner" [

        // ── 435.A — the input model ──

        test "input defaults the from-side to the running forge and carries no provenance" {
            let target = snapshotOf "9.9.9" 1 currentSlots currentKnobs
            let planInput = CompositionUpgradePlan.input composedManifest target

            Expect.equal planInput.PlanTargetSurface target "the target snapshot is carried verbatim"
            Expect.isTrue (Map.isEmpty planInput.PlanCurrentProvenance) "provenance defaults to empty"

            Expect.equal
                planInput.PlanDescriptorSchemaVersion
                CompositionDescriptor.CurrentSchemaVersion
                "the stored descriptor version defaults to the running forge's"

            Expect.equal
                planInput.PlanCurrentSurface.SnapshotDescriptorSchemaVersion
                CompositionDescriptor.CurrentSchemaVersion
                "the from-side snapshot is the running forge's"
        }

        test "currentSnapshot exports the running forge's surface, schema version and package version" {
            let snapshot = CompositionUpgradePlan.currentSnapshot ()

            Expect.equal
                snapshot.SnapshotSurface
                (ComposableSurface.describe ())
                "the snapshot carries the live composable surface"

            Expect.equal
                snapshot.SnapshotDescriptorSchemaVersion
                CompositionDescriptor.CurrentSchemaVersion
                "and the live descriptor schema version"

            Expect.isNotEmpty
                snapshot.SnapshotVersion
                "and a resolved package version (never empty — 'unknown' at worst)"
        }

        // ── 435.B — the empty plan ──

        test "an identical target surface yields the empty plan" {
            let plan = planFrom currentSnapshot

            Expect.isEmpty plan.PlanSteps "nothing composed is affected by a no-op jump"
            Expect.isTrue (CompositionUpgradePlan.isEmpty plan) "isEmpty agrees"
            Expect.equal plan.PlanVerdict UpgradeClean "the verdict is clean"
            Expect.isFalse (CompositionUpgradePlan.isBlocked plan) "nothing blocks"
        }

        test "the empty plan carries every composed component as verified-unaffected" {
            let plan = planFrom currentSnapshot

            Expect.equal
                (plan.PlanUnaffectedComponents |> List.map ComponentId.value |> List.sort)
                [
                    "companion:IAuditSink/primary"
                    "companion:IAuditSink/secondary"
                    "companion:IBlobStorage"
                ]
                "an empty plan is positive evidence, not just an absence of findings"
        }

        test "the Phase 287 golden baseline manifest plans clean against the running forge" {
            // The acceptance's "empty plan ⇔ no composed slot is affected",
            // verified against the committed golden file rather than a
            // fixture: the reference composition, planned against the very
            // surface it was composed on, must have nothing to do.
            let plan =
                CompositionUpgradePlan.input (goldenBaselineManifest ()) (CompositionUpgradePlan.currentSnapshot ())
                |> CompositionUpgradePlan.plan

            Expect.equal
                plan.PlanVerdict
                UpgradeClean
                (sprintf
                    "the golden baseline must plan clean against its own surface:\n%s"
                    (CompositionUpgradePlan.render plan))

            Expect.isNonEmpty
                plan.PlanUnaffectedComponents
                "and the baseline's composed companion slots are accounted for"
        }

        // ── 435.B — widened / changed / removed ──

        test "a removed slot is blocking and is named once per composed component" {
            // IAuditSink vanishes from the target; the instance composes
            // two impls of it.
            let target =
                snapshotOf
                    "1.0.0"
                    1
                    [ slot "IAuthProvider" SingleImpl []; slot "IBlobStorage" SingleImpl [] ]
                    currentKnobs

            let plan = planFrom target
            let removed = stepsOfCode CompositionUpgradePlan.SlotRemovedCode plan

            Expect.hasLength removed 2 "one step per composed component, not one per slot"

            Expect.equal
                (componentValues removed)
                [ "companion:IAuditSink/primary"; "companion:IAuditSink/secondary" ]
                "each blocking finding is named against its own composed component"

            Expect.all removed (fun step -> step.StepSeverity = UpgradeBlocking) "a vanished slot blocks the jump"
            Expect.all removed (fun step -> step.StepKind = ComposedSlotRemoved) "kind matches the code"
            Expect.equal plan.PlanVerdict UpgradeBlocked "the plan verdict is blocked"
            Expect.isTrue (CompositionUpgradePlan.isBlocked plan) "isBlocked agrees"
            Expect.hasLength (CompositionUpgradePlan.blockingSteps plan) 2 "both steps are blocking"

            Expect.equal
                (plan.PlanUnaffectedComponents |> List.map ComponentId.value)
                [ "companion:IBlobStorage" ]
                "the untouched slot is still reported unaffected"
        }

        test "a widened slot is additive and does not block" {
            // IBlobStorage widens SingleImpl -> MultiImpl; IAuditSink drops
            // its ISecretStore substrate requirement. Both are relaxations.
            let target =
                snapshotOf
                    "1.0.0"
                    1
                    [
                        slot "IAuthProvider" SingleImpl []
                        slot "IAuditSink" MultiImpl []
                        slot "IBlobStorage" MultiImpl []
                    ]
                    currentKnobs

            let plan = planFrom target
            let widened = stepsOfCode CompositionUpgradePlan.SlotWidenedCode plan

            Expect.hasLength widened 3 "one blob-storage component plus two audit-sink components"

            Expect.all widened (fun step -> step.StepSeverity = UpgradeInformational) "a relaxation is additive"

            Expect.equal plan.PlanVerdict UpgradeReviewable "steps, but nothing blocking"
            Expect.isFalse (CompositionUpgradePlan.isBlocked plan) "a widening never blocks"
        }

        test "one widened and one removed slot in the same target yield both, blocking-first" {
            let target =
                snapshotOf
                    "1.0.0"
                    1
                    [ slot "IAuthProvider" SingleImpl []; slot "IBlobStorage" MultiImpl [] ]
                    currentKnobs

            let plan = planFrom target

            Expect.hasLength (stepsOfCode CompositionUpgradePlan.SlotRemovedCode plan) 2 "IAuditSink removed, per impl"
            Expect.hasLength (stepsOfCode CompositionUpgradePlan.SlotWidenedCode plan) 1 "IBlobStorage widened"

            Expect.equal
                (plan.PlanSteps |> List.map _.StepSeverity)
                [ UpgradeBlocking; UpgradeBlocking; UpgradeInformational ]
                "steps are ordered blocking, attention, informational"

            Expect.equal plan.PlanVerdict UpgradeBlocked "one blocking step blocks the whole plan"
        }

        test "a new substrate requirement is a contract change needing attention, not a widening" {
            let target =
                snapshotOf
                    "1.0.0"
                    1
                    [
                        slot "IAuthProvider" SingleImpl []
                        slot "IAuditSink" MultiImpl [ "ISecretStore" ]
                        slot "IBlobStorage" SingleImpl [ "ISecretStore" ]
                    ]
                    currentKnobs

            let plan = planFrom target
            let changed = stepsOfCode CompositionUpgradePlan.SlotChangedCode plan

            Expect.hasLength changed 1 "the blob-storage slot gained a requirement"
            Expect.equal changed.Head.StepSeverity UpgradeAttention "gaining a requirement is not additive"
            Expect.equal changed.Head.StepKind ComposedSlotChanged "kind matches"
            Expect.stringContains changed.Head.StepDetail "ISecretStore" "the detail names what the target now requires"
            Expect.equal plan.PlanVerdict UpgradeReviewable "attention alone does not block"
        }

        test "a narrowed cardinality is a contract change needing attention" {
            let target =
                snapshotOf
                    "1.0.0"
                    1
                    [
                        slot "IAuthProvider" SingleImpl []
                        slot "IAuditSink" SingleImpl [ "ISecretStore" ]
                        slot "IBlobStorage" SingleImpl []
                    ]
                    currentKnobs

            let plan = planFrom target
            let changed = stepsOfCode CompositionUpgradePlan.SlotChangedCode plan

            Expect.hasLength changed 2 "one per composed audit-sink impl"
            Expect.all changed (fun step -> step.StepSeverity = UpgradeAttention) "multi -> single narrows"
            Expect.stringContains changed.Head.StepDetail "multi -> single" "the detail names the cardinality move"
        }

        test "a composed interface that is a slot only in the target is additive" {
            let manifest = {
                composedManifest with
                    CompanionSlots = [ CompositionManifest.companionSlotEntry "IFactStore" ]
            }

            let target =
                snapshotOf "1.0.0" 1 (slot "IFactStore" SingleImpl [] :: currentSlots) currentKnobs

            let plan =
                CompositionUpgradePlan.input manifest target
                |> CompositionUpgradePlan.withCurrentSurface currentSnapshot
                |> CompositionUpgradePlan.plan

            let widened = stepsOfCode CompositionUpgradePlan.SlotWidenedCode plan

            Expect.hasLength widened 1 "becoming a first-class slot is a widening"
            Expect.equal widened.Head.StepSeverity UpgradeInformational "nothing to do"
        }

        test "a composed companion that is a slot in neither surface is reported unjudged, not unaffected" {
            let manifest = {
                composedManifest with
                    CompanionSlots = [
                        CompositionManifest.companionSlotEntry "IBlobStorage"
                        CompositionManifest.companionSlotEntry "IFactStore"
                    ]
            }

            let plan =
                CompositionUpgradePlan.input manifest currentSnapshot
                |> CompositionUpgradePlan.withCurrentSurface currentSnapshot
                |> CompositionUpgradePlan.plan

            Expect.equal
                (plan.PlanUnjudgedComponents |> List.map ComponentId.value)
                [ "companion:IFactStore" ]
                "a composition-wrapping companion is not silently counted as safe"

            Expect.equal
                (plan.PlanUnaffectedComponents |> List.map ComponentId.value)
                [ "companion:IBlobStorage" ]
                "and it is not counted as verified-unaffected either"

            Expect.isTrue
                (CompositionUpgradePlan.isEmpty plan)
                "an unjudged component is not a consequence — the plan stays empty"
        }

        test "slots the target adds but this instance does not compose are listed, not stepped" {
            let target =
                snapshotOf "1.0.0" 1 (slot "IVectorStore" SingleImpl [] :: currentSlots) currentKnobs

            let plan = planFrom target

            Expect.isTrue (CompositionUpgradePlan.isEmpty plan) "a slot this app never composes cannot affect it"

            Expect.equal
                (plan.PlanNewSlots |> List.map _.Interface |> List.sort)
                [ "IAuthProvider"; "IVectorStore" ]
                "what the jump makes newly available is reported informationally"
        }

        // ── 435.B — config knobs ──

        test "a knob value the target no longer admits is blocking" {
            let target =
                snapshotOf "1.0.0" 1 currentSlots [
                    {
                        Name = "ProcessProfile"
                        Values = [ "ApiOnly" ]
                    }
                ]

            let plan = planFrom target
            let steps = stepsOfCode CompositionUpgradePlan.KnobValueUnavailableCode plan

            Expect.hasLength steps 1 "the composed value vanished"
            Expect.equal steps.Head.StepSeverity UpgradeBlocking "a composition that cannot be re-valued cannot move"
            Expect.equal steps.Head.StepKind ComposedKnobValueUnavailable "kind matches"
            Expect.stringContains steps.Head.StepDetail "AllInOne" "the detail names the value this composition sets"
            Expect.equal plan.PlanVerdict UpgradeBlocked "the plan is blocked"
        }

        test "a knob that only gained values is additive" {
            let target =
                snapshotOf "1.0.0" 1 currentSlots [
                    {
                        Name = "ProcessProfile"
                        Values = [ "AllInOne"; "ApiOnly"; "WorkerOnly" ]
                    }
                ]

            let plan = planFrom target
            let steps = stepsOfCode CompositionUpgradePlan.KnobWidenedCode plan

            Expect.hasLength steps 1 "the value set widened"
            Expect.equal steps.Head.StepSeverity UpgradeInformational "gaining values affects nothing composed"
        }

        test "a knob that lost an unused value needs attention" {
            let target =
                snapshotOf "1.0.0" 1 currentSlots [
                    {
                        Name = "ProcessProfile"
                        Values = [ "AllInOne" ]
                    }
                ]

            let plan = planFrom target
            let steps = stepsOfCode CompositionUpgradePlan.KnobChangedCode plan

            Expect.hasLength steps 1 "the value set narrowed"
            Expect.equal steps.Head.StepSeverity UpgradeAttention "this deployment survives; another descriptor may not"
            Expect.equal plan.PlanVerdict UpgradeReviewable "nothing blocks"
        }

        test "a knob the target no longer declares as composition-shaping needs attention" {
            let target = snapshotOf "1.0.0" 1 currentSlots []
            let plan = planFrom target
            let steps = stepsOfCode CompositionUpgradePlan.KnobRemovedCode plan

            Expect.hasLength steps 1 "the knob left the enum-like schema"
            Expect.equal steps.Head.StepSeverity UpgradeAttention "withdrawn, or no longer a closed value set"
            Expect.equal steps.Head.StepKind ComposedKnobRemoved "kind matches"
        }

        test "a knob this composition never set cannot make its plan non-empty" {
            let target =
                snapshotOf "1.0.0" 1 currentSlots [
                    {
                        Name = "ProcessProfile"
                        Values = [ "AllInOne"; "ApiOnly" ]
                    }
                    {
                        Name = "JobScheduler"
                        Values = [ "NoJobScheduler" ]
                    }
                ]

            Expect.isTrue
                (CompositionUpgradePlan.isEmpty (planFrom target))
                "the plan is restricted to the knobs the manifest enumerates"
        }

        // ── 435.B — Phase 292 descriptor schema sequencing ──

        test "a descriptor two schema versions behind sequences both hops in ascending order" {
            let target = snapshotOf "1.0.0" 3 currentSlots currentKnobs

            let plan =
                CompositionUpgradePlan.input composedManifest target
                |> CompositionUpgradePlan.withCurrentSurface currentSnapshot
                |> CompositionUpgradePlan.withDescriptorSchemaVersion 1
                |> CompositionUpgradePlan.plan

            let hops = stepsOfCode CompositionUpgradePlan.DescriptorMigrationCode plan

            Expect.hasLength hops 2 "one step per schema hop, never one for the whole gap"

            Expect.equal
                (hops |> List.map _.StepSubject)
                [ "descriptor-schema-v2"; "descriptor-schema-v3" ]
                "chained migrations are sequenced in ascending version order"

            Expect.all hops (fun step -> step.StepKind = DescriptorSchemaMigration) "kind matches"

            Expect.all
                hops
                (fun step -> step.StepSeverity = UpgradeAttention)
                "a migration is not a blocker, it is work"

            Expect.equal
                (plan.PlanSteps |> List.truncate 2 |> List.map _.StepSubject)
                [ "descriptor-schema-v2"; "descriptor-schema-v3" ]
                "schema hops lead the plan — they gate everything after them"
        }

        test "a descriptor at the target's schema version needs no migration step" {
            let target = snapshotOf "1.0.0" 1 currentSlots currentKnobs

            let plan =
                CompositionUpgradePlan.input composedManifest target
                |> CompositionUpgradePlan.withCurrentSurface currentSnapshot
                |> CompositionUpgradePlan.withDescriptorSchemaVersion 1
                |> CompositionUpgradePlan.plan

            Expect.isEmpty
                (stepsOfCode CompositionUpgradePlan.DescriptorMigrationCode plan)
                "same version in, same version out — a no-op is not a step"
        }

        test "a descriptor newer than the target's schema version is blocking, in Phase 292's own words" {
            let target = snapshotOf "1.0.0" 1 currentSlots currentKnobs

            let plan =
                CompositionUpgradePlan.input composedManifest target
                |> CompositionUpgradePlan.withCurrentSurface currentSnapshot
                |> CompositionUpgradePlan.withDescriptorSchemaVersion 4
                |> CompositionUpgradePlan.plan

            let rejected = stepsOfCode CompositionUpgradePlan.DescriptorRejectedCode plan

            Expect.hasLength rejected 1 "a version gap the target cannot close"
            Expect.equal rejected.Head.StepSeverity UpgradeBlocking "never silently down-migrated"
            Expect.equal rejected.Head.StepKind DescriptorSchemaRejected "kind matches"

            Expect.stringContains
                rejected.Head.StepDetail
                (CompositionDescriptorVersion.renderMigrationError (DescriptorTooNew(4, 1)))
                "the planner and the loader say the same words about the same gap"
        }

        test "a corrupt descriptor schema version is blocking" {
            let target = snapshotOf "1.0.0" 1 currentSlots currentKnobs

            let plan =
                CompositionUpgradePlan.input composedManifest target
                |> CompositionUpgradePlan.withCurrentSurface currentSnapshot
                |> CompositionUpgradePlan.withDescriptorSchemaVersion -1
                |> CompositionUpgradePlan.plan

            Expect.hasLength (stepsOfCode CompositionUpgradePlan.DescriptorRejectedCode plan) 1 "not a known version"
            Expect.equal plan.PlanVerdict UpgradeBlocked "the composition does not move as data"
        }

        // ── 435.B — the module contract ──

        test "a moved ComponentId slot prefix is blocking; a moved file convention is not" {
            let prefixMoved = {
                currentSnapshot with
                    SnapshotVersion = "1.0.0"
                    SnapshotSurface = {
                        currentSnapshot.SnapshotSurface with
                            ModuleContract = {
                                ComposableSurface.moduleContract with
                                    ModuleSlotPrefix = "component"
                            }
                    }
            }

            let filesMoved = {
                currentSnapshot with
                    SnapshotVersion = "1.0.0"
                    SnapshotSurface = {
                        currentSnapshot.SnapshotSurface with
                            ModuleContract = {
                                ComposableSurface.moduleContract with
                                    Files = [ "SharedTypes"; "Server"; "Client" ]
                            }
                    }
            }

            let blocking = (planFrom prefixMoved).PlanSteps |> List.head
            let attention = (planFrom filesMoved).PlanSteps |> List.head

            Expect.equal blocking.StepKind ModuleContractChanged "the module contract moved"
            Expect.equal blocking.StepSeverity UpgradeBlocking "every composed id is keyed under the old prefix"

            Expect.equal
                attention.StepSeverity
                UpgradeAttention
                "a file-convention move breaks nothing already composed"
        }

        // ── 435.C — migration-doc linkage ──

        test "a step's migration-doc pointer follows the path convention when the doc is listed" {
            let target = snapshotOf "1.0.0" 1 [ slot "IBlobStorage" SingleImpl [] ] currentKnobs

            let plan =
                CompositionUpgradePlan.input composedManifest target
                |> CompositionUpgradePlan.withCurrentSurface currentSnapshot
                |> CompositionUpgradePlan.withAvailableMigrationDocs [ "1.0.0.md" ]
                |> CompositionUpgradePlan.plan

            Expect.all
                plan.PlanSteps
                (fun step -> step.StepMigrationDoc = Some "docs/migrations/1.0.0.md")
                "the listed target-version doc is resolved by convention, .md suffix and all"
        }

        test "an unlisted doc yields no pointer — a convention, never a hard dependency" {
            let target = snapshotOf "1.0.0" 1 [ slot "IBlobStorage" SingleImpl [] ] currentKnobs
            let plan = planFrom target

            Expect.isNonEmpty plan.PlanSteps "there are steps to point at"
            Expect.all plan.PlanSteps (fun step -> step.StepMigrationDoc = None) "no doc listed, no pointer invented"
        }

        test "a per-component doc slug wins over the version convention" {
            let target = snapshotOf "1.0.0" 1 [ slot "IBlobStorage" SingleImpl [] ] currentKnobs

            let plan =
                CompositionUpgradePlan.input composedManifest target
                |> CompositionUpgradePlan.withCurrentSurface currentSnapshot
                |> CompositionUpgradePlan.withAvailableMigrationDocs [ "1.0.0"; "435-upgrade-planner" ]
                |> CompositionUpgradePlan.withMigrationDocSlugs (
                    Map.ofList [ ComponentId.forCompanionImpl "IAuditSink" "primary", "435-upgrade-planner" ]
                )
                |> CompositionUpgradePlan.plan

            let pinned =
                plan.PlanSteps
                |> List.find (fun step ->
                    step.StepComponent = Some(ComponentId.forCompanionImpl "IAuditSink" "primary"))

            let byConvention =
                plan.PlanSteps
                |> List.find (fun step ->
                    step.StepComponent = Some(ComponentId.forCompanionImpl "IAuditSink" "secondary"))

            Expect.equal pinned.StepMigrationDoc (Some "docs/migrations/435-upgrade-planner.md") "the pinned slug wins"

            Expect.equal
                byConvention.StepMigrationDoc
                (Some "docs/migrations/1.0.0.md")
                "the rest fall back to the convention"
        }

        test "migrationDocPath is the published convention" {
            Expect.equal
                (CompositionUpgradePlan.migrationDocPath "435-upgrade-planner")
                "docs/migrations/435-upgrade-planner.md"
                "one place the path shape is defined"
        }

        // ── Provenance + rendering ──

        test "a step carries the Phase 288 provenance of the component it names" {
            let target = snapshotOf "1.0.0" 1 [ slot "IBlobStorage" SingleImpl [] ] currentKnobs

            let provenance =
                Map.ofList [
                    ComponentId.forCompanionImpl "IAuditSink" "primary",
                    {
                        Package = "ToolUp.AuditSinks.S3Archive"
                        Version = "0.9.0.0"
                        Assembly = "ToolUp.AuditSinks.S3Archive, Version=0.9.0.0"
                    }
                ]

            let plan =
                CompositionUpgradePlan.input composedManifest target
                |> CompositionUpgradePlan.withCurrentSurface currentSnapshot
                |> CompositionUpgradePlan.withProvenance provenance
                |> CompositionUpgradePlan.plan

            let named =
                plan.PlanSteps
                |> List.find (fun step ->
                    step.StepComponent = Some(ComponentId.forCompanionImpl "IAuditSink" "primary"))

            Expect.equal
                (named.StepProvenance |> Option.map _.Package)
                (Some "ToolUp.AuditSinks.S3Archive")
                "which nupkg provides the thing that breaks"

            let unknown =
                plan.PlanSteps
                |> List.find (fun step ->
                    step.StepComponent = Some(ComponentId.forCompanionImpl "IAuditSink" "secondary"))

            Expect.isNone unknown.StepProvenance "a component with no declared provenance carries none"
        }

        test "the empty plan renders to a single legible line" {
            let rendered = CompositionUpgradePlan.render (planFrom currentSnapshot)

            Expect.stringContains rendered "CLEAN" "nothing to do is as legible as a finding"
            Expect.isFalse (rendered.Contains "\n") "and fits on one line"
        }

        test "a blocked plan renders its verdict, its components and its doc pointers" {
            let target = snapshotOf "1.0.0" 1 [ slot "IBlobStorage" SingleImpl [] ] currentKnobs

            let plan =
                CompositionUpgradePlan.input composedManifest target
                |> CompositionUpgradePlan.withCurrentSurface currentSnapshot
                |> CompositionUpgradePlan.withAvailableMigrationDocs [ "1.0.0" ]
                |> CompositionUpgradePlan.plan

            let rendered = CompositionUpgradePlan.render plan

            Expect.stringContains rendered "BLOCKED" "the verdict leads"
            Expect.stringContains rendered "companion:IAuditSink/primary" "each finding names its component"
            Expect.stringContains rendered CompositionUpgradePlan.SlotRemovedCode "with its greppable code"
            Expect.stringContains rendered "docs/migrations/1.0.0.md" "and its migration-doc pointer"
        }

        test "an unjudged component is surfaced in the rendering of an otherwise-empty plan" {
            let manifest = {
                composedManifest with
                    CompanionSlots = [ CompositionManifest.companionSlotEntry "IFactStore" ]
            }

            let rendered =
                CompositionUpgradePlan.input manifest currentSnapshot
                |> CompositionUpgradePlan.withCurrentSurface currentSnapshot
                |> CompositionUpgradePlan.plan
                |> CompositionUpgradePlan.render

            Expect.stringContains rendered "not judged" "the plan says what it could not judge"
            Expect.stringContains rendered "companion:IFactStore" "naming it"
        }

        // ── The live-app convenience ──

        test "forApp plans a live composition against a target snapshot" {
            let app = ServerApp.empty |> ServerApp.addModule (ServerModule.create "Orders")

            let plan =
                CompositionUpgradePlan.forApp app (CompositionUpgradePlan.currentSnapshot ())

            Expect.equal
                plan.PlanVerdict
                UpgradeClean
                (sprintf
                    "a live app planned against its own surface has nothing to do:\n%s"
                    (CompositionUpgradePlan.render plan))

            Expect.equal
                plan.PlanToVersion
                (CompositionUpgradePlan.currentSnapshot ()).SnapshotVersion
                "the plan header names the target version"
        }
    ]