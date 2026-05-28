module ToolUp.Platform.NotificationChannel

open System
open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.Tracing

/// In-memory `INotificationChannel`. Keeps subscriptions in a
/// concurrent dictionary keyed by subscription id; every `Publish`
/// scans the dictionary for handlers whose recorded scope matches.
///
/// This is the default implementation registered by `SDK.Server.compose`.
/// A distributed deployment would replace this with a pub/sub-backed
/// variant (Redis Streams, Azure Service Bus topics, Orleans streams)
/// — the `INotificationChannel` contract is deliberately implementable
/// by any of those without signature changes (GP 12).
///
/// ## Why a flat dictionary, not per-scope buckets
///
/// A subscriber's scope is mostly read-only for its lifetime, so the
/// naive `Dictionary<scopeId, Handler list>` with a lock per bucket
/// would be cheaper on `Publish`. We use the flat keyed-by-id map
/// instead because:
/// 1. `Unsubscribe` becomes O(1) instead of scanning a bucket
/// 2. The subscription id is the natural primary key — it matches the
///    portability contract (Rule 1: identity by value)
/// 3. The expected cardinality is low (one SSE connection per user,
///    tens per server), so the linear scan on `Publish` is cheap
///
/// If profiling shows the scan cost matters, the migration to a
/// per-scope bucket + separate id index is local to this file.
type InMemoryNotificationChannel(logger: ILogger option) =

    /// All active subscriptions. Value is `(scopeId, handler)` so the
    /// scope lookup and the invocation can both happen without a
    /// second dictionary hop.
    let subscriptions =
        ConcurrentDictionary<NotificationSubscriptionId, string * (NotificationEnvelope -> unit)>()

    let log (message: string) =
        match logger with
        | Some l -> l.Warn(message)
        | None -> ()

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async {
            // Phase 9l — stamp the publisher's W3C trace id so out-of-
            // band subscribers (transactional dispatcher, distributed
            // channel companions) can re-parent under the originating
            // request. `currentTraceparent` returns `None` when no
            // activity is in flight; in that case the envelope's
            // `TraceContext` stays `None` (same shape as `create`).
            let envelope =
                NotificationEnvelope.createWithTraceContext
                    scopeId
                    notification
                    (ActivityContextHelpers.currentTraceparent ())

            // Snapshot the current subscribers. `ConcurrentDictionary`
            // enumeration is lock-free and gives us a consistent view
            // without blocking concurrent `Subscribe` / `Unsubscribe`
            // calls — a subscriber added mid-publish may or may not
            // see this notification, which is the expected "best
            // effort" semantics documented on the interface.
            let matching =
                subscriptions
                |> Seq.filter (fun kvp ->
                    let subScope, _ = kvp.Value
                    subScope = scopeId)
                |> Seq.toList

            for kvp in matching do
                let subId, (_, handler) = kvp.Key, kvp.Value

                try
                    handler envelope
                with ex ->
                    // GP 12 rule 3 — swallow, log, carry on. A
                    // misbehaving subscriber must not poison the
                    // publish path or prevent its siblings from
                    // receiving the same notification.
                    log $"NotificationChannel: handler %O{subId} threw %s{ex.GetType().Name}: %s{ex.Message}"
        }

        member _.Subscribe(scopeId, handler) = async {
            let id = Guid.NewGuid()
            let added = subscriptions.TryAdd(id, (scopeId, handler))

            if not added then
                // Guid collisions are astronomically unlikely but the
                // contract is "adds always succeed"; log and retry
                // with a fresh Guid rather than silently dropping the
                // subscription.
                log $"NotificationChannel: Guid collision on %O{id} — retrying"

                let retryId = Guid.NewGuid()
                subscriptions[retryId] <- (scopeId, handler)
                return retryId
            else
                return id
        }

        member _.Unsubscribe(subscriptionId) = async {
            // Idempotent per contract — missing id is a no-op, not an
            // error. Callers often double-unsubscribe on connection
            // teardown (cancellation token fires + `finally` block).
            subscriptions.TryRemove(subscriptionId) |> ignore
        }

/// `NoOp` `INotificationChannel`. Registered by `compose` when
/// `ServerConfig.Notifications` resolves to `NoNotifications` (Phase 1g
/// lightweight default). `Publish` returns immediately without
/// allocating; `Subscribe` returns a fresh `Guid` so callers that hold
/// onto subscription handles for `Unsubscribe` still work, but no
/// handler is ever invoked. The `/api/notifications` SSE route is
/// also skipped at compose time so no client can register a live
/// subscription against this channel.
type NoOpNotificationChannel() =
    interface INotificationChannel with
        member _.Publish(_scopeId, _notification) = async { return () }

        member _.Subscribe(_scopeId, _handler) = async { return Guid.NewGuid() }

        member _.Unsubscribe(_subscriptionId) = async { return () }

