module ToolUp.Platform.Tests.InProcess.ExternalCallbackTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tracing
open ToolUp.Platform.Tests.Contracts

// ─── Phase 320 — completion-callback ingress + idempotent resolution ──
//
// The phase's whole claim is "exactly once, whichever arrives first", so
// the pack is built around making that claim FALSIFIABLE rather than
// merely exercised:
//
//   * every idempotency assertion counts the OBSERVABLE EFFECT (one
//     `JobCompleted` event, one terminal row) and not the return value.
//     A gate that returns `false` correctly while still writing is the
//     bug this phase exists to prevent, and asserting on the return value
//     alone cannot see it.
//   * the callback-vs-poll interleave is PLACED, not raced for: the
//     second replica's poll is held inside `Poll` — past its own
//     awaiting re-verify — while the callback resolves underneath it
//     (`RendezvousDispatcher`). Deterministic in both directions, and
//     paired with a control on the identical construction with no handle
//     store that must observe TWO completions. A probabilistic control
//     was tried first and rejected: the estate already ships one of that
//     shape (Phase 190's privacy-budget over-admission control) and it
//     failed during this phase's own verification purely because the
//     operations did not interleave under load.
//   * the scope cross-check is asserted against a DELIBERATELY forged
//     record, so the check is shown to fire rather than shown not to
//     complain.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private handleFor (scope: string) (backend: string) = {
    HandleId = Guid.NewGuid()
    Backend = backend
    ScopeId = scope
    NativeRef = Guid.NewGuid().ToString "N"
    SubmittedAt = DateTime.UtcNow
}

// ─── 320.A — the store contract ──────────────────────────────────────

/// An `IBlobStorage` that does NOT implement `IConditionalBlobStorage` —
/// the shape `BlobExternalHandleStore` must refuse at construction. Same
/// bare-implementation device the Phase 600 pack uses for its capability
/// probe; it never needs to store anything, because construction is
/// refused before a read or a write is attempted.
let private plainBlobStorage () : IBlobStorage =
    { new IBlobStorage with
        // Phase 741 — no bounded multi-part commit primitive here; callers assemble through memory.
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Upload(_, _, _) = async { return Ok "" }
        member _.Download(_, _) = async { return Error "not conditional" }
        member _.DownloadRange(_, _, _, _) = async { return Error "not conditional" }
        member _.Delete(_, _) = async { return Ok() }
        member _.List(_, _) = async { return [] }
        member _.Exists(_, _) = async { return false }
        member _.GetMetadata(_, _) = async { return Error "not conditional" }
        member _.Erase(_, _, _, _) = async { return Ok(Unchecked.defaultof<_>) }
    }

// The parameterised store contract that used to live here — register /
// resolve / MarkTerminal-exactly-once / 32-way concurrency / IsDistributed,
// bound against both shipped implementations — **moved to
// `Contracts/IExternalHandleStoreContract.fs` in Phase 324.D**, where a
// companion store binds it unmodified, and where it gained the laws this
// pack could not express portably: scope partitioning as a property every
// backend must honour (it was asserted here for the blob store only, by
// writing blob names), the callback-vs-poll race reduced to the gate itself,
// `Resolve`'s non-destructiveness, and `Register`'s overwrite clause. It is
// wired into the runner directly, so it runs whether or not this pack does.
//
// What stays here is what is genuinely specific to THIS store or to the
// ingress: the per-implementation `IsDistributed` values, the
// conditional-write construction refusal, and the forged-partition test,
// which exercises `BlobExternalHandleStore`'s pointer indirection rather
// than the seam.

let private blobStore () =
    BlobExternalHandleStore(InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage) :> IExternalHandleStore

let private storeTests =
    testList "Phase 320 — IExternalHandleStore (blob-store specifics; the seam contract is Phase 324.D)" [

        test "the in-memory store declares itself NOT distributed" {
            Expect.isFalse (InMemoryExternalHandleStore() :> IExternalHandleStore).IsDistributed "in-memory"
        }

        test "the blob store declares itself distributed" { Expect.isTrue (blobStore ()).IsDistributed "blob-backed" }

        test "the blob store REFUSES a backend without conditional writes, at construction" {
            // The whole exactly-once guarantee is the CAS. A store that
            // silently degraded to download-modify-upload would read as
            // defended while racing exactly where the callback and the
            // poll meet, so the refusal is at construction and not at the
            // first callback.
            let plain = plainBlobStorage ()

            Expect.throwsT<ArgumentException>
                (fun () -> BlobExternalHandleStore plain |> ignore)
                "a non-conditional backend is refused"

            Expect.isNone (BlobExternalHandleStore.TryCreate plain) "and the probing form returns None"

            Expect.isSome
                (BlobExternalHandleStore.TryCreate(InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage))
                "while a conditional backend is accepted"
        }

        test "GP 4 — a record forged into the wrong scope partition does not resolve" {
            // The falsifiable form of the scope cross-check: write a
            // record whose own `ScopeId` disagrees with the partition it
            // sits in, exactly as a mis-pointed index blob would produce,
            // and require `Resolve` to refuse it. A test that only
            // registered honest records could not tell the check from its
            // absence.
            let blobs = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let store = BlobExternalHandleStore blobs :> IExternalHandleStore
            let handle = handleFor "team-victim" "gpu-pool"
            let _, hash = ExternalCallbackSecret.mint ()
            store.Register(handle, Guid.NewGuid(), hash) |> Async.RunSynchronously

            // Control: the honest record resolves.
            Expect.isSome (store.Resolve handle.HandleId |> Async.RunSynchronously) "the honest record resolves"

            // Now repoint the index at an attacker-controlled partition
            // and plant a record there.
            let attackerScope = "team-attacker"

            let planted =
                match store.Resolve handle.HandleId |> Async.RunSynchronously with
                | Some r -> r
                | None -> failtest "precondition"

            let options = ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

            let bytes =
                System.Text.Json.JsonSerializer.Serialize(planted, options)
                |> System.Text.Encoding.UTF8.GetBytes

            blobs.Upload("_platform", $"external-compute/handles/{attackerScope}/{handle.HandleId}.json", bytes)
            |> Async.RunSynchronously
            |> ignore

            blobs.Upload(
                "_platform",
                $"external-compute/handle-index/{handle.HandleId}",
                System.Text.Encoding.UTF8.GetBytes attackerScope
            )
            |> Async.RunSynchronously
            |> ignore

            Expect.isNone
                (store.Resolve handle.HandleId |> Async.RunSynchronously)
                "a record whose ScopeId disagrees with its partition is refused"
        }
    ]

// ─── 320.B — secret minting + verification ───────────────────────────

let private secretTests =
    testList "Phase 320 — callback secret" [
        test "mint produces distinct, high-entropy secrets" {
            let minted = List.init 64 (fun _ -> fst (ExternalCallbackSecret.mint ()))

            Expect.equal (minted |> List.distinct |> List.length) 64 "64 mints, 64 distinct secrets"

            Expect.all
                minted
                (fun s -> s.Length = ExternalCallbackSecret.SecretBytes * 2)
                "hex encoding of 32 bytes is 64 chars"
        }

        test "verify accepts the minted secret and rejects everything else" {
            let secret, hash = ExternalCallbackSecret.mint ()

            Expect.isTrue (ExternalCallbackSecret.verify hash secret) "the right secret verifies"

            let other, _ = ExternalCallbackSecret.mint ()
            Expect.isFalse (ExternalCallbackSecret.verify hash other) "a different secret does not"
            Expect.isFalse (ExternalCallbackSecret.verify hash "") "the empty secret does not"
            Expect.isFalse (ExternalCallbackSecret.verify hash (secret.ToLowerInvariant())) "case-shifted does not"

            Expect.isFalse
                (ExternalCallbackSecret.verify hash (secret.Substring(0, secret.Length - 1)))
                "a truncated secret does not"

            Expect.isFalse (ExternalCallbackSecret.verify "" secret) "and an empty stored hash accepts nothing"
        }
    ]

