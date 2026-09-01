module ToolUp.Platform.Tests.InProcess.WebhookFailureStateLeaseTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tracing
open ToolUp.Platform.WebhookDispatcher
open ToolUp.Platform.Tests.Contracts

// ─── Phase 16a — the webhook dispatcher's failure-state lease ────────
//
// The dispatcher's retry ladder ends in a read-modify-write over shared,
// blob-backed subscription state: read `ConsecutiveFailures`, write
// `n + 1`, and auto-disable (plus one `WebhookSubscriptionAutoDisabled`
// audit row) once the threshold is crossed. `IWebhookRegistry` documents
// that surface as last-write-wins, so two ladders finishing concurrently
// — two events to one failing subscription, or one subscription reached
// from two `WorkerOnly` / `DispatcherOnly` silos — both read `n` and both
// write `n + 1`. Phase 16a holds that transition under a lease on the
// injected `IDistributedLock`.
//
// **The pack is built around making the claim falsifiable.** Every arm
// asserts the OBSERVABLE EFFECT — the persisted counter and the audit
// rows — never the return value of a lock call, because a lease that is
// acquired and then not respected passes a call-count assertion and still
// loses the update. And the two-replica arm is PAIRED WITH A CONTROL on
// the identical construction whose only difference is that each replica
// holds its own lock table (which is exactly what the in-process default
// is across a process boundary): that arm MUST observe the lost update.
// Without it, an assertion that the counter reached 2 could be passing
// because the two ladders never overlapped at all.
//
// **The overlap is PLACED, not raced for.** `RendezvousRegistry` holds
// the first ladder inside `SetConsecutiveFailures` until a second reader
// has arrived — which, under a shared lease, it cannot, so that arm falls
// through a bounded gate instead. Deterministic in both directions: the
// unlocked arm loses the update whichever ladder gets there first, and
// the locked arm serialises whether or not the gate elapses.

// ─── Substrate doubles ───────────────────────────────────────────────

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Always-500 receiver: every attempt fails, so every delivery walks its
/// whole ladder and dead-letters. Never touches the network.
type private AlwaysFailingHandler() =
    inherit HttpMessageHandler()

    override _.SendAsync(_request, _ct) =
        let response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        response.Content <- new StringContent "receiver down"
        Task.FromResult response

/// Always-200 receiver — the healthy-path arm. A named type rather than
/// an object expression because `HttpMessageHandler.SendAsync` is
/// protected, which an object expression cannot implement.
type private AlwaysSucceedingHandler() =
    inherit HttpMessageHandler()

    override _.SendAsync(_request, _ct) =
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))

/// No declared limit ⇒ no gate. The rate limiter is orthogonal to what
/// this pack measures; `Refused` would skip the failure-state update
/// entirely (it is not a receiver-health signal), so admitting every
/// delivery is what keeps the arms comparable.
let private permissiveRateLimiter =
    { new IRateLimiter with
        member _.Wait(_key) = async { return Proceed }
    }

type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

// ─── Lock instrumentation ────────────────────────────────────────────

/// Records every acquire against an inner lock, and — the assertion that
/// matters — tracks how many leases are held CONCURRENTLY per id. A lock
/// that hands out two simultaneous holds for one id has not excluded
/// anything, whatever its acquire count says.
type private RecordingLock(inner: IDistributedLock) =
    let acquired = ConcurrentQueue<string>()
    let live = ConcurrentDictionary<string, int>()
    let mutable maxConcurrent = 0
    let mutable releases = 0

    member _.AcquiredIds = acquired |> List.ofSeq
    member _.MaxConcurrentPerId = maxConcurrent

    /// Leases RELEASED. `withLease` releases only after the body has
    /// run, so this — and not the counter write, and certainly not the
    /// delivery log — is the instant a failure-state transition is
    /// wholly done, audit row included. Both earlier bounds sampled
    /// mid-transition and read a half-applied result.
    member _.Releases = Volatile.Read &releases

    interface IDistributedLock with
        member _.TryAcquire(lockId, ttl) = async {
            match! inner.TryAcquire(lockId, ttl) with
            | None -> return None
            | Some lease ->
                acquired.Enqueue lockId
                let n = live.AddOrUpdate(lockId, 1, (fun _ c -> c + 1))
                // Racy only in the direction that under-reports; a value
                // above 1 is never a false positive.
                if n > maxConcurrent then
                    maxConcurrent <- n

                return Some lease
        }

        member _.Renew lease = inner.Renew lease

        member _.Release lease = async {
            live.AddOrUpdate(lease.LockId, 0, (fun _ c -> max 0 (c - 1))) |> ignore
            do! inner.Release lease
            Interlocked.Increment &releases |> ignore
        }

