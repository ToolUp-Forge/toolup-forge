# Budgets — one shape for every resource ceiling

Resource-exhaustion defence in this SDK is a *budget*: a subject may spend so much of a resource in
a window, and something legible happens when it cannot. Several such mechanisms exist — compute
submissions, render cost, and (ahead) AI token and monetary spend — and before Phase 689 each had
invented its own vocabulary. This page is the shape they share, where each budget actually lives,
and how a refusal reaches an operator.

If you are wiring compute budgets specifically, read this page for the vocabulary and
[`external-compute.md`](external-compute.md) for the seam they decorate.

---

## The four parts

Every budget in the SDK is these four things, and the seam gives each one a name.

| Part | Type | What it answers |
|---|---|---|
| **declare** | `BudgetSubject`, `BudgetPeriod`, `BudgetClaim` | who is budgeted, over what window, against which ceilings |
| **check** | `BudgetPolicy` → `BudgetVerdict` | may this request proceed — and is it close to the edge |
| **account** | `BudgetAccount` | what a refusal or a threshold crossing records |
| **store** | `IBudgetLedger` | where consumption lives, and how a check and its reservation stay indivisible |

`BudgetPeriod` / `BudgetSubject` / `BudgetClaim` / `BudgetDenial` / `BudgetWarning` /
`BudgetVerdict` / `BudgetUsage` / `BudgetAccount` / `BudgetPolicy` live in `ToolUp.Platform.Core`
(`Shared/Budget.fs`) and are Fable-safe, so a client renders a refusal without pulling the server
tier in. `IBudgetLedger`, its blob layout and the two shipped ledgers are `ToolUp.Platform.Server`.

### One ceiling is a claim, and one predicate decides them all

A `BudgetClaim` is a ceiling plus both halves of the measurement against it:

```fsharp skip=fragment
{ Dimension = "tokens-per-hour"; Ceiling = 100_000M; Spent = 99_000M; Requested = 2_000M }
```

and the whole check is `Spent + Requested > Ceiling`. That one predicate covers ceilings that look
unrelated:

| Budget | Dimension | Ceiling | Spent | Requested |
|---|---|---|---|---|
| compute concurrency | `concurrency` | max in flight | runs in flight | `1` |
| compute run duration | `run-duration` | cap, seconds | `0` | declared, seconds |
| compute allowance | `period-allowance` | units per period | consumed | this run's cost |
| AI tokens | `tokens-per-hour` | cap | tokens used | estimate |
| AI spend | `spend` | ceiling | spent | estimated cost |

Two rules follow from the shape rather than from a convention:

- **A ceiling of `<= 0` is unrestricted**, on every dimension. The *absent* budget and the *empty*
  budget are therefore the same value: an unconfigured scope is unrestricted by construction rather
  than by a missing branch (GP 11), and a deployment that composes nothing pays nothing (GP 13).
- **A request landing exactly ON the ceiling is admitted; the next is refused.** An allowance of 100
  units admits the run that takes consumption to 100, which is what "an allowance of 100" means.

Quantities are `decimal` and abstract — cost units, tokens, runs, seconds. Never a currency: a
monetary budget denominates its own units and says so in its dimension label, and the SDK core
carries no pricing vocabulary or rounding policy (GP 1).

### The period is a storage key, not a counter to reset

`BudgetPeriod` is `Perpetual | Hourly | Daily | Monthly`, and `BudgetPeriod.key` renders the window
`now` falls in — `"perpetual"`, `"2026-08-05T14"`, `"2026-08-05"`, `"2026-08"`. That key is part of
the ledger's identity, so **a period reset is free and cannot fail to run**: the next period is a
different key that does not exist yet, and a key that does not exist reads as zero consumption.
There is no reset job, so there is no reset job to be missed — the failure mode where a control
silently becomes an outage.

Keys are always computed in **UTC**. A boundary that moved with the server's timezone would put two
replicas of one deployment in different periods for an hour twice a year.

### Refusals and warnings are data

