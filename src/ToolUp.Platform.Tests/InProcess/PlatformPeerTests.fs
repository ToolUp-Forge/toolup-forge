module ToolUp.Platform.Tests.InProcess.PlatformPeerTests

open System
open System.Collections.Concurrent
open System.Net.Http
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.InterPlatform
open ToolUp.Platform.Tests.Contracts

// ─── IPlatformPeerContract — in-process binding + worked example ─────
//
// Two halves.
//
// 1. The in-process binding wires the parameterised
//    `IPlatformPeerContract` pack against the default receiver
//    (`DefaultPlatformPeer`), so the ≥ 8 contract-surface tests run
//    over a fresh contract table with no transport.
//
// 2. The worked example stands up a genuine two-deployment scenario over
//    a `TestServer`: a *buyer* deployment calls a typed contract on a
//    *seller* deployment across an HTTP boundary, through the real
//    `JsonRpcPeerHost.routes` (auth-gated, fail-closed) and the real
//    `HttpPeerClient` initiator transport. It covers the three behaviours
//    the in-process pack cannot reach because they need the JSON-RPC
//    host's private internals: identity validation (a wrong signing key
//    is rejected), audit emission (`PeerCallCompleted` is recorded once),
//    and a matching `RootRequestId` observed on both sides of the wire.

// ─── In-process binding ──────────────────────────────────────────────

let inProcessTests =
    IPlatformPeerContract.tests "DefaultPlatformPeer (in-process loopback)" (fun () ->
        DefaultPlatformPeer() :> IPlatformPeer)

// ─── Worked-example fixtures ─────────────────────────────────────────

/// The typed contract the seller hosts and the buyer calls. One
/// immediate method — enough to demonstrate the round trip end to end.
///
/// NOT `private`: the host reflects via `FSharpType.IsRecord` without the
/// private-representation flag, so a `private` record reads back as a
/// non-record and `JsonRpcPeerHost.contract` rejects it.
type DirectoryContract = {
    GetCapabilities: unit -> Async<string list>
}

let private directoryImpl: DirectoryContract = {
    GetCapabilities = fun () -> async { return [ "directory.list"; "directory.lookup" ] }
}

let private buyerId: PeerIdentity = {
    PeerId = "buyer"
    DisplayName = "Buyer Deployment"
}

let private sellerId: PeerIdentity = {
    PeerId = "seller"
    DisplayName = "Seller Deployment"
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }

/// In-memory `ISecretStore` — the canonical binding for the JWT auth
/// provider, which reads the per-peer signing key on every call.
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

/// `IPeerClient` decorator that stashes the `RootRequestId` the proxy
/// generated for an outbound call before delegating to the real
/// transport. The buyer side never sees the cascade id otherwise (the
/// proxy mints it internally), so this is how the test captures the
/// initiator's view of the correlation id to compare against the
/// receiver's audit row.
type private RecordingPeerClient(inner: IPeerClient, captured: string ref) =
    interface IPeerClient with
        member _.Invoke(target, contractId, methodName, payload, ?cancellationToken) =
            captured.Value <- payload.Context.RootRequestId

            match cancellationToken with
            | Some ct -> inner.Invoke(target, contractId, methodName, payload, ct)
            | None -> inner.Invoke(target, contractId, methodName, payload)

        member _.PollJob(target, contractId, jobId, ?cancellationToken) =
            match cancellationToken with
            | Some ct -> inner.PollJob(target, contractId, jobId, ct)
            | None -> inner.PollJob(target, contractId, jobId)

/// `IAuditLog` that retains every recorded event for inspection. The
/// peer host records `PeerCallCompleted` best-effort per inbound call.
type private RecordingAuditLog() =
    let events = ConcurrentBag<AuditEvent>()
    member _.Events = events |> List.ofSeq

    interface IAuditLog with
        member _.Record(_scopeId, audit) = async { events.Add audit }

        member _.GetAuditTrail(_scopeId, _dateRange, _eventType) = async { return events |> List.ofSeq }

