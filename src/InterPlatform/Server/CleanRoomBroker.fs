// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

// ─── Layer 5 — clean-room broker (privacy-preserving query gate) ─────
//
// A clean-room broker sits between an inbound peer call and the contract
// handler and enforces a declarative privacy floor on every answer, so a
// counterparty can run an approved query against sensitive data and
// receive only privacy-preserving outputs — cohort counts at or above a
// k-anonymity floor, small cells suppressed, output shape constrained —
// never row-level data.
//
// **Where the "by construction" guarantee actually lives (Phase 311).**
// This file is the neutral MECHANISM and nothing more: `Enforce` is a
// pure function a caller has to invoke. Until Phase 311 the header
// claimed "I asked for a cohort count and got an aggregate ≥ k" was
// guaranteed by construction, and that was untrue of the library on its
// own — nothing in the dispatch path called the broker, so the guarantee
// held only while every contract author remembered to. A handler that
// forgot returned row-level data with no error, no warning and a passing
// build, which is precisely the shape of enforcement this SDK does not
// ship (GP 4: structural, not "remember to filter").
//
// The guarantee is real on the COMPOSED path:
// `PeerServerApp.withCleanRoomTemplate contractId template` wraps the
// named contract's registration so `ICleanRoomBroker.Enforce` runs on
// every answer of every method, the handler having no say in the matter
// — see `CleanRoomGate.fs`. Calling `Enforce` by hand from a handler
// remains supported for bespoke callers (a caller-requested gate, a
// non-peer surface), but it is no longer the documented default, and a
// gated contract does not depend on it.
//
// Scope discipline (GP 1): the broker ships the neutral *mechanism* —
// k-anonymity, cell suppression, output-shape constraint, and the
// gate-composition rule. It ships NO opinion on which queries a given
// data domain should permit or how N answers combine across a federation
// (`IPeerFanout` leaves aggregation to the caller for the same reason):
// "correct" privacy policy and aggregation are deployment-domain
// judgements that vary by data, regulator, and risk appetite. A
// deployment that needs a different mechanism (e.g. a differential-
// privacy budget) substitutes its own `ICleanRoomBroker`.
//
// Six portability rules (GP 12): every type is a value-typed immutable
// record / DU (rule 1), enforcement is a pure function returning data
// (rule 3 — the decision is a `GateDecision`, never a thrown exception),
// and the broker holds no state between calls (rule 4). `Enforce` is
// synchronous: it is pure CPU over an already-materialised result, with
// nothing to await — the documented shape for a pure transform.

/// The permitted shape of a clean-room answer. Row-level data is never a
/// shape — the broker exists precisely to forbid it.
type OutputShape =
    /// A single cohort size (one number).
    | Count
    /// A summary statistic over a cohort (sum / mean / …), already
    /// computed by the handler — the broker checks the cohort floor, not
    /// the arithmetic.
    | Aggregate
    /// A bucketed distribution: many labelled cells, each a count.
    | Histogram

/// The enforceable privacy floor. A caller may request a STRICTER gate
/// than a template's floor, never a looser one; the effective gate is the
/// stricter of the two (`PrivacyGate.compose`).
type PrivacyGate = {
    /// k-anonymity floor: the smallest cohort size that may be released.
    /// A whole result whose total cohort is below `k` is rejected.
    MinCohortSize: int
    /// Cells (histogram buckets) with a count below this threshold are
    /// suppressed individually rather than rejecting the whole result.
    /// Typically `>= MinCohortSize`.
    SuppressionThreshold: int
    /// Output shapes this gate permits. An answer whose declared shape is
    /// not in the set is rejected before release.
    PermittedShapes: Set<OutputShape>
}

/// A single labelled cell in a result (one histogram bucket, or the sole
/// cell of a `Count` / `Aggregate`). `Value` carries the aggregate where
/// the shape is `Aggregate`; `Count` is the cohort size backing the cell
/// and is what the k-anonymity / suppression floors are checked against.
type PrivacyCell = {
    Label: string
    Count: int
    Value: float option
}

/// A handler's answer, expressed in a gate-checkable shape. The handler
/// computes the cohort/aggregate; the broker enforces the floor over it.
type CohortResult = {
    Shape: OutputShape
    Cells: PrivacyCell list
}

