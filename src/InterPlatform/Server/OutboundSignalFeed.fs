// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 491 — the governed outbound signal feed ───────────────────
//
// `CohortActivation.fs` (Phase 490) activates a cohort ONCE: one
// authorisation, one release, one delivery, and the governance question
// is answered at the moment of the act. This file is its CONTINUOUS
// sibling — a media partner optimising a campaign does not want a cohort
// snapshot, it wants a signal ("this opaque cohort's conversion
// propensity moved") arriving on a cadence, indefinitely.
//
// Read `IActivationDestination.fs`'s header first: the authorisation
// vocabulary, the canonical encoding, and the derived-template binding
// are all Phase 490's and are reused here verbatim. What follows is
// only what CONTINUITY changes, because that is the whole of this phase.
//
// ── Four things a one-shot activation never had to answer ──
//
//   1. **An approval must not become an open-ended tap.** A signature on
//      a single release is a bounded act. A signature on a *feed* is a
//      standing permission, and a standing permission with no end is a
//      data export wearing a governance vocabulary. So a feed cannot be
//      declared without a bound: `SignalFeedSpec.EpsilonCeiling` is
//      mandatory and the per-emission ε is mandatory (the noise policy
//      is not optional here, unlike Phase 490), which makes the emission
//      count bounded ARITHMETICALLY — `⌊ceiling / εPerEmission⌋`, exposed
//      as `SignalFeedSpec.emissionBound`. There is no way to express an
//      unbounded feed. `MaxEmissions` and `NotAfter` are additional
//      bounds a deployment may declare; a REFILLING budget epoch
//      (`DailyBudget` / `MonthlyBudget`) is refused by validation unless
//      one of them is present, because a ceiling that refills is a
//      ceiling a patient counterparty simply outwaits.
//
//   2. **Revocation must stop a RUNNING feed, not merely the next fresh
//      one.** This is the single most important property here and it is
//      structural rather than procedural: the feed caches NO approval
//      verdict between emissions. Every emission is a full dispatch
//      through `CleanRoomGate.wrapNoised`, so invariant 0 — Phase 480's
//      bilateral approval over the derived template — is re-evaluated
//      from the registry on every tick. A revocation (from either
//      party), an expiry on the approval record, or any edit to the
//      cohort / purpose / destination therefore stops the very next
//      emission, and the feed PAUSES with an audited reason rather than
//      quietly returning nothing. There is no "reload the feed" step for
//      an operator to forget, because there is nothing loaded.
//
//   3. **The privacy budget is spent continuously, so exhaustion has to
//      be a STOP.** Phase 190's ledger is the instrument, and the feed
//      hands the gate a meter whose ceiling is the feed's own — so the
//      accounting is the ledger's, not a second tally this file keeps.
//      When the ceiling binds, the gate withholds; the feed classifies
//      that withhold (see below) and pauses. It does not degrade, does
//      not fall back to a smaller ε, and does not emit un-noised — on
//      exactly `CleanRoomGate`'s argument about an exhausted budget:
//      quietly weakening the mechanism when the budget runs out is the
//      one behaviour that makes the arrangement worse than not having
//      it.
//
//   4. **A restart must not replay.** A feed whose history re-delivers
//      on every process start is a leak amplifier — the recipient gets N
//      independent noised draws over the same true values and averages
//      the noise away. The emission cursor therefore lives in an
//      `IFeedStateStore`, never in this module and never in a feed
//      object: a restarted process resumes at the sequence it left off,
//      and the ONLY thing it may re-deliver is the single emission whose
//      delivery had not yet been confirmed. See "At-least-once" below.
//
// ── Why the withhold is classified, and why that is not a second gate ──
//
// The gate collapses every refusal — unapproved, revoked, sub-floor,
// off-surface, budget-exhausted — into one opaque `PeerCleanRoomWithheld`,
// and it is right to: a remote caller that can vary its request and read
// the reason back has a counting oracle. A feed's OPERATOR is not that
// caller, and the three refusals mean completely different things to
// them: "chase the counterparty" / "raise the ceiling" / "the cohort was
// small this hour, it will be fine next hour". So on a withhold — and
// only on a withhold, never on the path that released (GP 13) — the feed
// re-asks the same approval check the gate ran and reads the same
// ledger's remaining budget. Deleting that block changes nothing about
// whether an emission releases; the gate remains the sole enforcer. It is
// Phase 490's `activate` doing the same thing for the same reason, one
// classification wider.
//
// ── At-least-once, and why a retry must not re-draw ──
//
// Delivery is at-least-once with a stable idempotency key
// (`SignalFeedCanonical.idempotencyKey` over the feed id, the
// authorisation id and the emission's sequence — the value a partner
// dedupes on, and recomputable partner-side from what the delivery
// carries). The emission is committed to the state store BEFORE it is
// delivered, and a delivery failure leaves it pending; the next tick
// re-delivers THAT VALUE rather than sampling a new one.
//
// Re-drawing on retry would be the subtle disaster: two independent
// noised releases of one true value let the recipient average them and
// halve the noise, so the ε actually spent would be twice the ε
// accounted. The pending emission is therefore the whole answer to "what
// happens on a restart" — at most one release is ever in flight, and it
// is byte-identical every time it is re-sent.
//
// ── What differencing across releases IS and IS NOT defended against ──
//
// Stated plainly, because the honest answer is narrower than "the feed
// is differentially private" and a reader deserves the narrow one:
//
//   * **Each emission is ε-DP** under the composed `INoiseMechanism`,
//     with ε the policy's `totalEpsilon` — provided the caller's declared
//     `NoiseSpec.Sensitivity` is true of its signal. That is the caller's
//     statement about its own query and the substrate cannot check it.
//   * **The SERIES is bounded at Σε by basic (sequential) composition**
//     (Dwork & Roth, Theorem 3.16), and that sum is what the ledger
//     enforces against the feed's ceiling. So a recipient who averages T
//     releases to shrink the noise is spending the budget that caps T:
//     the ceiling is not a quota, it is the whole disclosure bound.
//   * **The cohort definition cannot drift under a running feed.** The
//     authorisation covers the cohort's definition, and its digest is the
//     template id, so narrowing a cohort between emissions produces a
//     template nobody has approved and invariant 0 refuses it. The
//     classic "difference two near-identical cohorts to isolate a record"
//     attack cannot be mounted by the FEED against its own definition.
//   * **NOT continual-observation differential privacy.** The tighter
//     stream mechanisms (Dwork–Naor–Pitassi–Rothblum 2010; Chan–Shi–Song
//     2011) that buy a polylog-in-T error bound need a hierarchical
//     aggregation this substrate does not implement. What is shipped is
//     per-release noise plus a hard total bound. Describing it as
//     continual-observation DP to a regulator would be false, in the same
//     way and for the same reason `PrivacyBudgetLedger.fs`'s header says
//     accounting ε over deterministic answers is not DP.
//   * **NOT a defence against a caller whose signal re-samples the same
//     population every tick.** If the underlying data barely moves, T
//     releases are T noisy views of one number, and only the ceiling
//     stands between the recipient and the true value. That is why the
//     ceiling is mandatory, and it is the reason to set it from the
//     disclosure the parties agreed to rather than from the cadence the
//     campaign would like.
//
// ── Why a token release is refused outright ──
//
// A feed may release a count or a histogram, never `ReleaseTokens`.
// Phase 490 already refuses a noised token release (the token list's
// LENGTH is the true cohort size, which noise has just moved), and every
// feed emission is noised by construction — so the case would be dead.
// It is refused at VALIDATION rather than left to fail per-emission,
// because a feed that only discovers its shape is impossible on the first
// tick is a feed an operator believes is running.
//
// ── Cost when unused (GP 13) ──
//
// Nothing here is composed by `PeerServerApp.run`, and nothing here runs
// on a timer of its own: `SignalFeed.emit` is DRIVEN — a deployment ticks
// it from its own `IJobScheduler`, cron, or loop. There is no hosted
// service, no background thread, and no allocation in a deployment that
// never constructs a `SignalFeedSpec`; its peer substrate is byte-for-byte
// what it was before this phase (GP 11), the posture `CohortActivation`
// and `RoundOrchestrator` both take.