/// Seed a peer's symmetric HS256 signing key into a secret store at the
/// reserved `_platform` scope under `peers/{peerId}/signing-key` — the
/// exact key `JwtPeerAuthProvider` reads on every issue / validate.
let private seedSigningKey (store: ISecretStore) (peerId: string) (key: string) =
    store.SetSecret("_platform", $"peers/{peerId}/signing-key", key)
    |> Async.RunSynchronously
    |> ignore

/// Build a `TestServer`-hosted seller deployment that mounts the real
/// `JsonRpcPeerHost.routes` with the given providers registered as DI
/// singletons (resolved per-request by the host handlers). `fusion` is
/// registered only when supplied — the poll-route fixtures need it; the
/// immediate-dispatch worked example does not.
let private buildSellerHostWith
    (fusion: PeerJobFusion option)
    (auth: IPeerAuthProvider)
    (peer: IPlatformPeer)
    (audit: IAuditLog)
    : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost
                .UseTestServer()
                .ConfigureServices(fun services ->
                    // No `AddGiraffe()` needed: `JsonRpcPeerHost.routes`
                    // writes via the DI-free `SetStatusCode` /
                    // `WriteStringAsync` primitives and never resolves
                    // the Giraffe serializer / negotiation services.
                    services.AddSingleton<IPeerAuthProvider>(auth) |> ignore
                    services.AddSingleton<IPlatformPeer>(peer) |> ignore
                    services.AddSingleton<IAuditLog>(audit) |> ignore

                    match fusion with
                    | Some f -> services.AddSingleton<PeerJobFusion>(f) |> ignore
                    | None -> ())
                .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe JsonRpcPeerHost.routes)
            |> ignore)
        .Build()

let private buildSellerHost = buildSellerHostWith None

/// A proxy config that routes through `client` to the seller over the
/// absolute base URL the `TestServer` client expects.
let private buyerProxyConfig (client: IPeerClient) : PeerProxyConfig = {
    Client = client
    Target = {
        Peer = sellerId
        BaseUrl = "http://localhost"
    }
    Caller = buyerId
    User = Anonymous
    Version = v1
    ContractId = "directory"
    HopBudget = 8
}

let private peerCallRootRequestIds (audit: RecordingAuditLog) =
    audit.Events
    |> List.choose (fun e ->
        match e with
        | PeerCallCompleted payload -> Some payload
        | _ -> None)

