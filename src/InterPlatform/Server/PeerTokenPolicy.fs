// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Collections.Concurrent
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 338 — peer-token replay defence + call scoping ────────────
//
// A minted peer token carries `iss` / `aud` / `name` / `uctx` / `iat` /
// `exp` / `nbf`. Nothing in that set makes a token *single-use*, and
// nothing scopes it to the contract it was minted for — so within the
// 300 s lifetime plus 60 s skew, one observed or intermediary-relayed
// token is a bearer capability over the receiver's whole peer surface,
// replayable arbitrarily. Phase 130 (audience binding) limits *which
// receiver* will accept it; this file limits *how often* and *against
// what*.
//
// Two orthogonal seams, both off by default (GP 11):
//
//   • `IPeerReplayGuard` — a short-TTL seen-set over the token's `jti`
//     nonce. The receiver claims the `jti` after the signature, `exp`,
//     `nbf` and `aud` all check out; a second claim of the same nonce
//     inside the freshness window is a replay and the call is refused.
//   • `PeerCallScope` — the optional `cid` claim binding a token to one
//     contract id, minted and checked through `IPeerCallScopedAuth`.
//
// **The guard is a store, not a cache, and it fails CLOSED.** A cache
// that answers "not seen" when it cannot reach its backing state would
// silently downgrade the receiver to the pre-338 posture at exactly the
// moment an attacker is hammering it. `ReplayGuardUnavailable` is
// therefore a distinct verdict from `ReplayFirstUse`, and
// `JwtPeerAuthProvider` maps it to `PeerUnauthorized` — a peer call is
// refused rather than admitted unchecked.
//
// **Bounded growth is part of the contract, not an implementation
// detail.** A seen-set that only ever grows is a slow-motion outage: the
// in-memory default prunes expired nonces and refuses (rather than
// grows) past its capacity, and the blob-backed one buckets claims by
// expiry hour and reclaims stale buckets lazily. A `jti` is only useful
// to an attacker for as long as the token around it is still valid, so
// nothing needs retaining past `exp + skew`.
//
// **Six portability rules (GP 12), for `IPeerReplayGuard`:**
//   1. Identity by value       — `jti` is a string, `expiresAt` a
//                                `DateTimeOffset`. No live handles.
//   2. Async at the boundary   — `ClaimTokenId` returns `Async<_>`.
//   3. Retry / supervision as
//      data                    — the outcome is a `PeerReplayVerdict`
//                                value; no callback, no thrown
//                                exception on the refusal path.
//   4. Stateless between calls — every claim round-trips the backing
//                                store. The blob guard's one piece of
//                                in-process state is a sweep memo that
//                                only suppresses redundant housekeeping
//                                (see below); correctness never depends
//                                on it.
//   5. No cross-shard ordering — claims partition by `jti`; two claims
//                                for different nonces may resolve in
//                                any order.
//   6. Precision at the lower
//      bound                   — expiry is second-precision, matching
//                                the JWT `exp` claim it is derived from.

/// The outcome of claiming one token's `jti` against the replay store.
type PeerReplayVerdict =
    /// The nonce had not been seen inside its freshness window and is
    /// now claimed. The call may proceed.
    | ReplayFirstUse
    /// The nonce is already claimed — this token has been presented
    /// before. Refuse the call.
    | ReplayDetected
    /// The backing store could not answer. Callers MUST treat this as a
    /// refusal: a guard that cannot see its state has no basis for
    /// saying a token is fresh.
    | ReplayGuardUnavailable of reason: string

/// Short-TTL seen-set over peer-token `jti` nonces. Implementations MUST
/// make `ClaimTokenId` atomic with respect to concurrent claims of the
/// same nonce — the whole defence is "first claimant wins", and a
/// read-then-write that is not atomic hands two racing replays the same
/// `ReplayFirstUse`.
///
/// Deliberately not folded into `IPeerAuthProvider`: replay state is the
/// one genuinely *shared* thing in an otherwise stateless validation
/// path, so it is the piece a scaled receiver must be able to replace
/// independently (GP 12 rule 4). The in-tree defaults are
/// `InMemoryPeerReplayGuard` (single instance) and
/// `BlobPeerReplayGuard` (any `IConditionalBlobStorage` backend).
type IPeerReplayGuard =
    /// Claim `jti` until `expiresAt` — the instant the token around it
    /// stops being accepted anyway (`exp` plus the validator's clock
    /// skew), after which the entry carries no information and may be
    /// reclaimed. Atomic per nonce.
    abstract ClaimTokenId: jti: string * expiresAt: DateTimeOffset -> Async<PeerReplayVerdict>

    /// Whether the seen-set is shared across replicas, declared as data
    /// rather than inferred by runtime type (GP 12) — mirroring
    /// `IShareTokenRateLimiter.IsDistributed`. `false` ⇒ each replica
    /// keeps its own set, so a token replayed against a *different*
    /// replica is not detected and the defence is only as strong as the
    /// load balancer's affinity. `true` ⇒ the claim is fleet-wide.
    abstract IsDistributed: bool

