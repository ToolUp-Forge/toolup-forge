module ToolUp.Platform.Tests.InProcess.PeerDelegationVerificationTests

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
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

// ─── Phase 330 — delegation assertions are verified before dispatch ──
//
// `ValidatePeerToken` authenticates the CALLING PEER. The end-user
// identity it returns rode inside that peer's own signed payload, so the
// outer signature proves who sent the assertion and nothing about
// whether it is true. On the `Delegated` case the caller is asserting "I
// am acting for user U, and peer P authorised me to" — the classic
// confused-deputy shape — and the only thing separating a genuine
// buyer→broker→seller delegation from an invented one is
// `DelegatedAssertion.Signature`, checked against the delegating peer's
// own trust anchor by `IPeerAuthProvider.VerifyDelegation`.
//
// Two seams are covered:
//
//   * the HOST seam (`JsonRpcPeerHost` contract dispatch) — where
//     `VerifyDelegation` is now actually called, over a real TestServer
//     so the refusal is observed at the HTTP layer and against a
//     recording `IPlatformPeer` that proves dispatch did not happen;
//   * the PROVIDER seam (`JwtPeerAuthProvider.ValidatePeerToken`) —
//     where a malformed `uctx` is now an explicit rejection rather than
//     a silent downgrade to `Anonymous`.
//
// **Every rejection is paired with a control** asserting the identical
// sequence SUCCEEDS when it legitimately should — otherwise "the call
// failed" would pass just as happily against a host that had broken and
// started refusing everything. The exposure itself is pinned by a
// NEGATIVE CONTROL that ADMITS the forged assertion when the delegation
// check is a no-op (the pre-330 posture), which is what makes the gap
// falsifiable rather than argued.

// ─── Fixtures ────────────────────────────────────────────────────────

/// The contract the seller hosts. NOT `private`: the host reflects via
/// `FSharpType.IsRecord` without the private-representation flag, so a
/// `private` record reads back as a non-record and
/// `JsonRpcPeerHost.contract` rejects it.
type LedgerContract = { Balance: unit -> Async<int> }

let private ledgerImpl: LedgerContract = {
    Balance = fun () -> async { return 4200 }
}

let private brokerId: PeerIdentity = {
    PeerId = "broker"
    DisplayName = "Broker Deployment"
}

let private sellerId: PeerIdentity = {
    PeerId = "seller"
    DisplayName = "Seller Deployment"
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }

let private contractId = "ledger"

/// The end user the honest delegation is for, and the one a forged
/// assertion tries to impersonate.
let private honestSubject = "analyst@origin"

let private impersonatedSubject = "admin@victim"

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

/// Seed a peer's symmetric HS256 signing key at the exact reserved
/// location `JwtPeerAuthProvider` reads on every issue / validate /
/// delegation-verify.
let private seedSigningKey (store: ISecretStore) (peerId: string) (key: string) =
    store.SetSecret("_platform", $"peers/{peerId}/signing-key", key)
    |> Async.RunSynchronously
    |> ignore

/// `IPlatformPeer` decorator retaining every context it was asked to
/// dispatch. This is the seam the assertions read: a refused call must
/// leave it EMPTY (dispatch never happened), and an accepted one must
/// carry the delegated `Subject` as the trusted call context's user.
type private RecordingPlatformPeer(inner: IPlatformPeer) =
    let seen = ConcurrentBag<PeerCallContext>()
    member _.Dispatched = seen |> List.ofSeq

    interface IPlatformPeer with
        member _.RegisterContract registration = inner.RegisterContract registration

        member _.Handle(contract, context, methodName, arguments) =
            seen.Add context
            inner.Handle(contract, context, methodName, arguments)

        member _.Capabilities() = inner.Capabilities()

