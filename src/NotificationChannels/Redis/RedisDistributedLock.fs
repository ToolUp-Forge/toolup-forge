module ToolUp.Platform.NotificationChannels.RedisDistributedLock

open System
open StackExchange.Redis
open ToolUp.Platform

// ─── Phase 9i — cross-instance IDistributedLock over Redis ───────────
//
// The reference distributed implementation of the SDK's lease primitive,
// following the pattern `RedisNotificationChannel` (Phase 1g) and
// `RedisLifecycleLock` (Phase 54h) established: the same
// `IConnectionMultiplexer` the deployment already owns backs the lock, so
// no new connection is opened and the three substrates share one
// connection pool.
//
// **The contract is "single-Redis lease", NOT Redlock — say so out loud.**
// Every operation here targets ONE Redis (or one primary of one
// replicated pair). That buys mutual exclusion exactly as strong as that
// Redis's availability and no stronger:
//
//   * a primary failing over to a replica that has not yet received the
//     lock key hands the same id to a second holder;
//   * an unfenced write from a holder whose lease lapsed during a GC
//     pause or a paused VM still lands, unless the downstream store
//     checks `FenceToken`.
//
// Redlock's multi-master quorum addresses the first, at the cost of N
// independent Redis deployments and a correctness argument that is itself
// contested. That trade is deliberately NOT taken here: for the
// subsystems this seam serves — job-dispatch de-duplication,
// admin-blob write serialisation — a lease occasionally handed out twice
// during a failover degrades to the behaviour they had *before* any lock
// existed, while `FenceToken` gives a store-side path to real safety for
// anything that needs it. A deployment needing quorum semantics
// implements `IDistributedLock` over its own consensus store (etcd /
// ZooKeeper / Consul) — which is the point of the seam, and is cleanly
// out of scope for this phase.
//
// **Acquire** is `INCR fence` then `SET key fence NX PX ttl`: one atomic
// conditional set that takes the id's key only if absent, with a
// millisecond expiry. Success ⇒ the caller holds it; failure ⇒ another
// instance does, so `TryAcquire` returns `None` (fail-fast, the
// contract's semantic).
//
// **The stored token IS the fence token, which is what keeps this
// implementation stateless (GP 12 rule 4).** `INCR` never returns the
// same value twice for a key, so the fence alone uniquely identifies one
// acquisition of one lock id — no per-instance nonce needs remembering,
// and therefore a lease minted by one process can be renewed or released
// by *any* process holding the record. An earlier draft kept a
// `ConcurrentDictionary` of nonces keyed by lease; that made `Release`
// silently no-op for a lease handed across a process boundary, which is
// exactly the identity-by-value promise the seam makes.
//
// The fence counter key deliberately carries NO TTL. If it expired,
// tokens would restart at 1 and a stale holder's higher token would
// outrank a live one — the exact inversion fencing exists to prevent. One
// small key per lock id that ever existed is worth that; a deployment
// minting unbounded lock ids expires them deliberately, knowing a reused
// id restarts its token sequence.
//
// **Release / Renew** are compare-and-act Lua scripts against the stored
// fence, so neither can touch a lease that already lapsed and was
// re-taken: release deletes only our fence, renew re-`PEXPIRE`s only our
// fence.
//
// **Six-rule portability (GP 12):** identity by value (`lockId` string,
// `Lease` record — nothing Redis-shaped crosses the seam); async at every
// boundary; TTL as data, contention as `None`, no callbacks; stateless
// between calls (every operation round-trips and carries its whole state,
// so any instance observes any other's hold); per-`lockId` keying with no
// cross-id ordering; the TTL honoured at Redis's millisecond `PX`
// resolution — the declared precision floor.

/// Compare-and-delete: delete the lock key only if it still holds our
/// fence. `KEYS[1]` the lock key, `ARGV[1]` our fence token. Returns 1 on
/// delete, 0 when the key is absent or held by a later token.
[<Literal>]
let private releaseScript =
    "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end"

/// Compare-and-extend: re-`PEXPIRE` the lock key only if it still holds
/// our fence. `KEYS[1]` the lock key, `ARGV[1]` our fence token, `ARGV[2]`
/// the new TTL in milliseconds. Returns 1 on extend, 0 when the key is
/// absent or held by a later token — which is how `Renew` knows to hand
/// the caller its lease back unchanged.
[<Literal>]
let private renewScript =
    "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('pexpire', KEYS[1], ARGV[2]) else return 0 end"

/// Per-id lock key. Namespaced under `toolup:distributed-lock:` so it
/// cannot collide with the notification channels
/// (`toolup:notifications:`), the lifecycle lock
/// (`toolup:lifecycle-lock:`), or another application sharing the Redis
/// instance.
let private lockKey (lockId: string) : RedisKey =
    RedisKey.op_Implicit (sprintf "toolup:distributed-lock:%s" lockId)

/// Per-id fence counter key. Separate from the lock key because it must
/// OUTLIVE the lease — see the header note on why it carries no TTL.
let private fenceKey (lockId: string) : RedisKey =
    RedisKey.op_Implicit (sprintf "toolup:distributed-lock-fence:%s" lockId)

/// The stored token for a fence value. Decimal text, so a stuck lock is
/// readable with a bare `GET` in `redis-cli`.
let private tokenFor (fence: int64) : RedisValue = RedisValue.op_Implicit (string fence)

/// A Lua compare-and-act script returns 1 on success, 0 when the compare
/// failed (key absent or held by a later token).
let private succeeded (result: RedisResult) =
    not result.IsNull && result.ToString() = "1"

