// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 437 — per-component resource envelopes ─────────────────────
//
// A declared BUDGET keyed by `ComponentId`: how many jobs a component may
// run at once, how many requests per minute it may serve, how deep its
// queue may grow, and an advisory memory hint a backend interprets as it
// sees fit. Four optional numbers and one string — nothing here is a
// scheduler, a limiter, or a queue.
//
// **Policy as data, enforced at seams that already exist (GP 12).** The
// envelope is a *record the existing seams consult*: Phase 9b's job
// scheduler before it dispatches a component's handler, the rate-limit
// substrate before it admits a request, a bounded queue before it accepts
// an item. No new background service, no new dependency, no framework
// vocabulary in the record itself — the same reason `RetryPolicy` is data
// rather than an `OnFailure` callback.
//
// **`None` is unconstrained, and an ABSENT component is unconstrained
// too (GP 11 / GP 13).** `ResourceEnvelope.unconstrained` is the identity:
// every dimension `None`, admitting everything. A composition that
// declares no envelope resolves every component to it, every admission
// check short-circuits to `EnvelopeAdmitted`, and the health rollup reports no
// pressure dimension at all — byte-for-byte the pre-437 deployment.
//
// **Never a silent drop (GP 6).** An admission decision is a typed value
// (`EnvelopeAdmission`), and a refusal carries the component, the
// dimension, the limit and the observed level — enough for an audit
// record, a metric, and a back-pressure response, without the seam having
// to reconstruct why it said no. A seam that discards an over-budget item
// without surfacing the refusal is using this wrong.
//
// **Generic substrate (GP 1) + Fable-safe.** `ComponentId`, small closed
// DUs, ints, strings, `Map` — no vendor type, no BCL surface beyond
// `System.String`. An authoring / dashboard tool on either tier reads the
// same shapes.

/// One resource dimension a budget can constrain. Closed on purpose: each
/// case names a seam the SDK already owns a consultation point in, so a
/// new case is a new enforcement site rather than a free-form label.
///
/// `MemoryHint` is deliberately NOT a dimension — it is advisory, carries
/// no number to compare against, and no SDK seam refuses on it. It rides
/// on the envelope as a string a backend (a container scheduler, a host
/// sizing tool) interprets; nothing in this file acts on it.
type EnvelopeDimension =
    /// Concurrently-executing background jobs for one component — the
    /// Phase 9b scheduler's dispatch gate.
    | JobConcurrencyDimension
    /// Requests admitted per rolling minute for one component — the
    /// rate-limit seam.
    | RequestRateDimension
    /// Items resident in a component's queue — the ingestion / work-queue
    /// back-pressure seam.
    | QueueDepthDimension

/// One component's declared resource budget. Every field is optional and
/// `None` means UNCONSTRAINED — the value an undeclared component
/// resolves to, and the reason an envelope-free deployment behaves
/// exactly as it did before (GP 13).
///
/// A limit is an INCLUSIVE ceiling on the level *after* admitting: a
/// `MaxJobConcurrency = Some 2` component runs at most two handlers at
/// once, so the third concurrent request is refused. A non-positive limit
/// is rejected at construction rather than silently treated as "off" —
/// `Some 0` is a real intent (admit nothing) that reads identically to a
/// typo, so a limit is declared through the `withMax*` helpers, which
/// reject only the genuinely-impossible negative.
type ResourceEnvelope = {
    /// Ceiling on concurrently-executing background jobs for this
    /// component. `None` = the scheduler's existing behaviour.
    MaxJobConcurrency: int option
    /// Ceiling on requests admitted per rolling minute for this
    /// component. `None` = whatever the composed rate-limit policy already
    /// does; the envelope never LOOSENS a limit another layer imposes.
    MaxRequestsPerMinute: int option
    /// Ceiling on items resident in this component's queue. `None` = the
    /// queue's existing capacity behaviour.
    MaxQueueDepth: int option
    /// Advisory, backend-interpreted memory hint (e.g. `"512Mi"`). Carried
    /// for a container scheduler / host sizing tool to read; no SDK seam
    /// enforces it, and nothing in this file parses it.
    MemoryHint: string option
}

/// Every composed component's declared envelope, keyed by stable
/// `ComponentId` — the parallel of Phase 282's `CapabilitySignature`,
/// Phase 432's `RequirementsSignature` and Phase 433's
/// `FootprintSignature` over the same id space. An ABSENT id resolves to
/// `ResourceEnvelope.unconstrained`, so an undeclared component is never
/// constrained by the map's mere existence (GP 11).
type EnvelopeSignature = Map<ComponentId, ResourceEnvelope>

