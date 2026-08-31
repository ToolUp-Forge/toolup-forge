// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 689 — the budget ledger seam ──────────────────────────────────
//
// `BudgetPolicy` (Core) decides. This seam is where the consumption it
// decides against lives, and — the part that is not obvious — how a check
// and its reservation become **one** operation.
//
// **Why `Reserve` is a ledger method and not `ReadUsage` + a caller-side
// decision.** The interesting concurrency here is precisely the one a
// budget exists to bound: N requests arriving at once. Read-then-decide-
// then-write admits all N, because every one of them reads the same
// pre-burst row and every one concludes there is room. That is not a rare
// race — it is the *expected* behaviour of an agent fanning out, which is
// the workload budgets are written for. So the read, the decision and the
// increment happen inside one critical section owned by the
// implementation, and the pure policy is handed IN rather than applied
// outside. A ledger that cannot make that atomic cannot enforce a
// concurrency ceiling at all; it can only report having missed one.
//
// **The ledger holds consumption, never the budget itself.** Where a
// domain's ceilings are configured is the domain's own question — Phase
// 451 keeps them in a blob beside the usage row, a token budget will read
// them from per-team config — and a seam that insisted on owning both
// would force every domain to move its policy storage to satisfy an
// interface. What every domain shares is the counter, so that is what this
// seam is.
//
// **Six portability rules (GP 12) — audited.**
// 1. *Identity by value* — `BudgetLedgerKey` is three strings, and the
//    rows are records of primitives. No live handle, transaction object or
//    lock crosses the surface.
// 2. *Async at every boundary* — every member returns `Async<_>`.
// 3. *Retry + supervision as data* — a refusal is a `BudgetDenial` in the
//    error channel, never an exception; nothing here takes a failure
//    callback. The accounting effects are `BudgetAccount`, a record.
// 4. *Stateless between invocations* — every call carries the whole key.
//    An implementation caches nothing across calls that would make a
//    recycled instance answer differently.
// 5. *No cross-shard ordering* — two keys are independent. Ordering is
//    promised only within one `BudgetLedgerKey`.
// 6. *Precision at the lower bound* — counts are `int`, quantities are
//    `decimal`, and the period boundary is UTC-hour / -day / -month
//    granularity, declared as such and never sub-second.

/// Phase 689 — the row one budget decision is accounted to: a domain, a
/// scope, and a period.
///
/// The period key is IN the identity rather than a field on a mutable
/// counter, which is what makes a period reset free and correct: the next
/// period is a different key that does not exist yet, and a key that does
/// not exist reads as zero consumption. There is nothing to reset, so
/// there is no reset job that can fail to run.
type BudgetLedgerKey = {
    /// `BudgetSubject.Domain` — namespaces the row, so two budget
    /// families in one deployment can never read each other's counters.
    Domain: string
    /// The scope the row belongs to. The GP 4 partition.
    ScopeId: string
    /// `BudgetPeriod.key` of the accounting window.
    PeriodKey: string
}

[<RequireQualifiedAccess>]
module BudgetLedgerKey =
    /// The key for `subject` in the period `periodKey`.
    ///
    /// Note the **class label is deliberately absent**: consumption
    /// accrues to the scope, and per-class ceilings are a policy question
    /// answered before the ledger is reached. A per-class counter would
    /// let a scope spend its allowance once per class, which is not what
    /// an operator who wrote one allowance meant.
    let ofSubject (subject: BudgetSubject) (periodKey: string) : BudgetLedgerKey = {
        Domain = subject.Domain
        ScopeId = subject.ScopeId
        PeriodKey = periodKey
    }

    /// A key built from its parts.
    let create (domain: string) (scopeId: string) (periodKey: string) : BudgetLedgerKey = {
        Domain = domain
        ScopeId = scopeId
        PeriodKey = periodKey
    }

    /// A stable single-string form, for an in-process dictionary or a
    /// warning latch. Not a storage path — see `BudgetLedgerLayout`.
    let cacheKey (key: BudgetLedgerKey) : string =
        key.Domain + "|" + key.ScopeId + "|" + key.PeriodKey

    /// The zero row for this key.
    let emptyUsage (key: BudgetLedgerKey) : BudgetUsage =
        BudgetUsage.empty key.Domain key.ScopeId key.PeriodKey