// ─── 320.C — the wire contract ───────────────────────────────────────

let private wireTests =
    testList "Phase 320 — callback wire contract" [
        test "the three terminal statuses round-trip through ofOutcome / toOutcome" {
            let handleId = Guid.NewGuid()

            let cases = [
                ExternalOutcome.Succeeded "s3://bucket/result.bin"
                ExternalOutcome.Failed {
                    Message = "cuda oom"
                    Retriable = true
                }
                ExternalOutcome.Failed {
                    Message = "unknown kind"
                    Retriable = false
                }
                ExternalOutcome.Cancelled
            ]

            for outcome in cases do
                match ExternalCallback.ofOutcome handleId outcome with
                | Error e -> failtestf "ofOutcome refused a terminal outcome: %s" e
                | Ok payload ->
                    Expect.equal payload.HandleId handleId "handle id survives"

                    match ExternalCallback.toOutcome payload with
                    | Error e -> failtestf "toOutcome refused its own output: %s" e
                    | Ok round -> Expect.equal round outcome "round-trip is exact"
        }

        test "non-terminal statuses are refused on both sides" {
            let handleId = Guid.NewGuid()

            for outcome in [ ExternalOutcome.Pending; ExternalOutcome.Running(Some 0.5) ] do
                Expect.isError (ExternalCallback.ofOutcome handleId outcome) "ofOutcome refuses non-terminal"

            for status in [ "pending"; "running"; "PENDING" ] do
                let payload = {
                    HandleId = handleId
                    Status = status
                    ResultRef = None
                    Error = None
                    Retriable = None
                }

                Expect.isError (ExternalCallback.toOutcome payload) $"toOutcome refuses '{status}'"
        }

        test "status parsing is case- and whitespace-insensitive, and refuses the unrecognised" {
            let mk status resultRef = {
                HandleId = Guid.NewGuid()
                Status = status
                ResultRef = resultRef
                Error = None
                Retriable = None
            }

            Expect.isOk (ExternalCallback.toOutcome (mk "  SUCCEEDED " (Some "ref"))) "trimmed + upper-cased parses"
            Expect.isError (ExternalCallback.toOutcome (mk "done" (Some "ref"))) "an unrecognised status is refused"
            Expect.isError (ExternalCallback.toOutcome (mk "" (Some "ref"))) "an empty status is refused"
        }

        test "succeeded needs a resultRef; failed needs an error; retriable defaults to terminal" {
            let handleId = Guid.NewGuid()

            let bare status = {
                HandleId = handleId
                Status = status
                ResultRef = None
                Error = None
                Retriable = None
            }

            Expect.isError (ExternalCallback.toOutcome (bare "succeeded")) "succeeded without a resultRef"

            Expect.isError
                (ExternalCallback.toOutcome {
                    bare "succeeded" with
                        ResultRef = Some "   "
                })
                "succeeded with a whitespace resultRef"

            Expect.isError (ExternalCallback.toOutcome (bare "failed")) "failed without an error"

            // A backend that does not say whether a failure is worth
            // retrying is not asserting that it is. Defaulting the other
            // way would re-submit external work on a backend's silence.
            match
                ExternalCallback.toOutcome {
                    bare "failed" with
                        Error = Some "boom"
                }
            with
            | Ok(ExternalOutcome.Failed e) -> Expect.isFalse e.Retriable "absent retriable means terminal"
            | other -> failtestf "unexpected: %A" other

            // Cancelled needs nothing.
            Expect.isOk (ExternalCallback.toOutcome (bare "cancelled")) "cancelled needs no extra field"
        }

        test "the route and header names are pinned — they are a published contract" {
            Expect.equal ExternalCallback.Route "/_platform/external-compute/callback" "route"
            Expect.equal ExternalCallback.SecretHeader "X-ToolUp-External-Callback-Secret" "header"
        }
    ]

// ─── Scheduler integration: registration, push resolution, the race ──

/// A dispatcher that also declares the Phase 320 callback capability, so
/// the credential hand-off can be asserted to have happened.
type private CallbackCapableDispatcher() =
    let credentials =
        System.Collections.Concurrent.ConcurrentDictionary<Guid, ExternalCallbackCredential>()

    let mutable polls = 0
    let mutable outcome = ExternalOutcome.Pending

    member _.PollCount = polls

    member _.CredentialFor(handleId: Guid) =
        match credentials.TryGetValue handleId with
        | true, c -> Some c
        | _ -> None

    member _.CredentialCount = credentials.Count
    member _.SetOutcome(o: ExternalOutcome) = outcome <- o

    interface IExternalComputeDispatcher with
        member _.Backend = "gpu-pool"

        member _.Submit(scopeId: string, _spec: ExternalWorkSpec) = async {
            return
                Ok {
                    HandleId = Guid.NewGuid()
                    Backend = "gpu-pool"
                    ScopeId = scopeId
                    NativeRef = Guid.NewGuid().ToString "N"
                    SubmittedAt = DateTime.UtcNow
                }
        }

        member _.Poll(_handle: ExternalHandle) = async {
            polls <- polls + 1
            return outcome
        }

        member _.Cancel(_handle: ExternalHandle) = async { return () }

    interface IExternalCallbackCapableBackend with
        member _.AcceptCallbackCredential(handle: ExternalHandle, credential: ExternalCallbackCredential) = async {
            credentials[handle.HandleId] <- credential
        }

/// A dispatcher that does NOT declare the capability — used to pin GP 11
/// (a backend that never opted in is never handed a secret).
type private PlainDispatcher() =
    let mutable outcome = ExternalOutcome.Pending
    member _.SetOutcome(o: ExternalOutcome) = outcome <- o

    interface IExternalComputeDispatcher with
        member _.Backend = "plain-pool"

        member _.Submit(scopeId: string, _spec: ExternalWorkSpec) = async {
            return
                Ok {
                    HandleId = Guid.NewGuid()
                    Backend = "plain-pool"
                    ScopeId = scopeId
                    NativeRef = "n"
                    SubmittedAt = DateTime.UtcNow
                }
        }

        member _.Poll(_handle) = async { return outcome }
        member _.Cancel(_handle) = async { return () }

