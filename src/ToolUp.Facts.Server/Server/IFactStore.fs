// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Security.Cryptography
open System.Text

// ─── Fact-store audit events (Phase 520) ─────────────────────────────
//
// The fact store audits to `IEventStore` under a **reserved source
// module** (`_facts`) rather than adding cases to the core `AuditEvent`
// DU — the `ILineageStore` pattern (which writes `_platform.lineage`
// events). This keeps the companion self-contained (GP 1: no core edit)
// while producing a durable, scope-isolated, queryable audit record
// (`IEventStore.ReadBySource scope "_facts"`) that the later provenance
// chain (Phase 524) walks. PII-free: identifiers + classification only.

/// Reserved source-module + event-type discriminators for the fact-store
/// audit trail.
module FactEvents =
    /// `ModuleEvent.SourceModule` for every fact-store audit event. Filter
    /// `IEventStore.ReadBySource scope FactEvents.SourceModule` for the
    /// fact audit trail in isolation.
    [<Literal>]
    let SourceModule = "_facts"

    /// A new fact was asserted.
    [<Literal>]
    let AssertedType = "FactAsserted"

    /// An assertion superseded the current head of its lineage.
    [<Literal>]
    let SupersededType = "FactSuperseded"

    /// A **batch** assertion completed (Phase 704) — one summarised row
    /// per `IFactStore.AssertBatch` call, carrying its
    /// `BatchAssertReceipt`, in place of the per-fact `FactAsserted` /
    /// `FactSuperseded` pair a scalar `Assert` emits.
    [<Literal>]
    let BatchAssertedType = "FactBatchAsserted"

/// Payload of a `FactAsserted` audit event (JSON-serialised into
/// `ModuleEvent.Payload`).
type FactAssertedEvent = {
    FactId: string
    /// Readable subject reference (`hierarchy/level>level`).
    Subject: string
    Metric: string
    /// Method identity (`computed:op:ver:hash` / `asserted:principal` /
    /// `imported:cert`).
    Method: string
    /// Disclosure class at birth (`Surfaceable` / `Internal` /
    /// `Restricted(policy)`).
    Disclosure: string
    /// Transaction time the fact entered the store.
    AsOf: DateTime
}

/// Payload of a `FactSuperseded` audit event.
type FactSupersededEvent = {
    NewFactId: string
    SupersededFactId: string
    Subject: string
    Metric: string
    AsOf: DateTime
}

// ─── Batch assertion (Phase 704) ─────────────────────────────────────
//
// The write-side twin of the Phase 701 cross-subject read. A
// population-scale computation run asserts one fact per subject over
// 10⁵–10⁶ subjects; through the per-fact `Assert` that is 10⁵+ audit
// events and blob round trips per run. `AssertBatch` amortises the
// round trips and summarises the audit into ONE row, with every per-fact
// semantic unchanged — each draft still gets its content-addressed id,
// its idempotency (law L2), and its derived supersession edge within its
// own lineage (law L3).
//
// What is deliberately NOT amortised is attribution. GP 6 says every
// assertion is audited; the batch keeps that promise through a digest
// over the batch's full ordered disposition rather than through one row
// each, so a summarised event still pins exactly which facts were
// asserted, which superseded, and which were skipped — recomputable by
// anyone holding the drafts.

/// The disposition of one draft inside a batch assertion — the three
/// outcomes `IFactStore.AssertBatch` distinguishes. They **partition** a
/// batch: every draft is exactly one of them.
type BatchAssertOutcome =
    /// Newly stored, and first in its lineage — nothing was superseded.
    | BatchAsserted
    /// Newly stored, and it superseded its lineage's current head. Still
    /// a write; counted apart because "how much of this population
    /// actually moved" is the question a producer asks of a re-run.
    | BatchSuperseding
    /// Its content address was already stored, so the batch wrote
    /// nothing for it (law L2). Re-running an unchanged population is
    /// all-idempotent, and the receipt says so.
    | BatchIdempotent

/// The canonical spelling of a `BatchAssertOutcome` — one renderer,
/// shared by the receipt's digest and the audit payload, so the digest a
/// verifier recomputes is the digest the store wrote.
module BatchAssertOutcome =

    let toString (outcome: BatchAssertOutcome) : string =
        match outcome with
        | BatchAsserted -> "asserted"
        | BatchSuperseding -> "superseding"
        | BatchIdempotent -> "idempotent"

