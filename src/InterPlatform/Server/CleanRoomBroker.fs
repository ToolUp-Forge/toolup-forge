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
// `PeerOptions.withCleanRoomTemplate contractId template` wraps the
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

/// The complement check a template declares: for every released cell of a
/// split carrying `splitKey`, the split's base less the cell must also clear
/// the floor, or the cell is withheld. A cell whose complement is three
/// people gives those three away, though the cell itself clears the floor.
[<RequireQualifiedAccess>]
type ComplementRule =
    /// Check each released cell's complement within the answer's declared
    /// splits that carry this key.
    | Within of splitKey: string

/// Which earlier releases a submission's filter cohort is compared with.
[<RequireQualifiedAccess>]
type DifferencingScope =
    /// The releases this template made to the same counterparty, within the
    /// composed window's length.
    | PerCounterparty
    /// Every release this template made on the same vintage, to any
    /// counterparty, so two counterparties cannot difference each other's
    /// releases.
    | PerVintage

/// Whether a template's answers are held to the near-duplicate check when
/// the composition composes a release window.
[<RequireQualifiedAccess>]
type NearDuplicateRule =
    /// Compared with the earlier releases `scope` names: the default.
    | Compared of scope: DifferencingScope
    /// Not compared. The reason is part of the template, so the approval
    /// both parties sign records why.
    | NotCompared of reason: string

/// The terms a template releases its answers on, beyond the
/// floor: how each declared method's answer is binned and parameterised,
/// and whether the released cells carry calibrated noise.
///
/// **Template data, never call data.** Both members are covered by the
/// template's canonical encoding (`TemplateCanonical`), so its content
/// hash moves with them and a bilateral approval binds to
/// them: moving a bin edge or switching noise on makes a different
/// template, which has to be approved again. A caller that could set a
/// bin edge could place two edges either side of one record and read it
/// off the difference between two releases; a template it cannot edit
/// leaves it nothing to nudge.
type ReleaseTerms = {
    /// Per-method release declarations, keyed by method name. Opaque to
    /// the broker and the gate: the projection family that answers a
    /// method owns its declaration's grammar and refuses, at composition,
    /// a template whose declaration it cannot read. Empty for a template
    /// that declares none.
    Declarations: Map<string, string>
    /// Calibrated noise the released cells carry, spending the
    /// policy's ε per release. `None`, the default, is k-suppression only,
    /// for which no differential-privacy claim is made. A gate composed
    /// for a template that declares noise, without the same policy and a
    /// mechanism to draw it, withholds every answer rather than releasing
    /// one un-noised.
    Noise: NoisedReleasePolicy option
    /// Whether a released answer names to the caller the labels of the
    /// cells the floor withheld, as a `ReleasedCohort`. `false`, the
    /// default, records them receiver-side only. A caller that draws the
    /// answer needs them: a withheld cell (1 to k−1 rows) drawn as a
    /// missing value reads as "none", which is false, so a template whose
    /// answers are drawn on the caller's side names them, and its approval
    /// records that both parties agreed it would.
    NamesWithheld: bool
    /// The complement check, or `None`, the default, for no complement check.
    /// A template that declares one withholds every answer that does not
    /// declare a split under its key.
    Complement: ComplementRule option
    /// How a composed release window compares this template's answers.
    /// `Compared PerCounterparty`, the default, has no effect until a
    /// composition composes a window.
    NearDuplicate: NearDuplicateRule
}

/// Constructors for `ReleaseTerms`.
[<RequireQualifiedAccess>]
module ReleaseTerms =
    /// No declarations, no noise and no complement check: the terms of a
    /// template that declares none, whose canonical encoding (and so its
    /// approvals) it leaves unchanged.
    let none: ReleaseTerms = {
        Declarations = Map.empty
        Noise = None
        NamesWithheld = false
        Complement = None
        NearDuplicate = NearDuplicateRule.Compared DifferencingScope.PerCounterparty
    }

