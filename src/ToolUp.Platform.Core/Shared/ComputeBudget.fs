// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Compute-budget governance ───────────────────────────────
//
// The platform has a portable way to hand work to compute that is not
// this process, and a way to enqueue a long fit.
// Neither of them can say **no**. A submission is accepted because it is
// well-formed, and a deployment whose work costs real money on a metered
// backend has, until this phase, exactly one lever: hope.
//
// This file is the value layer of the answer — the budget as data, and the
// admission decision as a pure function over it. The store, the decorator
// and the cost model are Server-tier; everything here is what a budget IS,
// so it survives serialisation, rides the wire in a typed refusal, and can
// be reasoned about by a test with no infrastructure at all.
//
// **It compiles BEFORE ExternalCompute.fs, which is why the denial's
// projection to `ExternalComputeError` lives Server-side rather than
// here.** `ExternalWorkSpec` carries a `SubmitterClass`, so this file has
// to precede it, and precedence cannot be mutual. Projecting a denial onto
// the error shape is a decorator concern anyway — a Fable client
// receives the typed `BudgetDenial`, never the lossy string form —
// so the split costs nothing and the helper sits with its one caller
// (`ComputeBudgetGuard.toComputeError`).
//
// **Cost is an abstract unit, never a currency (GP 1).** `PeriodAllowance`
// is a `decimal` count of *cost units*, and forge never learns what one
// costs: the mapping from a run to a number is a deployment-supplied cost
// model (Server tier). Putting a currency here would mean the SDK carrying
// a pricing vocabulary, a rounding policy, and eventually a rate table for
// a backend it has never heard of — the same argument that keeps
// `ExternalWorkSpec.ResourceHints` a string map rather than a typed record.
// `decimal` rather than `float` for the reason `UsageRecord.Quantity` is:
// an allowance that drifts by a float epsilon per run is an allowance an
// operator cannot reconcile.
//
// **Zero is unrestricted, everywhere.** Every numeric ceiling reads "no
// limit" at `<= 0`, matching the quota fields an operator already
// knows (`MaxConcurrentJobs` / `MaxAITokensPerDay` default to `0` = no
// ceiling). It also makes the *absent* budget and the *empty* budget the
// same value, so a scope with no configuration is unrestricted by
// construction rather than by a missing branch (GP 11).
//
// **Submitter class is on the submission, not on the caller.** An agent
// exploring a branch space and a human clicking "fit this" arrive at the
// identical seam with the identical payload; the only thing that
// distinguishes them is what the submitter *declares*. Policy can then gate
// agent-driven exploration harder than interactive use without forge
// learning what an "agent exploration" is — which it must not, because the
// distinction is a domain one and the substrate is not.

/// Who (or what) asked for this unit of work.
///
/// A **closed** vocabulary, deliberately: policy keyed on an open string
/// would let a submitter mint a class nobody wrote a limit for and be
/// governed by the default, which is the wrong direction for a control
/// whose whole purpose is to be stricter on the least-supervised path.
/// Three cases is what the interface plan reserved (decision D5) and what
/// an operator can hold in their head.
///
/// `[<RequireQualifiedAccess>]` for the reason `ExecutionProfile` carries
/// it — `Scheduled` and `Human` are collision-prone case names in a
/// namespace this size, and an unqualified one is how a call site silently
/// binds the union you did not mean.
[<RequireQualifiedAccess>]
type SubmitterClass =
    /// An interactive request a person is waiting on. The most permissive
    /// class by convention, and the default every call site that declares no class gets
    /// (GP 11) — an existing deployment's submissions do not silently
    /// become agent traffic.
    | Human
    /// A recurring / triggered submission with no person waiting — a cron
    /// re-fit, a nightly re-score. Predictable volume, so a deployment
    /// typically budgets it separately from interactive use rather than
    /// more harshly.
    | Scheduled
    /// A submission an autonomous agent decided to make. The class the
    /// whole mechanism exists for: an agent exploring a branch space
    /// generates unbounded, unsupervised spend that looks exactly like
    /// legitimate work one submission at a time.
    | AgentInitiated