/// What one `AssertBatch` call did. The three counts partition
/// `DraftCount`; the per-outcome id lists are **capped**
/// (`BatchAssertReceipt.IdListCap`) so a receipt over a million drafts
/// stays a receipt rather than becoming the batch again, and `Digest`
/// covers the FULL ordered set regardless of the cap.
type BatchAssertReceipt = {
    /// Drafts submitted.
    DraftCount: int
    /// Drafts newly stored as the first fact of their lineage.
    AssertedCount: int
    /// Drafts newly stored that superseded a predecessor.
    SupersedingCount: int
    /// Drafts whose content address was already stored — no write.
    IdempotentCount: int
    /// Content addresses of the newly-asserted facts, capped.
    AssertedFactIds: string list
    /// Content addresses of the superseding facts, capped.
    SupersedingFactIds: string list
    /// Content addresses of the idempotent skips, capped.
    IdempotentFactIds: string list
    /// Whether any of the three lists was truncated by the cap. Stated
    /// rather than inferred from a length, so a reader never mistakes a
    /// capped list for a complete one.
    Truncated: bool
    /// SHA-256 over the batch's full ordered disposition — every draft's
    /// outcome and content address, in submission order. This is what
    /// keeps the summarised audit honest (GP 6): a producer holding the
    /// drafts recomputes it and learns whether the store saw the batch it
    /// sent, in the order it sent it, with the outcomes it was told.
    Digest: string
}

/// Folding a batch's ordered dispositions into its receipt, and the
/// digest that pins the full set. Shared rather than restated in each
/// store, so two implementations of `AssertBatch` produce the same
/// digest over the same batch by construction.
module BatchAssertReceipt =

    /// How many fact ids a receipt carries per outcome before it
    /// truncates. Sized so an ordinary batch is reported in full and a
    /// population-scale one is reported by digest.
    [<Literal>]
    let IdListCap = 64

    let private sha256Hex (s: string) : string =
        use sha = SHA256.Create()

        sha.ComputeHash(Encoding.UTF8.GetBytes s)
        |> Array.map (sprintf "%02x")
        |> String.concat ""

    /// The digest over a batch's full ordered disposition. Order is
    /// submission order and it is significant: two batches over the same
    /// drafts in different orders can legitimately produce different
    /// supersession outcomes, so a digest blind to order would call them
    /// the same batch.
    let digest (dispositions: (BatchAssertOutcome * string) list) : string =
        dispositions
        |> List.map (fun (outcome, factId) -> sprintf "%s:%s" (BatchAssertOutcome.toString outcome) factId)
        |> String.concat "\n"
        |> sha256Hex

    /// Fold a batch's ordered dispositions into its receipt.
    let ofDispositions (dispositions: (BatchAssertOutcome * string) list) : BatchAssertReceipt =
        let idsOf outcome =
            dispositions |> List.filter (fun (o, _) -> o = outcome) |> List.map snd

        let asserted = idsOf BatchAsserted
        let superseding = idsOf BatchSuperseding
        let idempotent = idsOf BatchIdempotent

        {
            DraftCount = List.length dispositions
            AssertedCount = List.length asserted
            SupersedingCount = List.length superseding
            IdempotentCount = List.length idempotent
            AssertedFactIds = asserted |> List.truncate IdListCap
            SupersedingFactIds = superseding |> List.truncate IdListCap
            IdempotentFactIds = idempotent |> List.truncate IdListCap
            Truncated =
                [ asserted; superseding; idempotent ]
                |> List.exists (fun ids -> List.length ids > IdListCap)
            Digest = digest dispositions
        }

    /// The receipt of a batch that asserted nothing — an empty draft
    /// list. "Nothing to do" is an answer, not a failure.
    let empty: BatchAssertReceipt = ofDispositions []

/// Payload of a `FactBatchAsserted` audit event — the whole receipt, so
/// the audit trail carries what the caller was told rather than a
/// re-rendering of it.
type FactBatchAssertedEvent = {
    Receipt: BatchAssertReceipt
    /// Transaction time of the batch: the latest `AsOf` it stamped, or
    /// the store's clock when it stamped none (an all-idempotent batch).
    AsOf: DateTime
}