/// Why a feed is not running. Failure as data (GP 12 rule 3), one case
/// per cause, because the operator's next action differs completely
/// between them: chase a counterparty, raise a ceiling, or accept that
/// the agreed bound is spent.
type FeedHalt =
    /// Invariant 0 refused, or the live delivery seam's descriptor has
    /// drifted from the authorised one. Covers revocation by either
    /// party, an expired approval record, and every edit to the cohort,
    /// the purpose or the destination. **Recoverable** — a fresh
    /// bilateral approval plus an operator `resume` restarts the feed.
    | FeedUnauthorised of reason: string
    /// The declared ε ceiling is reached. **Recoverable** — the
    /// composition raises the ceiling and an operator resumes.
    | FeedBudgetExhausted of reason: string
    /// The declared `MaxEmissions` bound is spent. **Terminal**: the
    /// parties agreed to a number of emissions and it has been made.
    | FeedVolumeReached of emissions: int
    /// The declared `NotAfter` instant has passed. **Terminal.**
    | FeedWindowClosed of at: DateTimeOffset
    /// An operator paused the feed. **Recoverable.**
    | FeedOperatorPaused of reason: string
    /// An operator stopped the feed. **Terminal.**
    | FeedOperatorStopped of reason: string

/// Where a feed is in its lifecycle. Persisted, because "is this feed
/// running?" must survive the process that was running it.
type FeedStatus =
    /// Emitting on cadence, subject to every per-emission invariant.
    | FeedRunning
    /// Halted and resumable. A `resume` flips this back to
    /// `FeedRunning`; it re-checks NOTHING itself, because the next tick
    /// re-runs invariant 0 and the budget anyway — resuming a feed whose
    /// approval is still revoked simply pauses it again, which is the
    /// fail-closed reading.
    | FeedPaused of halt: FeedHalt * at: DateTimeOffset
    /// Halted for good. Only a new feed (a new id, a new agreed bound)
    /// starts emitting again.
    | FeedStopped of halt: FeedHalt * at: DateTimeOffset

/// One emission: what was released, under which authorisation, at which
/// point in the feed's sequence.
///
/// Value-typed and immutable (GP 12 rule 1) — it is persisted, replayed
/// on a delivery retry, and handed to a sink that may be a webhook, a
/// queue or a peer, so it carries no live handle.
///
/// **`Release` is stored, not recomputed.** A retry re-sends this exact
/// value; drawing fresh noise for the same sequence would hand the
/// recipient two independent samples of one true number and spend twice
/// the ε that was accounted. See the file header.
type FeedEmission = {
    FeedId: string
    /// `ActivationCanonical.id` of the feed's authorisation — the join
    /// key between this delivery and both parties' signed approval
    /// records.
    AuthorisationId: string
    /// Monotonic from 1, never reused, and stable across a restart
    /// because it is derived from the persisted cursor.
    Sequence: int64
    /// The stable dedupe key a partner verifies and dedupes on
    /// (Phase 6d outbound / Phase 440 inbound). Recomputable
    /// partner-side with `SignalFeedCanonical.idempotencyKey`.
    IdempotencyKey: string
    /// What crossed the boundary — a Phase 490 release, so the same
    /// structural guarantee holds: there is no case that could carry the
    /// cohort's own member ids.
    Release: ActivationRelease
    /// The ε this emission spent, as accounted by the ledger.
    Epsilon: decimal
    EmittedAt: DateTimeOffset
}

/// The persisted cursor. **This, not a feed object, is the feed** — a
/// process holds no state between ticks, so any replica can advance a
/// feed another replica started (GP 12 rule 4).
type FeedState = {
    FeedId: string
    /// The authorisation this cursor's counters belong to. A feed whose
    /// authorisation changed is a different agreement and gets a
    /// different feed, so `SignalFeed.start` refuses to adopt a cursor
    /// whose version has moved — otherwise an edited cohort would
    /// inherit the spend of the one the parties actually signed.
    AuthorisationId: string
    AuthorisationVersion: string
    Status: FeedStatus
    /// Emissions PRODUCED (not necessarily delivered). Counted at
    /// production so a delivery-retry loop cannot outrun the declared
    /// volume bound.
    EmissionCount: int
    /// This feed's own tally. Diagnostic — the authoritative reading is
    /// the ledger's (`SignalFeed.inspect` reports both, and they differ
    /// only when something else shares the scope).
    EpsilonSpent: decimal
    /// The ε of the most recent emission — 491.C's "last value's ε".
    LastEpsilon: decimal
    LastSequence: int64
    LastEmittedAt: DateTimeOffset option
    /// The one emission that may be re-delivered. At most one is ever in
    /// flight; a restart re-sends this and nothing else.
    Pending: FeedEmission option
    /// Optimistic-concurrency token. Bumped by the store on every write;
    /// a stale value is refused, which is what stops two ticks
    /// committing over one another.
    Revision: int64
}

/// Why a state write did not happen. Typed rather than a string so a
/// caller can tell "someone else got there first" (benign, retry next
/// tick) from "the store is down" (fail-closed).
type FeedStateWriteError =
    /// The stored revision has moved — another tick committed first.
    | FeedStateConflict
    /// The store could not be read or written.
    | FeedStateUnavailable of reason: string

/// The feed cursor's home. A seam because durability is a deployment
/// choice and because a feed that only exists in one process is not a
/// feed a federation can be told about.
///
/// Two methods, deliberately: the DECISION (what the next state is) lives
/// in `SignalFeed`, so two store implementations cannot disagree about
/// what a feed's rules are — the same split `ITemplateApprovalRegistry`
/// takes against `TemplateApproval.status`.
type IFeedStateStore =
    /// `Ok None` for a feed that was never started. `Error` is a store
    /// failure and is fail-closed by every caller — a feed whose cursor
    /// cannot be read is a feed that must not emit, because emitting
    /// would risk replaying.
    abstract Load: feedId: string -> Async<Result<FeedState option, string>>

    /// Compare-and-swap on `state.Revision`. Returns the stored state
    /// with its new revision.
    abstract Save: state: FeedState -> Async<Result<FeedState, FeedStateWriteError>>

    /// `false` for a process-local store whose cursor is lost on restart
    /// and wrong across replicas. Declared rather than inferred, the
    /// shape `IPrivacyBudgetLedger.IsDurable` takes.
    abstract IsDurable: bool

/// The signal itself: an opaque aggregate over the authorised cohort,
/// computed by the deployment.
///
/// A seam, and the aggregate is the CALLER's (GP 1) — forge has no
/// opinion about what "conversion propensity" means, only about what may
/// leave once it has been computed. The answer is a `CohortResult`
/// because that is the gate-checkable shape: a source that answered in
/// any other shape is withheld by invariant 2, exactly as a gated
/// contract handler is.
///
/// Stateless between calls (GP 12 rule 4) — the emission's sequence is
/// passed in so a source can align its window to the feed's ordinal
/// rather than remembering where it was.
type ISignalSource =
    abstract Sample: authorisationId: string * sequence: int64 -> Async<Result<CohortResult, string>>