let workedExampleTests =
    testList "IPlatformPeerContract — buyer→seller worked example (TestServer)" [

        // ─── Happy path: round trip + matching RootRequestId + audit ──

        testCaseAsync "GetCapabilities round-trips buyer→seller with a matching RootRequestId and one audit row"
        <| async {
            let signingKey = "shared-buyer-seller-signing-key-0123456789"

            // Both deployments hold the buyer's signing key: the buyer
            // signs its bearer token with it, the seller verifies against
            // the same key. (Shared out of band in production.)
            let buyerSecrets = InMemorySecretStore() :> ISecretStore
            let sellerSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey buyerSecrets buyerId.PeerId signingKey
            seedSigningKey sellerSecrets buyerId.PeerId signingKey

            let buyerAuth = JwtPeerAuthProvider(buyerSecrets) :> IPeerAuthProvider
            let sellerAuth = JwtPeerAuthProvider(sellerSecrets) :> IPeerAuthProvider

            let seller = DefaultPlatformPeer() :> IPlatformPeer

            let directoryHost =
                JsonRpcPeerHost.contract<DirectoryContract> "directory" [ v1 ] None directoryImpl

            seller.RegisterContract directoryHost.Registration

            let audit = RecordingAuditLog()
            let host = buildSellerHost sellerAuth seller audit
            host.Start()
            use testClient = host.GetTestClient()

            let captured = ref ""
            let transport = HttpPeerClient(testClient, buyerAuth, buyerId) :> IPeerClient
            let recording = RecordingPeerClient(transport, captured) :> IPeerClient

            let proxy = JsonRpcPeerClient.create<DirectoryContract> (buyerProxyConfig recording)

            let! caps = proxy.GetCapabilities()

            Expect.equal
                caps
                [ "directory.list"; "directory.lookup" ]
                "the seller's capability list crosses the HTTP boundary intact"

            let recorded = peerCallRootRequestIds audit

            Expect.hasLength recorded 1 "exactly one PeerCallCompleted audit row is emitted for the inbound call"

            let payload = List.head recorded
            Expect.equal payload.ContractId "directory" "the audit row names the dispatched contract"
            Expect.equal payload.MethodName "GetCapabilities" "the audit row names the dispatched method"
            Expect.equal payload.CallerPeerId buyerId.PeerId "the audit row attributes the validated calling peer"
            Expect.isTrue payload.Succeeded "the resolved call is recorded as successful"

            Expect.equal
                payload.RootRequestId
                captured.Value
                "the RootRequestId the buyer minted is the same id the seller audits — one correlation id across the wire"
        }

        // ─── Identity validation: a wrong signing key is rejected ─────

        testCaseAsync "a buyer signing with the wrong key is rejected by the seller as unauthorized"
        <| async {
            let sellerKey = "seller-trusted-buyer-key-aaaaaaaaaaaaaaaa"
            let buyerWrongKey = "buyer-divergent-key-bbbbbbbbbbbbbbbbbbbb"

            // The seller trusts `sellerKey` for the buyer; the buyer signs
            // with a different key, so the HS256 signature will not verify.
            let buyerSecrets = InMemorySecretStore() :> ISecretStore
            let sellerSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey buyerSecrets buyerId.PeerId buyerWrongKey
            seedSigningKey sellerSecrets buyerId.PeerId sellerKey

            let buyerAuth = JwtPeerAuthProvider(buyerSecrets) :> IPeerAuthProvider
            let sellerAuth = JwtPeerAuthProvider(sellerSecrets) :> IPeerAuthProvider

            let seller = DefaultPlatformPeer() :> IPlatformPeer

            let directoryHost =
                JsonRpcPeerHost.contract<DirectoryContract> "directory" [ v1 ] None directoryImpl

            seller.RegisterContract directoryHost.Registration

            let audit = RecordingAuditLog()
            let host = buildSellerHost sellerAuth seller audit
            host.Start()
            use testClient = host.GetTestClient()

            let captured = ref ""
            let transport = HttpPeerClient(testClient, buyerAuth, buyerId) :> IPeerClient
            let recording = RecordingPeerClient(transport, captured) :> IPeerClient
            let proxy = JsonRpcPeerClient.create<DirectoryContract> (buyerProxyConfig recording)

            let! outcome = proxy.GetCapabilities() |> Async.Catch

            match outcome with
            | Choice2Of2(PeerInvocationException(PeerUnauthorized _)) -> ()
            | Choice2Of2 ex -> failtestf "Expected PeerInvocationException(PeerUnauthorized …), got %A" ex
            | Choice1Of2 caps -> failtestf "Expected the call to be rejected, but it returned %A" caps

            Expect.isEmpty
                (peerCallRootRequestIds audit)
                "an unauthorized call never reaches dispatch, so no PeerCallCompleted row is emitted"
        }
    ]

// ─── Job-poll caller-ownership scoping (Phase 308) ───────────────────
//
// The poll route (`GET /peer/v1/{contractId}/jobs/{jobId}`) returns a
// parked long-running result only to the peer that scheduled it —
// possession of the `jobId` is not authorization (GP 4). The fixtures
// seed the result store directly via `SaveResult` (the exact write
// `PeerJobHandler.Execute` performs; the dispatch→handler owner
// threading is covered by the contract pack) so these tests isolate the
// poll seam without standing up a scheduler.