/// Process-local `IPeerReplayGuard`. Correct and allocation-cheap for a
/// single-instance receiver; **not** distributed — a replayed token that
/// lands on another replica sees a fresh set (`IsDistributed = false`).
///
/// Growth is bounded on both axes. Expired nonces are pruned lazily
/// whenever the set reaches `capacity`, which is enough on its own for
/// any real traffic shape: a peer token lives ~6 minutes, so the steady
/// state is "nonces minted in the last six minutes". `capacity` is the
/// backstop for the case pruning cannot fix — genuinely more live
/// nonces than the deployment budgeted for — and there the guard
/// **refuses** rather than growing. That is a deliberate trade: an
/// unbounded set converts a burst into an out-of-memory kill of the
/// whole process, whereas a refusal is scoped to peer calls, is
/// reported as `ReplayGuardUnavailable` (so it is legible in the
/// rejection message rather than mysterious), and is tunable by raising
/// `capacity` or moving to `BlobPeerReplayGuard`.
type InMemoryPeerReplayGuard(capacity: int, now: unit -> DateTimeOffset) =
    // Documented mutable-state exception to GP 5: a seen-set IS state,
    // and it is the only state in the validation path. Concurrent by
    // construction — `TryAdd` is the atomic first-claimant-wins primitive
    // rule 1's atomicity requirement asks for.
    let seen = ConcurrentDictionary<string, DateTimeOffset>()

    /// Drop every nonce whose token has already expired. Safe to run
    /// concurrently with claims: removing an entry can only ever admit a
    /// token that would be refused by `exp` a moment later anyway.
    let pruneExpired (cutoff: DateTimeOffset) =
        for entry in seen do
            if entry.Value <= cutoff then
                seen.TryRemove entry.Key |> ignore

    /// The default ceiling on live nonces. A peer token is valid for
    /// 300 s + 60 s skew, so this is ~275 peer calls/second sustained
    /// before the backstop is reached — far above what the in-process
    /// guard's single-instance deployment shape implies, and raisable.
    static member DefaultCapacity = 100_000

    /// Live nonce count. Exposed so a deployment (or a test) can assert
    /// the set is actually bounded rather than trusting that it is.
    member _.Count = seen.Count

    new() = InMemoryPeerReplayGuard(InMemoryPeerReplayGuard.DefaultCapacity, (fun () -> DateTimeOffset.UtcNow))

    new(capacity: int) = InMemoryPeerReplayGuard(capacity, (fun () -> DateTimeOffset.UtcNow))

    interface IPeerReplayGuard with
        member _.IsDistributed = false

        member _.ClaimTokenId(jti: string, expiresAt: DateTimeOffset) = async {
            let nowUtc = now ()

            if seen.Count >= capacity then
                pruneExpired nowUtc

            if seen.Count >= capacity then
                return
                    ReplayGuardUnavailable
                        $"in-memory replay guard is at its {capacity}-nonce capacity with no expired entries to reclaim"
            elif seen.TryAdd(jti, expiresAt) then
                return ReplayFirstUse
            else
                return ReplayDetected
        }

