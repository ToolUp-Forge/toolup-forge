module ToolUp.Platform.Tests.InProcess.TransactionalDispatcherTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tracing
open ToolUp.Platform.Tests.Contracts

// ─── Test doubles ────────────────────────────────────────────────
//
// The dispatcher's collaborators (`IConfigStore`, `INotificationSink`,
// `ILogger`) are replaced with capturing fakes so the tests assert
// against observable side-effects rather than reaching through to a
// real blob store. Phase 9c rule 4 (handlers stateless between
// invocations) means every test is free to construct fresh fakes.

/// Fake `IConfigStore` returning a fixed prefs map. Other methods
/// throw — the dispatcher only ever reads `GetRaw` for the prefs
/// scope, so any other call is a contract regression that should
/// surface loudly.
type FakeConfigStore(prefs: Map<string, string>) =
    interface IConfigStore with
        member _.GetRaw(_: StorageScope, _: string) = async { return prefs }

        member _.Get<'T>(_: StorageScope, _: string) = async { return (None: 'T option) }

        member _.GetEffective<'T>(_: StorageScope, _: string, _: ModuleConfigSchema) = async {
            return Unchecked.defaultof<'T>
        }

        member _.Set<'T>(_: StorageScope, _: string, _: 'T, _: ModuleConfigSchema) = async {
            return Error "FakeConfigStore.Set unsupported"
        }

        member _.SetRaw(_: StorageScope, _: string, _: Map<string, string>, _: ModuleConfigSchema) = async {
            return Error "FakeConfigStore.SetRaw unsupported"
        }

        member _.Clear(_: StorageScope, _: string) = async { return () }

        member _.Erase(_: string, _: string, _: ErasurePolicy, _: bool) = async {
            return
                Result.Ok {
                    HandlerName = "config"
                    RecordsAffected = 0
                    Note = None
                }
        }

/// Fake `ILogger` capturing all entries for later assertion. Errors
/// are recorded in plain string form; the optional exn is
/// stringified onto the same record.
type CapturingLogger() =
    let entries = ConcurrentQueue<string * string>()

    member _.Entries = entries |> Seq.toList

    interface ILogger with
        member _.Debug(message) = entries.Enqueue("Debug", message)
        member _.Info(message) = entries.Enqueue("Info", message)
        member _.Warn(message) = entries.Enqueue("Warn", message)

        member _.Error(message, ex) =
            let suffix =
                match ex with
                | Some e -> $" :: {e.GetType().Name}: {e.Message}"
                | None -> ""

            entries.Enqueue("Error", message + suffix)

/// Fake `IAuditLog` capturing every recorded event. Phase 6f step
/// (b) — the dispatcher emits `NotificationSent` /
/// `NotificationDeliveryFailed` at terminal-status points; tests
/// assert the right cases land in the right order.
type CapturingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    let gate = obj ()

    member _.Recorded = lock gate (fun () -> List.ofSeq recorded)

    /// The dispatcher emits audit via `Async.Start` (fire-and-forget
    /// onto the thread pool), so a test asserting on the rows waits for
    /// writes it deliberately did not await. Event-driven, not polled:
    /// `Record` pulses the monitor, so this returns the instant the
    /// `count`-th row lands, and the cap only bites when the rows are
    /// not coming at all. A timeout fails HERE, naming what did arrive
    /// — the deny-observer fixtures' 5s wall-clock poll expired under
    /// machine load on 2026-08-24 and blamed the downstream audit claim
    /// instead of the scheduler; this file's old predicate-poll
    /// `waitFor` carried the same 5s bound.
    member _.WaitFor(count: int) =
        let cap = TimeSpan.FromSeconds 30.0
        let sw = Diagnostics.Stopwatch.StartNew()

        lock gate (fun () ->
            while recorded.Count < count && sw.Elapsed < cap do
                let remaining = cap - sw.Elapsed

                if remaining > TimeSpan.Zero then
                    Monitor.Wait(gate, remaining) |> ignore

            if recorded.Count < count then
                failtestf
                    "audit wait: %d of %d expected row(s) arrived within %.0fs — with an event-driven wait this long the dispatcher's fire-and-forget write never happened (it is not merely late); the audit assertion after this wait has NOT been evaluated"
                    recorded.Count
                    count
                    cap.TotalSeconds)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async {
            lock gate (fun () ->
                recorded.Add((scopeId, audit))
                Monitor.PulseAll gate)
        }

        member _.GetAuditTrail(_, _, _) = async { return [] }

