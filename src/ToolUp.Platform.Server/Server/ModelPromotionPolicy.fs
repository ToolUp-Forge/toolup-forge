// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text
open System.Text.Json
open System.Security.Cryptography
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 645 — registry promotion policies ────────────────────────────
//
// Governed always-on modelling needs "auto-promote only within declared
// tolerances, otherwise queue for human curation" as substrate, not as app
// code. This file is that substrate: a **declared promotion policy** whose
// tolerance predicates over score / comparison output decide, per newly
// registered artifact, whether it is promoted without a human, held for
// one, or refused outright.
//
// **It adds no second state machine.** Every verdict that moves an artifact
// is executed through the Phase 644 author-agnostic seam
// (`ModelTransition.invoke`) with `ModelTransitionAuthor.PolicyVerdict` —
// the case 644 cut for exactly this. The lifecycle graph, the declared-grant
// check and the attributed audit row are therefore written once and judged
// identically whether a human, a peer, or a policy authored the call. What
// this phase adds is the *judgment upstream of* that seam and the durable
// evidence behind it.
//
// **Three things are separated deliberately.**
//
//   1. `PromotionJudge.judge` is **pure and total** — declared tolerances,
//      an observed metric map and an optional incumbent map in, a verdict +
//      per-tolerance evidence out. No store, no clock, nothing ambient, for
//      the reason `ModelTransition.judge` is pure: a fail-safe claim about a
//      policy is only worth making against the SHIPPED function.
//   2. `ModelPromotionPolicyEvaluator` resolves what the pure judge needs
//      (the artifact, the incumbent, the metrics) and executes the verdict.
//      Every resolution failure it meets becomes a *queued* decision, never
//      a promoted one.
//   3. The `PromotionDecision` is stored, so the evidence a verdict rested
//      on outlives the process that computed it — and so the curation queue
//      is a query rather than an operator's memory.
//
// **Fail-safe is structural, not a default value.** No declared policy
// queues. An unevaluable tolerance queues. A raising metric source queues.
// An empty `PromoteWhen` queues — an empty conjunction is *vacuously true*
// in ordinary logic, and reading it that way here would mean a policy
// declaring no promotion tolerances promoted everything. That is the one
// place where the mathematically-natural reading is the dangerous one, so
// it is refused explicitly rather than inherited.
//
// **Supersession is never silent.** An auto-promotion that displaces a
// currently-`Approved` artifact retires the incumbent through the same seam
// and records both the superseded key and the per-metric deltas that
// justified it. Without that, a refresh would change what a consumer
// resolves with nothing anywhere saying so.
//
// **Generic vocabulary throughout (GP 1).** "Promotion policy", "tolerance",
// "metric", "incumbent" — any governed-artifact consumer can declare one;
// nothing here knows what is being modelled.
//
// **Six portability rules (GP 12).**
// 1. *Identity by value* — policies, tolerances, decisions and artifacts are
//    value records keyed by hash strings; no live handle crosses a seam.
// 2. *Async at every boundary* — every evaluator method returns `Async<_>`.
// 3. *Behaviour as data* — the tolerances, their comparators, the metric
//    source and the policy's transition grant are all declared values; a
//    refusal is a typed `PromotionPolicyError`, never an exception.
// 4. *Stateless between calls* — every call carries its scope + artifact
//    key; the evaluator caches nothing across calls.
// 5. *No cross-shard ordering* — decisions are independent per artifact.
// 6. *Precision at the lower bound* — timestamps are `DateTimeOffset`
//    supplied by the caller; identity is an exact SHA-256 key.

/// How one declared tolerance compares an observed metric — the typed
/// comparator half of a tolerance predicate (GP 12 rule 3).
///
/// **Incumbent-relative comparators are first-class, not a special case of
/// the absolute ones.** "Is this fit stable against the one it would
/// replace" is the question a standing refit actually asks, and expressing
/// it as an absolute threshold requires the operator to re-derive the
/// threshold every time the incumbent moves — which is precisely the manual
/// step this phase exists to remove.
///
/// Every case carries exactly one float (`PromotionComparator.bound`), so
/// the evidence record stays flat and an operator surface can render any
/// tolerance without matching on its case.
[<RequireQualifiedAccess>]
type PromotionComparator =
    /// Absolute floor: the observed metric must be `>= threshold`.
    /// Direction-independent — the operator states the inequality directly.
    | AtLeast of threshold: float
    /// Absolute ceiling: the observed metric must be `<= threshold`.
    | AtMost of threshold: float
    /// The observed metric must beat the incumbent's, **in the tolerance's
    /// declared direction**, by more than `margin` (`0.0` = any strict
    /// improvement). The champion-challenger comparison (Phase 456) as a
    /// declared predicate.
    | ImprovesOnIncumbentBy of margin: float
    /// Stability: the observed metric may be worse than the incumbent's, in
    /// the declared direction, by at most `tolerance` — the "a refit is
    /// allowed to drift a little, not a lot" predicate. An improvement
    /// always satisfies it.
    | NoWorseThanIncumbentBy of tolerance: float
    /// Two-sided stability band: `|observed - incumbent|` must be at most
    /// `|incumbent| * fraction`. Direction-independent, because a parameter
    /// that moved sharply in the *favourable* direction is as much a reason
    /// to look as one that moved against — which is what a stability
    /// tolerance is for. An incumbent of exactly `0.0` therefore demands an
    /// exact match, which is the honest reading of a proportional band
    /// around zero rather than a special case worth inventing.
    | WithinFractionOfIncumbent of fraction: float

