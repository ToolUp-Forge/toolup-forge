// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
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
// ── Phase 679 — the ceiling moves only by countersigned amendment ──
//
// A ceiling declared above is a term of an agreement, and it is the term
// most often asked to move mid-engagement — the analysis needed one more
// crossing than anyone estimated. Everything above makes exhaustion
// enforced; nothing above says how it may be relieved, and the practical
// answer in the absence of a mechanism is that somebody edits the
// declaration and redeploys. That is a change to what a deployment may
// disclose, made by one party alone, which is precisely the act the
// clean-room's D2 principle forbids.
//
// So the raise itself is a countersigned subject (Phase 676's
// `BudgetAmendment`, declared beside the registry) and the ceiling in
// force is DERIVED rather than stored:
//
//   effective ceiling = declared ceiling, folded through every declared
//   amendment for that (routine, party) whose countersignature is
//   live-complete at the moment of the crossing.
//
// Four consequences follow from the construction rather than from
// policy, and each is why this is not a stored flag or a config edit:
//
//   * **An amendment applies only WHILE its countersignature is live.**
//     The status is evaluated per crossing, so a revocation takes effect
//     on the next disclosure — there is nothing to remember to undo,
//     because nothing was written down.
//   * **The signature covers the exact delta.** `BudgetAmendment`'s hash
//     spans the routine, the party, the baseline and the change, so an
//     approval of "+500 against 500" is worth nothing for "+5000", and
//     nothing at all for a budget whose ceiling has since moved.
//   * **A chain composes in exactly one order.** Each amendment names
//     the ceiling it was agreed against, so applying one is a
//     compare-and-set: an amendment whose baseline has moved is INERT
//     and named, never silently re-based onto a number its signatories
//     never saw.
//   * **A retroactive breach is unrepresentable.** A lowering amendment
//     is refused at application time when the ceiling it proposes is
//     below spend the ledger has already recorded. The alternative —
//     admitting it — would make a deployment that had spent 400 of 500
//     instantly, and retroactively, in breach of a 300 ceiling for
//     disclosures that were permitted when they happened.
//
// **Application is audited, and the audit is the effective history.**
// Every application (and every refused retroactive lowering) writes a
// row carrying the subject hash, the roster, and the ceiling before and
// after, so the ceiling in force at any past instant is reconstructable
// from the trail plus the declared budget — no second store, and no
// mutable ceiling anywhere to disagree with it.
//
// ── Cost when unused (GP 13) ──
//
// Nothing here is reachable unless a composition calls
// `FactsCompose.withDeclassificationBudgets`. No config registered ⇒ the
// gate holds `None`, takes one option match on a path that already
// matched an option, reads no ledger and allocates nothing — the
// disclosure verdict, the event count and every audit payload are
// byte-for-byte the Phase 674 gate's (GP 11).
//
// The amendment facet is a second, narrower opt-in inside that one: a
// budget config declaring no amendments consults no registry, reads no
// ledger reading, writes no audit row and reserves against the declared
// ceiling — byte-for-byte the Phase 675 path (GP 11 / GP 13). So is a
// crossing whose (routine, party) pair has no declared amendment, which
// is the common case in a deployment that has amended one budget.

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

    /// The `BudgetScope.TemplateId` for one routine. Exposed (Phase 679)
    /// because an amendment is keyed on the template id, and a caller
    /// that concatenated the prefix itself would be one typo away from
    /// amending a budget nothing accounts under — silently, since an
    /// amendment naming an unknown template is simply never applied.
    let templateIdFor (operationId: string) : string = ScopePrefix + operationId

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

// ─── Phase 679 — countersigned amendment, at the grounding tier ──────

