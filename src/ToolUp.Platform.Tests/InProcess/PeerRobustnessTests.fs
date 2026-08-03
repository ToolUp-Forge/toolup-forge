module ToolUp.Platform.Tests.InProcess.PeerRobustnessTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
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

// ─── Phase 343 — peer robustness roundup ─────────────────────────────
//
// Three independent hardenings of the federation layer, each pinned with
// a probe that goes red when the hardening is removed and a CONTROL
// asserting the legitimate path still succeeds. Without the pair, "the
// call was refused" would pass just as happily against a layer that had
// broken and started refusing everything.
//
//   1. **A malformed base64url signature is a 401, not a 500.**
//      `Base64Url.decode` throws on a non-base64url segment, and
//      `JwtCrypto.result` is a `Result` CE rather than an exception
//      boundary — so the throw escaped `ValidatePeerToken` and the host
//      answered 500 where every other bad credential answers 401. Still
//      fail-closed, but a status code an unauthenticated caller can flip
//      at will is an oracle separating "malformed encoding" from "wrong
//      key". Fixed at source, plus a host-level backstop so the class
//      cannot recur through some future provider.
//   2. **A non-2xx capability-profile fetch does not degrade.** The
//      18d fallback mapped ANY non-2xx onto the bare capability list,
//      which carries no method lifecycle at all — so a receiver (or
//      anything on the path) could mask a `Deprecated` method by
//      choosing a status code, and nothing logged a downgrade.
//   3. **An asymmetric provider verifies without the private key.** The
//      symmetric default shares one secret per peer, so one receiver
//      compromise leaks every trusted peer's minting key.
//
// The HS256 default composition is unchanged by all three (GP 11), and
// the last list below asserts that rather than assuming it.

// ─── Fixtures ────────────────────────────────────────────────────────

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

let private seed (store: ISecretStore) (key: string) (value: string) =
    store.SetSecret("_platform", key, value) |> Async.RunSynchronously |> ignore

let private callerId: PeerIdentity = {
    PeerId = "buyer"
    DisplayName = "Buyer Deployment"
}

let private receiverId: PeerIdentity = {
    PeerId = "seller"
    DisplayName = "Seller Deployment"
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }

let private contractId = "ledger"

let private hs256Key = "peer-robustness-shared-signing-key-0123456789"

/// A segment that is NOT valid base64url — `!` is outside the alphabet
/// on both the standard and the URL-safe variants.
let private malformedSignature = "!!!not-base64url!!!"

/// A segment that IS valid base64url but is not the right MAC. The
/// distinction is the whole point of case (1): both must be refused
/// identically, or the difference is the oracle.
let private wrongButWellFormedSignature =
    Base64Url.encode (Encoding.UTF8.GetBytes "not-the-signature")

let private issue (provider: IPeerAuthProvider) (caller: PeerIdentity) (audience: PeerIdentity) = async {
    match! provider.IssuePeerToken(caller, audience, Anonymous) with
    | Ok token -> return token
    | Error e -> return failtestf "Expected a minted token, got %A" e
}

/// Replace a token's signature segment, leaving header + payload intact.
let private withSignature (signature: string) (token: string) =
    let parts = token.Split '.'
    $"{parts[0]}.{parts[1]}.{signature}"

let private expectAccepted (label: string) (result: Result<PeerPrincipal, PeerError>) =
    match result with
    | Ok principal -> Expect.equal principal.Caller.PeerId callerId.PeerId label
    | Error e -> failtestf "%s — expected acceptance, got %A" label e

let private expectRefused (label: string) (result: Result<PeerPrincipal, PeerError>) =
    match result with
    | Error(PeerUnauthorized _) -> ()
    | Error e -> failtestf "%s — expected PeerUnauthorized, got %A" label e
    | Ok p -> failtestf "%s — expected refusal, but the call was admitted as %s" label p.Caller.PeerId

// ─── (1) Malformed base64url signature ───────────────────────────────

/// The contract the receiver hosts. NOT `private`: the host reflects via
/// `FSharpType.IsRecord`, and a private record reads back as a non-record.
type LedgerContract = { Balance: unit -> Async<int> }

let private ledgerImpl: LedgerContract = {
    Balance = fun () -> async { return 4200 }
}

