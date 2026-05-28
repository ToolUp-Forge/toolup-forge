namespace ToolUp.Platform

open System

/// Scope-indexed persistent registry for outbound webhook subscriptions.
///
/// One blob per subscription under
/// `_platform/webhooks/{scopeId}/subscriptions/{subscriptionId:N}.json`.
/// Listing a scope is a prefix scan; cross-scope reads are structurally
/// impossible — every method takes the `scopeId` as the first parameter
/// and the implementation never widens the lookup.
///
/// Lives in the server layer (not shared) because implementations
/// depend on `IBlobStorage` and `FableJsonConverter` for persistence.
/// The Fable.Remoting admin surface (`IWebhookApi`) is the only thing
/// the browser sees — it exposes a strict subset and resolves the
/// `scopeId` server-side from `AccessContext`.
///
/// Phase 9c portability: subscription identity is `Guid` (rule 1),
/// every method is `Async<_>` (rule 2), `ConsecutiveFailures` is
/// persisted on the record so the dispatcher does not assume in-memory
/// state across silo restarts (rule 4). A future Akka.Persistence /
/// Orleans implementation drops in without changing the contract.
type IWebhookRegistry =
    /// Persist a new subscription. The caller has already populated
    /// every field (including `SubscriptionId` — generated upstream so
    /// the post-create flow can correlate audit events with the new
    /// id without a second round-trip).
    abstract CreateSubscription: subscription: WebhookSubscription -> Async<Result<unit, string>>

    /// All subscriptions under the scope. Order is undefined; callers
    /// that care sort client-side. Returns `[]` for unknown scopes.
    abstract ListSubscriptions: scopeId: string -> Async<WebhookSubscription list>

    /// One subscription by id within the scope. `None` when no blob
    /// exists at `subscriptions/{id:N}.json` under the scope prefix —
    /// the registry never resolves a foreign-scope subscription, even
    /// if the same `Guid` exists elsewhere.
    abstract GetSubscription: scopeId: string * subscriptionId: Guid -> Async<WebhookSubscription option>

    /// Flip status. Setting `Active` from `Disabled` resets
    /// `ConsecutiveFailures` to 0 — the dispatcher needs the counter
    /// reset so re-enabling clears the auto-disable trigger. Setting
    /// `Paused` or `Disabled` leaves the counter untouched (an admin
    /// pausing during incident response should resume to the same
    /// state on re-activation).
    abstract UpdateStatus: scopeId: string * subscriptionId: Guid * status: WebhookStatus -> Async<Result<unit, string>>

    /// Persist the current `ConsecutiveFailures` value. Called by the
    /// dispatcher after every dead-letter (increment) and after every
    /// successful delivery (reset to 0). Separate from `UpdateStatus`
    /// because the dispatcher updates the counter far more often than
    /// it changes status.
    abstract SetConsecutiveFailures: scopeId: string * subscriptionId: Guid * count: int -> Async<Result<unit, string>>

    /// Idempotent delete — succeeds when the subscription does not
    /// exist. Removes the subscription blob; the delivery log is
    /// pruned by the dispatcher's retention pass on the next sweep.
    abstract DeleteSubscription: scopeId: string * subscriptionId: Guid -> Async<Result<unit, string>>

    /// Cross-scope enumeration for the dispatcher's fan-out match
    /// pass. Returns every active subscription across every scope —
    /// the dispatcher then filters by `ScopeId` against the inbound
    /// event and by `EventTypes` against the event's type. Disabled
    /// and Paused subscriptions are excluded so the dispatcher's hot
    /// path doesn't waste work on them.
    ///
    /// This is the *only* method that reads across scopes. It is
    /// server-internal — never exposed through `IWebhookApi`. A
    /// distributed implementation would back this with a secondary
    /// index keyed on `(EventType, Status)`; the in-process default
    /// scans the prefix tree and is fine for single-instance
    /// deployments (single-instance dispatcher is the documented
    /// Phase 9c follow-up).
    abstract ListAllActive: unit -> Async<WebhookSubscription list>

/// Per-subscription delivery log. Persisted as one blob per attempt
/// under `_platform/webhooks/{scopeId}/deliveries/{subscriptionId:N}/{yyyy-MM-ddTHH-mm-ss-fffffffZ}-{deliveryId:N}.json`.
/// Date-prefixed name enables cheap retention pruning by `olderThan`
/// without scanning the JSON body, mirroring `PersistentEventStore`'s
/// scheme.
///
/// Separate interface from `IWebhookRegistry` because the lifecycles
/// differ: subscriptions are mutable user-facing records;
/// deliveries are append-only audit rows. Distributed implementations
/// will likely back deliveries with a different store (append-optimised
/// log, maybe Kafka-style) than subscriptions (key-value with
/// secondary indexes), so the split is structurally appropriate too.
type IWebhookDeliveryLog =
    /// Append a delivery row. Idempotent only on `(SubscriptionId,
    /// DeliveryId)` — the dispatcher generates a fresh `DeliveryId`
    /// per attempt, so retries don't collide with the original.
    abstract Record: scopeId: string * delivery: WebhookDelivery -> Async<Result<unit, string>>

    /// Recent deliveries for one subscription, newest first, capped
    /// at `limit`. Used by the admin UI's per-subscription history
    /// view. Returns `[]` for unknown subscriptions.
    abstract ListRecent: scopeId: string * subscriptionId: Guid * limit: int -> Async<WebhookDelivery list>

    /// Drop deliveries older than `olderThan`. Called by the
    /// dispatcher's periodic retention pass. `scopeId = None` prunes
    /// across every scope (used by the background sweep);
    /// `Some scopeId` prunes one scope (used when a subscription is
    /// deleted, to clean up its rows immediately).
    abstract Prune: scopeId: string option * olderThan: DateTime -> Async<unit>