/// What one declared amendment did when the ceiling was resolved.
///
/// A DU rather than a boolean-plus-message because the three ways an
/// amendment fails to apply have three different remedies, and only one
/// of them is a defect: an un-countersigned amendment is waiting for a
/// party, a moved baseline needs a fresh agreement, and a lowering below
/// recorded spend is the refusal that keeps a retroactive breach
/// unrepresentable.
type DeclassificationAmendmentOutcome =
    /// The roster's countersignature over this exact delta was live and
    /// complete, and the ceiling moved. `effectiveFrom` is the instant
    /// the agreement became complete — the latest of the parties'
    /// `NotBefore` instants, from `Countersigned`.
    | AmendmentApplied of effectiveFrom: DateTimeOffset
    /// No live, complete countersignature over this exact delta. Carries
    /// the registry's own verdict, so the row says who is missing (or who
    /// withdrew) rather than only that something is.
    ///
    /// **This is also the unreadable-registry case**, and deliberately
    /// so: `ICountersignatureRegistry.Status` collapses a store failure
    /// to pending, and the effect here is that the DECLARED ceiling
    /// stands. That is not failing open — it is falling back to the
    /// baseline every party already agreed to, which is the only number
    /// available that anyone signed.
    | AmendmentNotCountersigned of status: CountersignatureStatus
    /// The amendment names a baseline the budget has left — usually
    /// because an earlier amendment in the chain applied, or did not.
    /// Inert and named, never re-based: a delta re-based onto a
    /// different baseline is a change nobody signed.
    | AmendmentBaselineMoved of inForce: decimal
    /// The amendment would put the ceiling below spend the ledger has
    /// already recorded, retroactively declaring a breach for
    /// disclosures that were permitted when they happened. Refused.
    | AmendmentBelowRecordedSpend of proposed: decimal * recorded: decimal

/// One amendment's resolution against the ceiling in force. The unit the
/// audit trail records, and the unit a diagnostic surface renders.
type DeclassificationAmendmentResolution = {
    Amendment: BudgetAmendment
    /// `sha256:{hex}` over the exact bytes the roster countersigned —
    /// the join key between this row and the countersignature records.
    SubjectHash: string
    /// The canonical roster the amendment was evaluated against.
    Roster: string list
    Outcome: DeclassificationAmendmentOutcome
    /// The ceiling in force before this amendment was considered.
    CeilingBefore: decimal
    /// The ceiling in force after. Equal to `CeilingBefore` on every
    /// outcome but `AmendmentApplied`.
    CeilingAfter: decimal
    /// When the resolution was taken — the crossing's instant, not the
    /// agreement's.
    ResolvedAt: DateTimeOffset
}

/// The ceiling in force for one (routine, party), plus how it got there.
type DeclassificationCeiling = {
    /// What the reservation is taken against.
    Ceiling: decimal
    /// Every declared amendment for this pair, in chain order, with what
    /// each one did. Empty when none is declared.
    Resolutions: DeclassificationAmendmentResolution list
}

/// Reserved event-type discriminators for the amendment trail. They ride
/// the same `_facts` source module the disclosure trail does, so one
/// `IEventStore.ReadBySource scope FactEvents.SourceModule` recovers the
/// disclosures AND the ceilings they were judged against.
module DeclassificationBudgetEvents =

    /// A countersigned amendment moved a ceiling.
    [<Literal>]
    let AmendedType = "DeclassificationBudgetAmended"

    /// An amendment was refused because it would have put the ceiling
    /// below spend already recorded.
    [<Literal>]
    let AmendmentRefusedType = "DeclassificationBudgetAmendmentRefused"

    /// The stable wire name of an outcome. Written out rather than
    /// derived from the DU case name, so renaming a case in F# source
    /// cannot silently change what a persisted audit row says.
    let outcomeName (outcome: DeclassificationAmendmentOutcome) : string =
        match outcome with
        | AmendmentApplied _ -> "Applied"
        | AmendmentNotCountersigned _ -> "NotCountersigned"
        | AmendmentBaselineMoved _ -> "BaselineMoved"
        | AmendmentBelowRecordedSpend _ -> "BelowRecordedSpend"

/// Payload of an amendment audit event (JSON-serialised into
/// `ModuleEvent.Payload`). PII-free: identifiers, a digest and
/// quantities the deployment itself declared.
///
/// **`CeilingBefore` / `CeilingAfter` are the whole point.** A trail of
/// applications carrying both is enough to replay the effective ceiling
/// at any past instant from the declared budget alone, which is what
/// makes the derived ceiling auditable without a second store to
/// disagree with it.
type DeclassificationAmendmentEvent = {
    /// The budget template — `declassify:{operationId}`.
    TemplateId: string
    /// The party whose allowance moved.
    PartyId: string
    /// The countersigned subject hash.
    SubjectHash: string
    /// The roster that agreed it.
    Roster: string list
    /// The stable outcome name — `DeclassificationBudgetEvents.outcomeName`.
    Outcome: string
    CeilingBefore: decimal
    CeilingAfter: decimal
    /// The delta the parties signed over. Recorded beside the ceilings
    /// because a refused amendment moves neither, and the trail would
    /// otherwise not say what was refused.
    CeilingDelta: decimal
    OccurredAt: DateTimeOffset
}