/// Phase 9i — cross-instance `IDistributedLock` over Redis `SET NX PX` +
/// `INCR` fence tokens + compare-and-act Lua release/renew. Construct
/// from the multiplexer the deployment already owns (the same one backing
/// `RedisNotificationChannel`).
type RedisDistributedLock(multiplexer: IConnectionMultiplexer, logger: ILogger option) =

    let db = multiplexer.GetDatabase()

    let warn (message: string) =
        match logger with
        | Some l -> l.Warn message
        | None -> ()

    interface IDistributedLock with
        member _.TryAcquire(lockId: string, ttl: TimeSpan) : Async<Lease option> = async {
            // Fence FIRST, unconditionally. Bumping on a losing acquire
            // wastes one token, and the contract requires tokens to be
            // strictly increasing, not gapless. The other order — set,
            // then fence — would let two instances that both acquired the
            // id across a failover receive the SAME token, which is the
            // one thing a fence token must never do.
            let! fence = db.StringIncrementAsync(fenceKey lockId) |> Async.AwaitTask

            let acquiredAt = DateTime.UtcNow

            let! acquired =
                db.StringSetAsync(lockKey lockId, tokenFor fence, Nullable ttl, When.NotExists)
                |> Async.AwaitTask

            if not acquired then
                // Another instance holds the id — fail fast.
                return None
            else
                return
                    Some {
                        LockId = lockId
                        FenceToken = fence
                        AcquiredAt = acquiredAt
                        ExpiresAt = acquiredAt + ttl
                    }
        }

        member _.Renew(lease: Lease) : Async<Lease> = async {
            let ttl = Lease.originalTtl lease
            let renewedAt = DateTime.UtcNow

            try
                let! result =
                    db.ScriptEvaluateAsync(
                        renewScript,
                        [| lockKey lease.LockId |],
                        [|
                            tokenFor lease.FenceToken
                            RedisValue.op_Implicit (int64 ttl.TotalMilliseconds)
                        |]
                    )
                    |> Async.AwaitTask

                if succeeded result then
                    return {
                        lease with
                            ExpiresAt = renewedAt + ttl
                    }
                else
                    // Key gone or held by a later token — the hold is
                    // genuinely lost. Unchanged lease ⇒ the caller's
                    // `Lease.isLive` check fails and it stops working.
                    return lease
            with ex ->
                // A transient Redis fault is not evidence the hold was
                // lost, but it is not evidence it was renewed either.
                // Returning the lease unchanged makes `Lease.isLive` the
                // arbiter, which fails safe: the caller stops before the
                // un-extended TTL rather than after it.
                warn
                    $"RedisDistributedLock: renew of {lease.LockId} (fence {lease.FenceToken}) failed ({ex.Message}); treating the lease as not renewed"

                return lease
        }

        member _.Release(lease: Lease) : Async<unit> = async {
            try
                // Compare-and-delete, so a lease that lapsed and was
                // re-taken by a later fence is never released out from
                // under its new holder. A no-op result (0) is the
                // contract's idempotent case — releasing twice, or
                // releasing a lost lease, is silent.
                do!
                    db.ScriptEvaluateAsync(releaseScript, [| lockKey lease.LockId |], [| tokenFor lease.FenceToken |])
                    |> Async.AwaitTask
                    |> Async.Ignore
            with ex ->
                // Best-effort: a Redis outage at release costs only the
                // lease's remaining TTL, after which `PX` reclaims the id.
                // Never raise from a release — it runs in a caller's
                // `finally`.
                warn
                    $"RedisDistributedLock: release of {lease.LockId} (fence {lease.FenceToken}) failed ({ex.Message}); the lease will expire via its TTL"
        }

/// Factory helpers + the `TOOLUP_DISTRIBUTED_LOCK=redis` selector entry.
module RedisDistributedLock =
    /// Wrap a multiplexer the deployment already owns — the same instance
    /// backing `RedisNotificationChannel` / `RedisLifecycleLock`, so all
    /// three share one connection pool.
    let fromMultiplexer (multiplexer: IConnectionMultiplexer) (logger: ILogger option) : IDistributedLock =
        RedisDistributedLock(multiplexer, logger) :> IDistributedLock

    /// Connect and wrap. Returns `None` when the connection cannot be
    /// established, so a caller can fall back to the in-process default
    /// with a Warn rather than failing startup (the
    /// `DistributedLockSelection.fromEnv` contract).
    let fromConnectionString (connectionString: string) (logger: ILogger option) : IDistributedLock option =
        try
            let multiplexer = ConnectionMultiplexer.Connect connectionString
            Some(fromMultiplexer multiplexer logger)
        with ex ->
            match logger with
            | Some l -> l.Warn $"RedisDistributedLock: could not connect to Redis at {connectionString}: {ex.Message}"
            | None -> ()

            None

    /// Selector entry for `TOOLUP_DISTRIBUTED_LOCK=redis` with the
    /// connection string in `TOOLUP_REDIS_CONNECTION` — the same env var
    /// `RedisNotificationChannel` reads, so a deployment wiring both
    /// substrates configures one connection.
    ///
    /// Thread it into `DistributedLockSelection.fromEnv` and register the
    /// result from `ComposeExtensions.ServiceConfig`:
    ///
    ///     let lck = DistributedLockSelection.fromEnv logger [ RedisDistributedLock.resolver ]
    ///     { ComposeExtensions.empty with
    ///         ServiceConfig = Some(fun s -> s.AddSingleton<IDistributedLock>(lck)) }
    let resolver: DistributedLockResolver = {
        Name = "redis"
        ConnectionEnvVar = ConfigKeys.Names.redisConnection
        Resolve = fun logger connStr -> fromConnectionString connStr (Some logger)
    }