/// A released answer that names the cells the floor withheld, for a
/// template whose release terms say so. `Cells` are the released cells;
/// `Withheld` the labels of the suppressed ones, in the order the answer
/// carried them. A label in neither is a cell the answer never had.
type ReleasedCohort = {
    /// The answer's declared shape.
    Shape: OutputShape
    /// The cells the floor released.
    Cells: PrivacyCell list
    /// The labels of the cells the floor withheld.
    Withheld: string list
}

/// One split of a cohort answer, for the complement check: the base the
/// split's cells break down, and which cells they are. A two-dimensional
/// answer declares one split per column (or per row) under one key.
type CohortSplit = {
    /// The key a template's `ComplementRule.Within` names.
    Key: string
    /// The number of members the split's cells are a breakdown of, counted
    /// as the cells are. A cell's complement is this less the cell.
    Base: int
    /// The labels of the split's cells.
    Cells: string list
}

/// The members a cohort answer was computed over, as the near-duplicate
/// check reads them: how many, a hash of which, and a count sketch, never
/// a member. Built by the data holder with `FilterCohort.ofMembers`, read
/// by the gate on the holder's side, and never released.
type FilterCohort = {
    /// The number of distinct members.
    Size: int
    /// `sha256:` over the members' sorted digests. Two cohorts with one
    /// hash are the same cohort.
    MembershipHash: string
    /// `FilterCohort.Buckets` counts: how many members' digests fall in each
    /// bucket. The sum of the per-bucket differences between two sketches
    /// never exceeds the number of members in one cohort and not the other.
    Sketch: int list
    /// The data the cohort was drawn from, which a `PerVintage` window keys
    /// on. `None` where the template compares per counterparty.
    Vintage: string option
}

/// A cohort answer with the structure the gate's differencing checks read:
/// its splits and its filter cohort. The gate releases `Result` alone, so
/// neither crosses the wire. A handler answering under a template that
/// declares neither check answers a bare `CohortResult` as before.
type DeclaredCohort = {
    /// The answer.
    Result: CohortResult
    /// The splits the complement check reads. Empty for none.
    Splits: CohortSplit list
    /// The members the answer was computed over, which the near-duplicate
    /// check reads.
    Filter: FilterCohort option
}

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
    /// The release terms beyond the floor: per-method
    /// declarations and calibrated noise. `ReleaseTerms.none` for a
    /// template that declares neither.
    Release: ReleaseTerms
}

/// The cell-label grammar for a two-dimensional cell.
///
/// A 2-D answer stays an `OutputShape.Histogram`: the gate's check is per
/// cell count whichever way the cells are arranged, so a grid case on
/// `OutputShape` would buy structure and nothing on disclosure, and would
/// break every exhaustive match on that union. A grid cell is instead
/// labelled `row␟column`, the separator being U+241F (SYMBOL FOR UNIT
/// SEPARATOR), and the label vocabulary is refused from containing it, so
/// a label parses as a grid cell exactly when it was built as one.
[<RequireQualifiedAccess>]
module CellLabel =

    /// The row/column separator, U+241F.
    [<Literal>]
    let GridSeparator = "␟"

    /// True when `label` is an ordinary one-dimensional label: it does not
    /// contain the grid separator.
    let isPlain (label: string) : bool =
        not (label.Contains(GridSeparator, System.StringComparison.Ordinal))

    /// The label of the grid cell at (`row`, `column`). Refused when either
    /// part contains the separator, since the label would then read back as
    /// a different cell: a collision is an error, never a guess.
    let grid (row: string) (column: string) : Result<string, string> =
        if not (isPlain row) then
            Error $"the row label '{row}' contains the grid separator U+241F"
        elif not (isPlain column) then
            Error $"the column label '{column}' contains the grid separator U+241F"
        else
            Ok(row + GridSeparator + column)

    /// Read a grid label back into its row and column. `None` for a plain
    /// label, and for a label carrying more than one separator, which no
    /// `grid` call produces.
    let tryGrid (label: string) : (string * string) option =
        match label.Split(GridSeparator) with
        | [| row; column |] -> Some(row, column)
        | _ -> None

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

    /// The count the differencing checks hold to: the larger of the cohort
    /// floor and the suppression threshold. A complement is a cell in all
    /// but name, and two cohorts closer than this are one cohort and a
    /// group too small to release.
    let differencingFloor (gate: PrivacyGate) : int =
        max gate.MinCohortSize gate.SuppressionThreshold

