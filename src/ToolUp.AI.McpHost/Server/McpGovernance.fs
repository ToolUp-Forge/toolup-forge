// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.McpHost.McpGovernance

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Phase 489 — per-agent budgets + rate limits ─────────────────────
//
// **Neither ceiling is a new primitive, and the shard's premises about
// both have moved.**
//
// The task text names "the 9s shape" for token budgets. Phase 9s has not
// shipped, but the seam that generalises it HAS: Phase 689's
// `IBudgetLedger` + `BudgetPolicy` + `BudgetClaim` + `BudgetDenial`, the
// seam Phase 451's compute budget was itself re-expressed over. It is
// domain-namespaced, so per-agent MCP consumption cannot read or be read
// by a compute or token budget; its reservation is ATOMIC, which is the
// one property a concurrency ceiling cannot be built without and which
// read-then-decide-then-write structurally cannot provide against the
// very workload a budget exists to bound — an agent fanning out; and its
// refusal is already a typed value carrying the dimension, the quota,
// the spend and the period. So per-agent budgets are that seam under the
// domain `mcp-agent`, with 451's `AgentInitiated` class label, and there
// is nothing here for a future 9s to conflict with: a token budget is a
// different domain on the same ledger.
//
// The task text also names "the existing `RateLimit` partitioner", which
// a plain grep for `RateLimit` does not obviously find. It exists under
// a name the shard did not use: `RateLimitPolicy.partitionFor` already
// maps `Subject.ClaimBearer` to `token:<TokenId>` — and a validated
// agent credential resolves to exactly that subject — while Phase 56's
// `IRateLimitStore.IncrementAndCheck` is the atomic counting substrate
// behind it. So a per-agent rate limit is that partition, that store,
// and nothing else.
//
// **Both degrade to unrestricted, and both fail OPEN.** A deployment
// that declares no budget touches no ledger and a deployment that
// composed no `IRateLimitStore` counts nothing (GP 13). And where a
// store IS composed but fails, the call is admitted: that is the
// shipped contract of both substrates, stated in `IBudgetLedger`'s own
// header ("a budget that fails closed is a budget an operator switches
// off after the first incident") and in `IRateLimitStore`'s ("degrades
// open rather than denying every request when the store is down"). A
// companion that inverted either would be a companion whose refusals an
// operator cannot trust, so the posture is inherited rather than
// re-decided.

/// The budget ledger on this request, if one is composed.
///
/// Resolved from DI rather than constructed, so a deployment running
/// compute budgets and agent budgets sees ONE ledger and one set of
/// contention characteristics. `McpServerApp.run` registers a
/// blob-backed default when a budget is declared and none is present —
/// see `McpCompose`.
let private budgetLedger (ctx: HttpContext) : IBudgetLedger option =
    match ctx.RequestServices.GetService typeof<IBudgetLedger> with
    | :? IBudgetLedger as ledger -> Some ledger
    | _ -> None

/// The inbound rate-limit store on this request, if one is composed.
let private rateLimitStore (ctx: HttpContext) : IRateLimitStore option =
    match ctx.RequestServices.GetService typeof<IRateLimitStore> with
    | :? IRateLimitStore as store -> Some store
    | _ -> None

/// The budget subject one agent's MCP traffic accrues to.
///
/// `ScopeId` is the AGENT's account id, not its owning team: the phase's
/// requirement is a per-AGENT ceiling, and an account-keyed row is what
/// makes one runaway agent unable to exhaust a sibling's allowance. The
/// GP 4 partition is not weakened by this — the account id is
/// server-assigned, unique within the deployment, and never reaches the
/// ledger from a caller-supplied value.
///
/// `ClassLabel` is 451's `agent` label, so an operator reading a ledger
/// row sees the same class vocabulary the compute budget uses.
let budgetSubject (agent: AgentPrincipal) : BudgetSubject =
    BudgetSubject.create
        McpHostConstants.BudgetDomain
        agent.AccountId
        (SubmitterClass.label SubmitterClass.AgentInitiated)

/// The ledger key for one agent in the period `now` falls in.
let budgetKey (budget: AgentBudget) (agent: AgentPrincipal) (now: DateTime) : BudgetLedgerKey =
    BudgetLedgerKey.ofSubject (budgetSubject agent) (BudgetPeriod.key budget.Period now)

/// The claims one tool call is measured against, in refusal order.
///
/// Concurrency first, allowance second — the same order Phase 451 chose
/// and for the same reason, which `BudgetPolicy.breach` documents: the
/// burst control refuses the hundredth simultaneous call before any of
/// them has cost anything, while the allowance's only remedy is to wait
/// for the period to roll. A refusal naming two problems invites fixing
/// the wrong one.
let budgetClaims (budget: AgentBudget) (usage: BudgetUsage) : BudgetClaim list = [
    BudgetClaim.create "concurrency" (decimal budget.MaxConcurrentCalls) (decimal usage.InFlight) 1M
    BudgetClaim.create "calls" budget.CallAllowance usage.Spent McpHostConstants.CallCost
]