[<RequireQualifiedAccess>]
module SubmitterClass =
    /// Stable lowercase label — the wire form, the policy-map key, the
    /// audit-payload field, and the log string. Stable across releases:
    /// a stored per-class limit is keyed by it.
    let label =
        function
        | SubmitterClass.Human -> "human"
        | SubmitterClass.Scheduled -> "scheduled"
        | SubmitterClass.AgentInitiated -> "agent"

    /// Every case, in escalating-scrutiny order. The list a config UI
    /// renders and an exhaustiveness test enumerates.
    let all = [
        SubmitterClass.Human
        SubmitterClass.Scheduled
        SubmitterClass.AgentInitiated
    ]

    /// Parse a `label`. `None` for anything else — an unrecognised class
    /// is a caller defect, and the callers that must not fail on one use
    /// `ofLabelOrHuman` and say so.
    let parse (raw: string) : SubmitterClass option =
        if isNull (box raw) then
            None
        else
            all |> List.tryFind (fun c -> label c = raw.Trim().ToLowerInvariant())

    /// Parse a `label`, falling back to `Human` for an unknown or absent
    /// one.
    ///
    /// **The fallback direction is deliberate and is the conservative
    /// one, which is not the obvious reading.** Falling back to
    /// `AgentInitiated` would be "stricter" and is wrong: this function
    /// exists for the *legacy* case — a persisted job payload written
    /// before this phase, an older peer that does not send the field — and
    /// that traffic is overwhelmingly the interactive traffic the
    /// deployment was already running. Defaulting it to the harshest class
    /// would refuse work that has always been allowed, at upgrade time,
    /// with a budget the operator has not yet configured. A submitter that
    /// wants agent scrutiny declares it; forge does not infer it from
    /// silence.
    let ofLabelOrHuman (raw: string) : SubmitterClass =
        parse raw |> Option.defaultValue SubmitterClass.Human

/// One set of ceilings. The three dimensions the interface
/// plan named, and no more.
///
/// Each is independently disableable at `<= 0` / `None`, so a deployment
/// that only cares about runaway concurrency sets `MaxConcurrent` and
/// leaves the rest unrestricted without having to invent a spend model.
type ComputeBudgetLimits = {
    /// Maximum runs admitted and not yet settled at one time. `<= 0` is
    /// unrestricted.
    ///
    /// This is the dimension that bounds a *burst*, and it is the one an
    /// agent trips first: a spend allowance is only breached after the
    /// spend has happened, whereas a concurrency cap refuses the hundredth
    /// simultaneous submission before any of them has cost anything.
    MaxConcurrent: int
    /// Longest wall-clock a single run may declare. `None` is
    /// unrestricted.
    ///
    /// Enforced in both directions at admission (see
    /// `ComputeBudgetPolicy.admit`): a submission declaring a longer
    /// budget than this is refused, and one declaring none is **clamped**
    /// to it rather than admitted unbounded — a duration cap that only
    /// caught the submissions honest enough to declare a duration would
    /// bind exactly the callers who did not need binding.
    MaxRunDuration: TimeSpan option
    /// Cost units admissible per period. `<= 0` is unrestricted.
    ///
    /// Abstract units, never a currency — see the file header.
    PeriodAllowance: decimal
}

[<RequireQualifiedAccess>]
module ComputeBudgetLimits =
    /// No ceiling on any dimension — the value an unconfigured scope has,
    /// and the identity the whole model degrades to (GP 11 / GP 13).
    let unrestricted: ComputeBudgetLimits = {
        MaxConcurrent = 0
        MaxRunDuration = None
        PeriodAllowance = 0M
    }

    /// `true` when no dimension constrains anything — the fast path an
    /// admission check short-circuits on before reading any state.
    let isUnrestricted (limits: ComputeBudgetLimits) : bool =
        limits.MaxConcurrent <= 0
        && limits.MaxRunDuration.IsNone
        && limits.PeriodAllowance <= 0M

    /// A concurrency-only budget.
    let concurrency (maxConcurrent: int) : ComputeBudgetLimits = {
        unrestricted with
            MaxConcurrent = maxConcurrent
    }

    /// A period-allowance-only budget.
    let allowance (units: decimal) : ComputeBudgetLimits = {
        unrestricted with
            PeriodAllowance = units
    }

    /// A run-duration-only budget.
    let runDuration (longest: TimeSpan) : ComputeBudgetLimits = {
        unrestricted with
            MaxRunDuration = Some longest
    }