// ─── Phase 11.G — env-var-driven channel construction ─────────────

/// Resolver for one distributed-pub/sub `INotificationChannel`
/// companion (e.g. Redis). The consumer threads in one entry per
/// companion the deployment has wired (e.g. `{ Name = "redis";
/// Resolve = redisResolve }` where `redisResolve` constructs the
/// channel + matching `IHealthCheck` + matching `IConfigValidator`
/// from one shared multiplexer connection). Keeps
/// `ToolUp.Platform.Server` free of any direct dependency on
/// notification-channel companion packages.
type NotificationChannelResolver = {
    /// Matched against `TOOLUP_NOTIFICATION_CHANNEL` (case-
    /// insensitive). Common value: `"redis"`.
    Name: string
    /// The companion's builder, applied with the deployment's
    /// `ILogger` + the resolved connection string. Companions that
    /// need additional env vars read them inside `Resolve`. Returns
    /// `None` when the connection-string env var is unset / empty so
    /// the helper falls back to the in-process default with a Warn.
    Resolve:
        ILogger
            -> string
            -> (INotificationChannel *
            ToolUp.Platform.HealthChecks.IHealthCheck option *
            ToolUp.Platform.ConfigValidation.IConfigValidator option) option
    /// Env-var name carrying the connection string the resolver
    /// consumes. The helper looks it up before calling `Resolve` and
    /// warns + falls back to in-process when unset.
    ConnectionEnvVar: string
}

let private envVar (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some v

/// Build the deployment's notification-channel triple from
/// `TOOLUP_NOTIFICATION_CHANNEL`. The triple `(channel, healthProbe,
/// configValidator)` is returned in one shot so the channel, its
/// probe, and its preflight share one TCP connection / multiplexer
/// (matches the hand-written reference behaviour byte-for-byte).
///
/// Recognised values:
///   - unset / `inprocess` / `in-process` (default) —
///     `InMemoryNotificationChannel`, no probe, no validator.
///   - any value matched by a `resolvers` entry — resolved against
///     that companion. Falls back to in-process + Warn when the
///     connection env var is unset, or to in-process when the
///     resolver returns `None`.
///   - unrecognised value — falls back to in-process with a Warn
///     naming the recognised values.
let fromEnv
    (logger: ILogger)
    (resolvers: NotificationChannelResolver list)
    : INotificationChannel *
      ToolUp.Platform.HealthChecks.IHealthCheck option *
      ToolUp.Platform.ConfigValidation.IConfigValidator option
    =
    let inProcess () =
        InMemoryNotificationChannel(Some logger) :> INotificationChannel, None, None

    let chosen = envVar "TOOLUP_NOTIFICATION_CHANNEL" |> Option.map _.ToLowerInvariant()

    match chosen with
    | None
    | Some "inprocess"
    | Some "in-process" ->
        logger.Info "Notification channel: in-process (InMemoryNotificationChannel)"
        inProcess ()
    | Some other ->
        match
            resolvers
            |> List.tryFind (fun r -> r.Name.Equals(other, StringComparison.OrdinalIgnoreCase))
        with
        | Some resolver ->
            match envVar resolver.ConnectionEnvVar with
            | None ->
                logger.Warn
                    $"TOOLUP_NOTIFICATION_CHANNEL={resolver.Name} but {resolver.ConnectionEnvVar} is not set. Falling back to the in-process InMemoryNotificationChannel — cross-process notification delivery is disabled."

                inProcess ()
            | Some connStr ->
                match resolver.Resolve logger connStr with
                | Some triple ->
                    logger.Info $"Notification channel: {resolver.Name} ({connStr})"
                    triple
                | None ->
                    logger.Warn
                        $"TOOLUP_NOTIFICATION_CHANNEL={resolver.Name} but {resolver.ConnectionEnvVar}={connStr} did not yield a working channel. Falling back to in-process."

                    inProcess ()
        | None ->
            let recognisedNames =
                "inprocess" :: (resolvers |> List.map _.Name) |> String.concat ", "

            logger.Warn
                $"TOOLUP_NOTIFICATION_CHANNEL={other} not recognised. Valid values: {recognisedNames}. Falling back to in-process InMemoryNotificationChannel."

            inProcess ()