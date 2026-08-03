// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Text
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Layer 5 — federation orchestration: multi-round runs ─────────────
//
// Iterative cross-party protocols — split-learning rounds, multi-round
// PSI, federated aggregation, round-based tree building — all rebuild the
// same loop app-side: a round barrier, a per-round deadline, a decision
// about participants that did not answer, and enough persisted state that
// a restart resumes rather than starts over. `IRoundOrchestrator` ships
// that loop once, as neutral substrate.
//
// Scope discipline (GP 1): the substrate owns the ROUND MECHANICS and
// nothing else. Payloads are opaque strings it never parses; the
// accumulated state between rounds is an opaque string it never reads;
// the decision about what a round's answers MEAN is the consumer's
// `RoundStep.Fold`. The same rule `IPeerFanout` follows for aggregation
// and `ICleanRoomBroker` follows for privacy policy: mechanism is the
// SDK's, judgement is the deployment's. A protocol expressed here is
// therefore a step function plus payloads — barriers, timeouts, dropout,
// resume, and audit come from the substrate.
//
// Zero cost when unused (GP 13): nothing here is composed by
// `PeerServerApp.run`. A deployment that never constructs an orchestrator
// allocates nothing, registers no hosted service, and stores no blob. The
// observer defaults to `RoundObserver.none`, so even a constructed
// orchestrator emits no audit row and publishes no notification until a
// deployment hands it the substrate to do so.
//
// Six portability rules (GP 12):
//   1. Identity by value — `RoundState` is a plain immutable record keyed
//      by `PeerId` strings; it holds no live handle and no `BaseUrl`, so
//      a run resumed after a redeployment picks up the CURRENT endpoints
//      from the plan rather than replaying stale ones.
//   2. Async at every boundary — `RunRounds`, the state store, and the
//      observer all return `Async<_>`.
//   3. Policy as data — the round deadline, concurrency bound, and
//      `DropoutPolicy` ride the `RoundRunPlan` record; a dropout decision
//      is reported as data (`RoundEvent`), never a callback.
//   4. Stateless between calls — every round's state arrives via
//      `IRoundStateStore`; the default orchestrator holds no cross-call
//      state, so a second instance resumes what the first left.
//   5. No cross-participant ordering — a round's calls are concurrent and
//      the response map is unordered; ordering exists only BETWEEN rounds,
//      which is the barrier's whole job.
//   6. Precision at the lower bound — the round deadline is a best-effort
//      wall-clock bound inherited from `IPeerFanout`, not a real-time
//      guarantee.

/// Where one participant stands within the round currently being run.
/// Carried on `RoundState` so a resumed run can report what the last
/// round saw, not merely that it happened.
type ParticipantStatus =
    /// Fanned out to, no answer recorded yet.
    | Awaiting
    /// Answered before the round's effective deadline.
    | Responded
    /// Did not answer in time, or answered with an error, and the run's
    /// `DropoutPolicy` classified it as dropped. `Reason` is the
    /// `PeerError` case name or the substrate's own deadline label —
    /// PII-free, suitable for the audit row.
    | Dropped of reason: string

/// The resumable state of one multi-round run, as at the end of the last
/// COMPLETED round. Identity by value (GP 12 rule 1): plain immutable
/// record, no framework types, no live handles — a distributed
/// `IRoundStateStore` can persist it verbatim.
///
/// `Accumulated` is the consumer's opaque protocol state (JSON, or
/// base64 for binary). The substrate carries it from round to round and
/// persists it; it never parses it. That is the GP 1 line — forge owns
/// the loop, the consumer owns the content.
type RoundState = {
    /// Caller-assigned stable id for the run. The key the state store
    /// resumes on, and the correlation id every audit row carries.
    RunId: string
    /// Number of COMPLETED rounds. `0` is a run that has not yet folded
    /// a round; a resumed run continues at `RoundNumber + 1`.
    RoundNumber: int
    /// The participant set going INTO the next round. Shrinks when a
    /// `ContinueWithout` policy drops a non-responder.
    Participants: PeerIdentity list
    /// Per-participant status of the last completed round, keyed by
    /// `PeerId`. Empty before the first round.
    Statuses: Map<string, ParticipantStatus>
    /// The consumer's opaque accumulated state. Never read by forge.
    Accumulated: string
    /// Wall-clock time this state was last written.
    UpdatedAt: DateTimeOffset
}

