// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Collections.Generic
open ToolUp.Platform.Grounding

// ─── The write of a fact, as one pure plan ──────────────────────────
//
// What asserting drafts MEANS — the content address, the idempotency test,
// the lineage head, the strictly-increasing `AsOf`, the supersession edge,
// the batch receipt and its digest, and the audit rows — is decided here,
// once, by functions with no storage behind them. A store is an EXECUTOR:
// it reads what the plan needs (which drafts are already stored, the
// current heads of the lineages they touch), asks `FactWrite.plan`, and
// persists the facts, the head moves and the audit rows the plan names.
//
// Before this file the blob store and the PostgreSQL companion each held a
// copy of that derivation, of the malformed-batch refusal, of the audit
// rows and of the competition indicator, kept "identical" by a differential
// test run. They are now one definition, so a fact asserted
// through any store supersedes, competes and audits identically by
// construction; the differential pack still compares the two stores'
// READS, which no write plan governs.
//
// The types a write is described in — the draft, the outcome, the receipt
// and the audit payloads — moved here from `IFactStore.fs` with the plan,
// unchanged and in the same namespace.

// ─── Fact-store audit events ───────────────────────────────────────
//
// The fact store audits to `IEventStore` under a **reserved source
// module** (`_facts`) rather than adding cases to the core `AuditEvent`
// DU — the `ILineageStore` pattern (which writes `_platform.lineage`
// events). This keeps the companion self-contained (GP 1: no core edit)
// while producing a durable, scope-isolated, queryable audit record
// (`IEventStore.ReadBySource scope "_facts"`) that the later provenance
// chain walks. PII-free: identifiers + classification only.

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

    /// A **batch** assertion completed — one summarised row
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

// ─── Batch assertion ───────────────────────────────────────────────
//
// The write-side twin of the cross-subject population read. A
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

    /// The digest over a batch's full ordered disposition. Order is
    /// submission order and it is significant: two batches over the same
    /// drafts in different orders can legitimately produce different
    /// supersession outcomes, so a digest blind to order would call them
    /// the same batch.
    ///
    /// Framed by `CanonicalFrame` — the label, then each
    /// disposition's outcome and content address as two fields.
    let digest (dispositions: (BatchAssertOutcome * string) list) : string =
        ToolUp.Platform.CanonicalFrame.digest [
            "batch-assert-receipt"
            for outcome, factId in dispositions do
                BatchAssertOutcome.toString outcome
                factId
        ]

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

/// Structural well-formedness of a draft.
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

// ─── The plan ──────────────────────────────────────────────────────

/// One audit row a write produces (GP 6), as the value the store writes
/// under `FactEvents.SourceModule`. A scalar `Assert` produces an
/// `AssertedRow` per new fact, followed by a `SupersededRow` when it
/// superseded a head; a batch produces ONE `BatchAssertedRow` carrying its
/// receipt. An idempotent scalar assert produces none: nothing changed
/// state.
[<RequireQualifiedAccess>]
type FactAuditEvent =
    | AssertedRow of FactAssertedEvent
    | SupersededRow of FactSupersededEvent
    | BatchAssertedRow of FactBatchAssertedEvent

/// The event type and transaction time an audit row is written under.
[<RequireQualifiedAccess>]
module FactAuditEvent =

    /// The `ModuleEvent.EventType` the row is written under.
    let eventType (event: FactAuditEvent) : string =
        match event with
        | FactAuditEvent.AssertedRow _ -> FactEvents.AssertedType
        | FactAuditEvent.SupersededRow _ -> FactEvents.SupersededType
        | FactAuditEvent.BatchAssertedRow _ -> FactEvents.BatchAssertedType

    /// The transaction time the row records, which is when it occurred.
    let occurredAt (event: FactAuditEvent) : DateTime =
        match event with
        | FactAuditEvent.AssertedRow e -> e.AsOf
        | FactAuditEvent.SupersededRow e -> e.AsOf
        | FactAuditEvent.BatchAssertedRow e -> e.AsOf