// ─── Rendezvous over the registry ────────────────────────────────────

/// Places the overlap the two-replica arms need. The FIRST caller into
/// `SetConsecutiveFailures` parks until a second `GetSubscription` has
/// been observed, or until `gate` elapses — whichever comes first.
///
/// Under separate locks the second reader arrives immediately, so the
/// first writer resumes having read a value the second reader has already
/// read: the lost update, deterministically. Under one shared lock the
/// second reader cannot arrive (it is queued on the lease), so the first
/// writer falls through the gate and the ladders serialise. The gate is
/// therefore paid exactly once, and only by the arm that is asserting
/// exclusion.
type private RendezvousRegistry(inner: IWebhookRegistry, gate: TimeSpan) =
    let secondReaderArrived =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable readers = 0
    let mutable firstWriterClaimed = 0
    let mutable completedWrites = 0

    /// How many failure-state writes have RETURNED. The arms assert on
    /// the persisted counter, and the delivery log is written BEFORE the
    /// transition — so waiting on dead-letter rows samples the counter
    /// while a write is still parked in the gate, which is exactly how
    /// the shared-lock arm first read `Some 0`. This is the observable
    /// that actually bounds the assertion.
    member _.CompletedWrites = Volatile.Read &completedWrites

    interface IWebhookRegistry with
        member _.CreateSubscription subscription = inner.CreateSubscription subscription

        member _.ListSubscriptions scopeId = inner.ListSubscriptions scopeId

        member _.GetSubscription(scopeId, subscriptionId) = async {
            let! result = inner.GetSubscription(scopeId, subscriptionId)

            if Interlocked.Increment &readers >= 2 then
                secondReaderArrived.TrySetResult() |> ignore

            return result
        }

        member _.UpdateStatus(scopeId, subscriptionId, status) =
            inner.UpdateStatus(scopeId, subscriptionId, status)

        member _.RotateSecret(scopeId, subscriptionId, currentRef, previousRef, graceExpiresAt) =
            inner.RotateSecret(scopeId, subscriptionId, currentRef, previousRef, graceExpiresAt)

        member _.SetConsecutiveFailures(scopeId, subscriptionId, count) = async {
            if Interlocked.CompareExchange(&firstWriterClaimed, 1, 0) = 0 then
                do!
                    Task.WhenAny(secondReaderArrived.Task, Task.Delay gate)
                    |> Async.AwaitTask
                    |> Async.Ignore

            let! result = inner.SetConsecutiveFailures(scopeId, subscriptionId, count)
            Interlocked.Increment &completedWrites |> ignore
            return result
        }

        member _.DeleteSubscription(scopeId, subscriptionId) =
            inner.DeleteSubscription(scopeId, subscriptionId)

        member _.ListAllActive() = inner.ListAllActive()

// ─── Fixture ─────────────────────────────────────────────────────────

let private scopeId = "team-alpha"

let private receiverHost = "receiver.example.com"

/// The delivery-time SSRF guard re-resolves the target host on EVERY
/// attempt, and a refusal dead-letters WITHOUT touching the failure
/// state — a platform-side block is not a receiver-health signal. An
/// unresolvable test host therefore produces dead-letter rows that never
/// reach the code this pack measures, which is exactly how the first run
/// of these arms failed. Naming the mock host on the operator allowlist
/// short-circuits the guard ahead of DNS, which is the affordance the
/// validator documents for precisely this case.
let private allowlistedPolicy: WebhookUrlValidator.WebhookUrlPolicy = { AllowedHosts = [ receiverHost ] }

