// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open ToolUp.Platform

// ─── Phase 675 — declassification budgets at the grounding tier ──────
//
// Phase 562 made a declassification routine *data*: a catalog entry whose
// output is disclosable regardless of tainted inputs, because the
// operation is an approved information-losing transform. Phase 674 made
// clearance per-party. Neither counts. A routine that is safe to cross
// once is a routine a counterparty may cross a thousand times, and the
// taint walk — a pure function of the derivation graph in front of it —
// cannot notice, because every single crossing was permitted. That is the
// same gap the federation tier had before Phase 190, and it has the same
// answer: a cumulative ledger beside the per-query verdict.
//
// **Reuse, not rebuild.** The value types, the `IPrivacyBudgetLedger`
// seam, the two ledger implementations and the blob-backed CAS store are
// Phase 190's, consumed from `ToolUp.Platform` where Phase 675 moved
// them; nothing here re-implements any of it. What this file adds is the
// grounding tier's own vocabulary — a budget declared per routine,
// accounted per contributing party — and the binding into
// `FactDisclosureGate`.
//
// ── What this DOES guarantee, stated narrowly ──
//
//   * A declared ceiling per (routine, contributing party, epoch) is
//     enforced. Once spend reaches the ceiling every further disclosure
//     whose derivation crosses that routine is DENIED — a typed,
//     audited refusal on exactly the path a policy denial takes, so a
//     handler has no say in it (GP 6).
//   * The accounting is ATOMIC, because it is the Phase 190 ledger's:
//     reservations go through a conditional (compare-and-swap) write, so
//     N concurrent disclosures against one remaining unit admit exactly
//     one.
//   * The reservation is taken BEFORE the disclosure verdict and settled
//     after. A disclosure cannot leave on credit, and a check that
//     produced no disclosure does not silently erode a budget nobody
//     spent.
//   * One party's budget is never spent by another's crossing. The
//     charge follows `TaintCrossing.AcceptedScopes` — the parties that
//     accepted this routine as a declassifier of their OWN data — which
//     is the same set Phase 674's conjunction cleared.
//
// ── What this does NOT guarantee, stated plainly ──
//
// **This is an ACCOUNTING control, not a differential-privacy
// guarantee**, and the distinction is load-bearing rather than
// pedantic. ε-differential privacy is a property of a RANDOMISED
// mechanism: an answer is ε-DP because calibrated noise was added to it.
// A declassification routine as Phase 562 defines it is a DETERMINISTIC
// operation — an aggregation over k, an approved index formula — and
// summing charges over deterministic answers bounds nothing formally.
// What it bounds is **how many questions were asked** under a declared
// schedule, and that is why a deterministic routine's budget is spelled
// `CountedCrossings` here and its unit is a crossing, not an ε. Calling
// that number ε would be false, so this file does not let you.
//
// ε becomes chargeable exactly when something randomises: a routine that
// names the `INoiseMechanism` (Phase 481) it draws calibrated noise from
// may declare `ChargedEpsilon`, and a routine that names none may not.
// **That is refused at REGISTRATION** (`DeclassificationBudgetConfig`
// validates before a config exists), not documented and hoped for — a
// rule enforced only in prose is a rule a deployment discovers it broke
// after the fact, if at all.
//
// Three further limits, all deliberate, all inherited unchanged from the
// federation tier's reading of the same mechanism:
//
//   * **Composition is BASIC (sequential).** Charges add: a series of
//     crossings costing ε₁…εₙ is accounted at Σεᵢ (Dwork & Roth, *The
//     Algorithmic Foundations of Differential Privacy*, Theorem 3.16).
//     No advanced composition is offered — the √(2n ln(1/δ)) saving
//     needs (ε, δ) accounting and randomised mechanisms throughout, and
//     a tighter bound derived from assumptions the deployment does not
//     meet is worse than a loose one.
//   * **Collusion is out of scope.** Budgets are keyed per contributing
//     party, because two parties are two adversaries — but two parties
//     that share answers are one adversary with two budgets. Bounding
//     that needs a shared budget the composition declares, which is a
//     policy judgement about who colludes and not something a neutral
//     mechanism can infer (GP 1).
//   * **A refilling epoch is a weakening, chosen knowingly.**
//     `PerpetualBudget` is the only setting under which the ceiling is
//     the total lifetime disclosure through a routine; `DailyBudget` /
//     `MonthlyBudget` let a patient counterparty spend it again.
//
// ── Cost when unused (GP 13) ──
//
// Nothing here is reachable unless a composition calls
// `FactsCompose.withDeclassificationBudgets`. No config registered ⇒ the
// gate holds `None`, takes one option match on a path that already
// matched an option, reads no ledger and allocates nothing — the
// disclosure verdict, the event count and every audit payload are
// byte-for-byte the Phase 674 gate's (GP 11).

