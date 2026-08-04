module ToolUp.Platform.Tests.InProcess.AggregateLongRunningFrontingTests

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Net.Http.Headers
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 630 — long-running methods through the aggregate surface ──
//
// Phase 595's gateway fronted the *invoke* leg only. A member's
// long-running result parks in that member's own store, so the group's
// poll route could not read it and the aggregate honestly advertised
// `LongRunningEnabled = false` however capable its members were.
//
// The missing piece is handle translation, and every property worth
// having is a property of the handle rather than of the caller:
//
//   * it is content-free — a fresh Guid that names no member;
//   * it is durable and lifetime-bounded — a blob-backed binding under
//     the same `PeerJobRetentionPolicy` vocabulary the member's own
//     result store honours;
//   * polling it preserves the id echo (Phase 315), caller ownership
//     (Phase 308) and non-disclosure, each at the *group* edge, which is
//     a different edge from the one the member already guards.
//
// Every case below pairs its probe with a control: the same fixture with
// the mechanism's input removed, asserted to produce the *other* answer.
// Without the pair, "the poll was refused" would pass equally against a
// gateway that had broken and started refusing everything.

// ─── Contracts ───────────────────────────────────────────────────────

/// NOT `private`: the host reflects via `FSharpType.IsRecord`, and a
/// private record reads back as a non-record.
type ReconcileContract = {
    /// Long-running — the leg this phase exists to front.
    Reconcile: string -> Async<PeerJobHandle<string>>
    /// Immediate — the control, so an assertion about the long-running
    /// path cannot pass by the gateway having stopped forwarding at all.
    Ping: string -> Async<string>
}

/// An immediate-only contract, served by the second member. A group whose
/// members expose nothing long-running is the GP 11 fixture.
type CatalogueContract = {
    ListItems: unit -> Async<string list>
}

// ─── Identities + fixtures ───────────────────────────────────────────

let private v1: ContractVersion = { Major = 1; Minor = 0 }

let private reconcileId = "example.reconcile"
let private catalogueId = "example.catalogue"

let private peer id name : PeerIdentity = { PeerId = id; DisplayName = name }

let private alphaPeer = peer "alpha-site" "Alpha site"
let private betaPeer = peer "beta-site" "Beta site"
let private groupPeer = peer "consortium" "The consortium"
let private callerPeer = peer "buyer" "Buyer deployment"
let private intruderPeer = peer "intruder" "Intruder deployment"

let private target (identity: PeerIdentity) : TargetPeer = {
    Peer = identity
    BaseUrl = $"https://{identity.PeerId}.example"
}

let private callerKey = "aggregate-fronting-buyer-signing-key-01234567"
let private intruderKey = "aggregate-fronting-intruder-signing-key-0123"

/// The value the member's long-running method resolves to. Deliberately
/// outside the hex alphabet so a "no trace of the member" assertion
/// cannot be satisfied (or defeated) by an accidental substring of a Guid.
let private reconciledSentinel = "reconciled-QZX"

let private reconcileImpl (site: string) : ReconcileContract = {
    Reconcile =
        fun ledger -> async {
            return {
                JobId = Guid.NewGuid()
                Poll = fun () -> async { return Completed $"{site}:{reconciledSentinel}:{ledger}" }
            }
        }
    Ping = fun probe -> async { return $"{site}:pong:{probe}" }
}

let private catalogueImpl: CatalogueContract = {
    ListItems = fun () -> async { return [ "widget"; "sprocket" ] }
}

let private peerConfig (scheduler: JobSchedulerMode) = {
    ServerConfig.defaults with
        PeerSubstrate = EnabledPeerSubstrate
        JobScheduler = scheduler
}

// ─── Test doubles ────────────────────────────────────────────────────

type private TestClock(start: DateTimeOffset) =
    let mutable current = start
    member _.Advance(delta: TimeSpan) = current <- current.Add delta
    member _.Read() : DateTimeOffset = current

let private epoch = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