/// Where an emission goes.
///
/// Separate from Phase 490's `IActivationDestination` rather than reusing
/// it, for one reason that matters: a feed's deliveries all share an
/// authorisation id, so `Deliver(authorisationId, release)` carries
/// nothing a partner could dedupe on. This seam carries the whole
/// emission, including its stable idempotency key — which is what the
/// Phase 6d outbound path sends and the Phase 440 inbound verification
/// checks. Phase 490's seam is untouched (GP 11).
type ISignalFeedSink =
    /// The approvable description. Compared against the authorised
    /// destination on every emission, so a seam whose live description
    /// has drifted from the signed one halts the feed rather than
    /// quietly delivering somewhere else.
    abstract Descriptor: ActivationDestination

    /// Accept one emission. At-least-once: the same emission may arrive
    /// more than once, byte-identical, under the same
    /// `IdempotencyKey`.
    abstract Deliver: emission: FeedEmission -> Async<Result<unit, string>>

/// A feed's declared shape: what signal, to whom, how often, at what ε,
/// and — mandatorily — within what bound.
///
/// **Three things Phase 490 makes optional are required here**, and that
/// is the design rather than an oversight: a feed is the shape where
/// opting out of governance is not offered. The noise policy is a field
/// (not an `option`), so no feed can emit un-noised; the ε ceiling is a
/// field, so no feed is unbounded; and `SignalFeedDeps` requires an
/// approval check and a budget meter for the same reason.
type SignalFeedSpec = {
    /// Stable operator-facing id. Names the cursor in the state store,
    /// so two feeds must not share one.
    FeedId: string
    /// This cohort, for this purpose, to this destination — Phase 490's
    /// unit, reused verbatim. Its digest is the derived template id, and
    /// therefore the id every approval, every audit row and every
    /// delivery joins on.
    Authorisation: ActivationAuthorisation
    /// Which of the destination's approved shapes each emission takes.
    /// `ReleaseTokens` is refused — see the file header.
    Shape: ActivationShape
    /// The minimum interval between emissions. The rate shaping: a tick
    /// sooner than this is `FeedNotDue`, not an emission.
    Cadence: TimeSpan
    /// The per-emission calibrated noise. Its `totalEpsilon` IS the
    /// per-emission ε the ledger charges — `CleanRoomGate` substitutes
    /// the mechanism's ε for the policy schedule whenever a noise
    /// posture is composed, so there is one number and not two.
    Noise: NoisedReleasePolicy
    /// The total ε this feed may ever spend, enforced through the
    /// ledger. Reaching it pauses the feed.
    EpsilonCeiling: decimal
    /// An optional hard emission count, below the one the ceiling
    /// already implies.
    MaxEmissions: int option
    /// An optional end instant. A feed with neither this nor
    /// `MaxEmissions` may not be metered on a refilling budget epoch —
    /// see `SignalFeed.validate`.
    NotAfter: DateTimeOffset option
}

/// What one tick did. Every case is data an operator or a scheduler can
/// act on without parsing prose (GP 12 rule 3).
type FeedTick =
    /// A fresh emission was produced, committed and delivered.
    | FeedEmitted of emission: FeedEmission
    /// A previously-committed emission whose delivery had not been
    /// confirmed was re-sent, byte-identical. No sample was taken and no
    /// ε was spent.
    | FeedRedelivered of emission: FeedEmission
    /// The cadence has not elapsed. Carries when it will.
    | FeedNotDue of nextDueAt: DateTimeOffset
    /// The feed is paused or stopped — either already, or as of this
    /// tick.
    | FeedHalted of halt: FeedHalt
    /// This emission did not happen, and the feed is still running: the
    /// signal source failed, the gate withheld for a reason that is not
    /// a governance stop (a sub-floor cohort this hour), or the delivery
    /// failed and the emission is pending.
    | FeedEmissionRefused of refusal: ActivationRefusal
    /// The tick could not be decided: the feed was never started, its
    /// cursor is unreadable, or a concurrent tick committed first.
    /// Fail-closed — nothing was delivered.
    | FeedUnavailable of reason: string

/// The operator's read-out: 491.C's inspect.
type FeedInspection = {
    FeedId: string
    Status: FeedStatus
    EmissionCount: int
    /// The bound the ceiling implies at this feed's per-emission ε.
    EmissionBound: int
    MaxEmissions: int option
    NotAfter: DateTimeOffset option
    EpsilonCeiling: decimal
    /// From the ledger — the authoritative reading.
    EpsilonCommitted: decimal
    EpsilonReserved: decimal
    EpsilonRemaining: decimal
    /// The ε of the most recent emission.
    LastEpsilon: decimal
    LastEmittedAt: DateTimeOffset option
    NextDueAt: DateTimeOffset option
    /// The emission awaiting delivery confirmation, if any.
    Pending: FeedEmission option
}

/// The substrate one feed runs over.
///
/// Every governance control Phase 490 leaves optional is REQUIRED here.
/// A feed composed without an approval check would be a standing
/// permission nobody granted; one without a budget meter would be an
/// unbounded tap; one without a noise mechanism would emit deterministic
/// answers while charging ε for them, which
/// `PrivacyBudgetLedger.fs`'s header is explicit must never be described
/// as differential privacy.
type SignalFeedDeps = {
    /// The privacy mechanism. Substitutable (GP 1); the substrate's own
    /// release post-condition still binds over whatever it returns.
    Broker: ICleanRoomBroker
    /// Invariant 0 — normally `TemplateApprovalGate.check policy
    /// localPeerId`. Re-evaluated on EVERY emission; nothing is cached.
    Approval: CleanRoomApprovalCheck
    /// Invariant 0.5 — the cumulative ε meter. Its ceiling is overridden
    /// per feed by `SignalFeedSpec.EpsilonCeiling`, so the ledger is the
    /// single accountant.
    Budget: PrivacyBudgetMeter
    /// Invariant 4 — the calibrated-noise sampler.
    Mechanism: INoiseMechanism
    /// The caller's aggregate.
    Signal: ISignalSource
    /// The outbound delivery seam.
    Sink: ISignalFeedSink
    /// The persisted cursor.
    State: IFeedStateStore
    /// One row per feed decision and per operator act, alongside the
    /// rows the gate itself records. `SignalFeed.noAudit` records
    /// nothing.
    Audit: PeerCleanRoomDecisionPayload -> Async<unit>
    /// Injected so a cadence boundary and a `NotAfter` window are
    /// testable without waiting for one.
    Now: unit -> DateTimeOffset
}

/// The canonical encodings a feed adds to Phase 490's.
///
/// Same construction as `ActivationCanonical` — length-prefixed fields,
/// an explicit domain separator — for the same reason: a partner
/// recomputes the idempotency key from what the delivery carries, and a
/// key that could be forged by putting a delimiter in a feed id would be
/// no key at all.
[<RequireQualifiedAccess>]
module SignalFeedCanonical =

    /// Domain separator, so a delivery key can never collide with an
    /// activation authorisation's digest even if the field sequences
    /// coincided.
    [<Literal>]
    let domain = "fuaran.federation.signalfeed.delivery/1"

    let private field (sb: StringBuilder) (value: string) : unit =
        sb.Append(Encoding.UTF8.GetByteCount value).Append(':').Append(value).Append('\n')
        |> ignore

    /// The stable dedupe key for one emission: lowercase-hex SHA-256
    /// over the domain, the feed id, the authorisation id and the
    /// sequence.
    ///
    /// Deterministic in those three values and nothing else, which is
    /// what makes it identical on every retry and recomputable by the
    /// receiving partner.
    let idempotencyKey (feedId: string) (authorisationId: string) (sequence: int64) : string =
        let sb = StringBuilder()
        field sb domain
        field sb feedId
        field sb authorisationId
        field sb (string sequence)

        SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