/// `IPeerAuthProvider` decorator that counts `VerifyDelegation` calls and
/// can neuter the check. `admitEverything = true` is the PRE-330 posture
/// expressed as a fixture: the host calls the member, and the member
/// says yes to anything — exactly what a receiver that never verifies
/// behaves like.
type private ProbeAuthProvider(inner: IPeerAuthProvider, admitEverything: bool) =
    let mutable verifyCalls = 0
    member _.VerifyCalls = verifyCalls

    interface IPeerAuthProvider with
        member _.IssuePeerToken(caller, audience, user) =
            inner.IssuePeerToken(caller, audience, user)

        member _.ValidatePeerToken token = inner.ValidatePeerToken token

        member _.VerifyDelegation assertion = async {
            System.Threading.Interlocked.Increment(&verifyCalls) |> ignore

            if admitEverything then
                return Ok()
            else
                return! inner.VerifyDelegation assertion
        }

/// A `TestServer`-hosted receiver mounting the real
/// `JsonRpcPeerHost.routes`, with the given auth provider and a
/// recording peer registered as DI singletons.
let private buildReceiver (auth: IPeerAuthProvider) (peer: IPlatformPeer) : IHost =
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

/// Mint the delegation signature the way the delegating peer would:
/// HMAC-SHA256 over the canonical `{Subject}|{chain}` byte string, keyed
/// on the delegating (LAST-in-chain) peer's signing key, base64url. This
/// mirrors `PeerJwt.canonicalAssertion` + the verify path; it is spelled
/// out here rather than reused so a silent change to the canonical form
/// on either side shows up as a failing test.
let private signAssertion (delegatingKey: string) (subject: string) (chain: string list) : DelegatedAssertion =
    let canonical = $"""{subject}|{String.concat ">" chain}"""
    use hmac = new HMACSHA256(Encoding.UTF8.GetBytes delegatingKey)

    {
        Subject = subject
        DelegationChain = chain
        Signature = Base64Url.encode (hmac.ComputeHash(Encoding.UTF8.GetBytes canonical))
    }

/// A proxy config that calls the receiver over the TestServer client,
/// propagating `user` as the call's end-user identity (which is what
/// `HttpPeerClient` mints into the token's `uctx` claim).
let private proxyConfig (client: IPeerClient) (user: UserContext) : PeerProxyConfig = {
    Client = client
    Target = {
        Peer = sellerId
        BaseUrl = "http://localhost"
    }
    Caller = brokerId
    User = user
    Version = v1
    ContractId = contractId
    HopBudget = 8
}

/// A peer holds ONE symmetric key (`peers/{id}/signing-key`), used both
/// to sign its bearer tokens and to sign any delegation it issues — so
/// these are the two trust anchors the receiver stores, not four.
let private brokerKey = "broker-signing-key-0123456789abcdefghijkl"

let private originKey = "origin-signing-key-0123456789abcdefghijkl"

/// The whole two-deployment scenario in one call: the broker (the
/// immediate caller) invokes `Balance` on the seller, asserting `user`.
/// Returns the call outcome, the contexts the receiver dispatched, and
/// how many times the delegation check was consulted.
///
/// The receiver holds the correct trust anchor for BOTH `broker` and
/// `origin`, so the outer bearer token always validates and the only
/// thing any case below varies is the delegation leg.
let private callAs (admitEverything: bool) (user: UserContext) = async {
    let brokerSecrets = InMemorySecretStore() :> ISecretStore
    seedSigningKey brokerSecrets brokerId.PeerId brokerKey

    let sellerSecrets = InMemorySecretStore() :> ISecretStore
    seedSigningKey sellerSecrets brokerId.PeerId brokerKey
    seedSigningKey sellerSecrets "origin" originKey

    let brokerAuth = JwtPeerAuthProvider(brokerSecrets) :> IPeerAuthProvider

    let sellerAuth =
        ProbeAuthProvider(JwtPeerAuthProvider(sellerSecrets), admitEverything)

    let receiver = RecordingPlatformPeer(DefaultPlatformPeer())

    let ledgerHost =
        JsonRpcPeerHost.contract<LedgerContract> contractId [ v1 ] None ledgerImpl

    (receiver :> IPlatformPeer).RegisterContract ledgerHost.Registration

    let host = buildReceiver sellerAuth receiver
    host.Start()
    use testClient = host.GetTestClient()

    let transport = HttpPeerClient(testClient, brokerAuth, brokerId) :> IPeerClient
    let proxy = JsonRpcPeerClient.create<LedgerContract> (proxyConfig transport user)

    let! outcome = proxy.Balance() |> Async.Catch
    return outcome, receiver.Dispatched, sellerAuth.VerifyCalls
}

