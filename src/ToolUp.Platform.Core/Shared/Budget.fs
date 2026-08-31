// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 689 — the platform budget seam ────────────────────────────────
//
// Resource-exhaustion defence arrived one mechanism at a time, and each
// one invented its own shape. Phase 451 governs external compute
// submissions; the render-cost gates bound a tree's size and time; the AI
// token and monetary-spend budgets are still ahead. Read together they
// are the same four sentences in four vocabularies — *this* subject may
// spend *this much* of *this* resource in *this* window, and here is what
// happens when it cannot. Phase 451 said as much in its own header ("shaped
// like the AI token-budget model so operators learn one budget shape") and
// left a note to extract a shared helper "only if it falls out naturally".
// With two instances shipped it does, and this file is the extraction.
//
// **What a budget IS, in four parts**, matching the four things every
// instance has to do:
//
//   *declare* — a `BudgetSubject` (who), a `BudgetPeriod` (when), and a set
//   of ceilings expressed as `BudgetClaim`s (how much of what).
//   *check*   — `BudgetPolicy`, a total function from claims to a typed
//   `BudgetVerdict`: allowed, near the limit, or refused with the reason.
//   *account* — `BudgetAccount`, the pair of effects a decision produces,
//   as data rather than as an interface.
//   *store*   — `IBudgetLedger` (Server tier — a ledger needs a backend,
//   and none of that is Fable-safe).
//
// **A ceiling is a `BudgetClaim`, and that unification is the whole
// design.** Phase 451's three dimensions look unrelated — a concurrency
// cap counts runs in flight, a run-duration cap is a property of one
// submission and accumulates nothing, an allowance accumulates spend
// across a period — and all three are `Spent + Requested > Ceiling`:
//
//   concurrency  InFlight + 1        > MaxConcurrent
//   run-duration 0 + declaredSeconds > capSeconds
//   allowance    spent + cost        > periodAllowance
//
// So does a token cap (`used + estimate > perHour`) and a monetary one
// (`spentThisPeriod + estimatedCost > ceiling`). One predicate, one
// refusal record, one ordering rule — which is what lets an operator learn
// one budget shape and a reviewer audit exhaustion defence in one place.
//
// **`<= 0` is unrestricted on every ceiling**, carried forward from Phase
// 451 because it is what makes the *absent* budget and the *empty* budget
// the same value: a subject with no configuration is unrestricted by
// construction rather than by a missing branch (GP 11), and a deployment
// that composes nothing pays nothing (GP 13).
//
// **`decimal`, never `float`, and never a currency.** Every quantity here
// is an abstract count in the dimension's own unit — cost units, tokens,
// runs, seconds — because a ceiling that drifts by a float epsilon is one
// an operator cannot reconcile, and because a currency in the SDK core
// would drag a pricing vocabulary and a rounding policy in behind it
// (GP 1). A monetary budget denominates its own units and says so in its
// dimension label; the seam neither knows nor cares.
//
// **The period is a storage KEY, not a counter with a reset job.** The
// next period is a different key that starts at zero by not existing yet,
// so a reset is free and cannot fail to run — the failure mode where a
// missed reset silently turns a budget into an outage. Inherited from
// Phase 451, which inherited it from the differential-privacy ledger's
// epoch key.
//
// **Fable-safe throughout** (GP 10): primitives, records, DUs and `Async`.
// The ledger, the JSON codec and the enforcement decorators are Server
// tier, so a client can hold and render a refusal without pulling any of
// it in.

/// Phase 689 — the window a budget's accumulating ceilings are measured
/// over.
///
/// A superset of Phase 451's `ComputeBudgetPeriod`: `Hourly` is here
/// because a per-user token budget is naturally hourly, and adding it to
/// the compute DU would have been surface a compute deployment cannot use.
/// The compute periods project onto these one-for-one, so a
/// `ComputeBudgetPeriod.key` and the seam's key for the same window are
/// the same string.
[<RequireQualifiedAccess>]
type BudgetPeriod =
    /// One allowance for all time — a hard lifetime cap. Never refills.
    | Perpetual
    /// Refills at the top of each UTC hour.
    | Hourly
    /// Refills at UTC midnight.
    | Daily
    /// Refills at the first instant of the UTC month.
    | Monthly

