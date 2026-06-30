module ToolUp.Platform.NotificationChannels.RedisLifecycleLock

open System
open System.Threading
open StackExchange.Redis
open ToolUp.Platform

// ─── Phase 54h — distributed offboard exclusion lock over Redis ──────
//
// The cross-replica `ILifecycleLock` reference impl, mirroring the
// distributed-substrate pattern `RedisNotificationChannel` established
// (Phase 1g): the same `IConnectionMultiplexer` the deployment already
// owns for notifications backs the lock, so no new connection is opened.
//
// Acquire is a single `SET key token NX PX ttl` — atomic try-acquire: it
// sets the scope's lock key only if absent (`NX`) with a millisecond
// expiry (`PX`). Success ⇒ the caller holds the scope; failure ⇒ another
// replica already holds it, so `Acquire` returns `None` (the loser sees a
// prompt "offboard already in progress" rather than blocking for the
// minutes a cross-store erasure can take — the distributed-default
// semantic the `ILifecycleLock` contract permits).
//
// Release is a compare-and-delete Lua script: it deletes the key only if
// it still holds OUR token, so a lease whose TTL already lapsed (and was
// re-acquired by a *different* replica) is never deleted out from under
// the new holder. Disposing releases early; a holder that crashes
// mid-offboard never disposes, and the `PX` expiry reclaims the scope
// after the lease TTL — the crash-recovery the contract requires.
//
// **Six-rule portability (GP 12):** identity by value (`scopeId` string,
// the lease is an opaque `IDisposable`); async at the acquire boundary;
// contention is a value (`None`), not a callback; stateless between calls
// (every acquire round-trips to Redis, so any replica sees any other's
// hold); per-`scopeId` keying with no cross-scope ordering; the lease TTL
// is the declared precision, honoured at Redis's millisecond `PX`
// resolution.
//
// **Lease TTL must exceed the worst-case offboard.** `PX` reclaims the
// scope after the TTL whether or not the offboard finished, so a TTL
// shorter than a slow multi-store erasure would let a second replica in
// mid-offboard. The default (`DefaultLeaseTtlMinutes`) is generous; a
// deployment with longer offboards raises it. Lease renewal (a watchdog
// extending `PX` while the offboard runs) is a documented follow-on, not
// in this reference impl.

/// Compare-and-delete: delete the lock key only if it still holds our
/// token. `KEYS[1]` is the lock key, `ARGV[1]` our token. Returns 1 on
/// delete, 0 when the key is absent or held by a later token.
[<Literal>]
let private releaseScript =
    "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end"

/// Per-scope lock key. Namespaced under `toolup:lifecycle-lock:` so it
/// can't collide with the notification channels (`toolup:notifications:`)
/// or another application sharing the same Redis instance.
let private keyName (scopeId: string) : RedisKey =
    RedisKey.op_Implicit (sprintf "toolup:lifecycle-lock:%s" scopeId)

/// Phase 54h — distributed `ILifecycleLock` over Redis `SET NX PX` +
/// compare-and-delete release. Construct from the multiplexer the
/// deployment already owns (the same one backing `RedisNotificationChannel`);
/// `leaseTtl` is the scope-hold expiry that bounds crash recovery.
type RedisLifecycleLock(multiplexer: IConnectionMultiplexer, leaseTtl: TimeSpan, logger: ILogger option) =

    let db = multiplexer.GetDatabase()

    let log (message: string) =
        match logger with
        | Some l -> l.Warn message
        | None -> ()

    interface ILifecycleLock with
        member _.Acquire(scopeId: string) : Async<IDisposable option> = async {
            let key = keyName scopeId
            // A fresh per-acquire token so release only ever deletes the
            // lease THIS acquire took — never a later holder's.
            let token = Guid.NewGuid().ToString("N")

            let! acquired =
                db.StringSetAsync(key, RedisValue.op_Implicit token, Nullable leaseTtl, When.NotExists)
                |> Async.AwaitTask

            if not acquired then
                // Another replica holds the scope — fail fast.
                return None
            else
                let released = ref 0

                let lease =
                    { new IDisposable with
                        member _.Dispose() =
                            // Release-once + best-effort. A Redis outage at
                            // dispose costs nothing — the `PX` TTL reclaims
                            // the lease. Never throw from Dispose (it runs
                            // in the aggregator's `finally`).
                            if Interlocked.Exchange(released, 1) = 0 then
                                try
                                    db.ScriptEvaluate(releaseScript, [| key |], [| RedisValue.op_Implicit token |])
                                    |> ignore
                                with ex ->
                                    log (
                                        sprintf
                                            "RedisLifecycleLock: release of %s failed (%s); lease will expire via its TTL"
                                            scopeId
                                            ex.Message
                                    )
                    }

                return Some lease
        }

/// Factory helpers. A deployment that already builds a multiplexer for
/// `RedisNotificationChannel` passes the same instance here, so the lock
/// shares the connection pool. `fromMultiplexer` uses the default lease
/// TTL; `create` takes an explicit TTL for deployments whose offboards run
/// longer than the default window.
module RedisLifecycleLock =
    /// Default scope-hold lease TTL. Generous headroom over the aggregator's
    /// 5-minute per-hook `OnDeprovisioned` timeout so a typical multi-hook
    /// offboard finishes well inside the window; raise it for deployments
    /// with slower erasures.
    [<Literal>]
    let DefaultLeaseTtlMinutes = 15.0

    let create (multiplexer: IConnectionMultiplexer) (leaseTtl: TimeSpan) (logger: ILogger option) : ILifecycleLock =
        RedisLifecycleLock(multiplexer, leaseTtl, logger) :> ILifecycleLock

    let fromMultiplexer (multiplexer: IConnectionMultiplexer) (logger: ILogger option) : ILifecycleLock =
        create multiplexer (TimeSpan.FromMinutes DefaultLeaseTtlMinutes) logger