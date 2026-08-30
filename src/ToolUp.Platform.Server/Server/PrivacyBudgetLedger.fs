// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Platform.BlobStorage
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 190 — the cumulative privacy-budget ledger ────────────────
//
// **Phase 675 moved this file from the federation companion into the
// SDK server core, and moved nothing else.** It arrived serving one
// caller — the clean-room gate — and a second tier now needs the same
// accounting for the same reason, so it sits at the layer both can
// reach. The types, the seam, the two implementations and every
// paragraph of the honesty framing below are the Phase 190 ones,
// unchanged in substance; what changed is that the vocabulary is stated
// generically (a *gate*, a *counterparty*, a *query surface*) rather
// than in one tier's nouns. Nothing was reimplemented, because a second
// implementation of a privacy control is how a path that enforces
// slightly less appears.
//
// A per-query gate enforces a floor on ONE answer: this cohort is at or
// above k, these cells are suppressed, this shape is permitted. Such a
// gate is a pure function of the answer in front of it and holds nothing
// between calls, which is exactly right for what it does and is also its
// limit — a counterparty that asks a hundred individually-compliant
// questions gets a hundred compliant answers and the gate never notices.
// Cohort floors do not compose: differencing two in-floor cohorts that
// overlap in all but one record recovers that record, and no per-query
// check can see it, because each query passed.
//
// This file adds the CUMULATIVE half — the epsilon (ε) budget a
// regulated buyer audits. It is stateful and durable where a per-query
// gate is pure and stateless, and those are different jobs, which is why
// they are different seams rather than one interface doing both.
//
// ── What this DOES guarantee, stated narrowly ──
//
//   * A declared ε ceiling per (query surface, counterparty, epoch) is
//     enforced. Once spend reaches the ceiling every further answer under
//     that surface is WITHHELD — a refusal, not a warning, on the same
//     structural path the composing gate put its floor on, so a handler
//     has no say in it.
//   * The accounting is ATOMIC. Reservations are taken through a
//     conditional (compare-and-swap) write, so N concurrent queries
//     against one remaining unit admit exactly one. A ledger that
//     over-admits under load reads as defended and is not, which is the
//     failure a replay guard refuses a non-conditional backend to avoid,
//     and this file refuses one for the same reason.
//   * Spend is durable BEFORE release. The reservation is written before
//     the handler is dispatched, so an answer cannot reach the wire on
//     credit — a released answer that was never debited is a free query,
//     and a crash between release and debit would mint one.
//   * A reservation that does not turn into a release is RETURNED, so a
//     transport failure or an unparseable answer does not silently erode
//     a budget nobody spent.
//   * Remaining budget is readable (`RemainingBudget`) for the audit
//     trail the buyer actually asks for.
//
// ── What this does NOT guarantee, stated plainly ──
//
// **This is an ACCOUNTING control, not a differential-privacy
// guarantee.** ε-differential privacy is a property of a RANDOMISED
// mechanism: an answer is ε-DP because calibrated noise was added to it.
// A gate that suppresses small cells, refuses small cohorts, or applies
// an approved information-losing transform adds no noise — all three are
// deterministic. Charging ε for a deterministic answer and summing the
// charges bounds nothing formally; what it bounds is how many questions
// a counterparty may ask under a declared schedule, and it makes
// exhaustion enforced and auditable rather than discovered afterwards.
// Anyone who needs the formal guarantee must route the release through a
// mechanism that actually randomises its answers (`INoiseMechanism` is
// the shipped one), at which point these ε values become the mechanism's
// real privacy loss and this ledger becomes its accountant. Until then,
// calling the number "ε" is a naming convention borrowed from the
// accounting it resembles, and describing it to a regulator as
// differential privacy would be false.
//
// Three further limits, all deliberate, none of them defects to be fixed
// by tuning:
//
//   * **Collusion is out of scope.** Budgets are keyed per counterparty,
//     because two counterparties are two adversaries — but two
//     counterparties that share answers are one adversary with two
//     budgets. Bounding that needs a shared budget the composition
//     declares, which is a policy judgement about who colludes and not
//     something a neutral mechanism can infer (GP 1).
//   * **The charge is the immediate CALLER's, not the cascade origin's.**
//     A receiver validates the party it is talking to; the rest of a
//     route is upstream assertion. So an origin that fans one question
//     through three intermediaries spends three budgets. Charging the
//     origin would mean charging on an unvalidated field, which is worse.
//   * **A refilling epoch is a weakening, chosen knowingly.**
//     `PerpetualBudget` is the only setting under which the ceiling is
//     the total lifetime disclosure; `DailyBudget` / `MonthlyBudget`
//     let a patient counterparty spend it again, which is often what a
//     commercial agreement means and is never what a privacy proof
//     means.
//
// ── Composition ──
//
// Charges add: a series of queries costing ε₁…εₙ is accounted at Σεᵢ.
// That is **basic (sequential) composition** — the standard bound
// (Dwork & Roth, *The Algorithmic Foundations of Differential Privacy*,
// Theorem 3.16: the composition of ε-DP mechanisms is (Σεᵢ)-DP). No
// advanced composition is offered: the √(2n ln(1/δ)) saving requires
// (ε, δ) accounting, a δ budget, and randomised mechanisms — none of
// which this substrate has, and a tighter bound derived from
// assumptions the deployment does not meet is worse than a loose one.
//
// ── Six portability rules (GP 12) ──
//
// Identity is by value (`BudgetScope` is a record of strings, rule 1);
// every ledger method returns `Async` (rule 2); refusal is typed data,
// never a callback or an exception (rule 3); the ledger holds no
// per-call state — a reservation is identified by a value the caller
// carries, so any replica can commit one another opened (rule 4).
// Ordering promises are per-scope only (rule 5) — that is precisely what
// the conditional write buys and all it buys.
//
// ── Cost when unused (GP 13) ──
//
// Nothing here is reachable unless a composition declares a meter —
// `PeerServerApp.withPrivacyBudget` at the federation tier,
// `FactsCompose.withDeclassificationBudgets` at the grounding tier.
// `NoPrivacyBudgetLedger` exists for the composition that wants the seam
// present and the accounting off; a composition that declares no meter
// allocates nothing and takes no branch on the dispatch path.