/// Fake `INotificationSink` that signals a `TaskCompletionSource` on
/// each `Send`, returning a result chosen by the constructor. Used
/// by the dispatcher tests to prove that publishes flow through.
type FakeSink(kind: NotificationKind.SinkKind, provider: string, results: ConcurrentQueue<SinkResult>) =
    let calls = ConcurrentBag<NotificationEnvelope>()
    let allCalls = TaskCompletionSource<unit>()
    let firstCall = TaskCompletionSource<NotificationEnvelope>()

    member _.Calls = calls |> Seq.toList
    member _.FirstCallTask = firstCall.Task
    member _.AllCallsTask = allCalls.Task

    member _.SignalNoMore() = allCalls.TrySetResult() |> ignore

    interface INotificationSink with
        member _.Kind = kind
        member _.Provider = provider

        member _.Send(_, envelope) = async {
            calls.Add envelope
            firstCall.TrySetResult envelope |> ignore

            let mutable next = SinkResult.PermanentFailure "no result configured"

            if results.TryDequeue(&next) then
                return next
            else
                return SinkResult.Delivered None
        }

let private prefsAllowingEmail =
    Map.ofList [ ConfigKeys.NotificationPrefsKeys.EmailEnabled, "true" ]

let private prefsBlockingEmail =
    Map.ofList [ ConfigKeys.NotificationPrefsKeys.EmailEnabled, "false" ]

let private emptyEnvelope () : EmailEnvelope = {
    RecipientUserIds = [ "user-1" ]
    Content = InlineEmail("Subject", "Body", None)
    CorrelationId = None
}

/// Spin up a dispatcher, give it `dispatcherStartDelay` to wire
/// subscriptions, then publish via the supplied channel. Returns the
/// dispatcher + sink + channel + logger + audit log so each test
/// asserts what it cares about.
let private makeFixture
    (sinkKind: NotificationKind.SinkKind)
    (prefs: Map<string, string>)
    (results: SinkResult list)
    : Async<
          TransactionalDispatcher.TransactionalDispatcher *
          FakeSink *
          INotificationChannel *
          CapturingLogger *
          CapturingAuditLog
       >
    =
    async {
        let logger = CapturingLogger() :> ILogger

        let resultsQueue = ConcurrentQueue<SinkResult>()
        results |> List.iter resultsQueue.Enqueue

        let sink = FakeSink(sinkKind, "Fake", resultsQueue)

        let configStore = FakeConfigStore(prefs) :> IConfigStore
        let auditLog = CapturingAuditLog()

        let retry = {
            TransactionalRetryPolicy.defaults with
                MaxAttempts = 2
                InitialBackoff = TimeSpan.FromMilliseconds 1.0
                MaxBackoff = TimeSpan.FromMilliseconds 1.0
        }

        let dispatcher =
            new TransactionalDispatcher.TransactionalDispatcher(
                [ sink :> INotificationSink ],
                configStore,
                auditLog :> IAuditLog,
                logger,
                retry,
                NoOpActivitySink() :> IActivitySink
            )

        // Boot the BackgroundService so the queue's drain loop runs.
        do! dispatcher.StartAsync(CancellationToken.None) |> Async.AwaitTask

        let baseChannel =
            NotificationChannel.InMemoryNotificationChannel(None) :> INotificationChannel

        let wrapping =
            TransactionalDispatcher.DispatchingNotificationChannel(baseChannel, dispatcher) :> INotificationChannel

        return dispatcher, sink, wrapping, (logger :?> CapturingLogger), auditLog
    }

