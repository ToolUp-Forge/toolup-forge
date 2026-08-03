module ToolUp.Platform.Tests.Contracts.IPlatformPeerContract

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform

// ─── IPlatformPeerContract — Phase 18 contract pack ──────────────────
//
// Exercises the documented behaviour of the inter-platform peer
// substrate's receiver surface (`IPlatformPeer`) bound to the typed
// host + proxy (`JsonRpcPeerHost.contract` / `JsonRpcPeerClient.create`).
// The pack drives the in-process-doable surface: contract registration +
// capability handshake, immediate dispatch through `Handle`, a typed
// round-trip over a loopback `IPeerClient`, cascade-context propagation,
// every `Handle` guard (version / contract / method / hop / loop), and
// the job-fusion seam (no handler when fusion is off; exactly one
// correctly-named handler when it is on). Identity validation, audit
// emission, and matching `RootRequestId` across a real HTTP boundary
// need the JSON-RPC host wired over a TestServer; they live in the
// `InProcess/PlatformPeerTests.fs` worked example, not here.
//
// The pack takes a factory producing a fresh `IPlatformPeer` so each
// test starts from an empty contract table. The default binding supplies
// `DefaultPlatformPeer`; an alternate receiver implementation composes
// the same pack against its own registry.

// A record-of-functions contract: one immediate no-arg method, one
// immediate single-arg method, and one long-running two-arg method that
// resolves through the job substrate.
//
// NOT `private`: `JsonRpcPeerHost.contract` / `JsonRpcPeerClient.create`
// reflect via `FSharpType.IsRecord` without the private-representation
// binding flag, so a `private` record reads back as a non-record and the
// host rejects it ("requires a record contract type").
type GreeterContract = {
    GetCapabilities: unit -> Async<string list>
    Echo: string -> Async<string>
    SlowSum: int -> int -> Async<PeerJobHandle<int>>
}

let private buyerId = {
    PeerId = "buyer"
    DisplayName = "Buyer Deployment"
}

let private sellerId = {
    PeerId = "seller"
    DisplayName = "Seller Deployment"
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }

let private greeterImpl: GreeterContract = {
    GetCapabilities = fun () -> async { return [ "echo"; "sum" ] }
    Echo = fun s -> async { return $"echo: {s}" }
    SlowSum =
        fun a b -> async {
            return {
                JobId = Guid.NewGuid()
                Poll = fun () -> async { return PeerJobStatus.Completed(a + b) }
            }
        }
}

/// In-process `IPeerClient` that routes a typed proxy call straight to a
/// receiver's `Handle` — no transport, no auth, no serialisation round
/// trip beyond what the proxy itself performs. Lets the contract pack
/// exercise the typed proxy without standing up an HTTP host.
type private LoopbackPeerClient(peer: IPlatformPeer) =
    interface IPeerClient with
        member _.Invoke(_target: TargetPeer, contractId: string, methodName: string, payload: PeerWirePayload) =
            peer.Handle(contractId, payload.Context, methodName, payload.Arguments)

        member _.PollJob(_target: TargetPeer, _contractId: string, _jobId: PeerJobId) = async {
            return Error(PeerTransport "loopback client does not poll jobs")
        }

/// `IJobScheduler` stub for the fusion-on tests. `JsonRpcPeerHost.contract`
/// only *builds* a `PeerJobHandler` + handler name for a long-running
/// method — it never schedules — so every member except the unit-returning
/// `RegisterHandler` throws if reached, surfacing any accidental dispatch.
/// NOT `private`: the TestServer worked example (`PlatformPeerTests`)
/// reuses it for poll-route fixtures that never schedule.
type StubScheduler() =
    interface IJobScheduler with
        member _.RegisterHandler(_name, _handler) = ()

        member _.RegisterHandlerAsync(_name, _handler) = async { return Ok() }

        member _.Schedule(_registration) =
            failwith "StubScheduler.Schedule must not be invoked by the contract pack"

        member _.Cancel(_scopeId, _jobId) =
            failwith "StubScheduler.Cancel must not be invoked by the contract pack"

        member _.Disable(_scopeId, _jobId) =
            failwith "StubScheduler.Disable must not be invoked by the contract pack"

        member _.Enable(_scopeId, _jobId) =
            failwith "StubScheduler.Enable must not be invoked by the contract pack"

        member _.Get(_scopeId, _jobId) =
            failwith "StubScheduler.Get must not be invoked by the contract pack"

        member _.ListJobs(_scopeId) =
            failwith "StubScheduler.ListJobs must not be invoked by the contract pack"

        member _.GetRecentRuns(_scopeId, _jobId, _count) =
            failwith "StubScheduler.GetRecentRuns must not be invoked by the contract pack"

        member _.TriggerOnce(_scopeId, _jobId, _byUserId) =
            failwith "StubScheduler.TriggerOnce must not be invoked by the contract pack"

        member _.NotifyEventWritten(_scopeId, _eventType, _eventId) =
            failwith "StubScheduler.NotifyEventWritten must not be invoked by the contract pack"