/// What to do about participants that did not answer a round before its
/// deadline. Policy as data (GP 12 rule 3) — the substrate never asks a
/// callback whether to continue.
///
/// None of the three ever RE-CALLS a participant. Re-sending a round's
/// payload would double-execute a protocol step the peer may already have
/// applied, which is the same reason `IPeerClient` does not auto-retry a
/// mutating call. `WaitUntil` therefore buys patience, not a retry.
type DropoutPolicy =
    /// Any participant missing at the deadline aborts the run. The
    /// strictest policy and the right default for a protocol whose
    /// arithmetic is only correct over the full participant set.
    | AbortRun
    /// Continue with whoever answered, provided at least
    /// `minParticipants` did; below that the run aborts. Non-responders
    /// are removed from the participant set for every SUBSEQUENT round —
    /// a dropout is not re-invited, because a peer that missed round *n*
    /// no longer holds the state round *n+1* assumes.
    | ContinueWithout of minParticipants: int
    /// Extend this round's effective deadline by `grace` before treating a
    /// non-responder as dropped; a participant still missing when the
    /// extended deadline elapses aborts the run. The effective deadline is
    /// `RoundTimeout + grace` (or `grace` alone when the plan sets no
    /// round timeout) — one deadline, one fan-out, no second call.
    | WaitUntil of grace: TimeSpan

/// The consumer's per-round protocol logic: build a payload, fold the
/// answers. Everything about payload SEMANTICS lives here — the substrate
/// treats both directions as opaque strings (GP 1).
type RoundStep = {
    /// Build the outbound payload for one participant in the round about
    /// to be fanned out. Receives the round's state (round number,
    /// participant set, the accumulated state from the previous round).
    Payload: RoundState -> TargetPeer -> string
    /// Fold the round's collected answers into the next accumulated
    /// state. Receives the round's state and the total per-participant
    /// response map — errors included, so a protocol that tolerates a
    /// specific failure can say so rather than being told by the policy.
    Fold: RoundState -> Map<PeerIdentity, Result<string, PeerError>> -> RoundOutcome
}

/// The consumer's verdict on a folded round.
and RoundOutcome =
    /// Carry `accumulated` into the next round. The run still stops at
    /// `RoundRunPlan.MaxRounds`.
    | ContinueRun of accumulated: string
    /// The protocol reached its own terminal condition early. The run
    /// completes successfully at this round.
    | FinishRun of accumulated: string
    /// The consumer rejects this round; the run aborts. The state as at
    /// the last COMPLETED round survives in the store, so the run is
    /// still resumable once whatever caused the rejection is fixed.
    | AbortRunWith of reason: string

/// Everything a run needs that is not protocol logic. Policy as data
/// (GP 12 rule 3): a run is fully described by this record plus a
/// `RoundStep`, so it can be logged, diffed, or reconstructed.
type RoundRunPlan = {
    /// Caller-assigned stable id. Two `RunRounds` calls with the same
    /// `RunId` and `ScopeId` are the SAME run — the second resumes the
    /// first. That is the resume contract, and it is why the id is the
    /// caller's to choose rather than minted here.
    RunId: string
    /// Storage scope the run's state is persisted under, and the scope
    /// audit rows and progress notifications are published to (GP 4).
    ScopeId: string
    /// The full participant set, with endpoints. `RoundState` carries
    /// only the identities, so the endpoints are re-read from the plan on
    /// every round — a resumed run reaches the peers' CURRENT addresses.
    Participants: TargetPeer list
    /// Upper bound on rounds. The run completes when this many rounds
    /// have folded or `RoundStep.Fold` returns `FinishRun`, whichever
    /// comes first.
    MaxRounds: int
    /// Best-effort per-round deadline. `None` = no deadline (the round
    /// barrier waits for every participant indefinitely).
    RoundTimeout: TimeSpan option
    /// Ceiling on participant calls in flight within a round — the same
    /// back-pressure lever `FanoutPolicy.MaxConcurrency` provides, which
    /// is what this rides. `None` = every participant launches at once.
    MaxConcurrency: int option
    /// What to do about participants that miss a round's deadline.
    Dropout: DropoutPolicy
    /// Seed for round 1's accumulated state. Ignored on resume — the
    /// persisted state wins, which is the point of resuming.
    InitialState: string
}