[<RequireQualifiedAccess>]
module PromotionComparator =

    /// The stable lowercase label recorded on the stored evidence and the
    /// audit trail. Do not rename without a wire-format bump — a decision
    /// record is read long after the code that wrote it.
    let label (comparator: PromotionComparator) : string =
        match comparator with
        | PromotionComparator.AtLeast _ -> "at-least"
        | PromotionComparator.AtMost _ -> "at-most"
        | PromotionComparator.ImprovesOnIncumbentBy _ -> "improves-on-incumbent-by"
        | PromotionComparator.NoWorseThanIncumbentBy _ -> "no-worse-than-incumbent-by"
        | PromotionComparator.WithinFractionOfIncumbent _ -> "within-fraction-of-incumbent"

    /// The single declared number the comparator carries.
    let bound (comparator: PromotionComparator) : float =
        match comparator with
        | PromotionComparator.AtLeast threshold -> threshold
        | PromotionComparator.AtMost threshold -> threshold
        | PromotionComparator.ImprovesOnIncumbentBy margin -> margin
        | PromotionComparator.NoWorseThanIncumbentBy tolerance -> tolerance
        | PromotionComparator.WithinFractionOfIncumbent fraction -> fraction

    /// Does this comparator need an incumbent value to be evaluable at all?
    /// A tolerance that needs one and has none is *unevaluable*, which
    /// queues — it is never quietly treated as satisfied.
    let requiresIncumbent (comparator: PromotionComparator) : bool =
        match comparator with
        | PromotionComparator.AtLeast _
        | PromotionComparator.AtMost _ -> false
        | PromotionComparator.ImprovesOnIncumbentBy _
        | PromotionComparator.NoWorseThanIncumbentBy _
        | PromotionComparator.WithinFractionOfIncumbent _ -> true

/// One declared tolerance predicate: a named metric, which way it improves,
/// and the comparator applied to it.
///
/// `Direction` is carried per tolerance rather than per policy because one
/// policy routinely mixes an error measure with a fit score, and a single
/// policy-level direction would force an operator to declare two policies
/// for one decision.
type PromotionTolerance = {
    /// The metric name to look up in the observed (and incumbent) map. A
    /// name forge looks up — never a statistic forge computes (plan D10).
    Metric: string
    /// Which way this metric improves (Phase 456's `MetricDirection`, reused
    /// rather than re-declared so a comparison and a policy cannot disagree
    /// about what "better" means).
    Direction: MetricDirection
    /// How the observed value is judged.
    Comparator: PromotionComparator
}

/// Where a policy reads the metrics it judges.
///
/// Two cases, both resolvable from already-composed substrate. A third
/// "whatever the caller passes in" case is deliberately absent: a policy
/// whose evidence source is the caller's choice is not a declared policy.
[<RequireQualifiedAccess>]
type PromotionMetricSource =
    /// The artifact's fit-reported diagnostics (Phase 453), carried verbatim
    /// from the fit. Available on every registered artifact with no further
    /// substrate composed.
    | Diagnostics
    /// The most recent stored holdout-evaluation run's provider-computed
    /// metrics (Phase 456) — i.e. score / comparison output. Requires an
    /// `IModelEvaluationRunner` to be composed; a policy declaring it
    /// without one queues rather than promoting on nothing.
    | LatestEvaluation

[<RequireQualifiedAccess>]
module PromotionMetricSource =

    /// Stable label for the stored decision + audit row.
    let label (source: PromotionMetricSource) : string =
        match source with
        | PromotionMetricSource.Diagnostics -> "diagnostics"
        | PromotionMetricSource.LatestEvaluation -> "latest-evaluation"

/// A declared promotion policy — versioned like any other declared config.
///
/// **`Version` is the operator's, not the store's.** A tolerance change that
/// keeps the same `PolicyId` and does not advance `Version` produces
/// decisions indistinguishable from those made under the previous rules, so
/// the recorded evidence stops being able to explain itself. The field
/// exists to make that a visible omission rather than an invisible one.
///
/// **The grant is declared here because a policy's authority is the
/// deployment's statement, not the policy's own claim.** Phase 644 takes the
/// grant as a parameter precisely so an author cannot carry one it controls;
/// a policy record IS a deployment declaration, so this is where the
/// deployment says what its policy may do. `ModelTransitionAuthority.none`
/// (the default) admits nothing — a policy declared without a grant can
/// still judge, and every verdict it reaches is recorded, but it moves
/// nothing. That is a legitimate and useful shape: a dry run.
type PromotionPolicy = {
    /// Stable id recorded as the transition's author (`PolicyVerdict`) and
    /// on every decision. "The policy decided" is only auditable if the
    /// trail names WHICH policy.
    PolicyId: string
    /// The operator's version of the declared rules.
    Version: int
    /// The spec hashes this policy governs. **Empty = every artifact in the
    /// scope** — the deployment-wide default policy.
    SpecHashes: string list
    /// Where the judged metrics come from.
    Source: PromotionMetricSource
    /// A declared floor: every tolerance here must hold, and one that is
    /// evaluable and *unsatisfied* is a `Reject`, not a queue. Empty (the
    /// default) means no floor — nothing is ever auto-rejected.
    RejectUnless: PromotionTolerance list
    /// Every tolerance here must hold for `AutoPromote`. **Empty means
    /// nothing is ever auto-promoted** — see the file header on why the
    /// vacuous-truth reading is refused.
    PromoteWhen: PromotionTolerance list
    /// What this policy is permitted to drive an artifact into (Phase 644).
    /// `Approved` for auto-promotion; `Retired` for rejection AND for
    /// retiring a superseded incumbent.
    Grant: ModelTransitionAuthority
}

[<RequireQualifiedAccess>]
module PromotionPolicy =

    /// The grant a policy that both promotes and supersedes needs:
    /// `Approved` (the promotion) plus `Retired` (the incumbent it
    /// displaces, and any artifact it rejects).
    let promotionGrant: ModelTransitionAuthority =
        ModelTransitionAuthority.ofTargets [
            ModelArtifactStatus.name ModelArtifactStatus.Approved
            ModelArtifactStatus.name ModelArtifactStatus.Retired
        ]

    /// A policy that judges and records but moves nothing — every field at
    /// its safest value: no floor, no promotion tolerances, no grant. Build
    /// a real policy from this so a field added later defaults safe.
    let create (policyId: string) : PromotionPolicy = {
        PolicyId = policyId
        Version = 1
        SpecHashes = []
        Source = PromotionMetricSource.Diagnostics
        RejectUnless = []
        PromoteWhen = []
        Grant = ModelTransitionAuthority.none
    }

    /// Does this policy govern `artifact`?
    let governs (artifact: ModelArtifact) (policy: PromotionPolicy) : bool =
        List.isEmpty policy.SpecHashes
        || List.contains artifact.CompositeKey.SpecHash policy.SpecHashes

    /// Resolve the policy governing `artifact` from the declared set.
    ///
    /// **Specific beats general, then declaration order.** A policy naming
    /// the artifact's spec hash wins over the deployment-wide default; among
    /// equals the first declared wins. Both tiers are decided by data the
    /// deployment controls, so the resolution is deterministic without
    /// needing a tie-break nobody declared. `None` — no policy at all —
    /// queues (the fail-safe default).
    let resolve (policies: PromotionPolicy list) (artifact: ModelArtifact) : PromotionPolicy option =
        let specific =
            policies
            |> List.tryFind (fun p ->
                not (List.isEmpty p.SpecHashes)
                && List.contains artifact.CompositeKey.SpecHash p.SpecHashes)

        match specific with
        | Some policy -> Some policy
        | None -> policies |> List.tryFind (fun p -> List.isEmpty p.SpecHashes)

