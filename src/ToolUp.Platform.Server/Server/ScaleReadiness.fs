// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform.ConfigValidation

// ─── Phase 434 — composition scale-readiness planner ──────────────────
//
// Every companion already knows its own deployment posture — Phase 282
// made it a typed value (`CompanionCapability.Readiness`:
// `DistributedReady` | `DevOnly`) instead of file-header prose — but
// nothing JOINS those declarations across a composition. So the question
// an operator actually asks ("can this app run N instances, or
// serverless?") has been answered by reading N file headers and holding
// the result in their head. This file answers it as data: a per-component
// scale finding, a whole-composition verdict that is the MEET of the
// parts, and an opt-in preflight gate.
//
// **Three surfaces, in the shape Phase 431 / 433 settled on:**
//
//   1. **The join (434.A)** — `assess` / `assessWith` walk the Phase 280
//      `CompositionManifest` and resolve each component's declared
//      readiness out of a Phase 296 `CapabilitySignature`, yielding
//      `SingleInstanceOnly` / `MultiInstanceSafe` / `MultiInstanceWith`
//      per component and the meet over all of them as the verdict.
//   2. **Unblock suggestions (434.B)** — for each scale-limiting finding,
//      the swap that would lift it, named from the Phase 293
//      `ComposableSurface` vocabulary. A REPORT LINE, never an auto-swap:
//      the vocabulary knows which slots exist and admit a replacement, so
//      it can say "this slot is swappable" without pretending to know
//      which concrete package a deployment should reach for.
//   3. **The preflight gate (434.C)** — `ScaleReadinessPreflight`, one
//      rule exported in the Phase 294 `CompositionRuleDescriptor`
//      vocabulary, run by a structural-class `IConfigValidator` through
//      the same `serviceRegistration` closure `CompositionValidator`,
//      `EventTopologyPreflight` and `DataFootprintPreflight` return.
//
// **The intent knob already exists — no new config surface (GP 11).** A
// deployment declares its intended topology today, in two `ServerConfig`
// fields: `ReplicaCount` (operator-declared instance count, default `1`)
// and `ServerlessHost` (default `KestrelHost`). `intentOf` reads them, so
// the gate is DORMANT for every deployment on the defaults — the
// single-instance intent is satisfied by any verdict, including a wholly
// dev-only composition, which is exactly right: an in-memory composition
// on one instance is not a defect, it is the normal development shape.
// The gate engages only where the deployment has already SAID it wants
// more than one instance.
//
// **This generalises three existing single-purpose validators.**
// `JobSchedulerInstanceValidator`, `ShareTokenRateLimiterDistribution
// Validator` and `AICancellationDispatchInstanceValidator` each hard-code
// one companion's single-instance assumption against `ReplicaCount`. Each
// was written by hand when someone noticed; the class — a single-instance
// assumption sitting in a production path — is open-ended, and nothing
// enumerated it. Here the same check is
// derived from whatever a composition DECLARED, so a companion that
// declares `DevOnly` is covered without anyone writing a validator for
// it. The three specific validators are deliberately left in place: they
// name their own escape hatches and remediation, and they fire whether or
// not a deployment declares a `CapabilitySignature`.
//
// **Zero runtime machinery; zero cost when unread (GP 13).** Nothing here
// runs per request, decorates anything, or holds state. A deployment that
// declares no `CapabilitySignature` gets an all-`MultiInstanceSafe`
// report (every component resolves to `CompanionCapability.identity`,
// whose readiness is the `DistributedReady` bottom), and a deployment on
// the default topology registers no validator at all — the composed
// `services` is byte-for-byte what it was.
//
// **Generic substrate (GP 1).** No vendor or domain vocabulary: only
// `ComponentId`, the Phase 282 axes, interface names, and strings.