[<RequireQualifiedAccess>]
module SignalFeedSpec =

    /// A feed of `authorisation`'s cohort in `shape`, emitting no more
    /// often than `cadence`, noised by `noise`, within a total ε
    /// `ceiling`.
    ///
    /// Every argument is mandatory because every one of them is a bound
    /// or a mechanism, and a feed missing either is the thing this phase
    /// exists to make unexpressible.
    let create
        (feedId: string)
        (authorisation: ActivationAuthorisation)
        (shape: ActivationShape)
        (cadence: TimeSpan)
        (noise: NoisedReleasePolicy)
        (ceiling: decimal)
        : SignalFeedSpec =
        {
            FeedId = feedId
            Authorisation = authorisation
            Shape = shape
            Cadence = cadence
            Noise = noise
            EpsilonCeiling = ceiling
            MaxEmissions = None
            NotAfter = None
        }

    /// Also bound the feed by a hard emission count.
    let withMaxEmissions (count: int) (spec: SignalFeedSpec) = { spec with MaxEmissions = Some count }

    /// Also bound the feed by an end instant.
    let withNotAfter (instant: DateTimeOffset) (spec: SignalFeedSpec) = { spec with NotAfter = Some instant }

    /// The ε one emission spends — the composed noise policy's total,
    /// which is exactly what `CleanRoomGate` reserves when a noise
    /// posture is composed.
    let epsilonPerEmission (spec: SignalFeedSpec) : decimal =
        NoisedReleasePolicy.totalEpsilon spec.Noise

    /// The most emissions the ceiling admits: `⌊ceiling / εPerEmission⌋`.
    ///
    /// **This is why a feed cannot be an open-ended tap**, and it is
    /// arithmetic rather than a rule anyone enforces: the noise policy is
    /// mandatory, a policy that draws no noise is refused by
    /// `NoisedReleasePolicy.validate`, so the per-emission ε is strictly
    /// positive and the count is finite.
    let emissionBound (spec: SignalFeedSpec) : int =
        let per = epsilonPerEmission spec

        if per <= 0m then
            0
        else
            let admitted = spec.EpsilonCeiling / per

            if admitted > decimal Int32.MaxValue then
                Int32.MaxValue
            else
                int (floor admitted)

    /// The derived Phase 480 template a feed's emissions are gated by —
    /// Phase 490's, unchanged.
    let template (spec: SignalFeedSpec) : CleanRoomTemplate =
        ActivationCanonical.template spec.Authorisation

    /// The authorisation id every emission, audit row and delivery joins
    /// on.
    let authorisationId (spec: SignalFeedSpec) : string =
        ActivationCanonical.id spec.Authorisation

    /// Every way a spec is not a feed, as data. Empty on a healthy one.
    ///
    /// The composition-level rule (a refilling epoch needs an explicit
    /// bound) lives in `SignalFeed.validate`, which can see the meter.
    let validate (spec: SignalFeedSpec) : string list =
        let per = epsilonPerEmission spec

        [
            if String.IsNullOrWhiteSpace spec.FeedId then
                yield
                    "a feed needs a stable FeedId: it names the cursor in the state store, and a blank one would share a cursor with every other blank-named feed"

            if spec.Cadence <= TimeSpan.Zero then
                yield "a feed's Cadence must be positive — a non-positive interval is not rate shaping, it is a loop"

            if spec.EpsilonCeiling <= 0m then
                yield
                    "a feed's EpsilonCeiling must be positive: it is the total disclosure bound, and a feed without one is a standing data export"

            yield! NoisedReleasePolicy.validate spec.Noise

            if per <= 0m then
                yield
                    "a feed's noise policy must spend a positive epsilon per emission, else the ceiling bounds nothing and the feed is unbounded by arithmetic"

            if per > 0m && spec.EpsilonCeiling > 0m && per > spec.EpsilonCeiling then
                yield
                    $"one emission costs {per} epsilon against a ceiling of {spec.EpsilonCeiling}, so this feed could never emit at all"

            if spec.Shape = ReleaseTokens then
                yield
                    "a signal feed may not release per-member tokens: every emission is noised, and a noised token release is already refused by invariant 5 because the token list's length would republish the true cohort size the noise had just moved"

            if not (Set.contains spec.Shape spec.Authorisation.Destination.PermittedShapes) then
                yield
                    $"shape '{ActivationShape.name spec.Shape}' is not among the shapes destination '{spec.Authorisation.Destination.DestinationId}' is authorised to receive"

            if spec.MaxEmissions |> Option.exists (fun count -> count <= 0) then
                yield "MaxEmissions, when declared, must be positive"
        ]

/// The process-local cursor store. Correct for a single-instance feed
/// runner; **lost on restart and wrong across replicas**, which is what
/// `IsDurable = false` declares. A feed metered on this store cannot
/// honour the no-replay claim across a process boundary — that is what
/// `BlobFeedStateStore` is for.
type InMemoryFeedStateStore() =
    let gate = obj ()
    let states = Dictionary<string, FeedState>()

    interface IFeedStateStore with
        member _.IsDurable = false

        member _.Load(feedId) = async {
            return
                lock gate (fun () ->
                    match states.TryGetValue feedId with
                    | true, state -> Ok(Some state)
                    | false, _ -> Ok None)
        }

        member _.Save(state) = async {
            return
                lock gate (fun () ->
                    let current =
                        match states.TryGetValue state.FeedId with
                        | true, existing -> Some existing
                        | false, _ -> None

                    let expected = current |> Option.map _.Revision |> Option.defaultValue 0L

                    if expected <> state.Revision then
                        Error FeedStateConflict
                    else
                        let next = {
                            state with
                                Revision = state.Revision + 1L
                        }

                        states[state.FeedId] <- next
                        Ok next)
        }