/// How often a budget refills.
///
/// `PerpetualBudget` is the only setting under which the ceiling bounds
/// *lifetime* disclosure to a counterparty. The other two express a
/// commercial rate limit ("a thousand ε a month") and are a deliberate
/// weakening of the privacy reading — a patient counterparty spends the
/// ceiling again next epoch.
type BudgetEpoch =
    /// One budget, never refilled.
    | PerpetualBudget
    /// A fresh budget each UTC day.
    | DailyBudget
    /// A fresh budget each UTC calendar month.
    | MonthlyBudget

/// The identity a privacy budget is accounted against.
///
/// A record of strings rather than a live handle (GP 12 rule 1), so the
/// same scope names the same budget on every replica and in every store.
type BudgetScope = {
    /// The query surface the budget protects — a clean-room template id
    /// at the federation tier, a declassification routine's operation id
    /// at the grounding tier. Opaque to the ledger: it is a key, and the
    /// composing gate decides what it names.
    TemplateId: string
    /// The *validated* counterparty the budget is accounted against — a
    /// peer id at the federation tier, a contributing party's scope at
    /// the grounding tier. Never a self-asserted wire body, and never a
    /// cascade origin (see the file header).
    CounterpartyPeerId: string
    /// The epoch label derived from `BudgetEpoch`, or `"perpetual"`.
    /// Carried as a string so the scope stays a pure value and the
    /// ledger never needs a clock of its own.
    Epoch: string
}

/// What a WITHHELD answer costs.
///
/// The default is `WithholdCharged`, and the reason is the one that
/// makes a budget worth having. A withhold is not silence: it discloses
/// that the cohort fell below the floor, and a counterparty that can
/// vary its query and read that bit for free has a counting oracle —
/// binary search over a cohort size, at zero ε. Charging refusals closes
/// it, at the honest cost of spending a budget on questions that
/// returned nothing.
type WithholdCharge =
    /// A withheld answer is charged the same ε a released one is.
    | WithholdCharged
    /// Only released answers are charged. Cheaper for the counterparty
    /// and strictly weaker — see above.
    | WithholdFree