/// What ONE composed component permits on the scale axis — the Phase 282
/// `Readiness` declaration read as a deployment-topology constraint,
/// widened with the "safe, but only alongside X" middle case a bare
/// two-valued readiness cannot express.
///
/// Ordered as a MEET-semilattice with `MultiInstanceSafe` as the identity
/// (the top — the least constrained, and what an undeclared component
/// contributes) descending to `SingleInstanceOnly` (the bottom — the most
/// constrained, and absorbing). The composition's verdict is the meet of
/// its parts, which is the mirror image of the Phase 296 capability JOIN:
/// there the bottom was the harmless value and one bad part contaminated
/// upward; here the top is the harmless value and one bad part
/// contaminates downward. Same lattice, read from the other end.
type ComponentScale =
    /// Stateless between invocations — N instances of this component can
    /// run concurrently with no further conditions. The meet identity, and
    /// what a component that declared nothing contributes (GP 11).
    | MultiInstanceSafe
    /// Safe across N instances only while the named distributed companions
    /// are ALSO composed — a component whose own code is stateless but
    /// which coordinates through something shared (a distributed lock, a
    /// cross-instance channel, a shared cache). Carries the UNSATISFIED
    /// prerequisites, so an empty list is meaningless: construct through
    /// `ComponentScale.ofNeeds`, which folds the empty list back to
    /// `MultiInstanceSafe` (the same normalisation
    /// `DeterminismSource.ofFactors` applies to its empty factor set).
    | MultiInstanceWith of ComponentId list
    /// Declared `DevOnly` — holds in-memory state or assumes a single
    /// instance. Absorbing: one such component makes the whole composition
    /// single-instance-only.
    | SingleInstanceOnly

/// The deployment topology a composition is being asked to run in — read
/// from `ServerConfig` by `intentOf`, never declared separately.
[<RequireQualifiedAccess>]
type ScaleIntent =
    /// One instance (`ReplicaCount <= 1`, `KestrelHost`) — the default.
    /// Satisfied by every verdict, so the gate is dormant.
    | SingleInstance
    /// N instances of the same build behind a load balancer
    /// (`ReplicaCount = N`). Carries the declared count so a defect can
    /// quote the operator's own number back at them.
    | MultiInstance of instances: int
    /// A serverless host profile (`ServerlessHost = ServerlessHost`) —
    /// instances appear and vanish per invocation, so there is no stable
    /// single instance for process-local state to live in. The strictest
    /// intent; an instance COUNT cannot express it, which is why it is its
    /// own case rather than `MultiInstance Int32.MaxValue`.
    | Serverless

/// One component's scale finding: what it permits, and — for a
/// scale-limiting one — the swap that would lift it (434.B).
type ScaleFinding = {
    /// The Phase 279 identity of the component this finding is about.
    Component: ComponentId
    /// The manifest's display label (module `Name`, companion interface
    /// slot, datatype id, tool name), carried so a reader need not
    /// re-join the manifest to render a report line.
    Label: string
    Scale: ComponentScale
    /// 434.B — the swap that would lift a `SingleInstanceOnly` finding,
    /// where the Phase 293 vocabulary knows a slot to swap in. `None`
    /// when the component is not scale-limiting, or when the vocabulary
    /// knows no swappable slot for it (a module, a datatype, a tool, or a
    /// companion interface this build does not expose as a slot) — an
    /// honest absence, never a fabricated suggestion.
    Unblock: string option
}

/// The whole composition's scale-readiness report: the verdict and the
/// per-component attributions it was derived from. Pure data — produced
/// on demand, held by nothing.
type ScaleReport = {
    /// The meet of every finding. `MultiInstanceSafe` for an empty or
    /// wholly-undeclared composition.
    Verdict: ComponentScale
    /// Every component the join considered, in manifest order.
    Findings: ScaleFinding list
}

/// What a deployment DECLARES about the scale axis: the Phase 282/296
/// capability signature its components' readiness is read from, plus the
/// per-component distributed prerequisites that produce the
/// `MultiInstanceWith` middle case.
///
/// A record rather than two parameters because the family grows: a third
/// declaration joins by growing this constructor once instead of every
/// `assessWith` call site. Both fields empty (`ScaleDeclarations.empty`)
/// makes the whole join a no-op that reports `MultiInstanceSafe`
/// throughout (GP 11 / GP 13).
type ScaleDeclarations = {
    /// Per-component declared `CompanionCapability` (Phase 296). A
    /// component absent from the map resolves to
    /// `CompanionCapability.identity`, whose readiness is
    /// `DistributedReady` — so an undeclared component never constrains
    /// the verdict.
    Capabilities: CapabilitySignature
    /// Per-component distributed prerequisites: "this component is
    /// multi-instance-safe only while these other components are composed
    /// and themselves distributed-ready." Empty — the default — means the
    /// `MultiInstanceWith` case never arises, and a component's scale is
    /// exactly its declared readiness.
    Prerequisites: Map<ComponentId, ComponentId list>
}