/// The broker's decision over a `CohortResult`.
type GateDecision =
    /// The result cleared the effective gate. `Result` is the released
    /// answer — possibly with sub-threshold cells removed — and
    /// `SuppressedCells` lists the labels that were dropped (for audit).
    | Released of result: CohortResult * suppressedCells: string list
    /// The whole result was withheld. `Reason` is a structured,
    /// audit-friendly explanation (shape not permitted / cohort below
    /// floor / method not on the template surface).
    | Withheld of reason: string
    /// Phase 481 — the result cleared the effective gate AND had
    /// calibrated noise applied to it. `Result` is the noised answer;
    /// `Noise` is the policy that calibrated it.
    ///
    /// **The spec travels with the answer on purpose.** ε, δ and
    /// sensitivity are declared parameters of a public mechanism, not
    /// secrets — Kerckhoffs applies, and a mechanism whose safety
    /// depended on hiding its calibration would not be one. What stays
    /// receiver-side is what the *ledger* knows (remaining budget), for
    /// the reason `PeerCleanRoomDecisionPayload` documents: a caller
    /// that can read its own remaining ε back while varying its query
    /// has an oracle the k-floor already refuses it.
    ///
    /// A `Released` becomes this only through `CohortNoise.applyTo`. An
    /// `ICleanRoomBroker` that returns it directly is **withheld** by
    /// `CleanRoomGate` — see that module's header. The substrate's
    /// release post-condition is evaluated over TRUE counts, and a
    /// broker-noised answer carries none for it to check.
    | NoisedRelease of result: CohortResult * suppressedCells: string list * noise: NoisedReleasePolicy

/// A declarative clean-room contract: the query surface a counterparty
/// may invoke, plus the privacy floor every answer must clear. Registered
/// alongside the deployment's peer contracts.
type CleanRoomTemplate = {
    /// Stable id for diagnostics / audit.
    TemplateId: string
    /// The method names on the peer contract this template gates. A call
    /// to any other method is withheld (surface enforcement).
    AllowedMethods: Set<string>
    /// The privacy floor. A caller may tighten it per call, never loosen
    /// it.
    Floor: PrivacyGate
}

[<RequireQualifiedAccess>]
module PrivacyGate =
    /// The stricter of two gates, field by field: the larger cohort floor,
    /// the larger suppression threshold, and the INTERSECTION of permitted
    /// shapes (a shape is permitted only if both gates permit it). This is
    /// the gate-composition invariant — composing a caller request with a
    /// template floor can only ever tighten, never relax.
    let compose (a: PrivacyGate) (b: PrivacyGate) : PrivacyGate = {
        MinCohortSize = max a.MinCohortSize b.MinCohortSize
        SuppressionThreshold = max a.SuppressionThreshold b.SuppressionThreshold
        PermittedShapes = Set.intersect a.PermittedShapes b.PermittedShapes
    }

    /// True when `candidate` is at least as strict as `floor` on every
    /// axis (cohort floor ≥, suppression ≥, permitted shapes ⊆).
    let isStricterOrEqual (floor: PrivacyGate) (candidate: PrivacyGate) : bool =
        candidate.MinCohortSize >= floor.MinCohortSize
        && candidate.SuppressionThreshold >= floor.SuppressionThreshold
        && Set.isSubset candidate.PermittedShapes floor.PermittedShapes

    /// Phase 311 — the strictest gate a materialised result demonstrably
    /// satisfies: the cohort it actually releases, the smallest cell it
    /// actually carries, and the single shape it actually has.
    ///
    /// It exists so a release can be checked against a floor with
    /// `isStricterOrEqual floor (observed released)` — one comparison
    /// answering "does this answer clear the composed floor on every
    /// axis?" rather than three hand-written ones that can drift from
    /// `PrivacyGate`'s own definition of strictness. That check is the
    /// composed gate's release post-condition (`CleanRoomGate`), and it
    /// is what makes the floor bind on a SUBSTITUTED `ICleanRoomBroker`
    /// too: the seam exists so a deployment can ship a different privacy
    /// mechanism, and a different mechanism must not be able to release
    /// below the floor the composition declared.
    ///
    /// An empty result observes `SuppressionThreshold = Int32.MaxValue`
    /// (no cell violates a threshold when there is no cell) and
    /// `MinCohortSize = 0`, so it clears a zero floor and nothing else —
    /// which is the honest reading of "everything was suppressed".
    let observed (result: CohortResult) : PrivacyGate = {
        MinCohortSize = result.Cells |> List.sumBy _.Count
        SuppressionThreshold =
            match result.Cells with
            | [] -> System.Int32.MaxValue
            | cells -> cells |> List.map _.Count |> List.min
        PermittedShapes = Set.singleton result.Shape
    }

/// The enforcement seam wrapping a clean-room query. The default
/// implementation is `DefaultCleanRoomBroker`; a deployment substitutes
/// its own behind this interface to ship an alternative privacy mechanism
/// without changing call sites.
type ICleanRoomBroker =
    /// Apply the effective gate (the stricter of the template floor and
    /// any caller-requested gate) to a materialised result for `methodName`.
    /// Withholds the whole result when the method is off the template
    /// surface, the shape is not permitted, or the cohort is below the
    /// k-floor; otherwise releases it with sub-threshold cells suppressed.
    abstract Enforce:
        template: CleanRoomTemplate * methodName: string * requested: PrivacyGate option * result: CohortResult ->
            GateDecision