/// A dispatcher that lets a test **place** the interleave instead of
/// hoping for it: `Poll` announces that it has been reached and then
/// blocks until released.
///
/// This is what turns the callback-vs-poll race from a probabilistic test
/// into a deterministic one, and the reason it is worth the machinery is
/// visible in this very repo — the Phase 190 privacy-budget pack ships an
/// "an ungated backend must over-admit" control of exactly the hopeful
/// shape, and it failed in this phase's own verification run purely
/// because the two operations did not happen to interleave under load. A
/// control that only sometimes observes the thing it exists to observe is
/// a flake that reads as a proof.
///
/// **Why blocking inside `Poll` lands the interleave in the right place.**
/// The reconciliation pass re-verifies that the run is still
/// `AwaitingExternal`, *then* calls `Poll`, *then* applies the outcome. So
/// a poll held here has already passed its re-verify with a view that is
/// about to go stale — exactly the position a second replica occupies when
/// a callback resolves the run underneath it. Every guard except
/// `MarkTerminal` has already been satisfied, which is what makes the
/// resulting assertion about the gate and nothing else.
type private RendezvousDispatcher(outcome: ExternalOutcome) =
    let reached = new System.Threading.ManualResetEventSlim(false)
    let release = new System.Threading.ManualResetEventSlim(false)

    /// Block until the reconciliation pass has entered `Poll`.
    member _.WaitUntilPolling(timeoutMs: int) =
        if not (reached.Wait timeoutMs) then
            failtest "the reconciliation pass never reached Poll"

    /// Let the held poll continue into the terminal-drive path.
    member _.Release() = release.Set()

    interface IExternalComputeDispatcher with
        member _.Backend = "rendezvous-pool"

        member _.Submit(scopeId: string, _spec: ExternalWorkSpec) = async {
            return
                Ok {
                    HandleId = Guid.NewGuid()
                    Backend = "rendezvous-pool"
                    ScopeId = scopeId
                    NativeRef = Guid.NewGuid().ToString "N"
                    SubmittedAt = DateTime.UtcNow
                }
        }

        member _.Poll(_handle: ExternalHandle) = async {
            reached.Set()

            if not (release.Wait 30_000) then
                failtest "the rendezvous was never released"

            return outcome
        }

        member _.Cancel(_handle: ExternalHandle) = async { return () }

/// A handler that submits and hands off — the shape Phase 319 exists for.
type private HandOffHandler(dispatcher: IExternalComputeDispatcher) =
    interface IJobHandler with
        member _.Execute ctx = async {
            match! dispatcher.Submit(ctx.ScopeId, ExternalWorkSpec.create "train" ctx.Payload) with
            | Ok handle -> return HandedOff handle
            | Error e -> return PermanentFailure e.Message
        }

type private Fixture = {
    Store: IJobStore
    EventStore: IEventStore
    HandleStore: IExternalHandleStore option
    Scheduler: JobScheduler.InProcessJobScheduler
}

/// **`InMemoryBlobStorage`, deliberately, and NOT `LocalFileStorage`.**
///
/// The race tests below fire the callback and the reconciliation poll at
/// the same run. `LocalFileStorage` cannot survive that on Windows for a
/// reason that has nothing to do with this phase: a run row's blob name is
/// derived from `StartedAt` + `RunId`, so the awaiting row and the
/// terminal row are the SAME file, and one path's read (the callback's
/// unleased locator query) overlapping the other path's write raises a
/// file-sharing violation out of `Upload`. That is a property of the
/// dev-only filesystem backend, not of the gate — and a test that failed
/// on it would be measuring `FileShare` flags while claiming to measure
/// exactly-once semantics.
///
/// (The `LocalFileStorage` concurrency gap is real and logged as an
/// out-of-scope finding rather than smuggled into this phase.)
let private build (dispatcher: IExternalComputeDispatcher) (handleStore: IExternalHandleStore option) : Fixture =
    let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let jobStore = JobStore.create storage eventStore

    let channel =
        { new INotificationChannel with
            member _.Publish(_, _) = async { return () }
            member _.Subscribe(_, _) = async { return Guid.NewGuid() }
            member _.Unsubscribe(_) = async { return () }
        }

    let scheduler =
        match handleStore with
        | Some hs ->
            new JobScheduler.InProcessJobScheduler(
                jobStore,
                eventStore,
                channel,
                ServerConfig.defaults,
                silentLogger,
                NoOpActivitySink() :> IActivitySink,
                distributedLock = InProcessDistributedLock.create (),
                externalDispatcher = dispatcher,
                externalHandleStore = hs
            )
        | None ->
            new JobScheduler.InProcessJobScheduler(
                jobStore,
                eventStore,
                channel,
                ServerConfig.defaults,
                silentLogger,
                NoOpActivitySink() :> IActivitySink,
                distributedLock = InProcessDistributedLock.create (),
                externalDispatcher = dispatcher
            )

    {
        Store = jobStore
        EventStore = eventStore
        HandleStore = handleStore
        Scheduler = scheduler
    }

/// Two schedulers over ONE job store and ONE handle store, each with its
/// **own** `IDistributedLock` — the multi-replica topology, modelled.
///
/// This is the shape the `MarkTerminal` CAS actually defends, and building
/// it explicitly is what makes the phase's claim testable. Within a single
/// process the per-`JobId` lease plus the awaiting re-verify already give
/// exactly-once, so a single-instance race cannot distinguish a working
/// gate from no gate at all — measured, not assumed: the ungated
/// single-process control produced zero doubles in 40 rounds. Across
/// replicas there is no shared lease and no shared awaiting view, both
/// callers pass their own re-verify, and the CAS is the only thing left
/// between them.
let private buildPair
    (dispatcher: IExternalComputeDispatcher)
    (handleStore: IExternalHandleStore option)
    : Fixture * Fixture =
    let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let jobStore = JobStore.create storage eventStore

    let channel =
        { new INotificationChannel with
            member _.Publish(_, _) = async { return () }
            member _.Subscribe(_, _) = async { return Guid.NewGuid() }
            member _.Unsubscribe(_) = async { return () }
        }

    let mk () =
        let scheduler =
            match handleStore with
            | Some hs ->
                new JobScheduler.InProcessJobScheduler(
                    jobStore,
                    eventStore,
                    channel,
                    ServerConfig.defaults,
                    silentLogger,
                    NoOpActivitySink() :> IActivitySink,
                    // A DISTINCT lock per instance — two replicas do not
                    // share an in-process mutex, and pretending they do is
                    // how a single-instance test claims a distributed
                    // guarantee.
                    distributedLock = InProcessDistributedLock.create (),
                    externalDispatcher = dispatcher,
                    externalHandleStore = hs
                )
            | None ->
                new JobScheduler.InProcessJobScheduler(
                    jobStore,
                    eventStore,
                    channel,
                    ServerConfig.defaults,
                    silentLogger,
                    NoOpActivitySink() :> IActivitySink,
                    distributedLock = InProcessDistributedLock.create (),
                    externalDispatcher = dispatcher
                )

        {
            Store = jobStore
            EventStore = eventStore
            HandleStore = handleStore
            Scheduler = scheduler
        }

    mk (), mk ()