/// The consumer's declared accounting policy: what to charge, against
/// what ceiling, over what epoch.
///
/// **Every value here is deployment policy, not SDK opinion (GP 1).**
/// What ε a given query is worth depends on the data, the regulator and
/// the risk appetite; the SDK ships the mechanism that enforces whatever
/// is declared and holds no view on the numbers. The per-method map is
/// data rather than a `methodName -> decimal` callback because a
/// callback would leak framework semantics into a value that has to
/// serialise, travel and be audited (GP 12 rule 3).
type PrivacyBudgetPolicy = {
    /// Total ε admissible per scope per epoch. Reaching it withholds.
    EpsilonCeiling: decimal
    /// ε charged for a method with no entry in `MethodEpsilon`.
    EpsilonPerQuery: decimal
    /// Per-method overrides, keyed by contract method name.
    MethodEpsilon: Map<string, decimal>
    /// How often the budget refills.
    Epoch: BudgetEpoch
    /// Whether a withheld answer is charged.
    WithholdCharge: WithholdCharge
    /// How long an open reservation is honoured before the ledger
    /// reclaims it.
    ///
    /// A reservation is written before the handler runs and settled
    /// after; a process that dies in between would otherwise strand its
    /// ε forever. The TTL must comfortably exceed the longest call the
    /// deployment permits — the default is 15 minutes against a peer
    /// transport deadline of 100 s, so a reclaim cannot race a live call.
    ReservationTtl: TimeSpan
}

/// One accounted charge: a reservation while open, the same value when
/// settled. Identified by a caller-carried id rather than a handle, so
/// any replica can settle a reservation another opened (GP 12 rule 4).
type BudgetSpend = {
    /// Unique per reservation. The ledger's idempotency key: settling
    /// the same id twice is a no-op, not a double refund.
    ReservationId: string
    /// The budget this charge is drawn from.
    Scope: BudgetScope
    /// The ε charged. Non-negative; a zero charge is legal and means
    /// "count the query, spend nothing".
    Epsilon: decimal
    /// The contract method the charge was raised for — audit only; the
    /// ledger accounts by `Scope`.
    MethodName: string
    /// When the reservation was opened.
    OccurredAt: DateTimeOffset
    /// When an unsettled reservation may be reclaimed.
    ExpiresAt: DateTimeOffset
}

/// The accounted state of one scope — what `RemainingBudget` reports and
/// what an auditor reads.
type PrivacyBudget = {
    Scope: BudgetScope
    /// The ceiling the reading was taken against (policy, not stored
    /// state — a composition may retune it between reads).
    EpsilonCeiling: decimal
    /// ε from settled, committed charges.
    EpsilonCommitted: decimal
    /// ε held by reservations that are open and unexpired.
    EpsilonReserved: decimal
    /// Committed charges settled against this scope. Reservations that
    /// were returned do not count.
    QueryCount: int
}

/// Why a reservation was refused. Both cases refuse the call: a ledger
/// that cannot account for a query has not established that the query is
/// affordable, and releasing an answer it could not account for is
/// exactly the free query the ledger exists to prevent.
type BudgetRefusal =
    /// The declared ceiling is reached (or this charge would breach it).
    | BudgetExhausted of requested: decimal * remaining: decimal * ceiling: decimal
    /// The ledger could not decide — storage unreachable, contention
    /// unresolved within the retry bound, stored state unreadable.
    /// **Fail-closed**: unavailable is not "allow".
    | BudgetLedgerUnavailable of reason: string

/// The outcome of asking a ledger to reserve.
type BudgetDecision =
    /// ε is held. The caller MUST settle it with `RecordSpend`.
    | BudgetReserved of spend: BudgetSpend * remaining: decimal
    | BudgetRefused of refusal: BudgetRefusal

/// How a reservation settles.
type SpendOutcome =
    /// The answer was released (or withheld under `WithholdCharged`):
    /// the ε stands.
    | SpendCommitted
    /// Nothing about the protected data reached the caller: the ε goes
    /// back. `reason` is audit-facing.
    | SpendReturned of reason: string