let private expectRejected (label: string) (outcome: Choice<int, exn>) =
    match outcome with
    | Choice2Of2(PeerInvocationException(PeerUnauthorized _)) -> ()
    | Choice2Of2 ex -> failtestf "%s: expected PeerInvocationException(PeerUnauthorized …), got %A" label ex
    | Choice1Of2 value -> failtestf "%s: expected rejection, but the call returned %d" label value

let private expectAccepted (label: string) (outcome: Choice<int, exn>) =
    match outcome with
    | Choice1Of2 value -> Expect.equal value 4200 $"%s{label}: the contract's own result crosses the wire intact"
    | Choice2Of2 ex -> failtestf "%s: expected the call to succeed, got %A" label ex

// ─── Host seam ───────────────────────────────────────────────────────

let hostVerificationTests =
    testList "JsonRpcPeerHost — delegation assertion verification (Phase 330)" [

        // ─── (1) The exposure: a forged assertion is refused … ────────

        testCaseAsync "a Delegated assertion whose chain signature does not verify is rejected before dispatch"
        <| async {
            // A valid outer token (the broker really is the broker) that
            // asserts an originator it invented: right shape, wrong
            // signature. This is the whole confused-deputy vector.
            let forged = {
                Subject = impersonatedSubject
                DelegationChain = [ "origin" ]
                Signature = "not-a-real-delegation-signature"
            }

            let! outcome, dispatched, verifyCalls = callAs false (Delegated forged)

            expectRejected "forged delegation" outcome
            Expect.equal verifyCalls 1 "the host consulted VerifyDelegation exactly once"

            Expect.isEmpty
                dispatched
                "a refused delegation never reaches IPlatformPeer.Handle — the receiver acts on nothing"
        }

        // ─── (2) … the CONTROL: the same sequence, correctly signed ───

        testCaseAsync "a correctly-signed single-hop delegation is accepted and its Subject drives the call context"
        <| async {
            let honest = signAssertion originKey honestSubject [ "origin" ]
            let! outcome, dispatched, verifyCalls = callAs false (Delegated honest)

            expectAccepted "signed single-hop delegation" outcome
            Expect.equal verifyCalls 1 "the host consulted VerifyDelegation exactly once"
            Expect.hasLength dispatched 1 "the verified call reaches dispatch"

            match (List.head dispatched).User with
            | Delegated a ->
                Expect.equal a.Subject honestSubject "the delegated originator becomes the trusted context principal"
                Expect.equal a.DelegationChain [ "origin" ] "the verified chain rides into the call context intact"
            | other -> failtestf "expected a Delegated principal in the trusted context, got %A" other
        }

        // ─── (3) Multi-hop: signed by the LAST peer in the chain ──────

        testCaseAsync "a correctly-signed multi-hop delegation is accepted (signed by the last peer in the chain)"
        <| async {
            // origin → broker → seller. The immediate delegating peer is
            // the broker (last in the chain), so the receiver checks the
            // signature against the BROKER's trust anchor, not origin's.
            let chain = [ "origin"; "broker" ]
            let honest = signAssertion brokerKey honestSubject chain
            let! outcome, dispatched, _ = callAs false (Delegated honest)

            expectAccepted "signed multi-hop delegation" outcome
            Expect.hasLength dispatched 1 "the verified multi-hop call reaches dispatch"

            match (List.head dispatched).User with
            | Delegated a -> Expect.equal a.DelegationChain chain "the full ordered chain survives into the context"
            | other -> failtestf "expected a Delegated principal, got %A" other
        }

        testCaseAsync "a multi-hop assertion signed by the WRONG chain member is rejected"
        <| async {
            // Signed by origin (first in the chain) rather than the broker
            // (last / immediate delegator) — a chain the receiver must not
            // accept, and the control above proves the same shape passes
            // when it IS signed by the right member.
            let chain = [ "origin"; "broker" ]
            let misSigned = signAssertion originKey honestSubject chain
            let! outcome, dispatched, _ = callAs false (Delegated misSigned)

            expectRejected "delegation signed by the wrong chain member" outcome
            Expect.isEmpty dispatched "a mis-signed multi-hop delegation never reaches dispatch"
        }

        // ─── (4) An empty chain has no delegator to verify against ────

        testCaseAsync "a Delegated assertion with an empty chain is rejected"
        <| async {
            let chainless = {
                Subject = impersonatedSubject
                DelegationChain = []
                Signature = "irrelevant"
            }

            let! outcome, dispatched, _ = callAs false (Delegated chainless)

            expectRejected "empty delegation chain" outcome
            Expect.isEmpty dispatched "an empty-chain delegation never reaches dispatch"
        }

        // ─── (5) NEGATIVE CONTROL: the exposure, made falsifiable ─────

        testCaseAsync "NEGATIVE CONTROL — with the delegation check neutered, the forged assertion IS admitted"
        <| async {
            // The pre-330 posture, expressed as a fixture: the receiver
            // still authenticates the caller, but the delegation leg says
            // yes to anything. If this case ever went red the same way the
            // first one goes green, the refusal above would be coming from
            // somewhere other than the delegation check — a validator that
            // had broken and started refusing everything would satisfy
            // case (1) just as well.
            let forged = {
                Subject = impersonatedSubject
                DelegationChain = [ "origin" ]
                Signature = "not-a-real-delegation-signature"
            }

            let! outcome, dispatched, _ = callAs true (Delegated forged)

            expectAccepted "neutered delegation check" outcome
            Expect.hasLength dispatched 1 "with no verification the forged call reaches dispatch"

            match (List.head dispatched).User with
            | Delegated a ->
                Expect.equal
                    a.Subject
                    impersonatedSubject
                    "and the receiver acts as the impersonated originator — precisely the exposure Phase 330 closes"
            | other -> failtestf "expected a Delegated principal, got %A" other
        }

        // ─── (6) Non-delegating paths are byte-for-byte unchanged ─────

        testCaseAsync "a Direct principal dispatches unchanged and never consults VerifyDelegation"
        <| async {
            let direct =
                Direct {
                    Subject = honestSubject
                    Issuer = brokerId.PeerId
                    DisplayName = Some "Analyst"
                }

            let! outcome, dispatched, verifyCalls = callAs false direct

            expectAccepted "Direct principal" outcome

            Expect.equal
                verifyCalls
                0
                "a Direct assertion carries no signature to verify — the provider is not consulted"

            Expect.hasLength dispatched 1 "the Direct path reaches dispatch exactly as before"

            match (List.head dispatched).User with
            | Direct a -> Expect.equal a.Subject honestSubject "the Direct subject rides into the call context"
            | other -> failtestf "expected a Direct principal, got %A" other
        }

        testCaseAsync "an Anonymous call dispatches unchanged and never consults VerifyDelegation"
        <| async {
            let! outcome, dispatched, verifyCalls = callAs false Anonymous

            expectAccepted "Anonymous call" outcome
            Expect.equal verifyCalls 0 "the deployment-to-deployment path pays nothing for the delegation check"
            Expect.hasLength dispatched 1 "the Anonymous path reaches dispatch exactly as before"
        }
    ]