// ─── IFactStore (Phase 520) ──────────────────────────────────────────
//
// The server-side, team-scoped surface over the bitemporal fact base.
// Append-only: `Assert` never mutates — a changed input supersedes the
// current lineage head with a *derived* edge; an identical tuple is
// idempotent (content-addressed). Reads honour law L4 visibility
// (`AsOf`), and supersession chains are walkable.
//
// **Six portability rules (GP 12).**
//  1. Identity by value — `string` scope / fact ids, domain records; no
//     live handles.
//  2. Async at every boundary — every method returns `Async<_>`.
//  3. Retry / failure as data — `Assert` returns `Result<_, string>`; no
//     `OnFailure` callbacks.
//  4. Stateless between calls — no per-call state survives on the store;
//     every read recomputes from the backing store.
//  5. No cross-shard ordering — ordering is only meaningful within one
//     `scopeId` (the shard key); the store promises none across scopes.
//  6. Precision at the lower bound — transaction time (`AsOf`) is stamped
//     at second precision from the store's injected clock; callers must
//     not assume sub-second `AsOf` ordering.
//
// **`AssertBatch` (Phase 704) is held to the same six**, and two of them
// decided its shape rather than merely permitting it:
//
//  - *Rule 3 (retry / failure as data).* A batch that cannot be asserted
//    returns `Error` naming the offending drafts by position — never a
//    throw, never a partial `Ok` a caller has to diff against its own
//    input to interpret. Malformed drafts are rejected BEFORE any write,
//    so the batch is genuinely the atom a producer retries.
//  - *Rule 5 (no cross-shard ordering).* A batch is scoped to ONE
//    `scopeId`, and its drafts are ordered only relative to each other:
//    the receipt's digest is taken in submission order because two
//    drafts in one lineage supersede in the order they were submitted,
//    but nothing is promised about ordering against a concurrent batch
//    in another scope.
//
// Rule 1 holds (drafts and receipts are values; ids are strings), rule 2
// holds (`Async<_>`), rule 4 holds (a batch carries no state past its
// own call — the lineage heads it derives against are read from the log
// at the start of the call and discarded at the end), and rule 6 is
// unchanged: the batch stamps `AsOf` from the same clock under the same
// strictly-increasing-within-a-lineage rule.

/// A caller's proposed fact — everything except the machine-derived
/// fields. The store computes the content-addressed `FactId`, stamps the
/// transaction time (`AsOf`) from its clock, and derives the `Supersedes`
/// edge; a caller never supplies those.
type FactDraft = {
    Subject: SubjectRef
    Metric: MetricRef
    Value: FactValue
    Period: TemporalExtent
    Method: MethodRef
    Evidence: Evidence
    Confidence: Confidence option
    Disclosure: Disclosure
}

/// Structural well-formedness of a draft (Phase 704).
module FactDraft =

    /// The structural defects that make a draft unassertable, or the
    /// empty list. Each defect is a short phrase the caller-facing
    /// refusal quotes verbatim beside the draft's position.
    ///
    /// **Deliberately narrow: this is well-formedness, not semantics.**
    /// It refuses only what the store's own laws cannot express — an
    /// unaddressable identity component (an empty metric id, hierarchy,
    /// path member, or method identity field), and a valid-time extent
    /// that is not half-open (`From >= To`), which no period-overlap
    /// clause can ever match, so such a fact is written and then invisible
    /// to every read that filters by period. It does NOT check the metric
    /// against a registry, the subject against a hierarchy, or the value
    /// against a unit: those are grounding questions, answered by the
    /// registry-aware layers above, and a store that refused on them would
    /// make an unregistered intermediate unassertable.
    ///
    /// **Applied by `AssertBatch`, NOT by `Assert`** — see the
    /// `AssertBatch` contract for why (GP 11: a scalar `Assert` that
    /// began refusing drafts it accepts today is a break, and the scalar
    /// path has no atomicity claim that a pre-flight protects).
    let defects (draft: FactDraft) : string list = [
        if String.IsNullOrWhiteSpace draft.Metric.Value then
            "metric id is empty"

        if String.IsNullOrWhiteSpace draft.Subject.Hierarchy then
            "subject hierarchy id is empty"

        if draft.Subject.Path |> List.exists String.IsNullOrWhiteSpace then
            "subject path has an empty member id"

        // Raw comparison, not a `ToUniversalTime` normalisation — the
        // store's own `periodsOverlap` compares these values as they
        // stand, so this refuses exactly the extents that can never match.
        if draft.Period.From >= draft.Period.To then
            "period is not a half-open [From, To) extent"

        match draft.Method with
        | Computed(operationId, version, _) ->
            if String.IsNullOrWhiteSpace operationId then
                "method operation id is empty"

            if String.IsNullOrWhiteSpace version then
                "method version is empty"
        | HumanAsserted principal ->
            if String.IsNullOrWhiteSpace principal then
                "method principal is empty"
        | Imported certificateRef ->
            if String.IsNullOrWhiteSpace certificateRef then
                "method certificate ref is empty"
    ]