/// Why an admission was refused: the component, the dimension, the
/// declared ceiling, and the level observed when the seam asked. Enough
/// for an audit record, a metric, and a back-pressure response without
/// the seam reconstructing any of it (GP 6).
///
/// Field names are deliberately distinct from `EnvelopePressure`'s — two
/// records sharing a full field-name set make every unannotated
/// construction ambiguous under F#'s last-declared-wins inference, the
/// hazard recorded on `EventTopology.Participants`.
type EnvelopeRefusal = {
    /// The component whose budget was exceeded.
    RefusedComponent: ComponentId
    /// The dimension the refusal is about.
    RefusedDimension: EnvelopeDimension
    /// The declared ceiling that was reached.
    RefusedLimit: int
    /// The level observed when the seam asked — at or above the limit,
    /// since that is what made the answer a refusal.
    RefusedObserved: int
}

/// The typed outcome of asking whether one more unit fits inside a
/// component's envelope. A DU rather than a `bool` so a refusal cannot be
/// discarded without discarding the reason with it (GP 6) — the queue
/// seam's back-pressure signal, the scheduler's dispatch gate, and the
/// middleware's admission answer are all this one value.
type EnvelopeAdmission =
    /// The unit fits — either the dimension is unconstrained or the
    /// observed level leaves room. The overwhelmingly common answer, and
    /// the only one an envelope-free deployment ever produces.
    | EnvelopeAdmitted
    /// The unit does not fit; the refusal carries everything an audit /
    /// metric / response needs.
    | EnvelopeRefused of EnvelopeRefusal

/// One component's utilisation of one declared dimension — the Phase 290
/// health-rollup pressure reading. Produced ONLY for a dimension the
/// component actually declares, so a component with no envelope
/// contributes no pressure entries at all and the rollup is unchanged.
///
/// Field names are distinct from `EnvelopeRefusal`'s for the
/// field-inference reason recorded above.
type EnvelopePressure = {
    /// The component this reading is about.
    PressureComponent: ComponentId
    /// The dimension being reported.
    PressureDimension: EnvelopeDimension
    /// The level observed.
    PressureObserved: int
    /// The declared ceiling.
    PressureLimit: int
}

[<RequireQualifiedAccess>]
module EnvelopeDimension =

    /// A stable, human-readable token for a dimension — for reports,
    /// metric names, audit details and authoring tools. Never positional;
    /// matches the DU case.
    let toWireString (dimension: EnvelopeDimension) : string =
        match dimension with
        | JobConcurrencyDimension -> "job-concurrency"
        | RequestRateDimension -> "request-rate"
        | QueueDepthDimension -> "queue-depth"

    /// Read a persisted dimension token back. Unrecognised input raises: a
    /// dimension is a closed vocabulary tied to a real enforcement seam,
    /// and coercing an unknown token to one of them would fabricate an
    /// enforcement point.
    let ofWireString (token: string) : EnvelopeDimension =
        match token with
        | "job-concurrency" -> JobConcurrencyDimension
        | "request-rate" -> RequestRateDimension
        | "queue-depth" -> QueueDepthDimension
        | other -> invalidArg "token" ("Unknown resource-envelope dimension token '" + other + "'.")

    /// Every dimension, in the order reports render them.
    let all: EnvelopeDimension list = [ JobConcurrencyDimension; RequestRateDimension; QueueDepthDimension ]