/// One attempt per delivery and no backoff, so a ladder is one failing
/// POST and its terminal transition — the pack measures the transition,
/// not the retry arithmetic. Auto-disable at 2 so the threshold is
/// crossed by the SECOND delivery: with a lost update it is never reached
/// at all, which is precisely the difference the control arm reads.
let private retryPolicy = {
    WebhookRetryPolicy.defaults with
        MaxAttempts = 1
        InitialBackoff = TimeSpan.Zero
        MaxBackoff = TimeSpan.Zero
        DisableAfterConsecutiveFailures = 2
}

let private subscriptionFor (subscriptionId: Guid) : WebhookSubscription = {
    SubscriptionId = subscriptionId
    ScopeId = scopeId
    TargetUrl = sprintf "https://%s/hook" receiverHost
    SecretRef = ""
    Secret = Some "legacy-inline-signing-secret"
    EventTypes = []
    Status = WebhookStatus.Active
    CreatedBy = "tests"
    CreatedAt = DateTime.UtcNow
    ConsecutiveFailures = 0
    PreviousSecretRef = None
    PreviousSecret = None
    PreviousSecretExpiresAt = None
}

let private eventFor (i: int) : ModuleEvent = {
    Id = Guid.NewGuid()
    OccurredAt = DateTime.UtcNow
    ScopeId = scopeId
    SourceModule = "tests.module"
    EventType = sprintf "ThingHappened%d" i
    Payload = """{"n":1}"""
}

/// Build `replicaCount` dispatchers over ONE shared blob store — the
/// shared-persistence half of the multi-silo topology — each handed the
/// lock its `lockFor` index returns. Passing the same instance for both
/// models a store-backed companion; passing two models the in-process
/// default across a process boundary.
let private buildReplicas (replicaCount: int) (lockFor: int -> IDistributedLock) (gate: TimeSpan) =
    let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let baseRegistry = WebhookRegistry.createRegistry storage
    let rendezvous = RendezvousRegistry(baseRegistry, gate)
    let registry = rendezvous :> IWebhookRegistry
    let deliveryLog = WebhookRegistry.createDeliveryLog storage
    let secretStore = InMemorySecretStore() :> ISecretStore
    let urlPolicy: WebhookUrlValidator.WebhookUrlPolicy = allowlistedPolicy

    let dispatchers = [
        for i in 0 .. replicaCount - 1 ->
            let httpClient = new HttpClient(new AlwaysFailingHandler())

            WebhookDispatcher.createWithLock
                registry
                deliveryLog
                eventStore
                httpClient
                retryPolicy
                silentLogger
                (NoOpActivitySink() :> IActivitySink)
                secretStore
                (fun () -> permissiveRateLimiter)
                urlPolicy
                (lockFor i)
    ]

    baseRegistry, rendezvous, deliveryLog, eventStore, dispatchers

/// Poll until `condition` holds or the deadline passes. The dispatcher is
/// a `BackgroundService` draining a queue, so completion is observed, not
/// awaited. Returns whether the condition was reached, so a caller can
/// fail with its own message rather than on a bare timeout.
let private waitFor (deadline: TimeSpan) (condition: unit -> Async<bool>) : Async<bool> = async {
    let started = DateTime.UtcNow
    let mutable met = false

    while not met && DateTime.UtcNow - started < deadline do
        let! now = condition ()
        met <- now

        if not met then
            do! Async.Sleep 25

    return met
}

