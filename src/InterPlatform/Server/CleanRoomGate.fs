// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Runtime.ExceptionServices
open ToolUp.Platform

// ─── Phase 311 — the structurally-enforced clean-room gate ───────────
//
// Phase 18b shipped `ICleanRoomBroker` and registered it as a DI
// singleton, and nothing in the dispatch path ever called it. The gate
// therefore held exactly as well as each contract author's memory: a
// handler that forgot to invoke the broker returned row-level data with
// no error, no warning, and a passing build. That is an advisory gate
// wearing a structural gate's documentation, and the estate has watched
// the same shape fail before — Phase 330's `VerifyDelegation` shipped
// with no call site and sat inert for months, because "the mechanism
// exists" and "the mechanism runs" are different claims.
//
// This file closes the gap by moving the gate to the one seam every
// answer must cross. `CleanRoomGate.wrap` takes a contract's
// `PeerContractRegistration` — whose `Dispatch` closure is the ONLY
// route from a handler's answer to the wire, because `IPlatformPeer`
// holds nothing else — and returns a replacement whose dispatch runs the
// gate over the result. `PeerServerApp.withCleanRoomTemplate` applies it
// at compose time. The handler is not consulted and cannot opt out: the
// gate is not a call it makes, it is the pipe its answer travels down.
//
// ── What the SUBSTRATE enforces, independently of the broker ──
//
// `ICleanRoomBroker` is a substitutable seam (GP 1 — a deployment may
// ship a differential-privacy budget instead), so "the broker enforces
// it" would put the floor back in the hands of code the composition can
// replace. Three invariants are therefore checked HERE, by the wrapper,
// on every gated answer, whatever the broker does:
//
//   1. **Surface.** A method outside `template.AllowedMethods` is
//      withheld before the broker is consulted. A broker cannot widen
//      the query surface the composition declared.
//   2. **Checkability.** An answer that does not parse as a
//      `CohortResult` is withheld. A gated method that answers in some
//      other shape has produced something the floor cannot be evaluated
//      against — and the failure mode of a privacy gate must be silence,
//      not "release the bit I could not check". This is what makes
//      row-level data unreachable rather than merely discouraged: a
//      handler that returns rows does not bypass the gate, it fails it.
//   3. **Release post-condition.** A `Released` decision is re-checked
//      against the composed floor with
//      `PrivacyGate.isStricterOrEqual template.Floor
//       (PrivacyGate.observed released)` — every released cell clears
//      the suppression threshold, the released cohort clears k, and the
//      shape is permitted. A broker that releases below the floor is
//      overridden and the override is audited.
//
// Between (1) and (3) sits the broker's own `Enforce`, which is where
// the suppression + gate-composition MECHANISM lives and where a
// deployment's substituted mechanism gets to act. The substrate's job is
// not to reimplement it but to make sure its output cannot be worse than
// the composed floor.
//
// ── The witness type ──
//
// `GatedContractRegistration` has a private representation and exactly
// one construction route, `wrap`, whose signature demands an
// `ICleanRoomBroker` and a `CleanRoomTemplate`. A value of that type is
// therefore *evidence* that the gate is installed — there is no way to
// mint one from a bare registration, so "gated" is a fact about the
// type rather than a claim in a comment. `CleanRoomGateTests` asserts
// the absence of any other public route by reflection over the
// assembly's public surface, which is a stronger statement than any
// number of tests showing that one particular caller remembered.
//
// ── Cost when unused (GP 13) ──
//
// A composition that never calls `withCleanRoomTemplate` never reaches
// this file: `PeerCompose.run` wraps nothing, registers the same
// `PeerContractRegistration` values it registered before, and an
// ungated contract's dispatch is byte-for-byte the closure Phase 18
// built. There is no middleware, no allocation, and no per-call branch
// on an ungated path.

/// Phase 480 — the bilateral-approval pre-check a gated dispatch runs
/// before anything else: given the validated call context and the LIVE
/// template the composition is about to enforce, decide whether both
/// parties have a live, signed approval of that exact template content.
///
/// A function rather than an interface because it is a decision, not a
/// service: the substrate needs one answer per call and holds nothing
/// between them (GP 12 rule 4). `Error` carries the audit-facing reason,
/// which is recorded receiver-side and never sent on the wire.
///
/// The implementation the SDK ships is `TemplateApprovalGate.check` over
/// a composed `ITemplateApprovalRegistry`; a deployment whose consent
/// record lives somewhere else supplies its own.
type CleanRoomApprovalCheck = PeerCallContext -> CleanRoomTemplate -> Async<Result<unit, string>>