/// Construction, resolution and pure admission arithmetic over resource
/// envelopes + the `EnvelopeSignature` that keys them by `ComponentId`.
/// Every function here is pure — nothing is counted, scheduled or
/// throttled until a seam asks (GP 13).
module ResourceEnvelope =

    // ── construction ──────────────────────────────────────────────────

    /// The identity: no dimension constrained. An UNDECLARED component
    /// resolves to this, so an empty signature admits everything and a
    /// pre-437 deployment is unchanged (GP 11).
    let unconstrained: ResourceEnvelope = {
        MaxJobConcurrency = None
        MaxRequestsPerMinute = None
        MaxQueueDepth = None
        MemoryHint = None
    }

    let private checkedLimit (dimension: EnvelopeDimension) (limit: int) : int =
        if limit < 0 then
            invalidArg
                "limit"
                ("A "
                 + EnvelopeDimension.toWireString dimension
                 + " envelope limit cannot be negative (got "
                 + string limit
                 + "). Use None to leave the dimension unconstrained.")

        limit

    /// Constrain job concurrency. `0` admits nothing — a real, if severe,
    /// intent; leave the dimension `None` to mean unconstrained.
    let withMaxJobConcurrency (limit: int) (envelope: ResourceEnvelope) : ResourceEnvelope = {
        envelope with
            MaxJobConcurrency = Some(checkedLimit JobConcurrencyDimension limit)
    }

    /// Constrain the per-rolling-minute request rate.
    let withMaxRequestsPerMinute (limit: int) (envelope: ResourceEnvelope) : ResourceEnvelope = {
        envelope with
            MaxRequestsPerMinute = Some(checkedLimit RequestRateDimension limit)
    }

    /// Constrain queue depth.
    let withMaxQueueDepth (limit: int) (envelope: ResourceEnvelope) : ResourceEnvelope = {
        envelope with
            MaxQueueDepth = Some(checkedLimit QueueDepthDimension limit)
    }

    /// Attach the advisory memory hint. Advisory only — no SDK seam reads
    /// it to refuse anything.
    let withMemoryHint (hint: string) (envelope: ResourceEnvelope) : ResourceEnvelope =
        if System.String.IsNullOrWhiteSpace hint then
            invalidArg "hint" "A memory hint must be non-empty. Omit the call to leave it unset."

        {
            envelope with
                MemoryHint = Some(hint.Trim())
        }

    /// Whether this envelope constrains nothing enforceable. A
    /// memory-hint-only envelope IS budget-free by this reading: the hint
    /// is advisory and refuses nothing, so a component carrying only a
    /// hint still admits everything.
    let isUnconstrained (envelope: ResourceEnvelope) : bool =
        Option.isNone envelope.MaxJobConcurrency
        && Option.isNone envelope.MaxRequestsPerMinute
        && Option.isNone envelope.MaxQueueDepth

    /// The declared ceiling for one dimension, if any.
    let limitFor (dimension: EnvelopeDimension) (envelope: ResourceEnvelope) : int option =
        match dimension with
        | JobConcurrencyDimension -> envelope.MaxJobConcurrency
        | RequestRateDimension -> envelope.MaxRequestsPerMinute
        | QueueDepthDimension -> envelope.MaxQueueDepth

    /// The dimensions this envelope actually constrains, in
    /// `EnvelopeDimension.all` order.
    let declaredDimensions (envelope: ResourceEnvelope) : EnvelopeDimension list =
        EnvelopeDimension.all
        |> List.filter (fun dimension -> Option.isSome (limitFor dimension envelope))

    // ── the signature ─────────────────────────────────────────────────

    /// The empty signature — no component declares a budget. Every derived
    /// surface degrades against it: admission is `EnvelopeAdmitted`, pressure is
    /// empty, no enforcement adapter is registered (GP 13).
    let emptySignature: EnvelopeSignature = Map.empty

    /// Declare one component's envelope in a signature, replacing any
    /// prior declaration for the same id. REPLACE rather than merge:
    /// unlike a footprint (a set union) a budget is a single decision, and
    /// silently combining two declarations of `MaxJobConcurrency` would
    /// pick a winner the caller never chose.
    let declare (componentId: ComponentId) (envelope: ResourceEnvelope) (signature: EnvelopeSignature) =
        Map.add componentId envelope signature

    /// Build a signature from `(id, envelope)` pairs. A repeated id keeps
    /// the LAST declaration, matching `declare`.
    let signatureOf (entries: (ComponentId * ResourceEnvelope) seq) : EnvelopeSignature =
        entries
        |> Seq.fold (fun acc (componentId, envelope) -> declare componentId envelope acc) Map.empty

    /// Look a component's envelope up, falling back to `unconstrained` for
    /// an undeclared component — the single reason an absent id never
    /// constrains anything (GP 11). Every enforcement path resolves
    /// through this.
    let resolve (signature: EnvelopeSignature) (componentId: ComponentId) : ResourceEnvelope =
        signature |> Map.tryFind componentId |> Option.defaultValue unconstrained

    /// Every declared envelope, ordered by `ComponentId` so reports are
    /// deterministic.
    let all (signature: EnvelopeSignature) : (ComponentId * ResourceEnvelope) list =
        signature |> Map.toList |> List.sortBy (fst >> ComponentId.value)

    /// Whether any component in the signature constrains anything
    /// enforceable — the gate on registering an enforcement adapter at
    /// all (GP 13). A signature of memory-hint-only envelopes answers
    /// `false`, because nothing would ever be refused.
    let anyConstrained (signature: EnvelopeSignature) : bool =
        signature |> Map.exists (fun _ envelope -> not (isUnconstrained envelope))

    /// Restrict a signature to a set of composed component ids — keeping a
    /// stale declaration for an UNCOMPOSED component out of the enforced
    /// surface. An empty id set restricts to nothing.
    let restrictTo (composed: Set<ComponentId>) (signature: EnvelopeSignature) : EnvelopeSignature =
        signature |> Map.filter (fun componentId _ -> composed.Contains componentId)

    // ── admission (the pure arithmetic every seam shares) ─────────────

    /// Whether one more unit fits, given the level ALREADY in flight.
    /// `observed` is the count before admitting, so the answer is
    /// `EnvelopeAdmitted` while `observed < limit`.
    ///
    /// An unconstrained dimension short-circuits to `EnvelopeAdmitted` without
    /// touching `observed` — which is what makes the envelope-free path
    /// free (GP 13).
    let admit
        (dimension: EnvelopeDimension)
        (observed: int)
        (componentId: ComponentId)
        (envelope: ResourceEnvelope)
        : EnvelopeAdmission =
        match limitFor dimension envelope with
        | None -> EnvelopeAdmitted
        | Some limit when observed < limit -> EnvelopeAdmitted
        | Some limit ->
            EnvelopeRefused {
                RefusedComponent = componentId
                RefusedDimension = dimension
                RefusedLimit = limit
                RefusedObserved = observed
            }

    /// `admit` against a whole signature, resolving the component's
    /// envelope first. The shape every seam adapter calls.
    let admitIn
        (signature: EnvelopeSignature)
        (dimension: EnvelopeDimension)
        (observed: int)
        (componentId: ComponentId)
        : EnvelopeAdmission =
        if Map.isEmpty signature then
            EnvelopeAdmitted
        else
            admit dimension observed componentId (resolve signature componentId)

    /// Whether an admission let the unit through.
    let isAdmitted (admission: EnvelopeAdmission) : bool =
        match admission with
        | EnvelopeAdmitted -> true
        | EnvelopeRefused _ -> false

    /// The refusal, when there was one.
    let refusal (admission: EnvelopeAdmission) : EnvelopeRefusal option =
        match admission with
        | EnvelopeAdmitted -> None
        | EnvelopeRefused r -> Some r

    /// One line naming what was refused and why — the audit detail and
    /// metric label every enforcement site renders through, so two seams
    /// never word the same refusal differently.
    let describeRefusal (r: EnvelopeRefusal) : string =
        "component '"
        + ComponentId.value r.RefusedComponent
        + "' is at its declared "
        + EnvelopeDimension.toWireString r.RefusedDimension
        + " envelope ("
        + string r.RefusedObserved
        + " of "
        + string r.RefusedLimit
        + ")"

    // ── pressure (437.D — the Phase 290 rollup dimension) ────────────

    /// Utilisation as a percentage of the declared ceiling, rounded DOWN
    /// so a reading never reports 100% before the ceiling is actually
    /// reached. A `0` limit reports 100% at any observed level — it admits
    /// nothing, so it is saturated by definition.
    let utilisationPercent (pressure: EnvelopePressure) : int =
        if pressure.PressureLimit <= 0 then
            100
        else
            pressure.PressureObserved * 100 / pressure.PressureLimit

    /// A pressure reading for one declared dimension, or `None` when the
    /// component does not declare that dimension — the "no envelope →
    /// dimension absent" rule, in one place.
    let pressureFor
        (dimension: EnvelopeDimension)
        (observed: int)
        (componentId: ComponentId)
        (envelope: ResourceEnvelope)
        : EnvelopePressure option =
        limitFor dimension envelope
        |> Option.map (fun limit -> {
            PressureComponent = componentId
            PressureDimension = dimension
            PressureObserved = observed
            PressureLimit = limit
        })

    /// Every pressure reading for one component, given a level per
    /// dimension. Dimensions the component does not declare are absent
    /// from the result — never present with a null / zero limit, which
    /// would read as a declared budget of nothing.
    let pressuresFor
        (observedBy: EnvelopeDimension -> int)
        (componentId: ComponentId)
        (envelope: ResourceEnvelope)
        : EnvelopePressure list =
        declaredDimensions envelope
        |> List.choose (fun dimension -> pressureFor dimension (observedBy dimension) componentId envelope)

    /// One line describing a pressure reading — the rollup's rendering.
    let describePressure (pressure: EnvelopePressure) : string =
        EnvelopeDimension.toWireString pressure.PressureDimension
        + " "
        + string pressure.PressureObserved
        + "/"
        + string pressure.PressureLimit
        + " ("
        + string (utilisationPercent pressure)
        + "%)"