/// Where an amendment resolution is recorded.
///
/// A seam rather than a hard-wired `IEventStore` write for one reason
/// that is not symmetry: an amendment is a governance act between the
/// parties to an agreement, not a per-request act by a tenant, so the
/// scope it is filed under is a composition decision. Binding it to
/// whichever request happened to trigger the crossing would scatter one
/// agreement's history across every tenant that touched it.
///
/// Async at the boundary and stateless between calls (GP 12 rules 2 + 4).
type IDeclassificationAmendmentAudit =
    /// Record one resolution. Called only for outcomes that CHANGED
    /// something or REFUSED something — an amendment merely awaiting a
    /// signature is the default state of a declared amendment, and a row
    /// per crossing saying so would bury the two that matter.
    abstract Record: resolution: DeclassificationAmendmentResolution -> Async<unit>

/// The shipped default: one `IEventStore` row per resolution, under the
/// reserved `_facts` source module and the composition-declared scope.
type EventStoreAmendmentAudit(events: IEventStore, scopeId: string) =

    static let jsonOptions = FableConverters.create ()

    interface IDeclassificationAmendmentAudit with
        member _.Record(resolution: DeclassificationAmendmentResolution) = async {
            let payload: DeclassificationAmendmentEvent = {
                TemplateId = resolution.Amendment.TemplateId
                PartyId = resolution.Amendment.PartyId
                SubjectHash = resolution.SubjectHash
                Roster = resolution.Roster
                Outcome = DeclassificationBudgetEvents.outcomeName resolution.Outcome
                CeilingBefore = resolution.CeilingBefore
                CeilingAfter = resolution.CeilingAfter
                CeilingDelta = resolution.Amendment.CeilingDelta
                OccurredAt = resolution.ResolvedAt
            }

            let eventType =
                match resolution.Outcome with
                | AmendmentApplied _ -> DeclassificationBudgetEvents.AmendedType
                | _ -> DeclassificationBudgetEvents.AmendmentRefusedType

            // Best-effort, matching the disclosure trail: an audit-write
            // failure must never turn a permitted disclosure into an
            // exception on the answer path.
            try
                do!
                    events.Write {
                        Id = Guid.NewGuid()
                        OccurredAt = DateTime.UtcNow
                        ScopeId = scopeId
                        SourceModule = FactEvents.SourceModule
                        EventType = eventType
                        Payload = JsonSerializer.Serialize(payload, jsonOptions)
                    }
            with _ ->
                ()
        }

/// The declared amendments to a deployment's declassification budgets,
/// plus the registry that says which of them the parties have agreed.
///
/// The amendments are DECLARED here rather than discovered from the
/// registry, and that is not redundancy. A countersignature record
/// carries only the subject hash, which is opaque by construction — the
/// registry can say "this roster agreed these bytes" and can never say
/// what the bytes meant. So the deployment states the deltas it holds,
/// and the registry decides which of them are in force. Neither half can
/// move a ceiling alone.
type DeclassificationAmendmentConfig = {
    /// The registry the roster's agreement is read from. **Clock-skew
    /// tolerance is the registry's**, declared where it is constructed —
    /// this config deliberately holds no second opinion about how far
    /// apart two clocks may be.
    Registry: ICountersignatureRegistry
    /// The parties every amendment here is agreed under. Signed into
    /// each record and part of the evaluation key, so adding a party
    /// re-opens approval rather than inheriting it (Phase 676).
    Roster: string list
    /// The declared amendments, in the order the chain folds.
    Amendments: BudgetAmendment list
    /// Where applications and retroactive-lowering refusals are
    /// recorded. `None` leaves the ceiling derived and the trail silent
    /// — legal, and a deliberate choice a deployment makes rather than a
    /// default it falls into.
    Audit: IDeclassificationAmendmentAudit option
}