/// One scope's whole budget: a period, a default set of
/// ceilings, and per-submitter-class overrides.
///
/// **An override REPLACES the default rather than layering on it.** A
/// per-dimension merge (agent inherits the default `MaxConcurrent` but
/// overrides `PeriodAllowance`) reads as more expressive and is worse: the
/// operator writing "agents get 10 units" would silently also be granting
/// them the default concurrency, and the value they'd have to read to know
/// what an agent is actually allowed is not written down anywhere. A class
/// with an entry is governed entirely by that entry; a class without one is
/// governed entirely by `Limits`. One lookup, one answer.
type ComputeBudget = {
    /// Window `PeriodAllowance` is measured over. The seam's periods; a
    /// compute budget has no use for `Hourly`, which a token budget needs,
    /// and nothing stops a deployment choosing it.
    Period: BudgetPeriod
    /// Ceilings applied to any class with no override.
    Limits: ComputeBudgetLimits
    /// Per-`SubmitterClass.label` overrides. Keyed by the stable label
    /// rather than by the DU so the record round-trips through plain JSON
    /// without a DU converter and stays readable in a config blob.
    ClassLimits: Map<string, ComputeBudgetLimits>
}

[<RequireQualifiedAccess>]
module ComputeBudget =
    /// This budget family's stable label in the platform
    /// budget seam. Namespaces the ledger key, the blob prefix and the
    /// refusal, so a deployment running compute budgets alongside a token
    /// or spend budget can never have one read the other's consumption.
    ///
    /// It is the string the compute budget already used as its blob prefix, which
    /// is what makes adopting the shared ledger a re-expression rather
    /// than a data migration.
    [<Literal>]
    let Domain = "compute-budget"

    /// The budget an unconfigured scope has: no ceiling on any dimension,
    /// for any class. Structurally identical to having no budget at all,
    /// which is what makes "no budget configured" and "a budget that
    /// permits everything" the same behaviour rather than two code paths.
    let unrestricted: ComputeBudget = {
        Period = BudgetPeriod.Monthly
        Limits = ComputeBudgetLimits.unrestricted
        ClassLimits = Map.empty
    }

    /// A budget applying `limits` to every class over `period`.
    let create (period: BudgetPeriod) (limits: ComputeBudgetLimits) : ComputeBudget = {
        Period = period
        Limits = limits
        ClassLimits = Map.empty
    }

    /// Override the ceilings for one submitter class.
    let withClassLimits
        (submitter: SubmitterClass)
        (limits: ComputeBudgetLimits)
        (budget: ComputeBudget)
        : ComputeBudget =
        {
            budget with
                ClassLimits = budget.ClassLimits |> Map.add (SubmitterClass.label submitter) limits
        }

    /// The ceilings governing `submitter` — its override if it has one,
    /// otherwise the default.
    let limitsFor (submitter: SubmitterClass) (budget: ComputeBudget) : ComputeBudgetLimits =
        match budget.ClassLimits.TryFind(SubmitterClass.label submitter) with
        | Some overridden -> overridden
        | None -> budget.Limits

    /// `true` when no class is constrained on any dimension.
    let isUnrestricted (budget: ComputeBudget) : bool =
        ComputeBudgetLimits.isUnrestricted budget.Limits
        && budget.ClassLimits
           |> Map.forall (fun _ l -> ComputeBudgetLimits.isUnrestricted l)

/// Which ceiling a refusal hit. Enumerable data (interface plan
/// decision D6), so a client branches on the dimension rather than
/// string-matching a message.
[<RequireQualifiedAccess>]
type ComputeBudgetDimension =
    /// Too many runs already in flight for this scope.
    | Concurrency
    /// The submission declared a longer run than the budget permits.
    | RunDuration
    /// The period's cost allowance is exhausted.
    | PeriodAllowance

[<RequireQualifiedAccess>]
module ComputeBudgetDimension =
    /// Stable lowercase label for wire payloads, audit rows and logs.
    let label =
        function
        | ComputeBudgetDimension.Concurrency -> "concurrency"
        | ComputeBudgetDimension.RunDuration -> "run-duration"
        | ComputeBudgetDimension.PeriodAllowance -> "period-allowance"