/// `IConditionalBlobStorage`-backed `IPeerReplayGuard` — the
/// distributed-ready default (`IsDistributed = true`). One tiny blob per
/// claimed nonce, written with `ETagCondition.IfAbsent`: the backend's
/// own create-only atomicity is what makes "first claimant wins" hold
/// across replicas, so the guard needs no lock, no lease and no
/// coordination of its own.
///
/// **Conditional writes are a hard requirement, checked at construction.**
/// A plain `Exists`-then-`Upload` has a read-modify-write race exactly
/// wide enough to admit the concurrent replay this exists to stop, and a
/// guard that is racy under load is worse than none — it reads as
/// defended. The constructor therefore refuses an `IBlobStorage` without
/// `IConditionalBlobStorage`, loudly and at compose time rather than at
/// the first peer call (the same posture the companion-authoring guide
/// asks of a native dependency).
///
/// **Bounded growth.** Claims are keyed
/// `peers/replay/{yyyyMMddHH}/{jti}.json`, bucketed by the *expiry*
/// hour. A nonce whose token expired hours ago cannot be replayed —
/// `checkExpiry` rejects the token before the guard is ever consulted —
/// so whole buckets older than the retain horizon are reclaimed
/// wholesale. The sweep is lazy (no hosted service, no scheduler
/// dependency — GP 13), triggered on the first claim landing in a new
/// bucket, and its failure is swallowed: housekeeping that cannot run is
/// a storage-growth problem, and turning it into a refused peer call
/// would trade a slow leak for an outage.
type BlobPeerReplayGuard(blobs: IBlobStorage, now: unit -> DateTimeOffset) =
    let container = "_platform"
    let prefix = "peers/replay/"

    let cas =
        match box blobs with
        | :? IConditionalBlobStorage as c -> c
        | _ ->
            invalidArg
                "blobs"
                "BlobPeerReplayGuard requires an IBlobStorage that also implements IConditionalBlobStorage (conditional writes). The atomic IfAbsent claim is the entire replay defence — an Exists-then-Upload fallback would race exactly where a concurrent replay lands. Use InMemoryPeerReplayGuard for a single-instance receiver, or a backend with conditional-write support."

    /// Buckets are `yyyyMMddHH` so ordinal string comparison is
    /// chronological — the sweep needs no date parsing to decide what is
    /// stale.
    let bucketOf (t: DateTimeOffset) =
        t.ToUniversalTime().ToString "yyyyMMddHH"

    let blobNameFor (bucket: string) (jti: string) = $"{prefix}{bucket}/{jti}.json"

    /// How far back buckets are retained. A peer token's whole freshness
    /// window is ~6 minutes; two hours is several orders of magnitude of
    /// slack over any clock disagreement between replicas.
    let retainHours = 2.0

    // Documented mutable-state exception to GP 5, and to GP 12 rule 4's
    // spirit: this memo suppresses a redundant sweep, nothing more. Every
    // claim's correctness is decided entirely by the conditional write;
    // an instance that has just started (memo empty) sweeps once more than
    // strictly necessary, and two instances racing both sweep — the sweep
    // is idempotent, so neither costs anything but a List call.
    let mutable lastSweptBucket = ""

    let sweepStale (nowUtc: DateTimeOffset) = async {
        try
            let horizon = bucketOf (nowUtc.AddHours(-retainHours))
            let! names = blobs.List(container, prefix)

            let stale =
                names
                |> List.filter (fun name ->
                    if not (name.StartsWith(prefix, StringComparison.Ordinal)) then
                        false
                    else
                        let rest = name.Substring prefix.Length

                        match rest.IndexOf '/' with
                        | -1 -> false
                        | i -> String.CompareOrdinal(rest.Substring(0, i), horizon) < 0)

            for name in stale do
                let! _ = blobs.Delete(container, name)
                ()
        with _ ->
            // Housekeeping only — see the type doc. A claim must not fail
            // because the sweep could not enumerate the container.
            ()
    }

    new(blobs: IBlobStorage) = BlobPeerReplayGuard(blobs, (fun () -> DateTimeOffset.UtcNow))

    /// Build the guard when `blobs` supports conditional writes, `None`
    /// otherwise — the probing form, for a compose path that wants to
    /// fall back to `InMemoryPeerReplayGuard` rather than fail.
    static member TryCreate(blobs: IBlobStorage) : IPeerReplayGuard option =
        match box blobs with
        | :? IConditionalBlobStorage -> Some(BlobPeerReplayGuard blobs :> IPeerReplayGuard)
        | _ -> None

    interface IPeerReplayGuard with
        member _.IsDistributed = true

        member _.ClaimTokenId(jti: string, expiresAt: DateTimeOffset) = async {
            let nowUtc = now ()
            let bucket = bucketOf expiresAt

            if bucket <> lastSweptBucket then
                lastSweptBucket <- bucket
                do! sweepStale nowUtc

            // The body is the expiry, so an operator reading the store can
            // tell a live claim from one awaiting its bucket sweep. Nothing
            // reads it back: the verdict is decided by the conditional
            // write alone.
            let payload = Encoding.UTF8.GetBytes(expiresAt.ToString "O")
            let! claimed = cas.UploadWithETag(container, blobNameFor bucket jti, payload, IfAbsent)

            match claimed with
            | Ok _ -> return ReplayFirstUse
            | Error(ETagMismatch _) ->
                // The blob already exists — this nonce was claimed by an
                // earlier presentation of the same token, on this replica
                // or another.
                return ReplayDetected
            | Error(ConditionalWriteFailure message) -> return ReplayGuardUnavailable message
        }