// ─── Provider seam — malformed `uctx` rejects, never degrades ────────
//
// These drive `ValidatePeerToken` directly with hand-rolled tokens, so
// each claim shape is controlled exactly — including a `uctx` that is
// not a JSON string at all, and one absent entirely, neither of which
// `IssuePeerToken` ever mints.

/// Mint a raw HS256 token with an arbitrary `uctx` claim fragment.
/// `uctxFragment` is spliced into the payload verbatim (already
/// including its trailing comma) so a test can emit a non-string claim,
/// an unparseable string, or nothing at all.
let private mintRawToken (signingKey: string) (issuer: string) (uctxFragment: string) =
    let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    let header = """{"alg":"HS256","typ":"JWT"}"""

    let payload =
        $"""{{"iss":"{issuer}",{uctxFragment}"name":"{issuer}","iat":{now},"exp":{now + 300L},"nbf":{now}}}"""

    let h = Base64Url.encode (Encoding.UTF8.GetBytes header)
    let p = Base64Url.encode (Encoding.UTF8.GetBytes payload)
    let signingInput = $"{h}.{p}"
    use hmac = new HMACSHA256(Encoding.UTF8.GetBytes signingKey)

    let signature =
        Base64Url.encode (hmac.ComputeHash(Encoding.UTF8.GetBytes signingInput))

    $"{signingInput}.{signature}"

