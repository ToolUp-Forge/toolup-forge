# Peer JWT replay defence + call scoping

**Ships in:** ToolUp.InterPlatform (Phase 338).

## What changes

A minted peer token carried `iss` / `aud` / `name` / `uctx` / `iat` / `exp` /
`nbf` and nothing else. Nothing made it *single-use*, and nothing scoped it to
the contract it was minted for — so inside its 300 s lifetime plus 60 s skew, one
observed or intermediary-relayed token was a bearer capability over the
receiver's **whole** peer surface, replayable arbitrarily.
[Phase 130](130-pkce-and-peer-audience.md) constrains *which receiver* will accept
a token; this constrains *how often* and *against what*.

Three additive pieces:

```fsharp
// 1. A short-TTL seen-set over the token's `jti` nonce.
type IPeerReplayGuard =
    abstract ClaimTokenId: jti: string * expiresAt: DateTimeOffset -> Async<PeerReplayVerdict>
    abstract IsDistributed: bool          // mirrors IShareTokenRateLimiter

type PeerReplayVerdict =
    | ReplayFirstUse                      // claimed; proceed
    | ReplayDetected                      // already spent; refuse
    | ReplayGuardUnavailable of reason: string   // cannot tell; refuse (fail closed)

// 2. The receiver-side policy the provider reads.
type PeerTokenPolicy = {
    ReplayGuard: IPeerReplayGuard option  // None = no jti examined at all
    CallScope: PeerCallScope              // UnscopedCalls | ContractBoundCalls
}

// 3. The call-scoped auth surface, held alongside IPeerAuthProvider so no
//    existing implementer or call site is broken.
type IPeerCallScopedAuth =
    abstract IssueScopedPeerToken:
        caller: PeerIdentity * audience: PeerIdentity * user: UserContext * contractId: string ->
            Async<Result<string, PeerError>>
    abstract ValidateScopedPeerToken: token: string * contractId: string -> Async<Result<PeerPrincipal, PeerError>>
```

Two guards ship. `InMemoryPeerReplayGuard` is process-local
(`IsDistributed = false`) and bounded on both axes: expired nonces are pruned
lazily, and at capacity it **refuses** rather than growing — an unbounded
seen-set turns a burst into an out-of-memory kill of the whole process, whereas a
refusal is scoped to peer calls and legible in the rejection message.
`BlobPeerReplayGuard` is the distributed-ready one (`IsDistributed = true`): one
tiny blob per nonce written with `ETagCondition.IfAbsent`, so the backend's own
create-only atomicity *is* the "first claimant wins" rule, with no lock or lease
of its own. Claims are bucketed by expiry hour and stale buckets are reclaimed
lazily on the first claim landing in a new one — no hosted service, no scheduler
dependency (GP 13). It **refuses an `IBlobStorage` without
`IConditionalBlobStorage`** at construction: an `Exists`-then-`Upload` fallback
races exactly where a concurrent replay lands, and a guard that is racy under
load is worse than none, because it reads as defended.

**`jti` is minted unconditionally**, whether or not this deployment enforces
anything. A receiver ignores claims it does not know, so the extra claim is inert
against a pre-338 peer — and minting it always is what lets a fleet upgrade
first and switch enforcement on second, instead of needing both halves of every
peer pair to move in one step.

**The replay claim is the LAST check**, after signature, `exp`, `nbf` and `aud`.
Claiming earlier would let an unauthenticated attacker burn seen-set entries with
forged tokens, turning the defence into the denial-of-service it exists to
prevent.

## ⚠ This tightens auth: it can refuse calls that previously succeeded

Once you opt in, three previously-accepted shapes are refused:

| Shape | Refused because | Applies when |
|---|---|---|
| The same token presented twice | `jti` already spent | `ReplayGuard = Some _` |
| A token from a **pre-338 peer** (no `jti`) | cannot be de-duplicated, so it fails closed | `ReplayGuard = Some _` |
| A `cid`-bearing token on the unscoped `ValidatePeerToken` path | the receiver cannot see which contract the call is for, so it cannot honour the binding | `CallScope = ContractBoundCalls` |

**Nothing above happens by default.** `PeerTokenPolicy.unscoped` — what every
existing constructor supplies, and what `PeerCompose` still composes — consults
no store, examines no `jti` and no `cid`, and validates byte-for-byte as before
(GP 11). A deployment that never constructs a policy is unaffected and pays
nothing (GP 13).

