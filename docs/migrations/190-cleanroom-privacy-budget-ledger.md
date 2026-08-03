# Migration — Phase 190: clean-room differential-privacy budget ledger

**Status:** additive and opt-in. No consumer action is required to upgrade. A composition that
never calls `PeerServerApp.withPrivacyBudget` reads no ledger, allocates nothing, and dispatches
through the identical closure it did before (GP 11 / GP 13).

## Read this first: what the ledger is, and what it is not

**This is an accounting control, not a differential-privacy guarantee.** ε-differential privacy is
a property of a *randomised* mechanism — an answer is ε-DP because calibrated noise was added to
it. The shipped `ICleanRoomBroker` adds no noise: it suppresses small cells and refuses small
cohorts, both deterministically. Charging ε for a deterministic answer and summing the charges
bounds nothing formally.

What it *does* bound is **how many questions a counterparty may ask under a declared schedule**,
with exhaustion enforced structurally and the remaining budget auditable. That is the control a
regulated clean-room buyer asks for, and it is the one worth shipping — but describing it to a
regulator as differential privacy would be false. A deployment that needs the formal guarantee
substitutes an `ICleanRoomBroker` that actually randomises its answers, at which point these ε
values become that mechanism's real privacy loss and this ledger becomes its accountant.

Composition is **basic (sequential) composition** — charges add (Dwork & Roth, *The Algorithmic
Foundations of Differential Privacy*, Theorem 3.16). No advanced composition is offered: the
√(2n ln(1/δ)) saving needs (ε, δ) accounting, a δ budget and randomised mechanisms, none of which
this substrate has.

## Why

Phase 18b's floor and Phase 311's structural gate both decide about **one** answer: this cohort is
at or above k, these cells are suppressed, this shape is permitted. Cohort floors do not compose —
differencing two in-floor cohorts that overlap in all but one record recovers that record, and no
per-query check can see it because *each query passed*. A counterparty that asks a hundred
individually-compliant questions gets a hundred compliant answers, and nothing notices.

Phase 190 adds the cumulative half.

## What is new

All new surface lives in `ToolUp.InterPlatform` (`src/InterPlatform/Server/PrivacyBudgetLedger.fs`,
a new file compiled between `CleanRoomBroker.fs` and `CleanRoomGate.fs`).

| Surface | Shape |
|---|---|
| `BudgetScope` | `{ TemplateId; CounterpartyPeerId; Epoch }` — what a budget is keyed by |
| `BudgetEpoch` | `PerpetualBudget \| DailyBudget \| MonthlyBudget` |
| `WithholdCharge` | `WithholdCharged` (default) \| `WithholdFree` |
| `PrivacyBudgetPolicy` | ceiling + per-query ε + per-method overrides + epoch + withhold charge + reservation TTL |
| `BudgetSpend` | one reservation / settled charge, identified by `ReservationId` |
| `PrivacyBudget` | the auditable reading: ceiling, committed, reserved, query count |
| `BudgetDecision` / `BudgetRefusal` | `BudgetReserved` \| `BudgetRefused (BudgetExhausted \| BudgetLedgerUnavailable)` |
| `SpendOutcome` | `SpendCommitted` \| `SpendReturned of reason` |
| `IPrivacyBudgetLedger` | `ReserveBudget` / `RecordSpend` / `RemainingBudget` / `IsDurable` |
| `NoPrivacyBudgetLedger` | no-op — the seam present, the accounting off |
| `InMemoryPrivacyBudgetLedger` | process-local reference impl (`IsDurable = false`) |
| `BlobPrivacyBudgetLedger` | `IConditionalBlobStorage`-backed, compare-and-swap (`IsDurable = true`) |
| `PrivacyBudgetMeter` | ledger + policy + clock, as composed |
| `CleanRoomGate.wrapMetered` | the gate wrapper, now taking a `PrivacyBudgetMeter option` |
| `PeerServerApp.withPrivacyBudget` | the compose-time opt-in |

## Opting in

```fsharp
app
|> PeerServerApp.withContract (JsonRpcPeerHost.contract<IReachApi> "reach" [ v1 ])
|> PeerServerApp.withCleanRoomTemplate "reach" reachTemplate
|> PeerServerApp.withPrivacyBudget (
       PrivacyBudgetMeter.create
           (BlobPrivacyBudgetLedger blobs)
           (PrivacyBudgetPolicy.create 50m 1m
            |> PrivacyBudgetPolicy.withMethodEpsilon "Histogram" 4m))
```

One meter serves every gated contract — the per-template axis is already in the scope key. The
ledger arrives built rather than resolved from DI, on the same argument
`withTemplateApprovals` takes its registry: which storage backs it is the deployment's call, and
`BlobPrivacyBudgetLedger` refuses a backend without conditional writes **at construction**, which
an operator should see when they wire it rather than at the first peer call.

**Verification steps for an adopting consumer:**

1. Compose the meter, start the deployment — a `BlobPrivacyBudgetLedger` over a backend without
   `IConditionalBlobStorage` throws `ArgumentException` here, naming the remedy.