module ScaleDeclarations =
    /// The declarations of a deployment that declared nothing — every
    /// component resolves to `MultiInstanceSafe` and no prerequisite is
    /// checked.
    let empty: ScaleDeclarations = {
        Capabilities = Map.empty
        Prerequisites = Map.empty
    }

[<RequireQualifiedAccess>]
module ComponentScale =

    /// Build a `ComponentScale` from a prerequisite list, normalising the
    /// empty list to `MultiInstanceSafe` so `MultiInstanceWith []` never
    /// exists as an alias for the identity and structural equality stays
    /// total. Mirrors `DeterminismSource.ofFactors` (Phase 282).
    /// Prerequisites are deduplicated and ordered by id, so two joins that
    /// saw the same needs in different orders produce equal values.
    let ofNeeds (needs: ComponentId list) : ComponentScale =
        match needs |> List.distinct |> List.sortBy ComponentId.value with
        | [] -> MultiInstanceSafe
        | ns -> MultiInstanceWith ns

    /// The unsatisfied distributed prerequisites this scale names — empty
    /// for both `MultiInstanceSafe` and `SingleInstanceOnly` (the latter is
    /// not blocked on a prerequisite; it is blocked on itself).
    let needs (scale: ComponentScale) : ComponentId list =
        match scale with
        | MultiInstanceWith ns -> ns
        | MultiInstanceSafe
        | SingleInstanceOnly -> []

    /// The scale a single Phase 282 readiness declaration implies, before
    /// prerequisites are considered: `DevOnly` is single-instance-only,
    /// `DistributedReady` is safe.
    let ofReadiness (readiness: Readiness) : ComponentScale =
        match readiness with
        | DevOnly -> SingleInstanceOnly
        | DistributedReady -> MultiInstanceSafe

    /// Componentwise MEET: `SingleInstanceOnly` absorbs, two
    /// `MultiInstanceWith`s union their unsatisfied prerequisites, and
    /// `MultiInstanceSafe` is the identity. Associative + commutative +
    /// idempotent — the mirror of `CompanionCapability.join`, so the
    /// verdict does not depend on the order the parts were composed.
    let meet (a: ComponentScale) (b: ComponentScale) : ComponentScale =
        match a, b with
        | SingleInstanceOnly, _
        | _, SingleInstanceOnly -> SingleInstanceOnly
        | MultiInstanceWith xs, MultiInstanceWith ys -> ofNeeds (xs @ ys)
        | MultiInstanceWith xs, MultiInstanceSafe
        | MultiInstanceSafe, MultiInstanceWith xs -> ofNeeds xs
        | MultiInstanceSafe, MultiInstanceSafe -> MultiInstanceSafe

    /// The meet of a sequence, folded from `MultiInstanceSafe`. An empty
    /// sequence meets to `MultiInstanceSafe` — a composition of nothing
    /// constrains nothing.
    let meetAll (scales: ComponentScale seq) : ComponentScale = Seq.fold meet MultiInstanceSafe scales

    /// A short operator-facing rendering of a scale value.
    let describe (scale: ComponentScale) : string =
        match scale with
        | MultiInstanceSafe -> "multi-instance-safe"
        | MultiInstanceWith needs ->
            sprintf
                "multi-instance only alongside %s"
                (needs |> List.map (ComponentId.value >> sprintf "'%s'") |> String.concat ", ")
        | SingleInstanceOnly -> "single-instance-only"