/// Run one delivery through each dispatcher and wait for both ladders to
/// have dead-lettered. Every attempt fails, so each delivery records one
/// attempt row plus one terminal dead-letter row.
let private runOneDeliveryPerReplica
    (deliveryLog: IWebhookDeliveryLog)
    (completedTransitions: unit -> int)
    (expectedTransitions: int)
    (subscriptionId: Guid)
    (dispatchers: WebhookDispatcher.WebhookDispatcherService list)
    : Async<bool> =
    async {
        let hosted = dispatchers |> List.map (fun d -> d :> IHostedService)

        for h in hosted do
            do! h.StartAsync CancellationToken.None |> Async.AwaitTask

        dispatchers
        |> List.iteri (fun i d -> (d :> IWebhookDispatcher).Dispatch(eventFor i))

        let expectedDeadLetters = List.length dispatchers

        let! settled =
            waitFor (TimeSpan.FromSeconds 20.0) (fun () -> async {
                let! rows = deliveryLog.ListRecent(scopeId, subscriptionId, 100)

                let deadLettered =
                    rows
                    |> List.filter (fun r ->
                        match r.Outcome with
                        | WebhookDeliveryOutcome.DeadLettered _ -> true
                        | _ -> false)

                return
                    List.length deadLettered >= expectedDeadLetters
                    && completedTransitions () >= expectedTransitions
            })

        for h in hosted do
            do! h.StopAsync CancellationToken.None |> Async.AwaitTask

        return settled
    }

let private autoDisableRowCount (eventStore: IEventStore) = async {
    let! events = eventStore.ReadByType(scopeId, WebhookEventTypes.SubscriptionAutoDisabled)
    return List.length events
}

// ─── Tests ───────────────────────────────────────────────────────────