[<RequireQualifiedAccess>]
module RoundRunPlan =
    /// A plan over `participants` running at most `maxRounds` rounds:
    /// no deadline, no concurrency bound, `AbortRun` on any dropout, and
    /// an empty seed state. The strict, no-surprises starting point a
    /// caller refines with the `with` builders below.
    let create (runId: string) (scopeId: string) (participants: TargetPeer list) (maxRounds: int) : RoundRunPlan = {
        RunId = runId
        ScopeId = scopeId
        Participants = participants
        MaxRounds = maxRounds
        RoundTimeout = None
        MaxConcurrency = None
        Dropout = AbortRun
        InitialState = ""
    }

    /// Bound each round's barrier by `timeout`.
    let withRoundTimeout (timeout: TimeSpan) (plan: RoundRunPlan) : RoundRunPlan = {
        plan with
            RoundTimeout = Some timeout
    }

    /// Cap participant calls in flight within a round at `k`.
    let withMaxConcurrency (k: int) (plan: RoundRunPlan) : RoundRunPlan = { plan with MaxConcurrency = Some k }

    /// Replace the dropout policy.
    let withDropout (policy: DropoutPolicy) (plan: RoundRunPlan) : RoundRunPlan = { plan with Dropout = policy }

    /// Seed round 1's accumulated state.
    let withInitialState (state: string) (plan: RoundRunPlan) : RoundRunPlan = { plan with InitialState = state }

/// How a run finished. Both cases carry the final `RoundState`, because
/// an aborted run is still resumable and the caller needs to know which
/// round it stopped at.
type RoundRunResult =
    /// The run reached `MaxRounds` or the fold returned `FinishRun`.
    | RunSucceeded of state: RoundState
    /// The run stopped early: the dropout policy refused to continue, the
    /// fold aborted, or the run was cancelled. The state as at the last
    /// COMPLETED round is persisted and reported here.
    | RunHalted of state: RoundState * reason: string

// ─── Observability (GP 6) ─────────────────────────────────────────────

/// Round-progress events as data. The observer stream, distinct from the
/// `AuditEvent` cases it is projected onto: `RoundStarted` is progress
/// only (no audit row — a round that starts and completes would otherwise
/// double every run's audit volume for no incident-response value), while
/// the other three each map to a `Federation*` audit case.
type RoundEvent =
    /// A round's fan-out is about to leave the local deployment.
    | RoundStarted of runId: string * roundNumber: int * participants: string list
    /// A round's barrier resolved and its answers were folded.
    | RoundCompleted of runId: string * roundNumber: int * responded: string list * dropped: string list
    /// One participant was classified as dropped for this round.
    | ParticipantDropped of runId: string * roundNumber: int * peerId: string * reason: string
    /// The run stopped without reaching its completion condition.
    | RunAborted of runId: string * roundNumber: int * reason: string

/// The progress record published on the notification channel, one per
/// `RoundEvent`. A separate type from `RoundEvent` because it is a WIRE
/// shape: it is serialised into `CustomNotification`'s `payloadJson` and
/// read by a client, so it is flat, counted rather than enumerated, and
/// free of DU cases a non-F# subscriber would have to decode.
///
/// This is also the shape a job-hosted run reports progress in. Forge
/// ships no `IJobProgressSink` today — long-running jobs report progress
/// by publishing notifications through `INotificationChannel`, which is
/// exactly what `PlatformRoundObserver` does — so a run scheduled onto
/// `IJobScheduler` needs no adapter: its progress arrives on the same
/// channel, under the same key, as every other job's.
type RoundProgress = {
    RunId: string
    RoundNumber: int
    /// `"started"` / `"completed"` / `"dropped"` / `"aborted"`.
    Stage: string
    ParticipantCount: int
    RespondedCount: int
    DroppedCount: int
    /// The dropout / abort explanation; empty on `started` / `completed`.
    Detail: string
}

