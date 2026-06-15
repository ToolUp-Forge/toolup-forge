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
// never row-level data. The enforcement is mechanical: "I asked for a
// cohort count and got an aggregate ≥ k" is guaranteed by construction,
// not by trust.
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