/// `IJobScheduler` that runs a scheduled job's handler when the test says
/// so, not when it is triggered. Holding the run is what makes `Pending`
/// observable end to end — an immediately-completing member would let the
/// forwarding assertion pass without ever having forwarded a non-terminal
/// status.
type private DeferredScheduler() =
    let handlers = ConcurrentDictionary<string, IJobHandler>()
    let pending = ResizeArray<Guid * JobRegistration>()
    let everScheduled = ResizeArray<Guid>()

    /// Every job id ever handed out, including ones already run — a
    /// queue that empties as jobs execute could not answer "which id did
    /// the member assign".
    member _.Scheduled = everScheduled |> List.ofSeq

    /// Execute every job scheduled so far, in order, exactly as the real
    /// scheduler's dispatch would.
    member _.RunPending() = async {
        let queued = pending |> List.ofSeq
        pending.Clear()

        for jobId, registration in queued do
            let handler = handlers[registration.Handler]

            let jobCtx: JobContext = {
                JobId = jobId
                ScopeId = registration.ScopeId
                AccessContext = AccessContext.unrestricted (AuthenticatedUser "_system")
                Attempt = 1
                Trigger = Manual
                TriggerSource = TriggerSource.ScheduledManually "_system"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = registration.Payload
                DeadLetterDestination = None
            }

            let! _ = handler.Execute jobCtx
            ()
    }

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = handlers[name] <- handler

        member _.RegisterHandlerAsync(name, handler) = async {
            handlers[name] <- handler
            return Ok()
        }

        member _.Schedule(registration) = async {
            let jobId = Guid.NewGuid()

            lock pending (fun () ->
                pending.Add((jobId, registration))
                everScheduled.Add jobId)

            return Ok jobId
        }

        member _.TriggerOnce(_scopeId, _jobId, _byUserId) = async { return Ok() }

        member _.Cancel(_scopeId, _jobId) =
            failwith "DeferredScheduler.Cancel must not be invoked"

        member _.Disable(_scopeId, _jobId) =
            failwith "DeferredScheduler.Disable must not be invoked"

        member _.Enable(_scopeId, _jobId) =
            failwith "DeferredScheduler.Enable must not be invoked"

        member _.Get(_scopeId, _jobId) =
            failwith "DeferredScheduler.Get must not be invoked"

        member _.ListJobs(_scopeId) =
            failwith "DeferredScheduler.ListJobs must not be invoked"

        member _.GetRecentRuns(_scopeId, _jobId, _count) =
            failwith "DeferredScheduler.GetRecentRuns must not be invoked"

        member _.NotifyEventWritten(_scopeId, _eventType, _eventId) =
            failwith "DeferredScheduler.NotifyEventWritten must not be invoked"

type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()

    member _.PeerJobRows =
        recorded
        |> List.ofSeq
        |> List.choose (fun (_, e) ->
            match e with
            | PeerJobCompleted p -> Some p
            | _ -> None)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { lock recorded (fun () -> recorded.Add((scopeId, audit))) }
        member _.GetAuditTrail(_scopeId, _dateRange, _eventType) = async { return [] }

type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
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

let private seedSigningKey (store: ISecretStore) (peerId: string) (key: string) =
    store.SetSecret("_platform", $"peers/{peerId}/signing-key", key)
    |> Async.RunSynchronously
    |> ignore

let private issue (provider: IPeerAuthProvider) (caller: PeerIdentity) = async {
    match! provider.IssuePeerToken(caller, groupPeer, Anonymous) with
    | Ok token -> return token
    | Error e -> return failtestf "Expected a minted token, got %A" e
}

// ─── Member deployments ──────────────────────────────────────────────

/// A live member: the composed app (whose `PeerSurface` the group reads),
/// the in-process peer the gateway's transport reaches, its own result
/// store, and the scheduler holding its jobs.
type private LiveMember = {
    Identity: PeerIdentity
    App: PeerServerApp
    Peer: IPlatformPeer
    ResultStore: IPeerJobResultStore
    Scheduler: DeferredScheduler
}

let private memberSurface (m: LiveMember) : AggregateMember = {
    Target = target m.Identity
    Surface = PeerSurface.describe m.App
}

/// Register a composed app's contracts on a fresh in-process peer and its
/// long-running job handlers on the member's own scheduler — the same two
/// things `PeerCompose.run` does at first `IPlatformPeer` resolution,
/// without standing up a host.
let private liveMember
    (identity: PeerIdentity)
    (retention: PeerJobRetentionPolicy)
    (clock: TestClock)
    (builders: (PeerJobFusion option -> PeerContractHost) list)
    : LiveMember =
    let blobs = InMemoryBlobStorage() :> IBlobStorage
    let scheduler = DeferredScheduler()

    let store =
        BlobPeerJobResultStore(blobs, retention, clock.Read) :> IPeerJobResultStore

    let fusion: PeerJobFusion = {
        Scheduler = scheduler
        ResultStore = store
        AuditLog = None
    }

    let platformPeer = DefaultPlatformPeer() :> IPlatformPeer

    for builder in builders do
        let host = builder (Some fusion)
        platformPeer.RegisterContract host.Registration

        for handlerName, handler in host.JobHandlers do
            (scheduler :> IJobScheduler).RegisterHandler(handlerName, handler)

    let app =
        builders
        |> List.fold
            (fun acc builder -> PeerServerApp.withContract builder acc)
            (PeerServerApp.create ()
             |> PeerServerApp.withConfig (peerConfig InProcessJobScheduler)
             |> PeerServerApp.withLocalPeer identity)

    {
        Identity = identity
        App = app
        Peer = platformPeer
        ResultStore = store
        Scheduler = scheduler
    }

/// `alpha` — serves the long-running reconcile contract.
let private alphaMember (retention: PeerJobRetentionPolicy) (clock: TestClock) =
    liveMember alphaPeer retention clock [
        (fun fusion -> JsonRpcPeerHost.contract<ReconcileContract> reconcileId [ v1 ] fusion (reconcileImpl "alpha"))
    ]

