module ToolUp.Platform.Tests.InProcess.RoundOrchestratorTests

open System
open System.Collections.Concurrent
open System.Threading
open Expecto
open ToolUp.InterPlatform
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 483 — multi-round protocol orchestrator ───────────────────
//
// Coverage of `DefaultRoundOrchestrator`: the round barrier and the
// accumulated-state thread (a three-round two-party protocol expressed as
// nothing but a step function), each `DropoutPolicy` variant against a
// participant that misses the round deadline, restart-resume from
// persisted state, and cancellation reaching the in-flight participant
// calls rather than orphaning them.
//
// No TestServer and no peer host: the orchestrator takes the wire as a
// caller-supplied thunk precisely so a protocol can be exercised without
// one, which is the same reason `PeerFederationTests` drives `IPeerFanout`
// directly. The "in-memory peer pair" here is two entries in that thunk.

let private peer id = { PeerId = id; DisplayName = id }

let private target id = {
    Peer = peer id
    BaseUrl = sprintf "https://%s.example" id
}

let private scope = "_platform"

// ─── Test doubles ─────────────────────────────────────────────────────

/// Records every audit row the observer writes. `GetAuditTrail` is never
/// exercised by the orchestrator — the observer is write-only — so it
/// answers from the same list rather than pretending to filter.
type private RecordingAuditLog() =
    let recorded = ConcurrentQueue<AuditEvent>()

    member _.Events = recorded |> List.ofSeq

    interface IAuditLog with
        member _.Record(_scopeId, audit) = async { recorded.Enqueue audit }
        member this.GetAuditTrail(_scopeId, _dateRange, _eventType) = async { return this.Events }

/// Records every notification published. Subscribe / Unsubscribe are
/// inert — the orchestrator only ever publishes.
type private RecordingNotificationChannel() =
    let published = ConcurrentQueue<string * Notification>()

    member _.Published = published |> List.ofSeq

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Enqueue(scopeId, notification) }
        member _.Subscribe(_scopeId, _handler) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_subscriptionId) = async { () }

/// An observer that always throws. The contract says a misbehaving
/// observer must not take the run down with it.
type private ThrowingRoundObserver() =
    interface IRoundObserver with
        member _.Observe(_scopeId, _event) = async { return failwith "the observer is broken" }

// ─── A protocol expressed as a step function and nothing else ─────────

/// The consumer's whole protocol: each participant is sent the current
/// round number and accumulated state, answers with an integer
/// contribution, and the fold sums them into a running total.
/// Deliberately trivial arithmetic — what is under test is that the
/// substrate threads the state, holds the barrier, and never looks inside
/// either string.
let private sumStep = {
    Payload = fun state _ -> sprintf "%d:%s" state.RoundNumber state.Accumulated
    Fold =
        fun state responses ->
            let contributed =
                responses
                |> Map.toList
                |> List.sumBy (fun (_, r) ->
                    match r with
                    | Ok v -> int v
                    | Error _ -> 0)

            let running =
                (if String.IsNullOrEmpty state.Accumulated then
                     0
                 else
                     int state.Accumulated)
                + contributed

            ContinueRun(string running)
}

/// A wire thunk that records `(peerId, payload)` for every call and
/// answers `"1"` from every participant, except those named in `slow`
/// (which sleep `delay` first, so a round deadline can elapse over them).
let private recordingCall (slow: string list) (delay: TimeSpan) =
    let seen = ConcurrentQueue<string * string>()

    let call (t: TargetPeer) (payload: string) = async {
        seen.Enqueue(t.Peer.PeerId, payload)

        if List.contains t.Peer.PeerId slow then
            do! Async.Sleep(int delay.TotalMilliseconds)

        return Ok "1"
    }

    call, (fun () -> seen |> List.ofSeq)

/// The round number a recorded payload was built for — `sumStep.Payload`
/// writes it as the `"{round}:{accumulated}"` prefix. Extracted through a
/// local binding rather than `payload.Split(':')[0]`, which F# re-parses
/// as list application after an args-carrying call.
let private roundOf (payload: string) =
    let parts = payload.Split ':'
    parts[0]

