// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Globalization
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

/// Rendering for a `SubjectRef` — the canonical readable form
/// (`hierarchy/level>level`) used by audit events, provenance nodes, and
/// the fact-evidence seam, so those surfaces never drift.
module SubjectRef =
    let toString (s: SubjectRef) : string =
        sprintf "%s/%s" s.Hierarchy (String.concat ">" s.Path)

/// Rendering for a `Disclosure` — the canonical string form
/// (`Surfaceable` / `Internal` / `Restricted(policy)`) shared by the audit
/// trail and the provenance/evidence surfaces.
module Disclosure =
    let toString (d: Disclosure) : string =
        match d with
        | Surfaceable -> "Surfaceable"
        | Internal -> "Internal"
        | Restricted policyRef -> sprintf "Restricted(%s)" policyRef

    /// The exact inverse of `toString`. `None` for anything this build does
    /// not recognise — a stance it cannot read is never silently defaulted,
    /// because every default is either a widening (unsafe) or a narrowing
    /// that misreports what the source actually said. The caller refuses.
    ///
    /// Lives beside `toString` deliberately: a stance rendered into a
    /// signed artefact by one deployment is read back by another, and a
    /// parser written at the reading end would be a second definition of
    /// the wire shape, free to drift from the one that writes it.
    let tryParse (s: string) : Disclosure option =
        if isNull s then
            None
        else
            match s.Trim() with
            | "Surfaceable" -> Some Surfaceable
            | "Internal" -> Some Internal
            | t when
                t.StartsWith("Restricted(", StringComparison.Ordinal)
                && t.EndsWith(")", StringComparison.Ordinal)
                ->
                // A policy ref may itself contain parentheses; take
                // everything between the FIRST '(' and the LAST ')'.
                Some(Restricted(t.Substring(11, t.Length - 12)))
            | _ -> None

    /// The **conservative floor** (the meet) of two stances — the more
    /// restrictive of the two, under the Phase 525 egress predicate's own
    /// ordering:
    ///
    ///   `Internal`  ≤  `Restricted p`  ≤  `Surfaceable`
    ///
    /// `Internal` is the bottom because it is denied at every surface
    /// unconditionally, while a `Restricted` policy MAY permit some
    /// surface; `Surfaceable` is the top because it is permitted at every
    /// surface subject only to composition policy above.
    ///
    /// **Two `Restricted` stances under DIFFERENT policy refs meet at
    /// `Internal`**, not at either ref. They are incomparable: nothing
    /// tells this deployment that satisfying one policy satisfies the
    /// other, and picking either would assert a permission neither source
    /// granted. Collapsing to the bottom is the only answer that cannot
    /// widen — and because the choice is recorded wherever the meet is
    /// taken, it is a visible narrowing rather than a silent one.
    ///
    /// Commutative, associative, idempotent, with `Surfaceable` as the
    /// identity — so a boundary that imposes no ceiling of its own is
    /// byte-for-byte the stance it was handed (GP 11).
    let floor (a: Disclosure) (b: Disclosure) : Disclosure =
        match a, b with
        | Internal, _
        | _, Internal -> Internal
        | Surfaceable, other
        | other, Surfaceable -> other
        | Restricted p, Restricted q when p = q -> Restricted p
        | Restricted _, Restricted _ -> Internal

