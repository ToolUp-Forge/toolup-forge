// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── IFactStore (Phase 520) ──────────────────────────────────────────
//
// The server-side, team-scoped surface over the bitemporal fact base.
// Append-only: `Assert` never mutates — a changed input supersedes the
// current lineage head with a *derived* edge; an identical tuple is
// idempotent (content-addressed). Reads honour law L4 visibility
// (`AsOf`), and supersession chains are walkable.
//
// **Six portability rules (GP 12).**
//  1. Identity by value — `ResolvedScope` values, `string` fact ids,
//     domain records; no live handles.
//  2. Async at every boundary — every method returns `Async<_>`.
//  3. Retry / failure as data — every member returns
//     `Result<_, FactStoreError>`; no `OnFailure` callbacks.
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

/// A subject branch — every subject instance in one hierarchy, optionally
/// under a path prefix and at one depth (Phase 1123). The shape
/// `PopulationQuery` names its subject set in (`Hierarchy`, `PathPrefix`,
/// `Level`), and decided by the same predicate, so a fact read and a
/// population read never disagree on what "under this branch" means.
type SubjectBranch = {
    /// The subject hierarchy id (`SubjectRef.Hierarchy`).
    Hierarchy: string
    /// Admit only subjects whose `Path` starts with these segments. `None`
    /// (or an empty prefix) admits the whole hierarchy.
    PathPrefix: string list option
    /// Admit only subjects with this many `Path` segments (`0` is the
    /// hierarchy root). `None` admits every depth.
    Level: int option
}

/// Construction and the membership predicate of a subject branch.
module SubjectBranch =

    /// The whole of one hierarchy, every depth.
    let ofHierarchy (hierarchy: string) : SubjectBranch = {
        Hierarchy = hierarchy
        PathPrefix = None
        Level = None
    }

    /// Whether `subject` lies in `branch` — `PopulationQuery.matchesSubject`
    /// over the branch's three clauses, so the two reads share one decision.
    /// Partially apply it once per read: the predicate is built once.
    let matches (branch: SubjectBranch) : SubjectRef -> bool =
        let population = {
            PopulationQuery.create (MetricRef String.Empty) branch.Hierarchy with
                Level = branch.Level
                PathPrefix = branch.PathPrefix
        }

        PopulationQuery.matchesSubject population