// ─── Synchronisation, not timing ──────────────────────────────────────
//
// One rule governs every deadline in this file: a deadline is either
// UNREACHABLE BY CONSTRUCTION (the straggler cannot answer at all) or
// generous enough that only a genuine failure ever spends it. A deadline
// chosen to be "long enough on my machine" is a bet against the load on
// the machine that runs it next, and this file lost that bet in forge CI.

/// Wait for a signal the assertion below it depends on. The timeout is a
/// FAILURE PATH, not a schedule: a green run never spends any of it, so
/// making it generous costs nothing, and a run that exhausts it has
/// genuinely lost the signal rather than merely been slow.
let private awaitSignal (label: string) (signal: SemaphoreSlim) = async {
    let! signalled = signal.WaitAsync(TimeSpan.FromSeconds 60.0) |> Async.AwaitTask

    if not signalled then
        failtestf "timed out waiting for %s" label
}

/// A participant delay no round in this file can outlast: the straggler's
/// answer is not "late", it never arrives. That is what makes a dropout
/// deterministic — the round deadline never has to be short to produce
/// one. The length costs no wall-clock, because the fan-out abandons the
/// sleeping call when it cancels its own token at the barrier.
let private neverAnswers = TimeSpan.FromMinutes 5.0

/// The deadline given to a round whose assertion turns on WHICH
/// participant was classified as dropped. Deliberately generous: the
/// responder must never be misclassified merely because a loaded runner
/// was slow to schedule it, and with `neverAnswers` above supplying the
/// dropout, nothing is lost by waiting. A round that drops someone spends
/// this in full, so it stays bounded.
let private classifyingDeadline = TimeSpan.FromSeconds 2.0

/// The deadline for a round that halts whichever participants answer —
/// every `AbortRun` / unmet-quorum arm below reports the same halt if the
/// responder is slow too, so a short deadline here is a cost, not a bet.
let private haltingDeadline = TimeSpan.FromMilliseconds 100.0

let private pair = [ target "a"; target "b" ]

/// The plain, unobserved orchestrator over the supplied store.
let private orchestratorWith (store: IRoundStateStore) =
    DefaultRoundOrchestrator(PeerFanout.create (), store) :> IRoundOrchestrator

let private run (orchestrator: IRoundOrchestrator) plan step call =
    orchestrator.RunRounds(plan, step, call)

let private planFor runId maxRounds =
    RoundRunPlan.create runId scope pair maxRounds