[<RequireQualifiedAccess>]
module PrivacyBudgetPolicy =

    /// The default reservation TTL. Two orders of magnitude above the
    /// 100 s default peer call deadline — see `ReservationTtl`.
    [<Literal>]
    let DefaultReservationTtlMinutes = 15.0

    /// A policy charging `perQuery` per answer against `ceiling`, on a
    /// perpetual budget, charging withholds.
    ///
    /// Every default here is the strict reading: the budget never
    /// refills, and a refusal costs the same as an answer. Loosen
    /// deliberately with `withEpoch` / `withWithholdCharge`.
    let create (ceiling: decimal) (perQuery: decimal) : PrivacyBudgetPolicy = {
        EpsilonCeiling = ceiling
        EpsilonPerQuery = perQuery
        MethodEpsilon = Map.empty
        Epoch = PerpetualBudget
        WithholdCharge = WithholdCharged
        ReservationTtl = TimeSpan.FromMinutes DefaultReservationTtlMinutes
    }

    /// Charge `epsilon` for `methodName` instead of `EpsilonPerQuery`.
    let withMethodEpsilon (methodName: string) (epsilon: decimal) (policy: PrivacyBudgetPolicy) = {
        policy with
            MethodEpsilon = Map.add methodName epsilon policy.MethodEpsilon
    }

    /// Refill the budget on the given cadence. A weakening — see
    /// `BudgetEpoch`.
    let withEpoch (epoch: BudgetEpoch) (policy: PrivacyBudgetPolicy) = { policy with Epoch = epoch }

    /// Decide whether a withheld answer is charged. `WithholdFree`
    /// re-opens the free-probe channel — see `WithholdCharge`.
    let withWithholdCharge (charge: WithholdCharge) (policy: PrivacyBudgetPolicy) = {
        policy with
            WithholdCharge = charge
    }

    /// Override how long an open reservation is honoured.
    let withReservationTtl (ttl: TimeSpan) (policy: PrivacyBudgetPolicy) = { policy with ReservationTtl = ttl }

    /// The ε this policy charges for `methodName`.
    let epsilonFor (methodName: string) (policy: PrivacyBudgetPolicy) : decimal =
        policy.MethodEpsilon
        |> Map.tryFind methodName
        |> Option.defaultValue policy.EpsilonPerQuery

    /// The epoch label for `now` under this policy. Ordinal-comparable
    /// and stable across replicas: UTC, fixed-format, culture-invariant.
    let epochLabel (now: DateTimeOffset) (policy: PrivacyBudgetPolicy) : string =
        let utc = now.ToUniversalTime()

        match policy.Epoch with
        | PerpetualBudget -> "perpetual"
        | DailyBudget -> utc.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)
        | MonthlyBudget -> utc.ToString("yyyy-MM", Globalization.CultureInfo.InvariantCulture)

    /// The scope a call against `templateId` from `counterpartyPeerId`
    /// is accounted under.
    let scopeFor
        (templateId: string)
        (counterpartyPeerId: string)
        (now: DateTimeOffset)
        (policy: PrivacyBudgetPolicy)
        : BudgetScope =
        {
            TemplateId = templateId
            CounterpartyPeerId = counterpartyPeerId
            Epoch = epochLabel now policy
        }

/// The cumulative-accounting seam. Substitutable on the same terms as
/// every other store substrate: a deployment whose ε accounting already
/// lives in a warehouse ships its own and keeps one book.
///
/// **Two-phase by design, and the phasing is the whole contract.**
/// `ReserveBudget` debits atomically *before* the answer is computed;
/// `RecordSpend` either commits that debit or returns it once the
/// outcome is known. A one-shot `Charge` after the fact would release
/// answers on credit (a crash between release and debit is a free
/// query); a one-shot `Charge` before the fact would erode the budget
/// on every failure. Neither is recoverable after the fact, which is
/// why the seam has two methods rather than one.
type IPrivacyBudgetLedger =
    /// Atomically hold `spend.Epsilon` against `spend.Scope`, refusing
    /// if that would carry committed + reserved ε past `ceiling`.
    ///
    /// Implementations MUST make this atomic with respect to concurrent
    /// reservations on the same scope — the ledger's only real claim is
    /// that two parallel queries cannot both spend the last unit.
    abstract ReserveBudget: spend: BudgetSpend * ceiling: decimal -> Async<BudgetDecision>

    /// Settle a reservation. Idempotent by `ReservationId`: settling one
    /// that is already settled (or has been reclaimed on TTL) is a
    /// no-op, never a double credit.
    abstract RecordSpend: spend: BudgetSpend * outcome: SpendOutcome -> Async<unit>

    /// The auditable reading for `scope` against `ceiling`.
    abstract RemainingBudget: scope: BudgetScope * ceiling: decimal -> Async<PrivacyBudget>

    /// `false` for a process-local ledger whose accounting is lost on
    /// restart and wrong across replicas; `true` for one whose state is
    /// durable and shared. Declared rather than inferred so a
    /// composition (or a preflight) can assert on data — the same shape
    /// a replay guard's `IsDistributed` takes.
    abstract IsDurable: bool