/// `IConditionalBlobStorage`-backed cursor store — the distributed-ready
/// default (`IsDurable = true`). One JSON document per feed under the
/// reserved `_platform` container, every write a compare-and-swap.
///
/// **Conditional writes are a hard requirement, checked at construction**,
/// on exactly `BlobPrivacyBudgetLedger`'s argument: a
/// download-modify-upload has a race precisely wide enough for two ticks
/// to read the same cursor and both commit an emission, which is a
/// double delivery and a double ε spend. Refusing loudly at compose time
/// beats discovering it at the first tick.
type BlobFeedStateStore(blobs: IBlobStorage) =
    let container = "_platform"
    let prefix = "signal-feeds/"

    let cas =
        match box blobs with
        | :? IConditionalBlobStorage as c -> c
        | _ ->
            invalidArg
                "blobs"
                "BlobFeedStateStore requires an IBlobStorage that also implements IConditionalBlobStorage (conditional writes). The compare-and-swap on the feed cursor is what stops two concurrent ticks committing an emission over one another — a double delivery and a double epsilon spend. Use InMemoryFeedStateStore for a single-instance runner, or a backend with conditional-write support."

    /// Blob names are `/`-delimited and a feed id is free text, so the
    /// id is reduced to a path-safe token and disambiguated by a short
    /// hash of the raw value — `BlobPrivacyBudgetLedger.token`'s
    /// construction, so two feeds cannot collide onto one document
    /// merely because their punctuation normalised alike.
    let blobNameFor (feedId: string) =
        let raw = if isNull feedId then "" else feedId

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
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes raw)).ToLowerInvariant().Substring(0, 12)

        $"{prefix}{safe}-{digest}.json"

    let parse (bytes: byte[]) =
        try
            let parsed = JsonRpc.deserialize<FeedState> (Encoding.UTF8.GetString bytes)

            if isNull (box parsed) || isNull (box parsed.FeedId) then
                None
            else
                Some parsed
        with _ ->
            None

    /// Build the store when `blobs` supports conditional writes, `None`
    /// otherwise — the probing form, for a compose path that would
    /// rather fall back than fail.
    static member TryCreate(blobs: IBlobStorage) : IFeedStateStore option =
        match box blobs with
        | :? IConditionalBlobStorage -> Some(BlobFeedStateStore blobs :> IFeedStateStore)
        | _ -> None

    interface IFeedStateStore with
        member _.IsDurable = true

        member _.Load(feedId) = async {
            let! read = cas.DownloadWithETag(container, blobNameFor feedId)

            match read with
            | Error _ -> return Ok None
            | Ok(bytes, _) ->
                match parse bytes with
                | Some state -> return Ok(Some state)
                | None ->
                    // Present but unreadable. Refusing is the only safe
                    // reading: treating it as absent would restart the
                    // feed at sequence 1 and replay its whole history.
                    return
                        Error
                            $"the stored cursor for feed '{feedId}' could not be read as feed state; refusing rather than restarting a feed whose emission history is unknown"
        }

        member _.Save(state) = async {
            let name = blobNameFor state.FeedId
            let! read = cas.DownloadWithETag(container, name)

            let stored, condition =
                match read with
                | Ok(bytes, etag) -> parse bytes, IfMatch etag
                | Error _ -> None, IfAbsent

            let expected = stored |> Option.map _.Revision |> Option.defaultValue 0L

            if expected <> state.Revision then
                return Error FeedStateConflict
            else
                let next = {
                    state with
                        Revision = state.Revision + 1L
                }

                let! written =
                    cas.UploadWithETag(container, name, Encoding.UTF8.GetBytes(JsonRpc.serialize next), condition)

                match written with
                | Ok _ -> return Ok next
                | Error(ETagMismatch _) -> return Error FeedStateConflict
                | Error(ConditionalWriteFailure message) ->
                    return Error(FeedStateUnavailable $"the feed cursor '{name}' could not be written: {message}")
        }

[<RequireQualifiedAccess>]
module SignalFeedDeps =

    /// The minimum composition — and every argument is required. There
    /// is no `withApproval` / `withBudget` / `withNoise` here on
    /// purpose: a feed cannot be composed without them, so a deployment
    /// cannot believe it has a governed feed it never governed.
    let create
        (broker: ICleanRoomBroker)
        (approval: CleanRoomApprovalCheck)
        (budget: PrivacyBudgetMeter)
        (mechanism: INoiseMechanism)
        (signal: ISignalSource)
        (sink: ISignalFeedSink)
        (state: IFeedStateStore)
        : SignalFeedDeps =
        {
            Broker = broker
            Approval = approval
            Budget = budget
            Mechanism = mechanism
            Signal = signal
            Sink = sink
            State = state
            Audit = fun _ -> async { return () }
            Now = fun () -> DateTimeOffset.UtcNow
        }

    /// Record one row per feed decision and per operator act.
    let withAudit (sink: PeerCleanRoomDecisionPayload -> Async<unit>) (deps: SignalFeedDeps) = {
        deps with
            Audit = sink
    }

    /// Drive the cadence and the `NotAfter` window from an injected
    /// clock — for a test, or a deployment whose windows follow
    /// something other than UTC wall time.
    let withClock (now: unit -> DateTimeOffset) (deps: SignalFeedDeps) = { deps with Now = now }