/// Phase 689 — where a budget's live consumption lives, and how a check
/// and its reservation are made indivisible.
///
/// Implemented once per storage substrate, not once per budget domain: the
/// domain rides in the key, so one composed ledger serves every budget a
/// deployment runs.
type IBudgetLedger =
    /// The live row for one key. Read-only; returns the zero row for a
    /// period nothing has been charged to yet, and for a read that failed
    /// — see `Reserve` on the failure direction a budget may have.
    abstract ReadUsage: key: BudgetLedgerKey -> Async<BudgetUsage>

    /// **Atomically** evaluate `decide` against the live row and, if it
    /// admits, apply the reservation (`InFlight + 1`, `Spent + cost`) in
    /// the same critical section.
    ///
    /// `decide` is the pure policy — normally a partial application of
    /// `BudgetPolicy.check` over the claims the domain built from the row.
    /// Handing the decision IN rather than exposing the row and trusting
    /// the caller is what closes the read-then-write race the file header
    /// describes; an implementation MUST NOT satisfy this by calling
    /// `ReadUsage` and then a separate write.
    ///
    /// `Ok` carries the row **as it stands after** the reservation, so a
    /// caller can emit a threshold warning without a second read.
    ///
    /// A storage failure admits rather than refuses. The failure direction
    /// a budget may have is admitting work it should have refused; the
    /// direction it may NOT have is turning a transient storage blip into
    /// a deployment-wide refusal, because a budget that fails closed is a
    /// budget an operator switches off after the first incident — which
    /// leaves them with no budget at all.
    abstract Reserve:
        key: BudgetLedgerKey * cost: decimal * decide: (BudgetUsage -> Result<unit, BudgetDenial>) ->
            Async<Result<BudgetUsage, BudgetDenial>>

    /// Release one reservation: `InFlight - 1`, and `Spent +
    /// costAdjustment`.
    ///
    /// `costAdjustment` is the **difference** between what the work
    /// actually cost and what admission reserved, so it is frequently
    /// negative and zero for a flat per-request cost model. A delta rather
    /// than an absolute because the ledger does not retain per-request
    /// reservations — it holds one aggregate per period, which is what
    /// makes the row a single cheap read on the hot path.
    ///
    /// Idempotency is NOT promised: releasing twice releases two slots.
    /// The caller settles once per reservation it holds, and adding a
    /// per-request ledger to make this idempotent would trade the
    /// aggregate row (one read, one write) for per-request state whose
    /// lifetime nothing bounds.
    abstract Release: key: BudgetLedgerKey * costAdjustment: decimal -> Async<unit>

/// Phase 689 — where budget consumption lives in blob storage. Shared so
/// an admin surface or a sweep enumerates exactly what a ledger writes.
///
/// Generalises Phase 451's `ComputeBudgetLayout` by making the domain the
/// leading path segment: with `domain = "compute-budget"` the paths are
/// byte-identical to the ones that phase has been writing since it
/// shipped, which is what lets its store adopt this ledger without a data
/// migration.
[<RequireQualifiedAccess>]
module BudgetLedgerLayout =
    /// Reserved SDK-level container, alongside every other platform store.
    [<Literal>]
    let DefaultContainer = "_platform"

    /// Blob-name prefix for one budget domain — the whole of what that
    /// domain writes.
    let domainPrefix (domain: string) : string = domain + "/"

    /// Every blob for one scope within one domain. The scope segment is
    /// what makes the layout structurally isolating (GP 4) — a lookup for
    /// one scope cannot construct a path under another, and the only
    /// prefix a caller can enumerate is bounded by the scope it resolved.
    let scopePrefix (domain: string) (scopeId: string) : string = domainPrefix domain + scopeId + "/"

    /// The usage row for one key. The period key is IN the blob name,
    /// which is what makes a period reset free: the next period is a
    /// different blob that does not exist yet, and a blob that does not
    /// exist reads as zero consumption.
    let usageBlob (key: BudgetLedgerKey) : string =
        scopePrefix key.Domain key.ScopeId + "usage/" + key.PeriodKey + ".json"