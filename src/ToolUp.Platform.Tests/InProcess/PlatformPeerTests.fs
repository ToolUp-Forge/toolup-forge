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
        member _.Invoke(target, contractId, methodName, payload) =
            captured.Value <- payload.Context.RootRequestId
            inner.Invoke(target, contractId, methodName, payload)

        member _.PollJob(target, contractId, jobId) =
            inner.PollJob(target, contractId, jobId)

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
/// singletons (resolved per-request by the host handlers).
let private buildSellerHost (auth: IPeerAuthProvider) (peer: IPlatformPeer) (audit: IAuditLog) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost
                .UseTestServer()
                .ConfigureServices(fun services ->
                    services.AddGiraffe() |> ignore
                    services.AddSingleton<IPeerAuthProvider>(auth) |> ignore
                    services.AddSingleton<IPlatformPeer>(peer) |> ignore
                    services.AddSingleton<IAuditLog>(audit) |> ignore)
                .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe JsonRpcPeerHost.routes)
            |> ignore)
        .Build()

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