/// The one road from a signal to a partner, on a cadence.
[<RequireQualifiedAccess>]
module SignalFeed =

    /// The audit sink a composition without an `IAuditLog` uses: the
    /// feed still decides, it simply records nothing. Explicit rather
    /// than an `option` so there is one code path, the same reason
    /// `CleanRoomGate.noAudit` exists.
    let noAudit: PeerCleanRoomDecisionPayload -> Async<unit> =
        fun _ -> async { return () }

    /// The version the synthesised peer contract is dispatched under. It
    /// never reaches the wire — the registration is built and called
    /// in-process — but the gate reads a context, and a context needs
    /// one.
    let private v1: ContractVersion = { Major = 1; Minor = 0 }

    /// Record without ever letting the recording fail the feed.
    /// Best-effort on `CleanRoomGate.record`'s terms: an audit sink that
    /// is down must not turn a delivered emission into a refusal, nor a
    /// halt into a release.
    let private record (deps: SignalFeedDeps) payload = async {
        try
            do! deps.Audit payload
        with ex when not (ex :? OperationCanceledException) ->
            ()
    }

    let private row (deps: SignalFeedDeps) (spec: SignalFeedSpec) released reason : PeerCleanRoomDecisionPayload = {
        ContractId = spec.Authorisation.Destination.DestinationId
        MethodName = ActivationShape.name spec.Shape
        TemplateId = SignalFeedSpec.authorisationId spec
        CallerPeerId = spec.Authorisation.Destination.CounterpartyPeerId
        RootRequestId = spec.FeedId
        Released = released
        SuppressedCells = []
        Reason = reason
        OccurredAt = deps.Now()
    }

    /// The same row correlated to ONE emission rather than to the feed
    /// as a whole.
    ///
    /// The gate's own decision row for that emission carries the same
    /// `RootRequestId` — it is the emission's idempotency key, per
    /// `contextFor` — so an auditor reconstructs one emission from both
    /// rows, and from the delivery the partner received, exactly the way
    /// Phase 490 reconstructs one activation.
    let private emissionRow
        (deps: SignalFeedDeps)
        (spec: SignalFeedSpec)
        (emission: FeedEmission)
        released
        reason
        : PeerCleanRoomDecisionPayload =
        {
            row deps spec released reason with
                RootRequestId = emission.IdempotencyKey
        }

    /// The meter one feed is accounted through: the composed ledger and
    /// clock, with THIS feed's ceiling.
    ///
    /// Overriding the ceiling rather than keeping a second tally is what
    /// makes the ledger the single accountant — and the budget scope is
    /// keyed by the derived template id, so a feed's spend is its own
    /// and cannot be eroded by an unrelated query under a different
    /// template.
    let private meterFor (deps: SignalFeedDeps) (spec: SignalFeedSpec) : PrivacyBudgetMeter = {
        deps.Budget with
            Policy = {
                deps.Budget.Policy with
                    EpsilonCeiling = spec.EpsilonCeiling
            }
    }

    /// The ε still available to this feed, read from the ledger.
    let private remainingEpsilon (deps: SignalFeedDeps) (spec: SignalFeedSpec) = async {
        let meter = meterFor deps spec

        let scope =
            PrivacyBudgetPolicy.scopeFor
                (SignalFeedSpec.authorisationId spec)
                spec.Authorisation.Destination.CounterpartyPeerId
                (meter.Now())
                meter.Policy

        let! reading = meter.Ledger.RemainingBudget(scope, spec.EpsilonCeiling)
        return reading, spec.EpsilonCeiling - reading.EpsilonCommitted - reading.EpsilonReserved
    }

    /// The outbound call context one emission is dispatched under. The
    /// counterparty is taken from the AUTHORISED descriptor, never from
    /// anything the sink asserts at call time — it is the id the approval
    /// was signed against. `RootRequestId` is the emission's idempotency
    /// key, so the gate's audit row and the feed's join on the delivery.
    let private contextFor (spec: SignalFeedSpec) (sequence: int64) : PeerCallContext =
        let authorisationId = SignalFeedSpec.authorisationId spec

        {
            Peer = {
                PeerId = spec.Authorisation.Destination.CounterpartyPeerId
                DisplayName = spec.Authorisation.Destination.DestinationId
            }
            User = Anonymous
            ContractVersion = v1
            Route = [ spec.Authorisation.Destination.CounterpartyPeerId ]
            RootRequestId = SignalFeedCanonical.idempotencyKey spec.FeedId authorisationId sequence
            ParentRequestId = None
            HopsRemaining = 1
        }

    /// Invariant 5 — the egress projection, in a feed's two shapes.
    ///
    /// Pure and total. A token release is unreachable (validation refuses
    /// the spec and `start` runs validation), and it is kept as an
    /// explicit refusal rather than assumed away: an egress classifier's
    /// default branch must be silence.
    let private project (spec: SignalFeedSpec) (cleared: CohortResult) : Result<ActivationRelease, ActivationRefusal> =
        let destination = spec.Authorisation.Destination

        if not (Set.contains spec.Shape destination.PermittedShapes) then
            Error(
                ActivationEgressRefused
                    $"shape '{ActivationShape.name spec.Shape}' is not among the shapes destination '{destination.DestinationId}' is authorised to receive"
            )
        else
            match spec.Shape with
            | ReleaseCount -> Ok(ActivatedCount(cleared.Cells |> List.sumBy _.Count))
            | ReleaseHistogram ->
                Ok(
                    ActivatedHistogram(
                        cleared.Cells
                        |> List.map (fun cell -> {
                            Label = cell.Label
                            Count = cell.Count
                        })
                    )
                )
            | ReleaseTokens ->
                Error(
                    ActivationEgressRefused
                        "a signal feed may not release per-member tokens; every emission is noised and a noised token list would republish the true cohort size"
                )

    /// Every reason this composition cannot run this feed. Spec rules
    /// plus the one that needs the meter.
    ///
    /// **The refilling-epoch rule is the anti-tap rule.** A
    /// `DailyBudget` / `MonthlyBudget` ceiling is spent and then handed
    /// back, so a feed metered on one has no total disclosure bound at
    /// all — a patient counterparty simply waits. That is a legitimate
    /// commercial arrangement, so it is permitted, but only alongside an
    /// explicit `MaxEmissions` or `NotAfter` that a refill cannot
    /// outwait.
    let validate (deps: SignalFeedDeps) (spec: SignalFeedSpec) : string list = [
        yield! SignalFeedSpec.validate spec

        if
            deps.Budget.Policy.Epoch <> PerpetualBudget
            && Option.isNone spec.MaxEmissions
            && Option.isNone spec.NotAfter
        then
            yield
                "this feed is metered on a REFILLING budget epoch, so its epsilon ceiling is not a total disclosure bound — a counterparty that waits for the next epoch spends it again. Declare a MaxEmissions or a NotAfter bound, or meter the feed on PerpetualBudget"
    ]

    /// Start (or adopt) a feed. The audited operator act that makes a
    /// spec a running feed.
    ///
    /// Refuses a spec this composition cannot honour, and refuses to
    /// adopt an existing cursor whose authorisation has moved: the
    /// counters — emissions made, ε spent — belong to the agreement the
    /// parties signed, and letting an edited cohort inherit them would
    /// be the cheapest possible way to reset a bound.
    ///
    /// Idempotent on an already-running feed.
    let start (deps: SignalFeedDeps) (spec: SignalFeedSpec) : Async<Result<FeedState, string>> = async {
        match validate deps spec with
        | _ :: _ as errors -> return Error(String.concat "; " errors)
        | [] ->
            let authorisationId = SignalFeedSpec.authorisationId spec
            let version = TemplateCanonical.version (SignalFeedSpec.template spec)
            let! loaded = deps.State.Load spec.FeedId

            match loaded with
            | Error reason -> return Error reason
            | Ok(Some existing) when existing.AuthorisationVersion <> version ->
                return
                    Error
                        $"feed '{spec.FeedId}' already exists under a different authorisation ({existing.AuthorisationVersion}); a changed cohort, purpose or destination is a new agreement and needs a new feed id, not the emission count and epsilon spend of the one the parties signed"
            | Ok(Some existing) ->
                match existing.Status with
                | FeedRunning ->
                    do! record deps (row deps spec false $"feed '{spec.FeedId}' was already running")
                    return Ok existing
                | FeedPaused _
                | FeedStopped _ ->
                    return
                        Error
                            $"feed '{spec.FeedId}' is halted; use SignalFeed.resume to restart a paused feed, which re-runs the approval check and the budget on its next tick"
            | Ok None ->
                let fresh = {
                    FeedId = spec.FeedId
                    AuthorisationId = authorisationId
                    AuthorisationVersion = version
                    Status = FeedRunning
                    EmissionCount = 0
                    EpsilonSpent = 0m
                    LastEpsilon = 0m
                    LastSequence = 0L
                    LastEmittedAt = None
                    Pending = None
                    Revision = 0L
                }

                let! saved = deps.State.Save fresh

                match saved with
                | Error FeedStateConflict ->
                    return Error $"feed '{spec.FeedId}' was started concurrently by another writer"
                | Error(FeedStateUnavailable reason) -> return Error reason
                | Ok state ->
                    do!
                        record
                            deps
                            (row
                                deps
                                spec
                                false
                                $"feed '{spec.FeedId}' started for purpose '{spec.Authorisation.Purpose.PurposeId}' to destination '{spec.Authorisation.Destination.DestinationId}' under authorisation {authorisationId}, bounded at {spec.EpsilonCeiling} epsilon ({SignalFeedSpec.emissionBound spec} emissions at {SignalFeedSpec.epsilonPerEmission spec} each)")

                    return Ok state
    }

    /// Move a feed to a halted status. The shared tail of every operator
    /// act and every governance stop, so a halt is recorded exactly once
    /// and in one vocabulary.
    let private halt
        (deps: SignalFeedDeps)
        (spec: SignalFeedSpec)
        (state: FeedState)
        (status: FeedStatus)
        (reason: string)
        : Async<Result<FeedState, string>> =
        async {
            let! saved = deps.State.Save { state with Status = status }

            match saved with
            | Error FeedStateConflict ->
                return Error $"feed '{spec.FeedId}' was updated concurrently by another writer; re-read and retry"
            | Error(FeedStateUnavailable failure) -> return Error failure
            | Ok next ->
                do! record deps (row deps spec false reason)
                return Ok next
        }

    let private load (deps: SignalFeedDeps) (spec: SignalFeedSpec) = async {
        let! loaded = deps.State.Load spec.FeedId

        match loaded with
        | Error reason -> return Error reason
        | Ok None ->
            return
                Error
                    $"feed '{spec.FeedId}' has not been started; SignalFeed.start is the audited act that creates its cursor"
        | Ok(Some state) -> return Ok state
    }

    /// Pause a running feed. An audited operator act; resumable.
    let pause (deps: SignalFeedDeps) (spec: SignalFeedSpec) (reason: string) : Async<Result<FeedState, string>> = async {
        let! loaded = load deps spec

        match loaded with
        | Error e -> return Error e
        | Ok state ->
            match state.Status with
            | FeedStopped _ -> return Error $"feed '{spec.FeedId}' is stopped and cannot be paused"
            | FeedRunning
            | FeedPaused _ ->
                let at = deps.Now()

                return!
                    halt
                        deps
                        spec
                        state
                        (FeedPaused(FeedOperatorPaused reason, at))
                        $"feed '{spec.FeedId}' paused by operator — {reason}"
    }

    /// Resume a paused feed. An audited operator act.
    ///
    /// It re-checks nothing itself, deliberately: the next tick re-runs
    /// invariant 0 and the budget, so resuming a feed whose approval is
    /// still revoked or whose ceiling is still spent simply pauses it
    /// again. Re-checking here as well would be a second gate to keep in
    /// step with the first.
    let resume (deps: SignalFeedDeps) (spec: SignalFeedSpec) : Async<Result<FeedState, string>> = async {
        let! loaded = load deps spec

        match loaded with
        | Error e -> return Error e
        | Ok state ->
            match state.Status with
            | FeedRunning -> return Ok state
            | FeedStopped(reason, _) ->
                return
                    Error
                        $"feed '{spec.FeedId}' is stopped (%A{reason}) and cannot be resumed; a spent bound is a spent agreement, so a further feed is a further authorisation"
            | FeedPaused(reason, _) ->
                let! saved = deps.State.Save { state with Status = FeedRunning }

                match saved with
                | Error FeedStateConflict ->
                    return Error $"feed '{spec.FeedId}' was updated concurrently by another writer; re-read and retry"
                | Error(FeedStateUnavailable failure) -> return Error failure
                | Ok next ->
                    do!
                        record
                            deps
                            (row
                                deps
                                spec
                                false
                                $"feed '{spec.FeedId}' resumed by operator from %A{reason}; the next tick re-runs the approval check and the budget")

                    return Ok next
    }

    /// Stop a feed for good. An audited operator act; terminal.
    let stop (deps: SignalFeedDeps) (spec: SignalFeedSpec) (reason: string) : Async<Result<FeedState, string>> = async {
        let! loaded = load deps spec

        match loaded with
        | Error e -> return Error e
        | Ok state ->
            let at = deps.Now()

            return!
                halt
                    deps
                    spec
                    state
                    (FeedStopped(FeedOperatorStopped reason, at))
                    $"feed '{spec.FeedId}' stopped by operator — {reason}"
    }

    /// The operator read-out: status, emissions made, ε remaining, and
    /// the last emission's ε.
    ///
    /// The ε figures come from the LEDGER, not from the feed's own
    /// tally, because the ledger is what actually refuses an emission —
    /// an inspect that reported a number the enforcement does not use
    /// would be a dashboard rather than an audit.
    let inspect (deps: SignalFeedDeps) (spec: SignalFeedSpec) : Async<Result<FeedInspection, string>> = async {
        let! loaded = load deps spec

        match loaded with
        | Error e -> return Error e
        | Ok state ->
            let! reading, remaining = remainingEpsilon deps spec

            return
                Ok {
                    FeedId = state.FeedId
                    Status = state.Status
                    EmissionCount = state.EmissionCount
                    EmissionBound = SignalFeedSpec.emissionBound spec
                    MaxEmissions = spec.MaxEmissions
                    NotAfter = spec.NotAfter
                    EpsilonCeiling = spec.EpsilonCeiling
                    EpsilonCommitted = reading.EpsilonCommitted
                    EpsilonReserved = reading.EpsilonReserved
                    EpsilonRemaining = remaining
                    LastEpsilon = state.LastEpsilon
                    LastEmittedAt = state.LastEmittedAt
                    NextDueAt = state.LastEmittedAt |> Option.map (fun at -> at.Add spec.Cadence)
                    Pending = state.Pending
                }
    }

    /// Deliver one emission and settle the cursor. Shared by the fresh
    /// path and the retry path so a re-delivery cannot take a route the
    /// first attempt did not.
    let private deliver (deps: SignalFeedDeps) (spec: SignalFeedSpec) (state: FeedState) (emission: FeedEmission) = async {
        let! delivered = deps.Sink.Deliver emission

        match delivered with
        | Error reason ->
            // The emission stays pending. It is durable, it has
            // already been charged, and the next tick re-sends this
            // exact value — a fresh draw would hand the recipient a
            // second independent sample of one true number.
            do!
                record
                    deps
                    (emissionRow
                        deps
                        spec
                        emission
                        false
                        $"emission {emission.Sequence} of feed '{spec.FeedId}' could not be delivered and remains pending — {reason}")

            return FeedEmissionRefused(ActivationDeliveryFailed reason)
        | Ok() ->
            let! saved = deps.State.Save { state with Pending = None }

            match saved with
            | Error FeedStateConflict ->
                // Delivered, but another writer moved the cursor.
                // Reporting the delivery is the honest answer: it
                // happened, and the partner dedupes the re-send the
                // stale cursor will cause.
                return FeedEmitted emission
            | Error(FeedStateUnavailable reason) ->
                return
                    FeedUnavailable
                        $"emission {emission.Sequence} of feed '{spec.FeedId}' was delivered but its cursor could not be cleared, so it will be re-sent under the same idempotency key — {reason}"
            | Ok _ ->
                do!
                    record
                        deps
                        (emissionRow
                            deps
                            spec
                            emission
                            true
                            $"emitted signal {emission.Sequence} of feed '{spec.FeedId}' over cohort '{spec.Authorisation.Cohort.CohortId}' for purpose '{spec.Authorisation.Purpose.PurposeId}' to destination '{spec.Authorisation.Destination.DestinationId}' at {emission.Epsilon} epsilon under authorisation {emission.AuthorisationId}")

                return FeedEmitted emission
    }

    /// Classify a gate withhold into the operator's three cases.
    ///
    /// Runs only on the refusal path, never on the path that released
    /// (GP 13). The gate stays the sole enforcer: deleting this changes
    /// nothing about whether an emission releases, only what the
    /// operator is told about why it did not.
    let private classify
        (deps: SignalFeedDeps)
        (spec: SignalFeedSpec)
        (context: PeerCallContext)
        (templateId: string)
        : Async<Choice<FeedHalt, ActivationRefusal>> =
        async {
            let template = SignalFeedSpec.template spec
            let! verdict = deps.Approval context template

            match verdict with
            // Revocation, expiry and every edit land here — and this is
            // the whole of "revocation stops a RUNNING feed": the check
            // is re-asked from the registry on every emission, so a
            // withdrawal takes effect on the very next tick.
            | Error reason -> return Choice1Of2(FeedUnauthorised reason)
            | Ok() ->
                let! _, remaining = remainingEpsilon deps spec
                let per = SignalFeedSpec.epsilonPerEmission spec

                if remaining < per then
                    return
                        Choice1Of2(
                            FeedBudgetExhausted
                                $"feed '{spec.FeedId}' has {remaining} epsilon remaining of a {spec.EpsilonCeiling} ceiling and one emission costs {per}; the feed is paused rather than degraded, because emitting un-noised or at a smaller epsilon when the budget runs out is the one behaviour that would make the arrangement worse than not having it"
                        )
                else
                    // Sub-floor this tick, or an answer the gate could
                    // not check. Not a governance stop — the feed stays
                    // running and the next sample may clear.
                    return Choice2Of2(ActivationWithheld templateId)
        }

    /// One fresh emission: bounds, cadence, the drift check, the gated
    /// dispatch, invariant 5, the commit and the delivery.
    ///
    /// Split from `emit` so the pending re-delivery path stays visibly
    /// separate — the two are different acts, and a reader should not
    /// have to follow a nested branch to see that a retry takes no
    /// sample and spends no ε.
    let private emitFresh
        (deps: SignalFeedDeps)
        (spec: SignalFeedSpec)
        (state: FeedState)
        (now: DateTimeOffset)
        : Async<FeedTick> =
        async {
            let authorisationId = SignalFeedSpec.authorisationId spec

            // The declared bounds, checked before any epsilon is spent:
            // an emission the feed is no longer entitled to make must
            // not cost the budget it was refused for.
            let bounded =
                if spec.MaxEmissions |> Option.exists (fun cap -> state.EmissionCount >= cap) then
                    Some(FeedVolumeReached state.EmissionCount)
                elif spec.NotAfter |> Option.exists (fun notAfter -> now > notAfter) then
                    Some(FeedWindowClosed(Option.get spec.NotAfter))
                else
                    None

            match bounded with
            | Some halted ->
                let! _ =
                    halt
                        deps
                        spec
                        state
                        (FeedStopped(halted, now))
                        $"feed '{spec.FeedId}' stopped — the declared bound is spent (%A{halted})"

                return FeedHalted halted
            | None ->
                let due =
                    match state.LastEmittedAt with
                    | Some at -> at.Add spec.Cadence
                    | None -> now

                if now < due then
                    return FeedNotDue due
                // The live sink must be the one that was approved. A seam
                // whose descriptor has drifted from the signed one is not
                // an edge case, it is the whole attack in miniature — and
                // on a CONTINUOUS feed it is worse than on a one-shot
                // activation, because it would redirect every future
                // emission too.
                elif deps.Sink.Descriptor <> spec.Authorisation.Destination then
                    let halted =
                        FeedUnauthorised
                            $"the sink for feed '{spec.FeedId}' presents a descriptor that differs from the authorised one, so the approval does not cover the destination this feed would reach"

                    let! _ = halt deps spec state (FeedPaused(halted, now)) $"feed '{spec.FeedId}' paused — %A{halted}"

                    return FeedHalted halted
                else
                    let sequence = state.LastSequence + 1L
                    let context = contextFor spec sequence
                    let template = SignalFeedSpec.template spec
                    let shapeName = ActivationShape.name spec.Shape

                    // Out-of-band carrier for the typed refusal the
                    // dispatch computed. `PeerDispatch` answers with a
                    // serialised string and a `PeerError`, which is the
                    // right contract for a peer call and the wrong one
                    // for a typed refusal. A local, not module state —
                    // the feed holds nothing between ticks (GP 12 rule
                    // 4).
                    let refusal = ref None

                    let dispatch: PeerDispatch =
                        fun _ _ _ -> async {
                            let! sampled = deps.Signal.Sample(authorisationId, sequence)

                            match sampled with
                            | Error reason ->
                                refusal.Value <- Some(ActivationCohortUnresolved reason)
                                return Error(PeerHandler reason)
                            | Ok result ->
                                let expected = ActivationShape.outputShape spec.Shape

                                if result.Shape <> expected then
                                    // Checked here rather than left to
                                    // the broker: the broker asks whether
                                    // the shape is PERMITTED, which is a
                                    // weaker question than whether it is
                                    // the shape this feed's destination
                                    // was approved to receive.
                                    let reason =
                                        $"the signal source answered with shape %A{result.Shape} for a feed declared as '{shapeName}', which expects %A{expected}"

                                    refusal.Value <- Some(ActivationEgressRefused reason)
                                    return Error(PeerHandler reason)
                                else
                                    return Ok(JsonRpc.serialize result)
                        }

                    let registration: PeerContractRegistration = {
                        ContractId = spec.Authorisation.Destination.DestinationId
                        Versions = [ v1 ]
                        Dispatch = dispatch
                    }

                    // The single road. Invariants 0 → 4, applied by the
                    // same wrapper every gated peer contract goes through
                    // — and re-applied on EVERY emission, which is why a
                    // revocation stops a RUNNING feed.
                    let gated =
                        (CleanRoomGate.wrapNoised
                            deps.Broker
                            template
                            (Some deps.Approval)
                            (Some(meterFor deps spec))
                            (Some(deps.Mechanism, spec.Noise))
                            deps.Audit
                            registration)
                            .Registration

                    let! answer = gated.Dispatch context shapeName "[]"

                    match answer with
                    | Error(PeerCleanRoomWithheld templateId) ->
                        let! classified = classify deps spec context templateId

                        match classified with
                        | Choice1Of2 halted ->
                            let! _ =
                                halt
                                    deps
                                    spec
                                    state
                                    (FeedPaused(halted, now))
                                    $"feed '{spec.FeedId}' paused — %A{halted}"

                            return FeedHalted halted
                        | Choice2Of2 typed ->
                            do!
                                record
                                    deps
                                    (row
                                        deps
                                        spec
                                        false
                                        $"emission {sequence} of feed '{spec.FeedId}' was withheld by the clean-room gate; the feed is still running")

                            return FeedEmissionRefused typed
                    | Error other ->
                        let typed =
                            refusal.Value
                            |> Option.defaultValue (ActivationDeliveryFailed $"the feed dispatch failed: %A{other}")

                        do! record deps (row deps spec false $"emission {sequence} of feed '{spec.FeedId}' — %A{typed}")

                        return FeedEmissionRefused typed
                    | Ok json ->
                        let cleared = JsonRpc.deserialize<CohortResult> json

                        match project spec cleared with
                        | Error typed ->
                            do!
                                record
                                    deps
                                    (row deps spec false $"emission {sequence} of feed '{spec.FeedId}' — %A{typed}")

                            return FeedEmissionRefused typed
                        | Ok release ->
                            let emission = {
                                FeedId = spec.FeedId
                                AuthorisationId = authorisationId
                                Sequence = sequence
                                IdempotencyKey = SignalFeedCanonical.idempotencyKey spec.FeedId authorisationId sequence
                                Release = release
                                Epsilon = SignalFeedSpec.epsilonPerEmission spec
                                EmittedAt = now
                            }

                            // Committed BEFORE delivery. The ε is already
                            // spent by the ledger at this point, so a
                            // crash here must leave a value to re-send
                            // rather than a hole — and the counters
                            // advance at PRODUCTION, so a delivery-retry
                            // loop cannot outrun the volume bound.
                            let! saved =
                                deps.State.Save {
                                    state with
                                        EmissionCount = state.EmissionCount + 1
                                        EpsilonSpent = state.EpsilonSpent + emission.Epsilon
                                        LastEpsilon = emission.Epsilon
                                        LastSequence = sequence
                                        LastEmittedAt = Some now
                                        Pending = Some emission
                                }

                            match saved with
                            | Error FeedStateConflict ->
                                // Another tick committed first. Its
                                // emission is the one that counts; this
                                // one is discarded undelivered. The ε it
                                // reserved stands, which is the safe
                                // direction — over-charged, never
                                // over-delivered.
                                return
                                    FeedUnavailable
                                        $"a concurrent tick advanced feed '{spec.FeedId}' first; this emission was discarded before delivery"
                            | Error(FeedStateUnavailable reason) ->
                                return
                                    FeedUnavailable
                                        $"feed '{spec.FeedId}' could not commit emission {sequence} and did not deliver it — {reason}"
                            | Ok next -> return! deliver deps spec next emission
        }

    /// Advance the feed by one tick.
    ///
    /// Drives everything: the cadence, the declared bounds, the pending
    /// re-delivery, and — through `CleanRoomGate.wrapNoised` — invariants
    /// 0 to 4 on every single emission. A deployment ticks this from its
    /// own scheduler; the SDK starts no timer of its own (GP 13).
    let emit (deps: SignalFeedDeps) (spec: SignalFeedSpec) : Async<FeedTick> = async {
        let! loaded = load deps spec

        match loaded with
        | Error reason -> return FeedUnavailable reason
        | Ok state ->
            match state.Status with
            | FeedPaused(halted, _)
            | FeedStopped(halted, _) -> return FeedHalted halted
            | FeedRunning ->
                // A pending emission is re-sent BEFORE anything else: it
                // is already charged and already durable, and taking a
                // fresh sample while one is in flight would spend a
                // second ε on a value the recipient has not been told
                // about.
                match state.Pending with
                | Some pending ->
                    let! outcome = deliver deps spec state pending

                    return
                        match outcome with
                        | FeedEmitted emission -> FeedRedelivered emission
                        | other -> other
                | None -> return! emitFresh deps spec state (deps.Now())
    }