/// The 434.A join and the 434.B unblock suggestions. Pure: every function
/// is a projection over declared data, and nothing here reads
/// configuration, touches DI, or performs I/O.
[<RequireQualifiedAccess>]
module ScaleReadiness =

    /// The composed component ids — the universe a prerequisite is
    /// resolved against, so a prerequisite naming something this
    /// deployment does not compose is reported as unsatisfied rather than
    /// silently assumed present.
    let private composedIds (manifest: CompositionManifest) : Set<ComponentId> =
        CompositionManifest.allComponents manifest |> List.map _.Id |> Set.ofList

    /// Whether one declared prerequisite is satisfied: it must be composed
    /// AND itself declared `DistributedReady`.
    ///
    /// Deliberately ONE level deep, not recursive. A prerequisite chain
    /// could be walked transitively, but a prerequisite that is itself
    /// blocked on a prerequisite is a shape nothing in the estate declares
    /// yet, and a transitive walk needs cycle handling to be total —
    /// machinery this phase would ship untested and unused. One level is
    /// sound (it never reports a satisfied prerequisite that is
    /// single-instance-only) and terminates by construction.
    let private prerequisiteSatisfied
        (declarations: ScaleDeclarations)
        (composed: Set<ComponentId>)
        (prerequisite: ComponentId)
        : bool =
        composed.Contains prerequisite
        && (CompanionCapability.resolve declarations.Capabilities prerequisite
            |> _.Readiness
            |> (=) DistributedReady)

    /// One component's scale: its declared readiness, narrowed by any
    /// unsatisfied prerequisites. A `SingleInstanceOnly` component is
    /// already at the bottom, so its prerequisites are not consulted —
    /// naming them would misdirect the operator toward composing a
    /// companion when the fix is swapping this one.
    let private scaleOf
        (declarations: ScaleDeclarations)
        (composed: Set<ComponentId>)
        (componentId: ComponentId)
        : ComponentScale =
        let declared =
            CompanionCapability.resolve declarations.Capabilities componentId
            |> _.Readiness
            |> ComponentScale.ofReadiness

        match declared with
        | SingleInstanceOnly -> SingleInstanceOnly
        | MultiInstanceSafe
        | MultiInstanceWith _ ->
            declarations.Prerequisites
            |> Map.tryFind componentId
            |> Option.defaultValue []
            |> List.filter (prerequisiteSatisfied declarations composed >> not)
            |> ComponentScale.ofNeeds

    /// The Phase 293 vocabulary slot a component id belongs to, if any.
    ///
    /// Joins on the vocabulary's OWN slot ids rather than parsing the
    /// `companion:` prefix out of the id: a slot id matches exactly, and a
    /// multi-impl id (`companion:IX/sub`) matches by that prefix plus the
    /// separator. So this cannot drift from `ComponentId`'s derivation the
    /// way a hand-written parser would.
    let private slotOf (vocabulary: ComposableSlot list) (componentId: ComponentId) : ComposableSlot option =
        let id = ComponentId.value componentId

        vocabulary
        |> List.tryFind (fun slot ->
            let slotId = ComponentId.value slot.Slot
            id = slotId || id.StartsWith(slotId + "/", StringComparison.Ordinal))

    /// **434.B.** The swap that would lift a scale-limiting finding, named
    /// from what the Phase 293 vocabulary actually knows: whether the
    /// component fills a composable companion slot, and whether that slot
    /// admits one implementation or many.
    ///
    /// `None` when nothing can honestly be suggested — the component is
    /// not scale-limiting, or it is a module / datatype / tool / an
    /// interface this build exposes no slot for. The vocabulary enumerates
    /// SLOTS, not the concrete packages that fill them, so a suggestion
    /// names the slot and the posture required; it never invents a package
    /// name, and it is a report line rather than an applied change.
    let private unblockFor
        (vocabulary: ComposableSlot list)
        (componentId: ComponentId)
        (scale: ComponentScale)
        : string option =
        match scale with
        | MultiInstanceSafe
        | MultiInstanceWith _ -> None
        | SingleInstanceOnly ->
            slotOf vocabulary componentId
            |> Option.map (fun slot ->
                let cardinality =
                    match slot.Cardinality with
                    | SingleImpl ->
                        sprintf
                            "The '%s' slot takes at most one implementation, so this is a replacement rather than an addition"
                            slot.Interface
                    | MultiImpl ->
                        sprintf
                            "The '%s' slot admits several implementations, so the dev-only one must be REMOVED as well as the distributed one added — one DevOnly implementation still makes the composition single-instance-only"
                            slot.Interface

                let substrate =
                    match slot.SubstrateRequirements with
                    | [] -> ""
                    | reqs ->
                        sprintf
                            " A distributed implementation of this slot typically receives %s through its `create`."
                            (reqs |> List.map (sprintf "'%s'") |> String.concat ", ")

                sprintf
                    "Swap the '%s' companion slot (%s) for an implementation declaring Readiness = DistributedReady. %s.%s"
                    slot.Interface
                    (ComponentId.value slot.Slot)
                    cardinality
                    substrate)

    /// **The 434.A join.** Every composed component's scale finding plus
    /// the composition verdict, over an explicitly-supplied Phase 293
    /// vocabulary and the deployment's own declarations.
    ///
    /// The vocabulary is a parameter rather than read from
    /// `ComposableSurface.slots ()` inside, so the join is a pure function
    /// of its inputs and a test can pin the unblock suggestions against a
    /// fixed vocabulary instead of whatever slots this build happens to
    /// reflect.
    let assessWith
        (vocabulary: ComposableSlot list)
        (declarations: ScaleDeclarations)
        (manifest: CompositionManifest)
        : ScaleReport =
        let composed = composedIds manifest

        let findings =
            CompositionManifest.allComponents manifest
            |> List.map (fun entry ->
                let scale = scaleOf declarations composed entry.Id

                {
                    Component = entry.Id
                    Label = entry.Label
                    Scale = scale
                    Unblock = unblockFor vocabulary entry.Id scale
                })

        {
            Verdict = findings |> List.map _.Scale |> ComponentScale.meetAll
            Findings = findings
        }

    /// `assessWith` over this build's own Phase 293 vocabulary — the
    /// production shape, so an unblock suggestion names a slot this build
    /// really exposes.
    let assessDeclared (declarations: ScaleDeclarations) (manifest: CompositionManifest) : ScaleReport =
        assessWith (ComposableSurface.slots ()) declarations manifest

    /// `assessDeclared` against `ScaleDeclarations.empty` — the base case
    /// for a deployment that declares no capability signature. Every
    /// component resolves to `CompanionCapability.identity`, so the report
    /// is all-`MultiInstanceSafe` and the verdict is
    /// `MultiInstanceSafe`: honest, because a composition that declares
    /// nothing has asserted nothing to the contrary, and unchanged from
    /// pre-434 behaviour (GP 11).
    let assess (manifest: CompositionManifest) : ScaleReport =
        assessDeclared ScaleDeclarations.empty manifest

    /// The findings that constrain the verdict — everything that is not
    /// `MultiInstanceSafe`, in manifest order. Empty exactly when the
    /// verdict is `MultiInstanceSafe`.
    let limitingFindings (report: ScaleReport) : ScaleFinding list =
        report.Findings |> List.filter (fun f -> f.Scale <> MultiInstanceSafe)

    /// **434.B, as report lines.** One line per scale-limiting finding:
    /// what it permits, and the swap that would lift it where the
    /// vocabulary knows one. A finding with no nameable swap says so
    /// rather than going unmentioned — the operator needs to know a
    /// limiting component exists even when the tooling cannot advise on
    /// it.
    let unblockLines (report: ScaleReport) : string list =
        limitingFindings report
        |> List.map (fun finding ->
            let advice =
                match finding.Unblock with
                | Some swap -> swap
                | None ->
                    "No companion swap can be named for this component: the composable-surface vocabulary exposes no slot for it (a module, data type or tool declares its own posture, and is changed in its own registration rather than swapped out)."

            sprintf
                "%s '%s' is %s. %s"
                (ComponentId.value finding.Component)
                finding.Label
                (ComponentScale.describe finding.Scale)
                advice)

    /// The deployment's declared topology intent, read from the two
    /// `ServerConfig` fields that already carry it. `ServerlessHost` wins
    /// over `ReplicaCount`: a serverless host has no stable instance at
    /// all, which is strictly stronger than any replica count, and a
    /// serverless deployment leaving `ReplicaCount` at its `1` default is
    /// the normal shape rather than a contradiction.
    let intentOf (config: ServerConfig) : ScaleIntent =
        match config.ServerlessHost with
        | ServerlessHost -> ScaleIntent.Serverless
        | KestrelHost ->
            if config.ReplicaCount > 1 then
                ScaleIntent.MultiInstance config.ReplicaCount
            else
                ScaleIntent.SingleInstance

    /// Whether an intent asks for more than one concurrent instance — i.e.
    /// whether the gate has anything to say at all. `false` for the
    /// default topology, which is what keeps the gate dormant and the
    /// composed `services` unchanged for every deployment that has not
    /// declared otherwise (GP 11 / GP 13).
    let intentEngaged (intent: ScaleIntent) : bool =
        match intent with
        | ScaleIntent.SingleInstance -> false
        | ScaleIntent.MultiInstance instances -> instances > 1
        | ScaleIntent.Serverless -> true

    /// A short operator-facing rendering of an intent, quoting the
    /// declaration it was read from.
    let describeIntent (intent: ScaleIntent) : string =
        match intent with
        | ScaleIntent.SingleInstance ->
            "a single instance (ServerConfig.ReplicaCount = 1, ServerlessHost = KestrelHost)"
        | ScaleIntent.MultiInstance instances ->
            sprintf "%d instances (ServerConfig.ReplicaCount = %d)" instances instances
        | ScaleIntent.Serverless -> "a serverless host profile (ServerConfig.ServerlessHost = ServerlessHost)"

    /// Whether a composition verdict can satisfy a declared intent.
    ///
    /// A single-instance intent is satisfied by every verdict — including
    /// `SingleInstanceOnly`, which is the whole point: an in-memory
    /// composition running one instance is correct, not defective. An
    /// intent asking for concurrency requires `MultiInstanceSafe`;
    /// `MultiInstanceWith` does NOT satisfy it, because the list it
    /// carries is by construction the prerequisites that are *not*
    /// composed.
    let satisfies (intent: ScaleIntent) (verdict: ComponentScale) : bool =
        if not (intentEngaged intent) then
            true
        else
            verdict = MultiInstanceSafe