/// In-memory `IPeerJobResultStore` for the fusion-on tests. NOT
/// `private`: the TestServer worked example (`PlatformPeerTests`) reuses
/// it for the poll-route ownership fixtures (Phase 308), seeding via
/// `SaveResult`.
type InMemoryResultStore() =
    let store = ConcurrentDictionary<string * PeerJobId, PeerJobRecord>()

    interface IPeerJobResultStore with
        // Phase 316 — the pack's double retains everything; retention
        // behaviour is exercised against the real blob-backed store in
        // `InProcess/PeerJobRetentionTests.fs`.
        member _.Retention = PeerJobRetentionPolicy.keepForever

        member _.SaveResult(scopeId: string, jobId: PeerJobId, ownerPeerId: string, status: PeerJobStatus<string>) = async {
            store[(scopeId, jobId)] <- {
                OwnerPeerId = ownerPeerId
                Status = status
            }
        }

        member _.TryGetResult(scopeId: string, jobId: PeerJobId) = async {
            match store.TryGetValue((scopeId, jobId)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

/// `IJobScheduler` stub that records the `JobRegistration` a long-running
/// dispatch schedules and acknowledges the follow-up `TriggerOnce`, so
/// the pack can assert the owner-stamped payload (Phase 308) without a
/// real scheduler. Every other member throws if reached.
type private RecordingScheduler() =
    let mutable registered: JobRegistration option = None
    member _.Registered = registered

    interface IJobScheduler with
        member _.RegisterHandler(_name, _handler) = ()

        member _.RegisterHandlerAsync(_name, _handler) = async { return Ok() }

        member _.Schedule(registration) = async {
            registered <- Some registration
            return Ok(Guid.NewGuid())
        }

        member _.TriggerOnce(_scopeId, _jobId, _byUserId) = async { return Ok() }

        member _.Cancel(_scopeId, _jobId) =
            failwith "RecordingScheduler.Cancel must not be invoked by the contract pack"

        member _.Disable(_scopeId, _jobId) =
            failwith "RecordingScheduler.Disable must not be invoked by the contract pack"

        member _.Enable(_scopeId, _jobId) =
            failwith "RecordingScheduler.Enable must not be invoked by the contract pack"

        member _.Get(_scopeId, _jobId) =
            failwith "RecordingScheduler.Get must not be invoked by the contract pack"

        member _.ListJobs(_scopeId) =
            failwith "RecordingScheduler.ListJobs must not be invoked by the contract pack"

        member _.GetRecentRuns(_scopeId, _jobId, _count) =
            failwith "RecordingScheduler.GetRecentRuns must not be invoked by the contract pack"

        member _.NotifyEventWritten(_scopeId, _eventType, _eventId) =
            failwith "RecordingScheduler.NotifyEventWritten must not be invoked by the contract pack"

// ─── Phase 310 — terminal-outcome fixtures ───────────────────────────
//
// A separate contract from `GreeterContract` so the Phase 310 tests can
// exercise a *failing* long-running method without perturbing the
// handler-count assertions the fusion-seam tests make about the greeter.
// Two long-running methods, one of each terminal shape.

type TerminalOutcomeContract = {
    Compute: int -> Async<PeerJobHandle<int>>
    Refuse: string -> Async<PeerJobHandle<int>>
}

let private terminalImpl: TerminalOutcomeContract = {
    Compute =
        fun n -> async {
            return {
                JobId = Guid.NewGuid()
                Poll = fun () -> async { return PeerJobStatus.Completed(n * 2) }
            }
        }
    Refuse =
        fun reason -> async {
            return {
                JobId = Guid.NewGuid()
                // A typed peer-side refusal, not an exception — the shape a
                // real gate produces, and one whose `PeerError` case name is
                // distinguishable from the generic `PeerHandler` collapse.
                Poll = fun () -> async { return PeerJobStatus.Failed(PeerUnauthorized reason) }
            }
        }
}

/// The pre-Phase-310 `PeerJobPayload` shape. Kept as its own type so the
/// back-compat test schedules a genuinely old payload through the same
/// serialiser, rather than hand-rolling the JSON it hopes the old one had.
type private LegacyPeerJobPayload = {
    OwnerPeerId: string
    ArgsJson: string
}

/// `IAuditLog` that keeps every recorded event in order. Reads are not
/// exercised by the job path — the handler only writes.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()

    member _.Recorded = recorded |> List.ofSeq

    member this.PeerJobRows =
        this.Recorded
        |> List.choose (fun (_, e) ->
            match e with
            | PeerJobCompleted p -> Some p
            | _ -> None)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add((scopeId, audit)) }
        member _.GetAuditTrail(_scopeId, _dateRange, _eventType) = async { return [] }

/// `IAuditLog` whose writes always throw — the flaky-store control. The
/// job's parked result must be unaffected.
type private ThrowingAuditLog() =
    interface IAuditLog with
        member _.Record(_scopeId, _audit) = failwith "audit store is unavailable"

        member _.GetAuditTrail(_scopeId, _dateRange, _eventType) = async { return [] }

/// Drive one long-running job handler to completion over `payload` and
/// return the `JobId` it ran under, so a test can assert the parked record
/// and the audit row against the same id.
let private runJobHandler (handler: IJobHandler) (payload: string) = async {
    let jobId = Guid.NewGuid()

    let jobCtx: JobContext = {
        JobId = jobId
        ScopeId = PeerJob.Scope
        AccessContext = AccessContext.unrestricted (AuthenticatedUser "_system")
        Attempt = 1
        Trigger = Manual
        TriggerSource = TriggerSource.ScheduledManually "_system"
        ScheduledAt = DateTime.UtcNow
        RunningAt = DateTime.UtcNow
        Payload = payload
        DeadLetterDestination = None
    }

    let! result = handler.Execute jobCtx
    return jobId, result
}

/// A call context with explicit version / route / hop budget — for the
/// guard-path tests. Identity is the buyer; user is anonymous.
let private mkContext (version: ContractVersion) (route: string list) (hops: int) : PeerCallContext = {
    Peer = buyerId
    User = Anonymous
    ContractVersion = version
    Route = route
    RootRequestId = Guid.NewGuid().ToString()
    ParentRequestId = None
    HopsRemaining = hops
}

/// Proxy config that routes through the loopback client to `peer`.
let private loopbackConfig (peer: IPlatformPeer) (contractId: string) : PeerProxyConfig = {
    Client = LoopbackPeerClient(peer)
    Target = {
        Peer = sellerId
        BaseUrl = "loopback"
    }
    Caller = buyerId
    User = Anonymous
    Version = v1
    ContractId = contractId
    HopBudget = 8
}

/// Build the greeter host over `versions` (+ optional fusion) and
/// register it on `peer`. Returns the host so a test can inspect its
/// `JobHandlers`.
let private registerGreeter
    (peer: IPlatformPeer)
    (versions: ContractVersion list)
    (fusion: PeerJobFusion option)
    : PeerContractHost =
    let host =
        JsonRpcPeerHost.contract<GreeterContract> "greeter" versions fusion greeterImpl

    peer.RegisterContract host.Registration
    host

let tests (name: string) (factory: unit -> IPlatformPeer) =
    testList $"{name} — IPlatformPeerContract" [

        // ─── Registration + capability handshake ──────────────────

        testCaseAsync "RegisterContract then Capabilities lists the contract + its versions"
        <| async {
            let peer = factory ()
            registerGreeter peer [ v1 ] None |> ignore

            let! caps = peer.Capabilities()

            Expect.exists
                caps
                (fun c -> c.ContractId = "greeter" && c.Versions = [ v1 ])
                "Capabilities reports the registered contract under its id + supported versions"
        }

        // ─── Immediate dispatch via Handle ────────────────────────

        testCaseAsync "Handle dispatches an immediate method and serialises its result"
        <| async {
            let peer = factory ()
            registerGreeter peer [ v1 ] None |> ignore

            let! result = peer.Handle("greeter", mkContext v1 [ "buyer" ] 8, "Echo", JsonRpc.serialize [ "world" ])

            match result with
            | Ok json -> Expect.equal (JsonRpc.deserialize<string> json) "echo: world" "Echo round-trips its argument"
            | Error e -> failtestf "Expected Ok, got Error %A" e
        }

        // ─── Typed proxy round-trip over loopback ─────────────────

        testCaseAsync "typed proxy round-trips Echo and GetCapabilities over a loopback transport"
        <| async {
            let peer = factory ()
            registerGreeter peer [ v1 ] None |> ignore

            let proxy =
                JsonRpcPeerClient.create<GreeterContract> (loopbackConfig peer "greeter")

            let! echoed = proxy.Echo "hello"
            Expect.equal echoed "echo: hello" "proxy Echo resolves the typed result inline"

            let! caps = proxy.GetCapabilities()
            Expect.equal caps [ "echo"; "sum" ] "proxy GetCapabilities deserialises the string list"
        }

        // ─── Cascade-context propagation ──────────────────────────

        testCaseAsync "Handle propagates the full cascade context to the dispatch closure"
        <| async {
            let peer = factory ()
            let captured = ref None

            let registration: PeerContractRegistration = {
                ContractId = "capture"
                Versions = [ v1 ]
                Dispatch =
                    fun ctx _methodName _argsJson -> async {
                        captured.Value <- Some ctx
                        return Ok(JsonRpc.serialize "ok")
                    }
            }

            peer.RegisterContract registration

            let ctx = {
                Peer = buyerId
                User = Anonymous
                ContractVersion = v1
                Route = [ "buyer"; "broker" ]
                RootRequestId = "root-123"
                ParentRequestId = Some "parent-1"
                HopsRemaining = 5
            }

            let! _ = peer.Handle("capture", ctx, "anything", "[]")

            match captured.Value with
            | None -> failtest "dispatch closure was never invoked"
            | Some c ->
                Expect.equal c.RootRequestId "root-123" "RootRequestId reaches the handler unchanged"
                Expect.equal c.Route [ "buyer"; "broker" ] "Route reaches the handler unchanged"
                Expect.equal c.ParentRequestId (Some "parent-1") "ParentRequestId reaches the handler unchanged"
                Expect.equal c.HopsRemaining 5 "HopsRemaining reaches the handler unchanged"
        }

        // ─── Guard: version mismatch ──────────────────────────────

        testCaseAsync "Handle rejects a call whose version is not in the contract's supported set"
        <| async {
            let peer = factory ()
            registerGreeter peer [ v1 ] None |> ignore

            let requested = { Major = 2; Minor = 0 }
            let! result = peer.Handle("greeter", mkContext requested [ "buyer" ] 8, "Echo", JsonRpc.serialize [ "x" ])

            match result with
            | Error(PeerVersionMismatch(req, supported)) ->
                Expect.equal req requested "the rejected version echoes back the requested one"
                Expect.equal supported [ v1 ] "the supported set is reported for negotiation"
            | other -> failtestf "Expected PeerVersionMismatch, got %A" other
        }

        // ─── Guard: unknown contract ──────────────────────────────

        testCaseAsync "Handle rejects a call to an unregistered contract id"
        <| async {
            let peer = factory ()

            let! result = peer.Handle("ghost", mkContext v1 [ "buyer" ] 8, "Echo", "[]")

            match result with
            | Error(PeerContractNotFound contractId) ->
                Expect.equal contractId "ghost" "the unknown contract id is reported"
            | other -> failtestf "Expected PeerContractNotFound, got %A" other
        }

        // ─── Guard: unknown method ────────────────────────────────

        testCaseAsync "Handle rejects an unknown method on a registered contract"
        <| async {
            let peer = factory ()
            registerGreeter peer [ v1 ] None |> ignore

            let! result = peer.Handle("greeter", mkContext v1 [ "buyer" ] 8, "Nope", "[]")

            match result with
            | Error(PeerMethodNotFound methodName) ->
                Expect.equal methodName "Nope" "the unknown method name is reported"
            | other -> failtestf "Expected PeerMethodNotFound, got %A" other
        }

        // ─── Guard: hop budget exhausted ──────────────────────────

        testCaseAsync "Handle rejects a call that arrives with no remaining hop budget"
        <| async {
            let peer = factory ()
            registerGreeter peer [ v1 ] None |> ignore

            let! result = peer.Handle("greeter", mkContext v1 [ "buyer" ] 0, "Echo", JsonRpc.serialize [ "x" ])

            match result with
            | Error PeerHopLimitExceeded -> ()
            | other -> failtestf "Expected PeerHopLimitExceeded, got %A" other
        }

        // ─── Guard: cascade loop ──────────────────────────────────

        testCaseAsync "Handle rejects a call whose route already contains a repeated peer id"
        <| async {
            let peer = factory ()
            registerGreeter peer [ v1 ] None |> ignore

            let route = [ "buyer"; "broker"; "buyer" ]
            let! result = peer.Handle("greeter", mkContext v1 route 8, "Echo", JsonRpc.serialize [ "x" ])

            match result with
            | Error(PeerLoopDetected reported) ->
                Expect.equal reported route "the looping route is reported for diagnosis"
            | other -> failtestf "Expected PeerLoopDetected, got %A" other
        }

        // ─── Job fusion off — no handler, dispatch fails clearly ──

        testCaseAsync "without fusion a long-running method contributes no job handler and dispatch fails clearly"
        <| async {
            let peer = factory ()
            let host = registerGreeter peer [ v1 ] None

            Expect.isEmpty host.JobHandlers "no job handler is built when the fusion substrate is absent"

            let! result = peer.Handle("greeter", mkContext v1 [ "buyer" ] 8, "SlowSum", JsonRpc.serialize [ 3; 4 ])

            match result with
            | Error(PeerHandler msg) ->
                Expect.stringContains msg "not enabled" "the failure explains the fusion substrate is required"
            | other -> failtestf "Expected PeerHandler, got %A" other
        }

        // ─── Job fusion on — exactly one correctly-named handler ──

        testCase "with fusion a long-running method contributes exactly one correctly-named job handler"
        <| fun _ ->
            let peer = factory ()

            let fusion: PeerJobFusion = {
                Scheduler = StubScheduler()
                ResultStore = InMemoryResultStore()
                AuditLog = None
            }

            let host = registerGreeter peer [ v1 ] (Some fusion)

            Expect.equal (List.length host.JobHandlers) 1 "the single LongRunning method yields one job handler"

            let handlerName, _ = List.head host.JobHandlers

            Expect.equal
                handlerName
                "_platform.peer.greeter.SlowSum"
                "the handler name follows the _platform.peer.{contract}.{method} convention"

        // ─── Phase 308 — owner rides the scheduled job payload ────

        testCaseAsync "a long-running dispatch stamps the validated caller as the scheduled payload's owner"
        <| async {
            let peer = factory ()
            let scheduler = RecordingScheduler()

            let fusion: PeerJobFusion = {
                Scheduler = scheduler
                ResultStore = InMemoryResultStore()
                AuditLog = None
            }

            registerGreeter peer [ v1 ] (Some fusion) |> ignore

            let argsJson = JsonRpc.serialize [ 3; 4 ]
            let! result = peer.Handle("greeter", mkContext v1 [ "buyer" ] 8, "SlowSum", argsJson)

            match result with
            | Ok _ -> ()
            | Error e -> failtestf "Expected the dispatch to schedule, got Error %A" e

            match scheduler.Registered with
            | None -> failtest "the dispatch never reached Schedule"
            | Some registration ->
                let envelope = JsonRpc.deserialize<PeerJobPayload> registration.Payload

                Expect.equal
                    envelope.OwnerPeerId
                    buyerId.PeerId
                    "the validated caller's PeerId is stamped as the job's owner"

                Expect.equal envelope.ArgsJson argsJson "the original positional args ride inside the envelope"
        }

        // ─── Phase 308 — handler parks the owner-stamped record ───

        testCaseAsync "the job handler parks the terminal status stamped with the envelope's owner"
        <| async {
            let peer = factory ()
            let store = InMemoryResultStore()

            let fusion: PeerJobFusion = {
                Scheduler = StubScheduler()
                ResultStore = store
                AuditLog = None
            }

            let host = registerGreeter peer [ v1 ] (Some fusion)
            let _, handler = List.head host.JobHandlers
            let jobId = Guid.NewGuid()

            let envelope: PeerJobPayload = {
                OwnerPeerId = buyerId.PeerId
                ArgsJson = JsonRpc.serialize [ 3; 4 ]
                RootRequestId = "root-308"
            }

            let jobCtx: JobContext = {
                JobId = jobId
                ScopeId = "_platform"
                AccessContext = AccessContext.unrestricted (AuthenticatedUser "_system")
                Attempt = 1
                Trigger = Manual
                TriggerSource = TriggerSource.ScheduledManually "_system"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = JsonRpc.serialize envelope
                DeadLetterDestination = None
            }

            let! jobResult = handler.Execute jobCtx
            Expect.equal jobResult Success "capturing the terminal status succeeds"

            let! record = (store :> IPeerJobResultStore).TryGetResult("_platform", jobId)

            match record with
            | None -> failtest "the handler did not park a record"
            | Some r ->
                Expect.equal r.OwnerPeerId buyerId.PeerId "the parked record is owned by the scheduling caller"

                match r.Status with
                | PeerJobStatus.Completed json ->
                    Expect.equal (JsonRpc.deserialize<int> json) 7 "SlowSum's typed result is parked for the owner"
                | other -> failtestf "Expected Completed, got %A" other
        }

        // ─── Phase 310 — terminal-outcome audit ───────────────────
        //
        // Before this phase a long-running call's audit trail ended at
        // dispatch: `peer.Handle` returns `Ok jobId`, so the only row said
        // `Succeeded = true, Outcome = "ok"` however the background
        // computation ended. These assert the *content* of the terminal
        // row, not merely that one appeared — a test that counted rows
        // would pass against an emitter writing the wrong outcome.

        testCaseAsync "a long-running dispatch threads the derived RootRequestId onto the scheduled payload"
        <| async {
            let peer = factory ()
            let scheduler = RecordingScheduler()

            let fusion: PeerJobFusion = {
                Scheduler = scheduler
                ResultStore = InMemoryResultStore()
                AuditLog = None
            }

            let host =
                JsonRpcPeerHost.contract<TerminalOutcomeContract> "terminal" [ v1 ] (Some fusion) terminalImpl

            peer.RegisterContract host.Registration

            let ctx = mkContext v1 [ "buyer" ] 8
            let! result = peer.Handle("terminal", ctx, "Compute", JsonRpc.serialize [ 21 ])

            match result with
            | Ok _ -> ()
            | Error e -> failtestf "Expected the dispatch to schedule, got Error %A" e

            match scheduler.Registered with
            | None -> failtest "the dispatch never reached Schedule"
            | Some registration ->
                let envelope = JsonRpc.deserialize<PeerJobPayload> registration.Payload

                Expect.equal
                    envelope.RootRequestId
                    ctx.RootRequestId
                    "the correlation id the receiver derived rides the job payload, so the terminal row can join the schedule-time row"
        }

        testCaseAsync "a completed long-running job records a terminal audit row carrying the real outcome"
        <| async {
            let peer = factory ()
            let store = InMemoryResultStore()
            let audit = RecordingAuditLog()

            let fusion: PeerJobFusion = {
                Scheduler = StubScheduler()
                ResultStore = store
                AuditLog = Some audit
            }

            let host =
                JsonRpcPeerHost.contract<TerminalOutcomeContract> "terminal" [ v1 ] (Some fusion) terminalImpl

            peer.RegisterContract host.Registration

            let handler =
                host.JobHandlers
                |> List.find (fun (n, _) -> n = PeerJob.handlerName "terminal" "Compute")
                |> snd

            let envelope: PeerJobPayload = {
                OwnerPeerId = buyerId.PeerId
                ArgsJson = JsonRpc.serialize [ 21 ]
                RootRequestId = "root-310-ok"
            }

            let! jobId, jobResult = runJobHandler handler (JsonRpc.serialize envelope)
            Expect.equal jobResult Success "capturing the terminal status succeeds"

            match audit.PeerJobRows with
            | [ row ] ->
                Expect.equal row.ContractId "terminal" "the row names the hosted contract"
                Expect.equal row.MethodName "Compute" "the row names the contract method whose job resolved"
                Expect.equal row.CallerPeerId buyerId.PeerId "the row attributes the scheduling caller"
                Expect.equal row.RootRequestId "root-310-ok" "the row carries the schedule-time correlation id"
                Expect.equal row.JobId jobId "the row carries the backing job id the caller polls with"
                Expect.isTrue row.Succeeded "a Completed job records success"
                Expect.equal row.Outcome "ok" "a Completed job records the ok outcome label"
            | rows -> failtestf "Expected exactly one PeerJobCompleted row, got %i" (List.length rows)
        }

        testCaseAsync "a failed long-running job records the failing PeerError case name, not ok"
        <| async {
            let peer = factory ()
            let audit = RecordingAuditLog()

            let fusion: PeerJobFusion = {
                Scheduler = StubScheduler()
                ResultStore = InMemoryResultStore()
                AuditLog = Some audit
            }

            let host =
                JsonRpcPeerHost.contract<TerminalOutcomeContract> "terminal" [ v1 ] (Some fusion) terminalImpl

            peer.RegisterContract host.Registration

            let handler =
                host.JobHandlers
                |> List.find (fun (n, _) -> n = PeerJob.handlerName "terminal" "Refuse")
                |> snd

            let envelope: PeerJobPayload = {
                OwnerPeerId = buyerId.PeerId
                ArgsJson = JsonRpc.serialize [ "k-anonymity gate" ]
                RootRequestId = "root-310-fail"
            }

            let! _, jobResult = runJobHandler handler (JsonRpc.serialize envelope)

            Expect.equal
                jobResult
                Success
                "a peer-side refusal is a recorded Failed status, not a job failure — re-running would double-execute"

            match audit.PeerJobRows with
            | [ row ] ->
                Expect.isFalse row.Succeeded "the trail no longer reports a failed computation as a success"

                Expect.equal
                    row.Outcome
                    "PeerUnauthorized"
                    "the specific PeerError case name is recorded, not a generic failure label"

                Expect.equal row.RootRequestId "root-310-fail" "the failure is correlated to its schedule-time row"
            | rows -> failtestf "Expected exactly one PeerJobCompleted row, got %i" (List.length rows)
        }

        testCaseAsync "the schedule-time row and the terminal row share one correlation id end to end"
        <| async {
            // The join that makes 18a's transparency read reconstructable:
            // dispatch through `Handle` (which the host audits at schedule
            // time under `trustedContext.RootRequestId`), then run the job
            // the dispatch scheduled and read the terminal row's id.
            let peer = factory ()
            let scheduler = RecordingScheduler()
            let audit = RecordingAuditLog()

            let fusion: PeerJobFusion = {
                Scheduler = scheduler
                ResultStore = InMemoryResultStore()
                AuditLog = Some audit
            }

            let host =
                JsonRpcPeerHost.contract<TerminalOutcomeContract> "terminal" [ v1 ] (Some fusion) terminalImpl

            peer.RegisterContract host.Registration

            let ctx = mkContext v1 [ "buyer" ] 8
            let! _ = peer.Handle("terminal", ctx, "Refuse", JsonRpc.serialize [ "gate" ])

            let scheduledPayload =
                match scheduler.Registered with
                | Some r -> r.Payload
                | None -> failtest "the dispatch never reached Schedule"

            let handler =
                host.JobHandlers
                |> List.find (fun (n, _) -> n = PeerJob.handlerName "terminal" "Refuse")
                |> snd

            let! _ = runJobHandler handler scheduledPayload

            match audit.PeerJobRows with
            | [ row ] ->
                Expect.equal
                    row.RootRequestId
                    ctx.RootRequestId
                    "the terminal row files under the same correlation id the schedule-time row used"

                Expect.equal row.CallerPeerId buyerId.PeerId "attribution survives the hop into the job substrate"
            | rows -> failtestf "Expected exactly one PeerJobCompleted row, got %i" (List.length rows)
        }

        testCaseAsync "a host without an audit log records nothing and still parks the result"
        <| async {
            // The control for the three assertions above: same handler,
            // same payload, audit log absent. A partial host records
            // nothing — matching the immediate path, where `auditPeerCall`
            // no-ops when no `IAuditLog` is registered.
            let peer = factory ()
            let store = InMemoryResultStore()

            let fusion: PeerJobFusion = {
                Scheduler = StubScheduler()
                ResultStore = store
                AuditLog = None
            }

            let host =
                JsonRpcPeerHost.contract<TerminalOutcomeContract> "terminal" [ v1 ] (Some fusion) terminalImpl

            peer.RegisterContract host.Registration

            let handler =
                host.JobHandlers
                |> List.find (fun (n, _) -> n = PeerJob.handlerName "terminal" "Compute")
                |> snd

            let envelope: PeerJobPayload = {
                OwnerPeerId = buyerId.PeerId
                ArgsJson = JsonRpc.serialize [ 4 ]
                RootRequestId = "root-310-silent"
            }

            let! jobId, jobResult = runJobHandler handler (JsonRpc.serialize envelope)
            Expect.equal jobResult Success "the job still completes"

            let! record = (store :> IPeerJobResultStore).TryGetResult(PeerJob.Scope, jobId)

            match record with
            | Some r ->
                match r.Status with
                | PeerJobStatus.Completed json ->
                    Expect.equal (JsonRpc.deserialize<int> json) 8 "the typed result is parked regardless of auditing"
                | other -> failtestf "Expected Completed, got %A" other
            | None -> failtest "the handler did not park a record"
        }

        testCaseAsync "a flaky audit store never changes the job result"
        <| async {
            let peer = factory ()
            let store = InMemoryResultStore()

            let fusion: PeerJobFusion = {
                Scheduler = StubScheduler()
                ResultStore = store
                AuditLog = Some(ThrowingAuditLog())
            }

            let host =
                JsonRpcPeerHost.contract<TerminalOutcomeContract> "terminal" [ v1 ] (Some fusion) terminalImpl

            peer.RegisterContract host.Registration

            let handler =
                host.JobHandlers
                |> List.find (fun (n, _) -> n = PeerJob.handlerName "terminal" "Compute")
                |> snd

            let envelope: PeerJobPayload = {
                OwnerPeerId = buyerId.PeerId
                ArgsJson = JsonRpc.serialize [ 5 ]
                RootRequestId = "root-310-flaky"
            }

            let! jobId, jobResult = runJobHandler handler (JsonRpc.serialize envelope)
            Expect.equal jobResult Success "an audit-store failure does not fail the job"

            let! record = (store :> IPeerJobResultStore).TryGetResult(PeerJob.Scope, jobId)

            match record with
            | Some r ->
                match r.Status with
                | PeerJobStatus.Completed json ->
                    Expect.equal (JsonRpc.deserialize<int> json) 10 "the parked result survives the audit failure"
                | other -> failtestf "Expected Completed, got %A" other
            | None -> failtest "the handler did not park a record"
        }

        testCaseAsync "a job scheduled before this phase still records a terminal row, uncorrelated"
        <| async {
            // A pre-310 payload carries no `RootRequestId`; a missing JSON
            // field is absence, not a parse failure, so it deserialises to
            // null. The row must still be written — an uncorrelated
            // terminal outcome beats a lost one — and must not carry null.
            let peer = factory ()
            let audit = RecordingAuditLog()

            let fusion: PeerJobFusion = {
                Scheduler = StubScheduler()
                ResultStore = InMemoryResultStore()
                AuditLog = Some audit
            }

            let host =
                JsonRpcPeerHost.contract<TerminalOutcomeContract> "terminal" [ v1 ] (Some fusion) terminalImpl

            peer.RegisterContract host.Registration

            let handler =
                host.JobHandlers
                |> List.find (fun (n, _) -> n = PeerJob.handlerName "terminal" "Compute")
                |> snd

            let legacy: LegacyPeerJobPayload = {
                OwnerPeerId = buyerId.PeerId
                ArgsJson = JsonRpc.serialize [ 6 ]
            }

            let! _, jobResult = runJobHandler handler (JsonRpc.serialize legacy)
            Expect.equal jobResult Success "an old payload still resolves"

            match audit.PeerJobRows with
            | [ row ] ->
                Expect.equal row.CallerPeerId buyerId.PeerId "the pre-310 owner still attributes the row"
                Expect.equal row.RootRequestId "" "the absent correlation id normalises to empty, never null"
                Expect.equal row.Outcome "ok" "the terminal outcome is still recorded"
            | rows -> failtestf "Expected exactly one PeerJobCompleted row, got %i" (List.length rows)
        }
    ]