[<RequireQualifiedAccess>]
module DeclassificationAmendmentConfig =

    /// Build the config, or every reason it is unenforceable.
    ///
    /// An EMPTY roster is refused rather than accepted-and-inert. Phase
    /// 676 evaluates an empty roster as pending — "everyone has agreed"
    /// must not be satisfiable by there being no one — so an empty roster
    /// here would produce a config whose every amendment silently never
    /// applies, which reads at the composition site exactly like one that
    /// works.
    ///
    /// Two amendments with the same signed bytes are likewise refused: a
    /// duplicate cannot chain (the second always finds the baseline
    /// moved) and its presence suggests an author expected the delta to
    /// apply twice.
    let tryCreate
        (registry: ICountersignatureRegistry)
        (roster: string list)
        (amendments: BudgetAmendment list)
        : Result<DeclassificationAmendmentConfig, string list> =

        let enrolled = Countersignature.roster roster

        let rosterErrors = [
            if List.isEmpty enrolled then
                "a budget-amendment roster names no parties; an empty roster is never countersigned, so every amendment declared under it would be silently inert"
        ]

        let duplicates =
            amendments
            |> List.countBy (fun a -> BudgetAmendment.subject a)
            |> List.filter (fun (_, n) -> n > 1)
            |> List.map (fun (subject, n) ->
                $"the budget amendment {subject.ContentHash} is declared {n} times; a duplicate can never chain, because the second copy always finds the baseline it names already moved")

        match
            (amendments |> List.collect BudgetAmendment.validate)
            @ rosterErrors
            @ duplicates
        with
        | [] ->
            Ok {
                Registry = registry
                Roster = enrolled
                Amendments = amendments
                Audit = None
            }
        | errors -> Error errors

    /// `tryCreate`, raising on an unenforceable declaration. Loud at
    /// compose time rather than at the first crossing — the posture the
    /// budget declaration itself takes, for the same reason.
    let create
        (registry: ICountersignatureRegistry)
        (roster: string list)
        (amendments: BudgetAmendment list)
        : DeclassificationAmendmentConfig =
        match tryCreate registry roster amendments with
        | Ok config -> config
        | Error errors -> invalidArg "amendments" (String.Join("; ", errors))

    /// Record applications and retroactive-lowering refusals through the
    /// given sink.
    let withAudit (audit: IDeclassificationAmendmentAudit) (config: DeclassificationAmendmentConfig) = {
        config with
            Audit = Some audit
    }

    /// The declared amendments for one (template, party) pair, in chain
    /// order.
    let chainFor (templateId: string) (party: string) (config: DeclassificationAmendmentConfig) =
        config.Amendments
        |> List.filter (fun a -> a.TemplateId = templateId && a.PartyId = party)

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
    /// Phase 679 — the countersigned amendments that may move a declared
    /// ceiling. `None` (the default, and what every pre-679 composition
    /// produces) leaves every ceiling exactly as declared: no registry
    /// is consulted, no ledger reading taken, no audit row written
    /// (GP 11 / GP 13).
    Amendments: DeclassificationAmendmentConfig option
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
                Amendments = None
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

    /// Phase 679 — arm the amendment facet: declared ceilings may be
    /// moved by an amendment the roster has countersigned over the exact
    /// delta. Refuses at compose time on an amendment declaring nothing
    /// enforceable (`DeclassificationAmendmentConfig.create`).
    ///
    /// An amendment naming a routine no budget declares is inert rather
    /// than refused — the same posture a budget naming a routine no
    /// catalog declares takes, and for the same reason: nothing can ever
    /// cross it, so it enforces and relieves nothing.
    let withAmendments
        (amendments: DeclassificationAmendmentConfig)
        (config: DeclassificationBudgetConfig)
        : DeclassificationBudgetConfig =
        {
            config with
                Amendments = Some amendments
        }