/// What a declassification routine's budget accounts — and, which is the
/// entire reason this is a discriminated union rather than two numbers,
/// **which claim the number supports**.
type DeclassificationCharge =
    /// Questions asked: one unit per crossing, against `ceiling`
    /// crossings per contributing party per epoch.
    ///
    /// The only admissible charge for a DETERMINISTIC routine, and the
    /// honest one — a count of crossings is exactly what it is. See the
    /// file header for why the alternative spelling would be a false
    /// statement rather than a loose one.
    | CountedCrossings of ceiling: int
    /// Calibrated privacy loss: `perCrossing` ε against `ceiling` ε per
    /// contributing party per epoch.
    ///
    /// Admissible ONLY on a routine that names the `INoiseMechanism` it
    /// draws from — `DeclassificationBudgetConfig` refuses the
    /// combination at registration, so a deployment cannot boot carrying
    /// an ε it is not spending.
    | ChargedEpsilon of ceiling: decimal * perCrossing: decimal

/// The budget declared for one declassification routine.
///
/// **Every value here is deployment policy, not SDK opinion (GP 1).**
/// What a crossing is worth depends on the data, the regulator and the
/// agreement with the contributing party; the SDK ships the mechanism
/// that enforces whatever is declared and holds no view on the numbers.
type DeclassificationBudget = {
    /// The `Computed` operation id — the same key
    /// `DeclassificationRoutine.OperationId` carries, so a budget names a
    /// routine the catalog already declares. An id no routine declares is
    /// inert: no crossing can ever name it, so it budgets nothing. (The
    /// posture `DeclassificationRoutine.AcceptingScopes` already takes for
    /// a party no policy declares.)
    OperationId: string
    /// What is charged, and against what ceiling.
    Charge: DeclassificationCharge
    /// The Phase 481 `INoiseMechanism` this routine draws calibrated
    /// noise from, named for the audit trail. `None` ⇒ the routine is
    /// deterministic ⇒ `ChargedEpsilon` is refused at registration.
    ///
    /// A *name*, not the mechanism: the ledger accounts, it does not
    /// sample, and naming the instance here would put a live handle in a
    /// value that has to travel and be audited (GP 12 rule 1).
    NoiseMechanism: string option
    /// How often the budget refills. `PerpetualBudget` is the only
    /// setting under which the ceiling bounds lifetime disclosure.
    Epoch: BudgetEpoch
    /// Whether a DENIED disclosure is charged.
    ///
    /// `WithholdCharged` (the default) is the strict reading and the one
    /// that makes the budget worth having: a denial is not silence — it
    /// discloses that a party's restriction survived — and a caller that
    /// can vary its query and read that bit for free has an oracle.
    WithholdCharge: WithholdCharge
    /// How long an open reservation is honoured before the ledger
    /// reclaims it. The reservation is written before the verdict is
    /// finalised and settled after; a process that dies in between would
    /// otherwise strand its charge forever.
    ReservationTtl: TimeSpan
}