/// An auth provider that THROWS rather than returning a typed error —
/// the pre-343 `JwtPeerAuthProvider` behaviour on a malformed signature,
/// expressed as a fixture so the host's backstop is reachable
/// deterministically and stays covered after the source fix.
type private ThrowingAuthProvider() =
    interface IPeerAuthProvider with
        member _.IssuePeerToken(_, _, _) = async { return Error(PeerUnauthorized "not used") }

        member _.ValidatePeerToken _ = async {
            return raise (FormatException "The input is not a valid Base-64 string")
        }

        member _.VerifyDelegation _ = async { return Ok() }

/// A `TestServer`-hosted receiver mounting the real
/// `JsonRpcPeerHost.routes`, so a status code is observed at the HTTP
/// layer rather than inferred from a `PeerError`.
let private buildReceiver (auth: IPeerAuthProvider) : IHost =
    let peer = DefaultPlatformPeer() :> IPlatformPeer

    let ledgerHost =
        JsonRpcPeerHost.contract<LedgerContract> contractId [ v1 ] None ledgerImpl

    peer.RegisterContract ledgerHost.Registration

    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost
                .UseTestServer()
                .ConfigureServices(fun services ->
                    services.AddSingleton<IPeerAuthProvider>(auth) |> ignore
                    services.AddSingleton<IPlatformPeer>(peer) |> ignore)
                .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe JsonRpcPeerHost.routes)
            |> ignore)
        .Build()

/// POST an arbitrary body to the contract route with an arbitrary
/// bearer token, returning the raw status code and body.
let private postContract (client: HttpClient) (token: string option) (body: string) = async {
    use request = new HttpRequestMessage(HttpMethod.Post, $"/peer/v1/{contractId}")
    request.Content <- new StringContent(body, Encoding.UTF8, "application/json")

    match token with
    | Some t -> request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", t)
    | None -> ()

    let! response = client.SendAsync request |> Async.AwaitTask
    let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
    return int response.StatusCode, responseBody
}