/// The stored shape of one scope's accounting. Internal: the on-disk
/// layout is not a contract, and a deployment reading it should read
/// `RemainingBudget` instead.
type internal LedgerState = {
    Committed: decimal
    QueryCount: int
    Open: BudgetSpend list
}

module internal LedgerState =

    let empty: LedgerState = {
        Committed = 0m
        QueryCount = 0
        Open = []
    }

    /// Drop reservations whose TTL has passed — the reclaim that stops a
    /// crashed request stranding ε forever. Lazy (no sweeper, no
    /// scheduler dependency — GP 13): it runs inside the same
    /// read-modify-write every reservation already performs.
    let prune (now: DateTimeOffset) (state: LedgerState) : LedgerState = {
        state with
            Open = state.Open |> List.filter (fun s -> s.ExpiresAt > now)
    }

    let reserved (state: LedgerState) : decimal = state.Open |> List.sumBy _.Epsilon

    let snapshot (scope: BudgetScope) (ceiling: decimal) (state: LedgerState) : PrivacyBudget = {
        Scope = scope
        EpsilonCeiling = ceiling
        EpsilonCommitted = state.Committed
        EpsilonReserved = reserved state
        QueryCount = state.QueryCount
    }

    /// The pure accounting decision, shared by every implementation so
    /// two ledgers cannot disagree about what "exhausted" means.
    let tryReserve (now: DateTimeOffset) (ceiling: decimal) (spend: BudgetSpend) (state: LedgerState) =
        let pruned = prune now state
        let remaining = ceiling - pruned.Committed - reserved pruned

        if spend.Epsilon > remaining then
            Error(BudgetExhausted(spend.Epsilon, remaining, ceiling))
        else
            let next = {
                pruned with
                    Open = spend :: pruned.Open
            }

            Ok(next, remaining - spend.Epsilon)

    /// Settle. Unknown ids settle to a no-op — the reservation expired
    /// and was reclaimed, or this is a duplicate settlement.
    let settle (now: DateTimeOffset) (spend: BudgetSpend) (outcome: SpendOutcome) (state: LedgerState) =
        let pruned = prune now state

        if pruned.Open |> List.exists (fun s -> s.ReservationId = spend.ReservationId) then
            let remainingOpen =
                pruned.Open |> List.filter (fun s -> s.ReservationId <> spend.ReservationId)

            match outcome with
            | SpendCommitted -> {
                pruned with
                    Open = remainingOpen
                    Committed = pruned.Committed + spend.Epsilon
                    QueryCount = pruned.QueryCount + 1
              }
            | SpendReturned _ -> { pruned with Open = remainingOpen }
        else
            pruned

/// The no-op default: every reservation succeeds, nothing accumulates,
/// no ε ceiling binds.
///
/// It exists so a composition can declare the seam without declaring a
/// policy — a staging deployment mirroring production's wiring, a
/// consumer migrating in two steps. **It is not a privacy control**, and
/// `RemainingBudget` says so honestly by reporting the ceiling it was
/// asked about as entirely unspent, forever.
type NoPrivacyBudgetLedger() =

    interface IPrivacyBudgetLedger with
        member _.IsDurable = false

        member _.ReserveBudget(spend, ceiling) = async { return BudgetReserved(spend, ceiling) }

        member _.RecordSpend(_, _) = async { return () }

        member _.RemainingBudget(scope, ceiling) = async {
            return {
                Scope = scope
                EpsilonCeiling = ceiling
                EpsilonCommitted = 0m
                EpsilonReserved = 0m
                QueryCount = 0
            }
        }