/// `beta` — serves an immediate-only catalogue contract.
let private betaMember (retention: PeerJobRetentionPolicy) (clock: TestClock) =
    liveMember betaPeer retention clock [
        (fun fusion -> JsonRpcPeerHost.contract<CatalogueContract> catalogueId [ v1 ] fusion catalogueImpl)
    ]

/// A member with NO job substrate at all — its surface reports
/// `LongRunningEnabled = false` and no routine, so it is the floor's
/// negative control.
let private schedulerlessMember (identity: PeerIdentity) (contractId: string) : AggregateMember = {
    Target = target identity
    Surface =
        PeerSurface.describe (
            PeerServerApp.create ()
            |> PeerServerApp.withConfig (peerConfig NoJobScheduler)
            |> PeerServerApp.withLocalPeer identity
            |> PeerServerApp.withContract (fun fusion ->
                JsonRpcPeerHost.contract<ReconcileContract> contractId [ v1 ] fusion (reconcileImpl identity.PeerId))
        )
}

// ─── The gateway's transport ─────────────────────────────────────────

/// Replaces the wire with a direct call into each member's own
/// `IPlatformPeer` (invoke) and result store (poll). The poll leg mirrors
/// what the member's OWN `/peer/v1/{contract}/jobs/{jobId}` route does:
/// Phase 308 ownership against the *polling* peer — which here is the
/// gateway, since the gateway is the peer that authenticated to the member
/// and scheduled the work. An absent record answers `Pending`.
let private inProcessClient (members: LiveMember list) (pollingAs: PeerIdentity) =
    let table = members |> List.map (fun m -> m.Identity.PeerId, m) |> Map.ofList

    { new IPeerClient with
        member _.Invoke(target, contractId, methodName, payload, ?_cancellationToken) = async {
            match Map.tryFind target.Peer.PeerId table with
            | Some m -> return! m.Peer.Handle(contractId, payload.Context, methodName, payload.Arguments)
            | None -> return Error(PeerTransport $"no member registered for {target.Peer.PeerId}")
        }

        member _.PollJob(target, _contractId, jobId, ?_cancellationToken) = async {
            match Map.tryFind target.Peer.PeerId table with
            | None -> return Error(PeerTransport $"no member registered for {target.Peer.PeerId}")
            | Some m ->
                let! record = m.ResultStore.TryGetResult(PeerJob.Scope, jobId)

                match record with
                | None -> return Ok PeerJobStatus.Pending
                | Some r when r.OwnerPeerId <> "" && r.OwnerPeerId = pollingAs.PeerId -> return Ok r.Status
                | Some _ -> return Error(PeerUnauthorized "peer job result is not owned by the calling peer")
        }
    }

// ─── The gateway ─────────────────────────────────────────────────────

let private expose (contractId: string) : ExposedContract = {
    ContractId = contractId
    Owner = None
}

let private exposureOf (contractIds: string list) : AggregateExposure = {
    Group = groupPeer
    Contracts = contractIds |> List.map expose
}

let private composeGateway
    (jobMap: IPeerGroupJobMap option)
    (client: IPeerClient)
    (members: AggregateMember list)
    (exposure: AggregateExposure)
    : PeerServerApp =
    let baseApp =
        PeerServerApp.create () |> PeerServerApp.withConfig (peerConfig NoJobScheduler)

    let withMap =
        match jobMap with
        | None -> baseApp
        | Some map -> PeerServerApp.withGroupJobMap map baseApp

    match PeerGateway.withAggregate client members exposure withMap with
    | Ok app -> app
    | Error errors -> failtestf "gateway composition was expected to succeed but reported %A" errors

/// The gateway's in-process peer — the dispatch side under test.
let private gatewayPeer (app: PeerServerApp) : IPlatformPeer =
    let platformPeer = DefaultPlatformPeer() :> IPlatformPeer

    for builder in app.Contracts do
        platformPeer.RegisterContract (builder None).Registration

    platformPeer

/// An inbound call context as an external counterparty presents it to the
/// group.
let private inboundContext: PeerCallContext = {
    Peer = callerPeer
    User = Anonymous
    ContractVersion = v1
    Route = [ callerPeer.PeerId ]
    RootRequestId = "root-630"
    ParentRequestId = None
    HopsRemaining = 4
}

/// A `TestServer`-hosted gateway whose poll route resolves group handles.
/// No `PeerJobFusion` is registered: a gateway has no job substrate of its
/// own, which is exactly the composition the fronting has to work under.
let private hostGateway (auth: IPeerAuthProvider) (fronting: PeerGroupJobFronting) (audit: IAuditLog option) =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost
                .UseTestServer()
                .ConfigureServices(fun services ->
                    services.AddSingleton<IPeerAuthProvider>(auth) |> ignore
                    services.AddSingleton<PeerGroupJobFronting>(fronting) |> ignore

                    match audit with
                    | Some log -> services.AddSingleton<IAuditLog>(log) |> ignore
                    | None -> ())
                .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe JsonRpcPeerHost.routes)
            |> ignore)
        .Build()