[<Tests>]
let tests =
    testList "TransactionalDispatcher" [
        testCaseAsync "publish of TransactionalEmail with prefs enabled lands at the registered sink"
        <| async {
            let! dispatcher, sink, channel, _logger, _audit =
                makeFixture NotificationKind.SinkKind.Email prefsAllowingEmail [ SinkResult.Delivered(Some "msg-id-1") ]

            try
                do! channel.Publish("scope-A", TransactionalEmail(emptyEnvelope ()))

                let! winner =
                    Task.WhenAny(sink.FirstCallTask, Task.Delay(TimeSpan.FromSeconds 5.0))
                    |> Async.AwaitTask

                if obj.ReferenceEquals(winner, sink.FirstCallTask) then
                    let! envelope = sink.FirstCallTask |> Async.AwaitTask
                    Expect.equal envelope.ScopeId "scope-A" "envelope scope round-trips"

                    match envelope.Notification with
                    | TransactionalEmail _ -> ()
                    | other -> failtestf "expected TransactionalEmail, got %A" other
                else
                    failtest "sink never received the publish (timeout)"
            finally
                dispatcher.StopAsync(CancellationToken.None) |> ignore
        }

        testCaseAsync
            "publish with prefs disabled does NOT reach the sink and emits one NotificationSilentlySkipped audit (Phase 6l.C)"
        <| async {
            let! dispatcher, sink, channel, _logger, audit =
                makeFixture NotificationKind.SinkKind.Email prefsBlockingEmail []

            try
                do! channel.Publish("scope-A", TransactionalEmail(emptyEnvelope ()))

                // Wait briefly for the dispatcher to consume the
                // queued item and short-circuit on prefs. If the sink
                // received anything in this window, the prefs gate
                // is broken.
                do! Async.Sleep 250

                Expect.isEmpty sink.Calls "sink must not be invoked when prefs are disabled"

                // Phase 6l.C: prefs-driven drops now emit a single
                // NotificationSilentlySkipped audit event so an admin
                // who disabled email opt-in but a publish happened
                // anyway can correlate "thought email was on" against
                // "actually dropped". PII-free — recipient hashes only.
                Expect.equal audit.Recorded.Length 1 "exactly one audit event emitted"

                let _, evt = audit.Recorded.Head

                match evt with
                | NotificationSilentlySkipped p ->
                    Expect.equal
                        p.NotificationKind
                        (NotificationKind.SinkKind.toWireString NotificationKind.SinkKind.Email)
                        "Kind"

                    Expect.equal p.Reason "team_opted_out" "Reason"
                    Expect.equal p.ScopeId "scope-A" "ScopeId"
                | other -> failtestf "expected NotificationSilentlySkipped, got %A" other
            finally
                dispatcher.StopAsync(CancellationToken.None) |> ignore
        }

        testCaseAsync "publish of non-transactional kind passes through to the inner channel"
        <| async {
            let! dispatcher, sink, channel, _logger, _audit =
                makeFixture NotificationKind.SinkKind.Email prefsAllowingEmail []

            try
                // The wrapping channel forwards non-transactional
                // kinds to its inner channel. A subscriber on the
                // inner channel still receives them; the dispatcher
                // never sees them.
                let inner =
                    NotificationChannel.InMemoryNotificationChannel(None) :> INotificationChannel

                use dispatcher2 =
                    new TransactionalDispatcher.TransactionalDispatcher(
                        [ sink :> INotificationSink ],
                        FakeConfigStore(prefsAllowingEmail) :> IConfigStore,
                        CapturingAuditLog() :> IAuditLog,
                        CapturingLogger() :> ILogger,
                        TransactionalRetryPolicy.defaults,
                        NoOpActivitySink() :> IActivitySink
                    )

                let wrapping =
                    TransactionalDispatcher.DispatchingNotificationChannel(inner, dispatcher2) :> INotificationChannel

                let received = TaskCompletionSource<NotificationEnvelope>()

                let! _subId = wrapping.Subscribe("scope-A", fun env -> received.TrySetResult env |> ignore)

                do! wrapping.Publish("scope-A", SystemMessage(SystemMessageLevel.Info, "ping"))

                let! winner =
                    Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds 2.0))
                    |> Async.AwaitTask

                if obj.ReferenceEquals(winner, received.Task) then
                    let! env = received.Task |> Async.AwaitTask
                    Expect.equal env.ScopeId "scope-A" "non-transactional envelope round-trips"
                else
                    failtest "non-transactional publish was not delivered to the inner channel"

                Expect.isEmpty sink.Calls "non-transactional kinds must not invoke the sink"
            finally
                dispatcher.StopAsync(CancellationToken.None) |> ignore
        }

        testCaseAsync "Delivered emits NotificationSent audit with envelope identity"
        <| async {
            let! dispatcher, _sink, channel, _logger, audit =
                makeFixture NotificationKind.SinkKind.Email prefsAllowingEmail [
                    SinkResult.Delivered(Some "msg-id-42")
                ]

            try
                let envelopePayload = {
                    RecipientUserIds = [ "user-a"; "user-b" ]
                    Content = InlineEmail("hi", "body", None)
                    CorrelationId = Some "corr-1"
                }

                do! channel.Publish("scope-A", TransactionalEmail envelopePayload)

                audit.WaitFor 1

                let entries = audit.Recorded
                Expect.equal entries.Length 1 "exactly one audit entry on a single Delivered"

                let scopeId, evt = entries.Head
                Expect.equal scopeId "scope-A" "audit entry tagged with publishing scope"

                match evt with
                | NotificationSent payload ->
                    Expect.equal payload.NotificationKind "Email" "Email kind on the payload"
                    Expect.equal payload.Provider "Fake" "Provider label preserved"
                    Expect.equal payload.RecipientUserIds [ "user-a"; "user-b" ] "recipient ids preserved (PII-free)"
                    Expect.equal payload.VendorMessageId (Some "msg-id-42") "vendor message id preserved"
                    Expect.equal payload.CorrelationId (Some "corr-1") "correlation id preserved"
                | other -> failtestf "expected NotificationSent, got %A" other
            finally
                dispatcher.StopAsync(CancellationToken.None) |> ignore
        }

        testCaseAsync "PermanentFailure emits NotificationDeliveryFailed audit on first attempt"
        <| async {
            let! dispatcher, _sink, channel, _logger, audit =
                makeFixture NotificationKind.SinkKind.Email prefsAllowingEmail [
                    SinkResult.PermanentFailure "vendor-rejected"
                ]

            try
                do! channel.Publish("scope-A", TransactionalEmail(emptyEnvelope ()))

                audit.WaitFor 1

                let _scopeId, evt = audit.Recorded.Head

                match evt with
                | NotificationDeliveryFailed payload ->
                    Expect.equal payload.Attempts 1 "permanent failure on the first attempt — Attempts = 1"
                    Expect.equal payload.Error "vendor-rejected" "error message preserved"
                | other -> failtestf "expected NotificationDeliveryFailed, got %A" other
            finally
                dispatcher.StopAsync(CancellationToken.None) |> ignore
        }

        testCaseAsync "Retry-exhausted TransientFailure emits NotificationDeliveryFailed with full attempt count"
        <| async {
            // MaxAttempts = 2 in the fixture, so two transient failures
            // exhaust the budget. Result queue feeds them in order.
            let! dispatcher, _sink, channel, _logger, audit =
                makeFixture NotificationKind.SinkKind.Email prefsAllowingEmail [
                    SinkResult.TransientFailure "503"
                    SinkResult.TransientFailure "503"
                ]

            try
                do! channel.Publish("scope-A", TransactionalEmail(emptyEnvelope ()))

                audit.WaitFor 1

                let _scopeId, evt = audit.Recorded.Head

                match evt with
                | NotificationDeliveryFailed payload ->
                    Expect.equal payload.Attempts 2 "Attempts equals MaxAttempts after exhaustion"
                | other -> failtestf "expected NotificationDeliveryFailed, got %A" other
            finally
                dispatcher.StopAsync(CancellationToken.None) |> ignore
        }

        testCaseAsync "Skipped result emits no audit"
        <| async {
            let! dispatcher, _sink, channel, _logger, audit =
                makeFixture NotificationKind.SinkKind.Email prefsAllowingEmail [ SinkResult.Skipped "no_address" ]

            try
                do! channel.Publish("scope-A", TransactionalEmail(emptyEnvelope ()))
                do! Async.Sleep 250

                Expect.isEmpty audit.Recorded "no audit entry when sink returns Skipped"
            finally
                dispatcher.StopAsync(CancellationToken.None) |> ignore
        }

        testCase "duplicate sink Kind registration is rejected at construction"
        <| fun _ ->
            let resultsQueue = ConcurrentQueue<SinkResult>()

            let s1 =
                FakeSink(NotificationKind.SinkKind.Email, "FakeA", resultsQueue) :> INotificationSink

            let s2 =
                FakeSink(NotificationKind.SinkKind.Email, "FakeB", resultsQueue) :> INotificationSink

            let configStore = FakeConfigStore(prefsAllowingEmail) :> IConfigStore
            let logger = CapturingLogger() :> ILogger

            // Direct call to validateSinkRegistration since the
            // duplicate-detection sits in front of the constructor.
            Expect.throws
                (fun () -> TransactionalDispatcher.validateSinkRegistration [ s1; s2 ])
                "Duplicate Kind registration must throw at compose time"
    ]