let base64UrlSignatureTests =
    testList "Phase 343 — a malformed base64url signature is a clean 401" [

        testCase "the probe really is malformed base64url"
        <| fun _ ->
            // Verify the probe, not just the verdict: every case below
            // rests on this segment being un-decodable. If some future
            // codec started accepting it, the 401 assertions would still
            // pass while measuring nothing.
            Expect.isNone
                (Base64Url.tryDecode malformedSignature)
                "the malformed segment does not decode — this is what the rest of this list is about"

            Expect.throws
                (fun () -> Base64Url.decode malformedSignature |> ignore)
                "…and the THROWING codec is what the pre-343 path called, which is where the 500 came from"

            Expect.isSome
                (Base64Url.tryDecode wrongButWellFormedSignature)
                "the control segment decodes cleanly — it is wrong, not malformed"

        testCaseAsync "the provider returns a typed refusal rather than throwing"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            seed secrets $"peers/{callerId.PeerId}/signing-key" hs256Key
            let provider = JwtPeerAuthProvider(secrets) :> IPeerAuthProvider

            let! token = issue provider callerId receiverId

            let! result =
                provider.ValidatePeerToken(withSignature malformedSignature token)
                |> Async.Catch

            match result with
            | Choice1Of2 r -> expectRefused "a token whose signature segment is not base64url" r
            | Choice2Of2 ex ->
                failtestf
                    "the provider THREW (%s) — this is the pre-343 defect: an exception here becomes a 500 at the host, and no other bad credential does that"
                    (ex.GetType().Name)
        }

        testCaseAsync "a malformed signature is indistinguishable from a merely wrong one"
        <| async {
            // The oracle, closed. A 500-vs-401 split (or two different
            // messages) tells an unauthenticated caller which of its two
            // guesses was closer, before any credential was accepted.
            let secrets = InMemorySecretStore() :> ISecretStore
            seed secrets $"peers/{callerId.PeerId}/signing-key" hs256Key
            let provider = JwtPeerAuthProvider(secrets) :> IPeerAuthProvider

            let! token = issue provider callerId receiverId
            let! malformed = provider.ValidatePeerToken(withSignature malformedSignature token)
            let! wrong = provider.ValidatePeerToken(withSignature wrongButWellFormedSignature token)

            expectRefused "malformed encoding" malformed
            expectRefused "well-formed but wrong" wrong

            Expect.equal
                malformed
                wrong
                "both refusals carry the same PeerError — an attacker learns nothing about WHY from the difference"
        }

        testCaseAsync "CONTROL — the untouched token still validates"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            seed secrets $"peers/{callerId.PeerId}/signing-key" hs256Key
            let provider = JwtPeerAuthProvider(secrets) :> IPeerAuthProvider

            let! token = issue provider callerId receiverId
            let! result = provider.ValidatePeerToken token

            expectAccepted
                "the genuine token — the refusals above came from the signature, not from a broken validator"
                result
        }

        testCaseAsync "the HOST answers 401, not 500, for a malformed signature"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            seed secrets $"peers/{callerId.PeerId}/signing-key" hs256Key
            let auth = JwtPeerAuthProvider(secrets) :> IPeerAuthProvider

            let! token = issue auth callerId receiverId

            use host = buildReceiver auth
            host.Start()
            use client = host.GetTestClient()

            let! malformedStatus, malformedBody =
                postContract client (Some(withSignature malformedSignature token)) "{}"

            let! wrongStatus, wrongBody =
                postContract client (Some(withSignature wrongButWellFormedSignature token)) "{}"

            Expect.equal malformedStatus 401 "a malformed signature is a rejected credential, not a server fault"
            Expect.equal wrongStatus 401 "…as is a wrong one"

            Expect.equal
                malformedBody
                wrongBody
                "identical response bodies — the status code and the message are both free of the oracle"
        }

        testCaseAsync "CONTROL — a valid token gets PAST auth on the same route"
        <| async {
            // Without this, the 401s above would pass equally against a
            // host that rejected everything. `{}` is not a JSON-RPC
            // envelope, so a request that authenticates lands on the
            // parse failure (400) — which is exactly the evidence wanted:
            // it got further than the credential check.
            let secrets = InMemorySecretStore() :> ISecretStore
            seed secrets $"peers/{callerId.PeerId}/signing-key" hs256Key
            let auth = JwtPeerAuthProvider(secrets) :> IPeerAuthProvider

            let! token = issue auth callerId receiverId

            use host = buildReceiver auth
            host.Start()
            use client = host.GetTestClient()

            let! status, _ = postContract client (Some token) "{}"

            Expect.equal status 400 "the genuine token authenticated; the request then failed on its unparseable body"
        }

        testCaseAsync "the host backstops a provider that throws"
        <| async {
            // `IPeerAuthProvider` is contractually total, and the default
            // provider now is. The backstop exists so the host's
            // fail-closed posture does not depend on every present and
            // future implementation being exception-free.
            use host = buildReceiver (ThrowingAuthProvider() :> IPeerAuthProvider)
            host.Start()
            use client = host.GetTestClient()

            let! status, _ = postContract client (Some "any.token.here") "{}"

            Expect.equal
                status
                401
                "a provider that throws is still a refused credential at the wire — never a 500 disclosing that something unusual happened"
        }
    ]

// ─── (2) The capability-profile fetch does not downgrade ─────────────