/// The admission decision, as a pure function.
///
/// Separated from the store on purpose: the interesting part of a budget
/// is the policy, and a policy expressed as a total function over
/// `(limits, usage, submission)` is one a test can exhaust without a blob,
/// a clock, or a container. The store's job is only to make the read and
/// the reservation atomic.
///
/// **The decision runs on the platform budget seam.** `claims` projects this domain's three ceilings onto
/// `BudgetClaim`s and `admit` is `BudgetPolicy.check` over them. The three
/// dimensions look unrelated and are all `Spent + Requested > Ceiling`
/// (see `Budget.fs`), so the projection is total and the behaviour is
/// unchanged — what changes is that a compute refusal and a token refusal
/// are now the same decision, in the same order, reported in the same
/// shape.
[<RequireQualifiedAccess>]
module ComputeBudgetPolicy =

    /// Seconds of a `TimeSpan`, as the `decimal` a denial reports.
    let private seconds (span: TimeSpan) : decimal = decimal span.TotalSeconds

    /// This submission's ceilings as seam claims, in check
    /// order.
    ///
    /// **Order is concurrency → duration → allowance, cheapest and
    /// most-immediate first**, and `BudgetPolicy.breach` reports the first
    /// one hit. Concurrency is the burst control and the one an agent
    /// trips first; duration is a property of the submission alone;
    /// allowance is the slowest-moving and the one whose remedy (wait for
    /// the period to roll) is the least actionable. Reporting the *first*
    /// ceiling rather than all of them is deliberate — a refusal naming
    /// three problems invites fixing the wrong one.
    ///
    /// An unrestricted dimension contributes no claim at all rather than
    /// a claim with a zero ceiling: the two are equivalent to the check
    /// (`<= 0` is unrestricted), and the short list is what a caller
    /// inspecting the claims to render "3 of 10 runs, 40 of 250 units"
    /// wants to read.
    let claims
        (limits: ComputeBudgetLimits)
        (usage: BudgetUsage)
        (declaredDuration: TimeSpan option)
        (cost: decimal)
        : BudgetClaim list =
        [
            if limits.MaxConcurrent > 0 then
                BudgetClaim.create
                    (ComputeBudgetDimension.label ComputeBudgetDimension.Concurrency)
                    (decimal limits.MaxConcurrent)
                    (decimal usage.InFlight)
                    1M

            match limits.MaxRunDuration, declaredDuration with
            | Some cap, Some declared ->
                BudgetClaim.perRequest
                    (ComputeBudgetDimension.label ComputeBudgetDimension.RunDuration)
                    (seconds cap)
                    (seconds declared)
            | _ -> ()

            if limits.PeriodAllowance > 0M then
                BudgetClaim.create
                    (ComputeBudgetDimension.label ComputeBudgetDimension.PeriodAllowance)
                    limits.PeriodAllowance
                    usage.Spent
                    cost
        ]

    /// May one more run be admitted?
    ///
    /// `declaredDuration` is the submission's own wall-clock budget
    /// (`ExternalWorkSpec.Timeout`, or `None` for a fit whose duration the
    /// submitter did not bound). `cost` is what the deployment's cost
    /// model says this run reserves.
    let admit
        (scopeId: string)
        (submitter: SubmitterClass)
        (limits: ComputeBudgetLimits)
        (usage: BudgetUsage)
        (declaredDuration: TimeSpan option)
        (cost: decimal)
        : Result<unit, BudgetDenial> =
        let subject =
            BudgetSubject.create ComputeBudget.Domain scopeId (SubmitterClass.label submitter)

        claims limits usage declaredDuration cost
        |> BudgetPolicy.check subject usage.PeriodKey

    /// One-line operator-facing description of a compute refusal. The
    /// typed denial, not this string, is the contract.
    let describe (denial: BudgetDenial) : string =
        sprintf
            "compute budget exceeded for scope '%s': %s limit %M reached by a '%s' submission (already consumed %M, this request needs %M) in period '%s'"
            denial.ScopeId
            denial.Dimension
            denial.Quota
            denial.ClassLabel
            denial.Spent
            denial.Requested
            denial.PeriodKey

    /// The wall-clock budget a submission should actually carry once the
    /// run-duration cap is applied — the submission's own when it is
    /// within the cap, the cap itself when the submission declared none.
    ///
    /// This is the clamp `MaxRunDuration` needs to be worth having. A
    /// submission declaring nothing runs for as long as the backend
    /// allows, so a cap that only refused over-long *declarations* would
    /// leave the unbounded case — the one it exists to catch — untouched.
    /// An over-long declaration is refused by `admit` before this is
    /// reached, so this never silently shortens work the caller asked for.
    let effectiveDuration (limits: ComputeBudgetLimits) (declared: TimeSpan option) : TimeSpan option =
        match limits.MaxRunDuration, declared with
        | Some cap, None -> Some cap
        | _, declared -> declared