/// The verdict a policy reached for one artifact.
///
/// Three values, closed. `QueueForCuration` is not a failure case — it is
/// the ordinary outcome of a policy that declines to decide, and it is what
/// every uncertainty in the pipeline collapses to.
[<RequireQualifiedAccess>]
type PromotionPolicyVerdict =
    /// Within every declared promotion tolerance — promote without a human.
    | AutoPromote
    /// Held for human curation. The artifact stays where Phase 453's
    /// lifecycle already puts an unpromoted fit (`Fitted`); nothing moves.
    | QueueForCuration
    /// A declared floor was proven breached — the artifact is retired
    /// rather than left in a queue nobody will clear.
    | Reject

[<RequireQualifiedAccess>]
module PromotionPolicyVerdict =

    /// Stable label recorded on the decision + audit row. Round-trips
    /// through `parse`; do not rename without a wire-format bump.
    let name (verdict: PromotionPolicyVerdict) : string =
        match verdict with
        | PromotionPolicyVerdict.AutoPromote -> "AutoPromote"
        | PromotionPolicyVerdict.QueueForCuration -> "QueueForCuration"
        | PromotionPolicyVerdict.Reject -> "Reject"

    /// Inverse of `name`. `None` for an unknown tag.
    let parse (tag: string) : PromotionPolicyVerdict option =
        match tag with
        | "AutoPromote" -> Some PromotionPolicyVerdict.AutoPromote
        | "QueueForCuration" -> Some PromotionPolicyVerdict.QueueForCuration
        | "Reject" -> Some PromotionPolicyVerdict.Reject
        | _ -> None

    /// The lifecycle status this verdict drives the artifact into, when it
    /// drives one. `QueueForCuration` moves nothing — the artifact stays in
    /// the status the lifecycle already holds an uncurated fit in.
    let target (verdict: PromotionPolicyVerdict) : ModelArtifactStatus option =
        match verdict with
        | PromotionPolicyVerdict.AutoPromote -> Some ModelArtifactStatus.Approved
        | PromotionPolicyVerdict.Reject -> Some ModelArtifactStatus.Retired
        | PromotionPolicyVerdict.QueueForCuration -> None

/// One tolerance's evaluated evidence — the row an operator reads when
/// asking why a verdict was reached.
///
/// `Evaluable` and `Satisfied` are two fields rather than one tri-state
/// because they answer different questions and drive different outcomes:
/// unsatisfied-and-evaluable is a finding, unevaluable is an absence of
/// evidence, and collapsing them would make "we proved this is bad" and "we
/// could not tell" the same row.
type PromotionToleranceCheck = {
    Metric: string
    /// `MetricDirection.name` of the declared direction.
    Direction: string
    /// `PromotionComparator.label`.
    Comparator: string
    /// `PromotionComparator.bound` — the declared number.
    Bound: float
    /// The candidate's metric value. `None` when the metric was absent from
    /// the source map.
    Observed: float option
    /// The incumbent's value for the same metric. `None` when there is no
    /// incumbent, its metrics were unreadable, or the comparator does not
    /// use one.
    Incumbent: float option
    /// Could this tolerance be judged at all?
    Evaluable: bool
    /// Did it hold? Always `false` when `Evaluable` is `false` — an
    /// unjudged tolerance is never counted as met.
    Satisfied: bool
    /// Human-readable one-line detail (logs + operator surfaces).
    Detail: string
}

/// The stored record of one policy evaluation: the verdict, the policy that
/// reached it, the evidence it rested on, and what it moved.
///
/// **This is the durable half of the phase.** A verdict that promoted
/// something leaves an attributed transition row (Phase 644); a verdict that
/// queued leaves no transition at all, so without this record the queue
/// would be an operator's memory and the evidence would be gone the moment
/// the process exited.
type PromotionDecision = {
    /// Deterministic identity over `(scope, artifact)` — one decision record
    /// per artifact, versioned on re-evaluation, so the latest is the one
    /// that stands. See `PromotionDecision.id`.
    DecisionId: string
    /// Scope the decision was made under (GP 4).
    ScopeId: string
    /// Composite-key hash of the judged artifact (Phase 453, plan D5).
    ArtifactKeyHash: string
    /// The policy that judged. `""` when no policy governed the artifact —
    /// the fail-safe queue still records a decision, and pretending a policy
    /// id it does not have would be worse than an empty one.
    PolicyId: string
    /// The judging policy's declared version. `0` when no policy governed.
    PolicyVersion: int
    /// `PromotionMetricSource.label` of the source the metrics came from.
    MetricSource: string
    /// `PromotionPolicyVerdict.name`.
    Verdict: string
    /// Why — one line, always populated.
    Reason: string
    /// The currently-`Approved` artifact this candidate was judged against.
    /// `None` when nothing was approved for the same spec.
    IncumbentKeyHash: string option
    /// Per-tolerance evidence, in declared order: floor tolerances first,
    /// then promotion tolerances.
    Checks: PromotionToleranceCheck list
    /// Did the verdict's transition land? `false` for a queue (which drives
    /// none) and for a transition the seam refused.
    TransitionApplied: bool
    /// The seam's refusal, described. `""` when nothing was refused.
    TransitionRefusal: string
    /// The artifact this promotion superseded — recorded on the promotion
    /// itself, so "what did this displace" never needs reconstructing from
    /// two trails.
    SupersededKeyHash: string option
    /// Why a supersession did not complete. `""` when it did, or when there
    /// was nothing to supersede.
    SupersessionRefusal: string
    /// When the decision was recorded.
    DecidedAt: DateTimeOffset
}