/// Read filter for `IFactStore.Query`. Every clause is optional and
/// AND-combined; an all-`None` query returns every current fact in scope.
type FactQuery = {
    /// Restrict to one subject instance.
    Subject: SubjectRef option
    /// Restrict to one metric.
    Metric: MetricRef option
    /// Restrict to facts whose `Period` overlaps this extent.
    PeriodOverlaps: TemporalExtent option
    /// Restrict to one method's lineage — the explicit mechanism for
    /// picking one among *competing* facts (plan D19: several methods
    /// computing one metric is normal; the store never merges them).
    /// `None` defaults to the metric's registry-declared canonical method
    /// when one exists (Phase 566 — `MetricDefinition.CanonicalMethod`);
    /// naming a method here always overrides that default, and a metric
    /// with no declaration surfaces every competing head as before.
    Method: MethodRef option
    /// Law L4 visibility instant — the fact base *as of* this transaction
    /// time. `None` = now (the current head of each lineage). `Some t`
    /// reconstructs "what we knew at `t`".
    AsOf: DateTime option
    /// Include superseded facts (full history per lineage). `false` (the
    /// default) returns only the current head visible at `AsOf`.
    IncludeSuperseded: bool
}

/// Construction helpers for the common query shapes.
module FactQuery =

    /// The empty query — every current fact in scope, visible now.
    let all: FactQuery = {
        Subject = None
        Metric = None
        PeriodOverlaps = None
        Method = None
        AsOf = None
        IncludeSuperseded = false
    }

    /// Every current fact for one (subject, metric), visible now.
    let forSubjectMetric (subject: SubjectRef) (metric: MetricRef) : FactQuery = {
        all with
            Subject = Some subject
            Metric = Some metric
    }

    /// Reconstruct the fact base as of a transaction time (law L4).
    let asOf (t: DateTime) (q: FactQuery) : FactQuery = { q with AsOf = Some t }

/// A queried fact annotated with its derived competition indicator
/// (Phase 566 / GP 9 — selection is visible, never silent). Derived per
/// query from the current heads, never stored on any fact.
type FactWithCompetition = {
    Fact: Fact
    /// Method identities (`Fact.methodIdentity` strings) of the *other*
    /// current heads for this fact's (subject, metric, period) — the
    /// competing methods that also computed this quantity. Empty when the
    /// fact is uncontested; non-empty tells an answer surface to disclose
    /// that alternatives were computed (D19: competition is surfaced,
    /// never hidden — canonical selection picks a default, it does not
    /// erase the competitors).
    CompetingMethods: string list
}

