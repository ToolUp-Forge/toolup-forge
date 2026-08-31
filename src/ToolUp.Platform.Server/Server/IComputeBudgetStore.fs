// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 451 — the compute-budget store seam ───────────────────────────
//
// `ComputeBudgetPolicy.admit` (Core) is a pure function of a budget, a
// usage row and a submission. This seam is the other half: where the budget
// is configured, where the usage row lives, and — the part that is not
// obvious — how a check and its reservation become **one** operation.
//
// **Why `Admit` is a store method and not `ReadUsage` + a caller-side
// decision.** The interesting concurrency here is precisely the one a
// budget exists to bound: N submissions arriving at once. Read-then-decide
// -then-write admits all N, because every one of them reads the same
// pre-burst count and every one of them concludes there is room. That is
// not a rare race — it is the *expected* behaviour of an agent fanning out,
// which is the workload this phase was written for. So the read, the
// decision and the increment happen inside one critical section owned by
// the implementation, and the pure policy function is handed in rather than
// applied outside. A store that cannot make that atomic cannot enforce a
// concurrency cap at all; it can only report having missed one.
//
// **Six portability rules (GP 12) — audited.**
// 1. *Identity by value* — scope ids, period keys, `decimal`s and the
//    `ComputeBudget` / `ComputeBudgetUsage` records. No live handle, no
//    transaction object, no lock crosses the surface.
// 2. *Async at every boundary* — every member returns `Async<_>`.
// 3. *Retry + supervision as data* — a refusal is a `ComputeBudgetDenial`
//    in the error channel, never an exception; a write failure is a
//    `Result` string. Nothing here takes a callback.
// 4. *Stateless between invocations* — every call carries the whole scope
//    + period key. An implementation caches nothing across calls that
//    would make a recycled instance answer differently.
// 5. *No cross-shard ordering* — two scopes are independent, and two
//    periods within a scope are independent. Ordering is promised only
//    within one `(scopeId, periodKey)` row.
// 6. *Precision at the lower bound* — counts are `int`, cost is `decimal`,
//    durations are `TimeSpan`. The period boundary is UTC-day / UTC-month
//    granularity and is declared as such, never sub-second.