[<RequireQualifiedAccess>]
module PromotionDecision =

    /// Canonical, order-fixed identity string — one decision per
    /// `(scope, artifact)`. Stable; do not reorder.
    let canonical (scopeId: string) (artifactKeyHash: string) : string =
        sprintf "promotion|scope=%s|artifact=%s" scopeId artifactKeyHash

    /// Deterministic decision id — lowercase SHA-256 hex of `canonical`.
    let id (scopeId: string) (artifactKeyHash: string) : string =
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical scopeId artifactKeyHash))
        |> Convert.ToHexStringLower

    /// The per-metric deltas that justified a supersession: every check that
    /// had both an observed and an incumbent value, as
    /// `(metric, observed, incumbent, observed - incumbent)`.
    ///
    /// Derived rather than stored, because a stored delta is a third copy of
    /// two numbers already on the record and the only way for it to be wrong.
    let supersessionDeltas (decision: PromotionDecision) : (string * float * float * float) list =
        decision.Checks
        |> List.choose (fun check ->
            match check.Observed, check.Incumbent with
            | Some observed, Some incumbent -> Some(check.Metric, observed, incumbent, observed - incumbent)
            | _ -> None)

    /// One-line description of the deltas — the rationale a supersession's
    /// transition carries and an operator reads first.
    let describeDeltas (decision: PromotionDecision) : string =
        match supersessionDeltas decision with
        | [] -> "no comparable metric deltas"
        | deltas ->
            deltas
            |> List.map (fun (metric, observed, incumbent, delta) ->
                sprintf "%s %g vs %g (%+g)" metric observed incumbent delta)
            |> String.concat "; "

/// The pure judgment — the whole of this phase's decision logic.
[<RequireQualifiedAccess>]
module PromotionJudge =

    /// A metric value that is present and not NaN. NaN fails closed here
    /// exactly as it does in `MetricDirection.beats`: a comparison against
    /// NaN is false in both directions, so treating it as "present" would
    /// silently mark a tolerance unsatisfied when the honest answer is that
    /// it could not be judged.
    let private usable (metrics: Map<string, float>) (metric: string) : float option =
        match Map.tryFind metric metrics with
        | Some value when not (Double.IsNaN value) -> Some value
        | _ -> None

    /// Judge one tolerance against an observed map and an optional incumbent
    /// map. Pure and total.
    let check
        (observed: Map<string, float>)
        (incumbent: Map<string, float> option)
        (tolerance: PromotionTolerance)
        : PromotionToleranceCheck =
        let observedValue = usable observed tolerance.Metric

        let incumbentValue =
            if PromotionComparator.requiresIncumbent tolerance.Comparator then
                incumbent |> Option.bind (fun m -> usable m tolerance.Metric)
            else
                None

        let baseCheck = {
            Metric = tolerance.Metric
            Direction = MetricDirection.name tolerance.Direction
            Comparator = PromotionComparator.label tolerance.Comparator
            Bound = PromotionComparator.bound tolerance.Comparator
            Observed = observedValue
            Incumbent = incumbentValue
            Evaluable = false
            Satisfied = false
            Detail = ""
        }

        match observedValue with
        | None -> {
            baseCheck with
                Detail = sprintf "metric '%s' is absent or NaN in the observed metrics" tolerance.Metric
          }
        | Some value ->
            let needsIncumbent = PromotionComparator.requiresIncumbent tolerance.Comparator

            match needsIncumbent, incumbentValue with
            | true, None -> {
                baseCheck with
                    Detail =
                        sprintf
                            "comparator '%s' needs an incumbent value for metric '%s' and none is available"
                            (PromotionComparator.label tolerance.Comparator)
                            tolerance.Metric
              }
            | _ ->
                let satisfied =
                    match tolerance.Comparator with
                    | PromotionComparator.AtLeast threshold -> value >= threshold
                    | PromotionComparator.AtMost threshold -> value <= threshold
                    | PromotionComparator.ImprovesOnIncumbentBy margin ->
                        // Phase 456's comparison, reused verbatim so a
                        // policy and a champion-challenger verdict cannot
                        // disagree about the same two numbers.
                        MetricDirection.beats tolerance.Direction margin value (Option.get incumbentValue)
                    | PromotionComparator.NoWorseThanIncumbentBy allowed ->
                        let reference = Option.get incumbentValue

                        match tolerance.Direction with
                        | MetricDirection.HigherIsBetter -> value >= reference - allowed
                        | MetricDirection.LowerIsBetter -> value <= reference + allowed
                    | PromotionComparator.WithinFractionOfIncumbent fraction ->
                        let reference = Option.get incumbentValue
                        abs (value - reference) <= abs reference * fraction

                {
                    baseCheck with
                        Evaluable = true
                        Satisfied = satisfied
                        Detail =
                            sprintf
                                "%s = %g, %s %g%s — %s"
                                tolerance.Metric
                                value
                                (PromotionComparator.label tolerance.Comparator)
                                (PromotionComparator.bound tolerance.Comparator)
                                (match incumbentValue with
                                 | Some reference -> sprintf " (incumbent %g)" reference
                                 | None -> "")
                                (if satisfied then "satisfied" else "not satisfied")
                }

    /// Judge a whole policy. **Pure and total over its inputs** — no store,
    /// no clock, nothing ambient.
    ///
    /// The order is load-bearing and mirrors the seam's:
    ///
    ///   1. *A proven floor breach rejects.* Only an **evaluable and
    ///      unsatisfied** floor tolerance counts — a floor that could not be
    ///      judged never rejects, because `Reject` retires an artifact and
    ///      `Retired` is terminal. Proving something is bad and failing to
    ///      measure it are not the same finding.
    ///   2. *Any unevaluable tolerance queues.* Missing evidence is the
    ///      human's business, in either list.
    ///   3. *A non-empty, fully-satisfied `PromoteWhen` promotes.* Empty
    ///      never promotes — see the file header.
    ///   4. *Everything else queues.*
    let judge
        (policy: PromotionPolicy)
        (observed: Map<string, float>)
        (incumbent: Map<string, float> option)
        : PromotionPolicyVerdict * PromotionToleranceCheck list * string =
        let floorChecks = policy.RejectUnless |> List.map (check observed incumbent)
        let promoteChecks = policy.PromoteWhen |> List.map (check observed incumbent)
        let allChecks = floorChecks @ promoteChecks

        let breached = floorChecks |> List.filter (fun c -> c.Evaluable && not c.Satisfied)

        let unevaluable = allChecks |> List.filter (fun c -> not c.Evaluable)

        if not (List.isEmpty breached) then
            let reason =
                breached
                |> List.map _.Detail
                |> String.concat "; "
                |> sprintf "declared floor breached: %s"

            PromotionPolicyVerdict.Reject, allChecks, reason
        elif not (List.isEmpty unevaluable) then
            let reason =
                unevaluable
                |> List.map _.Detail
                |> String.concat "; "
                |> sprintf "queued — a declared tolerance could not be evaluated: %s"

            PromotionPolicyVerdict.QueueForCuration, allChecks, reason
        elif List.isEmpty policy.PromoteWhen then
            PromotionPolicyVerdict.QueueForCuration,
            allChecks,
            sprintf "queued — policy '%s' declares no promotion tolerances" policy.PolicyId
        elif promoteChecks |> List.forall _.Satisfied then
            let reason =
                promoteChecks
                |> List.map _.Detail
                |> String.concat "; "
                |> sprintf "within every declared tolerance: %s"

            PromotionPolicyVerdict.AutoPromote, allChecks, reason
        else
            let reason =
                promoteChecks
                |> List.filter (fun c -> not c.Satisfied)
                |> List.map _.Detail
                |> String.concat "; "
                |> sprintf "queued — outside declared tolerance: %s"

            PromotionPolicyVerdict.QueueForCuration, allChecks, reason

