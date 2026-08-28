module ToolUp.Platform.Tests.InProcess.PeerJwtReplayTests

open System
open System.Collections.Concurrent
open System.Text
open Expecto
open ToolUp.InterPlatform
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 338 — peer-token replay defence + call scoping ────────────
//
// A peer token used to be a bearer capability over the receiver's whole
// peer surface for its 300 s + 60 s-skew lifetime: nothing made it
// single-use, and nothing scoped it to a contract. This pack pins both
// halves of the fix — the `jti` seen-set and the `cid` binding — plus
// the two properties that are easy to get wrong in a security control
// and invisible if you only assert the happy rejection:
//
//   • **The rejection must come from the guard.** Every "replay is
//     rejected" case has a paired CONTROL that runs the identical
//     sequence with the defence removed and asserts it SUCCEEDS. Without
//     that pair, an assertion that a second call fails would pass just
//     as happily against a validator that had broken and started
//     refusing everything — proving nothing about replay at all.
//   • **Fail closed, never open.** A guard that cannot reach its state
//     refuses the call; it does not answer "not seen".
//
// The guard-level cases inject a clock and a capacity so the bounded-
// growth claims are exact rather than argued.

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

/// A guard whose backing store is down. The point of a distinct verdict
/// (rather than an exception or a `ReplayFirstUse`) is that the caller
/// can tell "fresh" from "cannot tell", and this double is how the
/// fail-closed branch is reached deterministically.
type private BrokenReplayGuard() =
    interface IPeerReplayGuard with
        member _.IsDistributed = true

        member _.ClaimTokenId(_jti, _expiresAt) = async { return ReplayGuardUnavailable "backing store unreachable" }

/// Counts claims so a test can assert the guard was NOT consulted for a
/// token that failed an earlier check — the claim-order property that
/// keeps an unauthenticated attacker from burning seen-set entries.
type private CountingReplayGuard(inner: IPeerReplayGuard) =
    let mutable claims = 0
    member _.Claims = claims

    interface IPeerReplayGuard with
        member _.IsDistributed = inner.IsDistributed

        member _.ClaimTokenId(jti, expiresAt) = async {
            claims <- claims + 1
            return! inner.ClaimTokenId(jti, expiresAt)
        }

/// Hand-cranked clock — the same shape the guards' `now` seam takes, so
/// a TTL / bucket horizon is crossed exactly rather than slept out.
type private TestClock(start: DateTimeOffset) =
    let mutable current = start
    member _.Now = current
    member _.Advance(delta: TimeSpan) = current <- current.Add delta
    member this.Read() : DateTimeOffset = this.Now

let private epoch = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)

let private callerId: PeerIdentity = {
    PeerId = "buyer"
    DisplayName = "Buyer Deployment"
}

let private receiverId: PeerIdentity = {
    PeerId = "seller"
    DisplayName = "Seller Deployment"
}

let private signingKey = "peer-replay-defence-shared-key-0123456789"

/// A secret store holding the caller's signing key at the exact location
/// `JwtPeerAuthProvider` reads on every issue / validate, so every case
/// below isolates the replay / scoping decision from the key decision.
let private secretsWithKey () =
    let store = InMemorySecretStore() :> ISecretStore

    store.SetSecret("_platform", $"peers/{callerId.PeerId}/signing-key", signingKey)
    |> Async.RunSynchronously
    |> ignore

    store

let private issue (provider: IPeerAuthProvider) = async {
    match! provider.IssuePeerToken(callerId, receiverId, Anonymous) with
    | Ok token -> return token
    | Error e -> return failtestf "Expected a minted token, got %A" e
}

let private expectAccepted (label: string) (result: Result<PeerPrincipal, PeerError>) =
    match result with
    | Ok principal -> Expect.equal principal.Caller.PeerId callerId.PeerId label
    | Error e -> failtestf "%s — expected acceptance, got %A" label e