let private intruderId: PeerIdentity = {
    PeerId = "intruder"
    DisplayName = "Intruder Deployment"
}

let private pollTarget: TargetPeer = {
    Peer = sellerId
    BaseUrl = "http://localhost"
}

/// The parked result the no-leak assertions below look for. It carries
/// letters outside the hex alphabet on purpose.
///
/// The refusal echoes the polled `jobId` as the response's correlation
/// id (Phase 315), and a jobId is a random v4 GUID — so a numeric marker
/// like `42` appears somewhere inside its 32 hex digits about 15% of the
/// time, and the "no trace of the parked result" assertion then fails for
/// a reason that has nothing to do with a leak. Across the two cases
/// below that is a ~28% false-red rate. A sentinel that cannot be spelled
/// in hex measures what the assertion claims to measure.
///
/// Same reasoning, same sentinel as `PeerWireHardeningTests`, which hit
/// this when it introduced the echo.
let private parkedSentinel = "parked-result-QZX"

/// Fusion pair over a fresh in-memory store. The scheduler stub throws
/// on any use — the poll route must never schedule.
let private pollFixtureFusion () =
    let store = IPlatformPeerContract.InMemoryResultStore() :> IPeerJobResultStore

    let fusion: PeerJobFusion = {
        Scheduler = IPlatformPeerContract.StubScheduler()
        ResultStore = store
        AuditLog = None
    }

    store, fusion