/// The `uctx` claim as `IssuePeerToken` writes it: the serialised
/// `UserContext` carried as a JSON *string*, so the JSON must be escaped
/// when embedded in the outer payload.
let private uctxClaimFor (user: UserContext) =
    let escaped = (JsonRpc.serialize user).Replace("\\", "\\\\").Replace("\"", "\\\"")
    $"\"uctx\":\"{escaped}\","

let userContextClaimTests =
    let signingKey = "uctx-claim-shared-signing-key-0123456789"
    let issuer = brokerId.PeerId

    let receiver () =
        let s = InMemorySecretStore() :> ISecretStore
        seedSigningKey s issuer signingKey
        JwtPeerAuthProvider(s) :> IPeerAuthProvider

    testList "JwtPeerAuthProvider — malformed uctx rejects rather than degrades (Phase 330)" [

        // ─── The tightening ──────────────────────────────────────────

        testCaseAsync "a uctx claim that does not deserialise is rejected, not degraded to Anonymous"
        <| async {
            let token = mintRawToken signingKey issuer "\"uctx\":\"{not valid json at all\","

            match! (receiver ()).ValidatePeerToken token with
            | Error(PeerUnauthorized _) -> ()
            | Error e -> failtestf "expected PeerUnauthorized, got %A" e
            | Ok p -> failtestf "expected rejection — a tampered uctx was accepted as %A" p.User
        }

        testCaseAsync "a uctx claim that is not a JSON string is rejected"
        <| async {
            // The other malformed shape: a structurally valid token whose
            // `uctx` is an object. The pre-330 claim reader required a
            // string and silently answered Anonymous for anything else.
            let token = mintRawToken signingKey issuer "\"uctx\":{\"Case\":\"Delegated\"},"

            match! (receiver ()).ValidatePeerToken token with
            | Error(PeerUnauthorized _) -> ()
            | Error e -> failtestf "expected PeerUnauthorized, got %A" e
            | Ok p -> failtestf "expected rejection — a non-string uctx was accepted as %A" p.User
        }

        // ─── CONTROLS: the shapes that must still be accepted ────────

        testCaseAsync "CONTROL — a well-formed Delegated uctx on the same token shape validates"
        <| async {
            let assertion = {
                Subject = honestSubject
                DelegationChain = [ "origin" ]
                Signature = "signature-checked-at-the-host-seam-not-here"
            }

            let token = mintRawToken signingKey issuer (uctxClaimFor (Delegated assertion))

            match! (receiver ()).ValidatePeerToken token with
            | Ok p ->
                match p.User with
                | Delegated a ->
                    Expect.equal a.Subject honestSubject "the asserted (still unverified) originator round-trips"
                | other -> failtestf "expected the Delegated assertion back, got %A" other
            | Error e -> failtestf "expected acceptance of a well-formed uctx, got %A" e
        }

        testCaseAsync "CONTROL — a token with NO uctx claim is still Anonymous (GP 11)"
        <| async {
            // Nothing was asserted, so nothing is being ignored: the plain
            // deployment-to-deployment token keeps its pre-330 behaviour.
            let token = mintRawToken signingKey issuer ""

            match! (receiver ()).ValidatePeerToken token with
            | Ok p -> Expect.equal p.User Anonymous "an absent uctx stays Anonymous"
            | Error e -> failtestf "expected acceptance of a uctx-less token, got %A" e
        }

        testCaseAsync "CONTROL — a well-formed Direct uctx validates unchanged"
        <| async {
            let direct =
                Direct {
                    Subject = honestSubject
                    Issuer = issuer
                    DisplayName = None
                }

            let token = mintRawToken signingKey issuer (uctxClaimFor direct)

            match! (receiver ()).ValidatePeerToken token with
            | Ok p -> Expect.equal p.User direct "the Direct assertion round-trips byte-for-byte"
            | Error e -> failtestf "expected acceptance, got %A" e
        }
    ]