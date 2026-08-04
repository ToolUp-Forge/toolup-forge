// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open ToolUp.Platform

// ─── Phase 478 — the gated-output pipeline ────────────────────────────
//
// Phase 318 gave the substrate somewhere else to run heavy work. The
// clean room gave it a floor every answer must clear. This file is the
// join: when the heavy work ran **over gated data**, its output is not a
// result the caller receives — it is a candidate the gate has not yet
// ruled on.
//
// Read `CleanRoomGate.fs`'s header first. This is Phase 311's structural
// move applied to a compute result, and it makes the same three claims in
// the same order, for the same reasons.
//
// ── The ungated payload is UNREACHABLE, not merely discouraged ──
//
// `GatedComputeOutput` has a private representation, exactly one
// construction route (`hold`), and **no accessor for its payload**. The
// only function that can see the bytes is `release`, in this file, and
// `release` runs the gate before it returns anything. There is no
// `.Payload`, no `.Value`, no `tryGet`, and no overload that answers
// "just give me the raw result" — because a pipeline that ships one is a
// pipeline whose gate is advisory, and the estate has watched exactly
// that shape fail twice (Phase 18b's broker sat registered and uncalled
// for months; Phase 330's `VerifyDelegation` shipped with no call site at
// all).
//
// The two members it does expose — `Handle` and `Kind` — are the
// submission's own metadata, which the caller already held before it ever
// submitted. Exposing what the caller gave you back is not a disclosure.
//
// A withheld release is a **typed refusal that carries no payload by
// construction**: every case of `ComputeReleaseRefusal` is a string or an
// `ExternalComputeError`, and there is no case with a `CohortResult` or a
// `string` payload field for one to hide in. That is deliberate — a
// refusal type with a "partial result" case is how the thing you refused
// to release gets released.
//
// ── It dispatches THROUGH the gate; it does not re-implement it ──
//
// The obvious implementation is to call `ICleanRoomBroker.Enforce` here
// and map the decision. That implementation would be a second enforcement
// path with its own idea of the ordering, its own idea of what a withhold
// is, and — the moment Phase 190 or Phase 481 moved — its own idea of the
// ε. So, following Phase 490's `CohortActivation`, `release` builds a
// synthetic `PeerContractRegistration` whose dispatch hands back the held
// payload and passes it to `CleanRoomGate.wrapNoised`. Every invariant
// the gate has — 0 bilateral approval, 0.5 the ε reservation, 1 the
// surface, 2 checkability, 3 the release post-condition, 4 calibrated
// noise — therefore applies to a compute output with no code here to keep
// in step, and a future invariant applies the day it lands.
//
// Two consequences worth naming:
//
//   * **The work kind IS the gated method.** The template's
//     `AllowedMethods` is the set of `ExternalWorkSpec.Kind` values whose
//     outputs this template releases, so invariant 1 refuses an output
//     from a kind the template never authorised — even though the work
//     already ran. Running is not releasing.
//   * **An uncheckable output is withheld, not passed through.** A worker
//     that answered with rows, a scalar, or a record of some other shape
//     produced something the floor cannot be evaluated against, and
//     invariant 2 withholds it. This is what makes row-level worker
//     output unreachable rather than merely discouraged: a worker that
//     returns rows does not bypass the gate, it fails it.
//
// ── Where the isolation posture binds, and where it does not ──
//
// `hold` refuses an output whose spec was not `Isolated`, and refuses one
// whose backend did not declare the posture. Both refusals are about
// *provenance*: they answer "was this computed somewhere that could not
// leak it", which is a question no amount of gating the output can
// answer afterwards. The gate answers the different question — "may this
// particular answer be seen" — and it runs regardless of the posture,
// because a backend's self-declaration is an assertion the substrate
// cannot verify and must not be allowed to stand in for the floor.
//
// ── Cost when unused (GP 13) ──
//
// Nothing here is composed by `PeerServerApp.run`. A deployment that
// never calls `hold` constructs none of these types and pays nothing; a
// `Standard` submission never enters this file at all, and its dispatcher
// path is byte-for-byte Phase 318's (GP 11).

/// Why a compute output could not be released.
///
/// **No case carries data.** Every field below is a diagnostic string or
/// an `ExternalComputeError`; there is deliberately no "partial result"
/// or "raw payload" case, because a refusal type with one is how the
/// thing you refused to release gets released.
type ComputeReleaseRefusal =
    /// The gate withheld the answer. The template id is all a caller
    /// gets, for the reason `PeerCleanRoomDecisionPayload` documents at
    /// length: the gate's reasons are quantitative, and a caller able to
    /// read them back while varying its query has a counting oracle over
    /// the protected data. The reason is recorded receiver-side.
    | ComputeOutputWithheld of templateId: string
    /// The spec was not `ExecutionProfile.Isolated`, so this pipeline is
    /// not the route its output travels — a `Standard` submission's
    /// result goes back to its caller exactly as it did before Phase 478.
    | ComputeOutputNotIsolated of reason: string
    /// The backend did not declare an isolation posture that honours
    /// `Isolated`, so its output has no clean-room provenance to gate.
    | ComputeOutputUntrustedBackend of ExternalComputeError
    /// The gated dispatch failed for a reason that is not a gate
    /// decision (a transport error, a handler refusal). Nothing about the
    /// protected data was produced.
    | ComputeOutputUnavailable of reason: string