[<RequireQualifiedAccess>]
module BudgetPeriod =
    /// Stable lowercase label — the config value, the log string and the
    /// audit-payload field. Stable across releases: a stored budget is
    /// keyed by it.
    let label =
        function
        | BudgetPeriod.Perpetual -> "perpetual"
        | BudgetPeriod.Hourly -> "hourly"
        | BudgetPeriod.Daily -> "daily"
        | BudgetPeriod.Monthly -> "monthly"

    /// Every case, shortest window last — the list a config UI renders and
    /// an exhaustiveness test enumerates.
    let all = [
        BudgetPeriod.Perpetual
        BudgetPeriod.Monthly
        BudgetPeriod.Daily
        BudgetPeriod.Hourly
    ]

    /// Parse a `label`. `None` for anything else — a stored period nobody
    /// wrote is a defect in the writer, and guessing at one would silently
    /// change the window a budget is measured over.
    let parse (raw: string) : BudgetPeriod option =
        if isNull (box raw) then
            None
        else
            all |> List.tryFind (fun p -> label p = raw.Trim().ToLowerInvariant())

    /// The key identifying the period `now` falls in — `"perpetual"`,
    /// `"2026-08-05T14"`, `"2026-08-05"`, or `"2026-08"`.
    ///
    /// Always computed in UTC, never local time. A boundary that moved
    /// with the server's timezone would put two replicas of one deployment
    /// in different periods for an hour twice a year, and the resulting
    /// double allowance is exactly the kind of thing nobody notices until
    /// the bill.
    let key (period: BudgetPeriod) (now: DateTime) : string =
        let utc = now.ToUniversalTime()

        match period with
        | BudgetPeriod.Perpetual -> "perpetual"
        | BudgetPeriod.Hourly -> utc.ToString "yyyy-MM-dd" + "T" + utc.ToString "HH"
        | BudgetPeriod.Daily -> utc.ToString "yyyy-MM-dd"
        | BudgetPeriod.Monthly -> utc.ToString "yyyy-MM"

/// Phase 689 — who a budget decision is about.
///
/// Three stable labels rather than typed identities, for the reason every
/// wire-facing record in this codebase carries labels: the subject rides an
/// audit payload, a peer refusal and a Fable client without a converter,
/// and a domain that needed its own principal type would be a domain the
/// seam could not serve.
///
/// `ClassLabel` is the axis policy discriminates on WITHIN a scope — Phase
/// 451's `SubmitterClass.label`, a user id under a per-user token budget, a
/// model id under a per-model spend cap. `""` means the scope's default,
/// which is what a domain with no such axis uses.
type BudgetSubject = {
    /// Stable label of the budget family — `"compute-budget"`,
    /// `"ai-tokens"`. Namespaces every ledger key and every audit row, so
    /// two budget domains in one deployment can never read each other's
    /// consumption.
    Domain: string
    /// The tenant / team / scope whose budget this is. The GP 4 partition:
    /// a ledger key built from it cannot address another scope's row.
    ScopeId: string
    /// The class policy keys on within the scope. `""` = the default.
    ClassLabel: string
}

[<RequireQualifiedAccess>]
module BudgetSubject =
    /// A subject discriminated by class.
    let create (domain: string) (scopeId: string) (classLabel: string) : BudgetSubject = {
        Domain = domain
        ScopeId = scopeId
        ClassLabel = classLabel
    }

    /// A subject governed by its scope's default limits — the shape a
    /// domain with no per-class axis uses.
    let ofScope (domain: string) (scopeId: string) : BudgetSubject = create domain scopeId ""