/// The bitemporal, append-only, content-addressed fact base. Team-scoped
/// throughout (GP 4): `scopeId` is a resolved storage scope, and a fact
/// asserted in one scope is structurally unreachable from another.
type IFactStore =
    /// Assert a fact (law L1/L2/L3). Content-addressed: asserting an
    /// identical (subject, metric, period, method, inputHashes) returns
    /// the existing fact unchanged (idempotent). A fact with the same
    /// lineage key (subject, metric, period, method id) but changed inputs
    /// supersedes the current head — the `Supersedes` edge is derived and
    /// its `AsOf` strictly exceeds its predecessor's. A different method
    /// yields a *competing* fact (both current, never merged — D19).
    /// Returns the stored fact with `FactId` / `AsOf` / `Supersedes`
    /// populated. Every assert (and supersession) is audited (GP 6).
    abstract Assert: scopeId: string * draft: FactDraft -> Async<Result<Fact, string>>

    /// Assert many drafts as one operation (Phase 704) — the write-side
    /// twin of `QueryPopulation`. **Per-fact semantics are exactly
    /// `Assert`'s**: each draft gets its own content-addressed id, is
    /// idempotent against an identical stored tuple (L2), and derives its
    /// own supersession edge within its own lineage (L3) — including
    /// against an EARLIER DRAFT OF THE SAME BATCH, so a batch carrying two
    /// versions of one lineage settles exactly as two sequential
    /// `Assert`s would.
    ///
    /// What differs is cost and audit. Storage round trips are amortised
    /// across the batch, and the audit is **one summarised
    /// `FactBatchAsserted` row** carrying the receipt instead of a
    /// `FactAsserted` (plus `FactSuperseded`) pair per fact. GP 6 is
    /// unchanged in meaning: every assertion is still attributable —
    /// through the receipt's digest over the batch's full ordered
    /// disposition rather than through one row each.
    ///
    /// **A malformed batch commits nothing.** Every draft is checked for
    /// structural well-formedness (`FactDraft.defects`) BEFORE the first
    /// write, and one offender refuses the whole batch with an `Error`
    /// naming the offenders by position — the batch is the atom the
    /// producer retries, so it must decide before it starts. That
    /// pre-flight is **specific to this member**: `Assert` is unchanged
    /// and still accepts any draft it accepts today (GP 11).
    ///
    /// A *storage* failure part-way through is reported the same way —
    /// `Error`, naming what failed — but is honest about what it leaves
    /// behind: facts already written stay written. There is no rollback
    /// in an append-only store, and none is needed, because re-running
    /// the identical batch is exact by content address (D1): the
    /// already-written drafts come back as idempotent skips and the rest
    /// complete.
    ///
    /// An empty draft list is `Ok BatchAssertReceipt.empty` with no
    /// write and no audit row — "nothing to do" is an answer.
    abstract AssertBatch: scopeId: string * drafts: FactDraft list -> Async<Result<BatchAssertReceipt, string>>

    /// A fact by its content-addressed id, or `None`.
    abstract Get: scopeId: string * factId: string -> Async<Fact option>

    /// Query the fact base under `query` (see `FactQuery`). Honours L4
    /// `AsOf` visibility and the competing-fact / supersession rules.
    /// A method-less query against a metric with a registry-declared
    /// canonical method (Phase 566) returns the canonical lineage's head
    /// among the competitors; an explicit `Method` clause overrides, an
    /// `IncludeSuperseded` listing is untouched, and a metric with no
    /// declaration behaves exactly as before (GP 11).
    abstract Query: scopeId: string * query: FactQuery -> Async<Fact list>

    /// `Query` with a derived competition annotation per returned fact:
    /// the method identities of the *other* current heads for the same
    /// (subject, metric, period), so an answer surface can disclose that
    /// alternatives were computed (Phase 566 / GP 9 — selection is
    /// visible, never silent). Selection semantics are identical to
    /// `Query` (canonical default, explicit-`Method` override,
    /// `IncludeSuperseded` listing untouched).
    abstract QueryWithCompetition: scopeId: string * query: FactQuery -> Async<FactWithCompetition list>

    /// The supersession chain a fact belongs to — every fact in its
    /// lineage from the earliest to the latest, ordered by `AsOf`
    /// ascending. A single-fact lineage returns just that fact; an unknown
    /// id returns the empty list.
    abstract QuerySupersessionChain: scopeId: string * factId: string -> Async<Fact list>

    /// The **cross-subject** read (Phase 701): rank one metric across a
    /// subject population and summarise what was ranked over. Resolves
    /// over the current heads visible at `PopulationQuery.AsOf` (law L4
    /// — a superseded value never ranks), honours the D19 canonical /
    /// all-competing method selection, and clamps the ranking to
    /// `PopulationQuery.MaxTopK`: the answer is a ranking plus a summary,
    /// never the population.
    ///
    /// `Error` is a **typed refusal, not a failure** — the ordering could
    /// not be resolved (a `RegistryDirection` request against a metric
    /// that is unregistered or declared `Neutral`), so the store declines
    /// to invent a sort order (GP 9). An empty population is `Ok` with an
    /// empty ranking and the empty summary; "nothing matched" is an
    /// answer.
    abstract QueryPopulation: scopeId: string * query: PopulationQuery -> Async<Result<PopulationResult, string>>