/// Typed failure of the evaluation *envelope*.
///
/// Deliberately short. Everything that could plausibly be "we could not
/// decide" is a queued decision rather than an error, per the phase's
/// fail-safe rule; these three are the cases where there is no decision to
/// record at all.
[<RequireQualifiedAccess>]
type PromotionPolicyError =
    /// No artifact with that composite-key hash exists in the scope — there
    /// is nothing to judge.
    | ArtifactNotFound
    /// The subject artifact could not be read for a non-`NotFound` reason.
    | RegistryFailure of reason: string
    /// Persisting the decision failed. The verdict is not returned, because
    /// a verdict nobody can read later is not a governed decision.
    | StorageFailure of reason: string

[<RequireQualifiedAccess>]
module PromotionPolicyError =

    /// Human-readable one-line description for logs + job-result messages.
    let describe (error: PromotionPolicyError) : string =
        match error with
        | PromotionPolicyError.ArtifactNotFound -> "model artifact not found"
        | PromotionPolicyError.RegistryFailure reason -> sprintf "model registry read failed: %s" reason
        | PromotionPolicyError.StorageFailure reason -> sprintf "promotion decision storage failure: %s" reason

/// One entry of the curation queue: the artifact awaiting a human, beside
/// the decision that put it there.
type PromotionQueueEntry = {
    Artifact: ModelArtifact
    Decision: PromotionDecision
}

/// The substrate the evaluator runs over.
///
/// `Evaluations` is optional because a policy sourcing `Diagnostics` needs
/// no evaluation runner at all, and requiring one would make the cheapest
/// useful policy the most expensive to compose (GP 13). A policy that
/// declares `LatestEvaluation` without one queues, with the reason saying so.
type PromotionPolicyDeps = {
    Registry: IModelRegistry
    /// Phase 456 runner — the source of holdout metrics. `None` when the
    /// deployment composes none.
    Evaluations: IModelEvaluationRunner option
    /// Where decision records live (the `BlobModelRegistry` idiom — no new
    /// storage concept).
    DataObjects: IDataObjectStore
    Audit: IAuditLog
    /// Supplied rather than read from the clock, so a test can assert a
    /// recorded instant without freezing one globally (GP 12 rule 6).
    Now: unit -> DateTimeOffset
}

/// Storage plumbing for decision records — the `ModelEvaluationStorage`
/// idiom over `IDataObjectStore`: queryable coordinates mirrored into the
/// metadata sidecar, content decoded only for matches.
module private ModelPromotionStorage =

    [<Literal>]
    let DecisionDataType = "toolup.modelpromotiondecision"

    [<Literal>]
    let ArtifactKey = "promotion.artifact"

    [<Literal>]
    let VerdictKey = "promotion.verdict"

    [<Literal>]
    let PolicyKey = "promotion.policy"

    let jsonOptions = FableConverters.create ()

    let mapDataObjectError (e: DataObjectError) : PromotionPolicyError =
        match e with
        | DataObjectError.NotFound -> PromotionPolicyError.StorageFailure "decision record not found"
        | DataObjectError.VersionNotFound v -> PromotionPolicyError.StorageFailure $"record version {v} not found"
        | DataObjectError.DeleteForbidden -> PromotionPolicyError.StorageFailure "record delete forbidden"
        | DataObjectError.PolicyMismatch _ -> PromotionPolicyError.StorageFailure "record versioning policy mismatch"
        | DataObjectError.StorageFailure m -> PromotionPolicyError.StorageFailure m

    /// Persist a decision. `Versioned` — a re-evaluation of the same
    /// artifact appends a fresh version under the same deterministic id, and
    /// reads return the latest, so the queue reflects the standing verdict
    /// rather than every verdict ever reached.
    let save (deps: PromotionPolicyDeps) (decision: PromotionDecision) : Async<Result<unit, PromotionPolicyError>> = async {
        let content = JsonSerializer.SerializeToUtf8Bytes(decision, jsonOptions)

        let metadata =
            Map [
                ArtifactKey, decision.ArtifactKeyHash
                VerdictKey, decision.Verdict
                PolicyKey, decision.PolicyId
            ]

        match!
            deps.DataObjects.Save(
                decision.ScopeId,
                decision.DecisionId,
                content,
                DecisionDataType,
                decision.PolicyId,
                metadata,
                Versioned
            )
        with
        | Ok _ -> return Ok()
        | Error e -> return Error(mapDataObjectError e)
    }

    /// Every stored decision in the scope whose sidecar satisfies
    /// `sidecarMatches`. Undecodable records are skipped — a decision nobody
    /// can read is not a decision that should block a queue read.
    let queryBy
        (deps: PromotionPolicyDeps)
        (scopeId: string)
        (sidecarMatches: Map<string, string> -> bool)
        : Async<PromotionDecision list> =
        async {
            let! objs = deps.DataObjects.ListObjects scopeId

            let candidates =
                objs
                |> List.filter (fun d -> d.DataType = DecisionDataType && sidecarMatches d.Metadata)

            let results = ResizeArray<PromotionDecision>()

            for dobj in candidates do
                match! deps.DataObjects.GetContent(scopeId, dobj.ContentHash) with
                | Ok bytes ->
                    try
                        results.Add(JsonSerializer.Deserialize<PromotionDecision>(bytes, jsonOptions))
                    with _ ->
                        ()
                | Error _ -> ()

            return List.ofSeq results
        }