/// Canonical display rendering of a `FactValue` under a metric's declared
/// `DisplayFormat` (a .NET numeric format string, or "" for verbatim) —
/// THE display form an answer quotes verbatim, shared by the retrieval
/// resolver and the fact-reading tool surfaces so a value never renders
/// two ways at two doors. Invariant culture throughout — the rendering is
/// a wire/prompt value, never localised.
///
/// A door that holds the metric renders with `renderFor`, which reads the
/// metric's declared currency; `render` is for a caller holding only a
/// format string.
module FactRendering =

    let private renderWith (fmt: decimal -> string) (value: FactValue) : string =
        match value with
        | Scalar d -> fmt d
        | Interval(lo, hi) -> sprintf "%s to %s" (fmt lo) (fmt hi)
        // A series fact references a data-object vintage, never inlines
        // points (the scale rule) — render the reference.
        | Series version -> sprintf "series %s" version
        | Distribution buckets ->
            buckets
            |> Map.toList
            |> List.map (fun (k, d) -> sprintf "%s: %s" k (fmt d))
            |> String.concat "; "
        | Categorical c -> c
        // An `Absent` fact is a queryable data gap — surfaced honestly
        // (GP 9: never fabricated into a number).
        | Absent reason -> sprintf "absent (%s)" reason

    let private formatWith (displayFormat: string) (numbers: NumberFormatInfo) (d: decimal) : string =
        if String.IsNullOrWhiteSpace displayFormat then
            d.ToString numbers
        else
            d.ToString(displayFormat, numbers)

    /// Renders `value` under `displayFormat` alone. A `C` format renders the
    /// invariant culture's generic currency symbol `¤`, because a format
    /// string names no currency.
    let render (displayFormat: string) (value: FactValue) : string =
        renderWith (formatWith displayFormat NumberFormatInfo.InvariantInfo) value

    /// A standard currency format: `C` or `c`, with an optional precision.
    /// A custom format (`"£#,##0"`) is not one, and renders as written.
    let private isCurrencyFormat (displayFormat: string) : bool =
        not (String.IsNullOrEmpty displayFormat)
        && (displayFormat.[0] = 'C' || displayFormat.[0] = 'c')
        && displayFormat |> Seq.skip 1 |> Seq.forall Char.IsDigit

    /// Renders `value` as the metric declares it. A metric whose
    /// dimensionality is `currency`, whose `Unit` is an ISO 4217 code and
    /// whose display format is a `C` format renders with that currency's
    /// symbol (`CurrencySymbols.prefix`): `£21,240` where `render` gives
    /// `¤21,240`. Grouping, the decimal separator, the digits and the
    /// negative form `(£21,240)` stay invariant. Every other metric renders
    /// exactly as `render` does with its `DisplayFormat`, and no metric as
    /// `render ""`.
    let renderFor (metric: ToolUp.Platform.Grounding.MetricDefinition option) (value: FactValue) : string =
        match metric with
        | None -> render "" value
        | Some m ->
            let symbol =
                if
                    String.Equals(m.Dimensionality, "currency", StringComparison.OrdinalIgnoreCase)
                    && isCurrencyFormat m.DisplayFormat
                then
                    CurrencySymbols.prefix m.Unit
                else
                    None

            match symbol with
            | None -> render m.DisplayFormat value
            | Some symbol ->
                let numbers = NumberFormatInfo.InvariantInfo.Clone() :?> NumberFormatInfo
                numbers.CurrencySymbol <- symbol
                renderWith (formatWith m.DisplayFormat numbers) value