/// Phase 311 — a `PeerContractRegistration` that has been through the
/// clean-room gate wrapper.
///
/// The representation is private and `CleanRoomGate.wrap` is the only
/// function that produces one, so possession of this type is proof that
/// the enclosed dispatch runs `ICleanRoomBroker.Enforce` plus the
/// substrate's own surface / checkability / post-condition invariants.
/// It exists so the compose path cannot accidentally register a contract
/// it *believes* is gated: there is no such value to hand it.
type GatedContractRegistration =
    private
    | Gated of PeerContractRegistration

    /// The gated registration, ready for `IPlatformPeer.RegisterContract`.
    /// Reading it out is safe by construction — the only registration
    /// that can be inside is one `wrap` built.
    member this.Registration =
        match this with
        | Gated registration -> registration

/// Wraps a peer contract's dispatch in its composed clean-room gate.
[<RequireQualifiedAccess>]
module CleanRoomGate =

    /// The audit sink a host without an `IAuditLog` composes: the gate
    /// still decides, it simply records nothing. Kept explicit rather
    /// than an `option` so the wrapper has one code path and a partial
    /// test host exercises the same one production does.
    let noAudit: PeerCleanRoomDecisionPayload -> Async<unit> =
        fun _ -> async { return () }

    /// Record a decision without ever letting the recording fail the
    /// call. Best-effort on the same terms as the host's
    /// `PeerCallCompleted` row: an audit sink that is down must not turn
    /// a released answer into an error, and must not turn a withheld one
    /// into a leak either — the decision has already been made by the
    /// time this runs.
    ///
    /// Cancellation propagates rather than being swallowed, matching
    /// `JsonRpcPeerHost.authenticate`: a disconnected caller is not a
    /// failed audit write, and pretending otherwise lies in the log.
    let private record (sink: PeerCleanRoomDecisionPayload -> Async<unit>) (payload: PeerCleanRoomDecisionPayload) = async {
        try
            do! sink payload
        with ex when not (ex :? OperationCanceledException) ->
            ()
    }

    /// Parse a dispatch result back into the gate-checkable shape.
    ///
    /// `PeerDispatch` hands back an already-serialised answer, so the
    /// gate's input is JSON — the reflection host serialised whatever
    /// the handler's return type was. Anything that is not a
    /// `CohortResult` (a row list, a bare scalar, `null`, a record of a
    /// different shape) yields `None` and is withheld by the caller.
    ///
    /// The explicit null checks are load-bearing, not defensive noise:
    /// the converter set fills an absent `Cells` field with `null`, and
    /// `null` is not a legal F# list — so a JSON object that happens to
    /// deserialise without throwing can still be a shape the gate cannot
    /// evaluate. Treating it as checkable would release it.
    let private tryParseCohort (json: string) : CohortResult option =
        try
            let parsed = JsonRpc.deserialize<CohortResult> json

            if isNull (box parsed) || isNull (box parsed.Cells) || isNull (box parsed.Shape) then
                None
            else
                Some parsed
        with _ ->
            None

    /// The wire refusal every withhold collapses to. One value, so no
    /// path can accidentally answer with a chattier one.
    let private withheld (template: CleanRoomTemplate) =
        Error(PeerCleanRoomWithheld template.TemplateId)

    /// Phase 480 — install `template`'s gate on `registration`'s
    /// dispatch, optionally behind a bilateral-approval pre-check.
    ///
    /// Every method the contract exposes is gated — the template's
    /// `AllowedMethods` decides which are *answerable*, not which are
    /// checked, so there is no per-method escape hatch and adding a
    /// method to a gated contract cannot quietly add an ungated one.
    ///
    /// `approval` is `None` on a composition that declares no approval
    /// registry, and that path is the pre-480 `wrap` exactly: one
    /// `option` match and then Phase 311's invariants, with no store
    /// read and nothing allocated (GP 11 / GP 13). `Some check` makes
    /// approval **invariant 0** — it runs before the surface check and
    /// therefore before the handler computes anything, because a
    /// template neither party has agreed to is not a contract this
    /// deployment answers questions under at all, and deciding otherwise
    /// after the sensitive computation would be work done for an answer
    /// that was never releasable.
    ///
    /// `sink` receives one `PeerCleanRoomDecision` payload per gated
    /// dispatch (`noAudit` to record nothing). It is passed in rather
    /// than resolved here so this stays a pure function of its arguments
    /// — the broker holds no state and neither does the wrapper (GP 12
    /// rule 4), which is what keeps a gated contract as portable as an
    /// ungated one.
    ///
    /// Phase 190 — `budget` is the cumulative ε accounting the per-query
    /// floor cannot do. `None` is every pre-190 composition exactly: no
    /// ledger call, no reservation, no branch beyond one `option` match
    /// (GP 11 / GP 13).
    ///
    /// **Where the debit sits is the design, and it is deliberately
    /// two-phase.** The reservation is taken as **invariant 0.5** —
    /// after approval, before the handler runs — so no answer can reach
    /// the wire on credit: a release that was never debited is a free
    /// query, and a crash between "released" and "debited" would mint
    /// one. It is then SETTLED once the outcome is known, so the
    /// mirror-image failure is closed too: a dispatch that errored, or
    /// answered in an uncheckable shape, returns its ε rather than
    /// quietly eroding a budget nobody spent. Whether a *withheld*
    /// answer is charged is the composition's declared
    /// `WithholdCharge` — a withhold does disclose one bit, and
    /// charging it (the default) is what stops a counterparty
    /// binary-searching a cohort size for free.
    ///
    /// A dispatch that throws leaves the reservation open on purpose:
    /// the ε is returned by the ledger's TTL reclaim rather than by a
    /// settlement written from a broken call path, which is the
    /// direction that never hands budget back for an answer that
    /// shipped.
    let wrapMetered
        (broker: ICleanRoomBroker)
        (template: CleanRoomTemplate)
        (approval: CleanRoomApprovalCheck option)
        (budget: PrivacyBudgetMeter option)
        (sink: PeerCleanRoomDecisionPayload -> Async<unit>)
        (registration: PeerContractRegistration)
        : GatedContractRegistration =

        let gatedDispatch: PeerDispatch =
            fun context methodName argsJson -> async {
                let decision
                    (released: bool)
                    (suppressed: string list)
                    (reason: string)
                    : PeerCleanRoomDecisionPayload =
                    {
                        ContractId = registration.ContractId
                        MethodName = methodName
                        TemplateId = template.TemplateId
                        CallerPeerId = context.Peer.PeerId
                        RootRequestId = context.RootRequestId
                        Released = released
                        SuppressedCells = suppressed
                        Reason = reason
                        OccurredAt = DateTimeOffset.UtcNow
                    }

                // Phase 311's invariants 1–3, verbatim. Lifted into a
                // local so Phase 480's approval check can sit in front
                // of them without either half being restated — a second
                // copy of this body is exactly how a gate acquires a
                // path that enforces slightly less.
                let enforce () = async {
                    // Invariant 1 — surface. Checked before the handler runs
                    // at all: a method off the template's declared surface is
                    // not a question this contract answers, so computing the
                    // answer and discarding it would be work done over
                    // sensitive data for nobody's benefit.
                    if not (Set.contains methodName template.AllowedMethods) then
                        do!
                            record
                                sink
                                (decision
                                    false
                                    []
                                    $"method '{methodName}' is not on clean-room template '{template.TemplateId}'")

                        return withheld template
                    else
                        let! inner = registration.Dispatch context methodName argsJson

                        match inner with
                        // The handler (or a guard above it) already refused.
                        // Nothing was produced, so there is nothing to gate
                        // and nothing to record — the host's own
                        // `PeerCallCompleted` row carries the outcome.
                        | Error e -> return Error e
                        | Ok resultJson ->
                            // Invariant 2 — checkability.
                            match tryParseCohort resultJson with
                            | None ->
                                do!
                                    record
                                        sink
                                        (decision
                                            false
                                            []
                                            $"the answer is not a gate-checkable CohortResult, so template '{template.TemplateId}' could not be evaluated against it")

                                return withheld template
                            | Some cohort ->
                                // The composed mechanism gets its say. The
                                // requested gate is `None`: the peer wire
                                // format carries no caller-supplied
                                // `PrivacyGate`, so the effective gate on
                                // this path is the composed floor. A bespoke
                                // caller that HAS a caller-requested gate
                                // still reaches `ICleanRoomBroker.Enforce`
                                // directly — that API is unchanged.
                                match broker.Enforce(template, methodName, None, cohort) with
                                | Withheld reason ->
                                    do! record sink (decision false [] reason)
                                    return withheld template
                                | Released(released, suppressedCells) ->
                                    // Invariant 3 — release post-condition.
                                    // The seam is substitutable; the floor is
                                    // not.
                                    if PrivacyGate.isStricterOrEqual template.Floor (PrivacyGate.observed released) then
                                        do! record sink (decision true suppressedCells "")
                                        return Ok(JsonRpc.serialize released)
                                    else
                                        do!
                                            record
                                                sink
                                                (decision
                                                    false
                                                    suppressedCells
                                                    $"the resolved ICleanRoomBroker released an answer that does not clear the composed floor of template '{template.TemplateId}'; the substrate withheld it")

                                        return withheld template
                }

                // Invariant 0.5 — the cumulative ε budget (Phase 190).
                // `None` is a composition that declared no meter: the
                // call goes straight to Phase 311's invariants, having
                // read no ledger and allocated nothing (GP 11 / GP 13).
                //
                // Sits AFTER approval and BEFORE the handler for the
                // same reason invariant 0 does: a template neither party
                // agreed to is not a question this deployment answers at
                // all, and a budget that cannot afford the answer is not
                // a reason to compute it. Sits before the SURFACE check
                // too, deliberately — an off-surface probe is still a
                // question asked, and under `WithholdCharged` it is
                // charged like any other refusal.
                let metered () = async {
                    match budget with
                    | None -> return! enforce ()
                    | Some meter ->
                        let held =
                            PrivacyBudgetMeter.spendFor template.TemplateId context.Peer.PeerId methodName meter

                        let! reserved = meter.Ledger.ReserveBudget(held, meter.Policy.EpsilonCeiling)

                        match reserved with
                        | BudgetRefused refusal ->
                            // The quantitative reason is recorded
                            // receiver-side only, exactly as every other
                            // withhold's is — a caller that could read
                            // back its own remaining budget while
                            // varying its query has a second oracle
                            // beside the one the k-floor already refuses.
                            let reason =
                                match PrivacyBudgetMeter.refusalDecision template.TemplateId refusal with
                                | Withheld r -> r
                                | Released _ -> ""

                            do! record sink (decision false [] reason)
                            return withheld template
                        | BudgetReserved(spend, _) ->
                            let! attempt = Async.Catch(enforce ())

                            match attempt with
                            | Choice2Of2 ex ->
                                // Leave the reservation open — the TTL
                                // reclaims it. Settling from a failed
                                // path would be the one direction that
                                // can hand budget back for an answer
                                // that shipped.
                                ExceptionDispatchInfo.Capture(ex).Throw()
                                return withheld template // unreachable; the throw above does not return
                            | Choice1Of2 outcome ->
                                let settlement =
                                    match outcome with
                                    // Released: the ε stands.
                                    | Ok _ -> SpendCommitted
                                    // Withheld by the gate. The refusal
                                    // itself discloses one bit, so
                                    // whether it is charged is declared
                                    // policy, not an SDK opinion.
                                    | Error(PeerCleanRoomWithheld _) ->
                                        match meter.Policy.WithholdCharge with
                                        | WithholdCharged -> SpendCommitted
                                        | WithholdFree ->
                                            SpendReturned
                                                "the answer was withheld and this policy charges only released answers"
                                    // Anything else is a handler or
                                    // transport failure: nothing about
                                    // the protected data was produced,
                                    // so nothing is owed.
                                    | Error _ ->
                                        SpendReturned
                                            "the gated dispatch produced no answer, so nothing about the protected data was disclosed"

                                do! meter.Ledger.RecordSpend(spend, settlement)
                                return outcome
                }

                // Invariant 0 — bilateral approval (Phase 480). `None` is
                // a composition that declared no approval registry: the
                // call goes straight to Phase 311's invariants, having
                // read no store and allocated nothing (GP 11 / GP 13).
                match approval with
                | None -> return! metered ()
                | Some check ->
                    let! approved = check context template

                    match approved with
                    | Error reason ->
                        // The reason names the missing / revoked /
                        // expired party and is recorded here only. The
                        // caller gets `PeerCleanRoomWithheld` carrying
                        // the template id, exactly as every other
                        // withhold does.
                        do! record sink (decision false [] reason)
                        return withheld template
                    | Ok() -> return! metered ()
            }

        Gated {
            registration with
                Dispatch = gatedDispatch
        }

    /// Phase 480 — install `template`'s gate on `registration`'s
    /// dispatch, optionally behind a bilateral-approval pre-check and
    /// with no cumulative ε accounting. The pre-190 shape, preserved
    /// exactly.
    ///
    /// Defined in terms of `wrapMetered` rather than beside it for the
    /// reason `wrap` is defined in terms of this: the whole argument for
    /// a structural gate is that there is ONE path, and a second
    /// implementation of "the gate" is how a path that enforces slightly
    /// less appears.
    let wrapApproved
        (broker: ICleanRoomBroker)
        (template: CleanRoomTemplate)
        (approval: CleanRoomApprovalCheck option)
        (sink: PeerCleanRoomDecisionPayload -> Async<unit>)
        (registration: PeerContractRegistration)
        : GatedContractRegistration =
        wrapMetered broker template approval None sink registration

    /// Phase 311 — install `template`'s gate on `registration`'s
    /// dispatch. The pre-480 shape, preserved exactly: no bilateral
    /// approval is required, so the gate is Phase 311's three
    /// invariants and nothing else.
    ///
    /// Defined in terms of `wrapApproved` rather than beside it so the
    /// two cannot drift — the whole argument for a structural gate is
    /// that there is one path, and two implementations of "the gate" is
    /// how a second path appears.
    let wrap
        (broker: ICleanRoomBroker)
        (template: CleanRoomTemplate)
        (sink: PeerCleanRoomDecisionPayload -> Async<unit>)
        (registration: PeerContractRegistration)
        : GatedContractRegistration =
        wrapApproved broker template None sink registration