let private expectRefused (label: string) (result: Result<PeerPrincipal, PeerError>) =
    match result with
    | Error(PeerUnauthorized _) -> ()
    | Error e -> failtestf "%s — expected PeerUnauthorized, got %A" label e
    | Ok p -> failtestf "%s — expected refusal, but the call was admitted as %s" label p.Caller.PeerId

let private base64UrlRaw (bytes: byte[]) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

/// Mint a raw HS256 token with full control over which claims are
/// present — specifically, one with NO `jti`, which is exactly the shape
/// a pre-338 peer still emits.
let private mintRawToken (claims: string) =
    let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    let header = """{"alg":"HS256","typ":"JWT"}"""

    let payload =
        sprintf
            """{"iss":"%s","aud":"%s","name":"%s",%s"iat":%d,"exp":%d,"nbf":%d}"""
            callerId.PeerId
            receiverId.PeerId
            callerId.DisplayName
            claims
            now
            (now + 300L)
            now

    let h = base64UrlRaw (Encoding.UTF8.GetBytes header)
    let p = base64UrlRaw (Encoding.UTF8.GetBytes payload)
    let signingInput = $"{h}.{p}"

    use hmac = new Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes signingKey)

    let signature = base64UrlRaw (hmac.ComputeHash(Encoding.UTF8.GetBytes signingInput))
    $"{signingInput}.{signature}"

/// The decoded payload of a minted token, for asserting which claims it
/// actually carries.
let private payloadOf (token: string) =
    let parts = token.Split '.'
    Encoding.UTF8.GetString(Base64Url.decode parts[1])

// ─── The replay-store seam ───────────────────────────────────────────