let private scheduleAndHandOff (fixture: Fixture) (handler: IJobHandler) : string * JobId * JobRun =
    let scheduler = fixture.Scheduler :> IJobScheduler
    let scope = "ext-" + Guid.NewGuid().ToString("N").Substring(0, 8)
    scheduler.RegisterHandler("ext-handler", handler)

    let registration: JobRegistration = {
        ScopeId = scope
        Handler = "ext-handler"
        Payload = """{"model":"forecast"}"""
        Trigger = Manual
        Idempotency = None
        RetryPolicy = {
            JobRetryPolicy.defaults with
                MaxAttempts = 1
                InitialBackoff = TimeSpan.Zero
                MaxBackoff = TimeSpan.Zero
        }
        ShardKey = None
        Precision = Minute
        CreatedBy = "test"
        Tags = Map.empty
    }

    let jobId =
        match scheduler.Schedule registration |> Async.RunSynchronously with
        | Ok id -> id
        | Error e -> failtestf "schedule failed: %A" e

    match scheduler.TriggerOnce(scope, jobId, "test") |> Async.RunSynchronously with
    | Ok() -> ()
    | Error e -> failtestf "TriggerOnce failed: %s" e

    // Wait for the hand-off to land. `AwaitingExternal` is written before
    // the lease is released, so the row is the FIRST settle signal — but
    // not the last write of the hand-off. The production ordering is
    // deliberate (see the `HandedOff` arm in `JobScheduler.fs`): the row
    // is made durable first so a crash degrades to poll-based resolution,
    // and the handle-store registration, the credential push and the
    // `JobExternalHandedOff` event all land AFTER it. A settle wait that
    // trusts the row alone can observe the row and race ahead of the
    // handle registration — a load-sensitive flake, observed 2026-08-25.
    // The event is emitted after every other write in the hand-off, so IT
    // is the signal that the whole hand-off has settled.
    //
    // Bounded polls rather than a monitor wait: the stores here are the
    // real implementations, not fixtures we can pulse. Each timeout names
    // the write that was missing, so a slow machine fails attributed to
    // the settle wait — never misreported as the domain claim under test.
    let deadline = DateTime.UtcNow.AddSeconds 15.0

    let rec awaitRow () =
        let latest =
            fixture.Store.GetRecentRuns(scope, jobId, 20)
            |> Async.RunSynchronously
            |> List.tryHead

        match latest with
        | Some r when r.Status = AwaitingExternal -> r
        | _ when DateTime.UtcNow < deadline ->
            System.Threading.Thread.Sleep 25
            awaitRow ()
        | _ ->
            failtest
                "settle wait: the run never reached AwaitingExternal within 15s — the hand-off's row write is missing"

    let run = awaitRow ()

    let rec awaitHandedOffEvent () =
        let emitted =
            fixture.EventStore.ReadAll scope
            |> Async.RunSynchronously
            |> List.exists (fun e -> e.EventType = "JobExternalHandedOff")

        if emitted then
            ()
        elif DateTime.UtcNow < deadline then
            System.Threading.Thread.Sleep 25
            awaitHandedOffEvent ()
        else
            failtest
                "settle wait: the run reached AwaitingExternal but JobExternalHandedOff never arrived within 15s — the hand-off's post-row writes (handle registration / credential push / event emit) did not settle; this is the settle wait timing out, not the domain claim under test"

    awaitHandedOffEvent ()
    scope, jobId, run

let private eventTypes (fixture: Fixture) (scope: string) =
    fixture.EventStore.ReadAll scope
    |> Async.RunSynchronously
    |> List.map _.EventType

let private latestRun (fixture: Fixture) scope jobId =
    fixture.Store.GetRecentRuns(scope, jobId, 20)
    |> Async.RunSynchronously
    |> List.tryHead