let tests =
    testList "Phase 483 IRoundOrchestrator" [

        // ─── The barrier and the accumulated-state thread ─────────────

        testCaseAsync "a three-round two-party protocol is a step function plus payloads"
        <| async {
            let call, seen = recordingCall [] TimeSpan.Zero
            let store = InMemoryRoundStateStore() :> IRoundStateStore
            let! result = run (orchestratorWith store) (planFor "run-happy" 3) sumStep call

            match result with
            | RunSucceeded state ->
                Expect.equal state.RoundNumber 3 "three rounds completed"
                Expect.equal state.Accumulated "6" "two participants × three rounds, folded by the consumer"
                Expect.equal (List.length state.Participants) 2 "the participant set is intact"
            | RunHalted(_, reason) -> failtestf "expected the run to succeed, halted with %s" reason

            let calls = seen ()
            Expect.hasLength calls 6 "each participant was called once per round"

            // The barrier: round n+1 never starts before round n has
            // folded, so the payload each participant sees carries the
            // PREVIOUS round's accumulated total.
            let payloadsByRound = calls |> List.map snd |> List.distinct |> List.sort
            Expect.equal payloadsByRound [ "1:"; "2:2"; "3:4" ] "each round's payload carries the previous fold"
        }

        testCaseAsync "a fold that finishes early completes the run before MaxRounds"
        <| async {
            let call, seen = recordingCall [] TimeSpan.Zero

            let stopAfterFirst = {
                sumStep with
                    Fold = fun _ _ -> FinishRun "done"
            }

            let store = InMemoryRoundStateStore() :> IRoundStateStore
            let! result = run (orchestratorWith store) (planFor "run-finish" 5) stopAfterFirst call

            match result with
            | RunSucceeded state ->
                Expect.equal state.RoundNumber 1 "the run stopped at the round the fold finished on"
                Expect.equal state.Accumulated "done" "the fold's terminal state is the run's result"
            | RunHalted(_, reason) -> failtestf "an early finish is a success, not a halt: %s" reason

            Expect.hasLength (seen ()) 2 "no round was fanned out after the fold finished"
        }

        testCaseAsync "a fold that aborts halts the run and leaves the last completed round durable"
        <| async {
            let call, _ = recordingCall [] TimeSpan.Zero
            let store = InMemoryRoundStateStore() :> IRoundStateStore

            let abortOnSecond = {
                sumStep with
                    Fold =
                        fun state responses ->
                            if state.RoundNumber = 2 then
                                AbortRunWith "the consumer rejected round 2"
                            else
                                sumStep.Fold state responses
            }

            let! result = run (orchestratorWith store) (planFor "run-fold-abort" 4) abortOnSecond call

            match result with
            | RunHalted(state, reason) ->
                Expect.stringContains reason "rejected round 2" "the consumer's reason is reported verbatim"
                Expect.equal state.RoundNumber 1 "the run halted holding the last COMPLETED round"
            | RunSucceeded _ -> failtest "a fold abort must halt the run"

            let! persisted = store.TryLoad(scope, "run-fold-abort")

            match persisted with
            | Some state -> Expect.equal state.RoundNumber 1 "round 1 stayed durable, so the run is resumable"
            | None -> failtest "an aborted run must still leave its last completed round persisted"
        }

        // ─── Dropout policy, one arm per variant ──────────────────────

        testCaseAsync "AbortRun halts the run when a participant misses the round deadline"
        <| async {
            let call, _ = recordingCall [ "b" ] neverAnswers
            let store = InMemoryRoundStateStore() :> IRoundStateStore

            let plan =
                planFor "run-abort" 3
                |> RoundRunPlan.withRoundTimeout haltingDeadline
                |> RoundRunPlan.withDropout AbortRun

            let! result = run (orchestratorWith store) plan sumStep call

            match result with
            | RunHalted(state, reason) ->
                Expect.stringContains reason "AbortRun" "the halt names the policy that refused to continue"
                Expect.equal state.RoundNumber 0 "no round ever completed"
            | RunSucceeded _ -> failtest "AbortRun must not tolerate a dropout"
        }

        testCaseAsync "ContinueWithout proceeds on the responders and drops the non-responder for good"
        <| async {
            // `a` must be classified as a RESPONDER here, so the deadline is
            // the generous one: a 100ms bound would misclassify it under
            // load and collapse the run to an unmet quorum.
            let call, seen = recordingCall [ "b" ] neverAnswers
            let store = InMemoryRoundStateStore() :> IRoundStateStore

            let plan =
                planFor "run-continue" 3
                |> RoundRunPlan.withRoundTimeout classifyingDeadline
                |> RoundRunPlan.withDropout (ContinueWithout 1)

            let! result = run (orchestratorWith store) plan sumStep call

            match result with
            | RunSucceeded state ->
                Expect.equal state.RoundNumber 3 "the run completed on the surviving participant"
                Expect.equal (state.Participants |> List.map _.PeerId) [ "a" ] "the non-responder left the set"
                Expect.equal state.Accumulated "3" "only a's contribution was folded, three times"
            | RunHalted(_, reason) -> failtestf "ContinueWithout 1 should have proceeded, halted with %s" reason

            // Rounds 2 and 3 must not re-invite the dropout: a peer that
            // missed round 1 no longer holds the state round 2 assumes.
            let callsToB = seen () |> List.filter (fun (id, _) -> id = "b")
            Expect.hasLength callsToB 1 "the dropped participant was called once and never re-invited"
        }

        testCaseAsync "ContinueWithout halts when too few participants respond"
        <| async {
            let call, _ = recordingCall [ "b" ] neverAnswers
            let store = InMemoryRoundStateStore() :> IRoundStateStore

            let plan =
                planFor "run-quorum" 3
                |> RoundRunPlan.withRoundTimeout haltingDeadline
                |> RoundRunPlan.withDropout (ContinueWithout 2)

            let! result = run (orchestratorWith store) plan sumStep call

            match result with
            | RunHalted(_, reason) ->
                Expect.stringContains reason "below the minimum" "the halt names the unmet participant floor"
            | RunSucceeded _ -> failtest "a quorum of 2 cannot be met by 1 responder"
        }

        testCaseAsync "WaitUntil extends the deadline once and the straggler still counts"
        <| async {
            // The straggler must ANSWER inside the grace, so the grace is a
            // failure path, not a schedule: the round completes the instant
            // both answer, and 30 seconds is only ever spent by a genuine
            // regression. The 5-second window this replaced was the same
            // shape of bet as the cancellation test's polled deadline.
            let call, seen = recordingCall [ "b" ] (TimeSpan.FromMilliseconds 300.0)
            let store = InMemoryRoundStateStore() :> IRoundStateStore

            let plan =
                planFor "run-wait" 2
                |> RoundRunPlan.withRoundTimeout (TimeSpan.FromMilliseconds 50.0)
                |> RoundRunPlan.withDropout (WaitUntil(TimeSpan.FromSeconds 30.0))

            let! result = run (orchestratorWith store) plan sumStep call

            match result with
            | RunSucceeded state ->
                Expect.equal state.RoundNumber 2 "both rounds completed under the extended deadline"
                Expect.equal state.Accumulated "4" "the straggler's contribution was folded, not discarded"
            | RunHalted(_, reason) -> failtestf "the grace window should have covered the straggler: %s" reason

            // Patience, not a retry — the extended deadline is ONE
            // fan-out, so the straggler is called once per round.
            let callsToB = seen () |> List.filter (fun (id, _) -> id = "b")
            Expect.hasLength callsToB 2 "one call per round, never a re-send"
        }

        testCaseAsync "WaitUntil still halts when the straggler misses the extended deadline"
        <| async {
            let call, _ = recordingCall [ "b" ] neverAnswers
            let store = InMemoryRoundStateStore() :> IRoundStateStore

            let plan =
                planFor "run-wait-short" 2
                |> RoundRunPlan.withRoundTimeout (TimeSpan.FromMilliseconds 50.0)
                |> RoundRunPlan.withDropout (WaitUntil(TimeSpan.FromMilliseconds 100.0))

            let! result = run (orchestratorWith store) plan sumStep call

            match result with
            | RunHalted(_, reason) ->
                Expect.stringContains reason "extended round deadline" "the halt names the extended deadline"
            | RunSucceeded _ -> failtest "a straggler beyond the grace window must abort the run"
        }

        // ─── Restart-resume ───────────────────────────────────────────

        testCaseAsync "a run resumes at the round after the last completed one"
        <| async {
            let store = InMemoryRoundStateStore() :> IRoundStateStore

            // Leg 1 — one round, then the process "restarts".
            let firstCall, firstSeen = recordingCall [] TimeSpan.Zero
            let! first = run (orchestratorWith store) (planFor "run-resume" 1) sumStep firstCall

            match first with
            | RunSucceeded state -> Expect.equal state.Accumulated "2" "round 1 folded"
            | RunHalted(_, reason) -> failtestf "leg 1 should have completed: %s" reason

            Expect.hasLength (firstSeen ()) 2 "leg 1 ran exactly one round"

            // Leg 2 — a fresh orchestrator over the same store and RunId.
            let secondCall, secondSeen = recordingCall [] TimeSpan.Zero

            let resumePlan = planFor "run-resume" 3 |> RoundRunPlan.withInitialState "999"

            let! second = run (orchestratorWith store) resumePlan sumStep secondCall

            match second with
            | RunSucceeded state ->
                Expect.equal state.RoundNumber 3 "the resumed run reached round 3, not round 1 again"

                Expect.equal
                    state.Accumulated
                    "6"
                    "the accumulated total continued from the persisted round, ignoring InitialState"
            | RunHalted(_, reason) -> failtestf "the resumed run should have completed: %s" reason

            let secondRounds =
                secondSeen () |> List.map (snd >> roundOf) |> List.distinct |> List.sort

            Expect.equal secondRounds [ "2"; "3" ] "only the outstanding rounds were re-run"
            Expect.hasLength (secondSeen ()) 4 "two rounds × two participants — round 1 was not repeated"
        }

        testCaseAsync "a run already at MaxRounds resumes as complete without calling anyone"
        <| async {
            let store = InMemoryRoundStateStore() :> IRoundStateStore
            let firstCall, _ = recordingCall [] TimeSpan.Zero
            let! _ = run (orchestratorWith store) (planFor "run-done" 2) sumStep firstCall

            let secondCall, secondSeen = recordingCall [] TimeSpan.Zero
            let! second = run (orchestratorWith store) (planFor "run-done" 2) sumStep secondCall

            match second with
            | RunSucceeded state -> Expect.equal state.RoundNumber 2 "the completed run reports its final state"
            | RunHalted(_, reason) -> failtestf "a completed run must not halt on resume: %s" reason

            Expect.isEmpty (secondSeen ()) "a finished run puts nothing back on the wire"
        }

        testCaseAsync "the blob-backed store round-trips a run's state"
        <| async {
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let store = BlobRoundStateStore(blobs) :> IRoundStateStore
            let call, _ = recordingCall [] TimeSpan.Zero
            let! result = run (orchestratorWith store) (planFor "run-blob" 2) sumStep call

            match result with
            | RunSucceeded state -> Expect.equal state.Accumulated "4" "the run completed over the durable store"
            | RunHalted(_, reason) -> failtestf "the blob-backed run should have completed: %s" reason

            let! reloaded = store.TryLoad(scope, "run-blob")

            match reloaded with
            | Some state ->
                Expect.equal state.RoundNumber 2 "round number survives the blob round-trip"
                Expect.equal state.Accumulated "4" "opaque accumulated state survives verbatim"
                Expect.equal (state.Participants |> List.map _.PeerId |> List.sort) [ "a"; "b" ] "participants survive"
            | None -> failtest "the persisted state must be readable back"

            do! store.Clear(scope, "run-blob")
            let! cleared = store.TryLoad(scope, "run-blob")
            Expect.isNone cleared "Clear reclaims the run's state"
        }

        // ─── Cancellation reaches the wire ────────────────────────────

        testCaseAsync "cancelling a run mid-round propagates cancellation to the participants"
        <| async {
            let store = InMemoryRoundStateStore() :> IRoundStateStore
            let cancelledCalls = ref 0
            let entered = new SemaphoreSlim(0)
            let observed = new SemaphoreSlim(0)

            // The probe is ARMED BEFORE entry is signalled, and that
            // ordering is the whole of this test's determinism.
            // `Async.OnCancel` registers on the call's ambient token — the
            // RUN's token, which the orchestrator threads into each
            // participant call — so by the time `entered` is released the
            // handler is provably registered and no cancellation can slip
            // past it.
            //
            // The reverse order is what made this the estate's
            // best-documented flake. Signalling first and arming a
            // `try/finally` afterwards leaves a window, and FSharp.Core's
            // `TryFinally` opens with a cancellation check that skips the
            // protected block outright when the token is already set — so a
            // cancel landing in that window never installs the
            // compensation at all. Both participants sit in that same
            // window, because the test cancels once BOTH have signalled,
            // which is why the failure was 2-expected-0-observed rather
            // than a partial count: on a loaded CI runner, and identically
            // under a 50ms preemption injected between the release and the
            // `try`.
            //
            // Falsifiability is unchanged: remove the orchestrator's
            // token-threading and `call` runs under the FAN-OUT's token
            // instead of the run's, this handler never fires, and the
            // assertion below goes red — which is exactly the propagation
            // it claims to measure.
            let call (_: TargetPeer) (_: string) = async {
                use! _armed =
                    Async.OnCancel(fun () ->
                        Interlocked.Increment(&cancelledCalls.contents) |> ignore
                        observed.Release() |> ignore)

                entered.Release() |> ignore
                do! Async.Sleep 10_000
                return Ok "1"
            }

            use cts = new CancellationTokenSource()

            let runTask =
                Async.StartAsTask(
                    run (orchestratorWith store) (planFor "run-cancel" 3) sumStep call,
                    cancellationToken = cts.Token
                )

            // Both participants are genuinely on the wire, with their
            // probes armed, before we cancel.
            do! awaitSignal "the first participant call to enter" entered
            do! awaitSignal "the second participant call to enter" entered
            cts.Cancel()

            let! outcome = Async.Catch(Async.AwaitTask runTask)

            match outcome with
            | Choice1Of2 result -> failtestf "a cancelled run must not return a result, got %A" result
            | Choice2Of2 ex ->
                Expect.isTrue
                    (ex :? OperationCanceledException
                     || (not (isNull ex.InnerException)
                         && ex.InnerException :? OperationCanceledException))
                    (sprintf "the run completes as cancelled, got %A" ex)

            // The point of the phase: the calls already on the wire were
            // cancelled, not orphaned to run their full ten seconds. Awaited
            // as signals — the polled 5-second window this replaced was a
            // second timing bet stacked on the first, and the one CI spent
            // in full before reporting 0.
            do! awaitSignal "the first participant call to observe cancellation" observed
            do! awaitSignal "the second participant call to observe cancellation" observed

            Expect.equal cancelledCalls.Value 2 "both in-flight participant calls observed the cancellation"

            let! persisted = store.TryLoad(scope, "run-cancel")
            Expect.isNone persisted "no round completed, so nothing was persisted — the run restarts from its seed"
        }

        // ─── Observability (GP 6) ─────────────────────────────────────

        testCaseAsync "every dropout decision is audited, alongside the round and the abort"
        <| async {
            let auditLog = RecordingAuditLog()
            let channel = RecordingNotificationChannel()
            let store = InMemoryRoundStateStore() :> IRoundStateStore

            let orchestrator =
                DefaultRoundOrchestrator(PeerFanout.create (), store, RoundObserver.create auditLog channel)
                :> IRoundOrchestrator

            // Same classification bet as "ContinueWithout proceeds …": the
            // assertions below count exactly one responder and exactly one
            // dropout, so `a` must not be misclassified under load.
            let call, _ = recordingCall [ "b" ] neverAnswers

            let plan =
                planFor "run-audit" 2
                |> RoundRunPlan.withRoundTimeout classifyingDeadline
                |> RoundRunPlan.withDropout (ContinueWithout 1)

            let! _ = run orchestrator plan sumStep call

            let dropped =
                auditLog.Events
                |> List.choose (function
                    | FederationParticipantDropped p -> Some p
                    | _ -> None)

            Expect.hasLength dropped 1 "the dropout produced exactly one audit row"
            Expect.equal dropped.Head.PeerId "b" "the row names the dropped participant"
            Expect.equal dropped.Head.RunId "run-audit" "the row carries the run correlation id"
            Expect.equal dropped.Head.RoundNumber 1 "the row names the round it dropped out of"

            let completed =
                auditLog.Events
                |> List.choose (function
                    | FederationRoundCompleted p -> Some p
                    | _ -> None)

            Expect.hasLength completed 2 "one row per completed round"
            Expect.equal completed.Head.RespondedCount 1 "round 1 recorded one responder"
            Expect.equal completed.Head.DroppedCount 1 "round 1 recorded one dropout"

            // Progress rides `CustomNotification` under the reserved key
            // rather than widening the platform `Notification` DU.
            let progress =
                channel.Published
                |> List.choose (fun (scopeId, n) ->
                    match n with
                    | CustomNotification(key, json) when key = RoundProgress.NotificationKey ->
                        Some(scopeId, JsonRpc.deserialize<RoundProgress> json)
                    | _ -> None)

            Expect.isGreaterThan (List.length progress) 0 "round progress reached the notification channel"

            Expect.isTrue
                (progress |> List.forall (fun (s, _) -> s = scope))
                "progress is published under the run's scope (GP 4)"

            Expect.isTrue
                (progress |> List.exists (fun (_, p) -> p.Stage = "started" && p.RoundNumber = 1))
                "a round announces itself before it fans out"

            Expect.isTrue
                (progress
                 |> List.exists (fun (_, p) -> p.Stage = "completed" && p.RespondedCount = 1))
                "a completed round reports its responder count"
        }

        testCaseAsync "an aborted run emits a RunAborted audit row naming the reason"
        <| async {
            let auditLog = RecordingAuditLog()
            let store = InMemoryRoundStateStore() :> IRoundStateStore

            let orchestrator =
                DefaultRoundOrchestrator(PeerFanout.create (), store, RoundObserver.auditOnly auditLog)
                :> IRoundOrchestrator

            let call, _ = recordingCall [ "b" ] neverAnswers

            let plan =
                planFor "run-aborted-audit" 2
                |> RoundRunPlan.withRoundTimeout haltingDeadline
                |> RoundRunPlan.withDropout AbortRun

            let! _ = run orchestrator plan sumStep call

            let aborted =
                auditLog.Events
                |> List.choose (function
                    | FederationRunAborted p -> Some p
                    | _ -> None)

            Expect.hasLength aborted 1 "the abort produced exactly one audit row"
            Expect.equal aborted.Head.RunId "run-aborted-audit" "the row carries the run correlation id"
            Expect.stringContains aborted.Head.Reason "AbortRun" "the row explains why the run stopped"

            Expect.isEmpty
                (auditLog.Events
                 |> List.filter (function
                     | FederationRoundCompleted _ -> true
                     | _ -> false))
                "no round completed, so no round-completed row was emitted"
        }

        testCaseAsync "a broken observer never takes the run down with it"
        <| async {
            let store = InMemoryRoundStateStore() :> IRoundStateStore

            let orchestrator =
                DefaultRoundOrchestrator(PeerFanout.create (), store, ThrowingRoundObserver()) :> IRoundOrchestrator

            let call, _ = recordingCall [] TimeSpan.Zero
            let! result = run orchestrator (planFor "run-bad-observer" 2) sumStep call

            match result with
            | RunSucceeded state -> Expect.equal state.Accumulated "4" "observation is best-effort, never a gate"
            | RunHalted(_, reason) -> failtestf "a throwing observer must not halt the run: %s" reason
        }

        testCaseAsync "the zero-config orchestrator runs a protocol with nothing composed (GP 13)"
        <| async {
            let call, _ = recordingCall [] TimeSpan.Zero
            let orchestrator = RoundOrchestrator.create ()
            let! result = orchestrator.RunRounds(planFor "run-default" 2, sumStep, call)

            match result with
            | RunSucceeded state ->
                Expect.equal state.Accumulated "4" "no store, no observer, no substrate wired — and it still runs"
            | RunHalted(_, reason) -> failtestf "the default orchestrator should have completed: %s" reason
        }

        // ─── The progress projection ──────────────────────────────────

        testCase "RoundProgress projects every RoundEvent onto the flat wire shape"
        <| fun () ->
            let started = RoundProgress.ofEvent (RoundStarted("r", 1, [ "a"; "b" ]))
            Expect.equal started.Stage "started" "stage label"
            Expect.equal started.ParticipantCount 2 "participants counted, not enumerated"

            let completed = RoundProgress.ofEvent (RoundCompleted("r", 2, [ "a" ], [ "b" ]))
            Expect.equal completed.Stage "completed" "stage label"
            Expect.equal completed.RespondedCount 1 "responders counted"
            Expect.equal completed.DroppedCount 1 "dropouts counted"
            Expect.equal completed.ParticipantCount 2 "participant count is the sum of both"

            let dropped =
                RoundProgress.ofEvent (ParticipantDropped("r", 3, "b", "PeerTransport"))

            Expect.equal dropped.Stage "dropped" "stage label"
            Expect.stringContains dropped.Detail "b" "detail names the participant"

            let aborted = RoundProgress.ofEvent (RunAborted("r", 3, "no quorum"))
            Expect.equal aborted.Stage "aborted" "stage label"
            Expect.equal aborted.Detail "no quorum" "detail carries the reason"
    ]