[<RequireQualifiedAccess>]
module RoundProgress =
    /// The `CustomNotification` key round progress is published under.
    /// Module-owned and reserved, following the platform convention that
    /// a feature-specific payload rides `CustomNotification` with a
    /// stable key rather than widening the `Notification` DU.
    [<Literal>]
    let NotificationKey = "_platform.peer.RoundProgress"

    /// Project a `RoundEvent` onto the flat wire shape.
    let ofEvent (event: RoundEvent) : RoundProgress =
        let empty = {
            RunId = ""
            RoundNumber = 0
            Stage = ""
            ParticipantCount = 0
            RespondedCount = 0
            DroppedCount = 0
            Detail = ""
        }

        match event with
        | RoundStarted(runId, round, participants) -> {
            empty with
                RunId = runId
                RoundNumber = round
                Stage = "started"
                ParticipantCount = List.length participants
          }
        | RoundCompleted(runId, round, responded, dropped) -> {
            empty with
                RunId = runId
                RoundNumber = round
                Stage = "completed"
                ParticipantCount = List.length responded + List.length dropped
                RespondedCount = List.length responded
                DroppedCount = List.length dropped
          }
        | ParticipantDropped(runId, round, peerId, reason) -> {
            empty with
                RunId = runId
                RoundNumber = round
                Stage = "dropped"
                DroppedCount = 1
                Detail = sprintf "%s: %s" peerId reason
          }
        | RunAborted(runId, round, reason) -> {
            empty with
                RunId = runId
                RoundNumber = round
                Stage = "aborted"
                Detail = reason
          }

/// Where round progress goes. A seam rather than a hard dependency on
/// `IAuditLog` + `INotificationChannel` so a deployment can route run
/// progress into its own operator surface, and — more importantly — so
/// the orchestrator costs nothing observability-wise until a deployment
/// asks for it (GP 13).
type IRoundObserver =
    /// Report one round event under `scopeId`. Best-effort by contract:
    /// an observer that throws must not fail the run, and the default
    /// orchestrator guarantees that by swallowing.
    abstract Observe: scopeId: string * event: RoundEvent -> Async<unit>

/// The default observer: audit rows through `IAuditLog`, progress
/// notifications through `INotificationChannel`. Either half is optional,
/// so a deployment with `AuditLog = NoAuditLog` still gets progress, and
/// one that wants an audit trail without client chatter gets that.
type PlatformRoundObserver(auditLog: IAuditLog option, channel: INotificationChannel option) =

    let record (scopeId: string) (event: AuditEvent) = async {
        match auditLog with
        | None -> ()
        | Some log -> do! log.Record(scopeId, event)
    }

    interface IRoundObserver with
        member _.Observe(scopeId, event) = async {
            match channel with
            | None -> ()
            | Some ch ->
                let progress = RoundProgress.ofEvent event

                do! ch.Publish(scopeId, CustomNotification(RoundProgress.NotificationKey, JsonRpc.serialize progress))

            let occurredAt = DateTimeOffset.UtcNow

            match event with
            | RoundStarted _ ->
                // Progress only — see the `RoundEvent` docs.
                ()
            | RoundCompleted(runId, round, responded, dropped) ->
                do!
                    record
                        scopeId
                        (FederationRoundCompleted {
                            RunId = runId
                            RoundNumber = round
                            ParticipantCount = List.length responded + List.length dropped
                            RespondedCount = List.length responded
                            DroppedCount = List.length dropped
                            OccurredAt = occurredAt
                        })
            | ParticipantDropped(runId, round, peerId, reason) ->
                do!
                    record
                        scopeId
                        (FederationParticipantDropped {
                            RunId = runId
                            RoundNumber = round
                            PeerId = peerId
                            Reason = reason
                            OccurredAt = occurredAt
                        })
            | RunAborted(runId, round, reason) ->
                do!
                    record
                        scopeId
                        (FederationRunAborted {
                            RunId = runId
                            RoundNumber = round
                            Reason = reason
                            OccurredAt = occurredAt
                        })
        }

