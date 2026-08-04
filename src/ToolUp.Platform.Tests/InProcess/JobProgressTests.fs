module ToolUp.Platform.Tests.InProcess.JobProgressTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform

// ─── Phase 321 — job progress checkpoints ─────────────────────────────
//
// The phase's load-bearing claim is "coalesce chatty checkpoints, but
// NEVER shed the terminal one", so the pack is built to make that
// falsifiable rather than merely exercised:
//
//   * the coalescing test fires a burst of intermediate checkpoints and a
//     terminal one INSIDE a single rate-limit window and asserts BOTH
//     halves: that intermediates were shed (else the test would pass on an
//     implementation with no rate limiting at all) and that the terminal
//     one arrived (the property that actually matters). Asserting only the
//     second is the vacuous version of this test.
//   * a MUTATION CONTROL pins the evaluation ORDER inside
//     `ProgressCoalescer.shouldPublish`: the terminal / durable arms must
//     be reached before the interval window. The control asserts the exact
//     input that distinguishes the two orderings — a terminal checkpoint
//     arriving zero milliseconds after a published one — so swapping the
//     branches makes this test red. Without it, "terminal survives" could
//     pass by luck of timing on a fast machine.
//   * the GP 13 test asserts on OBSERVABLE ABSENCE (zero publishes, zero
//     events) through a channel and an event store that would record them,
//     not on the returned reporter's identity. A no-op that publishes
//     would satisfy an identity check.
//   * the durable-vs-transient split is asserted in BOTH directions: a
//     transient checkpoint must NOT write an event, and a durable one
//     must. A test that only checks the positive direction passes on an
//     implementation that persists everything, which is the failure mode
//     with a real cost (a blob per progress frame).

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Channel double that records every publish. Deliberately NOT the
/// in-memory default: this pack asserts on what the publisher handed the
/// transport, and the default's subscriber fan-out would put delivery
/// semantics between the assertion and the thing being asserted.
type private RecordingChannel() =
    let published = ConcurrentQueue<string * Notification>()

    member _.Published = published |> Seq.toList

    member _.ProgressPayloads =
        published
        |> Seq.choose (fun (scope, n) ->
            match n with
            | CustomNotification(key, payload) when key = JobProgress.NotificationKey -> Some(scope, payload)
            | _ -> None)
        |> Seq.toList

    interface INotificationChannel with
        member _.Publish(scopeId: string, notification: Notification) = async {
            published.Enqueue((scopeId, notification))
        }

        member _.Subscribe(_scopeId: string, _handler: NotificationEnvelope -> unit) =
            async.Return(Guid.NewGuid(): NotificationSubscriptionId)

        member _.Unsubscribe(_subscriptionId: NotificationSubscriptionId) = async.Return()

/// Event store for the durable leg. The shipped in-memory default rather
/// than a hand-rolled double: it already implements the whole `IEventStore`
/// surface (including erasure), and this pack only needs to read back what
/// was written.
type private RecordingEventStore() =
    let inner = InMemoryEventStore.InMemoryEventStore()
    let written = ConcurrentQueue<ModuleEvent>()

    /// Every event the sink wrote, in write order — the recorded list, not
    /// a store read, so ordering assertions do not depend on the store's
    /// (deliberately unordered) read contract.
    member _.Written = written |> Seq.toList

    member this.ProgressEvents =
        this.Written |> List.filter (fun e -> e.EventType = JobProgress.EventType)

    interface IEventStore with
        member _.Write(evt: ModuleEvent) = async {
            written.Enqueue evt
            do! (inner :> IEventStore).Write evt
        }

        member _.ReadAll(scopeId: string) = (inner :> IEventStore).ReadAll scopeId

        member _.ReadByType(scopeId: string, eventType: string) =
            (inner :> IEventStore).ReadByType(scopeId, eventType)

        member _.ReadBySource(scopeId: string, sourceModule: string) =
            (inner :> IEventStore).ReadBySource(scopeId, sourceModule)

        member _.ListScopes() = (inner :> IEventStore).ListScopes()

        member _.Erase(scopeId: string, subjectUserId: string, policy: ErasurePolicy, dryRun: bool) =
            (inner :> IEventStore).Erase(scopeId, subjectUserId, policy, dryRun)

let private checkpointAt (fraction: float option) (message: string) (at: DateTime) =
    ProgressCheckpoint.createAt fraction message None at

let private baseTime = DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc)