let private schedulerTests =
    testList "Phase 320 — scheduler integration" [

        test "320.A/B — hand-off registers the handle and hands the backend a working credential" {
            let dispatcher = CallbackCapableDispatcher()
            let handleStore = InMemoryExternalHandleStore() :> IExternalHandleStore
            let fixture = build dispatcher (Some handleStore)
            let _, _, run = scheduleAndHandOff fixture (HandOffHandler(dispatcher))
            let handle = run.ExternalHandle.Value

            match handleStore.Resolve handle.HandleId |> Async.RunSynchronously with
            | None -> failtest "the hand-off did not register its handle"
            | Some record ->
                Expect.equal record.JobRunId run.RunId "the record routes to THIS run"
                Expect.equal record.Handle.ScopeId run.ScopeId "and carries the run's scope"

                // The credential the backend received must actually
                // authenticate against the stored hash. Asserting only
                // that a credential arrived would pass if the two halves
                // were minted independently.
                match dispatcher.CredentialFor handle.HandleId with
                | None -> failtest "the callback-capable backend was handed no credential"
                | Some credential ->
                    Expect.equal credential.HandleId handle.HandleId "credential names the handle"
                    Expect.equal credential.CallbackPath ExternalCallback.Route "credential carries the ingress path"

                    Expect.isTrue
                        (ExternalCallbackSecret.verify record.CallbackSecretHash credential.Secret)
                        "the backend's secret verifies against the stored hash"

                    Expect.notEqual credential.Secret record.CallbackSecretHash "and the two are not the same value"
        }

        test "GP 11 — a backend that never declared the capability is handed no secret" {
            let dispatcher = PlainDispatcher()
            let handleStore = InMemoryExternalHandleStore() :> IExternalHandleStore
            let fixture = build dispatcher (Some handleStore)
            let _, _, run = scheduleAndHandOff fixture (HandOffHandler(dispatcher))

            // The handle is still registered — the poll path is unchanged
            // and the store is what makes it exactly-once — but nothing
            // was pushed at a backend with no code path for it.
            Expect.isSome
                (handleStore.Resolve run.ExternalHandle.Value.HandleId |> Async.RunSynchronously)
                "the handle is registered regardless"
        }

        test "GP 11 — a scheduler with NO handle store behaves exactly as Phase 319" {
            let dispatcher = CallbackCapableDispatcher()
            let fixture = build dispatcher None
            let scope, jobId, _ = scheduleAndHandOff fixture (HandOffHandler(dispatcher))

            Expect.equal dispatcher.CredentialCount 0 "no store means no secret is minted or handed out"

            dispatcher.SetOutcome(ExternalOutcome.Succeeded "s3://r")
            fixture.Scheduler.ReconcileAwaitingExternal() |> Async.RunSynchronously

            Expect.equal (latestRun fixture scope jobId).Value.Status Succeeded "the poll still resolves the run"

            Expect.contains (eventTypes fixture scope) "JobCompleted" "and still emits JobCompleted"
        }

        test "320.C — a pushed outcome resolves the run with NO poll" {
            let dispatcher = CallbackCapableDispatcher()
            let handleStore = InMemoryExternalHandleStore() :> IExternalHandleStore
            let fixture = build dispatcher (Some handleStore)
            let scope, jobId, run = scheduleAndHandOff fixture (HandOffHandler(dispatcher))
            let sink = fixture.Scheduler :> IExternalCompletionSink

            let resolution =
                sink.ResolveExternal(run.ExternalHandle.Value, run.RunId, ExternalOutcome.Succeeded "s3://out")
                |> Async.RunSynchronously

            Expect.equal resolution (ExternalResolution.Resolved "succeeded") "the callback won the claim"
            Expect.equal (latestRun fixture scope jobId).Value.Status Succeeded "the run is terminal"

            let events = eventTypes fixture scope
            Expect.contains events "JobCompleted" "the standard terminal event fired"
            Expect.contains events "JobExternalReconciled" "alongside the external companion event"

            Expect.isEmpty
                (fixture.Store.AwaitingExternalRuns(scope, 100) |> Async.RunSynchronously)
                "the run left the awaiting index"

            // THE latency assertion. A push path that quietly polls is
            // not a push path.
            Expect.equal dispatcher.PollCount 0 "the backend was never polled"
        }

        test "320.C/D — a duplicate callback is a no-op, counted by EFFECT not by return value" {
            let dispatcher = CallbackCapableDispatcher()
            let handleStore = InMemoryExternalHandleStore() :> IExternalHandleStore
            let fixture = build dispatcher (Some handleStore)
            let scope, jobId, run = scheduleAndHandOff fixture (HandOffHandler(dispatcher))
            let sink = fixture.Scheduler :> IExternalCompletionSink
            let handle = run.ExternalHandle.Value

            let first =
                sink.ResolveExternal(handle, run.RunId, ExternalOutcome.Succeeded "s3://out")
                |> Async.RunSynchronously

            // A different outcome on the replay, deliberately: if the
            // duplicate were applied it would OVERWRITE the terminal
            // status, so the run row itself becomes evidence.
            let second =
                sink.ResolveExternal(handle, run.RunId, ExternalOutcome.Cancelled)
                |> Async.RunSynchronously

            let third =
                sink.ResolveExternal(handle, run.RunId, ExternalOutcome.Cancelled)
                |> Async.RunSynchronously

            Expect.equal first (ExternalResolution.Resolved "succeeded") "the first call resolved"

            // The run left `AwaitingExternal` after the first call, so the
            // replays are refused at the awaiting re-verify — which is the
            // second, independent line of defence and reported as such.
            Expect.isTrue
                (second = ExternalResolution.AlreadyResolved
                 || second = ExternalResolution.NoAwaitingRun)
                $"the replay was refused (got %A{second})"

            Expect.isTrue
                (third = ExternalResolution.AlreadyResolved
                 || third = ExternalResolution.NoAwaitingRun)
                $"and so was the third (got %A{third})"

            Expect.equal
                (latestRun fixture scope jobId).Value.Status
                Succeeded
                "the terminal status was NOT overwritten"

            let completions =
                eventTypes fixture scope |> List.filter ((=) "JobCompleted") |> List.length

            Expect.equal completions 1 "exactly one JobCompleted across three callbacks"
        }

        test "320.D — callback and poll fired CONCURRENTLY resolve exactly once (20 rounds)" {
            // The single-process race. Note what this pins and what it
            // does NOT: within one process the per-`JobId` lease plus the
            // awaiting re-verify already serialise the two callers, so
            // this passing does not by itself demonstrate the CAS gate
            // works — measured, not assumed (an ungated single-process
            // control produced zero doubles in 40 rounds, which is why it
            // is not the control this pack ships). It pins that adding
            // the gate did not BREAK the path that already worked. The
            // gate's own claim is pinned by the multi-instance pair
            // below, which has no shared lease.
            for round in 1..20 do
                let dispatcher = CallbackCapableDispatcher()
                let handleStore = InMemoryExternalHandleStore() :> IExternalHandleStore
                let fixture = build dispatcher (Some handleStore)
                let scope, jobId, run = scheduleAndHandOff fixture (HandOffHandler(dispatcher))
                let sink = fixture.Scheduler :> IExternalCompletionSink
                dispatcher.SetOutcome(ExternalOutcome.Succeeded "s3://poll")

                let callback =
                    sink.ResolveExternal(run.ExternalHandle.Value, run.RunId, ExternalOutcome.Succeeded "s3://push")
                    |> Async.Ignore
                    |> Async.StartAsTask

                let poll = fixture.Scheduler.ReconcileAwaitingExternal() |> Async.StartAsTask

                Task.WaitAll([| callback :> Task; poll :> Task |], 30_000) |> ignore

                let completions =
                    eventTypes fixture scope |> List.filter ((=) "JobCompleted") |> List.length

                Expect.equal completions 1 $"round {round}: exactly one JobCompleted"

                Expect.equal
                    (latestRun fixture scope jobId).Value.Status
                    Succeeded
                    $"round {round}: the run is terminal exactly once"
        }

        test "320.D — MULTI-INSTANCE callback vs poll resolves exactly once (deterministic)" {
            // The topology the CAS gate exists for, with the interleave
            // PLACED rather than hoped for: replica B's poll is held
            // inside `Poll` — past its own awaiting re-verify — while
            // replica A's callback resolves the run underneath it. Then B
            // is released and runs straight into the terminal-drive path
            // with a stale view.
            //
            // Every guard except `MarkTerminal` has already been satisfied
            // by the time B proceeds, so exactly one completion here is a
            // statement about the gate and nothing else.
            let dispatcher = RendezvousDispatcher(ExternalOutcome.Succeeded "s3://poll")

            // ONE handle store, shared — a per-replica store would be
            // per-replica gating, which is exactly why compose refuses to
            // fall back to an in-memory one on a multi-replica deployment.
            let shared =
                BlobExternalHandleStore(InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage)
                :> IExternalHandleStore

            let replicaA, replicaB = buildPair dispatcher (Some shared)
            let scope, jobId, run = scheduleAndHandOff replicaA (HandOffHandler(dispatcher))

            // B starts reconciling and parks inside Poll.
            let poll = replicaB.Scheduler.ReconcileAwaitingExternal() |> Async.StartAsTask
            dispatcher.WaitUntilPolling 30_000

            // A's callback wins the claim and drives the run to terminal
            // while B is still holding a pre-resolution view.
            let callback =
                (replicaA.Scheduler :> IExternalCompletionSink)
                    .ResolveExternal(run.ExternalHandle.Value, run.RunId, ExternalOutcome.Succeeded "s3://push")
                |> Async.RunSynchronously

            Expect.equal callback (ExternalResolution.Resolved "succeeded") "the callback won the claim"

            dispatcher.Release()
            Expect.isTrue (poll.Wait 60_000) "the released poll completed"

            let completions =
                eventTypes replicaA scope |> List.filter ((=) "JobCompleted") |> List.length

            Expect.equal completions 1 "exactly one JobCompleted across two replicas"

            Expect.equal (latestRun replicaA scope jobId).Value.Status Succeeded "the run is terminal exactly once"
        }

        test "320.D control — WITHOUT the gate, the identical interleave DOES double-resolve" {
            // The control, and the reason the test above is worth
            // anything. Byte-for-byte the same construction with no handle
            // store — i.e. Phase 319 exactly — and the second completion
            // appears. Deterministic in both directions: this is not "a
            // double is possible", it is "a double happens", so a
            // regression that silently removed the gate could not slip
            // through on a lucky scheduling.
            let dispatcher = RendezvousDispatcher(ExternalOutcome.Succeeded "s3://poll")
            let replicaA, replicaB = buildPair dispatcher None
            let scope, _, run = scheduleAndHandOff replicaA (HandOffHandler(dispatcher))

            let poll = replicaB.Scheduler.ReconcileAwaitingExternal() |> Async.StartAsTask
            dispatcher.WaitUntilPolling 30_000

            (replicaA.Scheduler :> IExternalCompletionSink)
                .ResolveExternal(run.ExternalHandle.Value, run.RunId, ExternalOutcome.Succeeded "s3://push")
            |> Async.RunSynchronously
            |> ignore

            dispatcher.Release()
            Expect.isTrue (poll.Wait 60_000) "the released poll completed"

            let completions =
                eventTypes replicaA scope |> List.filter ((=) "JobCompleted") |> List.length

            Expect.equal
                completions
                2
                "an ungated pair of replicas MUST double-resolve on this interleave — if it does not, the gated test above is not measuring the gate"
        }

        test "320.C — GP 4: a handle whose scope disagrees with the run's is refused, nothing written" {
            let dispatcher = CallbackCapableDispatcher()
            let handleStore = InMemoryExternalHandleStore() :> IExternalHandleStore
            let fixture = build dispatcher (Some handleStore)
            let scope, jobId, run = scheduleAndHandOff fixture (HandOffHandler(dispatcher))
            let sink = fixture.Scheduler :> IExternalCompletionSink

            let forged = {
                run.ExternalHandle.Value with
                    ScopeId = "team-attacker"
            }

            let resolution =
                sink.ResolveExternal(forged, run.RunId, ExternalOutcome.Succeeded "s3://stolen")
                |> Async.RunSynchronously

            // The forged scope makes the awaiting lookup miss entirely
            // (the query is scoped by the handle), which is the outer
            // layer of the same GP 4 property: a caller cannot steer the
            // resolution at a scope it does not own the handle in.
            Expect.equal resolution ExternalResolution.NoAwaitingRun "the cross-scope resolution found nothing"

            Expect.equal (latestRun fixture scope jobId).Value.Status AwaitingExternal "the real run is untouched"

            Expect.isFalse (eventTypes fixture scope |> List.contains "JobCompleted") "and no completion was emitted"

            // The handle was never claimed, so the honest callback still
            // works — a refusal that consumed the claim would strand the
            // run.
            Expect.equal
                (sink.ResolveExternal(run.ExternalHandle.Value, run.RunId, ExternalOutcome.Succeeded "s3://real")
                 |> Async.RunSynchronously)
                (ExternalResolution.Resolved "succeeded")
                "the genuine callback still resolves afterwards"
        }

    // NOTE — the scheduler's own `run.ScopeId <> handle.ScopeId` guard
    // inside the shared terminal-drive path is deliberately NOT
    // exercised here, and saying so is more useful than a test that
    // pretends to. It is unreachable through the public seam by
    // construction: the awaiting lookup is scoped BY the handle, so a
    // run it returns always carries the handle's scope. The guard
    // exists for the case where those two stores are made to disagree
    // by something outside this seam (a hand-edited row, a store that
    // loses the field on a round-trip), and the reachable half of the
    // same GP 4 property — a record whose ScopeId disagrees with its
    // blob partition — is pinned by the forged-partition test in
    // `storeTests` above. A test that reached in and mutated a private
    // closure to "cover" this line would assert nothing about the
    // deployed system.
    ]