/// Content-addressing, lineage keys, and freshness — the decidable laws
/// (L1–L4) the store is built on, factored out so both the store and its
/// contract tests share one implementation.
module Fact =

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

        ToolUp.Platform.Sha256.hexOfUtf8 canonical

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

    /// The frame's first field — the recipe label that keeps a fact id
    /// from ever equalling another kind of content id over the same values.
    [<Literal>]
    let IdLabel = "fact"

    /// The content-addressed `FactId` (plan D1 / law L2) —
    /// `CanonicalFrame.digest ("fact" :: subject :: metric :: period ::
    /// method :: sorted inputHashes)` (Phase 1089). Asserting an identical
    /// tuple always yields the same id, so the store converges under replay
    /// and duplication regardless of how many times a pipeline re-runs.
    /// `inputHashes` are ordinal-sorted so input ordering does not change
    /// identity, and each is its own field, so no input can be re-cut into
    /// two. The subject, period and method fields are the renderings the
    /// lineage key uses.
    let compute
        (subject: SubjectRef)
        (metric: MetricRef)
        (period: TemporalExtent)
        (method: MethodRef)
        (inputHashes: string list)
        : string =
        ToolUp.Platform.CanonicalFrame.digest [
            IdLabel
            canonicalSubject subject
            metric.Value
            canonicalPeriod period
            methodIdentity method
            yield! List.sortWith (fun a b -> String.CompareOrdinal(a, b)) inputHashes
        ]

    /// The `FactId` of a fully-populated fact — recomputed from its own
    /// fields (honouring the D18 human-asserted value-hash rule), so a
    /// store can verify an incoming fact's id rather than trust it
    /// (idempotency / tamper check).
    let computeFor (f: Fact) : string =
        compute f.Subject f.Metric f.Period f.Method (effectiveInputHashes f.Method f.Evidence f.Value)

    /// Whether a fact is a **forecast** — derived, never declared (plan
    /// D20): its valid time extends beyond its transaction time.
    let isForecast (f: Fact) : bool = f.Period.To > f.AsOf

/// The self-describing form an input's content hash takes in a fact's
/// `Evidence.InputHashes` (Phase 994): `<algorithm>:<lowercase hex>`, as in
/// `sha256:9f04…`. A producer names the files or objects a fact was
/// computed from in this form, so a reader can tell them apart from the
/// bare value hashes and run tokens that share the list.
module InputHash =

    /// The fewest hex digits an input hash may carry — a 128-bit digest.
    [<Literal>]
    let MinimumDigits = 32

    let private isAlgorithmChar (c: char) =
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')

    let private isHexChar (c: char) =
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')

    /// Whether `value` is in the `<algorithm>:<lowercase hex>` form: a
    /// non-empty lowercase alphanumeric algorithm name, one colon, and an
    /// even number of lowercase hex digits, at least `MinimumDigits`.
    let isContentHash (value: string) : bool =
        if isNull value then
            false
        else
            match value.IndexOf ':' with
            | colon when colon > 0 ->
                let algorithm = value.Substring(0, colon)
                let digest = value.Substring(colon + 1)

                algorithm |> Seq.forall isAlgorithmChar
                && digest.Length >= MinimumDigits
                && digest.Length % 2 = 0
                && digest |> Seq.forall isHexChar
            | _ -> false

    /// The inputs a fact's evidence names: its input hashes in the content
    /// hash form, in the order the evidence lists them.
    let named (evidence: Evidence) : string list =
        evidence.InputHashes |> List.filter isContentHash

/// The method a derived table's run declares it was produced by (Phase
/// 1040): the derivation's stable id and the version of it that ran — as in
/// `dailytrade.derive` at `v1`. Where `Evidence.InputHashes` names WHAT a fact
/// was computed from, this names HOW.
type DeclaredMethod = {
    /// The method's stable id.
    MethodId: string
    /// The version of the method that ran.
    MethodVersion: string
}