let jobPollOwnershipTests =
    testList "JsonRpcPeerHost — job-poll caller-ownership scoping (Phase 308)" [

        // ─── (a) + (b): owner reads, any other validated peer refused ─

        testCaseAsync
            "a parked result is readable by its scheduling peer only; another validated peer is refused with no payload"
        <| async {
            let buyerKey = "buyer-signing-key-0123456789abcdefghijkl"
            let intruderKey = "intruder-signing-key-0123456789abcdefghi"

            // The seller trusts both peers' signing keys — the intruder
            // is a *validated* peer, just not the scheduling caller.
            let sellerSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey sellerSecrets buyerId.PeerId buyerKey
            seedSigningKey sellerSecrets intruderId.PeerId intruderKey

            let buyerSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey buyerSecrets buyerId.PeerId buyerKey

            let intruderSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey intruderSecrets intruderId.PeerId intruderKey

            let sellerAuth = JwtPeerAuthProvider(sellerSecrets) :> IPeerAuthProvider
            let buyerAuth = JwtPeerAuthProvider(buyerSecrets) :> IPeerAuthProvider
            let intruderAuth = JwtPeerAuthProvider(intruderSecrets) :> IPeerAuthProvider

            let store, fusion = pollFixtureFusion ()

            // Park a completed result owned by the buyer — the exact
            // write the job handler performs when the job finishes.
            let jobId = Guid.NewGuid()

            do!
                store.SaveResult(
                    "_platform",
                    jobId,
                    buyerId.PeerId,
                    PeerJobStatus.Completed(JsonRpc.serialize parkedSentinel)
                )

            let seller = DefaultPlatformPeer() :> IPlatformPeer
            let host = buildSellerHostWith (Some fusion) sellerAuth seller (RecordingAuditLog())
            host.Start()
            use testClient = host.GetTestClient()

            // (a) the scheduling peer reads its own terminal status …
            let buyerClient = HttpPeerClient(testClient, buyerAuth, buyerId) :> IPeerClient
            let! owned = buyerClient.PollJob(pollTarget, "directory", jobId)

            match owned with
            | Ok(PeerJobStatus.Completed json) ->
                Expect.equal (JsonRpc.deserialize<string> json) parkedSentinel "the owner reads the parked typed result"
            | other -> failtestf "Expected Completed for the owner, got %A" other

            // … and an unknown jobId stays Pending for the owner — not a
            // disclosure of existence, the same answer as "not finished".
            let! unknown = buyerClient.PollJob(pollTarget, "directory", Guid.NewGuid())

            match unknown with
            | Ok PeerJobStatus.Pending -> ()
            | other -> failtestf "Expected Pending for an unknown jobId, got %A" other

            // (b) a different validated peer polling the same jobId is
            // rejected and never sees the payload …
            let intruderClient =
                HttpPeerClient(testClient, intruderAuth, intruderId) :> IPeerClient

            let! stolen = intruderClient.PollJob(pollTarget, "directory", jobId)

            match stolen with
            | Error(PeerUnauthorized _) -> ()
            | other -> failtestf "Expected PeerUnauthorized for a non-owner, got %A" other

            // … verified at the raw HTTP layer too: 401, no result body.
            let! tokenResult = intruderAuth.IssuePeerToken(intruderId, sellerId, Anonymous)

            let intruderToken =
                match tokenResult with
                | Ok t -> t
                | Error e -> failtestf "intruder token mint failed: %A" e

            use raw =
                new HttpRequestMessage(HttpMethod.Get, $"http://localhost/peer/v1/directory/jobs/{jobId}")

            raw.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", intruderToken)
            let! response = testClient.SendAsync raw |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

            Expect.equal (int response.StatusCode) 401 "a non-owner poll is refused at the HTTP layer"

            // CONTROL — the refusal really is the correlated JSON-RPC
            // body, so the absence assertion below is measuring a real
            // payload rather than passing vacuously on an empty response.
            Expect.stringContains body (string jobId) "the refusal correlates with the polled jobId (Phase 315)"

            Expect.isFalse (body.Contains parkedSentinel) "the refused response carries no trace of the parked result"
        }

        // ─── (c): unauthenticated poll stays 401 (unchanged) ──────────

        testCaseAsync "an unauthenticated poll is refused before any store read"
        <| async {
            let sellerSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey sellerSecrets buyerId.PeerId "buyer-signing-key-0123456789abcdefghijkl"
            let sellerAuth = JwtPeerAuthProvider(sellerSecrets) :> IPeerAuthProvider

            let store, fusion = pollFixtureFusion ()
            let jobId = Guid.NewGuid()

            do!
                store.SaveResult(
                    "_platform",
                    jobId,
                    buyerId.PeerId,
                    PeerJobStatus.Completed(JsonRpc.serialize parkedSentinel)
                )

            let seller = DefaultPlatformPeer() :> IPlatformPeer
            let host = buildSellerHostWith (Some fusion) sellerAuth seller (RecordingAuditLog())
            host.Start()
            use testClient = host.GetTestClient()

            use raw =
                new HttpRequestMessage(HttpMethod.Get, $"http://localhost/peer/v1/directory/jobs/{jobId}")

            let! response = testClient.SendAsync raw |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

            Expect.equal (int response.StatusCode) 401 "an unauthenticated poll stays 401"

            // CONTROL — see the matching note in the case above.
            Expect.stringContains body (string jobId) "the refusal correlates with the polled jobId (Phase 315)"

            Expect.isFalse (body.Contains parkedSentinel) "the refused response carries no trace of the parked result"
        }
    ]

// ─── Audience binding (Phase 130) ────────────────────────────────────
//
// `ValidatePeerToken` binds an inbound token's `aud` claim to the
// receiver's own peer id when one is composed. These cases drive the
// provider directly (no transport) so each claim shape is controlled
// exactly — including a token with NO `aud`, which `IssuePeerToken`
// never mints, so the tests hand-roll raw HS256 tokens.

let private base64UrlRaw (bytes: byte[]) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

