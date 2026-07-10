// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Security.Cryptography
open System.Text

// ─── Fact model (Phase 520) ──────────────────────────────────────────
//
// A **fact** is a verified assertion about a (subject, metric, period):
// a value, with provenance, produced by deterministic computation over
// validated data, asserted by a human, or imported from a peer. Facts
// are the typed *memory* tier — the numbers an answer quotes verbatim,
// as distinct from prose (comprehension) or a computation record
// (evidence). The model is:
//
//   - **content-addressed** — `FactId = hash(subject, metric, period,
//     method, inputHashes)`, so re-asserting the same tuple is idempotent
//     (law L2) and a changed input yields a *new* fact whose supersession
//     edge is derived, never declared;
//   - **bitemporal** — `Period` is valid time, `AsOf` is transaction
//     time; a forecast is simply a fact whose valid time extends beyond
//     its transaction time (no flag — the timestamps carry it);
//   - **append-only** — correction is supersession (a new fact with a
//     later `AsOf`), never mutation (law L1);
//   - **disclosure-classified at birth** — every fact carries a
//     `Disclosure` from its first assertion (plan D14); enforcement at the
//     egress choke points is a later phase, the *field* lands here.
//
// **Generic + OSS-safe.** No domain vocabulary — a metric is referenced
// by its registry id string, a subject by an opaque hierarchy + path.

/// Reference to a registered metric by its stable id (the Phase 519
/// `Grounding.MetricDefinition.Id`). A value wrapper so a `MetricRef` is
/// unambiguous in a signature and hashes structurally.
type MetricRef =
    | MetricRef of string

    /// The underlying metric id string.
    member this.Value =
        let (MetricRef v) = this
        v

/// Reference to a subject *instance* — a concrete node in a registered
/// subject hierarchy (Phase 519 `Grounding.SubjectDefinition`). `Hierarchy`
/// is the hierarchy's stable id; `Path` is the ordered member ids from the
/// root level down to the level this fact is about (`[ "acme"; "widget-x" ]`
/// under a `brand → sku` hierarchy). An empty `Path` addresses the
/// hierarchy root (a total, all-members roll-up).
type SubjectRef = { Hierarchy: string; Path: string list }

/// Valid-time extent of a fact — the period the value describes (a day, a
/// fiscal quarter, an open-ended "as of now"). Half-open `[From, To)` in
/// UTC; `Label` is an optional human tag (`"Q2-2026"`) scoped to the
/// subject's calendar (law L5) — never compared across calendars.
type TemporalExtent = {
    From: DateTime
    To: DateTime
    Label: string option
}

/// The value a fact carries. `Series` references a data-object *version*
/// (a vintage) rather than inlining points — a fact is an assertion
/// *about* data, not a copy of it (the scale rule). `Absent` records a
/// queryable data gap so the closed loop stops re-planning the same dead
/// end and the acquisition backlog is a derived view of current absences.
type FactValue =
    /// A single scalar value.
    | Scalar of decimal
    /// A `[Low, High]` interval (a range, a confidence band expressed as
    /// bounds — *not* a probabilistic claim; see the honesty boundary).
    | Interval of low: decimal * high: decimal
    /// A time/whatever series, referenced by the data-object version id it
    /// lives in (never inlined — scale rule).
    | Series of dataObjectVersion: string
    /// A named distribution — buckets / quantiles → value.
    | Distribution of buckets: Map<string, decimal>
    /// A categorical / label-valued fact.
    | Categorical of string
    /// The queryable *absence* of a value — the gap-detection branch of
    /// the closed loop asserts this so the gap is first-class. `reason`
    /// names why (e.g. `"no data loaded for this period"`).
    | Absent of reason: string

/// How a fact came to be (plan D18 — three origins, one model). The
/// method's *identity* (not its full payload) participates in the content
/// address and the supersession lineage key.
type MethodRef =
    /// Produced by a deterministic catalog operation at a version, with a
    /// hash over its parameters. Re-running on unchanged inputs is
    /// idempotent; a new input or a version bump supersedes within the
    /// lineage.
    | Computed of operationId: string * version: string * paramHash: string
    /// Asserted by a principal (a CFO's guidance, a contract term). For a
    /// human assertion the content address's input-hash slot is the hash
    /// of the asserted *value*, so re-asserting a different value
    /// supersedes within that principal's lineage; different principals
    /// asserting differently yields *competing* facts.
    | HumanAsserted of principal: string
    /// Imported from a peer deployment, carrying its signed certificate
    /// reference. Content-addressed identity is deployment-independent, so
    /// an imported fact keeps its id and verification re-derives it.
    | Imported of certificateRef: string