/// Default opinionated broker: k-anonymity floor + per-cell suppression +
/// output-shape constraint + template-surface enforcement. Pure; stateless
/// between calls (GP 12 rule 4).
type DefaultCleanRoomBroker() =

    interface ICleanRoomBroker with
        member _.Enforce(template, methodName, requested, result) =
            // 1. Surface enforcement — the method must be on the template.
            if not (Set.contains methodName template.AllowedMethods) then
                Withheld(sprintf "method '%s' is not on clean-room template '%s'" methodName template.TemplateId)
            else
                // 2. Effective gate — caller may only tighten the floor.
                let effective =
                    match requested with
                    | Some r -> PrivacyGate.compose template.Floor r
                    | None -> template.Floor

                // 3. Shape constraint.
                if not (Set.contains result.Shape effective.PermittedShapes) then
                    Withheld(
                        sprintf
                            "output shape %A is not permitted by the effective gate for template '%s'"
                            result.Shape
                            template.TemplateId
                    )
                else
                    // 4. Per-cell suppression — drop buckets below the
                    // suppression threshold; record their labels for audit.
                    let surviving, suppressed =
                        result.Cells
                        |> List.partition (fun c -> c.Count >= effective.SuppressionThreshold)

                    let suppressedLabels = suppressed |> List.map _.Label

                    // 5. k-anonymity over the released cohort — the total
                    // of the SURVIVING cells must clear the floor, else the
                    // whole answer leaks too small a cohort.
                    let releasedCohort = surviving |> List.sumBy _.Count

                    if releasedCohort < effective.MinCohortSize then
                        Withheld(
                            sprintf
                                "released cohort %d is below the k-anonymity floor %d for template '%s'"
                                releasedCohort
                                effective.MinCohortSize
                                template.TemplateId
                        )
                    else
                        Released({ result with Cells = surviving }, suppressedLabels)

[<RequireQualifiedAccess>]
module CleanRoomBroker =
    /// Construct the default broker behind the `ICleanRoomBroker` seam.
    let create () : ICleanRoomBroker =
        DefaultCleanRoomBroker() :> ICleanRoomBroker

/// Phase 481 — applying a `NoisedReleasePolicy` to a cleared release.
///
/// The module is `CohortNoise` and not `NoisedRelease` because that name
/// is already a `GateDecision` case, and a module sharing a union case's
/// name is how a call site resolves to the one you did not mean.
///
/// **Order is the whole design.** Suppression and the k-floor are decided
/// on TRUE counts, by the broker and then re-checked by the substrate;
/// noise is applied afterwards, to what survived. Reversing that would
/// let a cell whose true count is 1 be pushed above the suppression
/// threshold by a lucky draw and released — the floor would then hold
/// only in expectation, which is not what a floor is. DP is closed under
/// post-processing, so noising a cleared release is sound; deciding
/// clearance from a noised value is not.
[<RequireQualifiedAccess>]
module CohortNoise =

    /// Noise one cell. A `Value` of `None` is left `None`: there is
    /// nothing to noise, and substituting a zero would disclose that the
    /// handler produced no aggregate for this bucket.
    ///
    /// A noised count may come out negative, and it is left negative.
    /// Clamping is legal post-processing but biases the estimator, and
    /// choosing that trade is a deployment's call, not the SDK's (GP 1).
    let private noiseCell (mechanism: INoiseMechanism) (policy: NoisedReleasePolicy) (cell: PrivacyCell) =
        let count =
            match policy.CountNoise with
            | None -> cell.Count
            | Some spec ->
                let noised = NoiseMechanism.release mechanism spec (float cell.Count)
                int (max (float System.Int32.MinValue) (min (float System.Int32.MaxValue) noised))

        let value =
            match policy.ValueNoise, cell.Value with
            | Some spec, Some v -> Some(NoiseMechanism.release mechanism spec v)
            | _, existing -> existing

        {
            cell with
                Count = count
                Value = value
        }

    /// Apply `policy` to every cell of `result`, drawing an INDEPENDENT
    /// sample per cell per target.
    ///
    /// Independence per cell is not an optimisation to share away: one
    /// draw reused across a histogram's buckets is a single random
    /// offset an observer can difference out, and the mechanism would be
    /// deterministic in every way that matters. It also means a
    /// histogram's ε is the per-cell ε only when the cells are disjoint
    /// (parallel composition, Dwork & Roth §3.5) — which is the usual
    /// case for a bucketed cohort, and is the caller's to confirm.
    let apply (mechanism: INoiseMechanism) (policy: NoisedReleasePolicy) (result: CohortResult) : CohortResult = {
        result with
            Cells = result.Cells |> List.map (noiseCell mechanism policy)
    }

    /// Turn a cleared `Released` into a `NoisedRelease`. A `Withheld`
    /// passes through untouched — there is nothing released to noise —
    /// and an already-`NoisedRelease` is returned as-is, because noising
    /// twice would spend ε that was never reserved.
    let applyTo (mechanism: INoiseMechanism) (policy: NoisedReleasePolicy) (decision: GateDecision) : GateDecision =
        match decision with
        | Withheld _
        | NoisedRelease _ -> decision
        | Released(result, suppressedCells) -> NoisedRelease(apply mechanism policy result, suppressedCells, policy)