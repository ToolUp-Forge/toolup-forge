module ToolUp.Platform.WebhookRegistry

open System
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets

// ─── Storage layout ──────────────────────────────────────────────
//
// Container: always `_platform`.
// Subscription blob: `webhooks/{scopeId}/subscriptions/{subscriptionId:N}.json`
// Delivery blob:     `webhooks/{scopeId}/deliveries/{subscriptionId:N}/{ts}-{deliveryId:N}.json`
//
// The scope sits in the path prefix so per-scope operations are a
// cheap `List(container, "webhooks/{scopeId}/...")` call and cross-
// scope leakage is structurally impossible — the registry never widens
// the prefix during a scoped operation. `ListAllActive` is the *only*
// path that lists `webhooks/` directly; it's server-internal (not on
// `IWebhookApi`) and only the dispatcher calls it.

let private platformContainer = "_platform"

let private subscriptionBlob (scopeId: string) (subscriptionId: Guid) =
    $"webhooks/{scopeId}/subscriptions/{subscriptionId:N}.json"

let private subscriptionsPrefix (scopeId: string) = $"webhooks/{scopeId}/subscriptions/"

let private allSubscriptionsRoot = "webhooks/"

let private deliveryBlob (scopeId: string) (subscriptionId: Guid) (delivery: WebhookDelivery) =
    let ts =
        delivery.AttemptedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH-mm-ss-fffffffZ")

    $"webhooks/{scopeId}/deliveries/{subscriptionId:N}/{ts}-{delivery.DeliveryId:N}.json"

let private deliveriesPrefix (scopeId: string) (subscriptionId: Guid) =
    $"webhooks/{scopeId}/deliveries/{subscriptionId:N}/"

let private scopeDeliveriesPrefix (scopeId: string) = $"webhooks/{scopeId}/deliveries/"

let private allDeliveriesRoot = "webhooks/"

// ─── JSON ────────────────────────────────────────────────────────
//
// Subscriptions and deliveries both round-trip to the Fable admin UI,
// so use `FableConverters` (DU-aware shape compatible with
// `Fable.SimpleJson` on the client). Same pattern as `BlobConfigStore`.