/// How one lineage's current head moves in a write: the stored head the
/// write retires, if the lineage had one, and the head it leaves. An index
/// over current heads applies these and nothing else; a lineage the write
/// did not move has no delta.
type IndexDelta = {
    /// The lineage key (`Fact.lineageKey`).
    Lineage: string
    /// The fact id of the head the lineage had before the write, if any.
    Retires: string option
    /// The fact id of the lineage's head after the write.
    Head: string
}

/// Which `IFactStore` member a write serves. The two differ in exactly two
/// ways, both decided by the plan: a batch is pre-flighted for malformed
/// drafts and refused whole (a scalar assert accepts any draft it accepted
/// before batches existed, GP 11), and a batch is audited by one summarised row
/// where a scalar assert is audited per fact.
[<RequireQualifiedAccess>]
type WriteShape =
    | ScalarAssert
    | BatchAssert

/// Why a write was refused before anything was read or written.
type FactStoreError =
    /// A batch draft that is not well-formed (`FactDraft.defects`): its
    /// position in the batch, the draft, and the defects.
    | MalformedDraft of position: int * draft: FactDraft * defects: string list

/// One draft with its content address and lineage key — what a store needs
/// to decide which facts and heads to read before the plan is made.
type AddressedDraft = {
    Draft: FactDraft
    /// The content-addressed id the draft is stored under.
    FactId: string
    /// The lineage it belongs to (`Fact.lineageKey`).
    Lineage: string
}

/// A write that passed its pre-flight: the shape, and every draft in
/// submission order with its address.
type WriteRequest = {
    Shape: WriteShape
    Drafts: AddressedDraft list
}

/// Everything a store persists for one write, decided before it persists
/// anything. A store writes `Facts`, applies `IndexDeltas`, writes `Audit`
/// and returns `Receipt` (a batch) or the one fact (a scalar assert).
type WritePlan = {
    /// The facts to store, in submission order: each with its id, `AsOf`
    /// and `Supersedes` derived. Never two with one id.
    Facts: Fact list
    /// Every supersession edge this write adds, as (superseding fact id,
    /// superseded fact id), in the order the facts were derived.
    Supersessions: (string * string) list
    /// The current-head moves, one per lineage a new fact landed in, in the
    /// order the lineages were first touched.
    IndexDeltas: IndexDelta list
    /// The audit rows, in write order.
    Audit: FactAuditEvent list
    /// The content addresses of the drafts already stored (by an earlier
    /// write or an earlier draft of this one), in submission order. A draft
    /// here is never written again.
    Unchanged: string list
    /// The receipt over the full ordered disposition.
    Receipt: BatchAssertReceipt
}

/// The competition indicator and canonical selection over current heads
/// (D19): one definition, read by every store.
[<RequireQualifiedAccess>]
module FactCompetition =

    /// Two current heads compete when they share (subject, metric, period)
    /// under different methods. The period `Label` is cosmetic and excluded,
    /// mirroring the lineage key's canonical period.
    let key (f: Fact) =
        f.Subject, f.Metric, f.Period.From, f.Period.To

    /// Resolve a method-less query's competing heads to each metric's
    /// registry-declared canonical method (`PopulationSelection.canonicalHeads`):
    /// no registry or no declaration keeps every head, and a declaration no
    /// head matches keeps every head too (GP 9).
    let selectCanonical (registry: IMetricRegistry option) (heads: Fact list) : Fact list =
        match registry with
        | None -> heads
        | Some reg ->
            PopulationSelection.canonicalHeads
                key
                (fun f -> Fact.methodIdentity f.Method)
                (fun f -> reg.TryGetMetric f.Metric.Value |> Option.bind _.CanonicalMethod)
                heads

    /// The method identities of the OTHER current heads sharing `f`'s
    /// (subject, metric, period). A superseded fact in a history listing
    /// never lists its own lineage's head (same method identity).
    let competingMethods (heads: Fact list) (f: Fact) : string list =
        let own = key f
        let ownMethod = Fact.methodIdentity f.Method

        heads
        |> List.filter (fun g -> key g = own)
        |> List.map (fun g -> Fact.methodIdentity g.Method)
        |> List.filter (fun identity -> identity <> ownMethod)
        |> List.distinct