/// Disclosure classification carried on every fact from birth (plan D14).
/// Enforcement is a filter at the egress choke points (a later phase);
/// `Internal` facts remain fully first-class *inside* computation and
/// internal audit. Defaults on assertion: a fact whose metric is a
/// registry-declared metric is `Surfaceable`; an undeclared intermediate
/// is `Internal`.
type Disclosure =
    /// May reach an answer / an external door, subject to composition
    /// policy.
    | Surfaceable
    /// First-class inside computation and internal audit; never an egress
    /// output.
    | Internal
    /// Governed by a named policy (licensed-data terms, retention,
    /// k-anonymity). `policyRef` is an opaque string here (plan open
    /// question 7 — a typed policy vocabulary is a later metric-pack
    /// concern).
    | Restricted of policyRef: string

/// Optional confidence attribution for a fact. Confidence *attribution*
/// (citing the method's diagnostics) is in scope; uncertainty
/// *propagation* through derivations is explicitly not (the honesty
/// boundary). `Basis` names where the number came from (a fit diagnostic,
/// a methodology note).
type Confidence = { Value: decimal; Basis: string option }

/// The provenance a fact carries — the refs a later provenance-chain walk
/// (Phase 524) stitches into ingestion → run → fact → narrative → answer.
type Evidence = {
    /// Optional analysis-result / fit-artifact id this fact was computed
    /// from (the modelling-substrate artifact a `Computed` method points
    /// at). `None` for a hand-asserted or imported fact.
    ResultRef: string option
    /// Content hashes of the input data-object versions that fed the
    /// computation — the inputs that make the content address change when
    /// the data changes. For a `HumanAsserted` fact this is the hash of
    /// the asserted value (plan D18).
    InputHashes: string list
    /// Optional trigger ref (the conversation / watch / schedule that
    /// caused this fact to be computed) — "why was it computed at all".
    TriggerRef: string option
}

/// A verified assertion about a (subject, metric, period). Identity is
/// content-addressed (`FactId`); the record is immutable — a correction is
/// a *new* fact with a later `AsOf` and a derived `Supersedes` edge.
type Fact = {
    /// Content-addressed identity — `FactId.compute` over (subject,
    /// metric, period, method, inputHashes). Deployment-independent.
    FactId: string
    Subject: SubjectRef
    Metric: MetricRef
    Value: FactValue
    /// Valid time — the period the value describes.
    Period: TemporalExtent
    /// Transaction time — when this assertion entered the store (law L4's
    /// visibility clock). A fact is a *forecast* iff `Period.To > AsOf`.
    AsOf: DateTime
    Method: MethodRef
    Evidence: Evidence
    Confidence: Confidence option
    /// The `FactId` this fact supersedes within its lineage — *derived* at
    /// assert time, never supplied by the caller. `None` for the first
    /// fact in a lineage.
    Supersedes: string option
    Disclosure: Disclosure
}