/// A reservation held against an agent's budget, released when the call
/// settles.
///
/// `None` when nothing was reserved — an unrestricted budget, or a
/// deployment with no ledger — so the release path is one `Option.iter`
/// rather than a branch the caller can forget.
type BudgetReservation = {
    Ledger: IBudgetLedger
    Key: BudgetLedgerKey
}

/// Admit one tool call against the agent's budget, reserving a slot.
///
/// Returns the reservation to release when the call settles, or the
/// typed `BudgetDenial` the ceiling produced.
let admitBudget
    (ctx: HttpContext)
    (budget: AgentBudget)
    (agent: AgentPrincipal)
    : Async<Result<BudgetReservation option, McpError>> =
    async {
        if AgentBudget.isUnrestricted budget then
            // The fast path: no ledger is resolved, no row is read, no
            // reservation is held (GP 13).
            return Ok None
        else
            match budgetLedger ctx with
            | None ->
                // A budget was declared and nothing can count it. Admitting
                // is the only honest answer — refusing every call because a
                // ledger is absent would turn a composition mistake into an
                // outage — and `McpCompose.run` refuses this composition at
                // STARTUP precisely so the state is unreachable in a
                // deployment that started.
                return Ok None
            | Some ledger ->
                let subject = budgetSubject agent
                let periodKey = BudgetPeriod.key budget.Period DateTime.UtcNow
                let key = BudgetLedgerKey.ofSubject subject periodKey

                let decide (usage: BudgetUsage) =
                    BudgetPolicy.check subject periodKey (budgetClaims budget usage)

                match! ledger.Reserve(key, McpHostConstants.CallCost, decide) with
                | Error denial -> return Error(McpError.BudgetExhausted denial)
                | Ok _ -> return Ok(Some { Ledger = ledger; Key = key })
    }

/// Release a held reservation.
///
/// The cost adjustment is zero: the allowance counts CALLS, and one call
/// costs one call however long it took or however much the tool did. A
/// non-zero adjustment would be the shape a token- or spend-denominated
/// budget uses, and that budget is a different domain on the same
/// ledger.
let releaseBudget (reservation: BudgetReservation option) : Async<unit> = async {
    match reservation with
    | None -> return ()
    | Some held ->
        try
            do! held.Ledger.Release(held.Key, 0M)
        with _ ->
            // A failed release leaks one in-flight slot until the period
            // rolls. `IBudgetLedger`'s header states that cost and its
            // direction: a leaked slot refuses work, it never admits work
            // it should have refused. Swallowing here keeps a storage
            // blip out of a response whose tool already succeeded.
            return ()
}

/// The rate-limit partition for one agent.
///
/// `RateLimitPolicy.partitionFor` over the agent's `ClaimBearer` subject,
/// which yields `token:<TokenId>` — so the budget is per CREDENTIAL
/// rather than per account. That is the sharper axis for a pace limit: a
/// leaked token is throttled without throttling the account's other,
/// legitimate credentials, which is exactly the split Phase 21e's
/// per-token rate limit was introduced for.
///
/// The subject is the one the request's own scope resolution produced,
/// passed in rather than synthesised here: a partition computed from a
/// claim this module built would be a partition the rest of the pipeline
/// never agreed to, and the whole value of reusing `partitionFor` is
/// that it is the shipped function over the shipped value. The client IP
/// serves its anonymous branch and is never reached on this one.
let ratePartition (clientIp: string) (subject: Subject) : InboundRateLimitKey =
    InboundComposite(RateLimitPolicy.partitionFor clientIp subject)

/// Count one call against the agent's rate limit.
///
/// Admits when no limit is declared, when no store is composed, and when
/// the store fails — see the file header on the inherited fail-open
/// posture.
let admitRateLimit
    (ctx: HttpContext)
    (limit: AgentRateLimit option)
    (subject: Subject)
    : Async<Result<unit, McpError>> =
    async {
        match limit with
        | None -> return Ok()
        | Some declared ->
            match rateLimitStore ctx with
            | None -> return Ok()
            | Some store ->
                let clientIp =
                    match ctx.Connection.RemoteIpAddress with
                    | null -> "unknown"
                    | address -> address.ToString()

                let key = ratePartition clientIp subject

                try
                    match! store.IncrementAndCheck(key, declared.Window, declared.MaxCalls) with
                    | Ok(DenyWithError limited) -> return Error(McpError.RateLimited limited)
                    | Ok(AllowWithRemaining _) -> return Ok()
                    | Error _ -> return Ok()
                with _ ->
                    return Ok()
    }