[<RequireQualifiedAccess>]
module DeclassificationBudget =

    /// The party a budget is accounted against when the routine cleared
    /// no *scoped* policy — a Phase 562 deployment that declares no
    /// contributor scope at all, or a crossing over unscoped policies.
    ///
    /// A real bucket rather than "skip the accounting": a single-party
    /// deployment that declares a budget means it, and dropping the
    /// charge because nobody is named would silently make the ceiling
    /// unenforceable in exactly the commonest deployment.
    [<Literal>]
    let UnscopedParty = "_unscoped"

    /// The `BudgetScope.TemplateId` a routine's budget is keyed under.
    /// Prefixed so a grounding-tier budget and a federation-tier
    /// clean-room template can never collide on a shared ledger — they
    /// are different questions about different surfaces and must not
    /// share a document.
    [<Literal>]
    let ScopePrefix = "declassify:"

    /// The `FactNotDisclosable` policy ref for a disclosure refused
    /// because a routine's budget is spent. Stable and greppable; it
    /// names the ROUTINE, never a quantity — a caller able to read back
    /// its own remaining budget while varying its query has an oracle
    /// beside the one the taint walk already refuses it. The quantity is
    /// recorded server-side by the ledger and nowhere else.
    [<Literal>]
    let ExhaustedPrefix = "declassification-budget-exhausted:"

    /// The `FactNotDisclosable` policy ref for a disclosure refused
    /// because the ledger could not ACCOUNT for the crossing — storage
    /// unreachable, contention unresolved, stored state unreadable.
    ///
    /// A distinct ref from `ExhaustedPrefix` deliberately: "you have
    /// spent your allowance" and "I cannot tell whether you have" have
    /// different remedies and only one of them is the caller's. Both
    /// deny — **fail-closed**; unavailable is never "allow".
    [<Literal>]
    let UnaccountablePrefix = "declassification-budget-unaccountable:"

    /// A crossing-COUNT budget for a deterministic routine: `ceiling`
    /// crossings per contributing party per epoch, perpetual, charging
    /// denials. The strict defaults; loosen deliberately with `withEpoch`
    /// / `withWithholdCharge`.
    let countedCrossings (operationId: string) (ceiling: int) : DeclassificationBudget = {
        OperationId = operationId
        Charge = CountedCrossings ceiling
        NoiseMechanism = None
        Epoch = PerpetualBudget
        WithholdCharge = WithholdCharged
        ReservationTtl = TimeSpan.FromMinutes PrivacyBudgetPolicy.DefaultReservationTtlMinutes
    }

    /// An ε budget for a routine that draws calibrated noise from the
    /// named `INoiseMechanism`. The mechanism name is REQUIRED by this
    /// signature — the one construction that can produce a chargeable ε
    /// cannot be reached without naming what randomises.
    let chargedEpsilon
        (operationId: string)
        (noiseMechanism: string)
        (ceiling: decimal)
        (perCrossing: decimal)
        : DeclassificationBudget =
        {
            OperationId = operationId
            Charge = ChargedEpsilon(ceiling, perCrossing)
            NoiseMechanism = Some noiseMechanism
            Epoch = PerpetualBudget
            WithholdCharge = WithholdCharged
            ReservationTtl = TimeSpan.FromMinutes PrivacyBudgetPolicy.DefaultReservationTtlMinutes
        }

    /// Refill the budget on the given cadence. A weakening — see
    /// `DeclassificationBudget.Epoch`.
    let withEpoch (epoch: BudgetEpoch) (budget: DeclassificationBudget) = { budget with Epoch = epoch }

    /// Decide whether a denied disclosure is charged. `WithholdFree`
    /// re-opens the free-probe channel — see `WithholdCharge`.
    let withWithholdCharge (charge: WithholdCharge) (budget: DeclassificationBudget) = {
        budget with
            WithholdCharge = charge
    }

    /// Override how long an open reservation is honoured.
    let withReservationTtl (ttl: TimeSpan) (budget: DeclassificationBudget) = { budget with ReservationTtl = ttl }

    /// Every way a budget declaration is unenforceable, as data rather
    /// than an exception (GP 12 rule 3). Empty on a healthy declaration.
    ///
    /// The first case is 675.A's whole point: **ε > 0 on a routine that
    /// names no noise mechanism is refused**, because charging ε for a
    /// deterministic transform and calling the sum a privacy loss is the
    /// one thing an ε budget must never be asked to do.
    let validate (budget: DeclassificationBudget) : string list = [
        if String.IsNullOrWhiteSpace budget.OperationId then
            "a declassification budget must name the operation id of the routine it governs"

        match budget.Charge, budget.NoiseMechanism with
        | ChargedEpsilon _, None ->
            $"the declassification routine '{budget.OperationId}' declares a chargeable epsilon but names no INoiseMechanism. Epsilon-differential privacy is a property of a RANDOMISED mechanism, so a deterministic routine cannot spend epsilon and summing charges over its crossings would bound nothing: declare CountedCrossings (a count of questions asked, which is what the number would really be), or name the noise mechanism the routine draws from"
        | ChargedEpsilon(ceiling, _), Some _ when ceiling <= 0m ->
            $"the declassification routine '{budget.OperationId}' declares an epsilon ceiling of {ceiling}; a ceiling at or below zero admits nothing and is a sealed routine expressed by accident — remove the routine from the catalog instead"
        | ChargedEpsilon(_, perCrossing), Some _ when perCrossing <= 0m ->
            $"the declassification routine '{budget.OperationId}' charges {perCrossing} epsilon per crossing; a charge at or below zero accumulates nothing, so the ceiling can never be reached and the budget is not a control"
        | CountedCrossings ceiling, _ when ceiling <= 0 ->
            $"the declassification routine '{budget.OperationId}' declares a crossing ceiling of {ceiling}; a ceiling at or below zero admits nothing and is a sealed routine expressed by accident — remove the routine from the catalog instead"
        | _ -> ()

        if budget.ReservationTtl <= TimeSpan.Zero then
            $"the declassification routine '{budget.OperationId}' declares a reservation TTL of {budget.ReservationTtl}; a reservation that expires before it is settled is reclaimed on the spot and the accounting admits everything"
    ]

    /// The Phase 190 accounting policy this declaration reduces to.
    ///
    /// The reduction is the reuse: a crossing count is accounted by the
    /// identical ledger arithmetic an ε is, charging one unit per
    /// crossing against a ceiling of `n` units. What differs is what the
    /// number MEANS, and that is carried by `DeclassificationCharge` and
    /// by the audit refs — never by a second accounting path.
    let policyFor (budget: DeclassificationBudget) : PrivacyBudgetPolicy =
        let ceiling, perCrossing =
            match budget.Charge with
            | CountedCrossings ceiling -> decimal ceiling, 1m
            | ChargedEpsilon(ceiling, perCrossing) -> ceiling, perCrossing

        {
            EpsilonCeiling = ceiling
            EpsilonPerQuery = perCrossing
            MethodEpsilon = Map.empty
            Epoch = budget.Epoch
            WithholdCharge = budget.WithholdCharge
            ReservationTtl = budget.ReservationTtl
        }

    /// The ceiling this budget is read against.
    let ceilingOf (budget: DeclassificationBudget) : decimal = (policyFor budget).EpsilonCeiling

    /// The scope one contributing party's accounting against this routine
    /// is keyed under.
    let scopeFor (budget: DeclassificationBudget) (party: string) (now: DateTimeOffset) : BudgetScope =
        PrivacyBudgetPolicy.scopeFor (ScopePrefix + budget.OperationId) party now (policyFor budget)

    /// The reservation one crossing by one contributing party opens.
    /// Pure — the caller hands it to `IPrivacyBudgetLedger.ReserveBudget`.
    let spendFor (budget: DeclassificationBudget) (party: string) (now: DateTimeOffset) : BudgetSpend =
        let policy = policyFor budget

        {
            ReservationId = Guid.NewGuid().ToString "N"
            Scope = PrivacyBudgetPolicy.scopeFor (ScopePrefix + budget.OperationId) party now policy
            Epsilon = policy.EpsilonPerQuery
            MethodName = budget.OperationId
            OccurredAt = now
            ExpiresAt = now.Add policy.ReservationTtl
        }

    /// The parties a crossing is charged to: the contributing parties
    /// that accepted this routine (Phase 674), or the single unscoped
    /// bucket when the crossing cleared no party-scoped policy.
    let chargedParties (acceptedScopes: string list) : string list =
        match acceptedScopes |> List.distinct |> List.sort with
        | [] -> [ UnscopedParty ]
        | parties -> parties