**Rollout order, and it matters:** (1) upgrade every peer in the federation, so
all of them mint `jti`; (2) *then* switch on `ReplayGuard` at receivers. Doing it
the other way round refuses every call from a peer still on the old build. The
opt-out is the default value, so the "off switch" is simply not passing a policy.

## Diff to apply

**Nothing**, for a deployment composing the peer substrate via
`PeerServerApp.run`. The policy arrives as a constructor **overload**, not as an
optional argument (F# folds `?policy` into one widened constructor, which erases
the pre-338 signature and reads as a removal in the public-API baseline), so the
surface diff is purely additive at source *and* binary level:

```fsharp
// Unchanged — pre-338 behaviour, byte for byte
JwtPeerAuthProvider(secrets)
JwtPeerAuthProvider(secrets, localPeerId)          // Phase 130 audience binding

// Single-instance receiver: single-use tokens
JwtPeerAuthProvider(
    secrets,
    localPeerId,
    PeerTokenPolicy.unscoped
    |> PeerTokenPolicy.withReplayGuard (InMemoryPeerReplayGuard())
)

// Scaled receiver: the seen-set is shared, so a replay against ANY replica is
// caught. Requires a conditional-write-capable IBlobStorage.
JwtPeerAuthProvider(
    secrets,
    localPeerId,
    PeerTokenPolicy.unscoped
    |> PeerTokenPolicy.withReplayGuard (BlobPeerReplayGuard blobs)
    |> PeerTokenPolicy.withContractBinding
)
```

Nothing implements `IPeerReplayGuard` or `IPeerCallScopedAuth` outside this
commit, so there is no implementer sweep. A caller wanting call scoping type-tests
for the seam rather than assuming it:

```fsharp
match authProvider with
| :? IPeerCallScopedAuth as scoped -> scoped.ValidateScopedPeerToken(token, contractId)
| _ -> authProvider.ValidatePeerToken token
```

## Deferred

- **Compose-level wiring.** `PeerServerApp.withReplayGuard …` lands in
  `PeerCompose.fs`, outside this phase's declared key-file footprint and inside
  the serialised `PeerCompose` track. Until it ships, a deployment registers its
  own `IPeerAuthProvider` singleton constructed with the desired policy.
- **Host-side call scoping.** `JsonRpcPeerHost` still validates through the plain
  `ValidatePeerToken`, so it never supplies a contract id. That edit is in the
  same serialised track. Until it ships, `ContractBoundCalls` is usable by a
  deployment that validates through `IPeerCallScopedAuth` itself; a deployment
  on the stock host should leave `CallScope = UnscopedCalls`, because a bound
  token arriving at the stock host is refused (see the table above) — which is
  the fail-closed answer, not a silent downgrade.

## Consumer impact (SDK-adoption)

No consumer implements either new seam, and no consumer source change is needed:
the default policy is the pre-338 behaviour. `SDK-ADOPTION.md` is **generated**
from each consumer's own `sdk-adoption.json` — it is never hand-edited — and this
phase's row carries no adoption obligation beyond "opt in when you want
single-use peer tokens".

## Verification

- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 338"`
  — 22 cases: the guard seam (bounded growth, fail-closed at capacity, a second
  blob-guard instance seeing the first's claim, stale-bucket reclamation,
  construction refusal without conditional writes) and the provider behaviour
  over it (replay refused, nonce-less token refused, guard-down refused, the
  guard untouched by a bad-signature token, contract binding both ways).
- Each rejection case is paired with a **control** asserting the identical
  sequence *succeeds* with the defence removed — without that pair, "the second
  call failed" would pass just as happily against a validator that had broken and
  started refusing everything. Both defences were additionally demonstrated red
  before landing: neutering the nonce claim turns 5 cases red (controls stay
  green), neutering the `cid` comparison turns 1 red.
- `dotnet build ToolUp.Forge.sln` + `VerifyAll`.

## Rollback

Revert the Phase 338 commit. Tokens minted under it carry two extra claims
(`jti`, and `cid` only under `ContractBoundCalls`); a pre-338 receiver ignores
unknown claims, so in-flight tokens keep validating and no call is lost.
Blob-guard claims left behind under `_platform/peers/replay/` are inert and can
be deleted at leisure. Replay defence simply stops being enforced.