// ─── 320.C/E — the ingress over a real HTTP pipeline ─────────────────
//
// The tests above exercise the ingress's PARTS (the store, the wire
// contract, the sink). These exercise the endpoint itself over a real
// Giraffe pipeline, because the acceptance criteria are stated in terms of
// what a caller observes — a 403 for a wrong secret, a 200 for a
// duplicate — and none of that is reachable from the parts.

/// Collects audit events so the "every resolution is audited" and "every
/// refusal is audited" criteria can be asserted rather than assumed.
type private CapturingAuditLog() =
    let events = System.Collections.Concurrent.ConcurrentQueue<string * AuditEvent>()

    member _.Events = events |> List.ofSeq
    member this.Kinds = this.Events |> List.map (snd >> AuditEvent.eventTypeName)

    interface IAuditLog with
        member _.Record(scopeId: string, audit: AuditEvent) = async { events.Enqueue((scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

/// Records warnings so the rate-limited forged-callback signal can be
/// asserted present — and asserted SUPPRESSED on the repeat, which is the
/// half that a "the warning fired" test would miss.
type private CapturingLogger() =
    let warns = System.Collections.Concurrent.ConcurrentQueue<string>()

    member _.Warnings = warns |> List.ofSeq

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn msg = warns.Enqueue msg
        member _.Error(_, _) = ()

/// A sink that records what it was asked to resolve and answers with a
/// scripted resolution — so the handler's HTTP mapping is tested
/// independently of the scheduler's terminal-drive.
type private ScriptedSink(answer: ExternalResolution) =
    let calls = System.Collections.Concurrent.ConcurrentQueue<Guid * Guid>()

    member _.Calls = calls |> List.ofSeq

    interface IExternalCompletionSink with
        member _.ResolveExternal(handle: ExternalHandle, jobRunId: Guid, _outcome: ExternalOutcome) = async {
            calls.Enqueue((handle.HandleId, jobRunId))
            return answer
        }

type private IngressHarness = {
    Client: HttpClient
    Audit: CapturingAuditLog
    Logger: CapturingLogger
    Store: IExternalHandleStore option
    Sink: ScriptedSink option
    Dispose: unit -> unit
}

let private buildIngress (store: IExternalHandleStore option) (sink: ScriptedSink option) : IngressHarness =
    // Throttle + warning state is module-level (it is a per-process
    // counter, which is what the ingress documents), so it must be reset
    // per harness or the tests inherit each other's counts.
    ExternalComputeCallback.resetThrottleState ()

    let audit = CapturingAuditLog()
    let logger = CapturingLogger()

    let host =
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost
                    .UseTestServer()
                    .ConfigureServices(fun (services: IServiceCollection) ->
                        // `AddGiraffe()` is what `ComposeBootstrap` does for a
                        // real deployment; the handler's `WriteJsonAsync`
                        // resolves Giraffe's `ISerializer` from DI, so a
                        // harness without it fails where no deployment can.
                        services.AddGiraffe() |> ignore
                        services.AddSingleton<IAuditLog>(audit :> IAuditLog) |> ignore
                        services.AddSingleton<ILogger>(logger :> ILogger) |> ignore

                        store
                        |> Option.iter (fun s -> services.AddSingleton<IExternalHandleStore>(s) |> ignore)

                        sink
                        |> Option.iter (fun s ->
                            services.AddSingleton<IExternalCompletionSink>(s :> IExternalCompletionSink)
                            |> ignore))
                    .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe(choose ExternalComputeCallback.routes))
                |> ignore)
            .Build()

    host.Start()

    {
        Client = host.GetTestClient()
        Audit = audit
        Logger = logger
        Store = store
        Sink = sink
        Dispose = fun () -> host.Dispose()
    }

/// POST a callback body, optionally with a secret header.
let private postCallback (h: IngressHarness) (secret: string option) (body: string) =
    let req =
        new HttpRequestMessage(
            HttpMethod.Post,
            ExternalCallback.Route,
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        )

    secret
    |> Option.iter (fun s -> req.Headers.Add(ExternalCallback.SecretHeader, s))

    h.Client.SendAsync req |> Async.AwaitTask |> Async.RunSynchronously

let private bodyOf (response: HttpResponseMessage) =
    response.Content.ReadAsStringAsync()
    |> Async.AwaitTask
    |> Async.RunSynchronously

/// A registered handle plus the cleartext secret for it.
let private registered (store: IExternalHandleStore) (scope: string) =
    let handle = handleFor scope "gpu-pool"
    let runId = Guid.NewGuid()
    let secret, hash = ExternalCallbackSecret.mint ()
    store.Register(handle, runId, hash) |> Async.RunSynchronously
    handle, runId, secret

let private payloadFor (handleId: Guid) =
    sprintf """{"handleId":"%O","status":"succeeded","resultRef":"s3://out"}""" handleId

let private ingressTests =
    testList "Phase 320 — ingress over a real HTTP pipeline" [

        test "a valid callback resolves and is audited" {
            let store = InMemoryExternalHandleStore() :> IExternalHandleStore
            let sink = ScriptedSink(ExternalResolution.Resolved "succeeded")
            let h = buildIngress (Some store) (Some sink)

            try
                let handle, runId, secret = registered store "team-alpha"
                let response = postCallback h (Some secret) (payloadFor handle.HandleId)

                Expect.equal response.StatusCode HttpStatusCode.OK "200"
                Expect.stringContains (bodyOf response) "resolved" "the body names the resolution"

                // The sink was handed THIS handle and THIS run — a handler
                // that resolved the record but drove the wrong run would
                // pass a status-code-only assertion.
                Expect.equal sink.Calls [ handle.HandleId, runId ] "the sink was driven with the stored routing"

                Expect.contains h.Audit.Kinds "ExternalCallbackResolved" "the resolution was audited"

                Expect.isEmpty
                    (h.Audit.Kinds |> List.filter ((=) "ExternalCallbackRejected"))
                    "and nothing was recorded as a refusal"
            finally
                h.Dispose()
        }

        test "320.E — a WRONG secret is refused 403, audited, and warned about" {
            let store = InMemoryExternalHandleStore() :> IExternalHandleStore
            let sink = ScriptedSink(ExternalResolution.Resolved "succeeded")
            let h = buildIngress (Some store) (Some sink)

            try
                let handle, _, _ = registered store "team-alpha"
                let wrong, _ = ExternalCallbackSecret.mint ()
                let response = postCallback h (Some wrong) (payloadFor handle.HandleId)

                Expect.equal response.StatusCode HttpStatusCode.Forbidden "403, not 401"

                // THE assertion that the gate held: a forged callback must
                // not reach the sink at all.
                Expect.isEmpty sink.Calls "the sink was never driven"

                Expect.contains h.Audit.Kinds "ExternalCallbackRejected" "the refusal was audited"

                Expect.isTrue
                    (h.Logger.Warnings
                     |> List.exists (fun w -> w.Contains "callback_refused" && w.Contains "secret-mismatch"))
                    "a forged-callback warning names the reason"

                // Uniform refusal: the body must not disclose which gate
                // failed, or the endpoint is an oracle.
                let body = bodyOf response
                Expect.isFalse (body.Contains "secret") "the response does not name the failing gate"
                Expect.isFalse (body.Contains "unknown") "nor whether the handle exists"
            finally
                h.Dispose()
        }

        test "320.E — the forged-callback WARNING is rate-limited; the AUDIT is not" {
            // The half a "the warning fired" test misses. A scripted probe
            // must not be able to turn the log into the denial-of-service,
            // and the trail must stay complete anyway.
            let store = InMemoryExternalHandleStore() :> IExternalHandleStore

            let h =
                buildIngress (Some store) (Some(ScriptedSink ExternalResolution.NoAwaitingRun))

            try
                let handle, _, _ = registered store "team-alpha"
                let wrong, _ = ExternalCallbackSecret.mint ()

                for _ in 1..5 do
                    postCallback h (Some wrong) (payloadFor handle.HandleId) |> ignore

                let refusalWarnings =
                    h.Logger.Warnings |> List.filter (fun w -> w.Contains "callback_refused")

                let refusalAudits = h.Audit.Kinds |> List.filter ((=) "ExternalCallbackRejected")

                Expect.equal (List.length refusalWarnings) 1 "five refusals, ONE warning"
                Expect.equal (List.length refusalAudits) 5 "but five audit rows"
            finally
                h.Dispose()
        }

        test "a MISSING secret header is refused with the same uniform 403" {
            let store = InMemoryExternalHandleStore() :> IExternalHandleStore
            let sink = ScriptedSink(ExternalResolution.Resolved "succeeded")
            let h = buildIngress (Some store) (Some sink)

            try
                let handle, _, _ = registered store "team-alpha"
                let response = postCallback h None (payloadFor handle.HandleId)

                Expect.equal response.StatusCode HttpStatusCode.Forbidden "403"
                Expect.isEmpty sink.Calls "the sink was never driven"
                Expect.contains h.Audit.Kinds "ExternalCallbackRejected" "audited"
            finally
                h.Dispose()
        }

        test "an UNKNOWN handle is refused without a crypto comparison being decisive" {
            let store = InMemoryExternalHandleStore() :> IExternalHandleStore
            let sink = ScriptedSink(ExternalResolution.Resolved "succeeded")
            let h = buildIngress (Some store) (Some sink)

            try
                let secret, _ = ExternalCallbackSecret.mint ()
                let response = postCallback h (Some secret) (payloadFor (Guid.NewGuid()))

                Expect.equal response.StatusCode HttpStatusCode.Forbidden "403"
                Expect.isEmpty sink.Calls "the sink was never driven"

                Expect.isTrue
                    (h.Logger.Warnings |> List.exists (fun w -> w.Contains "unknown-handle"))
                    "the internal reason distinguishes it from a bad secret"
            finally
                h.Dispose()
        }

        test "320.C — a NON-TERMINAL status is refused, and does not consume the claim" {
            let store = InMemoryExternalHandleStore() :> IExternalHandleStore
            let sink = ScriptedSink(ExternalResolution.Resolved "succeeded")
            let h = buildIngress (Some store) (Some sink)

            try
                let handle, _, secret = registered store "team-alpha"

                let response =
                    postCallback h (Some secret) (sprintf """{"handleId":"%O","status":"running"}""" handle.HandleId)

                Expect.equal response.StatusCode HttpStatusCode.Forbidden "403"
                Expect.isEmpty sink.Calls "the sink was never driven"

                // The load-bearing half: a refused non-outcome must leave
                // the handle claimable, or a backend reporting progress to
                // the wrong endpoint would strand its own run.
                Expect.isTrue
                    (store.MarkTerminal handle.HandleId |> Async.RunSynchronously)
                    "the terminal claim is still available"
            finally
                h.Dispose()
        }

        test "a MALFORMED body is refused uniformly" {
            let store = InMemoryExternalHandleStore() :> IExternalHandleStore

            let h =
                buildIngress (Some store) (Some(ScriptedSink ExternalResolution.NoAwaitingRun))

            try
                for body in
                    [
                        "not json at all"
                        "{}"
                        """{"handleId":"not-a-guid","status":"succeeded"}"""
                    ] do
                    let response = postCallback h (Some "anything") body
                    Expect.equal response.StatusCode HttpStatusCode.Forbidden $"403 for %s{body}"

                Expect.isTrue
                    (h.Logger.Warnings |> List.exists (fun w -> w.Contains "malformed-body"))
                    "the internal reason says malformed"
            finally
                h.Dispose()
        }

        test "320.C — a DUPLICATE answers 200 idempotently, and says so" {
            // The response-shape half of the idempotency guarantee: a
            // backend that retries on non-2xx must not be handed a reason
            // to retry a correct duplicate forever.
            let store = InMemoryExternalHandleStore() :> IExternalHandleStore
            let sink = ScriptedSink ExternalResolution.AlreadyResolved
            let h = buildIngress (Some store) (Some sink)

            try
                let handle, _, secret = registered store "team-alpha"
                let response = postCallback h (Some secret) (payloadFor handle.HandleId)

                Expect.equal response.StatusCode HttpStatusCode.OK "200, NOT 409"
                Expect.stringContains (bodyOf response) "already-resolved" "the body distinguishes the case"

                Expect.contains h.Audit.Kinds "ExternalCallbackResolved" "the no-op is audited too"

                Expect.isEmpty
                    (h.Audit.Kinds |> List.filter ((=) "ExternalCallbackRejected"))
                    "a duplicate is not a refusal"
            finally
                h.Dispose()
        }

        test "a SCOPE MISMATCH from the sink is refused 403 and warned about" {
            let store = InMemoryExternalHandleStore() :> IExternalHandleStore

            let sink =
                ScriptedSink(ExternalResolution.ScopeMismatch("team-alpha", "team-other"))

            let h = buildIngress (Some store) (Some sink)

            try
                let handle, _, secret = registered store "team-alpha"
                let response = postCallback h (Some secret) (payloadFor handle.HandleId)

                Expect.equal response.StatusCode HttpStatusCode.Forbidden "403"

                Expect.isTrue
                    (h.Logger.Warnings
                     |> List.exists (fun w -> w.Contains "callback_refused" && w.Contains "scope-mismatch"))
                    "the GP 4 refusal is warned about"

                Expect.contains h.Audit.Kinds "ExternalCallbackRejected" "and audited as a refusal"
            finally
                h.Dispose()
        }

        test "with NO handle store the endpoint answers 503, not a refusal" {
            // An opted-in deployment whose blob backend cannot do
            // conditional writes. Not the caller's fault and not a forgery
            // signal, so it must not be audited as one — the trail would
            // fill with refusals nobody attacked.
            let h = buildIngress None (Some(ScriptedSink ExternalResolution.NoAwaitingRun))

            try
                let response = postCallback h (Some "anything") (payloadFor (Guid.NewGuid()))

                Expect.equal response.StatusCode HttpStatusCode.ServiceUnavailable "503"

                Expect.isEmpty
                    (h.Audit.Kinds |> List.filter ((=) "ExternalCallbackRejected"))
                    "a misconfiguration is not recorded as a forged callback"
            finally
                h.Dispose()
        }

        test "with a store but NO sink the endpoint answers 503 and audits the attempt" {
            let store = InMemoryExternalHandleStore() :> IExternalHandleStore
            let h = buildIngress (Some store) None

            try
                let handle, _, secret = registered store "team-alpha"
                let response = postCallback h (Some secret) (payloadFor handle.HandleId)

                Expect.equal response.StatusCode HttpStatusCode.ServiceUnavailable "503"
                Expect.contains h.Audit.Kinds "ExternalCallbackResolved" "the authenticated attempt is on the trail"
            finally
                h.Dispose()
        }

        test "GET is not routed — the ingress is POST-only" {
            let store = InMemoryExternalHandleStore() :> IExternalHandleStore

            let h =
                buildIngress (Some store) (Some(ScriptedSink ExternalResolution.NoAwaitingRun))

            try
                let response =
                    h.Client.GetAsync ExternalCallback.Route
                    |> Async.AwaitTask
                    |> Async.RunSynchronously

                Expect.equal response.StatusCode HttpStatusCode.NotFound "GET falls through to 404"
            finally
                h.Dispose()
        }
    ]

// ─── GP 13 — the endpoint and the store are absent when unused ───────

let private composeTests =
    testList "Phase 320 — GP 13 composition" [
        test "the default deployment mounts NO callback route and registers NO handle store" {
            let services = ServiceCollection() :> IServiceCollection
            let blobStore = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let channel =
                { new INotificationChannel with
                    member _.Publish(_, _) = async { return () }
                    member _.Subscribe(_, _) = async { return Guid.NewGuid() }
                    member _.Unsubscribe(_) = async { return () }
                }

            // `ServerConfig.defaults` carries `ExternalCompute =
            // NoExternalCompute`, which is the whole point of the pin. The
            // scheduler IS composed here, so the absence below is the
            // external-compute gate and not "no scheduler, nothing
            // registered".
            Expect.equal ServerConfig.defaults.ExternalCompute NoExternalCompute "the default is off"

            let config = {
                ServerConfig.defaults with
                    JobScheduler = InProcessJobScheduler
            }

            ComposeJobs.registerJobScheduler
                services
                config
                blobStore
                eventStore
                channel
                silentLogger
                (NoOpActivitySink() :> IActivitySink)
                (ref None)
                (ref None)
                None
            |> ignore

            let registered (t: Type) =
                services
                |> Seq.exists (fun d -> not (isNull d.ServiceType) && d.ServiceType = t)

            Expect.isFalse (registered typeof<IExternalHandleStore>) "no handle store on the default"
            Expect.isFalse (registered typeof<IExternalCompletionSink>) "no completion sink on the default"
        }

        test "an opted-in deployment with a conditional-write backend registers both" {
            let services = ServiceCollection() :> IServiceCollection
            let blobStore = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let channel =
                { new INotificationChannel with
                    member _.Publish(_, _) = async { return () }
                    member _.Subscribe(_, _) = async { return Guid.NewGuid() }
                    member _.Unsubscribe(_) = async { return () }
                }

            let config = {
                ServerConfig.defaults with
                    ExternalCompute = CustomExternalCompute
                    JobScheduler = InProcessJobScheduler
            }

            ComposeJobs.registerJobScheduler
                services
                config
                blobStore
                eventStore
                channel
                silentLogger
                (NoOpActivitySink() :> IActivitySink)
                (ref None)
                (ref None)
                None
            |> ignore

            let registered (t: Type) =
                services
                |> Seq.exists (fun d -> not (isNull d.ServiceType) && d.ServiceType = t)

            Expect.isTrue (registered typeof<IExternalHandleStore>) "the handle store is composed"
            Expect.isTrue (registered typeof<IExternalCompletionSink>) "and so is the completion sink"
        }

        test "an opted-in deployment WITHOUT conditional writes registers neither — polling, not a racy gate" {
            // The honest degradation. The tempting alternative is an
            // in-memory store so the endpoint "works"; on a multi-replica
            // deployment that gives a per-replica gate, which lets a
            // callback on one replica and a poll on another both win —
            // resolving one unit of work twice while reporting itself
            // protected.
            let services = ServiceCollection() :> IServiceCollection
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let channel =
                { new INotificationChannel with
                    member _.Publish(_, _) = async { return () }
                    member _.Subscribe(_, _) = async { return Guid.NewGuid() }
                    member _.Unsubscribe(_) = async { return () }
                }

            let config = {
                ServerConfig.defaults with
                    ExternalCompute = CustomExternalCompute
                    JobScheduler = InProcessJobScheduler
            }

            ComposeJobs.registerJobScheduler
                services
                config
                (plainBlobStorage ())
                eventStore
                channel
                silentLogger
                (NoOpActivitySink() :> IActivitySink)
                (ref None)
                (ref None)
                None
            |> ignore

            let registered (t: Type) =
                services
                |> Seq.exists (fun d -> not (isNull d.ServiceType) && d.ServiceType = t)

            Expect.isFalse (registered typeof<IExternalHandleStore>) "no store without conditional writes"
            Expect.isFalse (registered typeof<IExternalCompletionSink>) "and therefore no sink"
        }

        test "the ingress route table names exactly the documented path" {
            Expect.equal (List.length ExternalComputeCallback.routes) 1 "one route"
        }
    ]

let tests =
    testList "Phase 320 — external-compute completion callback" [
        storeTests
        secretTests
        wireTests
        schedulerTests
        ingressTests
        composeTests
    ]