/// Read filter for `IFactStore.Query`. Every clause is optional and
/// AND-combined; an all-`None` query returns every current fact in scope.
type FactQuery = {
    /// Restrict to one subject instance.
    Subject: SubjectRef option
    /// Restrict to the subject instances in one branch (Phase 1123) — the
    /// read of one brand, region or store across its members. AND-combined
    /// with `Subject` like every other clause and never exclusive with it:
    /// the two can always both be named, and a query naming a subject
    /// outside the branch matches nothing, as any two disjoint clauses do.
    /// `None` admits every subject.
    SubjectBranch: SubjectBranch option
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
        SubjectBranch = None
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

    /// Whether the query's subject clauses — `Subject` and `SubjectBranch`
    /// — both admit `subject`. Every store filters by this, so the two
    /// clauses mean one thing everywhere.
    let admitsSubject (q: FactQuery) : SubjectRef -> bool =
        let inBranch =
            match q.SubjectBranch with
            | Some branch -> SubjectBranch.matches branch
            | None -> fun _ -> true

        fun subject -> (q.Subject |> Option.forall (fun s -> s = subject)) && inBranch subject

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
/// throughout (GP 4): a fact asserted in one scope is structurally
/// unreachable from another.
///
/// **One scope form.** Every member takes a `ResolvedScope` — the value the
/// platform's scope resolution minted, which is the only way to obtain one.
/// A request-path door holds the scope its request resolved to; a
/// platform-carried caller (a job, an import, a recompute, a sweep) carries
/// a `ScopeKey` across its persistence boundary and is handed the scope the
/// resolver re-minted at execution. No member takes a scope as a string, so
/// no caller can address a shard nobody resolved. The anonymous scope is a
/// shard like any other — never a wildcard.
///
/// **One error.** Every member answers `Result<_, FactStoreError>`. A storage
/// fault is `Error(StorageFault …)` on a read as on a write — never a shorter
/// answer, never an absent fact. An empty answer is `Ok []` / `Ok None`.
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
    abstract Assert: scope: ResolvedScope * draft: FactDraft -> Async<Result<Fact, FactStoreError>>

    /// Assert many drafts as one operation — the write-side twin of
    /// `QueryPopulation`. **Per-fact semantics are exactly `Assert`'s**: each
    /// draft gets its own content-addressed id, is idempotent against an
    /// identical stored tuple (L2), and derives its own supersession edge
    /// within its own lineage (L3) — including against an EARLIER DRAFT OF
    /// THE SAME BATCH, so a batch carrying two versions of one lineage
    /// settles exactly as two sequential `Assert`s would.
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
    /// write, and one offender refuses the whole batch with
    /// `MalformedDrafts`, naming the offenders by position — the batch is
    /// the atom the producer retries, so it must decide before it starts.
    /// That pre-flight is **specific to this member**: `Assert` accepts any
    /// draft it accepted before batches existed (GP 11).
    ///
    /// A *storage* failure part-way through is a `StorageFault`, honest
    /// about what it leaves behind: facts already written stay written.
    /// There is no rollback in an append-only store, and none is needed,
    /// because re-running the identical batch is exact by content address
    /// (D1): the already-written drafts come back as idempotent skips and
    /// the rest complete.
    ///
    /// An empty draft list is `Ok BatchAssertReceipt.empty` with no
    /// write and no audit row — "nothing to do" is an answer.
    abstract AssertBatch:
        scope: ResolvedScope * drafts: FactDraft list -> Async<Result<BatchAssertReceipt, FactStoreError>>

    /// A fact by its content-addressed id: `Ok None` when the scope holds
    /// no such fact, `Error` when the store could not read it.
    abstract Get: scope: ResolvedScope * factId: string -> Async<Result<Fact option, FactStoreError>>

    /// Query the fact base under `query` (see `FactQuery`). Honours L4
    /// `AsOf` visibility and the competing-fact / supersession rules.
    /// A method-less query against a metric with a registry-declared
    /// canonical method returns the canonical lineage's head among the
    /// competitors; an explicit `Method` clause overrides, an
    /// `IncludeSuperseded` listing is untouched, and a metric with no
    /// declaration behaves exactly as before (GP 11).
    abstract Query: scope: ResolvedScope * query: FactQuery -> Async<Result<Fact list, FactStoreError>>

    /// `Query` with a derived competition annotation per returned fact:
    /// the method identities of the *other* current heads for the same
    /// (subject, metric, period), so an answer surface can disclose that
    /// alternatives were computed (GP 9 — selection is visible, never
    /// silent). Selection semantics are identical to `Query`.
    abstract QueryWithCompetition:
        scope: ResolvedScope * query: FactQuery -> Async<Result<FactWithCompetition list, FactStoreError>>

    /// The supersession chain a fact belongs to — every fact in its
    /// lineage from the earliest to the latest, ordered by `AsOf`
    /// ascending. A single-fact lineage returns just that fact; an unknown
    /// id returns the empty list.
    abstract QuerySupersessionChain: scope: ResolvedScope * factId: string -> Async<Result<Fact list, FactStoreError>>

    /// The **cross-subject** read: rank one metric across a subject
    /// population and summarise what was ranked over. Resolves over the
    /// current heads visible at `PopulationQuery.AsOf` (law L4 — a
    /// superseded value never ranks), honours the D19 canonical /
    /// all-competing method selection, and clamps the ranking to
    /// `PopulationQuery.MaxTopK`: the answer is a ranking plus a summary,
    /// never the population.
    ///
    /// `Error(Refused …)` is a **typed refusal, not a failure** — the
    /// ordering could not be resolved (a `RegistryDirection` request against
    /// a metric that is unregistered or declared `Neutral`), so the store
    /// declines to invent a sort order (GP 9). An empty population is `Ok`
    /// with an empty ranking and the empty summary; "nothing matched" is an
    /// answer.
    abstract QueryPopulation:
        scope: ResolvedScope * query: PopulationQuery -> Async<Result<PopulationResult, FactStoreError>>

/// A fact-store `Error` raised by a caller whose own signature has no
/// failure channel to carry it in. Raised, never swallowed: a fault the
/// caller cannot report is still not an absence.
exception FactStoreFault of error: FactStoreError with
    override this.Message = FactStoreError.describe this.error

/// Reading an `IFactStore` answer where the enclosing surface has no
/// `Result` to return it through.
[<RequireQualifiedAccess>]
module FactStoreResult =

    /// The answer, or `FactStoreFault` raised over the store's `Error`.
    let orRaise (result: Result<'T, FactStoreError>) : 'T =
        match result with
        | Ok value -> value
        | Error error -> raise (FactStoreFault error)

    /// `orRaise` over an asynchronous answer.
    let orRaiseAsync (answer: Async<Result<'T, FactStoreError>>) : Async<'T> = async {
        let! result = answer
        return orRaise result
    }

    /// A store executor's read, answered as the interface types it: the
    /// answer, or the fault the read met as `StorageFault` (a fault already
    /// raised as `FactStoreFault` keeps its own error). A fact the store
    /// cannot read is never dropped from an answer.
    let ofRead (operation: string) (read: Async<'T>) : Async<Result<'T, FactStoreError>> = async {
        try
            let! answer = read
            return Ok answer
        with
        | FactStoreFault fault -> return Error fault
        | ex -> return Error(FactStoreError.StorageFault(operation, ex.Message))
    }

/// Writing a plan's audit rows — the one serialisation every
/// store executor writes `WritePlan.Audit` through, so a row's type, time
/// and payload are decided by the plan and rendered once.
[<RequireQualifiedAccess>]
module FactAudit =

    let private jsonOptions = FableConverters.create ()

    /// The row's JSON payload — the event record the plan built, as is.
    let payload (event: FactAuditEvent) : string =
        match event with
        | FactAuditEvent.AssertedRow e -> JsonSerializer.Serialize(e, jsonOptions)
        | FactAuditEvent.SupersededRow e -> JsonSerializer.Serialize(e, jsonOptions)
        | FactAuditEvent.BatchAssertedRow e -> JsonSerializer.Serialize(e, jsonOptions)

    /// Write a plan's audit rows to the scope's event log, in order, under
    /// the reserved `_facts` source module.
    let write (events: IEventStore) (scopeId: string) (audit: FactAuditEvent list) : Async<unit> = async {
        for event in audit do
            do!
                events.Write {
                    Id = Guid.NewGuid()
                    OccurredAt = FactAuditEvent.occurredAt event
                    ScopeId = scopeId
                    SourceModule = FactEvents.SourceModule
                    EventType = FactAuditEvent.eventType event
                    Payload = payload event
                }
    }

// ─── Phase 1089 — moving persisted fact ids onto the canonical frame ──
//
// `Fact.compute` changed recipe once, at Phase 1089: the fields it digests
// are unchanged, the frame around them is now `CanonicalFrame`. Ids are
// content addresses, so a store written before the change holds facts
// whose stored id is no longer what `Fact.computeFor` says it is. They are
// MIGRATED, not dual-read (H §9 D1): a second id scheme kept alive is a
// second recipe. The plan below is the one decision both stores' migrations
// make — which facts change, and to what — so the blob store and the
// Postgres companion cannot re-key the same scope differently.

/// One re-keyed fact: its id before and after the migration.
type FactIdChange = { From: string; To: string }

/// What one id migration over one scope did — or, on a dry run, would do.
type FactIdMigrationReport = {
    /// The scope migrated.
    ScopeId: string
    /// Nothing was written.
    DryRun: bool
    /// Facts the scope held when the migration read it.
    FactsRead: int
    /// Facts whose stored id was not their recomputed id, and moved.
    Rekeyed: int
    /// Facts whose id was already current but whose `Supersedes` named a
    /// fact that moved, so the link was rewritten.
    Relinked: int
    /// The first fact (in the scope's read order) whose id moves.
    FirstDivergent: FactIdChange option
}

/// The id migration's pure half, shared by every store that persists facts.
[<RequireQualifiedAccess>]
module FactIdMigration =

    /// Each stored fact the migration changes, paired with its migrated
    /// form: `FactId` recomputed by `Fact.computeFor`, and `Supersedes`
    /// mapped through the same recomputation when it names a fact in
    /// `stored`. A fact already current, linked to facts already current,
    /// is absent — so a second run over a migrated scope plans nothing.
    /// Every other field — value, transaction time, disclosure — is the
    /// stored fact's, unchanged.
    let plan (stored: Fact list) : (Fact * Fact) list =
        let recomputed =
            Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)

        for f in stored do
            recomputed[f.FactId] <- Fact.computeFor f

        let mapped (id: string) =
            match recomputed.TryGetValue id with
            | true, current -> current
            | _ -> id

        stored
        |> List.choose (fun f ->
            let migrated = {
                f with
                    FactId = recomputed[f.FactId]
                    Supersedes = f.Supersedes |> Option.map mapped
            }

            if migrated.FactId = f.FactId && migrated.Supersedes = f.Supersedes then
                None
            else
                Some(f, migrated))

    /// The report for a plan over `factsRead` facts.
    let report (scopeId: string) (dryRun: bool) (factsRead: int) (changes: (Fact * Fact) list) : FactIdMigrationReport = {
        ScopeId = scopeId
        DryRun = dryRun
        FactsRead = factsRead
        Rekeyed =
            changes
            |> List.filter (fun (before, after) -> before.FactId <> after.FactId)
            |> List.length
        Relinked =
            changes
            |> List.filter (fun (before, after) -> before.FactId = after.FactId)
            |> List.length
        FirstDivergent =
            changes
            |> List.tryFind (fun (before, after) -> before.FactId <> after.FactId)
            |> Option.map (fun (before, after) -> {
                From = before.FactId
                To = after.FactId
            })
    }

    /// One line per scope, for an operator: the counts and the first id
    /// that moves.
    let render (r: FactIdMigrationReport) : string =
        let verb = if r.DryRun then "would re-key" else "re-keyed"

        let first =
            match r.FirstDivergent with
            | Some c -> sprintf "; first %s -> %s" c.From c.To
            | None -> ""

        sprintf "%s: %d facts read, %s %d, relinked %d%s" r.ScopeId r.FactsRead verb r.Rekeyed r.Relinked first