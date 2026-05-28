module ToolUp.Platform.NotificationChannels.Redis

open System
open System.Collections.Concurrent
open Newtonsoft.Json
open Fable.Remoting.Json
open StackExchange.Redis
open ToolUp.Platform

/// Redis pub/sub-backed `INotificationChannel`. Uses one Redis channel
/// per `scopeId` (`toolup:notifications:{scopeId}`) so scope isolation
/// is structural: a subscriber for scope A is listening on a different
/// Redis channel from scope B and will never see B's publishes, even
/// if the Redis server is compromised in a way that would break a
/// post-hoc server-side filter. GP 4 (team isolation) is enforced by
/// the transport, not by application code.
///
/// ## Serialisation
///
/// Envelopes are serialised with `FableJsonConverter` so the wire
/// format matches the SSE payload shape (Phase 6a). This is future-
/// proofing: a Redis-backed deployment could replay envelopes straight
/// from the channel into an SSE stream without a re-serialisation hop.
/// `Notification` is a DU; without the converter `Fable.SimpleJson` on
/// future client-side replay paths could not deserialise the
/// `{"Case":"X","Fields":[...]}` shape that Newtonsoft produces by
/// default.
///
/// ## Handler dispatch
///
/// `ISubscriber.Subscribe` returns a `ChannelMessageQueue`, and we use
/// its `OnMessage` async callback. Each subscription's queue has its
/// own handler loop, so a slow subscriber for one `Guid` cannot
/// starve siblings. Handler exceptions are caught + logged to honour
/// GP 12 rule 3 (no callback-based supervision leaks to callers).
///
/// ## Subscription identity
///
/// `NotificationSubscriptionId` remains a `Guid` — the internal map
/// from Guid → (scope, handler, ChannelMessageQueue) is private to
/// this type and never exposed. Orleans / Akka would do the same with
/// their own handle types; by keeping the public handle opaque, the
/// portability rule (Phase 9c rule 1) holds. A caller that serialises
/// its subscription id through a database and replays it into a
/// different channel instance gets an idempotent no-op, not a crash.
///
/// ## Connection lifecycle
///
/// Takes an `IConnectionMultiplexer`, which the caller owns. StackExchange.
/// Redis recommends one multiplexer per process (it multiplexes all
/// commands onto a small socket pool), so the SDK does not create one
/// internally — construction and teardown are the app's decision.
/// `Server.fs` builds it once via `ConnectionMultiplexer.Connect` and
/// passes it here.
type RedisNotificationChannel(multiplexer: IConnectionMultiplexer, logger: ILogger option) =

    let subscriber = multiplexer.GetSubscriber()

    /// Shared JSON settings with FableJsonConverter registered. Reused
    /// across publishes to avoid the ~5ms settings-construction cost
    /// on every call (Newtonsoft warms an internal cache keyed by the
    /// settings instance).
    let jsonSettings =
        let s = JsonSerializerSettings()
        s.Converters.Add(FableJsonConverter())
        s

    let subscriptions =
        ConcurrentDictionary<NotificationSubscriptionId, string * (NotificationEnvelope -> unit) * ChannelMessageQueue>()

    let log (message: string) =
        match logger with
        | Some l -> l.Warn(message)
        | None -> ()

    /// Per-scope Redis channel name. Scope ids can contain arbitrary
    /// characters; Redis channel names accept anything except NUL.
    /// Prefixing with `toolup:notifications:` keeps our channels in a
    /// dedicated namespace so other applications sharing the same Redis
    /// instance cannot accidentally publish into our scope space.
    let channelName (scopeId: string) =
        RedisChannel($"toolup:notifications:%s{scopeId}", RedisChannel.PatternMode.Literal)

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async {
            let envelope = NotificationEnvelope.create scopeId notification
            let payload = JsonConvert.SerializeObject(envelope, jsonSettings)

            let! _ =
                subscriber.PublishAsync(channelName scopeId, RedisValue.op_Implicit payload)
                |> Async.AwaitTask

            return ()
        }

        member _.Subscribe(scopeId, handler) = async {
            let id = Guid.NewGuid()
            let queue = subscriber.Subscribe(channelName scopeId)

            // `OnMessage` registers an async handler that runs on a
            // StackExchange.Redis-owned worker. The handler body is
            // synchronous (matches `INotificationChannel.Subscribe`'s
            // `NotificationEnvelope -> unit`); any long-running work
            // inside is the subscriber's responsibility to dispatch
            // off-thread.
            queue.OnMessage(fun channelMessage ->
                try
                    let payload = string channelMessage.Message

                    let envelope =
                        JsonConvert.DeserializeObject<NotificationEnvelope>(payload, jsonSettings)

                    handler envelope
                with ex ->
                    // GP 12 rule 3 — swallow, log, carry on. A slow or
                    // broken subscriber for one scope must not poison
                    // sibling scopes' dispatch.
                    log $"RedisNotificationChannel: handler %O{id} threw %s{ex.GetType().Name}: %s{ex.Message}")

            subscriptions[id] <- (scopeId, handler, queue)
            return id
        }

        member _.Unsubscribe(subscriptionId) = async {
            // Idempotent per contract — missing id is a no-op.
            match subscriptions.TryRemove(subscriptionId) with
            | true, (_, _, queue) ->
                // Unsubscribe from Redis synchronously so any pending
                // dispatch completes before this call returns. The
                // contract allows the caller to assume no more
                // deliveries after this point.
                do! queue.UnsubscribeAsync() |> Async.AwaitTask
            | false, _ -> ()
        }

/// Factory helpers. Apps typically construct a single multiplexer at
/// startup and pass it in; `fromConnectionString` is a convenience for
/// the common case where the connection string is read straight from
/// an env var without further configuration. `connect` returns the
/// multiplexer alongside the channel so the same instance can back the
/// Phase 9k `RedisNotificationChannelHealth` probe.
module RedisNotificationChannel =
    let fromMultiplexer (multiplexer: IConnectionMultiplexer) (logger: ILogger option) =
        RedisNotificationChannel(multiplexer, logger) :> INotificationChannel

    let fromConnectionString (connectionString: string) (logger: ILogger option) =
        let multiplexer =
            ConnectionMultiplexer.Connect(connectionString) :> IConnectionMultiplexer

        RedisNotificationChannel(multiplexer, logger) :> INotificationChannel

    /// Connect to Redis and return the multiplexer alongside the
    /// resulting channel. The multiplexer is exposed so the same
    /// connection backs both the channel and the Phase 9k health
    /// probe (`RedisNotificationChannelHealth.create multiplexer`).
    let connect (connectionString: string) (logger: ILogger option) : IConnectionMultiplexer * INotificationChannel =
        let multiplexer =
            ConnectionMultiplexer.Connect(connectionString) :> IConnectionMultiplexer

        let channel = RedisNotificationChannel(multiplexer, logger) :> INotificationChannel
        multiplexer, channel