module private Json =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : byte[] =
        JsonSerializer.Serialize(value, options) |> Encoding.UTF8.GetBytes

    let tryDeserialize<'T> (bytes: byte[]) : 'T option =
        try
            let json = Encoding.UTF8.GetString bytes
            Some(JsonSerializer.Deserialize<'T>(json, options))
        with _ ->
            None

// ─── Phase 464 — cross-instance rotation broadcast plumbing ──────

/// `CustomNotification` key the rotation envelope travels under. The
/// constant itself lives in `WebhookTypes` (Core) so a distributed
/// channel companion can recognise the topic without re-deriving the
/// string; this alias keeps the call sites below short.
let private SecretRotatedNotificationKey =
    WebhookSecretRotatedNotification.NotificationKey

/// Phase 464 — JSON options for the rotation envelope. The F# converter
/// set is mandatory (records / `DateTimeOffset` / `Guid` all break on a
/// bare `JsonSerializerOptions`); constructed once at module level per
/// the SDK's SSE / non-Remoting JSON convention. Separate from the
/// `Json` module above deliberately — that one is the Fable-shaped
/// PERSISTENCE codec for subscription blobs, and coupling a wire format
/// to a storage format is how one of them ends up unable to change.
let private envelopeJson = FableConverters.create ()

/// Phase 464 — this process's instance identity, stamped onto every
/// published envelope so a receiving instance can tell a sibling's
/// broadcast from its own echo.
///
/// Deliberately derived, not configured — `MachineName` is the container
/// / pod name under every orchestrator the SDK targets, so an instance is
/// already uniquely named without an env var the operator must remember
/// to set (and whose absence would silently collapse every instance onto
/// one identity, turning every sibling broadcast into a discarded echo).
/// The process id disambiguates instances colocated on one host. Same
/// derivation as the Phase 22b key-destruction fanout.
let internal defaultReplicaId () =
    sprintf "%s/%d" Environment.MachineName Environment.ProcessId

/// Phase 464 — decode a rotation-broadcast payload. `None` when the
/// payload is absent, not JSON, or carries no scope — a malformed
/// message must not throw inside a notification handler.
///
/// There is no legacy payload shape to tolerate here (unlike the Phase
/// 22b decoder, which absorbs a pre-22b bare-scopeId string): this topic
/// is new in Phase 464, so nothing older has ever been published under
/// it. A future shape change adds fields — the F# converter set ignores
/// unknown ones on read, so a newer publisher stays decodable by an
/// older subscriber.
let internal decodeSecretRotatedPayload (payload: string) : WebhookSecretRotatedEnvelope option =
    if String.IsNullOrWhiteSpace payload then
        None
    else
        try
            let env =
                JsonSerializer.Deserialize<WebhookSecretRotatedEnvelope>(payload, envelopeJson)

            if obj.ReferenceEquals(env, null) || String.IsNullOrWhiteSpace env.ScopeId then
                None
            else
                Some env
        with _ ->
            None

/// Phase 464 — the default rotation observer: drop the cached secret
/// material for the rotated scope when the composed `ISecretStore`
/// memoises (i.e. implements `ISecretCacheInvalidation`).
///
/// A store that does not implement the seam needs no action and gets
/// none — a cloud store that round-trips the vault per call already
/// serves the rotated value on the next read, so there is nothing stale
/// to evict. The type test is therefore a truthful question rather than
/// a capability probe with a silent failure arm.
let secretCacheInvalidator (secretStore: ISecretStore) : WebhookSecretRotatedEnvelope -> unit =
    match box secretStore with
    | :? ISecretCacheInvalidation as invalidation -> fun env -> invalidation.InvalidateScope env.ScopeId
    | _ -> ignore

// ─── Concurrency helper ──────────────────────────────────────────

/// Download many blobs in parallel, tolerating individual
/// deserialisation failures by skipping them. Mirrors
/// `PersistentEventStore.downloadAll` — a partial result is preferable
/// to a total failure for the dispatcher's hot path.
let private downloadAll<'T> (blobStorage: IBlobStorage) (blobNames: string list) : Async<'T list> = async {
    let! results =
        blobNames
        |> List.map (fun name -> async {
            let! result = blobStorage.Download(platformContainer, name)

            return
                match result with
                | Ok bytes -> Json.tryDeserialize<'T> bytes
                | Error _ -> None
        })
        |> Async.Parallel

    return results |> Array.choose id |> Array.toList
}

// ─── IWebhookRegistry — blob-backed ──────────────────────────────

/// Blob-backed `IWebhookRegistry`. One JSON blob per subscription,
/// stored in the reserved `_platform` container under the scope-
/// prefixed path. Updates are read-modify-write — no atomic
/// compare-and-swap, but subscriptions change rarely (only via admin
/// action and the dispatcher's `SetConsecutiveFailures`), so the
/// last-write-wins window is acceptable. A distributed implementation
/// adding ETag-based CAS drops in without changing the interface.
///
/// ## Secret rotation is EVENTUALLY CONSISTENT across instances (Phase 464)
///
/// `RotateSecret` durably rewrites the reference bookkeeping, and the
/// caller has already moved the secret VALUES in `ISecretStore`. Neither
/// act reaches a sibling instance's memory. The dispatcher itself holds
/// no subscription cache — it re-lists the scope per event and re-reads
/// the secret per delivery — so the only stale copy is one layer down, in
/// a **caching** `ISecretStore`. `FileSecretStore` memoises a scope's
/// whole secret map on first read and evicts only on its own write, with
/// no TTL: a rotation on instance A would otherwise leave instance B
/// signing with the superseded secret for the life of B's process. There
/// is no periodic refresh to wait for, which is why this needed a
/// broadcast rather than a shorter cache lifetime.
///
/// When wired via `WireToChannel`, a successful `RotateSecret` publishes
/// a `WebhookSecretRotatedEnvelope` on
/// `NotificationKind.PlatformReservedScope`, and every other instance
/// drops its cached secret material for that scope on receipt. The
/// convergence window is then the active channel companion's fanout
/// latency — **minute-grain per the `INotificationChannel` precision
/// contract, not instant** — instead of unbounded.
///
///  * **Single instance — wiring is optional.** There is no sibling
///    cache to invalidate, and the rotating process evicted its own on
///    the `SetSecret` write. The in-process default channel reaches only
///    the publishing process, so the fanout is a correct no-op and costs
///    nothing (GP 11 / GP 13).
///  * **More than one instance — wire it, and use a distributed channel.**
///    Unwired, every `RotateSecret` is counted and the first is logged at
///    security class, because the failure is silent otherwise: deliveries
///    keep succeeding from the rotating instance while siblings sign with
///    a secret the receiver has already retired, and the receiver reports
///    that as an inauthentic payload rather than as a stale key.
///
/// A caller that reads a signing secret ONCE and holds it — rather than
/// resolving it per use, as the dispatcher does — is outside what this
/// broadcast can fix and must restart to pick up a rotation. The Stripe
/// webhook companion's `WebhookSigner.verifyWithFetcher` is the shape
/// that avoids it.
type BlobWebhookRegistry(storage: IBlobStorage, logger: ILogger option) =
    /// Phase 464 — cross-instance channel, `None` until `WireToChannel`.
    /// `None` is the shipped default so an existing deployment that
    /// upgrades publishes nothing and behaves byte-for-byte as before
    /// (GP 11).
    let mutable channel: INotificationChannel option = None

    /// Phase 464 — this instance's identity, stamped on published
    /// envelopes and compared against incoming ones for echo
    /// suppression. Defaults to the derived `{machine}/{pid}`; the
    /// three-argument `WireToChannel` overrides it for tests that stand
    /// two logical instances up in one process.
    let mutable replicaId = defaultReplicaId ()

    /// Phase 464 — what this instance does when a SIBLING's rotation
    /// arrives. Production wiring supplies the secret-cache invalidator;
    /// the explicit overload lets a caller observe the envelope for its
    /// own caches too. `ignore` until wired.
    let mutable onRotationObserved: WebhookSecretRotatedEnvelope -> unit = ignore

    /// Phase 464 — rotations performed with no channel wired, i.e. how
    /// many rotations published no invalidation broadcast.
    let mutable unwiredRotateCount = 0

    /// Phase 464 — sibling rotations this instance has acted on. Zero on
    /// a single-instance deployment (its own publish is echo-suppressed),
    /// which is what makes it usable as fanout evidence.
    let mutable observedRotationCount = 0

    let load (scopeId: string) (subscriptionId: Guid) = async {
        let! result = storage.Download(platformContainer, subscriptionBlob scopeId subscriptionId)

        match result with
        | Ok bytes -> return Json.tryDeserialize<WebhookSubscription> bytes
        | Error _ -> return None
    }

    let save (sub: WebhookSubscription) = async {
        let bytes = Json.serialize sub
        let! result = storage.Upload(platformContainer, subscriptionBlob sub.ScopeId sub.SubscriptionId, bytes)

        match result with
        | Ok _ -> return Ok()
        | Error e -> return Error e
    }

    interface IWebhookRegistry with
        member _.CreateSubscription(subscription) = save subscription

        member _.ListSubscriptions(scopeId) = async {
            let! names = storage.List(platformContainer, subscriptionsPrefix scopeId)
            return! downloadAll<WebhookSubscription> storage names
        }

        member _.GetSubscription(scopeId, subscriptionId) = load scopeId subscriptionId

        member _.UpdateStatus(scopeId, subscriptionId, status) = async {
            match! load scopeId subscriptionId with
            | None -> return Error "Subscription not found."
            | Some sub ->
                let resetFailures =
                    status = WebhookStatus.Active && sub.Status = WebhookStatus.Disabled

                let updated = {
                    sub with
                        Status = status
                        ConsecutiveFailures = if resetFailures then 0 else sub.ConsecutiveFailures
                }

                return! save updated
        }

        member _.RotateSecret(scopeId, subscriptionId, currentSecretRef, previousSecretRef, graceExpiresAt) = async {
            match! load scopeId subscriptionId with
            | None -> return Error "Subscription not found."
            | Some sub ->
                let rotated =
                    WebhookSubscription.withRotatedSecret currentSecretRef previousSecretRef graceExpiresAt sub

                match! save rotated with
                | Ok() ->
                    // Phase 464 — broadcast AFTER the durable write. A
                    // publish that preceded it could invalidate every
                    // sibling's cache and then fail to persist, leaving
                    // the fleet re-reading the OLD value it just dropped
                    // — churn with no rotation. Ordering it here means a
                    // failed save publishes nothing.
                    match channel with
                    | Some ch ->
                        // Publish-side failures log but never fail
                        // `RotateSecret`: the durable rotation already
                        // succeeded, and returning `Error` here would
                        // tell the admin the rotation did not happen
                        // when it did. Channel-handler exceptions are
                        // swallowed by the channel itself (GP 12 r3);
                        // this guards the publish call.
                        try
                            let envelope: WebhookSecretRotatedEnvelope = {
                                ScopeId = scopeId
                                SubscriptionId = subscriptionId
                                CurrentSecretRef = currentSecretRef
                                PreviousSecretRef = previousSecretRef
                                GraceExpiresAt = graceExpiresAt
                                RotatedAt = DateTimeOffset.UtcNow
                                OriginReplicaId = replicaId
                            }

                            do!
                                ch.Publish(
                                    NotificationKind.PlatformReservedScope,
                                    CustomNotification(
                                        SecretRotatedNotificationKey,
                                        JsonSerializer.Serialize(envelope, envelopeJson)
                                    )
                                )
                        with ex ->
                            match logger with
                            | Some log ->
                                log.Error(
                                    sprintf
                                        "[WebhookRegistry] event=secret_rotation_broadcast_failed class=security scope=%s subscriptionId=%O — the rotation IS persisted, but the cross-instance invalidation broadcast did not reach the channel. Sibling instances keep signing with the superseded secret until they restart; a receiver that has already retired the old secret will report those deliveries as inauthentic. Check the distributed notification channel (e.g. Redis) connectivity, then restart the siblings."
                                        scopeId
                                        subscriptionId,
                                    Some ex
                                )
                            | None -> ()
                    // Unwired. On one instance that is correct and
                    // complete. On more than one it is a silent
                    // authenticity failure: this instance keeps
                    // delivering with the new secret while every sibling
                    // signs with the old one, and the receiver reports a
                    // bad signature rather than a stale key — so the
                    // cause is invisible from both ends. Counted always,
                    // logged once per process at security class.
                    | None ->
                        let count = Interlocked.Increment &unwiredRotateCount

                        if count = 1 then
                            match logger with
                            | Some log ->
                                log.Warn(
                                    sprintf
                                        "[WebhookRegistry] event=secret_rotation_unwired class=security scope=%s subscriptionId=%O — the signing secret was rotated, but this registry is not wired to an INotificationChannel (WireToChannel was never called), so NO invalidation broadcast was published. On a single-instance deployment this is correct and complete. On more than one instance, every SIBLING keeps signing deliveries with the superseded secret for the life of its process — a caching ISecretStore has no TTL, so the stale read never expires on its own — and a receiver updated to the new secret rejects those deliveries as inauthentic rather than as stale. Fix: compose webhooks through ServerApp (compose calls WireToChannel for you) and configure a distributed channel companion (ServerConfig.Notifications = RedisNotifications \"<connection-string>\"). Logged once per process; every unwired rotation is counted on UnwiredRotateSecretCount."
                                        scopeId
                                        subscriptionId
                                )
                            | None -> ()

                    return Ok rotated
                | Error e -> return Error e
        }

        member _.SetConsecutiveFailures(scopeId, subscriptionId, count) = async {
            match! load scopeId subscriptionId with
            | None -> return Error "Subscription not found."
            | Some sub ->
                return!
                    save {
                        sub with
                            ConsecutiveFailures = max 0 count
                    }
        }

        member _.DeleteSubscription(scopeId, subscriptionId) = async {
            let! _ = storage.Delete(platformContainer, subscriptionBlob scopeId subscriptionId)
            return Ok()
        }

        member _.ListAllActive() = async {
            // Two-stage list: enumerate the entire `webhooks/` prefix,
            // keep only paths matching `webhooks/{scopeId}/subscriptions/...`,
            // download them, filter to Active. The flat-list design of
            // `IBlobStorage` doesn't surface "directories" — we filter
            // on path shape ourselves.
            let! names = storage.List(platformContainer, allSubscriptionsRoot)

            let subscriptionNames =
                names
                |> List.filter (fun n -> n.Contains "/subscriptions/" && n.EndsWith ".json")

            let! all = downloadAll<WebhookSubscription> storage subscriptionNames
            return all |> List.filter (fun s -> s.Status = WebhookStatus.Active)
        }

    /// Backward-compatible constructor — no logger, so the Phase 464
    /// unwired-rotation warning cannot be emitted (the count is still
    /// kept, which is why it exists as a separate signal). Preserved
    /// unchanged so every existing call site compiles (GP 11).
    new(storage: IBlobStorage) = BlobWebhookRegistry(storage, None)

    /// Phase 464 — `true` once `WireToChannel` has been called, i.e.
    /// once `RotateSecret` publishes a cross-instance invalidation
    /// broadcast.
    member _.IsWiredToChannel: bool = channel.IsSome

    /// Phase 464 — how many `RotateSecret` calls published no
    /// invalidation broadcast because no channel was wired. `0` on a
    /// correctly-wired deployment and on one that has never rotated; any
    /// non-zero value on a multi-instance deployment counts rotations
    /// whose siblings are still signing with the superseded secret.
    /// Counted as well as logged because the one-arg constructor has no
    /// logger to warn through, and the count needs none to be true.
    member _.UnwiredRotateSecretCount: int = Volatile.Read &unwiredRotateCount

    /// Phase 464 — how many SIBLING rotations this instance has acted on.
    /// Echo-suppressed, so a single-instance deployment reads `0` no
    /// matter how many rotations it performs — which is what makes a
    /// non-zero value evidence that the fanout actually crossed an
    /// instance boundary rather than that a cache was merely cold.
    member _.ObservedRotationCount: int = Volatile.Read &observedRotationCount

    /// Phase 464 — wire this registry to a cross-process notification
    /// channel so signing-secret rotation propagates without a restart.
    /// Production overload: derives this instance's identity from
    /// `{machine-name}/{process-id}` and invalidates the composed
    /// `ISecretStore`'s cache on a sibling's rotation (a no-op when that
    /// store does not memoise).
    ///
    /// Single-instance deployments need not call this — the rotating
    /// process already evicted its own cache on the `SetSecret` write,
    /// and there is no sibling to tell.
    ///
    /// Idempotent: a second call replaces the prior subscription. The
    /// registry does not expose unsubscribe — its lifetime is the
    /// process lifetime.
    member this.WireToChannel(notificationChannel: INotificationChannel, secretStore: ISecretStore) : Async<unit> =
        this.WireToChannel(notificationChannel, defaultReplicaId (), secretCacheInvalidator secretStore)

    /// Phase 464 — `WireToChannel` with an explicit instance identity and
    /// an explicit rotation observer.
    ///
    /// Production wiring uses the two-argument overload. This one exists
    /// for the case that derivation cannot serve: two registry instances
    /// in ONE process standing in for two deployment instances (the
    /// in-process fanout test, and any deployment hosting several logical
    /// instances per process). They would otherwise share one derived
    /// identity and each would discard the other's broadcast as its own
    /// echo — the fanout would test as working while doing nothing.
    ///
    /// `onRotation` runs on the channel's delivery thread and must be
    /// cheap and non-throwing; a throw is caught here so it can never
    /// escape into the channel.
    member _.WireToChannel
        (
            notificationChannel: INotificationChannel,
            replicaIdentity: string,
            onRotation: WebhookSecretRotatedEnvelope -> unit
        ) : Async<unit> =
        async {
            channel <- Some notificationChannel
            replicaId <- replicaIdentity
            onRotationObserved <- onRotation

            // Subscribe to the cross-scope reserved bus for rotations
            // from other instances. The handler is synchronous — the
            // documented exemption to portability rule 2 — and must
            // stay so: the invalidation has to have happened before any
            // concurrent read on this instance can still hit the stale
            // entry.
            let handler (env: NotificationEnvelope) =
                match env.Notification with
                | CustomNotification(key, payloadJson) when key = SecretRotatedNotificationKey ->
                    match decodeSecretRotatedPayload payloadJson with
                    | Some envelope ->
                        // Echo suppression. The in-process channel
                        // delivers a publish back to its publisher, so
                        // without this a single-instance deployment
                        // would count its own rotation as a sibling
                        // signal and `ObservedRotationCount` would stop
                        // being fanout evidence. The invalidation is
                        // skipped too — the rotating instance's own
                        // `SetSecret` already evicted locally.
                        if envelope.OriginReplicaId <> replicaId then
                            try
                                onRotationObserved envelope
                                Interlocked.Increment &observedRotationCount |> ignore
                            with _ ->
                                ()
                    | None -> ()
                | _ -> ()

            let! _ = notificationChannel.Subscribe(NotificationKind.PlatformReservedScope, handler)
            return ()
        }

/// Blob-backed registry as the `IWebhookRegistry` interface. Unchanged
/// surface; the Phase 464 rotation broadcast is off until something
/// calls `WireToChannel`, which requires the concrete type — use
/// `createRegistryInstance` when the caller needs to wire it.
let createRegistry (storage: IBlobStorage) : IWebhookRegistry =
    BlobWebhookRegistry(storage) :> IWebhookRegistry

/// Phase 464 — the concrete registry, so a composition root can reach
/// `WireToChannel` after the `INotificationChannel` is resolved. The
/// logger carries the unwired-rotation and failed-broadcast warnings.
let createRegistryInstance (storage: IBlobStorage) (logger: ILogger) : BlobWebhookRegistry =
    BlobWebhookRegistry(storage, Some logger)

// ─── Storage-level helpers (Phase 6d.A migration + validator) ────
//
// The one-shot secret-at-rest migration and the preflight validator both
// need to walk EVERY persisted subscription regardless of status (a
// half-migrated Paused/Disabled subscription still carries a plaintext
// secret) and to re-persist a rewritten record. `IWebhookRegistry`
// deliberately exposes neither — `ListAllActive` filters to Active and
// there is no "raw save" on the interface — so these live as module
// functions over the same blob layout / JSON codec.

/// Every persisted subscription across every scope, any status. Used by
/// the Phase 6d.A migration + secret-at-rest validator; NOT on the hot
/// path (the dispatcher uses the per-scope / active-only registry
/// methods). Tolerates individual deserialisation failures by skipping.
let listAllSubscriptions (storage: IBlobStorage) : Async<WebhookSubscription list> = async {
    let! names = storage.List(platformContainer, allSubscriptionsRoot)

    let subscriptionNames =
        names
        |> List.filter (fun n -> n.Contains "/subscriptions/" && n.EndsWith ".json")

    return! downloadAll<WebhookSubscription> storage subscriptionNames
}

/// Re-persist a subscription record at its canonical blob path. Used by
/// the Phase 6d.A migration to rewrite a blob with `SecretRef` after
/// moving its inline secret into `ISecretStore`. Same read-modify-write
/// last-write-wins window as the registry's own `save`.
let saveSubscription (storage: IBlobStorage) (sub: WebhookSubscription) : Async<Result<unit, string>> = async {
    let bytes = Json.serialize sub
    let! result = storage.Upload(platformContainer, subscriptionBlob sub.ScopeId sub.SubscriptionId, bytes)

    match result with
    | Ok _ -> return Ok()
    | Error e -> return Error e
}

// ─── IWebhookDeliveryLog — blob-backed ───────────────────────────

/// Blob-backed `IWebhookDeliveryLog`. One JSON blob per attempt; the
/// blob name's timestamp prefix orders rows lexicographically and lets
/// `Prune` filter on name without parsing the JSON body.
///
/// The `ListRecent` path lists every blob under the subscription's
/// prefix, then sorts and trims — fine while delivery counts are
/// modest (Phase 6d's retention is bounded by the dispatcher's
/// periodic `Prune`). A distributed implementation backing this with
/// an append-optimised log (Kafka, EventGrid) would drop in without
/// changing the interface.
type BlobWebhookDeliveryLog(storage: IBlobStorage) =
    /// Parse the timestamp from a blob name of the form
    /// `webhooks/.../deliveries/.../{ts}-{guid:N}.json`. The timestamp
    /// component is `yyyy-MM-ddTHH-mm-ss-fffffffZ` (28 chars) — written
    /// with hyphens in the time positions because some blob backends
    /// disallow `:` in names. Restore them here before parsing.
    /// Returns `None` for unparseable names so a stray file in the
    /// path doesn't crash retention.
    let parseTimestamp (name: string) : DateTime option =
        try
            let lastSlash = name.LastIndexOf '/'

            if lastSlash < 0 then
                None
            else
                let leaf = name.Substring(lastSlash + 1)
                // `{ts}-{guid:N}.json` — guid:N is 32 chars, `.json` is
                // 5 chars, separator is 1 char, so the suffix length is
                // 38. Anything shorter isn't one of our delivery blobs.
                if leaf.Length < 38 + 28 then
                    None
                else
                    let tsPart = leaf.Substring(0, leaf.Length - 38)
                    let sb = StringBuilder(tsPart)
                    sb[13] <- ':'
                    sb[16] <- ':'
                    sb[19] <- '.'

                    match
                        DateTime.TryParse(
                            sb.ToString(),
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind
                        )
                    with
                    | true, dt -> Some(dt.ToUniversalTime())
                    | _ -> None
        with _ ->
            None

    interface IWebhookDeliveryLog with
        member _.Record(scopeId, delivery) = async {
            let bytes = Json.serialize delivery

            let! result =
                storage.Upload(platformContainer, deliveryBlob scopeId delivery.SubscriptionId delivery, bytes)

            match result with
            | Ok _ -> return Ok()
            | Error e -> return Error e
        }

        member _.ListRecent(scopeId, subscriptionId, limit) = async {
            let! names = storage.List(platformContainer, deliveriesPrefix scopeId subscriptionId)
            // Lex-desc on name == time-desc because the timestamp prefix
            // is ISO-ordered. Trim before download to keep the hot path
            // bounded.
            let trimmed = names |> List.sortByDescending id |> List.truncate (max 0 limit)

            let! rows = downloadAll<WebhookDelivery> storage trimmed
            return rows |> List.sortByDescending _.AttemptedAt
        }

        member _.Prune(scopeIdOpt, olderThan) = async {
            let prefix =
                match scopeIdOpt with
                | Some scopeId -> scopeDeliveriesPrefix scopeId
                | None -> allDeliveriesRoot

            let! names = storage.List(platformContainer, prefix)

            let toDelete =
                names
                |> List.filter (fun n -> n.Contains "/deliveries/" && n.EndsWith ".json")
                |> List.filter (fun n ->
                    match parseTimestamp n with
                    | Some ts -> ts < olderThan
                    | None -> false)

            let! _ =
                toDelete
                |> List.map (fun name -> storage.Delete(platformContainer, name))
                |> Async.Parallel

            return ()
        }

let createDeliveryLog (storage: IBlobStorage) : IWebhookDeliveryLog =
    BlobWebhookDeliveryLog(storage) :> IWebhookDeliveryLog