/// **434.C — the opt-in scale-readiness preflight gate.** One rule,
/// exported in the Phase 294 `CompositionRuleDescriptor` vocabulary and
/// the Phase 585 classified form, run by a structural-class
/// `IConfigValidator` wired through the Phase 9m aggregator via the same
/// `IServiceCollection -> IServiceCollection` closure
/// `CompositionValidator.serviceRegistration`,
/// `EventTopologyPreflight.serviceRegistration` and
/// `DataFootprintPreflight.serviceRegistration` return.
///
/// Not a `CompositionValidator` rule, for the reason Phase 432 recorded
/// and Phases 431 and 433 repeated: a Phase 281 `CompositionRule` is
/// `manifest -> refs -> string list`, and neither the capability
/// signature, the declared prerequisites, nor the deployment's topology
/// intent is reachable from either argument. Growing that signature would
/// break every rule that ships.
///
/// Structural-class: the check is a pure meet over component entries and
/// declarations already in memory, with no dependency that could be down,
/// so `ServerConfig.SkipPreflight` does not bypass it. An emergency boot
/// taken to ride out an outage should not silently start N instances of a
/// composition that cannot run N instances — and if the operator genuinely
/// wants that, the lever is `ReplicaCount`, which is the declaration the
/// gate reads.
[<RequireQualifiedAccess>]
module ScaleReadinessPreflight =

    /// Stable `IConfigValidator.Name` for the scale-readiness gate.
    /// Structural-class (Phase 585) — `SkipPreflight` does NOT bypass it.
    [<Literal>]
    let ValidatorName = "composition-scale-readiness"

    /// Stable rule code: the composition cannot satisfy the deployment's
    /// declared instance count / host profile.
    [<Literal>]
    let IntentUnsatisfiableRule = "scale-readiness-intent-unsatisfiable"

    /// Phase 294 — the introspectable rule manifest, in the same
    /// `CompositionRuleDescriptor` shape `CompositionValidator.ruleManifest`
    /// exports, so an external pre-build checker reads one vocabulary
    /// across every rule family.
    ///
    /// `DefectError`, unlike the Phase 433 coverage warnings: this is not
    /// a staged-work shape that resolves itself later. The deployment has
    /// declared a topology, and the composition provably cannot run in it
    /// — starting anyway means duplicated jobs, per-instance state that
    /// callers see at random, and rate-limit windows enforced N times.
    /// Fail-fast-with-names is the only honest outcome, and it is reached
    /// only from a declaration the operator made.
    let ruleManifest: CompositionRuleDescriptor list = [
        {
            Code = IntentUnsatisfiableRule
            Severity = DefectError
            Description =
                "A deployment declaring more than one instance (ServerConfig.ReplicaCount > 1) or a serverless host profile (ServerConfig.ServerlessHost = ServerlessHost) must compose only components declaring Readiness = DistributedReady, with every declared distributed prerequisite composed."
        }
    ]

    /// Phase 585 — the same rule with its class. Structural: a pure
    /// in-memory meet over the manifest's component entries and the
    /// deployment's own declarations, with nothing external to be down.
    let classifiedRuleManifest: ClassifiedCompositionRule list =
        ruleManifest
        |> List.map (fun rule -> {
            Code = rule.Code
            Severity = rule.Severity
            Description = rule.Description
            Class = StructuralRule
        })

    /// The defects of a composition against a declared intent. Empty when
    /// the intent is dormant (`SingleInstance`) or the verdict satisfies
    /// it; otherwise ONE defect naming the intent, the verdict, and every
    /// limiting component with its unblock suggestion (434.B) — so the
    /// failure is actionable from the message alone rather than requiring
    /// a second introspection call.
    let defects (intent: ScaleIntent) (report: ScaleReport) : CompositionDefect list =
        if ScaleReadiness.satisfies intent report.Verdict then
            []
        else
            let limiting =
                match ScaleReadiness.unblockLines report with
                | [] ->
                    // Unreachable while `satisfies` requires
                    // `MultiInstanceSafe` and `limitingFindings` returns
                    // every non-safe finding, but stated rather than
                    // assumed: an empty list must not render as a defect
                    // with no named cause.
                    "no component was individually attributed — re-run the assessment against the composed manifest"
                | lines -> lines |> List.map (sprintf "\n  • %s") |> String.concat ""

            [
                {
                    RuleCode = IntentUnsatisfiableRule
                    Severity = DefectError
                    Message =
                        sprintf
                            "This deployment declares %s, but the composition is %s. Scale-limiting components:%s\nEither compose distributed-ready implementations of the components named above, or declare the topology the composition can actually serve (ServerConfig.ReplicaCount = 1 with ServerlessHost = KestrelHost)."
                            (ScaleReadiness.describeIntent intent)
                            (ComponentScale.describe report.Verdict)
                            limiting
                }
            ]

    let private renderDefects (defects: CompositionDefect list) : string =
        defects
        |> List.map (fun d -> sprintf "[%s] %s" d.RuleCode d.Message)
        |> String.concat "\n"

    /// Translate scale defects into a `ValidationResult`: any
    /// `DefectError` aborts, a clean sweep is `Ok`, warnings-only surface
    /// as `Warning`.
    let toValidationResult (defects: CompositionDefect list) : ValidationResult =
        let errors = defects |> List.filter (fun d -> d.Severity = DefectError)

        if not errors.IsEmpty then
            Error(renderDefects errors)
        else
            match defects with
            | [] -> Ok
            | warnings -> Warning(renderDefects warnings)

    /// The structural-class `IConfigValidator` that runs the gate at
    /// preflight.
    type ScaleReadinessValidator(intent: ScaleIntent, report: ScaleReport) =
        interface IConfigValidator with
            member _.Name = ValidatorName
            member _.Timeout = IConfigValidator.defaultTimeout

            member _.Validate() = async { return toValidationResult (defects intent report) }

        interface IStructuralClassValidator

    /// The opt-in registration (434.C): folds the gate into the Phase 9m
    /// aggregator so it runs at the same startup gate as every other
    /// `IConfigValidator`.
    ///
    /// **Nothing is registered unless the deployment declared a topology
    /// that needs checking** (GP 13) — `ReplicaCount <= 1` with
    /// `KestrelHost`, which is every deployment on the defaults, composes
    /// a byte-for-byte identical `services` (GP 11).
    let serviceRegistration (intent: ScaleIntent) (report: ScaleReport) : IServiceCollection -> IServiceCollection =
        fun services ->
            if ScaleReadiness.intentEngaged intent then
                services.AddSingleton<IConfigValidator>(ScaleReadinessValidator(intent, report) :> IConfigValidator)
                |> ignore

            services

    /// `serviceRegistration` over a deployment's own `ServerConfig` and
    /// declarations — the production shape. The intent comes from the
    /// config the app is composing with, so the gate cannot be checking a
    /// topology other than the one being deployed.
    let serviceRegistrationForConfig
        (config: ServerConfig)
        (declarations: ScaleDeclarations)
        (manifest: CompositionManifest)
        : IServiceCollection -> IServiceCollection =
        let intent = ScaleReadiness.intentOf config

        // The report is only built when the gate is engaged: an
        // unengaged deployment must not pay for a manifest walk it will
        // discard (GP 13).
        if ScaleReadiness.intentEngaged intent then
            serviceRegistration intent (ScaleReadiness.assessDeclared declarations manifest)
        else
            id