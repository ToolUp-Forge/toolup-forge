# Phase 675 — declassification budgets, and the privacy-budget ledger's new home

Two changes ship together. The first affects every consumer of the federation companion
whether or not they want budgets; the second is opt-in and costs nothing until it is
composed.

| | What changed | Who is affected |
|---|---|---|
| 1 | `IPrivacyBudgetLedger` and its value types **moved** from `ToolUp.InterPlatform` to `ToolUp.Platform` (package `ToolUp.Platform.Server`) | anyone naming a budget type or pattern-matching its cases |
| 2 | `FactsCompose.withDeclassificationBudgets` — a cumulative budget per declassification routine | opt-in; no change unless composed |

---

## 1. The privacy-budget ledger moved

### What moved

`src/InterPlatform/Server/PrivacyBudgetLedger.fs` → `src/ToolUp.Platform.Server/Server/PrivacyBudgetLedger.fs`,
`namespace ToolUp.InterPlatform` → `namespace ToolUp.Platform`. Moved, not forked and not
reimplemented: `BudgetEpoch`, `BudgetScope`, `WithholdCharge`, `PrivacyBudgetPolicy`,
`BudgetSpend`, `PrivacyBudget`, `BudgetRefusal`, `BudgetDecision`, `SpendOutcome`,
`IPrivacyBudgetLedger`, `NoPrivacyBudgetLedger`, `InMemoryPrivacyBudgetLedger`,
`BlobPrivacyBudgetLedger`, `PrivacyBudgetMeter` and the `PrivacyBudgetPolicy` /
`PrivacyBudgetMeter` modules are the same code at a new address.

One member stayed behind, because its return type did:
`PrivacyBudgetMeter.refusalDecision` — which renders a `BudgetRefusal` into the clean-room
gate's `GateDecision` — is now **`PeerPrivacyBudget.refusalDecision`**, still in
`ToolUp.InterPlatform`. The rename is not cosmetic: a module named `PrivacyBudgetMeter` in
that namespace would shadow the moved one for every file opening both, silently making
`PrivacyBudgetMeter.spendFor` unresolvable.

### Why

Phase 675 needs the same cumulative accounting at the grounding tier. The alternatives were
a `ToolUp.Facts.Server → ToolUp.InterPlatform` package edge (a companion depending on
another companion, against GP 1) or a second ledger mirrored Facts-side. The second is the
worse of the two: two ledgers that drift are two different answers to "has this counterparty
spent its allowance", and only one can be right. The seam therefore sits at the layer both
tiers already depend on.

### What you need to do

**In most cases, nothing.** Every in-repo call site already carried `open ToolUp.Platform`,
and so does almost every realistic consumer of the federation companion — `ServerApp`,
`IBlobStorage` and `ISecretStore` all live there.

If a file of yours opens **only** `ToolUp.InterPlatform`, add one line:

```fsharp
open ToolUp.InterPlatform
open ToolUp.Platform          // <- add this
```

**Why an `open` and not a re-export.** `ToolUp.InterPlatform` keeps type abbreviations for
every moved type, so `ToolUp.InterPlatform.BudgetScope` still resolves and an annotation or
a qualified reference compiles unchanged. That is the limit of what F# can do here: a type
abbreviation re-exports the type *name* and neither the union **cases** nor the companion
**modules**. So this still needs the `open`:

```fsharp
match decision with
| BudgetReserved(spend, remaining) -> ...     // a union case
| BudgetRefused refusal -> ...
let policy = PrivacyBudgetPolicy.create 10m 0.5m   // a module function
```

### Verification

```bash
dotnet build ToolUp.Forge.sln
```

A missing `open` surfaces as `FS0039: The value or constructor 'BudgetReserved' is not
defined` (or the same for `PrivacyBudgetPolicy` / `PrivacyBudgetMeter`), at compile time, at
the call site. There is no silent behaviour change to look for: the accounting arithmetic,
the on-disk document layout, the blob container and the `cleanroom/budget/` prefix are all
unchanged, so an existing deployment reads back the spend it has already accounted.

### Rollback

Revert the phase commit. Nothing persists a type name, so no data migration is involved in
either direction.

---

## 2. Declassification budgets (opt-in)

### What it is

Phase 562 made a declassification routine *data*: a catalog entry whose
output is disclosable regardless of tainted inputs, because the operation is an approved
information-losing transform. Phase 674 made clearance per contributing party. Neither
**counts** — and a routine that is safe to cross once is a routine a counterparty may cross
a thousand times, which the taint walk cannot notice because every single crossing was
permitted.

A budget declared for a routine is reserved before the disclosure verdict and settled after,
per contributing party. An exhausted ceiling denies with the same typed, audited refusal
shape a policy denial takes.

### The honesty framing — read this before you declare an ε

**This is an accounting control, not a differential-privacy guarantee.** The four points
carried from the federation tier's reading of the same mechanism, because they decide
whether the knob is the control you think it is:

- **The accounting bounds *questions asked*, not information disclosed.** ε-differential
  privacy is a property of a RANDOMISED mechanism: an answer is ε-DP because calibrated
  noise was added to it. A declassification routine is a DETERMINISTIC transform, so summing
  charges over its crossings bounds nothing formally. What it bounds is how many times the
  routine was crossed under a declared schedule, and it makes exhaustion enforced and
  auditable rather than discovered afterwards.
- **Only a noise-drawing routine earns a DP claim.** ε is chargeable exactly where a routine
  names the `INoiseMechanism` (Phase 481) it draws from. A routine that names none may
  declare a crossing **count** and nothing else — and `DeclassificationBudgetConfig` refuses
  the other combination **at registration**, so a deployment cannot boot carrying an ε it is
  not spending. That is why the two are different constructors and different union cases
  rather than one number with a comment.