/// The write core: pre-flight, then the plan.
[<RequireQualifiedAccess>]
module FactWrite =

    /// A draft's content address and lineage key. The address honours plan
    /// D18 (`Fact.effectiveInputHashes`).
    let address (draft: FactDraft) : AddressedDraft = {
        Draft = draft
        FactId =
            Fact.compute
                draft.Subject
                draft.Metric
                draft.Period
                draft.Method
                (Fact.effectiveInputHashes draft.Method draft.Evidence draft.Value)
        Lineage = Fact.lineageKey draft.Subject draft.Metric draft.Period draft.Method
    }

    /// The fact a draft is, stored at `asOf` behind `supersedes`.
    let factOf (draft: AddressedDraft) (asOf: DateTime) (supersedes: string option) : Fact = {
        FactId = draft.FactId
        Subject = draft.Draft.Subject
        Metric = draft.Draft.Metric
        Value = draft.Draft.Value
        Period = draft.Draft.Period
        AsOf = asOf
        Method = draft.Draft.Method
        Evidence = draft.Draft.Evidence
        Confidence = draft.Draft.Confidence
        Supersedes = supersedes
        Disclosure = draft.Draft.Disclosure
    }

    /// A draft as the fact it would be, recorded at `asOf` with no
    /// predecessor — how a store-independent record of a draft (a delegate's
    /// historical quote) is made, under the same address as a write.
    let recordedAt (draft: FactDraft) (asOf: DateTime) : Fact = factOf (address draft) asOf None

    /// The head rule over facts in read order: the latest `AsOf` wins, and
    /// the FIRST fact seen among equal `AsOf` values.
    let headOf (facts: Fact list) : Fact option =
        facts
        |> List.fold
            (fun (acc: Fact option) f ->
                match acc with
                | Some existing when existing.AsOf >= f.AsOf -> acc
                | _ -> Some f)
            None

    /// The caller-facing refusal for a batch whose drafts are not all
    /// well-formed. Names offenders by POSITION — a malformed draft's
    /// content address is meaningless, and position is what the producer
    /// indexes its own input by — plus the subject and metric it claimed,
    /// at most ten of them.
    let renderOffenders (total: int) (errors: FactStoreError list) : string =
        let named =
            errors
            |> List.truncate 10
            |> List.map (fun (MalformedDraft(position, draft, defects)) ->
                sprintf
                    "#%d %s / %s: %s"
                    position
                    (SubjectRef.toString draft.Subject)
                    draft.Metric.Value
                    (String.concat ", " defects))
            |> String.concat "; "

        let more =
            let hidden = List.length errors - 10
            if hidden > 0 then sprintf " (and %d more)" hidden else ""

        sprintf
            "fact store batch rejected: %d of %d drafts are malformed and none were committed — %s%s"
            (List.length errors)
            total
            named
            more

    /// The pre-flight, before any read: a batch with a malformed draft is
    /// refused whole, naming every offender; otherwise each draft is
    /// addressed. A scalar write is never refused here.
    let prepare (shape: WriteShape) (drafts: FactDraft list) : Result<WriteRequest, FactStoreError list> =
        let offenders =
            match shape with
            | WriteShape.ScalarAssert -> []
            | WriteShape.BatchAssert ->
                drafts
                |> List.mapi (fun position draft -> position, draft, FactDraft.defects draft)
                |> List.filter (fun (_, _, defects) -> not (List.isEmpty defects))
                |> List.map MalformedDraft

        match offenders with
        | [] ->
            Ok {
                Shape = shape
                Drafts = drafts |> List.map address
            }
        | _ -> Error offenders

    /// The plan. `isStored` says whether a content address is already
    /// stored; `stored` carries stored facts of the lineages the request
    /// writes into, in read order (a fact of any other lineage is never a
    /// head here, and a lineage with no fact in it has no head). `now` is the
    /// store's clock, read once for the write.
    ///
    /// Per draft, in submission order: an address already stored, or
    /// produced by an earlier draft of this request, is `Unchanged` (law L2);
    /// otherwise the fact is stamped `AsOf = now`, or one tick after its
    /// lineage's head when the head is not earlier than `now` (law L3 —
    /// strictly increasing even under a coarse or frozen clock), supersedes
    /// that head, and becomes the head for the drafts after it. So a request
    /// carrying two versions of one lineage settles exactly as two
    /// sequential scalar asserts would.
    let plan (now: DateTime) (isStored: string -> bool) (stored: Fact list) (request: WriteRequest) : WritePlan =
        let heads = Dictionary<string, Fact>(StringComparer.Ordinal)

        for f in stored do
            let key = Fact.lineageKey f.Subject f.Metric f.Period f.Method

            match heads.TryGetValue key with
            | true, existing when existing.AsOf >= f.AsOf -> ()
            | _ -> heads[key] <- f

        let original = Dictionary<string, Fact>(heads, StringComparer.Ordinal)
        let seen = HashSet<string>(StringComparer.Ordinal)
        let facts = ResizeArray<Fact>()
        let dispositions = ResizeArray<BatchAssertOutcome * string>()
        let touched = ResizeArray<string>()
        let touchedSet = HashSet<string>(StringComparer.Ordinal)

        for draft in request.Drafts do
            if seen.Contains draft.FactId || isStored draft.FactId then
                seen.Add draft.FactId |> ignore
                dispositions.Add(BatchIdempotent, draft.FactId)
            else
                let head =
                    match heads.TryGetValue draft.Lineage with
                    | true, head -> Some head
                    | _ -> None

                let asOf =
                    match head with
                    | Some head when head.AsOf >= now -> head.AsOf.AddTicks 1L
                    | _ -> now

                let fact = factOf draft asOf (head |> Option.map _.FactId)
                seen.Add draft.FactId |> ignore
                heads[draft.Lineage] <- fact
                facts.Add fact

                if touchedSet.Add draft.Lineage then
                    touched.Add draft.Lineage

                dispositions.Add((if head.IsSome then BatchSuperseding else BatchAsserted), draft.FactId)

        let facts = List.ofSeq facts
        let receipt = BatchAssertReceipt.ofDispositions (List.ofSeq dispositions)

        let audit =
            match request.Shape with
            | WriteShape.ScalarAssert -> [
                for f in facts do
                    FactAuditEvent.AssertedRow {
                        FactId = f.FactId
                        Subject = SubjectRef.toString f.Subject
                        Metric = f.Metric.Value
                        Method = Fact.methodIdentity f.Method
                        Disclosure = Disclosure.toString f.Disclosure
                        AsOf = f.AsOf
                    }

                    match f.Supersedes with
                    | Some superseded ->
                        FactAuditEvent.SupersededRow {
                            NewFactId = f.FactId
                            SupersededFactId = superseded
                            Subject = SubjectRef.toString f.Subject
                            Metric = f.Metric.Value
                            AsOf = f.AsOf
                        }
                    | None -> ()
              ]
            // An empty batch is "nothing to do": no row.
            | WriteShape.BatchAssert when List.isEmpty request.Drafts -> []
            // Otherwise ONE summarised row, even for an all-idempotent batch,
            // timed by the clock: "the population re-ran and nothing moved"
            // is a claim a producer needs to be able to prove.
            | WriteShape.BatchAssert -> [
                FactAuditEvent.BatchAssertedRow {
                    Receipt = receipt
                    AsOf =
                        match facts with
                        | [] -> now
                        | _ -> facts |> List.map _.AsOf |> List.max
                }
              ]

        {
            Facts = facts
            Supersessions =
                facts
                |> List.choose (fun f -> f.Supersedes |> Option.map (fun s -> f.FactId, s))
            IndexDeltas =
                touched
                |> Seq.map (fun lineage -> {
                    Lineage = lineage
                    Retires =
                        match original.TryGetValue lineage with
                        | true, head -> Some head.FactId
                        | _ -> None
                    Head = heads[lineage].FactId
                })
                |> List.ofSeq
            Audit = audit
            Unchanged =
                dispositions
                |> Seq.choose (fun (outcome, factId) ->
                    match outcome with
                    | BatchIdempotent -> Some factId
                    | BatchAsserted
                    | BatchSuperseding -> None)
                |> List.ofSeq
            Receipt = receipt
        }