A refused request produces a `BudgetDenial` naming **which** ceiling, for **which** class, in
**which** period, with `Spent` and `Requested` kept apart so a caller can tell "you are already
over" from "this one request would take you over" — different remedies, and often the recipient is
an agent deciding whether to narrow its search or wait. When several ceilings are breached at once
the **first one listed** is reported, deliberately: a refusal naming three problems invites fixing
the wrong one.

An *admitted* request that pushes consumption past the warning threshold
(`BudgetPolicy.defaultWarnThreshold`, `0.8`) produces a `BudgetWarning` instead — a leading
indicator, reported **once per subject per period** so it does not decay into a line an operator
filters out. `BudgetVerdict` carries all three answers:

```fsharp skip=fragment
match BudgetPolicy.verdict subject periodKey BudgetPolicy.defaultWarnThreshold claims with
| BudgetVerdict.Allowed -> // proceed
| BudgetVerdict.NearLimit warning -> // proceed, and record
| BudgetVerdict.Refused denial -> // refuse, and record
```

`BudgetAccount` is what "record" means — a record of two functions rather than an interface, because
accounting is deployment policy (one domain writes a typed audit event, another emits a metric) and
because expressing it as data means composing one is a `let`. `BudgetAccount.silent` is the explicit
opt-out; an allowed verdict on an unthreatened budget records nothing at all.

### The ledger makes a check and its reservation one operation

`IBudgetLedger.Reserve` takes the pure decision **in** rather than exposing the row and trusting the
caller:

```fsharp skip=fragment
abstract Reserve:
    key: BudgetLedgerKey * cost: decimal * decide: (BudgetUsage -> Result<unit, BudgetDenial>) ->
        Async<Result<BudgetUsage, BudgetDenial>>
```

The concurrency this closes is the one a budget exists to bound: N requests arriving at once.
Read-then-decide-then-write admits all N, because every one of them reads the same pre-burst row and
concludes there is room — which is not a rare race but the *expected* behaviour of an agent fanning
out.

Two ledgers ship. `BlobBudgetLedger` serialises writers per key with a semaphore and, when the
composed `IBlobStorage` also implements `IConditionalBlobStorage`, makes the write an **ETag
compare-and-swap** that extends the guarantee across replicas. On a backend with no ETag support you
get the in-process tier only, so with N replicas a concurrency ceiling binds per replica and the
effective ceiling is up to N× the configured one — a real weakening, deliberately not resolved by
taking a distributed lock (that would put every request behind a round-trip to tighten a bound whose
job is to be approximately right about an already-approximate resource). The remedy is a conditional
blob backend, which every cloud one is. `InMemoryBudgetLedger` is genuinely atomic and genuinely
non-durable — for tests and single-node development.

**A ledger fails open.** A storage error reads as no consumption and admits. The failure direction a
budget may have is admitting work it should have refused; the direction it may **not** have is
turning a transient storage blip into a deployment-wide refusal, because a budget that fails closed
is a budget an operator switches off after the first incident — which leaves them with no budget at
all.

The ledger holds the **counter**, never the ceilings. Where a domain's budget is *configured* is the
domain's own question — compute keeps it in a blob beside the usage row; a token budget will read it
from per-team config — and a seam that owned both would force every domain to move its policy
storage to satisfy an interface.

---

## Where each budget lives

| Budget | Ceilings | Enforced at | On the seam? |
|---|---|---|---|
| **Compute** (`ComputeBudget`) | concurrency, run duration, period allowance | `BudgetedComputeDispatcher` over `IExternalComputeDispatcher`, **and** the fit-job enqueue path | yes — its decision is `BudgetPolicy.check`, its counter is `IBudgetLedger` |
| **Hosted-tree render cost** (`HostRenderBudget`) | max nodes, max depth, render time | in-band on the client render path, plus a CI fixture gate | no, by decision — see below |
| **Peer cascade** (`PeerCascadePolicy`) | hops remaining, route length, identifier length | receiver-side, on every inbound peer call | no, by decision — see below |
| **AI tokens** (ahead) | per-user / per-team, per window | the AI pre-call gate | intended — see the phase note |
| **AI monetary spend** (ahead) | currency ceiling per window | the same pre-call gate | intended — see the phase note |

### Compute budgets