- **Composition is basic (sequential).** Charges add: a series of crossings costing ε₁…εₙ is
  accounted at Σεᵢ (Dwork & Roth, *The Algorithmic Foundations of Differential Privacy*,
  Theorem 3.16). No advanced composition is offered — the √(2n ln(1/δ)) saving needs (ε, δ)
  accounting and randomised mechanisms throughout, and a tighter bound derived from
  assumptions the deployment does not meet is worse than a loose one.
- **Collusion is out of scope.** Budgets are keyed per contributing party, because two
  parties are two adversaries — but two parties that share answers are one adversary with
  two budgets. Bounding that needs a shared budget the composition declares, which is a
  policy judgement about who colludes and not something a neutral mechanism can infer
  (GP 1).

One further limit, inherited unchanged: a **refilling epoch is a weakening**.
`PerpetualBudget` is the only setting under which the ceiling bounds lifetime disclosure
through a routine.

### Composing it

```fsharp
open ToolUp.Platform
open ToolUp.Facts

ServerApp.empty
|> ServerApp.withStorage blob
|> FactsCompose.withFactStore
|> FactsCompose.withDeclassificationBudgets
       (BlobPrivacyBudgetLedger blob)
       [
         // A deterministic routine: 500 crossings per contributing party
         // per epoch. The number is a count of questions asked.
         DeclassificationBudget.countedCrossings "aggregate-over-k" 500

         // A routine that draws calibrated noise MAY charge epsilon. The
         // mechanism name is required by this signature: the only
         // construction that produces a chargeable epsilon cannot be
         // reached without naming what randomises.
         DeclassificationBudget.chargedEpsilon "noised-revenue" "discrete-laplace" 10m 0.5m
         |> DeclassificationBudget.withEpoch MonthlyBudget
       ]
|> ServerApp.run
```

Insert **after** `withFactStore` — it arms a facet on the gate that registered.

The ledger is the Phase 190 one: `BlobPrivacyBudgetLedger` for a distributed-ready
deployment (it **requires** an `IBlobStorage` that also implements
`IConditionalBlobStorage`, and refuses one that does not, loudly, at construction);
`InMemoryPrivacyBudgetLedger` for a single-instance receiver; `NoPrivacyBudgetLedger` for a
composition that wants the seam present and the accounting off.

### What is refused, and when

`withDeclassificationBudgets` validates and **raises at compose**, not at the first
disclosure — a privacy control that fails late fails after something has already been
disclosed. It refuses:

| Declaration | Why |
|---|---|
| `ChargedEpsilon` with no `NoiseMechanism` | a deterministic routine cannot spend ε; declare `CountedCrossings` |
| a ceiling ≤ 0 | admits nothing — a sealed routine expressed by accident; remove the routine instead |
| a per-crossing charge ≤ 0 | accumulates nothing, so the ceiling can never bind |
| two budgets for one operation id | picking one silently would enforce a ceiling nobody declared |
| a reservation TTL ≤ 0 | the hold is reclaimed on the spot and the accounting admits everything |

`DeclassificationBudgetConfig.tryCreate` returns the same findings as data when you would
rather surface them yourself.

An operation id that names **no registered routine** is inert rather than refused: no
crossing can ever name it, so it budgets nothing. (The posture
`DeclassificationRoutine.AcceptingScopes` already takes for a party no policy declares.)

### What a refusal looks like

The verdict is an ordinary `FactNotDisclosable`, and the audit row is the ordinary
`FactDisclosureDenied` — same event type, same reserved `_facts` source module, same payload
shape, so an existing per-party projection picks budget refusals up with no change. Two
policy refs are minted:

| Ref | Meaning |
|---|---|
| `declassification-budget-exhausted:<operationId>` | the ceiling is spent |
| `declassification-budget-unaccountable:<operationId>` | the ledger could not decide — storage unreachable, contention unresolved, stored state unreadable |

Both deny. **Fail-closed**: a ledger that cannot account for a disclosure has not established
that the disclosure is affordable, and releasing one it could not account for is exactly the
free crossing the budget exists to prevent. They are distinct refs deliberately — "you have
spent your allowance" and "I cannot tell whether you have" have different remedies, and only
one of them is the caller's problem.

Neither ref carries a **quantity**. A caller able to read back "remaining 0.4" while varying
its query has an oracle beside the one the taint walk already refuses it; the numbers are
recorded server-side by the ledger and read through `IPrivacyBudgetLedger.RemainingBudget`.

### Cost when unused

Nothing is reachable unless `withDeclassificationBudgets` is called. No config registered ⇒
the gate holds `None`, reads no ledger, opens no reservation, and every verdict, event count
and audit payload is byte-for-byte the Phase 674 gate's (GP 11 / GP 13). A config that
declares no routine is treated as absent for the same reason, and a budget for a routine the
derivation never crosses changes nothing.

### Verification

```bash
dotnet run --project Build.fsproj -- VerifyAll
```

The pack is `Phase 675 declassification budgets` in `ToolUp.Platform.Tests` — accumulation
against **both** shipped ledger implementations, the audited ceiling breach, per-party
independence, the registration refusals, and the byte-identical no-budget floor.

### Rollback

Remove the `withDeclassificationBudgets` line. The composition returns to its pre-675
behaviour immediately; accounting documents already written under `_platform` are simply no
longer read. Nothing else in the fact tier depends on them.