let tests =
    testList "WebhookFailureStateLease (Phase 16a)" [

        testCase "one failing delivery takes exactly one namespaced failure-state lease and releases it"
        <| fun _ ->
            let subscriptionId = Guid.NewGuid()
            let recording = RecordingLock(InProcessDistributedLock.create ())

            let baseRegistry, _rendezvous, deliveryLog, _eventStore, dispatchers =
                buildReplicas 1 (fun _ -> recording :> IDistributedLock) TimeSpan.Zero

            async {
                let! _ = baseRegistry.CreateSubscription(subscriptionFor subscriptionId)

                let! settled =
                    runOneDeliveryPerReplica deliveryLog (fun () -> recording.Releases) 1 subscriptionId dispatchers

                Expect.isTrue settled "the delivery should have dead-lettered inside the deadline"
            }
            |> Async.RunSynchronously

            let expectedId = sprintf "toolup:webhook-failure-state:%s:%O" scopeId subscriptionId

            Expect.equal
                recording.AcquiredIds
                [ expectedId ]
                "the dead-letter transition takes exactly one lease, on the scope+subscription-namespaced id"

            // Released, not merely acquired: a lease left held would stall
            // every subsequent ladder for this subscription until its TTL.
            Expect.equal recording.MaxConcurrentPerId 1 "the lease must be released on the way out"

        testCase "a healthy delivery takes no lease at all (GP 13)"
        <| fun _ ->
            // The success path's reset is guarded on the dispatch-time
            // snapshot BEFORE the lease, so a subscription that is already
            // at zero — the overwhelming majority of deliveries — pays
            // nothing. This arm is what stops that guard being "tidied"
            // inside the lease later.
            let subscriptionId = Guid.NewGuid()
            let recording = RecordingLock(InProcessDistributedLock.create ())
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let registry = WebhookRegistry.createRegistry storage
            let deliveryLog = WebhookRegistry.createDeliveryLog storage

            let succeedingClient = new HttpClient(new AlwaysSucceedingHandler())

            let dispatcher =
                WebhookDispatcher.createWithLock
                    registry
                    deliveryLog
                    eventStore
                    succeedingClient
                    retryPolicy
                    silentLogger
                    (NoOpActivitySink() :> IActivitySink)
                    (InMemorySecretStore() :> ISecretStore)
                    (fun () -> permissiveRateLimiter)
                    allowlistedPolicy
                    (recording :> IDistributedLock)

            async {
                let! _ = registry.CreateSubscription(subscriptionFor subscriptionId)
                let hosted = dispatcher :> IHostedService
                do! hosted.StartAsync CancellationToken.None |> Async.AwaitTask
                (dispatcher :> IWebhookDispatcher).Dispatch(eventFor 0)

                let! delivered =
                    waitFor (TimeSpan.FromSeconds 20.0) (fun () -> async {
                        let! rows = deliveryLog.ListRecent(scopeId, subscriptionId, 100)

                        return
                            rows
                            |> List.exists (fun r ->
                                match r.Outcome with
                                | WebhookDeliveryOutcome.Success _ -> true
                                | _ -> false)
                    })

                do! hosted.StopAsync CancellationToken.None |> Async.AwaitTask
                Expect.isTrue delivered "the delivery should have succeeded inside the deadline"
            }
            |> Async.RunSynchronously

            Expect.equal
                recording.AcquiredIds
                []
                "a subscription already at zero failures must not acquire, re-read, or write"

        testCase "two replicas sharing one lock both land their increment and auto-disable once"
        <| fun _ ->
            let subscriptionId = Guid.NewGuid()
            let shared = RecordingLock(InProcessDistributedLock.create ())

            let baseRegistry, _rendezvous, deliveryLog, eventStore, dispatchers =
                buildReplicas 2 (fun _ -> shared :> IDistributedLock) (TimeSpan.FromMilliseconds 750.0)

            let finalFailures, autoDisables =
                async {
                    let! _ = baseRegistry.CreateSubscription(subscriptionFor subscriptionId)

                    let! settled =
                        runOneDeliveryPerReplica deliveryLog (fun () -> shared.Releases) 2 subscriptionId dispatchers

                    Expect.isTrue settled "both ladders should have dead-lettered inside the deadline"

                    let! fresh = baseRegistry.GetSubscription(scopeId, subscriptionId)
                    let! disables = autoDisableRowCount eventStore
                    return (fresh |> Option.map _.ConsecutiveFailures), disables
                }
                |> Async.RunSynchronously

            Expect.equal
                finalFailures
                (Some 2)
                "each replica's increment must be read-modify-written under the lease, so two failures count as two"

            Expect.equal
                autoDisables
                1
                "crossing the threshold is ONE transition — a second WebhookSubscriptionAutoDisabled row is a duplicate fire"

            Expect.equal shared.MaxConcurrentPerId 1 "the shared lease must never be held twice for one subscription"

        testCase "CONTROL — two replicas with their own locks lose the increment (the shape the lease fixes)"
        <| fun _ ->
            // The go-red half. Same construction, same rendezvous, same
            // assertions available — the only difference is that each
            // replica holds its own lock table, which is exactly what
            // `InProcessDistributedLock` is across a process boundary. If
            // this arm ever reports 2, the arm above is passing because
            // the ladders did not overlap and proves nothing.
            let subscriptionId = Guid.NewGuid()

            // Wrapped exactly as the shared-lock arm's single lock is, so
            // the two arms are bounded by the same observable — a
            // transition is complete when its lease has been RELEASED.
            let locks: RecordingLock[] = [|
                RecordingLock(InProcessDistributedLock.create ())
                RecordingLock(InProcessDistributedLock.create ())
            |]

            let baseRegistry, _rendezvous, deliveryLog, eventStore, dispatchers =
                buildReplicas 2 (fun i -> locks[i] :> IDistributedLock) (TimeSpan.FromMilliseconds 750.0)

            let finalFailures, autoDisables =
                async {
                    let! _ = baseRegistry.CreateSubscription(subscriptionFor subscriptionId)

                    let! settled =
                        runOneDeliveryPerReplica
                            deliveryLog
                            (fun () -> locks[0].Releases + locks[1].Releases)
                            2
                            subscriptionId
                            dispatchers

                    Expect.isTrue settled "both ladders should have dead-lettered inside the deadline"

                    let! fresh = baseRegistry.GetSubscription(scopeId, subscriptionId)
                    let! disables = autoDisableRowCount eventStore
                    return (fresh |> Option.map _.ConsecutiveFailures), disables
                }
                |> Async.RunSynchronously

            Expect.equal
                finalFailures
                (Some 1)
                "without cross-instance exclusion both replicas read 0 and write 1 — the lost update this phase fixes"

            Expect.equal
                autoDisables
                0
                "and because the counter never reaches the threshold, a persistently-failing receiver is never auto-disabled"
    ]