/// Content-addressing, lineage keys, and freshness — the decidable laws
/// (L1–L4) the store is built on, factored out so both the store and its
/// contract tests share one implementation.
module Fact =

    let private sha256Hex (s: string) : string =
        use sha = SHA256.Create()

        sha.ComputeHash(Encoding.UTF8.GetBytes s)
        |> Array.map (sprintf "%02x")
        |> String.concat ""

    /// The identity of a method for content-addressing / lineage — the
    /// method *kind* + its identifying fields, but NOT the transient
    /// payload. `Computed` keys on (operation, version, paramHash);
    /// `HumanAsserted` on the principal; `Imported` on the certificate ref.
    let methodIdentity (m: MethodRef) : string =
        match m with
        | Computed(op, version, paramHash) -> sprintf "computed:%s:%s:%s" op version paramHash
        | HumanAsserted principal -> sprintf "asserted:%s" principal
        | Imported certificateRef -> sprintf "imported:%s" certificateRef

    let private canonicalSubject (s: SubjectRef) : string =
        sprintf "%s/%s" s.Hierarchy (String.concat ">" s.Path)

    let private canonicalPeriod (p: TemporalExtent) : string =
        sprintf "%s..%s" (p.From.ToUniversalTime().ToString("o")) (p.To.ToUniversalTime().ToString("o"))

    /// The **lineage key** (plan D19) — (subject, metric, period, method
    /// id) *without* the input hashes. Two facts share a lineage iff their
    /// lineage keys match: a new input or a method-version bump supersedes
    /// *within* the lineage; a different method id or parameter hash
    /// (which changes the method identity) yields a *competing* fact,
    /// never a supersession.
    let lineageKey (subject: SubjectRef) (metric: MetricRef) (period: TemporalExtent) (method: MethodRef) : string =
        sprintf "%s|%s|%s|%s" (canonicalSubject subject) metric.Value (canonicalPeriod period) (methodIdentity method)

    /// A stable hash of a `FactValue` — the content-address input for a
    /// `HumanAsserted` fact (plan D18: the identity's input-hash slot is
    /// the asserted value's hash, so re-asserting a *different* value
    /// supersedes within the principal's lineage).
    let valueHash (v: FactValue) : string =
        let canonical =
            match v with
            | Scalar d -> sprintf "scalar:%s" (string d)
            | Interval(lo, hi) -> sprintf "interval:%s:%s" (string lo) (string hi)
            | Series ver -> sprintf "series:%s" ver
            | Distribution buckets ->
                buckets
                |> Map.toList
                |> List.sortBy fst
                |> List.map (fun (k, v) -> sprintf "%s=%s" k (string v))
                |> String.concat ";"
                |> sprintf "dist:%s"
            | Categorical c -> sprintf "cat:%s" c
            | Absent reason -> sprintf "absent:%s" reason

        sha256Hex canonical

    /// The input hashes that actually enter the content address for a
    /// draft, honouring plan D18: a `HumanAsserted` fact folds the asserted
    /// value's hash into the slot (so a re-assertion of a different value
    /// gets a new id and supersedes); `Computed` / `Imported` use the
    /// declared input hashes verbatim.
    let effectiveInputHashes (method: MethodRef) (evidence: Evidence) (value: FactValue) : string list =
        match method with
        | HumanAsserted _ -> valueHash value :: evidence.InputHashes
        | Computed _
        | Imported _ -> evidence.InputHashes

    /// The content-addressed `FactId` (plan D1 / law L2) —
    /// `hash(subject, metric, period, method, inputHashes)`. Asserting an
    /// identical tuple always yields the same id, so the store converges
    /// under replay and duplication regardless of how many times a
    /// pipeline re-runs. `inputHashes` are order-normalised so input
    /// ordering does not change identity.
    let compute
        (subject: SubjectRef)
        (metric: MetricRef)
        (period: TemporalExtent)
        (method: MethodRef)
        (inputHashes: string list)
        : string =
        let inputs = inputHashes |> List.sort |> String.concat ","

        [
            canonicalSubject subject
            metric.Value
            canonicalPeriod period
            methodIdentity method
            inputs
        ]
        |> String.concat "\n"
        |> sha256Hex

    /// The `FactId` of a fully-populated fact — recomputed from its own
    /// fields (honouring the D18 human-asserted value-hash rule), so a
    /// store can verify an incoming fact's id rather than trust it
    /// (idempotency / tamper check).
    let computeFor (f: Fact) : string =
        compute f.Subject f.Metric f.Period f.Method (effectiveInputHashes f.Method f.Evidence f.Value)

    /// Whether a fact is a **forecast** — derived, never declared (plan
    /// D20): its valid time extends beyond its transaction time.
    let isForecast (f: Fact) : bool = f.Period.To > f.AsOf

/// Derived freshness of a fact — never a stored mutable flag (law L1 /
/// plan D2). `Stale` carries the instant the fact went stale (its `AsOf`
/// plus the freshness window) so a caller can render "as of / stale
/// since".
type FactFreshness =
    | Fresh
    | Stale of since: DateTime

/// Freshness derivation over a metric's `Grounding.StalenessPolicy`. Pure
/// — the store computes it per query rather than storing it, so it can
/// never drift from the truth the timestamps state.
module Freshness =

    /// Derive freshness for `fact` under `policy`, given whether the fact
    /// is still the *current* head of its lineage (`isCurrent`) and the
    /// evaluation instant `now`.
    ///
    ///  - `FreshFor window` — fresh while `now - AsOf <= window`.
    ///  - `UntilSuperseded` — fresh exactly while it is current (a
    ///    superseded fact is stale from its successor's arrival).
    ///  - `UntilUpstreamChange` — treated as `UntilSuperseded` here; the
    ///    upstream-change trigger that additionally stales a still-current
    ///    fact is the reactive-recomputation phase (Stage 3), not Stage 0.
    let derive
        (policy: ToolUp.Platform.Grounding.StalenessPolicy)
        (fact: Fact)
        (isCurrent: bool)
        (now: DateTime)
        : FactFreshness =
        match policy with
        | ToolUp.Platform.Grounding.FreshFor window ->
            let goesStaleAt = fact.AsOf + window
            if now <= goesStaleAt then Fresh else Stale goesStaleAt
        | ToolUp.Platform.Grounding.UntilSuperseded
        | ToolUp.Platform.Grounding.UntilUpstreamChange -> if isCurrent then Fresh else Stale fact.AsOf