/// Phase 478 — an `Isolated` worker's output, held **ungated**.
///
/// The representation is private and `GatedComputeOutput.hold` is the
/// only function that produces one, so possession of this type is proof
/// that the enclosed payload came from a spec that required isolation and
/// a backend that declared it. There is no accessor for the payload:
/// `GatedComputeOutput.release` is the only route to anything readable,
/// and it runs the clean-room gate first.
///
/// **`ToString` and `%A` are overridden, and that is load-bearing rather
/// than cosmetic.** F#'s generated structural rendering of a union prints
/// its case fields, so the default `ToString()` on this type would hand
/// the ungated payload to anything that interpolated a held value into a
/// log line, an exception message, or a debugger watch — a public
/// accessor in all but name, and the one the "there is no accessor"
/// claim would otherwise be quietly false about. Both renderings are
/// therefore pinned to `Descriptor`, which carries only the submission
/// metadata the caller already held.
[<StructuredFormatDisplay("{Descriptor}")>]
type GatedComputeOutput =
    private
    | Held of handle: ExternalHandle * kind: string * payload: string

    /// The handle the caller submitted under. Safe by construction — the
    /// caller minted the submission and already holds it.
    member this.Handle =
        match this with
        | Held(handle, _, _) -> handle

    /// The `ExternalWorkSpec.Kind` this output answers, which is the
    /// method name the gate's surface invariant checks. Safe for the same
    /// reason: the caller chose it.
    member this.Kind =
        match this with
        | Held(_, kind, _) -> kind

    /// What this value renders as, everywhere. Names the submission and
    /// says explicitly that the payload is held — never the payload.
    member this.Descriptor =
        match this with
        | Held(handle, kind, _) ->
            sprintf
                "GatedComputeOutput(kind=%s, backend=%s, handle=%O) — payload held ungated"
                kind
                handle.Backend
                handle.HandleId

    override this.ToString() = this.Descriptor

/// The substrate a gated release runs over.
///
/// A record rather than constructor parameters so a composition can carry
/// its posture as a value it can log and audit — the shape
/// `ActivationDeps` already takes, and for the same reason: the four
/// optional members mirror the gate's optional invariants exactly, so
/// `None` in each is the pre-phase behaviour of that invariant.
type GatedComputeDeps = {
    /// The privacy mechanism. Substitutable (GP 1); the substrate's own
    /// release post-condition still binds over whatever it returns.
    Broker: ICleanRoomBroker
    /// The template whose `AllowedMethods` are the work kinds this
    /// deployment releases outputs for, and whose `Floor` every released
    /// answer must clear.
    Template: CleanRoomTemplate
    /// Invariant 0 — the bilateral-approval check. `None` composes an
    /// ungoverned release, legitimate only where both parties to the
    /// computation are this deployment.
    Approval: CleanRoomApprovalCheck option
    /// Invariant 0.5 — the cumulative ε meter.
    Budget: PrivacyBudgetMeter option
    /// Invariant 4 — the calibrated-noise posture.
    Noise: (INoiseMechanism * NoisedReleasePolicy) option
    /// One row per gate decision. `GatedComputeOutput.noAudit` records
    /// nothing.
    Sink: PeerCleanRoomDecisionPayload -> Async<unit>
}

[<RequireQualifiedAccess>]
module GatedComputeDeps =

    /// The minimum composition: a mechanism, a template, and no audit.
    /// Every governance control is added explicitly, so a deployment
    /// cannot believe it has one it never composed.
    let create (broker: ICleanRoomBroker) (template: CleanRoomTemplate) : GatedComputeDeps = {
        Broker = broker
        Template = template
        Approval = None
        Budget = None
        Noise = None
        Sink = fun _ -> async { return () }
    }

    /// Invariant 0 — require a live bilateral approval of the exact
    /// template before any output is released.
    let withApproval (check: CleanRoomApprovalCheck) (deps: GatedComputeDeps) : GatedComputeDeps = {
        deps with
            Approval = Some check
    }

    /// Invariant 0.5 — meter each release against a cumulative ε budget.
    let withBudget (meter: PrivacyBudgetMeter) (deps: GatedComputeDeps) : GatedComputeDeps = {
        deps with
            Budget = Some meter
    }

    /// Invariant 4 — apply calibrated noise to the released cells.
    let withNoise
        (mechanism: INoiseMechanism)
        (policy: NoisedReleasePolicy)
        (deps: GatedComputeDeps)
        : GatedComputeDeps =
        {
            deps with
                Noise = Some(mechanism, policy)
        }

    /// Record one row per gate decision.
    let withAudit (sink: PeerCleanRoomDecisionPayload -> Async<unit>) (deps: GatedComputeDeps) : GatedComputeDeps = {
        deps with
            Sink = sink
    }