/// One open reservation, paired with the declaration it was taken under
/// so settlement can read that routine's own `WithholdCharge` without a
/// second lookup.
type DeclassificationHold = {
    Spend: BudgetSpend
    Budget: DeclassificationBudget
}

/// The registered budget catalog + the ledger it accounts through.
///
/// A record rather than three parameters because it travels together
/// into the gate, and because a composition that swaps its ledger without
/// restating its budgets has almost certainly made a mistake — the same
/// argument `PrivacyBudgetMeter` makes at the federation tier.
type DeclassificationBudgetConfig = {
    Ledger: IPrivacyBudgetLedger
    /// Keyed by `DeclassificationRoutine.OperationId`.
    Budgets: Map<string, DeclassificationBudget>
    /// Injected so an epoch boundary and a reservation TTL are testable
    /// without waiting for one. Production passes
    /// `DateTimeOffset.UtcNow`.
    Now: unit -> DateTimeOffset
}

[<RequireQualifiedAccess>]
module DeclassificationBudgetConfig =

    /// Build the config, or every reason it is unenforceable.
    ///
    /// **This is the registration gate of 675.A.** Validation happens
    /// here, before a config value exists, so there is no order in which
    /// a deployment holds a config carrying an ε it cannot justify. A
    /// duplicate operation id is refused rather than last-wins: two
    /// budgets for one routine is a declaration whose meaning nobody can
    /// state, and silently keeping one of them states it wrongly.
    let tryCreate
        (ledger: IPrivacyBudgetLedger)
        (budgets: DeclassificationBudget list)
        : Result<DeclassificationBudgetConfig, string list> =
        let duplicates =
            budgets
            |> List.countBy _.OperationId
            |> List.filter (fun (_, n) -> n > 1)
            |> List.map (fun (operationId, n) ->
                $"the declassification routine '{operationId}' has {n} budget declarations; one routine has one budget, and picking one of two silently would enforce a ceiling nobody declared")

        match (budgets |> List.collect DeclassificationBudget.validate) @ duplicates with
        | [] ->
            Ok {
                Ledger = ledger
                Budgets = budgets |> List.map (fun b -> b.OperationId, b) |> Map.ofList
                Now = fun () -> DateTimeOffset.UtcNow
            }
        | errors -> Error errors

    /// `tryCreate`, raising on an unenforceable declaration.
    ///
    /// Loud and at compose time rather than at the first disclosure —
    /// the posture `BlobPrivacyBudgetLedger` takes for a non-conditional
    /// backend, and for the same reason: a privacy control that fails
    /// late fails after something has already been disclosed.
    let create (ledger: IPrivacyBudgetLedger) (budgets: DeclassificationBudget list) : DeclassificationBudgetConfig =
        match tryCreate ledger budgets with
        | Ok config -> config
        | Error errors -> invalidArg "budgets" (String.Join("; ", errors))

    /// Account on an injected clock — for a test, or a deployment whose
    /// epoch boundaries follow something other than UTC wall time.
    let withClock (now: unit -> DateTimeOffset) (config: DeclassificationBudgetConfig) = { config with Now = now }

    /// Nothing declared ⇒ the gate skips the whole path.
    let isEmpty (config: DeclassificationBudgetConfig) : bool = config.Budgets.IsEmpty

    /// The budget declared for an operation id, when any.
    let budgetFor (config: DeclassificationBudgetConfig) (operationId: string) : DeclassificationBudget option =
        config.Budgets.TryFind operationId