/// Raw `GET` on the group's poll route: the status code, the parsed
/// JSON-RPC envelope, and the untouched body (the id echo and the
/// no-trace assertions both need the raw bytes).
let private pollRaw (client: HttpClient) (token: string) (contractId: string) (jobId: Guid) = async {
    use request =
        new HttpRequestMessage(HttpMethod.Get, $"http://localhost/peer/v1/{contractId}/jobs/{jobId}")

    request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
    let! response = client.SendAsync request |> Async.AwaitTask
    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
    return int response.StatusCode, JsonRpc.deserialize<JsonRpcResponse> body, body
}

let private statusOf (response: JsonRpcResponse) : PeerJobStatus<string> =
    match response.Result with
    | Some json -> JsonRpc.deserialize<PeerJobStatus<string>> json
    | None -> failtestf "expected a status in Result, got %A" response

/// Dispatch the long-running method through the gateway and hand back the
/// group handle it minted.
let private dispatchReconcile (gateway: IPlatformPeer) (context: PeerCallContext) = async {
    let! result = gateway.Handle(reconcileId, context, "Reconcile", JsonRpc.serialize [ "ledger-1" ])

    match result with
    | Ok json -> return JsonRpc.deserialize<PeerJobId> json
    | Error e -> return failtestf "the gateway was expected to front the long-running dispatch, got %A" e
}

/// Everything one end-to-end case needs, wired together.
type private Fixture = {
    Alpha: LiveMember
    Gateway: IPlatformPeer
    App: PeerServerApp
    Map: IPeerGroupJobMap
    Client: IPeerClient
    Blobs: IBlobStorage
    MemberClock: TestClock
}

let private fixtureWith (memberRetention: PeerJobRetentionPolicy) =
    let memberClock = TestClock(epoch)
    let alpha = alphaMember memberRetention memberClock
    let client = inProcessClient [ alpha ] groupPeer
    let blobs = InMemoryBlobStorage() :> IBlobStorage
    let map = BlobPeerGroupJobMap(blobs) :> IPeerGroupJobMap

    let app =
        composeGateway (Some map) client [ memberSurface alpha ] (exposureOf [ reconcileId ])

    {
        Alpha = alpha
        Gateway = gatewayPeer app
        App = app
        Map = map
        Client = client
        Blobs = blobs
        MemberClock = memberClock
    }

let private fixture () =
    fixtureWith PeerJobRetentionPolicy.default'

// ─── Tests ───────────────────────────────────────────────────────────