/// Process-local reference implementation. Correct and allocation-cheap
/// for a single-instance receiver; **wrong across replicas and lost on
/// restart**, which is what `IsDurable = false` declares.
///
/// Atomicity is a monitor around the whole read-modify-write, which is
/// the strongest thing available in one process and exactly as strong as
/// the conditional write is across many.
///
/// `now` reads the clock the reservation TTL is measured against — the
/// same injection point `BlobPrivacyBudgetLedger` takes, so both ledgers
/// answer "is this hold expired?" the same way and a test can cross a
/// TTL boundary without waiting for one.
type InMemoryPrivacyBudgetLedger(now: unit -> DateTimeOffset) =
    let gate = obj ()
    let states = Dictionary<BudgetScope, LedgerState>()

    let stateOf scope =
        match states.TryGetValue scope with
        | true, s -> s
        | false, _ -> LedgerState.empty

    new() = InMemoryPrivacyBudgetLedger(fun () -> DateTimeOffset.UtcNow)

    /// The scopes this ledger has accounted anything against. Diagnostic
    /// only — the auditable reading is `RemainingBudget`.
    member _.Scopes = lock gate (fun () -> states.Keys |> List.ofSeq)

    interface IPrivacyBudgetLedger with
        member _.IsDurable = false

        member _.ReserveBudget(spend, ceiling) = async {
            return
                lock gate (fun () ->
                    match LedgerState.tryReserve (now ()) ceiling spend (stateOf spend.Scope) with
                    | Error refusal -> BudgetRefused refusal
                    | Ok(next, remaining) ->
                        states[spend.Scope] <- next
                        BudgetReserved(spend, remaining))
        }

        member _.RecordSpend(spend, outcome) = async {
            return
                lock gate (fun () ->
                    states[spend.Scope] <- LedgerState.settle (now ()) spend outcome (stateOf spend.Scope))
        }

        member _.RemainingBudget(scope, ceiling) = async {
            return
                lock gate (fun () ->
                    let pruned = LedgerState.prune (now ()) (stateOf scope)
                    states[scope] <- pruned
                    LedgerState.snapshot scope ceiling pruned)
        }