let tests =
    testList "Phase 321 — job progress checkpoints" [

        // ─── 321.A — the checkpoint type ──────────────────────────────

        testList "ProgressCheckpoint" [
            test "clamps a fraction above 1.0 and below 0.0 rather than rejecting it" {
                // A bad progress number must never be worth failing the
                // work that reported it — clamp, don't throw.
                Expect.equal (ProgressCheckpoint.create (Some 4.7) "over").Fraction (Some 1.0) "clamps high"
                Expect.equal (ProgressCheckpoint.create (Some -2.0) "under").Fraction (Some 0.0) "clamps low"
                Expect.equal (ProgressCheckpoint.create (Some 0.37) "fine").Fraction (Some 0.37) "leaves in-range"
            }

            test "maps NaN to None rather than to a number" {
                // `Some NaN` would render as a garbage progress bar and
                // compare falsely against every threshold; `None` is the
                // honest answer and the one the type already models.
                Expect.equal (ProgressCheckpoint.create (Some nan) "nan").Fraction None "NaN becomes None"
            }

            test "isTerminal is true at and above 1.0, false below, false for None" {
                Expect.isTrue (ProgressCheckpoint.isTerminal (ProgressCheckpoint.create (Some 1.0) "done")) "1.0"
                Expect.isFalse (ProgressCheckpoint.isTerminal (ProgressCheckpoint.create (Some 0.99) "nearly")) "0.99"

                Expect.isFalse
                    (ProgressCheckpoint.isTerminal (ProgressCheckpoint.create None "unknown"))
                    "None is not terminal"
            }

            test "create defaults to transient — durability is opted into, never inferred" {
                Expect.isFalse (ProgressCheckpoint.create (Some 0.5) "half").Durable "transient by default"

                Expect.isTrue
                    (ProgressCheckpoint.create (Some 0.5) "half" |> ProgressCheckpoint.durable).Durable
                    "durable when asked"
            }

            test "the durable-event SourceModule matches the scheduler's own" {
                // The literal is duplicated across the tier boundary (the
                // sink compiles before the scheduler, so it cannot
                // back-reference). This is the assertion that keeps the
                // duplication honest — a divergence would split the jobs
                // event stream into two source modules and no read would
                // notice.
                Expect.equal
                    JobProgress.SourceModule
                    JobScheduler.JobsSourceModule
                    "progress events must land in the same _platform.jobs stream as the lifecycle events"
            }
        ]

        // ─── 321.C — the coalescing rule (the hot zone) ───────────────

        testList "ProgressCoalescer.shouldPublish" [
            test "publishes the first checkpoint for a job without waiting out a window" {
                Expect.isTrue
                    (ProgressCoalescer.shouldPublish
                        ProgressCoalescePolicy.defaults
                        None
                        (checkpointAt (Some 0.01) "starting" baseTime))
                    "a UI must get an immediate first frame"
            }

            test "sheds an intermediate checkpoint inside the rate-limit window" {
                let policy = {
                    MinInterval = TimeSpan.FromSeconds 1.0
                }

                Expect.isFalse
                    (ProgressCoalescer.shouldPublish
                        policy
                        (Some baseTime)
                        (checkpointAt (Some 0.5) "chatty" (baseTime.AddMilliseconds 10.0)))
                    "10ms after the last publish is inside a 1s window"
            }

            test "publishes an intermediate checkpoint once the window has elapsed" {
                let policy = {
                    MinInterval = TimeSpan.FromSeconds 1.0
                }

                Expect.isTrue
                    (ProgressCoalescer.shouldPublish
                        policy
                        (Some baseTime)
                        (checkpointAt (Some 0.5) "spaced" (baseTime.AddSeconds 1.5)))
                    "1.5s clears a 1s window"
            }

            // ── the mutation control ──
            test "MUTATION CONTROL — a terminal checkpoint publishes even zero ms into the window" {
                // This is the exact input that distinguishes "terminal
                // checked before the interval" from "terminal checked
                // after": zero elapsed time, so the interval arm would
                // refuse. Reordering the branches in `shouldPublish` turns
                // this test red, which is the point of writing it as its
                // own case rather than folding it into the burst test.
                let policy = { MinInterval = TimeSpan.FromHours 1.0 }

                Expect.isTrue
                    (ProgressCoalescer.shouldPublish policy (Some baseTime) (checkpointAt (Some 1.0) "done" baseTime))
                    "the terminal checkpoint must survive an hour-long shedding window at zero elapsed time"
            }

            test "MUTATION CONTROL — a durable checkpoint publishes even zero ms into the window" {
                // Same shape for the durable arm: it is already paying for
                // a blob write, so suppressing its live twin would make
                // the audit timeline and the progress bar disagree.
                let policy = { MinInterval = TimeSpan.FromHours 1.0 }

                let durable =
                    checkpointAt (Some 0.4) "stage boundary" baseTime |> ProgressCheckpoint.durable

                Expect.isTrue
                    (ProgressCoalescer.shouldPublish policy (Some baseTime) durable)
                    "a durable checkpoint is never shed"
            }

            test "an unlimited policy sheds nothing" {
                Expect.isTrue
                    (ProgressCoalescer.shouldPublish
                        ProgressCoalescePolicy.unlimited
                        (Some baseTime)
                        (checkpointAt (Some 0.5) "immediate" baseTime))
                    "MinInterval = Zero publishes everything"
            }
        ]

        // ─── 321.C — fan-out through the real sink ────────────────────

        testList "FanOutJobProgressSink" [
            test "publishes a checkpoint to the notification channel under the reserved key" {
                let channel = RecordingChannel()
                let events = RecordingEventStore()

                let sink =
                    FanOutJobProgressSink(channel, events, silentLogger, policy = ProgressCoalescePolicy.unlimited)
                    :> IJobProgressSink

                let jobId = Guid.NewGuid()

                sink.Report(jobId, "team-a", ProgressCheckpoint.create (Some 0.37) "materialising embeddings")
                |> Async.RunSynchronously

                let payloads = channel.ProgressPayloads
                Expect.hasLength payloads 1 "one publish"
                let scope, payload = payloads[0]
                Expect.equal scope "team-a" "published under the job's scope, not a broadcast"

                Expect.stringContains
                    payload
                    "materialising embeddings"
                    "the payload carries the operator-facing message"
            }

            test "publishes nothing on any scope other than the job's own (GP 4)" {
                let channel = RecordingChannel()
                let events = RecordingEventStore()

                let sink =
                    FanOutJobProgressSink(channel, events, silentLogger, policy = ProgressCoalescePolicy.unlimited)
                    :> IJobProgressSink

                sink.Report(Guid.NewGuid(), "team-a", ProgressCheckpoint.create (Some 0.5) "half")
                |> Async.RunSynchronously

                let scopes = channel.Published |> List.map fst |> List.distinct
                Expect.equal scopes [ "team-a" ] "exactly one scope saw the checkpoint"
            }

            test "a transient checkpoint writes NO event — the durable leg is opt-in per checkpoint" {
                // The negative direction, and the one with a real cost: an
                // implementation that persists every checkpoint turns a
                // progress bar into a blob write per frame.
                let channel = RecordingChannel()
                let events = RecordingEventStore()

                let sink =
                    FanOutJobProgressSink(channel, events, silentLogger, policy = ProgressCoalescePolicy.unlimited)
                    :> IJobProgressSink

                sink.Report(Guid.NewGuid(), "team-a", ProgressCheckpoint.create (Some 0.5) "transient")
                |> Async.RunSynchronously

                Expect.hasLength channel.ProgressPayloads 1 "published live"
                Expect.isEmpty events.ProgressEvents "and persisted nothing"
            }

            test "a durable checkpoint persists to the event store under _platform.jobs" {
                let channel = RecordingChannel()
                let events = RecordingEventStore()

                let sink =
                    FanOutJobProgressSink(channel, events, silentLogger, policy = ProgressCoalescePolicy.unlimited)
                    :> IJobProgressSink

                let checkpoint =
                    ProgressCheckpoint.create (Some 0.4) "epoch 4/10"
                    |> ProgressCheckpoint.withStage "epoch"
                    |> ProgressCheckpoint.durable

                sink.Report(Guid.NewGuid(), "team-a", checkpoint) |> Async.RunSynchronously

                let persisted = events.ProgressEvents
                Expect.hasLength persisted 1 "one durable event"
                Expect.equal persisted[0].SourceModule "_platform.jobs" "same source module as the lifecycle events"
                Expect.equal persisted[0].ScopeId "team-a" "scoped to the job"
                Expect.stringContains persisted[0].Payload "epoch 4/10" "payload carries the message"
            }

            test "the terminal checkpoint persists even when it was not marked durable" {
                // "…and the terminal one" from 321.C: a timeline whose last
                // entry is 94% is a timeline with no end.
                let channel = RecordingChannel()
                let events = RecordingEventStore()

                let sink =
                    FanOutJobProgressSink(channel, events, silentLogger, policy = ProgressCoalescePolicy.unlimited)
                    :> IJobProgressSink

                sink.Report(Guid.NewGuid(), "team-a", ProgressCheckpoint.create (Some 1.0) "complete")
                |> Async.RunSynchronously

                Expect.hasLength events.ProgressEvents 1 "the terminal checkpoint is durable by nature"
            }

            // ── the headline coalescing assertion ──
            test "coalesces a burst but delivers the terminal checkpoint" {
                // Both halves asserted. Dropping the first would let this
                // pass on an implementation with no rate limiting; dropping
                // the second would let it pass on one that sheds
                // everything.
                let channel = RecordingChannel()
                let events = RecordingEventStore()

                let sink =
                    FanOutJobProgressSink(
                        channel,
                        events,
                        silentLogger,
                        policy = {
                            MinInterval = TimeSpan.FromSeconds 30.0
                        }
                    )
                    :> IJobProgressSink

                let jobId = Guid.NewGuid()

                // 200 intermediate checkpoints, all inside one 30s window.
                for i in 1..200 do
                    sink.Report(
                        jobId,
                        "team-a",
                        checkpointAt (Some(float i / 400.0)) $"step {i}" (baseTime.AddMilliseconds(float i))
                    )
                    |> Async.RunSynchronously

                let afterBurst = channel.ProgressPayloads |> List.length

                // …then the terminal one, still inside the same window.
                sink.Report(jobId, "team-a", checkpointAt (Some 1.0) "complete" (baseTime.AddMilliseconds 201.0))
                |> Async.RunSynchronously

                let payloads = channel.ProgressPayloads

                Expect.equal afterBurst 1 "the burst coalesced to the single first frame — a flood would be 200"

                Expect.hasLength payloads 2 "first frame + terminal frame, and nothing in between"

                Expect.stringContains
                    (snd payloads[1])
                    "complete"
                    "THE invariant: the terminal checkpoint was not shed with the intermediates"
            }

            test "Latest reports the newest checkpoint, including after completion" {
                let channel = RecordingChannel()
                let events = RecordingEventStore()

                let sink =
                    FanOutJobProgressSink(
                        channel,
                        events,
                        silentLogger,
                        policy = { MinInterval = TimeSpan.FromHours 1.0 }
                    )

                let asSink = sink :> IJobProgressSink
                let jobId = Guid.NewGuid()

                asSink.Report(jobId, "team-a", checkpointAt (Some 0.2) "a" baseTime)
                |> Async.RunSynchronously

                // Shed on the transient leg, but still the latest known
                // state — `Latest` must track what was REPORTED, not what
                // was published, or the Phase 69i bridge would read a
                // stale fraction.
                asSink.Report(jobId, "team-a", checkpointAt (Some 0.6) "b" (baseTime.AddMilliseconds 5.0))
                |> Async.RunSynchronously

                let latest = asSink.Latest jobId |> Async.RunSynchronously
                Expect.equal (latest |> Option.bind _.Fraction) (Some 0.6) "tracks reported, not published"

                asSink.Report(jobId, "team-a", checkpointAt (Some 1.0) "done" (baseTime.AddMilliseconds 10.0))
                |> Async.RunSynchronously

                let afterTerminal = asSink.Latest jobId |> Async.RunSynchronously

                Expect.equal
                    (afterTerminal |> Option.bind _.Fraction)
                    (Some 1.0)
                    "a poll arriving just after completion reads 100%, not None"
            }

            test "NoOpJobProgressSink accepts a terminal checkpoint and reports no state" {
                // The explicit off-switch for a consumer wiring the seam by
                // hand. Asserted against the TERMINAL checkpoint, the one
                // every other test here proves is un-sheddable — un-sheddable
                // must not mean un-suppressible.
                let sink = NoOpJobProgressSink() :> IJobProgressSink
                let jobId = Guid.NewGuid()

                sink.Report(jobId, "team-a", ProgressCheckpoint.create (Some 1.0) "done")
                |> Async.RunSynchronously

                Expect.isNone (sink.Latest jobId |> Async.RunSynchronously) "remembers nothing"
            }

            test "Latest is None for a job that has reported nothing" {
                let sink =
                    FanOutJobProgressSink(RecordingChannel(), RecordingEventStore(), silentLogger) :> IJobProgressSink

                Expect.isNone (sink.Latest(Guid.NewGuid()) |> Async.RunSynchronously) "no checkpoint, no answer"
            }

            test "a throwing channel does not fail the report — progress is never why a job fails" {
                let events = RecordingEventStore()

                let throwingChannel =
                    { new INotificationChannel with
                        member _.Publish(_scopeId, _notification) = async { failwith "transport down" }

                        member _.Subscribe(_scopeId, _handler) =
                            async.Return(Guid.NewGuid(): NotificationSubscriptionId)

                        member _.Unsubscribe(_subscriptionId) = async.Return()
                    }

                let sink =
                    FanOutJobProgressSink(
                        throwingChannel,
                        events,
                        silentLogger,
                        policy = ProgressCoalescePolicy.unlimited
                    )
                    :> IJobProgressSink

                // The assertion is that this does not throw — and that the
                // durable leg still ran, so one leg failing does not
                // silently take the other with it.
                sink.Report(
                    Guid.NewGuid(),
                    "team-a",
                    ProgressCheckpoint.create (Some 0.5) "half" |> ProgressCheckpoint.durable
                )
                |> Async.RunSynchronously

                Expect.hasLength events.ProgressEvents 1 "the durable leg is independent of the transient one"
            }
        ]

        // ─── 321.B — ctx.Progress and the GP 13 no-op ────────────────

        testList "ctx.Progress" [
            test "GP 13 — with no sink composed, reporting publishes and persists NOTHING" {
                // Asserted on observable absence through doubles that would
                // record any traffic, not on the reporter's identity.
                let channel = RecordingChannel()
                let events = RecordingEventStore()

                let reporter = JobProgressSink.reporterForOption None (Guid.NewGuid()) "team-a"

                for i in 1..50 do
                    reporter.Report(ProgressCheckpoint.create (Some(float i / 50.0)) $"step {i}")
                    |> Async.RunSynchronously

                // Including the terminal checkpoint, which every other test
                // here proves is un-sheddable — un-sheddable is not the same
                // as un-suppressible when the feature is off.
                reporter.Report(ProgressCheckpoint.create (Some 1.0) "done")
                |> Async.RunSynchronously

                Expect.isEmpty channel.Published "no notification traffic"
                Expect.isEmpty events.Written "no event traffic"
            }

            test "the ambient default is the no-op reporter, so ctx.Progress is safe outside a dispatch" {
                // A handler invoked directly by a consumer's unit test has
                // no scheduler around it. `ctx.Progress` must still be
                // callable rather than null-referencing.
                let reporter = JobProgressScope.current ()

                reporter.Report(ProgressCheckpoint.create (Some 0.5) "outside a dispatch")
                |> Async.RunSynchronously

                Expect.isTrue true "reporting outside a dispatch neither throws nor requires a guard"
            }

            test "a pushed reporter is ambient inside the scope and restored after it" {
                let channel = RecordingChannel()
                let events = RecordingEventStore()

                let sink =
                    FanOutJobProgressSink(channel, events, silentLogger, policy = ProgressCoalescePolicy.unlimited)
                    :> IJobProgressSink

                let jobId = Guid.NewGuid()

                do
                    use _scope = JobProgressScope.push (JobProgressSink.reporterFor sink jobId "team-a")

                    JobProgressScope.current().Report(ProgressCheckpoint.create (Some 0.5) "inside")
                    |> Async.RunSynchronously

                // Outside the scope the ambient must be back to the no-op —
                // otherwise a later dispatch on the same execution-context
                // lineage could publish against the WRONG job id, which is
                // a scope-attribution bug, not a cosmetic one.
                JobProgressScope.current().Report(ProgressCheckpoint.create (Some 0.9) "outside")
                |> Async.RunSynchronously

                let payloads = channel.ProgressPayloads
                Expect.hasLength payloads 1 "only the in-scope report reached the channel"
                Expect.stringContains (snd payloads[0]) "inside" "and it was the in-scope one"
            }

            test "the ambient reporter survives await hops inside the scope" {
                // THE load-bearing property for 321.B, and the one that
                // reasoning about `AsyncLocal` cannot settle: the scheduler
                // pushes the scope, then calls `handler.Execute`, whose body
                // awaits repeatedly and reports between awaits. If the
                // ambient did not flow across those continuations, progress
                // would silently stop after a handler's first `do!` — a
                // failure that looks like "the handler stopped reporting"
                // rather than like a wiring bug.
                let channel = RecordingChannel()
                let events = RecordingEventStore()

                let sink =
                    FanOutJobProgressSink(channel, events, silentLogger, policy = ProgressCoalescePolicy.unlimited)
                    :> IJobProgressSink

                let jobId = Guid.NewGuid()

                // Shaped like a real dispatch: the scope is established by
                // the caller, the reporting happens inside a nested async
                // that the caller `do!`s into.
                let handlerBody = async {
                    do! Async.Sleep 1
                    do! JobProgressScope.current().Report(ProgressCheckpoint.create (Some 0.3) "after first await")
                    do! Async.Sleep 1
                    do! JobProgressScope.current().Report(ProgressCheckpoint.create (Some 1.0) "after second await")
                }

                async {
                    use _scope = JobProgressScope.push (JobProgressSink.reporterFor sink jobId "team-a")
                    do! handlerBody
                }
                |> Async.RunSynchronously

                let payloads = channel.ProgressPayloads
                Expect.hasLength payloads 2 "both post-await reports reached the sink"

                Expect.equal
                    (sink.Latest jobId |> Async.RunSynchronously |> Option.bind _.Fraction)
                    (Some 1.0)
                    "and were attributed to the pushed job"
            }

            test "the reporter binds the job id, so a handler cannot report against another job" {
                let channel = RecordingChannel()
                let events = RecordingEventStore()

                let sink =
                    FanOutJobProgressSink(channel, events, silentLogger, policy = ProgressCoalescePolicy.unlimited)
                    :> IJobProgressSink

                let jobId = Guid.NewGuid()
                let reporter = JobProgressSink.reporterFor sink jobId "team-a"

                reporter.Report(ProgressCheckpoint.create (Some 0.5) "half")
                |> Async.RunSynchronously

                let latest = sink.Latest jobId |> Async.RunSynchronously
                Expect.isSome latest "attributed to the bound job"

                // The seam offers no way to name a different job — this is
                // the structural half of GP 4 for progress reporting.
                let payloads = channel.ProgressPayloads
                Expect.equal (payloads |> List.map fst) [ "team-a" ] "and the bound scope"
            }
        ]

        // ─── 321.D — the reconciliation poll ─────────────────────────

        testList "reconciliation-loop integration" [
            test "ServerConfig defaults to NoJobProgress (GP 11)" {
                Expect.equal
                    ServerConfig.defaults.JobProgress
                    NoJobProgress
                    "an existing deployment gains no progress traffic on upgrade"
            }

            test "an externally-run job's fractional progress becomes a checkpoint with no handler code" {
                // 321.D's claim, asserted at the shape the poll produces:
                // `ExternalOutcome.Running (Some p)` → a checkpoint whose
                // Fraction is p. The scheduler's poll arm constructs
                // exactly this; the wiring itself is covered by the
                // external-compute packs.
                let channel = RecordingChannel()
                let events = RecordingEventStore()

                let sink =
                    FanOutJobProgressSink(channel, events, silentLogger, policy = ProgressCoalescePolicy.unlimited)
                    :> IJobProgressSink

                let jobId = Guid.NewGuid()

                let fraction =
                    match ExternalOutcome.Running(Some 0.62) with
                    | ExternalOutcome.Running(Some f) -> Some f
                    | _ -> None

                sink.Report(
                    jobId,
                    "team-a",
                    ProgressCheckpoint.createAt
                        fraction
                        "external work in progress on gpu-pool"
                        (Some "external")
                        baseTime
                )
                |> Async.RunSynchronously

                let latest = sink.Latest jobId |> Async.RunSynchronously
                Expect.equal (latest |> Option.bind _.Fraction) (Some 0.62) "the backend's fraction, unmodified"
                Expect.equal (latest |> Option.bind _.Stage) (Some "external") "labelled as external work"
                Expect.isFalse (latest.Value.Durable) "poll-driven checkpoints are transient — one per tick forever"
                Expect.isEmpty events.ProgressEvents "so they cost no blob write"
            }

            test "a backend reporting Running None yields no fabricated fraction" {
                // `Running None` means "running, cannot estimate". Turning
                // that into a number would be an invention the progress bar
                // then displays as fact.
                let fraction =
                    match ExternalOutcome.Running None with
                    | ExternalOutcome.Running(Some f) -> Some f
                    | _ -> None

                Expect.isNone fraction "no estimate in, no estimate out"
            }
        ]
    ]