let tests =
    testList "InProcess.AggregateLongRunningFronting (Phase 630)" [

        // ─── (a) the end-to-end leg ──────────────────────────────────

        testCaseAsync "a long-running call through the gateway completes end to end"
        <| async {
            let f = fixture ()

            let secrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey secrets callerPeer.PeerId callerKey
            let receiverAuth = JwtPeerAuthProvider(secrets) :> IPeerAuthProvider

            let callerSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey callerSecrets callerPeer.PeerId callerKey
            let callerAuth = JwtPeerAuthProvider(callerSecrets) :> IPeerAuthProvider

            use host = hostGateway receiverAuth { Map = f.Map; Client = f.Client } None
            host.Start()
            use httpClient = host.GetTestClient()

            let! groupJobId = dispatchReconcile f.Gateway inboundContext
            let! token = issue callerAuth callerPeer

            // (1) The member's job has not run, so the group forwards the
            // member's own `Pending` — the gateway invents nothing.
            let! pendingCode, pendingResponse, _ = pollRaw httpClient token reconcileId groupJobId
            Expect.equal pendingCode 200 "a poll for a still-running group job is answered, not refused"

            Expect.equal
                (statusOf pendingResponse)
                PeerJobStatus.Pending
                "the member has not finished, so the group reports exactly that"

            Expect.equal
                pendingResponse.Id
                (string groupJobId)
                "Phase 315 — the poll response echoes the GROUP's handle, the id the caller asked about"

            // (2) The member's job runs; the same handle now resolves to
            // the member's terminal result, projected verbatim.
            do! f.Alpha.Scheduler.RunPending()

            let! doneCode, doneResponse, doneBody = pollRaw httpClient token reconcileId groupJobId
            Expect.equal doneCode 200 "the terminal read is served to the handle's owner"

            Expect.equal
                (statusOf doneResponse)
                (PeerJobStatus.Completed(JsonRpc.serialize $"alpha:{reconciledSentinel}:ledger-1"))
                "the member's typed result reaches the external caller unchanged"

            Expect.equal
                doneResponse.Id
                (string groupJobId)
                "…still correlated to the group's handle, not to the member's job"

            Expect.isTrue
                (doneBody.Contains reconciledSentinel)
                "CONTROL — the result genuinely rides the response, so the assertions above are not vacuous"
        }

        testCaseAsync "an immediate method on the same contract is untouched by the fronting"
        <| async {
            // CONTROL for every long-running assertion in this file: the
            // gateway must still forward an immediate method's answer
            // straight through, with no handle minted.
            let f = fixture ()

            let! ping = f.Gateway.Handle(reconcileId, inboundContext, "Ping", JsonRpc.serialize [ "hello" ])

            Expect.equal
                ping
                (Ok(JsonRpc.serialize "alpha:pong:hello"))
                "an immediate method answers with its own result, not with a job handle"

            Expect.isEmpty
                f.Alpha.Scheduler.Scheduled
                "…and schedules nothing, so the long-running path was genuinely not taken"
        }

        // ─── (b) ownership at the group edge ─────────────────────────

        testCaseAsync "a poll for another caller's group handle is refused"
        <| async {
            let f = fixture ()

            let secrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey secrets callerPeer.PeerId callerKey
            seedSigningKey secrets intruderPeer.PeerId intruderKey
            let receiverAuth = JwtPeerAuthProvider(secrets) :> IPeerAuthProvider

            let callerSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey callerSecrets callerPeer.PeerId callerKey

            let intruderSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey intruderSecrets intruderPeer.PeerId intruderKey

            use host = hostGateway receiverAuth { Map = f.Map; Client = f.Client } None
            host.Start()
            use httpClient = host.GetTestClient()

            let! groupJobId = dispatchReconcile f.Gateway inboundContext
            do! f.Alpha.Scheduler.RunPending()

            let! callerToken = issue (JwtPeerAuthProvider(callerSecrets) :> IPeerAuthProvider) callerPeer
            let! intruderToken = issue (JwtPeerAuthProvider(intruderSecrets) :> IPeerAuthProvider) intruderPeer

            let! refusedCode, refused, refusedBody = pollRaw httpClient intruderToken reconcileId groupJobId

            Expect.equal
                refusedCode
                401
                "possession of a group handle is not authorization — the binding records who dispatched it"

            Expect.isFalse
                (refusedBody.Contains reconciledSentinel)
                "…and the refusal carries no trace of the federated result"

            Expect.equal
                refused.Id
                (string groupJobId)
                "the refusal still correlates — the id came from the caller's own URL"

            // CONTROL: the owner reads the very same handle.
            let! ownedCode, owned, _ = pollRaw httpClient callerToken reconcileId groupJobId
            Expect.equal ownedCode 200 "the caller that dispatched it collects it"

            Expect.equal
                (statusOf owned)
                (PeerJobStatus.Completed(JsonRpc.serialize $"alpha:{reconciledSentinel}:ledger-1"))
                "…so the refusal above is the ownership check, not a broken handle"
        }

        // ─── (c) non-disclosure ──────────────────────────────────────

        testCaseAsync "a retired member record reads as absent through the group handle"
        <| async {
            // The member retires its parked result under its own Phase 316
            // retention. The group holds a perfectly valid binding, forwards
            // the poll, and the member answers `Pending` — which is what an
            // unknown job answers too, so expired and never-existed stay
            // indistinguishable end to end.
            let retention =
                PeerJobRetentionPolicy.withTtl (TimeSpan.FromHours 1.0) PeerJobRetentionPolicy.keepForever

            let f = fixtureWith retention

            let secrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey secrets callerPeer.PeerId callerKey
            let receiverAuth = JwtPeerAuthProvider(secrets) :> IPeerAuthProvider

            let callerSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey callerSecrets callerPeer.PeerId callerKey

            use host = hostGateway receiverAuth { Map = f.Map; Client = f.Client } None
            host.Start()
            use httpClient = host.GetTestClient()

            let! groupJobId = dispatchReconcile f.Gateway inboundContext
            do! f.Alpha.Scheduler.RunPending()

            let! token = issue (JwtPeerAuthProvider(callerSecrets) :> IPeerAuthProvider) callerPeer

            // CONTROL: inside the member's TTL the handle resolves.
            let! freshCode, fresh, _ = pollRaw httpClient token reconcileId groupJobId
            Expect.equal freshCode 200 "inside the member's retention the group serves the result"

            match statusOf fresh with
            | PeerJobStatus.Completed _ -> ()
            | other -> failtestf "expected the parked result inside the TTL, got %A" other

            // Past the member's TTL the member's record is gone.
            f.MemberClock.Advance(TimeSpan.FromHours 2.0)

            let! staleCode, stale, staleBody = pollRaw httpClient token reconcileId groupJobId
            Expect.equal staleCode 200 "a retired record is not an error — it is an absence"

            Expect.equal
                (statusOf stale)
                PeerJobStatus.Pending
                "a retired member record reads as absent through the group handle"

            Expect.isFalse
                (staleBody.Contains reconciledSentinel)
                "…and nothing of the retired result survives in the answer"

            // …and a handle the gateway never minted answers identically,
            // which is the point: the two cases are the same answer.
            let! _, unknown, _ = pollRaw httpClient token reconcileId (Guid.NewGuid())

            Expect.equal
                (statusOf unknown)
                (statusOf stale)
                "expired and never-existed are one answer — possession of a handle discloses nothing"
        }

        testCaseAsync "the group handle discloses nothing about the owning member"
        <| async {
            let f = fixture ()

            let secrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey secrets callerPeer.PeerId callerKey
            let receiverAuth = JwtPeerAuthProvider(secrets) :> IPeerAuthProvider

            let callerSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey callerSecrets callerPeer.PeerId callerKey

            use host = hostGateway receiverAuth { Map = f.Map; Client = f.Client } None
            host.Start()
            use httpClient = host.GetTestClient()

            let! groupJobId = dispatchReconcile f.Gateway inboundContext
            do! f.Alpha.Scheduler.RunPending()

            let memberJobId =
                match f.Alpha.Scheduler.Scheduled with
                | [ single ] -> single
                | other -> failtestf "expected exactly one member job to have been scheduled, got %A" other

            // Asserted by VALUE, never by rendering a union case: F#'s
            // generated `ToString()` prints case fields, so a "does the
            // printed form mention the member" probe measures the
            // formatter rather than the wire.
            Expect.notEqual
                groupJobId
                memberJobId
                "the group handle is minted fresh — reusing the member's id would let a caller that also talks to that member correlate the two"

            let handle = string groupJobId

            Expect.isFalse
                (handle.Contains(alphaPeer.PeerId, StringComparison.OrdinalIgnoreCase))
                "the handle names no member"

            Expect.isFalse
                (handle.Contains(string memberJobId, StringComparison.OrdinalIgnoreCase))
                "the handle embeds no member job id"

            let! token = issue (JwtPeerAuthProvider(callerSecrets) :> IPeerAuthProvider) callerPeer
            let! _, _, body = pollRaw httpClient token reconcileId groupJobId

            for leak in
                [
                    alphaPeer.PeerId
                    alphaPeer.DisplayName
                    (target alphaPeer).BaseUrl
                    string memberJobId
                ] do
                Expect.isFalse
                    (body.Contains(leak, StringComparison.OrdinalIgnoreCase))
                    $"the poll response must not disclose '{leak}' — the group's face is one peer, not a topology"

            Expect.isTrue
                (body.Contains reconciledSentinel)
                "CONTROL — the response is a real answer, so the absences above are absences and not an empty body"
        }

        // ─── (d) the group-edge audit row ────────────────────────────

        testCaseAsync "a brokered long-running call lands one PeerJobCompleted row on the gateway"
        <| async {
            let f = fixture ()
            let audit = RecordingAuditLog()

            let secrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey secrets callerPeer.PeerId callerKey
            let receiverAuth = JwtPeerAuthProvider(secrets) :> IPeerAuthProvider

            let callerSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey callerSecrets callerPeer.PeerId callerKey

            use host =
                hostGateway receiverAuth { Map = f.Map; Client = f.Client } (Some(audit :> IAuditLog))

            host.Start()
            use httpClient = host.GetTestClient()

            let! groupJobId = dispatchReconcile f.Gateway inboundContext
            let! token = issue (JwtPeerAuthProvider(callerSecrets) :> IPeerAuthProvider) callerPeer

            // A poll while the work is still running concludes nothing, so
            // it must record nothing.
            let! _ = pollRaw httpClient token reconcileId groupJobId
            Expect.isEmpty audit.PeerJobRows "a Pending poll is not an outcome, so no terminal row is filed"

            do! f.Alpha.Scheduler.RunPending()

            let! _ = pollRaw httpClient token reconcileId groupJobId

            match audit.PeerJobRows with
            | [ row ] ->
                Expect.equal row.ContractId reconcileId "the row names the fronted contract"
                Expect.equal row.MethodName "Reconcile" "…and the method whose job resolved"

                Expect.equal
                    row.CallerPeerId
                    callerPeer.PeerId
                    "the caller is the peer that dispatched THROUGH the group, not the member"

                Expect.equal
                    row.RootRequestId
                    inboundContext.RootRequestId
                    "…correlated to the cascade the gateway already filed its PeerCallCompleted row under"

                Expect.equal
                    row.JobId
                    groupJobId
                    "the row is filed under the GROUP's handle — the id the caller polls with, so a poll trace joins"

                Expect.isTrue row.Succeeded "the brokered work completed"
                Expect.equal row.Outcome "ok" "…and the outcome label says so"
            | rows -> failtestf "expected exactly one terminal row at the group edge, got %A" rows

            // A caller that keeps polling a finished job must not multiply
            // the trail.
            let! _ = pollRaw httpClient token reconcileId groupJobId
            let! _ = pollRaw httpClient token reconcileId groupJobId

            Expect.equal
                (List.length audit.PeerJobRows)
                1
                "the row is claimed once per handle, however many times the caller polls"
        }

        // ─── (e) the LongRunningEnabled floor ────────────────────────

        test "LongRunningEnabled is the floor across the exposing members" {
            let clock = TestClock(epoch)
            let alpha = alphaMember PeerJobRetentionPolicy.default' clock
            let beta = betaMember PeerJobRetentionPolicy.default' clock

            let capable =
                match
                    AggregatePeerSurface.derive (
                        [ memberSurface alpha; memberSurface beta ],
                        exposureOf [ reconcileId; catalogueId ]
                    )
                with
                | Ok surface -> surface
                | Error errors -> failtestf "derivation was expected to succeed but reported %A" errors

            Expect.equal
                (capable.Budgets |> Option.map _.LongRunningEnabled)
                (Some true)
                "every exposing member dispatches long-running work, so the group does"

            // One exposing member with no job substrate floors the group,
            // exactly as a weaker trust facet does.
            let weaker = schedulerlessMember (peer "gamma-site" "Gamma site") "example.reports"

            let floored =
                match
                    AggregatePeerSurface.derive (
                        [ memberSurface alpha; memberSurface beta; weaker ],
                        exposureOf [ reconcileId; catalogueId; "example.reports" ]
                    )
                with
                | Ok surface -> surface
                | Error errors -> failtestf "derivation was expected to succeed but reported %A" errors

            Expect.equal
                (floored.Budgets |> Option.map _.LongRunningEnabled)
                (Some false)
                "one exposing member that cannot dispatch long-running work floors the whole group"

            // …and an unexposed weak member floors nothing, the same
            // negative control the posture floor carries.
            let unexposed =
                match
                    AggregatePeerSurface.derive (
                        [ memberSurface alpha; memberSurface beta; weaker ],
                        exposureOf [ reconcileId; catalogueId ]
                    )
                with
                | Ok surface -> surface
                | Error errors -> failtestf "derivation was expected to succeed but reported %A" errors

            Expect.equal
                (unexposed.Budgets |> Option.map _.LongRunningEnabled)
                (Some true)
                "a member nothing is routed to contributes no capability floor"
        }

        test "the resolved route carries the owning member's routine set" {
            let clock = TestClock(epoch)
            let alpha = alphaMember PeerJobRetentionPolicy.default' clock

            match AggregatePeerSurface.routes ([ memberSurface alpha ], exposureOf [ reconcileId ]) with
            | Error errors -> failtestf "route resolution was expected to succeed but reported %A" errors
            | Ok [ route ] ->
                Expect.equal
                    route.Routines
                    [ PeerJob.handlerName reconcileId "Reconcile" ]
                    "the gateway learns which methods are long-running from the owner's own advertised routines"
            | Ok other -> failtestf "expected exactly one resolved route, got %A" other

            // CONTROL: a member with no job substrate advertises no
            // routine, so the same contract resolves with an empty set and
            // the gateway treats every method as immediate.
            match
                AggregatePeerSurface.routes ([ schedulerlessMember alphaPeer reconcileId ], exposureOf [ reconcileId ])
            with
            | Error errors -> failtestf "route resolution was expected to succeed but reported %A" errors
            | Ok [ route ] -> Expect.isEmpty route.Routines "a member that dispatches no routine advertises none"
            | Ok other -> failtestf "expected exactly one resolved route, got %A" other
        }

        // ─── (f) GP 11 — a group with nothing long-running ───────────

        test "a group with no long-running members is byte-identical to Phase 595" {
            let members = [ schedulerlessMember alphaPeer reconcileId ]
            let exposure = exposureOf [ reconcileId ]

            let derived =
                match AggregatePeerSurface.derive (members, exposure) with
                | Ok surface -> surface
                | Error errors -> failtestf "derivation was expected to succeed but reported %A" errors

            Expect.equal
                (derived.Budgets |> Option.map _.LongRunningEnabled)
                (Some false)
                "the floor over a scheduler-less member is `false` — the value Phase 595 hard-coded"

            Expect.isTrue
                (derived.Serves.Contracts |> List.forall (fun c -> List.isEmpty c.Routines))
                "the group still advertises no routine of its own — the gateway fuses none onto its own substrate"

            // Composing the new capability over a group with nothing to
            // front must not move a single byte of the pinned face.
            let client = inProcessClient [] groupPeer
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let map = BlobPeerGroupJobMap(blobs) :> IPeerGroupJobMap

            let without = composeGateway None client members exposure
            let withMap = composeGateway (Some map) client members exposure

            let surfaceOf app =
                match PeerGateway.surface members exposure app with
                | Ok live -> live
                | Error errors -> failtestf "the gateway surface was expected to resolve but reported %A" errors

            Expect.equal
                (PeerSurface.exportJson (surfaceOf withMap))
                (PeerSurface.exportJson (surfaceOf without))
                "a group with nothing long-running exports identically whether or not a job map was composed"

            Expect.equal
                (PeerSurface.exportJson (surfaceOf without))
                (PeerSurface.exportJson derived)
                "…and both equal the derived aggregate, so the Phase 595 pinned face is unmoved"
        }

        test "a gateway with no group job map does not advertise a poll leg it cannot serve" {
            let clock = TestClock(epoch)
            let alpha = alphaMember PeerJobRetentionPolicy.default' clock
            let members = [ memberSurface alpha ]
            let exposure = exposureOf [ reconcileId ]

            let derived =
                match AggregatePeerSurface.derive (members, exposure) with
                | Ok surface -> surface
                | Error errors -> failtestf "derivation was expected to succeed but reported %A" errors

            Expect.equal
                (derived.Budgets |> Option.map _.LongRunningEnabled)
                (Some true)
                "the members' floor says the group COULD front long-running work"

            let client = inProcessClient [ alpha ] groupPeer
            let without = composeGateway None client members exposure

            match PeerGateway.surface members exposure without with
            | Error errors -> failtestf "the gateway surface was expected to resolve but reported %A" errors
            | Ok live ->
                Expect.equal
                    (live.Budgets |> Option.map _.LongRunningEnabled)
                    (Some false)
                    "…but THIS gateway composed no handle map, so its live face must not claim the leg"

            // CONTROL: composing the map makes the live face agree with the
            // members' floor again.
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let map = BlobPeerGroupJobMap(blobs) :> IPeerGroupJobMap
            let fronted = composeGateway (Some map) client members exposure

            match PeerGateway.surface members exposure fronted with
            | Error errors -> failtestf "the gateway surface was expected to resolve but reported %A" errors
            | Ok live ->
                Expect.equal live derived "a gateway that CAN front the leg advertises exactly the derived aggregate"
        }

        testCaseAsync "without a job map the member's own job id passes through, as before Phase 630"
        <| async {
            let clock = TestClock(epoch)
            let alpha = alphaMember PeerJobRetentionPolicy.default' clock
            let client = inProcessClient [ alpha ] groupPeer

            let without =
                composeGateway None client [ memberSurface alpha ] (exposureOf [ reconcileId ])

            let! handle = dispatchReconcile (gatewayPeer without) inboundContext

            match alpha.Scheduler.Scheduled with
            | [ memberJobId ] ->
                Expect.equal
                    handle
                    memberJobId
                    "the pre-630 gateway hands back whatever the member answered — no handle is minted, exactly as Phase 595 behaved"
            | other -> failtestf "expected exactly one member job to have been scheduled, got %A" other

            Expect.isNone without.GroupJobMap "…because no group job map was composed, which is the whole opt-in"
        }

        // ─── (g) the handle map's own retention ──────────────────────

        testCaseAsync "a group binding past its TTL reads as absent and is reclaimed"
        <| async {
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let clock = TestClock(epoch)

            let policy =
                PeerJobRetentionPolicy.withTtl (TimeSpan.FromHours 1.0) PeerJobRetentionPolicy.keepForever

            let map = BlobPeerGroupJobMap(blobs, policy, clock.Read) :> IPeerGroupJobMap
            let groupJobId = Guid.NewGuid()

            let binding: PeerGroupJobBinding = {
                OwnerPeerId = callerPeer.PeerId
                MemberPeer = target alphaPeer
                ContractId = reconcileId
                MethodName = "Reconcile"
                MemberJobId = Guid.NewGuid()
                RootRequestId = "root-630"
            }

            do! map.Bind(PeerJob.Scope, groupJobId, binding)

            let! fresh = map.TryGet(PeerJob.Scope, groupJobId)
            Expect.equal fresh (Some binding) "inside the TTL the binding resolves the handle intact"

            clock.Advance(TimeSpan.FromHours 2.0)

            let! stale = map.TryGet(PeerJob.Scope, groupJobId)
            Expect.isNone stale "past the TTL the binding reads as absent"

            let! exists = blobs.Exists("_platform", $"peers/groups/jobs/{PeerJob.Scope}/{groupJobId}.json")
            Expect.isFalse exists "…and its document is reclaimed on the read that found it expired, not merely hidden"
        }

        testCaseAsync "the terminal-observation claim is granted once"
        <| async {
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let map = BlobPeerGroupJobMap(blobs) :> IPeerGroupJobMap
            let groupJobId = Guid.NewGuid()

            let binding: PeerGroupJobBinding = {
                OwnerPeerId = callerPeer.PeerId
                MemberPeer = target alphaPeer
                ContractId = reconcileId
                MethodName = "Reconcile"
                MemberJobId = Guid.NewGuid()
                RootRequestId = "root-630"
            }

            do! map.Bind(PeerJob.Scope, groupJobId, binding)

            let! first = map.MarkTerminalObserved(PeerJob.Scope, groupJobId)
            Expect.isTrue first "the first terminal poll claims the audit row"

            let! second = map.MarkTerminalObserved(PeerJob.Scope, groupJobId)
            Expect.isFalse second "…and every later poll is refused the claim"

            let! unknown = map.MarkTerminalObserved(PeerJob.Scope, Guid.NewGuid())
            Expect.isFalse unknown "a handle this gateway never minted claims nothing"

            let! stillBound = map.TryGet(PeerJob.Scope, groupJobId)
            Expect.equal stillBound (Some binding) "claiming the row does not retire the binding the caller is polling"

            Expect.equal
                map.Retention
                PeerJobRetentionPolicy.default'
                "the map declares the retention it honours — the same default the member's own result store takes"
        }
    ]