/// The policy evaluator: judge a newly registered artifact against the
/// declared policies and execute the verdict through the Phase 644 seam.
///
/// **Composed explicitly or not at all (GP 13).** Nothing in forge calls
/// this; a deployment wires it to whatever its "a new artifact exists"
/// moment is — the Phase 599 batch-item registration, a Phase 601
/// re-vintage-triggered refit, or the job handler below. A deployment that
/// never constructs it is byte-for-byte unchanged (GP 11), and Phase 453
/// transitions stay exactly as they were.
type ModelPromotionPolicyEvaluator(policies: PromotionPolicy list, deps: PromotionPolicyDeps) =

    let transitionDeps: ModelTransitionDeps = {
        Registry = deps.Registry
        Audit = deps.Audit
        Now = deps.Now
    }

    /// The metrics a policy judges, for one artifact. `Error` is a reason
    /// string that becomes a queued decision — never a promotion.
    let metricsFor
        (policy: PromotionPolicy)
        (scopeId: string)
        (artifact: ModelArtifact)
        : Async<Result<Map<string, float>, string>> =
        async {
            match policy.Source with
            | PromotionMetricSource.Diagnostics -> return Ok artifact.Diagnostics
            | PromotionMetricSource.LatestEvaluation ->
                match deps.Evaluations with
                | None -> return Error "policy sources 'latest-evaluation' and no IModelEvaluationRunner is composed"
                | Some runner ->
                    let! runs = runner.GetTrackRecord(scopeId, artifact.CompositeKey.Hash)

                    match runs with
                    | [] -> return Error "no stored evaluation run for this artifact"
                    | _ -> return Ok (runs |> List.maxBy _.EvaluatedAt).Metrics
        }

    /// The incumbent: the currently-`Approved` artifact fit from the same
    /// opaque spec.
    ///
    /// **Same spec hash, not same anything else** — that is exactly Phase
    /// 453's "all vintages of one modelling decision", so the incumbent is
    /// the artifact a consumer resolving this model would get today. The
    /// ordering is most-recently-registered first, tie-broken by ordinal
    /// composite-key hash, so two clones resolve the same incumbent from the
    /// same store.
    let incumbentFor (scopeId: string) (artifact: ModelArtifact) : Async<ModelArtifact option> = async {
        let! sameSpec = deps.Registry.QueryBySpecHash(scopeId, artifact.CompositeKey.SpecHash)

        return
            sameSpec
            |> List.filter (fun a ->
                a.Status = ModelArtifactStatus.Approved
                && a.CompositeKey.Hash <> artifact.CompositeKey.Hash)
            |> List.sortWith (fun a b ->
                match compare b.RegisteredAt a.RegisteredAt with
                | 0 -> String.CompareOrdinal(a.CompositeKey.Hash, b.CompositeKey.Hash)
                | ordering -> ordering)
            |> List.tryHead
    }

    /// Drive one transition through the Phase 644 seam as the policy.
    let transition
        (scopeId: string)
        (policy: PromotionPolicy)
        (keyHash: string)
        (target: ModelArtifactStatus)
        (rationale: string)
        : Async<Result<ModelTransitionRecord, ModelTransitionRefusal>> =
        ModelTransition.invoke transitionDeps scopeId policy.Grant {
            ArtifactKey = keyHash
            Target = target
            Author = ModelTransitionAuthor.PolicyVerdict policy.PolicyId
            Rationale = Some rationale
        }

    /// Build the decision a resolution failure produces: always a queue,
    /// never a promotion, and always recorded.
    let queuedDecision
        (scopeId: string)
        (artifactKeyHash: string)
        (policyId: string)
        (policyVersion: int)
        (source: string)
        (incumbent: string option)
        (checks: PromotionToleranceCheck list)
        (reason: string)
        : PromotionDecision =
        {
            DecisionId = PromotionDecision.id scopeId artifactKeyHash
            ScopeId = scopeId
            ArtifactKeyHash = artifactKeyHash
            PolicyId = policyId
            PolicyVersion = policyVersion
            MetricSource = source
            Verdict = PromotionPolicyVerdict.name PromotionPolicyVerdict.QueueForCuration
            Reason = reason
            IncumbentKeyHash = incumbent
            Checks = checks
            TransitionApplied = false
            TransitionRefusal = ""
            SupersededKeyHash = None
            SupersessionRefusal = ""
            DecidedAt = deps.Now()
        }

    /// Persist the decision and emit its audit row, then hand it back.
    ///
    /// The audit row is the **subscription surface** (GP 6): a consumer that
    /// wants to react to promotions attaches an `IAuditSink`, exactly as it
    /// would for any other platform event. No new pub/sub is invented for
    /// this, because one would be a second thing to configure, secure and
    /// replicate for a stream the audit substrate already carries.
    let recordDecision (decision: PromotionDecision) : Async<Result<PromotionDecision, PromotionPolicyError>> = async {
        match! ModelPromotionStorage.save deps decision with
        | Error e -> return Error e
        | Ok() ->
            do!
                deps.Audit.Record(
                    decision.ScopeId,
                    ModelPromotionPolicyEvaluated {
                        CompositeKeyHash = decision.ArtifactKeyHash
                        PolicyId = decision.PolicyId
                        PolicyVersion = decision.PolicyVersion
                        MetricSource = decision.MetricSource
                        Verdict = decision.Verdict
                        Reason = decision.Reason
                        IncumbentKeyHash = decision.IncumbentKeyHash |> Option.defaultValue ""
                        ToleranceCount = List.length decision.Checks
                        TransitionApplied = decision.TransitionApplied
                        ScopeId = decision.ScopeId
                    }
                )

            match decision.SupersededKeyHash with
            | Some superseded when decision.TransitionApplied ->
                do!
                    deps.Audit.Record(
                        decision.ScopeId,
                        ModelArtifactSuperseded {
                            SupersedingKeyHash = decision.ArtifactKeyHash
                            SupersededKeyHash = superseded
                            PolicyId = decision.PolicyId
                            MetricCount = List.length (PromotionDecision.supersessionDeltas decision)
                            Retired = decision.SupersessionRefusal = ""
                            ScopeId = decision.ScopeId
                        }
                    )
            | _ -> ()

            return Ok decision
    }

    /// Evaluate the declared policies against one artifact and execute the
    /// verdict — the "a new artifact exists, decide what happens to it"
    /// entry point.
    ///
    /// Called on registration or refit completion by whatever produced the
    /// artifact. Idempotent in the sense that matters: the decision id is
    /// deterministic per artifact, so a re-evaluation appends a version
    /// rather than accumulating rows, and a verdict whose transition already
    /// landed is refused by the lifecycle graph rather than applied twice.
    member _.Evaluate
        (scopeId: string, artifactKeyHash: string)
        : Async<Result<PromotionDecision, PromotionPolicyError>> =
        async {
            match! deps.Registry.Get(scopeId, artifactKeyHash) with
            | Error ModelRegistryError.NotFound -> return Error PromotionPolicyError.ArtifactNotFound
            | Error e -> return Error(PromotionPolicyError.RegistryFailure(ModelRegistryError.describe e))
            | Ok artifact ->
                match PromotionPolicy.resolve policies artifact with
                | None ->
                    // Fail-safe default: nothing declared, so nothing is
                    // promoted. The decision is still recorded, because "no
                    // policy governed this" is an answer an operator needs and
                    // an absent record is not one.
                    return!
                        recordDecision (
                            queuedDecision
                                scopeId
                                artifactKeyHash
                                ""
                                0
                                ""
                                None
                                []
                                "queued — no promotion policy is declared for this artifact"
                        )
                | Some policy ->
                    let source = PromotionMetricSource.label policy.Source

                    // Every resolution step is inside the guard: a metric source
                    // that raises is a policy evaluation error, and a policy
                    // evaluation error queues (it must never promote).
                    let! resolved = async {
                        try
                            let! observed = metricsFor policy scopeId artifact

                            match observed with
                            | Error reason -> return Choice2Of2 reason
                            | Ok observed ->
                                let! incumbent = incumbentFor scopeId artifact

                                let! incumbentMetrics = async {
                                    match incumbent with
                                    | None -> return None
                                    | Some champion ->
                                        match! metricsFor policy scopeId champion with
                                        | Ok metrics -> return Some metrics
                                        | Error _ -> return None
                                }

                                return Choice1Of2(observed, incumbent, incumbentMetrics)
                        with ex ->
                            return Choice2Of2(sprintf "policy evaluation failed: %s" ex.Message)
                    }

                    match resolved with
                    | Choice2Of2 reason ->
                        return!
                            recordDecision (
                                queuedDecision
                                    scopeId
                                    artifactKeyHash
                                    policy.PolicyId
                                    policy.Version
                                    source
                                    None
                                    []
                                    (sprintf "queued — %s" reason)
                            )
                    | Choice1Of2(observed, incumbent, incumbentMetrics) ->
                        let incumbentKey = incumbent |> Option.map (fun a -> a.CompositeKey.Hash)
                        let verdict, checks, reason = PromotionJudge.judge policy observed incumbentMetrics

                        let decision = {
                            DecisionId = PromotionDecision.id scopeId artifactKeyHash
                            ScopeId = scopeId
                            ArtifactKeyHash = artifactKeyHash
                            PolicyId = policy.PolicyId
                            PolicyVersion = policy.Version
                            MetricSource = source
                            Verdict = PromotionPolicyVerdict.name verdict
                            Reason = reason
                            IncumbentKeyHash = incumbentKey
                            Checks = checks
                            TransitionApplied = false
                            TransitionRefusal = ""
                            SupersededKeyHash = None
                            SupersessionRefusal = ""
                            DecidedAt = deps.Now()
                        }

                        match PromotionPolicyVerdict.target verdict with
                        | None ->
                            // A queue moves nothing. The artifact stays in the
                            // status Phase 453's lifecycle already holds an
                            // uncurated fit in, which is what makes the queue a
                            // registry query rather than a new status.
                            return! recordDecision decision
                        | Some target ->
                            match! transition scopeId policy artifact.CompositeKey.Hash target reason with
                            | Error refusal ->
                                return!
                                    recordDecision {
                                        decision with
                                            TransitionRefusal = ModelTransitionRefusal.describe refusal
                                    }
                            | Ok _ ->
                                let applied = {
                                    decision with
                                        TransitionApplied = true
                                }

                                match verdict, incumbent with
                                | PromotionPolicyVerdict.AutoPromote, Some champion ->
                                    // Supersession is explicit, always. The
                                    // promotion happens first: if retiring the
                                    // incumbent then fails, the deployment has
                                    // two approved artifacts and a record
                                    // saying so, which an operator can fix —
                                    // whereas retiring first and failing to
                                    // promote would leave a consumer resolving
                                    // nothing at all.
                                    let rationale =
                                        sprintf
                                            "superseded by %s under policy '%s': %s"
                                            artifact.CompositeKey.Hash
                                            policy.PolicyId
                                            (PromotionDecision.describeDeltas applied)

                                    match!
                                        transition
                                            scopeId
                                            policy
                                            champion.CompositeKey.Hash
                                            ModelArtifactStatus.Retired
                                            rationale
                                    with
                                    | Ok _ ->
                                        return!
                                            recordDecision {
                                                applied with
                                                    SupersededKeyHash = Some champion.CompositeKey.Hash
                                            }
                                    | Error refusal ->
                                        return!
                                            recordDecision {
                                                applied with
                                                    SupersededKeyHash = Some champion.CompositeKey.Hash
                                                    SupersessionRefusal = ModelTransitionRefusal.describe refusal
                                            }
                                | _ -> return! recordDecision applied
        }

    /// The standing decision for one artifact, when it has been judged.
    member _.GetDecision(scopeId: string, artifactKeyHash: string) : Async<PromotionDecision option> = async {
        let! decisions =
            ModelPromotionStorage.queryBy deps scopeId (fun sidecar ->
                Map.tryFind ModelPromotionStorage.ArtifactKey sidecar = Some artifactKeyHash)

        return decisions |> List.sortByDescending _.DecidedAt |> List.tryHead
    }

    /// The curation queue: artifacts a policy declined to promote and that
    /// no human has moved since.
    ///
    /// **A registry query, narrowed by the standing decision.** The status
    /// half is `QueryByStatus(scope, Fitted)` — Phase 453 already models
    /// "registered, not yet promoted" as `Fitted`, so an artifact awaiting
    /// curation is one the lifecycle already holds there. Ordered by
    /// decision time, oldest first, so a queue is worked in the order it
    /// formed.
    member _.ListQueuedForCuration(scopeId: string) : Async<PromotionQueueEntry list> = async {
        let queuedLabel =
            PromotionPolicyVerdict.name PromotionPolicyVerdict.QueueForCuration

        let! decisions =
            ModelPromotionStorage.queryBy deps scopeId (fun sidecar ->
                Map.tryFind ModelPromotionStorage.VerdictKey sidecar = Some queuedLabel)

        let! pending = deps.Registry.QueryByStatus(scopeId, ModelArtifactStatus.Fitted)

        let byKey =
            decisions
            |> List.filter (fun d -> d.Verdict = queuedLabel)
            |> List.groupBy _.ArtifactKeyHash
            |> List.map (fun (key, group) -> key, group |> List.maxBy _.DecidedAt)
            |> Map.ofList

        return
            pending
            |> List.choose (fun artifact ->
                Map.tryFind artifact.CompositeKey.Hash byKey
                |> Option.map (fun decision -> {
                    Artifact = artifact
                    Decision = decision
                }))
            |> List.sortBy _.Decision.DecidedAt
    }