[<RequireQualifiedAccess>]
module RoundObserver =
    /// Observe nothing. The default an orchestrator is built with, so a
    /// consumer that has not wired observability pays nothing (GP 13).
    let none: IRoundObserver =
        { new IRoundObserver with
            member _.Observe(_scopeId, _event) = async.Return()
        }

    /// Audit rows + progress notifications.
    let create (auditLog: IAuditLog) (channel: INotificationChannel) : IRoundObserver =
        PlatformRoundObserver(Some auditLog, Some channel) :> IRoundObserver

    /// Audit rows only — no client-visible progress.
    let auditOnly (auditLog: IAuditLog) : IRoundObserver =
        PlatformRoundObserver(Some auditLog, None) :> IRoundObserver

    /// Progress notifications only — no audit trail.
    let notifyOnly (channel: INotificationChannel) : IRoundObserver =
        PlatformRoundObserver(None, Some channel) :> IRoundObserver

// ─── Resumable state persistence ──────────────────────────────────────

/// Where a run's `RoundState` lives between rounds. Scoped per `scopeId`
/// for isolation (GP 4), mirroring `IPeerJobResultStore` and `IJobStore`.
/// Async at every boundary + identity by value (GP 12 rules 1, 2).
type IRoundStateStore =
    /// Persist the state as at the end of a completed round. Called once
    /// per round, before the round is announced — a run announced but not
    /// persisted would resume by re-running a round the participants have
    /// already applied.
    abstract Save: scopeId: string * state: RoundState -> Async<unit>

    /// Read a run's persisted state. `None` means the run has never
    /// completed a round, so `RunRounds` starts it from `InitialState`.
    abstract TryLoad: scopeId: string * runId: string -> Async<RoundState option>

    /// Discard a run's state. Not called by the orchestrator — a
    /// completed run's state is deliberately left readable so an operator
    /// can inspect what it converged to; reclaiming it is the
    /// deployment's lifecycle decision, taken through this method.
    abstract Clear: scopeId: string * runId: string -> Async<unit>

/// `IBlobStorage`-backed default. One JSON document per run under the
/// reserved `_platform` container at `peers/rounds/{scopeId}/{runId}.json`,
/// matching the `BlobPeerRegistry` / `BlobPeerJobResultStore` layout.
/// Stateless between calls (GP 12 rule 4) — every method reads or writes
/// through to the blob store, so a second instance, or a restarted
/// process, resumes exactly what the first left.
type BlobRoundStateStore(blobs: IBlobStorage) =
    let container = "_platform"
    let blobNameFor (scopeId: string) (runId: string) = $"peers/rounds/{scopeId}/{runId}.json"

    interface IRoundStateStore with
        member _.Save(scopeId: string, state: RoundState) = async {
            let payload = Encoding.UTF8.GetBytes(JsonRpc.serialize state)
            let! _ = blobs.Upload(container, blobNameFor scopeId state.RunId, payload)
            return ()
        }

        member _.TryLoad(scopeId: string, runId: string) = async {
            let! result = blobs.Download(container, blobNameFor scopeId runId)

            return
                match result with
                | Ok bytes ->
                    // A document this store cannot decode is treated as
                    // absent rather than fatal: the run restarts from its
                    // seed, which is recoverable, where a throw mid-resume
                    // would strand it.
                    try
                        Some(JsonRpc.deserialize<RoundState> (Encoding.UTF8.GetString bytes))
                    with _ ->
                        None
                | Error _ -> None
        }

        member _.Clear(scopeId: string, runId: string) = async {
            let! _ = blobs.Delete(container, blobNameFor scopeId runId)
            return ()
        }