/// Phase 679 — folding a declared ceiling through its countersigned
/// amendments.
///
/// Pure with respect to the ceiling: nothing is stored, so the answer is
/// a function of the declaration, the registry's records and the ledger's
/// current reading at the instant of the crossing. A revocation
/// therefore takes effect on the next disclosure with nothing to undo.
[<RequireQualifiedAccess>]
module DeclassificationAmendments =

    /// Spend the ledger has already recorded against one scope:
    /// committed charges plus reservations that are open and unexpired.
    ///
    /// Open reservations count. A hold is a crossing in flight, and a
    /// ceiling lowered underneath one would either strand it or admit a
    /// disclosure past the new ceiling — both worse than refusing the
    /// lowering until the hold settles or expires.
    let private recordedSpend (config: DeclassificationBudgetConfig) (scope: BudgetScope) (ceiling: decimal) = async {
        let! reading = config.Ledger.RemainingBudget(scope, ceiling)
        return reading.EpsilonCommitted + reading.EpsilonReserved
    }

    /// The ceiling in force for one (routine, party) at `now`, and how it
    /// got there.
    ///
    /// The chain folds in declaration order and each step is a
    /// compare-and-set against the running ceiling, so the result is a
    /// deterministic function of the declaration — an amendment cannot
    /// apply "sometimes" depending on evaluation order.
    let resolve
        (config: DeclassificationBudgetConfig)
        (budget: DeclassificationBudget)
        (party: string)
        (now: DateTimeOffset)
        : Async<DeclassificationCeiling> =
        async {
            let declared = DeclassificationBudget.ceilingOf budget

            match config.Amendments with
            // No amendment facet composed ⇒ the declared ceiling, with
            // no registry read and no ledger reading (GP 11 / GP 13).
            | None -> return { Ceiling = declared; Resolutions = [] }
            | Some amendments ->
                let templateId = DeclassificationBudget.templateIdFor budget.OperationId

                let chain = DeclassificationAmendmentConfig.chainFor templateId party amendments

                // No amendment declared for THIS pair ⇒ the same
                // zero-cost path. The common case in a deployment that
                // has amended one budget.
                if List.isEmpty chain then
                    return { Ceiling = declared; Resolutions = [] }
                else
                    let scope = DeclassificationBudget.scopeFor budget party now
                    let mutable running = declared
                    let mutable resolved: DeclassificationAmendmentResolution list = []
                    let mutable remaining = chain

                    while not (List.isEmpty remaining) do
                        let amendment = List.head remaining
                        remaining <- List.tail remaining

                        let subject = BudgetAmendment.subject amendment
                        let! status = amendments.Registry.Status(amendments.Roster, subject, now)

                        let! outcome =
                            match status with
                            | Countersigned effectiveFrom when amendment.PriorCeiling = running ->
                                if amendment.CeilingDelta > 0m then
                                    // A RAISE can never fall below spend
                                    // already recorded: the ledger refuses
                                    // a reservation that would carry spend
                                    // past the ceiling, so recorded spend
                                    // is bounded by the ceiling in force
                                    // and a strictly higher one bounds it
                                    // too. No reading is taken — which is
                                    // also why the commonest amendment
                                    // costs one registry read and nothing
                                    // else.
                                    async.Return(AmendmentApplied effectiveFrom)
                                else
                                    async {
                                        let! recorded = recordedSpend config scope running
                                        let proposed = BudgetAmendment.amendedCeiling amendment

                                        return
                                            if proposed < recorded then
                                                AmendmentBelowRecordedSpend(proposed, recorded)
                                            else
                                                AmendmentApplied effectiveFrom
                                    }
                            // Agreed, but against a baseline the budget
                            // has left. Inert and named — a delta re-based
                            // onto a different baseline is a change nobody
                            // signed.
                            | Countersigned _ -> async.Return(AmendmentBaselineMoved running)
                            | incomplete -> async.Return(AmendmentNotCountersigned incomplete)

                        let after =
                            match outcome with
                            | AmendmentApplied _ -> BudgetAmendment.amendedCeiling amendment
                            | _ -> running

                        resolved <-
                            {
                                Amendment = amendment
                                SubjectHash = subject.ContentHash
                                Roster = amendments.Roster
                                Outcome = outcome
                                CeilingBefore = running
                                CeilingAfter = after
                                ResolvedAt = now
                            }
                            :: resolved

                        running <- after

                    let resolutions = List.rev resolved

                    match amendments.Audit with
                    | None -> ()
                    | Some audit ->
                        for resolution in resolutions do
                            match resolution.Outcome with
                            | AmendmentApplied _
                            | AmendmentBelowRecordedSpend _ -> do! audit.Record resolution
                            // An amendment merely awaiting a signature,
                            // or naming a baseline that has moved, is the
                            // resting state of a declared amendment. A
                            // row per crossing saying so would bury the
                            // two outcomes that changed something.
                            | AmendmentNotCountersigned _
                            | AmendmentBaselineMoved _ -> ()

                    return {
                        Ceiling = running
                        Resolutions = resolutions
                    }
        }

    /// `resolve`, keeping only the number the reservation is taken
    /// against.
    let effectiveCeiling
        (config: DeclassificationBudgetConfig)
        (budget: DeclassificationBudget)
        (party: string)
        (now: DateTimeOffset)
        : Async<decimal> =
        async {
            let! resolution = resolve config budget party now
            return resolution.Ceiling
        }

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
                    let mutable parties = DeclassificationBudget.chargedParties crossing.AcceptedScopes

                    while refusal.IsNone && not (List.isEmpty parties) do
                        let party = List.head parties
                        parties <- List.tail parties

                        // Phase 679 — the ceiling in force for THIS
                        // party, which is the declared one folded
                        // through every amendment the roster has
                        // countersigned and that still chains. Resolved
                        // per party because an amendment names a party:
                        // raising one party's allowance must never raise
                        // another's. With no amendment facet composed
                        // this is `DeclassificationBudget.ceilingOf` and
                        // one option match.
                        let! ceiling = DeclassificationAmendments.effectiveCeiling config budget party now

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