/// Phase 451 — where a scope's compute budget and its live consumption
/// live.
///
/// Two concerns behind one seam, deliberately: the budget (rarely written,
/// Owner/Admin-gated, operator-authored) and the usage row (written on
/// every admission and every settle). Splitting them into two interfaces
/// would mean every enforcement point resolving two services to answer one
/// question, and every implementation coordinating a decision across two
/// stores it does not own jointly — which is the atomicity above, given
/// away.
type IComputeBudgetStore =
    /// The budget configured for `scopeId`.
    ///
    /// Returns `ComputeBudget.unrestricted` — never an option — for a
    /// scope with nothing configured, so "no budget" and "a budget that
    /// permits everything" are one value and every caller has one path.
    /// A read failure is also unrestricted: a store that cannot be reached
    /// must not become an outage for every submission in the deployment
    /// (the failure direction a budget may have is *admitting* work, never
    /// refusing everything).
    abstract GetBudget: scopeId: string -> Async<ComputeBudget>

    /// Write `scopeId`'s budget. **Owner/Admin-gated at the call site**
    /// (GP 4) — the store enforces scope partitioning structurally, the
    /// caller enforces who may write.
    abstract SetBudget: scopeId: string * budget: ComputeBudget -> Async<Result<unit, string>>

    /// The live usage row for one scope+period. Read-only; returns
    /// `ComputeBudgetUsage.empty` for a period nothing has been charged
    /// to yet.
    abstract ReadUsage: scopeId: string * periodKey: string -> Async<ComputeBudgetUsage>

    /// **Atomically** evaluate `decide` against the live usage row and, if
    /// it admits, apply the reservation (`InFlight + 1`, `Spent + cost`)
    /// in the same critical section.
    ///
    /// `decide` is the pure policy — normally a partial application of
    /// `ComputeBudgetPolicy.admit`. Handing the decision IN rather than
    /// exposing the row and trusting the caller is what closes the
    /// read-then-write race the file header describes; an implementation
    /// MUST NOT satisfy this by calling `ReadUsage` and then a separate
    /// write.
    ///
    /// `Ok` carries the usage row **as it stands after** the reservation,
    /// so a caller can emit a threshold warning without a second read.
    abstract Admit:
        scopeId: string *
        periodKey: string *
        cost: decimal *
        decide: (ComputeBudgetUsage -> Result<unit, ComputeBudgetDenial>) ->
            Async<Result<ComputeBudgetUsage, ComputeBudgetDenial>>

    /// Release one reservation on a terminal outcome: `InFlight - 1`, and
    /// `Spent + costAdjustment`.
    ///
    /// `costAdjustment` is the **difference** between what the run actually
    /// cost and what admission reserved, so it is frequently negative (a
    /// run reserved at a per-minute estimate that finished in seconds) and
    /// zero for a flat per-run cost model. A delta rather than an absolute
    /// because the store does not retain per-run reservations — it holds
    /// one aggregate per period, which is what makes the row a single
    /// cheap read on the hot path.
    ///
    /// Idempotency is NOT promised: settling twice releases two slots. The
    /// callers are the two enforcement points, each of which settles once
    /// per handle it admitted, and adding a per-run ledger to make this
    /// idempotent would trade the aggregate row (one blob, one read) for
    /// per-run state whose lifetime nothing bounds.
    ///
    /// `InFlight` never goes below zero — an over-settle clamps rather
    /// than wrapping, because a negative in-flight count would silently
    /// grant extra concurrency, which is the wrong failure direction.
    abstract Settle: scopeId: string * periodKey: string * costAdjustment: decimal -> Async<unit>

/// Phase 451 — where budget state lives in blob storage. Shared so a
/// future admin surface or sweep enumerates exactly what the store writes,
/// the reason `ComputeMemoLayout` exists.
///
/// Phase 689: the usage half is `BudgetLedgerLayout` with this domain's
/// label, which is why the shared ledger reads and writes exactly the
/// blobs this phase has been writing since it shipped — the adoption is a
/// re-expression, not a data migration. The budget blob stays here,
/// because where a domain's *ceilings* are configured is the domain's own
/// question (a token budget will read them from per-team config) and the
/// ledger deliberately owns only the counter.
[<RequireQualifiedAccess>]
module ComputeBudgetLayout =
    /// Reserved SDK-level container, alongside every other platform store.
    [<Literal>]
    let DefaultContainer = "_platform"

    /// Blob-name prefix shared by everything this store writes.
    [<Literal>]
    let BlobPrefix = "compute-budget/"

    /// The ledger key for one scope+period in this domain.
    let ledgerKey (scopeId: string) (periodKey: string) : BudgetLedgerKey =
        BudgetLedgerKey.create ComputeBudget.Domain scopeId periodKey

    /// Every blob for one scope. The scope segment is what makes the
    /// layout structurally isolating (GP 4) — a lookup for one scope
    /// cannot construct a path under another, and the only prefix a caller
    /// can enumerate is bounded by the scope it resolved.
    let scopePrefix (scopeId: string) : string =
        BudgetLedgerLayout.scopePrefix ComputeBudget.Domain scopeId

    /// The configured budget for one scope.
    let budgetBlob (scopeId: string) : string = scopePrefix scopeId + "budget.json"

    /// The usage row for one scope+period. The period key is IN the blob
    /// name, which is what makes a period reset free: the next period is a
    /// different blob that does not exist yet, and a blob that does not
    /// exist reads as zero consumption.
    let usageBlob (scopeId: string) (periodKey: string) : string =
        BudgetLedgerLayout.usageBlob (ledgerKey scopeId periodKey)