/// The one road from an `Isolated` worker's output to a caller.
[<RequireQualifiedAccess>]
module GatedComputeOutput =

    /// The audit sink a composition without an `IAuditLog` uses: the gate
    /// still decides, it simply records nothing. Explicit rather than an
    /// `option` so there is one code path, the same reason
    /// `CleanRoomGate.noAudit` exists.
    let noAudit: PeerCleanRoomDecisionPayload -> Async<unit> =
        fun _ -> async { return () }

    /// The version the synthetic contract is dispatched under. It never
    /// reaches the wire — the registration is built and called in-process
    /// — but the gate reads a context, and a context needs one.
    let private v1: ContractVersion = { Major = 1; Minor = 0 }

    /// The call context a release runs under. The "peer" is the compute
    /// backend: it is the party that produced the answer, and naming it
    /// is what lets an operator join a gate decision to the submission
    /// that caused it.
    ///
    /// `RootRequestId` is the handle id, which is stable across the
    /// submission, every poll, and the release — so one correlation id
    /// spans the whole life of the work.
    let contextFor (handle: ExternalHandle) : PeerCallContext = {
        Peer = {
            PeerId = handle.Backend
            DisplayName = handle.Backend
        }
        User = Anonymous
        ContractVersion = v1
        Route = [ handle.Backend ]
        RootRequestId = string handle.HandleId
        ParentRequestId = None
        HopsRemaining = 1
    }

    /// Hold an `Isolated` worker's output ungated. **The only route into
    /// `GatedComputeOutput`.**
    ///
    /// `posture` is the backend's declared isolation posture — normally
    /// `ExecutionProfileGate.postureOf dispatcher`. It is passed in
    /// rather than read from a dispatcher here so this stays a pure
    /// function of its arguments and the pipeline holds nothing between
    /// outputs (GP 12 rule 4).
    ///
    /// Both refusals are about **provenance**, and both are checked here
    /// rather than at release because neither can be answered afterwards:
    ///
    ///   * a `Standard` spec's output never entered a clean room, so
    ///     there is no isolation claim to gate — and routing it here
    ///     would be a behaviour change on the pre-478 path (GP 11);
    ///   * a backend that declared no posture may already have leaked the
    ///     payload before it ever answered, and gating what it hands back
    ///     would be gating the copy you can see.
    let hold
        (posture: IsolationPosture)
        (spec: ExternalWorkSpec)
        (handle: ExternalHandle)
        (payload: string)
        : Result<GatedComputeOutput, ComputeReleaseRefusal> =
        match spec.Profile with
        | ExecutionProfile.Standard ->
            Error(
                ComputeOutputNotIsolated
                    $"work kind '{spec.Kind}' was submitted under ExecutionProfile.Standard, so its output is returned to its caller directly and is not routed through the clean-room gate; submit with ExternalWorkSpec.isolated to gate the output"
            )
        | ExecutionProfile.Isolated ->
            if IsolationPosture.honours ExecutionProfile.Isolated posture then
                Ok(Held(handle, spec.Kind, payload))
            else
                Error(ComputeOutputUntrustedBackend(IsolationPosture.refusal handle.Backend posture))

    /// Release a held output through the composed clean-room gate.
    ///
    /// The whole invariant chain in one call, applied by the same wrapper
    /// every gated peer contract goes through — see the file header for
    /// why this dispatches THROUGH `CleanRoomGate` rather than
    /// re-implementing its ordering.
    ///
    /// A `Withheld` yields `ComputeOutputWithheld` carrying the template
    /// id and nothing else. The payload is not attached, not truncated,
    /// and not summarised: there is no case on `ComputeReleaseRefusal`
    /// that could carry it.
    let release
        (deps: GatedComputeDeps)
        (held: GatedComputeOutput)
        : Async<Result<CohortResult, ComputeReleaseRefusal>> =
        async {
            let handle, kind, payload =
                match held with
                | Held(handle, kind, payload) -> handle, kind, payload

            let context = contextFor handle

            // The synthetic contract. Its dispatch hands back exactly the
            // held bytes: this pipeline computes nothing and normalises
            // nothing, so what the gate evaluates is what the worker
            // produced (invariant 2 is the only thing entitled to have an
            // opinion about its shape).
            let dispatch: PeerDispatch = fun _ _ _ -> async { return Ok payload }

            let registration: PeerContractRegistration = {
                ContractId = handle.Backend
                Versions = [ v1 ]
                Dispatch = dispatch
            }

            let gated =
                (CleanRoomGate.wrapNoised
                    deps.Broker
                    deps.Template
                    deps.Approval
                    deps.Budget
                    deps.Noise
                    deps.Sink
                    registration)
                    .Registration

            let! answer = gated.Dispatch context kind "[]"

            match answer with
            | Error(PeerCleanRoomWithheld templateId) -> return Error(ComputeOutputWithheld templateId)
            | Error e -> return Error(ComputeOutputUnavailable(sprintf "the gated release failed: %A" e))
            | Ok json ->
                // Deserialising what the gate just serialised. It cleared
                // invariant 2 to get here, so this cannot be the
                // uncheckable shape — the withhold for that already
                // happened inside the gate.
                return Ok(JsonRpc.deserialize<CohortResult> json)
        }