/// Phase 689 — one ceiling, and what is being measured against it.
///
/// The unit of the whole seam. A claim is deliberately self-contained:
/// it carries the ceiling AND both halves of the measurement, so the check
/// is a pure predicate over one value and a refusal can report the three
/// numbers that make it actionable without reaching back for anything.
type BudgetClaim = {
    /// Stable lowercase label for the ceiling — `"concurrency"`,
    /// `"tokens-per-hour"`, `"spend"`. Appears verbatim in the refusal, so
    /// a client branches on the dimension rather than string-matching a
    /// message.
    Dimension: string
    /// The configured ceiling in this dimension's own unit. `<= 0` is
    /// **unrestricted** — the claim can never be breached and never warns.
    Ceiling: decimal
    /// What the subject had already consumed against `Ceiling` when the
    /// request arrived. `0` for a dimension that accumulates nothing (a
    /// per-request size or duration cap).
    Spent: decimal
    /// What this request asks for **on top of** `Spent`. Carried
    /// separately so a caller can tell "you are already over" from "this
    /// one request would take you over", which are different remedies.
    Requested: decimal
}

[<RequireQualifiedAccess>]
module BudgetClaim =
    /// A claim in `dimension` with ceiling `ceiling`, `spent` already
    /// consumed and `requested` asked for now.
    let create (dimension: string) (ceiling: decimal) (spent: decimal) (requested: decimal) : BudgetClaim = {
        Dimension = dimension
        Ceiling = ceiling
        Spent = spent
        Requested = requested
    }

    /// A per-request cap that accumulates nothing — a maximum duration, a
    /// maximum payload size. `Spent` is zero by construction.
    let perRequest (dimension: string) (ceiling: decimal) (requested: decimal) : BudgetClaim =
        create dimension ceiling 0M requested

    /// `true` when this ceiling constrains nothing. The fast path a check
    /// short-circuits on before reading any state (GP 13).
    let isUnrestricted (claim: BudgetClaim) : bool = claim.Ceiling <= 0M

    /// Would admitting this request take the subject past the ceiling?
    ///
    /// **One predicate for every dimension** — see the file header. Strict
    /// `>`, so a request that lands exactly ON the ceiling is admitted:
    /// an allowance of 100 units admits the run that takes it to 100 and
    /// refuses the next, which is what "an allowance of 100" means.
    let wouldExceed (claim: BudgetClaim) : bool =
        not (isUnrestricted claim) && claim.Spent + claim.Requested > claim.Ceiling

    /// Consumption remaining under the ceiling, floored at zero.
    /// `0` for an unrestricted claim — ask `isUnrestricted` first; an
    /// unrestricted ceiling has no meaningful remainder.
    let remaining (claim: BudgetClaim) : decimal =
        if isUnrestricted claim then
            0M
        else
            max 0M (claim.Ceiling - claim.Spent)

/// Phase 689 — a budget refusal, as data.
///
/// Phase 451's `ComputeBudgetDenial` field-for-field, plus the `Domain`
/// that tells a reader WHICH budget refused when a deployment runs more
/// than one. A refusal that says only "over budget" leaves its recipient —
/// often an agent deciding whether to narrow its search or wait — with
/// nothing to decide on, so it names which ceiling, for which class, in
/// which period, and how far over.
///
/// Every field is a primitive or a stable label (GP 12 rule 1), so the
/// denial serialises onto a peer wire, into an audit payload and into a
/// Fable client without carrying a DU converter.
type BudgetDenial = {
    /// `BudgetSubject.Domain` of the budget that refused.
    Domain: string
    /// Scope whose budget refused the request.
    ScopeId: string
    /// `BudgetSubject.ClassLabel` of the refused request. `""` when the
    /// scope's default limits governed it.
    ClassLabel: string
    /// `BudgetClaim.Dimension` of the ceiling that was hit.
    Dimension: string
    /// The configured ceiling, in the dimension's own unit.
    Quota: decimal
    /// What the subject had already consumed against `Quota`.
    Spent: decimal
    /// What this request asked for on top of `Spent`.
    Requested: decimal
    /// `BudgetPeriod.key` of the accounting period the refusal was
    /// measured in. `"perpetual"` when the budget never refills.
    PeriodKey: string
}