2. Run `EpsilonCeiling / EpsilonPerQuery` in-floor queries from one counterparty; every one
   releases.
3. Run one more; it comes back `PeerCleanRoomWithheld` carrying only the template id.
4. Read `ledger.RemainingBudget(scope, ceiling)` — `EpsilonCommitted` equals the ceiling and
   `QueryCount` equals the number of answers that shipped.
5. Check the receiver-side audit trail: the refusal is a `PeerCleanRoomDecision` row with
   `Released = false` and a `Reason` naming the exhausted budget. The quantities live there and
   never on the wire.

## Where the debit sits, and why

Two-phase, and both phases are load-bearing:

- **`ReserveBudget` runs as invariant 0.5** — after Phase 480's bilateral approval, *before* the
  handler is dispatched. So no answer reaches the wire on credit: a release that was never debited
  is a free query, and a crash between "released" and "debited" would mint one.
- **`RecordSpend` settles after the outcome is known** — so the mirror failure is closed too. A
  dispatch that errored, or answered in a shape the gate could not check, returns its ε rather
  than eroding a budget nobody spent.
- **A withheld answer is charged by default.** A withhold discloses one bit ("the cohort was below
  the floor"), so a counterparty that reads it for free has a counting oracle — binary search over
  a cohort size at zero ε. `PrivacyBudgetPolicy.withWithholdCharge WithholdFree` opts out; it is a
  real weakening.
- **A dispatch that throws leaves its reservation open on purpose.** The ledger's TTL reclaim
  returns it (15 minutes by default, against a 100 s peer call deadline). That direction never
  hands budget back for an answer that shipped.

**Atomicity is the whole claim.** `BlobPrivacyBudgetLedger` takes every reservation through a
compare-and-swap against the stored document's etag, so N concurrent queries against a shared
remainder admit exactly the ceiling. A plain download-modify-upload has a race exactly wide enough
for two queries to read the same remaining budget and both spend it, which is the one thing a
budget ledger is for — hence the construction-time refusal of a non-conditional backend, the same
posture `BlobPeerReplayGuard` takes.

## Limits, stated so nobody has to discover them

- **Collusion is out of scope.** Two counterparties are two budgets; two counterparties that share
  answers are one adversary with two budgets. A shared budget is a policy judgement about who
  colludes, which a neutral mechanism cannot infer (GP 1).
- **The charge is the immediate caller's, not the cascade origin's.** A receiver validates the peer
  it is talking to; the rest of a `Route` is upstream assertion. An origin fanning one question
  through three intermediaries spends three budgets. Charging the origin would mean charging on an
  unvalidated field.
- **A refilling epoch is a weakening.** `PerpetualBudget` (the default) is the only setting under
  which the ceiling bounds lifetime disclosure.
- **`NoPrivacyBudgetLedger` is not a privacy control.** It exists so a composition can declare the
  seam without declaring a policy.

## What did NOT change

- `ICleanRoomBroker` and `DefaultCleanRoomBroker.Enforce` are untouched — same signature, same
  decisions, still pure and stateless. The ledger is a separate seam rather than a parameter of
  `Enforce`, because `Enforce` is synchronous and stateless by contract (GP 12 rules 2 and 4) and
  a durable ledger is neither; and because accounting inside a *substitutable* broker would be
  accounting a deployment can replace away, which is the argument Phase 311 already made about the
  floor.
- `CleanRoomGate.wrap` and `CleanRoomGate.wrapApproved` keep their exact signatures. Both are now
  defined in terms of `wrapMetered` so there is still one implementation of "the gate".
- `PeerServerApp.create ()` and every `with*` helper stay source-compatible; the new
  `PrivacyBudget` field defaults to `None`.
- `PeerSurface` does not project the composed budget. A `PeerSurface` is hash-stamped and pinned by
  counterparties, so an operator *tightening* a budget would invalidate every pinned copy — and
  the half a counterparty could act on (the live remainder) changes on every query and cannot live
  in a stamped descriptor at all. Advertising a budget properly is a `formatVersion` bump plus a
  live read path, which belongs to the surface's own phase.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — 0 failures.
  28 new cases in `PrivacyBudgetLedgerTests`, including: cumulative exhaustion through the gate
  paired with an unmetered control that releases the identical series; atomicity measured against
  a real conditional-blob backend under a forced barrier, paired with the *same ledger* over a
  backend whose conditional write ignores its precondition (which over-admits, so the green result
  is a measurement rather than an inference); reserve-before-release observed from inside the
  handler; the withhold-charge policy in both settings; refund on a failed dispatch; and the TTL
  reclaim of an abandoned reservation.

## Rollback

Every change is additive. Drop `PeerServerApp.withPrivacyBudget` from the composition and the gate
returns to its pre-190 behaviour with no code change; remove `PrivacyBudgetLedger.fs`, the
`PrivacyBudget` compose field and `CleanRoomGate.wrapMetered` (restoring `wrapApproved`'s body) to
remove the surface entirely. Stored budget documents live under `_platform` at
`cleanroom/budget/**` and are inert once no meter is composed.