Off by default: `ServerConfig.ComputeBudget = NoComputeBudget` registers nothing, wraps nothing, and
consults nothing, so an existing deployment is byte-for-byte what it was (GP 11 + GP 13).
`EnabledComputeBudget` registers the blob-backed store, builds the shared guard, and wraps the
composed dispatcher.

Per-scope ceilings are stored under `_platform` at `compute-budget/<scope>/budget.json`, with
consumption at `compute-budget/<scope>/usage/<period>.json` — the ledger's own layout, with
`"compute-budget"` as the domain segment. A submission declares a `SubmitterClass`
(`Human` / `Scheduled` / `AgentInitiated`) and a class with its own entry is governed **entirely**
by that entry, never by a merge with the default: an operator writing "agents get 10 units" is not
also silently granting them the default concurrency.

Compose the memo **outside** the budget —
`MemoizedComputeDispatcher(BudgetedComputeDispatcher(backend))` — so a cache hit spends no allowance
and holds no concurrency slot.

### The two budgets that stay in band, and why

**Render-cost budgets are not moved onto the seam, and that is a decision rather than an omission.**
`HostRenderBudget` measures one tree in one render, on the client, against limits the consumer
declared in code. It has no scope, no period, no accumulation and no store — three of the seam's four
parts are empty, and the fourth (`BudgetClaim`'s predicate) it would satisfy only by inventing a
subject and a period key for a measurement that has neither. Routing it through a ledger would add a
Server-tier dependency to a Client-tier measurement that is deliberately synchronous and free, and
would make a per-render check pay for state it does not have. The seam's vocabulary is the honest
overlap and the two are cited in each other's docs; the machinery is not.

The **receiver-side cascade budget** is the same finding from the other direction. Its ceilings are
per-call shape bounds derived from the validated principal — hops remaining, route length,
identifier length — and its whole value is that they are re-derived server-side on *every* inbound
call rather than accumulated anywhere. There is nothing for a ledger to hold, and a period key would
be meaningless for a bound that resets per request.

Both remain useful as **conformance instances**: a proposed change to the seam that either of them
could not express in principle is a change worth re-reading, because the seam's job is to be the
shape every ceiling in the SDK has.

---

## How refusals surface

1. **To the caller, as a typed value.** A budget refusal is a `BudgetDenial` in the error channel,
   never an exception, and — on the compute path — a `ComputeBudgetDenial` projected onto the
   dispatcher's `ExternalComputeError` as a **terminal, non-retriable** failure. Terminal is the
   point: re-submitting an identical over-budget request cannot succeed, because the allowance does
   not refill on the timescale a retry loop runs at, so a caller that retried would turn one refusal
   into a hot loop against a budget that is by definition already exhausted.
2. **To an operator, as audit rows.** The compute path records `ComputeBudgetDenied` (carrying the
   typed denial verbatim, plus which surface refused and what was submitted) and
   `ComputeBudgetWarning` (once per scope per period). Both are ordinary `AuditEvent` cases, so they
   reach every composed audit sink and are queryable per scope.
3. **To a log, once.** A denial writes one warn-level line naming the scope, the dimension, the two
   numbers and the period. It is a supplement to the audit row, never the record.
4. **To metering, on settle.** A settled run records its actual cost through `IUsageLog` alongside
   the period key, so spend reconciles against the same accounting substrate as everything else. A
   *refused* run meters nothing — nothing was consumed.

A request refused by a budget **never reaches the inner substrate**: the payload does not leave the
process, and no backend is asked to start work the deployment cannot pay for. A check performed
after the backend accepted the work is a check on something that has already left.

---

## See also

- [`external-compute.md`](external-compute.md) — `IExternalComputeDispatcher`, the seam compute
  budgets decorate, and the submit / poll / cancel handle model a reservation is settled against.
- [`model-fit-worker-contract.md`](model-fit-worker-contract.md) — the second enforcement point: a
  federated peer's fit submission never touches `Submit`, so budgeting only the dispatcher would
  leave it ungoverned.
- [`portability-rules.md`](portability-rules.md) — the six rules `IBudgetLedger` is audited against
  in its file header.
- [`events.md`](events.md) — the audit substrate the budget rows ride.