let guardTests =
    testList "Phase 338 — IPeerReplayGuard" [

        testCaseAsync "the in-memory guard admits a nonce once and refuses it thereafter"
        <| async {
            let guard = InMemoryPeerReplayGuard() :> IPeerReplayGuard
            let deadline = DateTimeOffset.UtcNow.AddMinutes 6.0

            let! first = guard.ClaimTokenId("nonce-a", deadline)
            let! second = guard.ClaimTokenId("nonce-a", deadline)
            let! other = guard.ClaimTokenId("nonce-b", deadline)

            Expect.equal first ReplayFirstUse "the first claim of a nonce wins"
            Expect.equal second ReplayDetected "the second claim of the same nonce is a replay"
            Expect.equal other ReplayFirstUse "a distinct nonce is unaffected"
        }

        testCase "the in-memory guard declares itself non-distributed"
        <| fun _ ->
            let guard = InMemoryPeerReplayGuard() :> IPeerReplayGuard

            Expect.isFalse
                guard.IsDistributed
                "a per-process seen-set must SAY so — a replay against another replica is not detected, and a deployment can only weigh that if the guarantee is data (GP 12)"

        testCaseAsync "the in-memory guard reclaims expired nonces rather than growing without bound"
        <| async {
            // Capacity 4, then claim well past it with nonces whose tokens
            // have already expired by the time of the next claim. An
            // unbounded dictionary would end at 12 entries.
            let clock = TestClock(epoch)
            let guard = InMemoryPeerReplayGuard(4, clock.Read)
            let seam = guard :> IPeerReplayGuard

            for i in 1..12 do
                let! verdict = seam.ClaimTokenId($"nonce-{i}", clock.Now.AddMinutes 6.0)
                Expect.equal verdict ReplayFirstUse $"claim {i} is a fresh nonce"
                clock.Advance(TimeSpan.FromMinutes 7.0)

            Expect.isLessThanOrEqual
                guard.Count
                4
                "the seen-set stays inside its capacity — expired nonces are pruned, which is what makes the store bounded rather than a slow-motion leak"
        }

        testCaseAsync "the in-memory guard fails CLOSED when it cannot reclaim its way back under capacity"
        <| async {
            let clock = TestClock(epoch)
            let guard = InMemoryPeerReplayGuard(2, clock.Read) :> IPeerReplayGuard
            let live = clock.Now.AddMinutes 6.0

            let! a = guard.ClaimTokenId("nonce-a", live)
            let! b = guard.ClaimTokenId("nonce-b", live)
            let! overflow = guard.ClaimTokenId("nonce-c", live)

            Expect.equal a ReplayFirstUse "first live nonce"
            Expect.equal b ReplayFirstUse "second live nonce"

            match overflow with
            | ReplayGuardUnavailable _ -> ()
            | v ->
                failtestf
                    "Expected ReplayGuardUnavailable at capacity with nothing reclaimable, got %A — answering 'fresh' here would silently drop the defence under exactly the load it exists for"
                    v
        }

        testCaseAsync "the blob guard admits a nonce once, fleet-wide"
        <| async {
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let guard = BlobPeerReplayGuard(blobs) :> IPeerReplayGuard
            let deadline = DateTimeOffset.UtcNow.AddMinutes 6.0

            let! first = guard.ClaimTokenId("nonce-a", deadline)
            let! second = guard.ClaimTokenId("nonce-a", deadline)

            Expect.equal first ReplayFirstUse "the conditional IfAbsent write is the claim"

            Expect.equal
                second
                ReplayDetected
                "the second IfAbsent write is refused, and that refusal IS the replay verdict"

            Expect.isTrue
                guard.IsDistributed
                "the blob guard's claim is atomic in the backend, so it holds across replicas"
        }

        testCaseAsync "a SECOND blob-guard instance sees the first instance's claim"
        <| async {
            // The whole point of the distributed guard: two receiver
            // replicas share the backing store and therefore the seen-set.
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let replicaOne = BlobPeerReplayGuard(blobs) :> IPeerReplayGuard
            let replicaTwo = BlobPeerReplayGuard(blobs) :> IPeerReplayGuard
            let deadline = DateTimeOffset.UtcNow.AddMinutes 6.0

            let! first = replicaOne.ClaimTokenId("nonce-a", deadline)
            let! replayed = replicaTwo.ClaimTokenId("nonce-a", deadline)

            Expect.equal first ReplayFirstUse "replica one claims the nonce"

            Expect.equal
                replayed
                ReplayDetected
                "replica two refuses the same nonce — this is the failure the in-memory guard cannot catch"
        }

        testCaseAsync "the blob guard reclaims stale expiry buckets"
        <| async {
            let clock = TestClock(epoch)
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let guard = BlobPeerReplayGuard(blobs, clock.Read) :> IPeerReplayGuard

            let! _ = guard.ClaimTokenId("old-nonce", clock.Now.AddMinutes 6.0)
            let! before = blobs.List("_platform", "peers/replay/")
            Expect.equal before.Length 1 "the claim is persisted"

            // Well past the retain horizon: the next claim lands in a new
            // bucket and sweeps everything older than two hours.
            clock.Advance(TimeSpan.FromHours 5.0)
            let! _ = guard.ClaimTokenId("new-nonce", clock.Now.AddMinutes 6.0)

            let! after = blobs.List("_platform", "peers/replay/")

            Expect.equal
                after.Length
                1
                "only the live bucket survives — a nonce whose token expired hours ago cannot be replayed, so retaining it is pure growth"
        }

        testCase "the blob guard refuses a backend without conditional writes"
        <| fun _ ->
            // An Exists-then-Upload fallback races exactly where a
            // concurrent replay lands, so there is no safe degraded mode
            // — the seam says so at construction rather than at the first
            // peer call.
            let plain =
                { new IBlobStorage with
                    // Phase 741 — no bounded multi-part commit primitive here; callers assemble through memory.
                    member _.CanComposeFrom = false

                    member _.ComposeFrom(_, _, _) =
                        ToolUp.Platform.BlobStorage.composeNotSupported "test double"

                    member _.Upload(_, _, _) = async { return Ok "" }
                    member _.Download(_, _) = async { return Error "absent" }
                    member _.Delete(_, _) = async { return Ok() }
                    member _.List(_, _) = async { return [] }
                    member _.Exists(_, _) = async { return false }
                    member _.GetMetadata(_, _) = async { return Error "absent" }
                    member _.DownloadRange(_, _, _, _) = async { return Error "absent" }

                    member _.Erase(_, _, _, _) = async {
                        return
                            Ok {
                                HandlerName = "blobs"
                                RecordsAffected = 0
                                Note = None
                            }
                    }
                }

            Expect.isNone
                (BlobPeerReplayGuard.TryCreate plain)
                "TryCreate is the probing form — a compose path can fall back to the in-memory guard deliberately"

            Expect.throws
                (fun () -> BlobPeerReplayGuard plain |> ignore)
                "the direct constructor fails loudly at compose time, not silently at the first claim"
    ]