[<RequireQualifiedAccess>]
module ModelPromotionPolicyEvaluator =

    /// Construct the evaluator over the declared policies. Nothing is
    /// registered by default — a deployment composes this explicitly, like
    /// every other member of this family (GP 13).
    let create (policies: PromotionPolicy list) (deps: PromotionPolicyDeps) : ModelPromotionPolicyEvaluator =
        ModelPromotionPolicyEvaluator(policies, deps)

/// The persisted payload of a `_platform.modelpromotion.evaluate` job — the
/// schedulable form of "a new artifact exists, apply the policy".
///
/// The artifact is referenced by composite-key hash, never by value, so a
/// queued evaluation always judges the artifact's *current* record (the
/// `EvaluationRequest` idiom).
type PromotionEvaluationRequest = {
    ScopeId: string
    ArtifactKeyHash: string
}

[<RequireQualifiedAccess>]
module PromotionEvaluationEnvelope =

    /// STJ options with the full F# converter set — the SDK-standard wire
    /// shape, so persisted job payloads round-trip faithfully.
    let private jsonOptions = FableConverters.create ()

    /// Serialise a request to the persisted job-payload string.
    let serialiseRequest (request: PromotionEvaluationRequest) : string =
        JsonSerializer.Serialize(request, jsonOptions)

    /// Parse a persisted job payload back into a request. `Error` carries
    /// the failure detail; a malformed payload is a `PermanentFailure` (it
    /// will not recover on retry).
    let tryParseRequest (payload: string) : Result<PromotionEvaluationRequest, string> =
        try
            Ok(JsonSerializer.Deserialize<PromotionEvaluationRequest>(payload, jsonOptions))
        with ex ->
            Error ex.Message

    /// Whether an envelope error is terminal. A missing artifact will not
    /// appear on retry; a registry or storage fault may clear.
    let isTerminal (error: PromotionPolicyError) : bool =
        match error with
        | PromotionPolicyError.ArtifactNotFound -> true
        | PromotionPolicyError.RegistryFailure _
        | PromotionPolicyError.StorageFailure _ -> false