/// A stub transport answering every request with one canned response, so
/// the fetch's status handling is exercised end to end rather than via
/// the pure decision alone.
type private StubHandler(status: HttpStatusCode, body: string) =
    inherit HttpMessageHandler()

    override _.SendAsync(_request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        let response = new HttpResponseMessage(status)
        response.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        Task.FromResult response

/// Mints a token without needing a secret store — the profile fetch's
/// own auth leg is not what these cases are about.
type private StubAuth() =
    interface IPeerAuthProvider with
        member _.IssuePeerToken(_, _, _) = async { return Ok "stub-token" }
        member _.ValidatePeerToken _ = async { return Error(PeerUnauthorized "not used") }
        member _.VerifyDelegation _ = async { return Ok() }

let private deprecationNotice: DeprecationNotice = {
    DeprecatedSince = v1
    RemovedIn = Some { Major = 2; Minor = 0 }
    Note = "use GetBalanceV2 instead"
}

/// The receiver's honest answer: `Balance` is deprecated at v1.
let private deprecatedProfile: PeerProfile = [
    {
        ContractId = contractId
        VersionProfiles = [
            {
                Version = v1
                Methods = [
                    {
                        Method = "Balance"
                        Status = Deprecated deprecationNotice
                    }
                ]
            }
        ]
    }
]

let private capabilityList: CapabilityList = [
    {
        ContractId = contractId
        Versions = [ v1 ]
    }
]

let private target: TargetPeer = {
    Peer = receiverId
    BaseUrl = "http://receiver.test"
}

/// Run the fetch against a canned response, reporting whether the legacy
/// capability-list leg was consulted — a fail-closed composition must not
/// issue that second request at all.
let private fetchProfile (fallback: PeerRemoteProfile.RemoteProfileFallback) (status: HttpStatusCode) (body: string) = async {
    let capabilityFetches = ref 0

    let fetchCapabilities (_: TargetPeer) = async {
        capabilityFetches.Value <- capabilityFetches.Value + 1
        return Ok capabilityList
    }

    // `HttpClient` owns and disposes the handler it is given.
    use http = new HttpClient(new StubHandler(status, body))

    let! result =
        PeerRemoteProfile.fetch http fallback fetchCapabilities (StubAuth() :> IPeerAuthProvider) callerId target

    return result, capabilityFetches.Value
}

let private methodStatusOf (profile: PeerProfile) =
    profile
    |> List.tryFind (fun c -> c.ContractId = contractId)
    |> Option.bind (fun c -> PeerCapabilityNegotiation.methodStatusAt c v1 "Balance")

let profileNoDowngradeTests =
    testList "Phase 343 — a non-2xx capability-profile fetch does not downgrade" [

        testCase "the non-success decision is fail-closed by default and permissive only on opt-in"
        <| fun _ ->
            for status in [ 400; 401; 403; 404; 500; 503 ] do
                Expect.isSome
                    (PeerRemoteProfile.nonSuccessOutcome PeerRemoteProfile.FailClosedProfile status)
                    $"HTTP {status} fails the handshake by default — including 404, which is NOT special-cased: 'the route is missing' is exactly what a masking response claims to be"

                Expect.isNone
                    (PeerRemoteProfile.nonSuccessOutcome PeerRemoteProfile.LegacyCapabilityFallback status)
                    $"HTTP {status} degrades only under the explicit opt-in"

        testCaseAsync "CONTROL — a 200 profile is returned intact, deprecation and all"
        <| async {
            let! result, capabilityFetches =
                fetchProfile PeerRemoteProfile.FailClosedProfile HttpStatusCode.OK (JsonRpc.serialize deprecatedProfile)

            match result with
            | Error e -> failtestf "expected the profile to be read, got %A" e
            | Ok profile ->
                Expect.equal
                    (methodStatusOf profile)
                    (Some(Deprecated deprecationNotice))
                    "the receiver's own lifecycle declaration crosses the wire intact — this is what the refusals below are protecting"

            Expect.equal capabilityFetches 0 "the happy path never touches the bare capability list"
        }

        testCaseAsync "a 500 fails the handshake instead of degrading"
        <| async {
            let! result, capabilityFetches =
                fetchProfile PeerRemoteProfile.FailClosedProfile HttpStatusCode.InternalServerError "boom"

            match result with
            | Ok profile ->
                failtestf
                    "a spoiled response yielded a usable profile (%A) — this is the masking vector: one status code and the caller stops asking about method lifecycle"
                    profile
            | Error(HandshakeRejected _) -> ()
            | Error e -> failtestf "expected HandshakeRejected naming the status, got %A" e

            Expect.equal
                capabilityFetches
                0
                "a fail-closed composition never issues the fallback request — the downgrade is not merely discarded, it is not attempted"
        }

        testCaseAsync "a 404 fails the handshake too — 'route missing' is not a trusted claim"
        <| async {
            let! result, _ = fetchProfile PeerRemoteProfile.FailClosedProfile HttpStatusCode.NotFound ""

            match result with
            | Error(HandshakeRejected _) -> ()
            | Ok profile -> failtestf "a 404 still produced a profile: %A" profile
            | Error e -> failtestf "expected HandshakeRejected, got %A" e
        }

        testCaseAsync
            "NEGATIVE CONTROL — the opt-in fallback loses the deprecation, which is what the default now refuses"
        <| async {
            // This case is the exposure, made falsifiable. The legacy leg
            // succeeds — so the refusals above are the policy talking, not
            // a broken transport — and what it returns has NO lifecycle
            // information at all, so `Balance` negotiates as though the
            // receiver had never declared it deprecated.
            let! result, capabilityFetches =
                fetchProfile PeerRemoteProfile.LegacyCapabilityFallback HttpStatusCode.InternalServerError "boom"

            match result with
            | Error e -> failtestf "the legacy fallback should still degrade successfully, got %A" e
            | Ok profile ->
                Expect.equal capabilityFetches 1 "the fallback consulted the bare capability list"

                Expect.isNone
                    (methodStatusOf profile)
                    "the degraded profile carries no method lifecycle — the Deprecated notice the receiver declared is simply gone"

                match PeerCapabilityNegotiation.negotiate profile profile contractId "Balance" with
                | Error(MethodNotAdvertised _) -> ()
                | Ok resolution ->
                    failtestf
                        "expected the deprecation to have been erased to MethodNotAdvertised, got a resolution of %A"
                        resolution.Status
                | Error e -> failtestf "unexpected negotiation error %A" e
        }
    ]

// ─── (3) The asymmetric provider ─────────────────────────────────────

let private newEcKeyPair () =
    use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
    ec.ExportPkcs8PrivateKeyPem(), ec.ExportSubjectPublicKeyInfoPem()

let private newRsaKeyPair (bits: int) =
    use rsa = RSA.Create(bits)
    rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem()

/// The two deployments' stores, built the way the topology actually
/// splits: the caller holds its own PRIVATE key and nothing else; the
/// receiver holds the caller's PUBLIC key and nothing else. That
/// asymmetry is the whole claim under test, so it is expressed in the
/// fixture rather than asserted about a shared store.
let private keyedPair (privatePem: string) (publicPem: string) =
    let callerStore = InMemorySecretStore() :> ISecretStore
    seed callerStore $"peers/{callerId.PeerId}/signing-private-key" privatePem

    let receiverStore = InMemorySecretStore() :> ISecretStore
    seed receiverStore $"peers/{callerId.PeerId}/signing-public-key" publicPem

    callerStore, receiverStore

let private headerOf (token: string) =
    Encoding.UTF8.GetString(Base64Url.decode ((token.Split '.')[0]))

let asymmetricProviderTests =
    testList "Phase 343 — asymmetric peer auth provider" [

        testCaseAsync "a receiver holding ONLY the public key validates the caller's token"
        <| async {
            let privatePem, publicPem = newEcKeyPair ()
            let callerStore, receiverStore = keyedPair privatePem publicPem

            let caller = AsymmetricPeerAuthProvider(callerStore, PeerEs256) :> IPeerAuthProvider

            let receiver =
                AsymmetricPeerAuthProvider(receiverStore, receiverId.PeerId, PeerEs256) :> IPeerAuthProvider

            let! token = issue caller callerId receiverId
            Expect.stringContains (headerOf token) "ES256" "the token names the asymmetric algorithm"

            let! result = receiver.ValidatePeerToken token
            expectAccepted "a token verified against a public key alone" result
        }

        testCaseAsync "…and that receiver cannot MINT as the caller — the blast radius is the whole point"
        <| async {
            // The symmetric provider's exposure stated as a test: there,
            // the receiver holds the same key it verifies with, so it can
            // forge the caller's tokens (and so can anyone who reads its
            // secret store). Here it holds no minting material at all.
            let privatePem, publicPem = newEcKeyPair ()
            let _, receiverStore = keyedPair privatePem publicPem

            let receiver =
                AsymmetricPeerAuthProvider(receiverStore, receiverId.PeerId, PeerEs256) :> IPeerAuthProvider

            match! receiver.IssuePeerToken(callerId, receiverId, Anonymous) with
            | Ok token ->
                failtestf
                    "the receiver minted a token as '%s' (%s) — it is holding signing material it must not have"
                    callerId.PeerId
                    token
            | Error(PeerUnauthorized _) -> ()
            | Error e -> failtestf "expected PeerUnauthorized, got %A" e
        }

        testCaseAsync "a token signed by a DIFFERENT key pair is refused"
        <| async {
            let privatePem, _ = newEcKeyPair ()
            let _, otherPublicPem = newEcKeyPair ()
            let callerStore, _ = keyedPair privatePem ""

            let receiverStore = InMemorySecretStore() :> ISecretStore
            seed receiverStore $"peers/{callerId.PeerId}/signing-public-key" otherPublicPem

            let caller = AsymmetricPeerAuthProvider(callerStore, PeerEs256) :> IPeerAuthProvider

            let receiver =
                AsymmetricPeerAuthProvider(receiverStore, receiverId.PeerId, PeerEs256) :> IPeerAuthProvider

            let! token = issue caller callerId receiverId
            let! result = receiver.ValidatePeerToken token

            expectRefused "an impostor's signature against the registered public key" result
        }

        testCaseAsync "a malformed base64url signature is refused, not thrown — on this path too"
        <| async {
            let privatePem, publicPem = newEcKeyPair ()
            let callerStore, receiverStore = keyedPair privatePem publicPem

            let caller = AsymmetricPeerAuthProvider(callerStore, PeerEs256) :> IPeerAuthProvider

            let receiver =
                AsymmetricPeerAuthProvider(receiverStore, receiverId.PeerId, PeerEs256) :> IPeerAuthProvider

            let! token = issue caller callerId receiverId

            let! result =
                receiver.ValidatePeerToken(withSignature malformedSignature token)
                |> Async.Catch

            match result with
            | Choice1Of2 r -> expectRefused "a malformed signature on the asymmetric path" r
            | Choice2Of2 ex ->
                failtestf "the asymmetric provider threw (%s) — same defect, new path" (ex.GetType().Name)
        }

        testCaseAsync "algorithm confusion is refused in BOTH directions"
        <| async {
            // An HS256 token presented to an ES256 receiver, and an ES256
            // token presented to the HS256 default. Each provider accepts
            // exactly one algorithm and never reads the header to decide
            // which verifier to use.
            let privatePem, publicPem = newEcKeyPair ()
            let callerStore, receiverStore = keyedPair privatePem publicPem
            seed receiverStore $"peers/{callerId.PeerId}/signing-key" hs256Key

            let symmetricStore = InMemorySecretStore() :> ISecretStore
            seed symmetricStore $"peers/{callerId.PeerId}/signing-key" hs256Key

            let symmetricCaller = JwtPeerAuthProvider(symmetricStore) :> IPeerAuthProvider

            let asymmetricCaller =
                AsymmetricPeerAuthProvider(callerStore, PeerEs256) :> IPeerAuthProvider

            let asymmetricReceiver =
                AsymmetricPeerAuthProvider(receiverStore, receiverId.PeerId, PeerEs256) :> IPeerAuthProvider

            let symmetricReceiver =
                JwtPeerAuthProvider(receiverStore, receiverId.PeerId) :> IPeerAuthProvider

            let! hsToken = issue symmetricCaller callerId receiverId
            let! esToken = issue asymmetricCaller callerId receiverId

            let! hsAtEs = asymmetricReceiver.ValidatePeerToken hsToken
            let! esAtHs = symmetricReceiver.ValidatePeerToken esToken

            expectRefused "an HS256 token at an ES256 receiver" hsAtEs
            expectRefused "an ES256 token at the HS256 default" esAtHs
        }

        testCaseAsync "RS256 round-trips on the same seam"
        <| async {
            let privatePem, publicPem = newRsaKeyPair 2048
            let callerStore, receiverStore = keyedPair privatePem publicPem

            let caller = AsymmetricPeerAuthProvider(callerStore, PeerRs256) :> IPeerAuthProvider

            let receiver =
                AsymmetricPeerAuthProvider(receiverStore, receiverId.PeerId, PeerRs256) :> IPeerAuthProvider

            let! token = issue caller callerId receiverId
            Expect.stringContains (headerOf token) "RS256" "the token names RS256"

            let! result = receiver.ValidatePeerToken token
            expectAccepted "an RS256 token verified against its public key" result
        }

        testCaseAsync "an under-strength RSA key fails closed rather than minting a weak signature"
        <| async {
            let privatePem, publicPem = newRsaKeyPair 1024
            let callerStore, _ = keyedPair privatePem publicPem

            let caller = AsymmetricPeerAuthProvider(callerStore, PeerRs256) :> IPeerAuthProvider

            match! caller.IssuePeerToken(callerId, receiverId, Anonymous) with
            | Ok token -> failtestf "a 1024-bit key minted a token (%s) — present-but-weak material must refuse" token
            | Error(PeerUnauthorized _) -> ()
            | Error e -> failtestf "expected PeerUnauthorized, got %A" e
        }

        testCaseAsync "audience binding and replay defence apply unchanged on the asymmetric path"
        <| async {
            // The shared validation tail asserted rather than assumed:
            // these are Phase 130 / Phase 338 behaviours, configured the
            // same way, and they must hold for a provider that did not
            // reimplement them.
            let privatePem, publicPem = newEcKeyPair ()
            let callerStore, receiverStore = keyedPair privatePem publicPem

            let caller = AsymmetricPeerAuthProvider(callerStore, PeerEs256) :> IPeerAuthProvider

            let policy =
                PeerTokenPolicy.unscoped
                |> PeerTokenPolicy.withReplayGuard (InMemoryPeerReplayGuard() :> IPeerReplayGuard)

            let receiver =
                AsymmetricPeerAuthProvider(receiverStore, receiverId.PeerId, PeerEs256, policy) :> IPeerAuthProvider

            // Audience: a token minted for a THIRD deployment.
            let! misaddressed =
                issue caller callerId {
                    PeerId = "elsewhere"
                    DisplayName = "Another Receiver"
                }

            let! audienceResult = receiver.ValidatePeerToken misaddressed
            expectRefused "a token minted for a different receiver" audienceResult

            // Replay: the correctly-addressed token, twice.
            let! token = issue caller callerId receiverId
            let! first = receiver.ValidatePeerToken token
            let! replay = receiver.ValidatePeerToken token

            expectAccepted "the token's single legitimate use" first
            expectRefused "the same token presented again" replay
        }

        testCaseAsync "an unknown peer is refused before its id reaches the key path"
        <| async {
            let privatePem, publicPem = newEcKeyPair ()
            let callerStore, _ = keyedPair privatePem publicPem

            // A receiver that trusts nobody.
            let receiver =
                AsymmetricPeerAuthProvider(InMemorySecretStore() :> ISecretStore, receiverId.PeerId, PeerEs256)
                :> IPeerAuthProvider

            let caller = AsymmetricPeerAuthProvider(callerStore, PeerEs256) :> IPeerAuthProvider

            let! token = issue caller callerId receiverId
            let! result = receiver.ValidatePeerToken token

            expectRefused "a peer with no registered public key" result
        }
    ]

// ─── The HS256 default is unchanged (GP 11) ──────────────────────────

let defaultCompositionUnchangedTests =
    testList "Phase 343 — the HS256 default composition is unchanged" [

        testCase "a fresh PeerServerApp does not opt into the legacy profile fallback"
        <| fun _ ->
            let fresh = PeerCompose.PeerServerApp.create ()

            let optedIn = fresh |> PeerCompose.PeerServerApp.withLegacyProfileFallback

            Expect.isFalse
                fresh.LegacyProfileFallback
                "the degrade is opt-in — a composition that never names it fails the handshake closed"

            Expect.isTrue optedIn.LegacyProfileFallback "…and naming it is what turns it back on"

        testCaseAsync "the symmetric provider still mints and validates an HS256 token end to end"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            seed secrets $"peers/{callerId.PeerId}/signing-key" hs256Key

            let provider = JwtPeerAuthProvider(secrets, receiverId.PeerId) :> IPeerAuthProvider

            let! token = issue provider callerId receiverId

            Expect.stringContains
                (headerOf token)
                "HS256"
                "the default algorithm is untouched by the asymmetric companion"

            let! result = provider.ValidatePeerToken token
            expectAccepted "the default round-trip" result
        }
    ]