// ─── Replay defence through the provider ─────────────────────────────

let replayDefenceTests =
    testList "Phase 338 — JwtPeerAuthProvider replay defence" [

        testCaseAsync "a minted token carries a jti nonce"
        <| async {
            let provider =
                JwtPeerAuthProvider(secretsWithKey (), "", PeerTokenPolicy.unscoped) :> IPeerAuthProvider

            let! token = issue provider

            Expect.stringContains
                (payloadOf token)
                "\"jti\""
                "the nonce is minted unconditionally — a fleet upgrades first and switches enforcement on second, rather than needing both halves of every peer pair to move at once"
        }

        testCaseAsync "a captured token replayed inside the freshness window is refused"
        <| async {
            let policy =
                PeerTokenPolicy.unscoped
                |> PeerTokenPolicy.withReplayGuard (InMemoryPeerReplayGuard() :> IPeerReplayGuard)

            let provider =
                JwtPeerAuthProvider(secretsWithKey (), receiverId.PeerId, policy) :> IPeerAuthProvider

            let! token = issue provider

            let! first = provider.ValidatePeerToken token
            let! replay = provider.ValidatePeerToken token

            expectAccepted "the token's first, legitimate use" first
            expectRefused "the same token presented a second time" replay
        }

        testCaseAsync "CONTROL — the same replay SUCCEEDS with the guard removed"
        <| async {
            // The negative control for the case above. Run the identical
            // sequence on a provider whose ONLY difference is the absent
            // guard: if this also refused, the assertion above would be
            // measuring something other than replay defence.
            let provider =
                JwtPeerAuthProvider(secretsWithKey (), receiverId.PeerId, PeerTokenPolicy.unscoped) :> IPeerAuthProvider

            let! token = issue provider

            let! first = provider.ValidatePeerToken token
            let! second = provider.ValidatePeerToken token

            expectAccepted "first use, guard absent" first

            expectAccepted
                "second use, guard absent — this is the pre-338 hole, and its persistence here is what proves the refusal above came from the guard"
                second
        }

        testCaseAsync "two distinct tokens from the same peer are both accepted"
        <| async {
            let policy =
                PeerTokenPolicy.unscoped
                |> PeerTokenPolicy.withReplayGuard (InMemoryPeerReplayGuard() :> IPeerReplayGuard)

            let provider =
                JwtPeerAuthProvider(secretsWithKey (), receiverId.PeerId, policy) :> IPeerAuthProvider

            let! tokenA = issue provider
            let! tokenB = issue provider

            Expect.notEqual tokenA tokenB "each mint gets its own nonce"

            let! a = provider.ValidatePeerToken tokenA
            let! b = provider.ValidatePeerToken tokenB

            expectAccepted "the first call" a
            expectAccepted "an ordinary second call on its own token — single-use must not mean one-call-per-peer" b
        }

        testCaseAsync "a token with no jti is refused once replay defence is on"
        <| async {
            // Exactly what a pre-338 peer still emits. It cannot be
            // de-duplicated, so it is refused rather than waved through —
            // which is why the migration doc's rollout order is upgrade
            // the fleet, THEN enforce.
            let policy =
                PeerTokenPolicy.unscoped
                |> PeerTokenPolicy.withReplayGuard (InMemoryPeerReplayGuard() :> IPeerReplayGuard)

            let provider =
                JwtPeerAuthProvider(secretsWithKey (), receiverId.PeerId, policy) :> IPeerAuthProvider

            let legacyToken = mintRawToken ""

            let! result = provider.ValidatePeerToken legacyToken
            expectRefused "a pre-338 nonce-less token under an enforcing receiver" result
        }

        testCaseAsync "CONTROL — the same nonce-less token is accepted with the guard removed"
        <| async {
            let provider =
                JwtPeerAuthProvider(secretsWithKey (), receiverId.PeerId, PeerTokenPolicy.unscoped) :> IPeerAuthProvider

            let! result = provider.ValidatePeerToken(mintRawToken "")

            expectAccepted
                "a pre-338 token against a non-enforcing receiver — an unconfigured deployment is byte-for-byte unchanged (GP 11)"
                result
        }

        testCaseAsync "an unreachable replay store refuses the call rather than admitting it"
        <| async {
            let policy =
                PeerTokenPolicy.unscoped
                |> PeerTokenPolicy.withReplayGuard (BrokenReplayGuard() :> IPeerReplayGuard)

            let provider =
                JwtPeerAuthProvider(secretsWithKey (), receiverId.PeerId, policy) :> IPeerAuthProvider

            let! token = issue provider
            let! result = provider.ValidatePeerToken token

            expectRefused
                "a perfectly valid, first-use token whose guard is down — fail closed: 'cannot tell' is not 'fresh'"
                result
        }

        testCaseAsync "the guard is not consulted for a token that fails an earlier check"
        <| async {
            // Claim order is load-bearing: claiming before the signature
            // verifies would let an unauthenticated attacker burn seen-set
            // entries with forged tokens, turning the defence into the
            // denial-of-service it exists to prevent.
            let counting = CountingReplayGuard(InMemoryPeerReplayGuard() :> IPeerReplayGuard)

            let policy =
                PeerTokenPolicy.unscoped
                |> PeerTokenPolicy.withReplayGuard (counting :> IPeerReplayGuard)

            let provider =
                JwtPeerAuthProvider(secretsWithKey (), receiverId.PeerId, policy) :> IPeerAuthProvider

            let! valid = issue provider
            let parts = valid.Split '.'
            let badSignature = base64UrlRaw (Encoding.UTF8.GetBytes "not-the-signature")
            let forged = $"{parts[0]}.{parts[1]}.{badSignature}"

            let! result = provider.ValidatePeerToken forged
            expectRefused "a token with a broken signature" result

            Expect.equal
                counting.Claims
                0
                "an unauthenticated token must not reach the seen-set at all, or forging tokens becomes a way to exhaust it"

            // …and the genuine token still works afterwards: the forgery
            // did not consume its nonce.
            let! genuine = provider.ValidatePeerToken valid
            expectAccepted "the real token, after the forgery attempt" genuine
            Expect.equal counting.Claims 1 "exactly one claim, for the one token that passed every other check"
        }
    ]