[<RequireQualifiedAccess>]
module BudgetDenial =
    /// One-line operator-facing description. The typed fields, not this
    /// string, are the contract.
    let describe (denial: BudgetDenial) : string =
        sprintf
            "budget '%s' exceeded for scope '%s': %s limit %M reached by a '%s' request (already consumed %M, this request needs %M) in period '%s'"
            denial.Domain
            denial.ScopeId
            denial.Dimension
            denial.Quota
            denial.ClassLabel
            denial.Spent
            denial.Requested
            denial.PeriodKey

/// Phase 689 — a leading indicator: the subject was admitted, and is now
/// past the warning threshold on an accumulating ceiling.
///
/// Distinct from a denial rather than a severity flag on one, because the
/// two are not the same event: a denial reports work that did NOT happen
/// and needs a remedy now; a warning reports work that DID happen and
/// gives an operator the remaining fraction of the period to decide
/// whether to raise the ceiling.
type BudgetWarning = {
    /// `BudgetSubject.Domain` of the budget that is filling up.
    Domain: string
    /// Scope whose consumption crossed the threshold.
    ScopeId: string
    /// `BudgetSubject.ClassLabel` of the request that crossed it.
    ClassLabel: string
    /// `BudgetClaim.Dimension` of the ceiling being approached.
    Dimension: string
    /// The configured ceiling.
    Quota: decimal
    /// Consumption **after** the admitted request.
    Spent: decimal
    /// Fraction of `Quota` that triggered this warning.
    Threshold: decimal
    /// `BudgetPeriod.key` of the accounting period.
    PeriodKey: string
}

[<RequireQualifiedAccess>]
module BudgetWarning =
    /// One-line operator-facing description.
    let describe (warning: BudgetWarning) : string =
        sprintf
            "budget '%s' for scope '%s' is at %M of %M on '%s' (%M%% threshold) in period '%s'"
            warning.Domain
            warning.ScopeId
            warning.Spent
            warning.Quota
            warning.Dimension
            (warning.Threshold * 100M)
            warning.PeriodKey

/// Phase 689 — the typed answer to "may this request proceed?".
///
/// Three cases, not a `Result`: the middle one is the reason. A budget
/// that could only say yes or no would have to express "yes, and you are
/// nearly out" as a side effect nobody can pattern-match, which is how a
/// leading indicator turns into a log line.
[<RequireQualifiedAccess>]
type BudgetVerdict =
    /// Proceed. No ceiling is threatened.
    | Allowed
    /// Proceed, and report — an accumulating ceiling has crossed its
    /// warning threshold.
    | NearLimit of BudgetWarning
    /// Do not proceed. The denial names which ceiling and by how much.
    | Refused of BudgetDenial