/// The outcome of asking the ledger to afford a disclosure's crossings.
type DeclassificationBudgetOutcome =
    /// Every budgeted crossing is affordable and its charge is HELD. The
    /// caller MUST settle every hold once the verdict is known.
    | CrossingsHeld of held: DeclassificationHold list
    /// A routine refused. `policyRef` is the typed deny ref the verdict
    /// carries; `held` is what had already been taken and must still be
    /// settled — a refusal part-way through leaves earlier reservations
    /// open, and leaking them would erode budgets nobody spent.
    | CrossingsRefused of policyRef: string * held: DeclassificationHold list

/// The reserve / settle binding the gate calls. Separated from
/// `FactDisclosureGate` so the gate stays one readable pass over the
/// verdict and the accounting is testable on its own.
[<RequireQualifiedAccess>]
module DeclassificationBudgetGate =

    /// Reserve every budgeted crossing on a disclosure's derivation path,
    /// per contributing party. Crossings whose routine declares no budget
    /// are free and untouched (GP 11).
    ///
    /// Fail-closed on both refusal shapes: an exhausted ceiling and an
    /// unreadable ledger both DENY, because a ledger that cannot account
    /// for a disclosure has not established that the disclosure is
    /// affordable, and releasing one it could not account for is exactly
    /// the free crossing the budget exists to prevent.
    let reserve
        (config: DeclassificationBudgetConfig)
        (crossings: TaintCrossing list)
        : Async<DeclassificationBudgetOutcome> =
        async {
            let now = config.Now()
            let mutable held: DeclassificationHold list = []
            let mutable refusal: string option = None
            let mutable remaining = crossings

            while refusal.IsNone && not (List.isEmpty remaining) do
                let crossing = List.head remaining
                remaining <- List.tail remaining

                match DeclassificationBudgetConfig.budgetFor config crossing.OperationId with
                | None -> ()
                | Some budget ->
                    let ceiling = DeclassificationBudget.ceilingOf budget
                    let mutable parties = DeclassificationBudget.chargedParties crossing.AcceptedScopes

                    while refusal.IsNone && not (List.isEmpty parties) do
                        let party = List.head parties
                        parties <- List.tail parties

                        let spend = DeclassificationBudget.spendFor budget party now
                        let! decision = config.Ledger.ReserveBudget(spend, ceiling)

                        match decision with
                        | BudgetReserved(reserved, _) -> held <- { Spend = reserved; Budget = budget } :: held
                        | BudgetRefused(BudgetExhausted _) ->
                            refusal <- Some(DeclassificationBudget.ExhaustedPrefix + crossing.OperationId)
                        | BudgetRefused(BudgetLedgerUnavailable _) ->
                            refusal <- Some(DeclassificationBudget.UnaccountablePrefix + crossing.OperationId)

            return
                match refusal with
                | Some policyRef -> CrossingsRefused(policyRef, List.rev held)
                | None -> CrossingsHeld(List.rev held)
        }

    /// Settle every hold once the verdict is known.
    ///
    /// `disclosed` commits unconditionally: the data left. A denial is
    /// settled per that routine's own declared `WithholdCharge`, because
    /// whether a refusal costs anything is deployment policy and not an
    /// SDK opinion — see `DeclassificationBudget.WithholdCharge` for why
    /// the strict default is the one worth having.
    let settle
        (config: DeclassificationBudgetConfig)
        (disclosed: bool)
        (held: DeclassificationHold list)
        : Async<unit> =
        async {
            for hold in held do
                let outcome =
                    if disclosed then
                        SpendCommitted
                    else
                        match hold.Budget.WithholdCharge with
                        | WithholdCharged -> SpendCommitted
                        | WithholdFree ->
                            SpendReturned
                                "the disclosure was denied and this routine's budget charges only disclosures that were released"

                do! config.Ledger.RecordSpend(hold.Spend, outcome)
        }