// ─── Call scoping (contract binding) ─────────────────────────────────

let callScopingTests =
    let boundProvider () =
        JwtPeerAuthProvider(
            secretsWithKey (),
            receiverId.PeerId,
            PeerTokenPolicy.unscoped |> PeerTokenPolicy.withContractBinding
        )

    let unscopedProvider () =
        JwtPeerAuthProvider(secretsWithKey (), receiverId.PeerId, PeerTokenPolicy.unscoped)

    testList "Phase 338 — JwtPeerAuthProvider call scoping" [

        testCaseAsync "a contract-bound token is accepted against the contract it names"
        <| async {
            let provider = boundProvider () :> IPeerCallScopedAuth

            match! provider.IssueScopedPeerToken(callerId, receiverId, Anonymous, "directory") with
            | Error e -> failtestf "Expected a minted token, got %A" e
            | Ok token ->
                Expect.stringContains (payloadOf token) "\"cid\"" "the binding rides the token as a claim"

                let! result = provider.ValidateScopedPeerToken(token, "directory")
                expectAccepted "the contract the token was minted for" result
        }

        testCaseAsync "a contract-bound token is refused against a DIFFERENT contract"
        <| async {
            let provider = boundProvider () :> IPeerCallScopedAuth

            match! provider.IssueScopedPeerToken(callerId, receiverId, Anonymous, "directory") with
            | Error e -> failtestf "Expected a minted token, got %A" e
            | Ok token ->
                let! result = provider.ValidateScopedPeerToken(token, "ledger")

                expectRefused
                    "a token minted for 'directory' spent against 'ledger' — this is the reach half of the threat, distinct from the replay half"
                    result
        }

        testCaseAsync "CONTROL — the same cross-contract use SUCCEEDS under the default unscoped policy"
        <| async {
            // Binding is opt-in, and the default must be indistinguishable
            // from pre-338 (GP 11). If this refused, the case above would
            // be proving that scoped validation rejects rather than that
            // binding does.
            let provider = unscopedProvider () :> IPeerCallScopedAuth

            match! provider.IssueScopedPeerToken(callerId, receiverId, Anonymous, "directory") with
            | Error e -> failtestf "Expected a minted token, got %A" e
            | Ok token ->
                Expect.isFalse
                    ((payloadOf token).Contains "\"cid\"")
                    "the unscoped policy mints no binding claim at all"

                let! result = provider.ValidateScopedPeerToken(token, "ledger")
                expectAccepted "cross-contract use under the default policy" result
        }

        testCaseAsync "a contract-bound token is refused on the unscoped validation path"
        <| async {
            // The receiver cannot see which contract the call is for, so it
            // cannot honour the binding — and accepting anyway would make
            // the `cid` claim decorative.
            let provider = boundProvider ()

            match!
                (provider :> IPeerCallScopedAuth).IssueScopedPeerToken(callerId, receiverId, Anonymous, "directory")
            with
            | Error e -> failtestf "Expected a minted token, got %A" e
            | Ok token ->
                let! result = (provider :> IPeerAuthProvider).ValidatePeerToken token
                expectRefused "a bound token on a path that cannot check the binding" result
        }

        testCaseAsync "an unbound token still validates under a binding receiver"
        <| async {
            // Nothing claimed a scope, so nothing is being ignored — the
            // other checks stand and a mixed fleet can roll forward.
            let provider = boundProvider ()
            let! token = issue (provider :> IPeerAuthProvider)

            let! result = (provider :> IPeerAuthProvider).ValidatePeerToken token
            expectAccepted "an ordinary IssuePeerToken token under ContractBoundCalls" result
        }

        testCaseAsync "replay defence and contract binding compose"
        <| async {
            let policy =
                PeerTokenPolicy.unscoped
                |> PeerTokenPolicy.withReplayGuard (InMemoryPeerReplayGuard() :> IPeerReplayGuard)
                |> PeerTokenPolicy.withContractBinding

            let provider =
                JwtPeerAuthProvider(secretsWithKey (), receiverId.PeerId, policy) :> IPeerCallScopedAuth

            match! provider.IssueScopedPeerToken(callerId, receiverId, Anonymous, "directory") with
            | Error e -> failtestf "Expected a minted token, got %A" e
            | Ok token ->
                let! first = provider.ValidateScopedPeerToken(token, "directory")
                let! replay = provider.ValidateScopedPeerToken(token, "directory")

                expectAccepted "the single legitimate use" first
                expectRefused "the same bound token replayed against its own contract" replay
        }
    ]