/// Building and comparing filter cohorts.
[<RequireQualifiedAccess>]
module FilterCohort =

    /// The number of buckets a sketch counts into.
    [<Literal>]
    let Buckets = 256

    /// The domain separator each member's digest is taken under.
    [<Literal>]
    let private MemberDomain = "toolup.filter-cohort.v1\n"

    /// The filter cohort of `members`, each a stable identifier of one
    /// member (a respondent id, a record key). Duplicates count once. The
    /// identifiers are digested and dropped: neither the hash nor the sketch
    /// holds one, though a party holding the identifiers could test a guess
    /// against the sketch, which is why the cohort never leaves the holder.
    let ofMembers (members: string seq) : FilterCohort =
        let digests =
            members
            |> Seq.map (fun m ->
                if isNull m then
                    invalidArg "members" "a filter cohort member identifier is null"

                ToolUp.Platform.Sha256.hexOfUtf8 (MemberDomain + m))
            |> Seq.distinct
            |> Seq.sortWith (fun a b -> System.String.CompareOrdinal(a, b))
            |> Seq.toArray

        // The digest's first byte picks the bucket: 256 values, 256 buckets.
        let sketch = Array.zeroCreate<int> Buckets

        for d in digests do
            let bucket = System.Convert.ToInt32(d.Substring(0, 2), 16)
            sketch[bucket] <- sketch[bucket] + 1

        {
            Size = digests.Length
            MembershipHash = "sha256:" + ToolUp.Platform.Sha256.hexOfUtf8 (String.concat "\n" digests)
            Sketch = List.ofArray sketch
            Vintage = None
        }

    /// Name the data `cohort` was drawn from, for a `PerVintage` window.
    let withVintage (vintage: string) (cohort: FilterCohort) : FilterCohort = { cohort with Vintage = Some vintage }

    /// `None` when `cohort` reads as one `ofMembers` could have built, else
    /// the reason it does not. The gate withholds an answer whose filter
    /// cohort does not read, since it could not compare it.
    let problem (cohort: FilterCohort) : string option =
        if isNull (box cohort.Sketch) || isNull cohort.MembershipHash then
            Some "the filter cohort is missing its sketch or its membership hash"
        elif List.length cohort.Sketch <> Buckets then
            Some $"the filter cohort's sketch has {List.length cohort.Sketch} buckets, not {Buckets}"
        elif cohort.Sketch |> List.exists (fun c -> c < 0) then
            Some "the filter cohort's sketch holds a negative count"
        elif List.sum cohort.Sketch <> cohort.Size then
            Some "the filter cohort's sketch does not sum to its size"
        elif not (cohort.MembershipHash.StartsWith("sha256:", System.StringComparison.Ordinal)) then
            Some "the filter cohort's membership hash is not a sha256: digest"
        else
            None

    /// A lower bound on the number of members in one cohort and not the
    /// other: the sum of the per-bucket differences of the two sketches.
    /// Exact when one cohort contains the other.
    let distance (a: FilterCohort) (b: FilterCohort) : int =
        List.fold2 (fun total x y -> total + abs (x - y)) 0 a.Sketch b.Sketch

    /// Whether `candidate` is within `floor` members of `released`: a
    /// different cohort whose difference from it may hold fewer than `floor`
    /// members. Since `distance` never exceeds the true difference, a pair
    /// that differs by fewer than `floor` members is always caught; a pair
    /// that differs by more is caught only when its additions and removals
    /// cancel bucket by bucket.
    let isNear (floor: int) (released: FilterCohort) (candidate: FilterCohort) : bool =
        released.MembershipHash <> candidate.MembershipHash
        && distance released candidate < floor