/// Phase 338 — how strictly a receiver ties a peer token to the contract
/// it is being spent against.
type PeerCallScope =
    /// Pre-338 behaviour: no `cid` claim is minted, and none is examined.
    /// The default (GP 11).
    | UnscopedCalls
    /// Tokens minted through `IPeerCallScopedAuth.IssueScopedPeerToken`
    /// carry a `cid` claim naming their target contract and are only
    /// accepted when validated against that same contract. A token
    /// carrying a `cid` is refused on the *unscoped* validation path —
    /// a receiver that cannot see which contract a call is for has no
    /// way to honour the binding, and silently ignoring it would make
    /// the claim decorative.
    | ContractBoundCalls

/// The receiver-side policy `JwtPeerAuthProvider` applies on top of
/// signature / `exp` / `nbf` / `aud`. Its default value
/// (`PeerTokenPolicy.unscoped`) is exactly the pre-338 behaviour, so a
/// deployment that never constructs one is byte-for-byte unchanged
/// (GP 11) and pays nothing (GP 13).
type PeerTokenPolicy = {
    /// The replay seen-set. `None` — the default — means no `jti` is
    /// examined and no store is consulted; tokens still *carry* a `jti`,
    /// which is what lets a fleet upgrade before any receiver starts
    /// enforcing (see the migration doc).
    ReplayGuard: IPeerReplayGuard option
    /// Contract binding. `UnscopedCalls` by default.
    CallScope: PeerCallScope
}

[<RequireQualifiedAccess>]
module PeerTokenPolicy =
    /// No replay guard, no contract binding — the pre-338 posture.
    let unscoped: PeerTokenPolicy = {
        ReplayGuard = None
        CallScope = UnscopedCalls
    }

    /// Enforce single-use tokens against `guard`.
    let withReplayGuard (guard: IPeerReplayGuard) (policy: PeerTokenPolicy) : PeerTokenPolicy = {
        policy with
            ReplayGuard = Some guard
    }

    /// Bind tokens to the contract they are minted for. Both peers must
    /// be on the scoped seam before this is switched on — see the
    /// migration doc's rollout order.
    let withContractBinding (policy: PeerTokenPolicy) : PeerTokenPolicy = {
        policy with
            CallScope = ContractBoundCalls
    }

/// Phase 338 — the call-scoped half of the peer auth surface, kept off
/// `IPeerAuthProvider` deliberately. Adding a contract id to
/// `IssuePeerToken` / `ValidatePeerToken` would be a compile break at
/// every implementer and every call site (GP 11); a provider that
/// supports scoping implements this alongside, and a caller that wants
/// scoping type-tests for it —
///
///     match authProvider with
///     | :? IPeerCallScopedAuth as scoped -> // bind to the contract id
///     | _ -> // unscoped provider: the pre-338 path
///
/// Under `UnscopedCalls` both members behave exactly like their
/// unscoped counterparts, so a caller can hold this seam unconditionally
/// and let the policy decide whether binding is in force.
type IPeerCallScopedAuth =
    /// Mint a token that may only be spent against `contractId`.
    abstract IssueScopedPeerToken:
        caller: PeerIdentity * audience: PeerIdentity * user: UserContext * contractId: string ->
            Async<Result<string, PeerError>>

    /// Authenticate an inbound token *for* `contractId`. Everything
    /// `ValidatePeerToken` checks, plus the contract binding.
    abstract ValidateScopedPeerToken: token: string * contractId: string -> Async<Result<PeerPrincipal, PeerError>>