/// `IConditionalBlobStorage`-backed ledger — the distributed-ready
/// default (`IsDurable = true`). One JSON document per scope under the
/// reserved `_platform` container, every mutation a compare-and-swap
/// against the document's etag.
///
/// **Conditional writes are a hard requirement, checked at construction.**
/// A plain download-modify-upload has a read-modify-write race exactly
/// wide enough for two concurrent queries to read the same remaining
/// budget and both spend it — which is the one thing a budget ledger is
/// for. A ledger that over-admits under load is worse than no ledger,
/// because a deployment believes it is bounded. The constructor
/// therefore refuses an `IBlobStorage` without `IConditionalBlobStorage`
/// loudly and at compose time rather than at the first gated call, the
/// same posture every replay guard in the SDK takes for the same reason.
type BlobPrivacyBudgetLedger(blobs: IBlobStorage, now: unit -> DateTimeOffset) =
    /// The universal F# converter set. Constructed here rather than
    /// borrowed from a companion so the stored document shape does not
    /// depend on which tier composed the ledger — and it is the SAME
    /// converter set the federation tier's serialiser used before Phase
    /// 675 moved this file, so an existing deployment's accounting
    /// documents are read back byte-for-byte.
    static let jsonOptions = FableConverters.create ()

    let container = "_platform"

    /// Retained verbatim across the Phase 675 move. It reads as a
    /// federation-tier name and is now the home of every tier's budget
    /// documents, and that is the correct trade: renaming it would strand
    /// the accounted spend of every deployment that already has some,
    /// which is the one thing a budget ledger must never do. Scopes
    /// cannot collide beneath it — each path component is hashed.
    let prefix = "cleanroom/budget/"

    /// Bounded CAS retries. Contention on one scope is serialised by the
    /// backend, so a retry only re-runs against a fresher document;
    /// exhausting the bound means sustained contention or a broken
    /// backend, and both are `BudgetLedgerUnavailable` (fail-closed)
    /// rather than a silent admit.
    let maxAttempts = 8

    let cas =
        match box blobs with
        | :? IConditionalBlobStorage as c -> c
        | _ ->
            invalidArg
                "blobs"
                "BlobPrivacyBudgetLedger requires an IBlobStorage that also implements IConditionalBlobStorage (conditional writes). The compare-and-swap reservation is the entire budget defence — a download-modify-upload fallback would race exactly where two concurrent queries spend the same remaining epsilon. Use InMemoryPrivacyBudgetLedger for a single-instance receiver, or a backend with conditional-write support."

    /// Blob names are `/`-delimited and a scope's components are free
    /// text (a peer id, a template id), so each component is reduced to
    /// a path-safe token and disambiguated by a short hash of the raw
    /// value. Two distinct scopes therefore cannot collide onto one
    /// document merely because their punctuation normalised alike.
    let token (raw: string) =
        let safe =
            raw
            |> Seq.map (fun c ->
                if Char.IsAsciiLetterOrDigit c || c = '-' || c = '.' then
                    c
                else
                    '_')
            |> Seq.truncate 48
            |> Seq.toArray
            |> System.String

        let digest =
            Convert
                .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(if isNull raw then "" else raw)))
                .ToLowerInvariant()
                .Substring(0, 12)

        $"{safe}-{digest}"

    let blobNameFor (scope: BudgetScope) =
        $"{prefix}{token scope.TemplateId}/{token scope.Epoch}/{token scope.CounterpartyPeerId}.json"

    let parse (bytes: byte[]) =
        try
            let parsed =
                JsonSerializer.Deserialize<LedgerState>(Encoding.UTF8.GetString bytes, jsonOptions)

            if isNull (box parsed) || isNull (box parsed.Open) then
                None
            else
                Some parsed
        with _ ->
            None

    let render (state: LedgerState) =
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, jsonOptions))

    /// One compare-and-swap cycle: read (or establish) the document,
    /// apply `f`, write back under the precondition the read observed.
    /// `f` returns `Error` to abandon the cycle without writing.
    let rec transact
        (attempts: int)
        (name: string)
        (f: LedgerState -> Result<LedgerState * 'a, BudgetRefusal>)
        : Async<Result<'a, BudgetRefusal>> =
        async {
            if attempts <= 0 then
                return
                    Error(
                        BudgetLedgerUnavailable
                            $"the privacy-budget ledger could not settle a conditional write on '{name}' within {maxAttempts} attempts"
                    )
            else
                let! read = cas.DownloadWithETag(container, name)

                let current, condition =
                    match read with
                    | Ok(bytes, etag) ->
                        match parse bytes with
                        | Some state -> Some state, IfMatch etag
                        // Present but unreadable. Refusing is the only
                        // safe reading: overwriting it would zero a
                        // budget somebody already spent.
                        | None -> None, IfMatch etag
                    | Error _ ->
                        // Absent, or storage failed. `IfAbsent` decides
                        // which — a create that loses to an existing
                        // document comes back as `ETagMismatch` and
                        // retries against the real state.
                        Some LedgerState.empty, IfAbsent

                match current with
                | None ->
                    return
                        Error(
                            BudgetLedgerUnavailable
                                $"the stored privacy-budget document '{name}' could not be read as ledger state; refusing rather than overwriting accounted spend"
                        )
                | Some state ->
                    match f state with
                    | Error refusal -> return Error refusal
                    | Ok(next, result) ->
                        let! written = cas.UploadWithETag(container, name, render next, condition)

                        match written with
                        | Ok _ -> return Ok result
                        | Error(ETagMismatch _) -> return! transact (attempts - 1) name f
                        | Error(ConditionalWriteFailure message) ->
                            return
                                Error(
                                    BudgetLedgerUnavailable
                                        $"the privacy-budget ledger could not write '{name}': {message}"
                                )
        }

    new(blobs: IBlobStorage) = BlobPrivacyBudgetLedger(blobs, (fun () -> DateTimeOffset.UtcNow))

    /// Build the ledger when `blobs` supports conditional writes, `None`
    /// otherwise — the probing form, for a compose path that would
    /// rather fall back to `InMemoryPrivacyBudgetLedger` than fail.
    static member TryCreate(blobs: IBlobStorage) : IPrivacyBudgetLedger option =
        match box blobs with
        | :? IConditionalBlobStorage -> Some(BlobPrivacyBudgetLedger blobs :> IPrivacyBudgetLedger)
        | _ -> None

    interface IPrivacyBudgetLedger with
        member _.IsDurable = true

        member _.ReserveBudget(spend, ceiling) = async {
            let! outcome =
                transact maxAttempts (blobNameFor spend.Scope) (fun state ->
                    LedgerState.tryReserve (now ()) ceiling spend state)

            return
                match outcome with
                | Ok remaining -> BudgetReserved(spend, remaining)
                | Error refusal -> BudgetRefused refusal
        }

        member _.RecordSpend(spend, outcome) = async {
            // A settlement that cannot be written leaves the
            // reservation open, and the TTL reclaims it. That is the
            // right failure direction for BOTH outcomes: an
            // uncommitted release is re-charged for at most the TTL,
            // and an unreturned reservation is returned late — never
            // the reverse, where a lost write would hand back budget
            // for an answer that shipped.
            let! _ =
                transact maxAttempts (blobNameFor spend.Scope) (fun state ->
                    Ok(LedgerState.settle (now ()) spend outcome state, ()))

            return ()
        }

        member _.RemainingBudget(scope, ceiling) = async {
            let name = blobNameFor scope
            let! read = cas.DownloadWithETag(container, name)

            let state =
                match read with
                | Ok(bytes, _) -> parse bytes |> Option.defaultValue LedgerState.empty
                | Error _ -> LedgerState.empty

            // A read never writes, so the prune here is a reading
            // convention, not a reclaim — the reclaim happens inside
            // the next reservation's compare-and-swap.
            return LedgerState.snapshot scope ceiling (LedgerState.prune (now ()) state)
        }

/// The composed accounting posture: a ledger, the policy read against
/// it, and the clock the epoch + reservation TTL are derived from.
///
/// A record rather than three parameters because it travels together
/// through the compose record and the gate, and because a composition
/// that swaps its ledger without restating its policy has almost
/// certainly made a mistake.
type PrivacyBudgetMeter = {
    Ledger: IPrivacyBudgetLedger
    Policy: PrivacyBudgetPolicy
    /// Injected so an epoch boundary and a reservation TTL are testable
    /// without waiting for one. Production passes
    /// `DateTimeOffset.UtcNow`.
    Now: unit -> DateTimeOffset
}

[<RequireQualifiedAccess>]
module PrivacyBudgetMeter =

    /// Meter `policy` through `ledger` on the wall clock.
    let create (ledger: IPrivacyBudgetLedger) (policy: PrivacyBudgetPolicy) : PrivacyBudgetMeter = {
        Ledger = ledger
        Policy = policy
        Now = fun () -> DateTimeOffset.UtcNow
    }

    /// Meter on an injected clock — for a test, or a deployment whose
    /// epoch boundaries follow something other than UTC wall time.
    let withClock (now: unit -> DateTimeOffset) (meter: PrivacyBudgetMeter) = { meter with Now = now }

    /// The reservation a call against `templateId` / `methodName` from
    /// `counterpartyPeerId` opens. Pure — the caller hands it to
    /// `ReserveBudget`.
    let spendFor
        (templateId: string)
        (counterpartyPeerId: string)
        (methodName: string)
        (meter: PrivacyBudgetMeter)
        : BudgetSpend =
        let at = meter.Now()

        {
            ReservationId = Guid.NewGuid().ToString "N"
            Scope = PrivacyBudgetPolicy.scopeFor templateId counterpartyPeerId at meter.Policy
            Epsilon = PrivacyBudgetPolicy.epsilonFor methodName meter.Policy
            MethodName = methodName
            OccurredAt = at
            ExpiresAt = at.Add meter.Policy.ReservationTtl
        }