/// Fuses the evaluator onto `IJobScheduler` so a refit pipeline can enqueue
/// "decide about this artifact" rather than call it inline. Consumer-wired,
/// like the Phase 454 scoring and Phase 599 batch-item handlers.
type ModelPromotionJobHandler(evaluator: ModelPromotionPolicyEvaluator, logger: ILogger) =
    interface IJobHandler with
        member _.Execute(context: JobContext) : Async<JobResult> = async {
            match PromotionEvaluationEnvelope.tryParseRequest context.Payload with
            | Error reason -> return PermanentFailure $"malformed promotion-evaluation payload: {reason}"
            | Ok request ->
                match! evaluator.Evaluate(request.ScopeId, request.ArtifactKeyHash) with
                | Ok decision ->
                    logger.Info
                        $"promotion policy '{decision.PolicyId}' judged {decision.ArtifactKeyHash}: {decision.Verdict}"

                    return Success
                | Error error ->
                    let described = PromotionPolicyError.describe error

                    if PromotionEvaluationEnvelope.isTerminal error then
                        return PermanentFailure described
                    else
                        return TransientFailure described
        }

[<RequireQualifiedAccess>]
module ModelPromotionJobHandler =

    /// The scheduler handler name a promotion-evaluation job is enqueued
    /// against.
    [<Literal>]
    let HandlerName = "_platform.modelpromotion.evaluate"

    /// Construct the handler. Consumer-wired — nothing in forge registers it.
    let create (evaluator: ModelPromotionPolicyEvaluator) (logger: ILogger) : IJobHandler =
        ModelPromotionJobHandler(evaluator, logger) :> IJobHandler