/// Mint a raw HS256 token directly so the test controls exactly which
/// claims are present. `aud = None` omits the `aud` claim entirely
/// (exercises the fail-closed missing-audience path).
let private mintRawToken (signingKey: string) (issuer: string) (aud: string option) =
    let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

    let audFragment =
        match aud with
        | Some a -> sprintf "\"aud\":\"%s\"," a
        | None -> ""

    let header = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}"

    let payload =
        sprintf
            "{\"iss\":\"%s\",%s\"name\":\"%s\",\"iat\":%d,\"exp\":%d,\"nbf\":%d}"
            issuer
            audFragment
            issuer
            now
            (now + 300L)
            now

    let h = base64UrlRaw (System.Text.Encoding.UTF8.GetBytes header)
    let p = base64UrlRaw (System.Text.Encoding.UTF8.GetBytes payload)
    let signingInput = sprintf "%s.%s" h p

    use hmac =
        new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes signingKey)

    let signature =
        base64UrlRaw (hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes signingInput))

    sprintf "%s.%s" signingInput signature

let audienceBindingTests =
    let signingKey = "audience-binding-shared-key-0123456789abcd"
    let issuer = buyerId.PeerId

    // A receiver secret store seeded with the issuer's signing key, so
    // the HS256 signature verifies on every case below — isolating the
    // audience decision from the signature decision.
    let receiverSecrets () =
        let s = InMemorySecretStore() :> ISecretStore
        seedSigningKey s issuer signingKey
        s

    testList "JwtPeerAuthProvider — audience binding (Phase 130)" [

        // ─── Cross-receiver replay: token for B rejected at C ─────────

        testCaseAsync "a token minted for peer B is rejected by a provider whose local id is C (same issuer key)"
        <| async {
            let token = mintRawToken signingKey issuer (Some "peer-b")

            let provider =
                JwtPeerAuthProvider(receiverSecrets (), "peer-c") :> IPeerAuthProvider

            match! provider.ValidatePeerToken token with
            | Error(PeerUnauthorized _) -> ()
            | Error e -> failtestf "Expected PeerUnauthorized, got %A" e
            | Ok p -> failtestf "Expected rejection — cross-receiver replay accepted as %s" p.Caller.PeerId
        }

        // ─── Correctly-addressed token accepted ───────────────────────

        testCaseAsync "a token addressed to this receiver (aud = local id) is accepted"
        <| async {
            let token = mintRawToken signingKey issuer (Some "peer-c")

            let provider =
                JwtPeerAuthProvider(receiverSecrets (), "peer-c") :> IPeerAuthProvider

            match! provider.ValidatePeerToken token with
            | Ok p -> Expect.equal p.Caller.PeerId issuer "the validated caller is the issuer"
            | Error e -> failtestf "Expected acceptance, got %A" e
        }

        // ─── Missing aud rejected fail-closed ─────────────────────────

        testCaseAsync "a token with no aud is rejected fail-closed when the receiver has an identity"
        <| async {
            let token = mintRawToken signingKey issuer None

            let provider =
                JwtPeerAuthProvider(receiverSecrets (), "peer-c") :> IPeerAuthProvider

            match! provider.ValidatePeerToken token with
            | Error(PeerUnauthorized _) -> ()
            | Error e -> failtestf "Expected PeerUnauthorized, got %A" e
            | Ok _ -> failtest "Expected fail-closed rejection of a token with no aud claim"
        }

        // ─── Unbound receiver keeps pre-130 behaviour (GP 11) ─────────

        testCaseAsync "a receiver with no declared identity keeps pre-130 behaviour (audience unbound)"
        <| async {
            // No expectedAudience → the audience check is skipped, so a
            // token addressed to a different peer still validates on
            // signature alone (byte-for-byte the pre-130 path).
            let token = mintRawToken signingKey issuer (Some "peer-b")
            let provider = JwtPeerAuthProvider(receiverSecrets ()) :> IPeerAuthProvider

            match! provider.ValidatePeerToken token with
            | Ok p -> Expect.equal p.Caller.PeerId issuer "unbound receiver accepts on signature alone"
            | Error e -> failtestf "Expected acceptance (audience unbound), got %A" e
        }
    ]