/// In-process state store. Dev / test only — state lives in a dictionary
/// and is lost with the process, which defeats the whole point of
/// resuming. A production run uses `BlobRoundStateStore` or a
/// deployment's own durable implementation.
type InMemoryRoundStateStore() =
    let states =
        System.Collections.Concurrent.ConcurrentDictionary<string, RoundState>()

    let keyFor (scopeId: string) (runId: string) = $"{scopeId}/{runId}"

    interface IRoundStateStore with
        member _.Save(scopeId, state) = async { states[keyFor scopeId state.RunId] <- state }

        member _.TryLoad(scopeId, runId) = async {
            match states.TryGetValue(keyFor scopeId runId) with
            | true, state -> return Some state
            | false, _ -> return None
        }

        member _.Clear(scopeId, runId) = async { states.TryRemove(keyFor scopeId runId) |> ignore }

// ─── The orchestrator ─────────────────────────────────────────────────

/// Drives a multi-round cross-party protocol over the peer channel. The
/// default implementation is `DefaultRoundOrchestrator`; the interface is
/// a seam so a deployment can substitute its own barrier / scheduling
/// discipline (a gossip round, a leader-driven round) without changing
/// the consumer's step function.
type IRoundOrchestrator =
    /// Run `plan` to completion, or until it aborts. `call` is the wire:
    /// it takes a participant and that participant's payload for the
    /// current round and returns the participant's answer. The substrate
    /// supplies no transport of its own — a consumer typically closes
    /// over a `JsonRpcPeerClient` proxy here, which is what keeps the
    /// round loop free of any contract knowledge (GP 1).
    ///
    /// A run whose `RunId` already has persisted state RESUMES at the
    /// round after the last completed one; `plan.InitialState` is then
    /// ignored.
    ///
    /// **Cancellation.** Cancelling the returned computation cancels the
    /// run in the ordinary F# way — the computation completes as
    /// cancelled rather than returning a `RoundRunResult`, because an
    /// `Async` bind observes the token before any continuation of ours
    /// could run. What the substrate adds is REACH: each participant call
    /// is dispatched under the run's own token, so cancelling the run
    /// cancels the calls already on the wire instead of orphaning them.
    /// Nothing is lost — the last COMPLETED round's state was persisted
    /// before that round was announced, so the same `RunId` resumes from
    /// it.
    abstract RunRounds:
        plan: RoundRunPlan * step: RoundStep * call: (TargetPeer -> string -> Async<Result<string, PeerError>>) ->
            Async<RoundRunResult>