/// Building a `DeclaredCohort`.
[<RequireQualifiedAccess>]
module DeclaredCohort =

    /// `result` with no splits and no filter cohort: judged as a bare
    /// `CohortResult` is.
    let ofResult (result: CohortResult) : DeclaredCohort = {
        Result = result
        Splits = []
        Filter = None
    }

    /// Add `split` to the splits the complement check reads.
    let withSplit (split: CohortSplit) (declared: DeclaredCohort) : DeclaredCohort = {
        declared with
            Splits = declared.Splits @ [ split ]
    }

    /// Set the filter cohort the near-duplicate check reads.
    let withFilter (filter: FilterCohort) (declared: DeclaredCohort) : DeclaredCohort = {
        declared with
            Filter = Some filter
    }

/// Why a differencing check withheld a cell or refused a submission. Each
/// is recorded receiver-side in the decision row's reason, and none is sent
/// on the wire: a caller learns only that the answer was withheld.
[<RequireQualifiedAccess>]
type DifferencingVerdict =
    /// A released cell's complement within its split is below the floor;
    /// the cell is withheld. Names the split and the cell, never the count.
    | ComplementBelowFloor of split: string * cell: string
    /// The submission's filter cohort is within the floor of a released
    /// one, named by its membership hash; the answer is refused.
    | NearDuplicateFilter of earlier: string
    /// The submission's filter cohort is smaller than the floor; the answer
    /// is refused.
    | FilterBelowFloor

[<RequireQualifiedAccess>]
module DifferencingVerdict =

    /// The audit-facing reason for `verdict`.
    let describe (verdict: DifferencingVerdict) : string =
        match verdict with
        | DifferencingVerdict.ComplementBelowFloor(split, cell) ->
            $"ComplementBelowFloor: the complement of cell '{cell}' within split '{split}' is below the floor, so the cell is withheld"
        | DifferencingVerdict.NearDuplicateFilter earlier ->
            $"NearDuplicateFilter: the filter cohort is within the floor of the cohort {earlier} already released, so the answer is refused"
        | DifferencingVerdict.FilterBelowFloor ->
            "FilterBelowFloor: the filter cohort is smaller than the floor, so the answer is refused"

[<RequireQualifiedAccess>]
module ComplementRule =

    /// The key the rule checks within.
    let key (rule: ComplementRule) : string =
        match rule with
        | ComplementRule.Within splitKey -> splitKey

    /// Apply `rule` to `released`, the cells the floor already cleared, on
    /// their true counts. `Ok` carries the cells that stand and a verdict per
    /// cell withheld; `Error` says why the declaration could not be read,
    /// and the caller withholds the whole answer: no split under the key,
    /// a negative base, or a released cell counting more than its split's
    /// base. A released cell in no split under the key is judged by the
    /// floor alone, as before.
    let apply
        (floor: int)
        (rule: ComplementRule)
        (splits: CohortSplit list)
        (released: CohortResult)
        : Result<CohortResult * DifferencingVerdict list, string> =
        let splitKey = key rule
        let within = splits |> List.filter (fun s -> s.Key = splitKey)

        let countOf = released.Cells |> List.map (fun c -> c.Label, c.Count) |> Map.ofList

        let tooLarge =
            within
            |> List.exists (fun s ->
                s.Cells
                |> List.exists (fun label ->
                    match Map.tryFind label countOf with
                    | Some count -> count > s.Base
                    | None -> false))

        if List.isEmpty within then
            Error $"the answer declares no split '{splitKey}', which the template's complement check reads"
        elif within |> List.exists (fun s -> s.Base < 0 || isNull (box s.Cells)) then
            Error $"a split '{splitKey}' declares a negative base or no cells"
        elif tooLarge then
            Error $"a released cell counts more than the base of its split '{splitKey}'"
        else
            let withheld =
                within
                |> List.collect (fun s ->
                    s.Cells
                    |> List.choose (fun label ->
                        match Map.tryFind label countOf with
                        | Some count when s.Base - count < floor -> Some label
                        | _ -> None))
                |> List.distinct

            let verdicts =
                withheld
                |> List.map (fun label -> DifferencingVerdict.ComplementBelowFloor(splitKey, label))

            Ok(
                {
                    released with
                        Cells = released.Cells |> List.filter (fun c -> not (List.contains c.Label withheld))
                },
                verdicts
            )

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