/// Phase 689 — the check, as a pure function.
///
/// Separated from the ledger deliberately: the interesting part of a
/// budget is the policy, and a policy expressed as a total function over
/// claims is one a test can exhaust without a blob, a clock or a
/// container. The ledger's only job is to make the read and the
/// reservation atomic.
[<RequireQualifiedAccess>]
module BudgetPolicy =

    /// Fraction of a ceiling at which an admitted request warns. `0.8` —
    /// late enough not to cry wolf, early enough that an operator raising
    /// the ceiling still has a fifth of the period to do it in.
    let defaultWarnThreshold = 0.8M

    /// The denial `claim` produces for `subject` in `periodKey`.
    let deny (subject: BudgetSubject) (periodKey: string) (claim: BudgetClaim) : BudgetDenial = {
        Domain = subject.Domain
        ScopeId = subject.ScopeId
        ClassLabel = subject.ClassLabel
        Dimension = claim.Dimension
        Quota = claim.Ceiling
        Spent = claim.Spent
        Requested = claim.Requested
        PeriodKey = periodKey
    }

    /// The warning `claim` produces, given a `Spent` that already includes
    /// the admitted request.
    let warn (subject: BudgetSubject) (periodKey: string) (threshold: decimal) (claim: BudgetClaim) : BudgetWarning = {
        Domain = subject.Domain
        ScopeId = subject.ScopeId
        ClassLabel = subject.ClassLabel
        Dimension = claim.Dimension
        Quota = claim.Ceiling
        Spent = claim.Spent
        Threshold = threshold
        PeriodKey = periodKey
    }

    /// The first breached ceiling, in the order the caller listed them.
    ///
    /// **Order is the caller's to choose and it is load-bearing.**
    /// Reporting the *first* ceiling hit rather than all of them is
    /// deliberate: a refusal naming three problems invites fixing the
    /// wrong one. Phase 451 lists concurrency → duration → allowance,
    /// cheapest and most-immediate first — the burst control an agent
    /// trips before it has spent anything, then the property of the
    /// submission alone, then the slow-moving allowance whose remedy
    /// (wait for the period to roll) is the least actionable.
    let breach (claims: BudgetClaim list) : BudgetClaim option =
        claims |> List.tryFind BudgetClaim.wouldExceed

    /// May this request proceed? `Error` names the first ceiling hit.
    ///
    /// The two-case form, for a caller that reserves through a ledger and
    /// therefore learns its post-request consumption from the ledger's
    /// answer rather than by arithmetic. Such a caller pairs this with
    /// `crossedThreshold` on the row it gets back.
    let check (subject: BudgetSubject) (periodKey: string) (claims: BudgetClaim list) : Result<unit, BudgetDenial> =
        match breach claims with
        | Some claim -> Error(deny subject periodKey claim)
        | None -> Ok()

    /// Has consumption reached `threshold` of this ceiling? `Spent` is
    /// read as the post-request figure.
    let crossedThreshold (threshold: decimal) (claim: BudgetClaim) : bool =
        not (BudgetClaim.isUnrestricted claim)
        && claim.Spent >= claim.Ceiling * threshold

    /// The full verdict for one request, computed entirely from the
    /// claims: refused on the first breach, else near-limit on the first
    /// ceiling whose post-request consumption has crossed `threshold`,
    /// else allowed.
    ///
    /// The one-call form, for a domain whose consumption is a plain read
    /// rather than an atomic reservation (a token budget summing an event
    /// store, a spend budget estimating a call's cost).
    let verdict
        (subject: BudgetSubject)
        (periodKey: string)
        (threshold: decimal)
        (claims: BudgetClaim list)
        : BudgetVerdict =
        match breach claims with
        | Some claim -> BudgetVerdict.Refused(deny subject periodKey claim)
        | None ->
            let settled (claim: BudgetClaim) = {
                claim with
                    Spent = claim.Spent + claim.Requested
                    Requested = 0M
            }

            match claims |> List.map settled |> List.tryFind (crossedThreshold threshold) with
            | Some claim -> BudgetVerdict.NearLimit(warn subject periodKey threshold claim)
            | None -> BudgetVerdict.Allowed

/// Phase 689 — one subject's live consumption within one period.
///
/// `InFlight` is a **reservation** count, not an observation: it is
/// incremented when a request is admitted and decremented when the work
/// reaches a terminal outcome. That makes a concurrency ceiling
/// enforceable against work that has not reported anything yet, which is
/// the whole point — a cap that could only count work a backend had
/// already acknowledged would admit an unbounded burst in the window
/// before the first acknowledgement. A domain with no concurrency
/// dimension simply leaves it at zero.
///
/// The honest cost of a reservation model is stated rather than hidden: a
/// process that dies between admitting and settling leaks one slot until
/// the period rolls. It is wrong in the safe direction — a leaked slot
/// refuses work, it never admits work it should have refused.
type BudgetUsage = {
    /// `BudgetSubject.Domain` this row accounts for.
    Domain: string
    /// Scope this row accounts for.
    ScopeId: string
    /// `BudgetPeriod.key` this row accounts for.
    PeriodKey: string
    /// Requests admitted and not yet settled.
    InFlight: int
    /// Units consumed in this period, including the reservations held by
    /// the `InFlight` requests.
    Spent: decimal
    /// When the row last changed (UTC).
    UpdatedAt: DateTime
}