/// Default round orchestration over `IPeerFanout` (the round's scatter),
/// `IRoundStateStore` (resume), and `IRoundObserver` (progress + audit).
/// Holds no state between calls (GP 12 rule 4).
///
/// The dependencies arrive as EXPLICIT OVERLOADS, not optional arguments:
/// F# folds `?observer` / `?now` into one widened constructor, which
/// erases the narrower signatures from the emitted public surface — source
/// compatible, but a binary change the public-API baseline correctly reads
/// as a removal. Secondary constructors keep every shape additive (GP 11),
/// the same discipline `BlobPeerJobResultStore` adopted at Phase 316.
type DefaultRoundOrchestrator
    (fanout: IPeerFanout, store: IRoundStateStore, observer: IRoundObserver, now: unit -> DateTimeOffset) =

    /// The label a participant that never answered is dropped under.
    /// `IPeerFanout` fills an un-awaited peer with a descriptive
    /// `PeerTransport`, so the deadline case is not distinguishable from a
    /// genuine transport failure at the map — nor should it be: both mean
    /// "no answer by the barrier", which is the only thing the round loop
    /// is entitled to conclude.
    static let noAnswer = "no answer before the round deadline"

    /// The default: no observability, an in-memory store, and the system
    /// clock — the cheapest thing that runs a protocol (GP 13).
    new(fanout: IPeerFanout) =
        DefaultRoundOrchestrator(
            fanout,
            InMemoryRoundStateStore() :> IRoundStateStore,
            RoundObserver.none,
            (fun () -> DateTimeOffset.UtcNow)
        )

    /// A durable store, still unobserved.
    new(fanout: IPeerFanout, store: IRoundStateStore) =
        DefaultRoundOrchestrator(fanout, store, RoundObserver.none, (fun () -> DateTimeOffset.UtcNow))

    /// The production shape: durable state and an observer, on the system
    /// clock. The four-argument primary constructor takes the clock too,
    /// for tests that need deterministic `UpdatedAt` stamps.
    new(fanout: IPeerFanout, store: IRoundStateStore, observer: IRoundObserver) =
        DefaultRoundOrchestrator(fanout, store, observer, (fun () -> DateTimeOffset.UtcNow))

    interface IRoundOrchestrator with
        member _.RunRounds(plan, step, call) = async {
            // The run's ambient token. Threaded into the participant
            // calls below so cancelling the run reaches the wire, not
            // merely the loop that is waiting on it.
            let! runToken = Async.CancellationToken

            /// Best-effort observation: an observer that throws must not
            /// take the run down with it, exactly as `IAuditLog.Record` is
            /// documented to be fire-and-forget from the caller's view.
            let observe (event: RoundEvent) = async {
                try
                    do! observer.Observe(plan.ScopeId, event)
                with _ ->
                    ()
            }

            /// Halt the run: persist nothing new (the last completed
            /// round's state is already durable), announce, and report.
            let halt (state: RoundState) (reason: string) = async {
                do! observe (RunAborted(plan.RunId, state.RoundNumber, reason))
                return RunHalted(state, reason)
            }

            /// The round's effective deadline. `WaitUntil` is the only
            /// policy that moves it, and it moves it once — patience, not
            /// a second call.
            let deadlineFor () =
                match plan.Dropout, plan.RoundTimeout with
                | WaitUntil grace, Some timeout -> Some(timeout + grace)
                | WaitUntil grace, None -> Some grace
                | _, timeout -> timeout

            let rec runRound (state: RoundState) = async {
                if state.RoundNumber >= plan.MaxRounds then
                    return RunSucceeded state
                else
                    let roundNumber = state.RoundNumber + 1
                    let liveIds = state.Participants |> List.map _.PeerId |> Set.ofList

                    let live =
                        plan.Participants |> List.filter (fun t -> Set.contains t.Peer.PeerId liveIds)

                    if List.isEmpty live then
                        return! halt state "no participants remain"
                    else
                        // The state the consumer's `Payload` sees: this
                        // round's number and participant set, over the
                        // previous round's accumulated state.
                        let roundState = {
                            state with
                                RoundNumber = roundNumber
                                Statuses = live |> List.map (fun t -> t.Peer.PeerId, Awaiting) |> Map.ofList
                        }

                        do! observe (RoundStarted(plan.RunId, roundNumber, live |> List.map _.Peer.PeerId))

                        let policy = {
                            FanoutPolicy.all with
                                Timeout = deadlineFor ()
                                MaxConcurrency = plan.MaxConcurrency
                        }

                        // Run each participant call under the RUN's token
                        // so cancelling the run cancels the calls already
                        // on the wire, rather than orphaning them.
                        let invoke (t: TargetPeer) = async {
                            try
                                let work = call t (step.Payload roundState t)
                                return! Async.AwaitTask(Async.StartAsTask(work, cancellationToken = runToken))
                            with
                            | :? OperationCanceledException -> return Error(PeerTransport "round cancelled")
                            | :? AggregateException as ex when
                                (ex.InnerExceptions |> Seq.exists (fun i -> i :? OperationCanceledException))
                                ->
                                return Error(PeerTransport "round cancelled")
                        }

                        let! responses = fanout.Fanout(live, policy, invoke)

                        let responded =
                            responses
                            |> Map.toList
                            |> List.choose (fun (p, r) -> if Result.isOk r then Some p.PeerId else None)

                        let dropped =
                            responses
                            |> Map.toList
                            |> List.choose (fun (p, r) ->
                                match r with
                                | Ok _ -> None
                                | Error(PeerTransport msg) when
                                    msg.StartsWith("peer not awaited", StringComparison.Ordinal)
                                    ->
                                    Some(p.PeerId, noAnswer)
                                | Error e -> Some(p.PeerId, JsonRpc.errorCaseName e))

                        // Every dropout decision is audited individually,
                        // before the policy decides what to do about them
                        // — a run that aborts must still say who dropped.
                        for peerId, reason in dropped do
                            do! observe (ParticipantDropped(plan.RunId, roundNumber, peerId, reason))

                        let statuses =
                            let droppedMap =
                                dropped |> List.map (fun (id, reason) -> id, Dropped reason) |> Map.ofList

                            live
                            |> List.map (fun t ->
                                let id = t.Peer.PeerId

                                match Map.tryFind id droppedMap with
                                | Some status -> id, status
                                | None -> id, Responded)
                            |> Map.ofList

                        let foldedState = { roundState with Statuses = statuses }

                        // Apply the dropout policy. `survivors` is the
                        // participant set the NEXT round runs over.
                        let policyVerdict =
                            match plan.Dropout with
                            | _ when List.isEmpty dropped -> Ok state.Participants
                            | AbortRun ->
                                Error(
                                    sprintf
                                        "%d participant(s) dropped and the dropout policy is AbortRun"
                                        (List.length dropped)
                                )
                            | WaitUntil _ ->
                                Error(
                                    sprintf
                                        "%d participant(s) still missing after the extended round deadline"
                                        (List.length dropped)
                                )
                            | ContinueWithout minParticipants ->
                                if List.length responded >= minParticipants then
                                    Ok(state.Participants |> List.filter (fun p -> List.contains p.PeerId responded))
                                else
                                    Error(
                                        sprintf
                                            "only %d participant(s) responded, below the minimum of %d"
                                            (List.length responded)
                                            minParticipants
                                    )

                        match policyVerdict with
                        | Error reason -> return! halt state reason
                        | Ok survivors ->
                            // Folded exactly once — the consumer's
                            // step function is not promised to be
                            // pure, and calling it twice to read its
                            // verdict again would be a defect the
                            // substrate could not see.
                            let outcome = step.Fold foldedState responses

                            match outcome with
                            | AbortRunWith reason -> return! halt state reason
                            | ContinueRun accumulated
                            | FinishRun accumulated ->
                                let next = {
                                    foldedState with
                                        Participants = survivors
                                        Accumulated = accumulated
                                        UpdatedAt = now ()
                                }

                                // Durable BEFORE announced: a run that
                                // announced a round it did not persist
                                // would re-run it on resume, against
                                // participants that already applied it.
                                do! store.Save(plan.ScopeId, next)

                                do!
                                    observe (
                                        RoundCompleted(plan.RunId, roundNumber, responded, dropped |> List.map fst)
                                    )

                                match outcome with
                                | FinishRun _ -> return RunSucceeded next
                                | _ -> return! runRound next
            }

            let! persisted = store.TryLoad(plan.ScopeId, plan.RunId)

            let start =
                match persisted with
                | Some state -> state
                | None -> {
                    RunId = plan.RunId
                    RoundNumber = 0
                    Participants = plan.Participants |> List.map _.Peer
                    Statuses = Map.empty
                    Accumulated = plan.InitialState
                    UpdatedAt = now ()
                  }

            return! runRound start
        }

[<RequireQualifiedAccess>]
module RoundOrchestrator =
    /// The cheapest orchestrator that runs a protocol: the default
    /// fan-out, an in-process state store, no observability. Suitable for
    /// a single-process run whose interruption is acceptable.
    let create () : IRoundOrchestrator =
        DefaultRoundOrchestrator(PeerFanout.create ()) :> IRoundOrchestrator

    /// A durable, observed orchestrator over already-composed substrate —
    /// the production shape.
    let createWith (store: IRoundStateStore) (observer: IRoundObserver) : IRoundOrchestrator =
        DefaultRoundOrchestrator(PeerFanout.create (), store, observer) :> IRoundOrchestrator