/// The form a declared method takes in a fact's evidence (Phase 1040).
///
/// A run that declares a method stamps every fact it writes with one token,
/// `method:<id>@<version>`, among the fact's input hashes. That placement is
/// deliberate: it puts the method in the fact's content address — so a run by
/// another method mints a new fact rather than an idempotent skip — while
/// leaving it OUT of the lineage key (`Fact.lineageKey`), so the facts two
/// methods write for one table, subject and period are one lineage and the
/// later supersedes the earlier. The method is evidence, never identity. The
/// token is never in the `InputHash` form (it carries an `@`), so
/// `InputHash.named` never mistakes it for an input.
module DeclaredMethod =

    /// The prefix of a declared method's evidence token.
    [<Literal>]
    let TokenPrefix = "method:"

    let private wellFormed (part: string) =
        not (String.IsNullOrEmpty part)
        && part |> Seq.forall (fun c -> not (Char.IsWhiteSpace c || Char.IsControl c))

    /// A declared method from its id and version.
    let create (methodId: string) (methodVersion: string) : DeclaredMethod = {
        MethodId = methodId
        MethodVersion = methodVersion
    }

    /// Why a method cannot be declared, or `None` when it can: its id and
    /// version are non-empty, free of whitespace and control characters, and
    /// its id carries no `@` (the token's separator).
    let problem (m: DeclaredMethod) : string option =
        if isNull (box m) then
            Some "the method is null"
        elif not (wellFormed m.MethodId) || m.MethodId.Contains '@' then
            Some(
                sprintf
                    "method id '%s' must be non-empty, without whitespace, control characters or '@'"
                    (if isNull m.MethodId then "" else m.MethodId)
            )
        elif not (wellFormed m.MethodVersion) then
            Some(
                sprintf
                    "method version '%s' must be non-empty, without whitespace or control characters"
                    (if isNull m.MethodVersion then "" else m.MethodVersion)
            )
        else
            None

    /// The evidence token of a method: `method:<id>@<version>`.
    let token (m: DeclaredMethod) : string =
        sprintf "%s%s@%s" TokenPrefix m.MethodId m.MethodVersion

    /// The method a token names, or `None` for anything else.
    let tryParse (value: string) : DeclaredMethod option =
        if isNull value || not (value.StartsWith(TokenPrefix, StringComparison.Ordinal)) then
            None
        else
            let rest = value.Substring TokenPrefix.Length

            match rest.IndexOf '@' with
            | at when at > 0 ->
                let m = create (rest.Substring(0, at)) (rest.Substring(at + 1))
                if (problem m).IsNone then Some m else None
            | _ -> None

    /// The method a fact's evidence names, or `None` when it names none — a
    /// fact written by a run that declared no method.
    let ofEvidence (evidence: Evidence) : DeclaredMethod option =
        evidence.InputHashes |> List.tryPick tryParse

    /// The readable form a citation shows: `<id> <version>`.
    let describe (m: DeclaredMethod) : string =
        sprintf "%s %s" m.MethodId m.MethodVersion

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

    /// Derive freshness from a transaction time alone — the whole of what
    /// the derivation reads off a fact (Phase 702).
    ///
    ///  - `FreshFor window` — fresh while `now - asOf <= window`.
    ///  - `UntilSuperseded` — fresh exactly while it is current (a
    ///    superseded fact is stale from its successor's arrival).
    ///  - `UntilUpstreamChange` — treated as `UntilSuperseded` here; the
    ///    upstream-change trigger that additionally stales a still-current
    ///    fact is the reactive-recomputation phase (Stage 3), not Stage 0.
    ///
    /// Exposed separately from `derive` because a **projection** of a fact
    /// — the population read model's current-heads surface — carries the
    /// transaction time without carrying the fact. Sharing the derivation
    /// rather than restating it is what makes the projected answer equal
    /// the enumerated one by construction rather than by a second reading
    /// of this comment.
    let deriveAt
        (policy: ToolUp.Platform.Grounding.StalenessPolicy)
        (asOf: DateTime)
        (isCurrent: bool)
        (now: DateTime)
        : FactFreshness =
        match policy with
        | ToolUp.Platform.Grounding.FreshFor window ->
            let goesStaleAt = asOf + window
            if now <= goesStaleAt then Fresh else Stale goesStaleAt
        | ToolUp.Platform.Grounding.UntilSuperseded
        | ToolUp.Platform.Grounding.UntilUpstreamChange -> if isCurrent then Fresh else Stale asOf

    /// Derive freshness for `fact` under `policy`, given whether the fact
    /// is still the *current* head of its lineage (`isCurrent`) and the
    /// evaluation instant `now`.
    let derive
        (policy: ToolUp.Platform.Grounding.StalenessPolicy)
        (fact: Fact)
        (isCurrent: bool)
        (now: DateTime)
        : FactFreshness =
        deriveAt policy fact.AsOf isCurrent now