[<RequireQualifiedAccess>]
module BudgetUsage =
    /// The zero row for a subject+period that has never been written. A
    /// ledger returns this rather than an option, so "no consumption yet"
    /// and "consumed nothing" are the same value at every call site.
    let empty (domain: string) (scopeId: string) (periodKey: string) : BudgetUsage = {
        Domain = domain
        ScopeId = scopeId
        PeriodKey = periodKey
        InFlight = 0
        Spent = 0M
        UpdatedAt = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    }

    /// The row after admitting one request reserving `cost`. The exact
    /// mutation every ledger implementation applies inside its critical
    /// section — shared so two ledgers cannot drift on what a reservation
    /// means.
    let reserve (cost: decimal) (at: DateTime) (usage: BudgetUsage) : BudgetUsage = {
        usage with
            InFlight = usage.InFlight + 1
            Spent = usage.Spent + cost
            UpdatedAt = at
    }

    /// The row after settling one reservation, folding in
    /// `costAdjustment` — the difference between what the work actually
    /// cost and what admission reserved, so frequently negative and zero
    /// for a flat per-request model.
    ///
    /// Both figures are **floored**. A negative in-flight count would
    /// silently grant extra concurrency; crediting a period for an
    /// adjustment larger than its recorded spend would manufacture
    /// allowance out of a clock boundary (the reservation was made in a
    /// period that has since rolled).
    let settle (costAdjustment: decimal) (at: DateTime) (usage: BudgetUsage) : BudgetUsage = {
        usage with
            InFlight = max 0 (usage.InFlight - 1)
            Spent = max 0M (usage.Spent + costAdjustment)
            UpdatedAt = at
    }

/// Phase 689 — what a deployment does with a budget decision.
///
/// **A record of functions, not an interface** (GP 12 rule 3 — behaviour
/// as data). Accounting is deployment policy: one domain writes a typed
/// audit event, another emits a metric, a third does both and warns a log.
/// Expressing it as data means composing one is a `let`, and means the
/// seam does not have to reference the audit substrate — which it must not,
/// because this file compiles before it.
///
/// A refusal nobody can see afterwards is a support ticket with no answer
/// (GP 6), so `silent` is the explicit opt-out rather than the shape you
/// get by forgetting.
type BudgetAccount = {
    /// Called when a request is refused, before the caller's own error
    /// path runs.
    OnRefused: BudgetDenial -> Async<unit>
    /// Called when an admitted request crossed the warning threshold.
    OnNearLimit: BudgetWarning -> Async<unit>
}

[<RequireQualifiedAccess>]
module BudgetAccount =
    /// Records nothing. The identity of `combine`, and the value a
    /// deployment that has deliberately chosen no accounting composes.
    let silent: BudgetAccount = {
        OnRefused = fun _ -> async { return () }
        OnNearLimit = fun _ -> async { return () }
    }

    /// Record refusals only.
    let onRefused (handle: BudgetDenial -> Async<unit>) : BudgetAccount = { silent with OnRefused = handle }

    /// Record threshold crossings only.
    let onNearLimit (handle: BudgetWarning -> Async<unit>) : BudgetAccount = { silent with OnNearLimit = handle }

    /// Both accounts, `first` before `second`. Sequential rather than
    /// parallel: accounting is ordered with respect to itself (an audit
    /// row before the metric that summarises it), and two sinks racing
    /// buys nothing on a path that has already decided.
    let combine (first: BudgetAccount) (second: BudgetAccount) : BudgetAccount = {
        OnRefused =
            fun denial -> async {
                do! first.OnRefused denial
                do! second.OnRefused denial
            }
        OnNearLimit =
            fun warning -> async {
                do! first.OnNearLimit warning
                do! second.OnNearLimit warning
            }
    }

    /// Apply the account to a verdict — the one call a caller that used
    /// `BudgetPolicy.verdict` makes before acting on it. `Allowed` records
    /// nothing, which is the point: an allowed request on an unthreatened
    /// budget must cost nothing to observe (GP 13).
    let record (account: BudgetAccount) (verdict: BudgetVerdict) : Async<unit> =
        match verdict with
        | BudgetVerdict.Allowed -> async { return () }
        | BudgetVerdict.NearLimit warning -> account.OnNearLimit warning
        | BudgetVerdict.Refused denial -> account.OnRefused denial