// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Subscription ────────────────────────────────────────────────

/// Lifecycle state of a webhook subscription.
///
/// `Active`: dispatcher delivers matching events.
/// `Paused`: admin temporarily silenced delivery; resumes by setting
///   back to `Active`. Useful for incident response without losing
///   the subscription's history.
/// `Disabled`: the dispatcher auto-flipped after
///   `WebhookRetryPolicy.DisableAfterConsecutiveFailures` consecutive
///   dead-lettered deliveries. The admin must manually re-enable —
///   the subscription is presumed broken until proven otherwise.
[<RequireQualifiedAccess>]
type WebhookStatus =
    | Active
    | Paused
    | Disabled

/// A registered webhook target. One blob per subscription under
/// `_platform/webhooks/{scopeId}/subscriptions/{subscriptionId:N}.json`.
///
/// **Secret storage (Phase 6d.A — encryption at rest).** The signing
/// secret VALUE is never persisted on this record; it lives in
/// `ISecretStore` (encrypted at rest by whichever store is composed —
/// `EncryptedSecretStore` by default, cloud-KMS in production). The blob
/// holds only `SecretRef`, the store key
/// (`_platform/webhooks/{subscriptionId:N}.secret`). The dispatcher
/// resolves the ref to a value immediately before HMAC signing and never
/// caches it beyond the request.
///
/// `Secret` is the LEGACY inline plaintext, kept as an `option` for
/// backward-compat: `Some` only on a pre-6d.A blob that has not yet run
/// the migration (the dispatcher falls back to it, the migration moves it
/// into `ISecretStore`, and the preflight validator Errors on any
/// residual populated value), or transiently on a create / rotate
/// RESPONSE so the admin can copy the value once. `None` on every
/// migrated / freshly-created persisted blob. Server-side responses for
/// `List` / `GetSubscription` mask any inline value; only
/// `CreateSubscription` and `RotateSecret` reveal it (once).
///
/// `PreviousSecretRef` / `PreviousSecret` / `PreviousSecretExpiresAt`
/// carry the *grace-window* state for a rotated secret (Phase 235). On
/// rotation the new secret is written at `SecretRef` and the prior
/// current secret is copied to a `.secret.previous` store key referenced
/// by `PreviousSecretRef` until `PreviousSecretExpiresAt`, so the
/// dispatcher dual-signs deliveries (new + previous) during the window —
/// a receiver still configured with the old secret keeps verifying while
/// it updates, then the previous secret expires. `PreviousSecret` is the
/// legacy inline counterpart (same migration semantics as `Secret`);
/// both are `None` on a never-rotated / migrated subscription.
///
/// `EventTypes = []` means "all event types" — empty as wildcard
/// matches the event-store filter convention (no entries =
/// unrestricted).
///
/// `ConsecutiveFailures` is bookkeeping for the auto-disable threshold
/// — incremented on dead-letter, reset to 0 on successful delivery.
/// Persisted on the subscription record so a silo restart resumes
/// from the correct count (portability rule 4: stateless handlers
/// between invocations).
type WebhookSubscription = {
    SubscriptionId: Guid
    ScopeId: string
    TargetUrl: string
    /// Phase 6d.A — `ISecretStore` key for the current signing secret.
    /// Empty / null on a pre-6d.A blob not yet migrated (the dispatcher
    /// then falls back to the legacy inline `Secret`).
    SecretRef: string
    /// Legacy inline signing secret — see the type doc. `None` on every
    /// migrated / freshly-created persisted blob.
    Secret: string option
    EventTypes: string list
    Status: WebhookStatus
    CreatedBy: string
    CreatedAt: DateTime
    ConsecutiveFailures: int
    /// Phase 6d.A — `ISecretStore` key for the grace-window previous
    /// secret. `Some` only while a rotation grace window is open.
    PreviousSecretRef: string option
    /// Legacy inline previous secret — same migration semantics as
    /// `Secret`. `None` on migrated / fresh records.
    PreviousSecret: string option
    PreviousSecretExpiresAt: DateTime option
}

/// Phase 6d.A — `ISecretStore` reference conventions for webhook signing
/// secrets. The secret VALUE lives in `ISecretStore`; the subscription
/// blob carries only these deployment-namespaced keys. Webhook secrets
/// are deployment-wide (GP 4: keyed under the reserved `_platform`
/// scope), so `GetSecret` / `SetSecret` / `DeleteSecret` all pass
/// `Scope` as the scope and `keyOf ref` as the key.
module WebhookSecretRef =
    /// The reserved scope every webhook secret is stored under.
    [<Literal>]
    let Scope = "_platform"

    /// Persisted reference for a subscription's current signing secret.
    let current (subscriptionId: Guid) : string =
        sprintf "_platform/webhooks/%s.secret" (subscriptionId.ToString "N")

    /// Persisted reference for a subscription's grace-window previous
    /// secret (set during a rotation, cleared when the window closes).
    let previous (subscriptionId: Guid) : string =
        sprintf "_platform/webhooks/%s.secret.previous" (subscriptionId.ToString "N")

    /// The `ISecretStore` key for a persisted ref — the ref with the
    /// `_platform/` scope prefix stripped (the scope is passed separately
    /// to the store). Robust to a ref that already lacks the prefix.
    let keyOf (reference: string) : string =
        let prefix = Scope + "/"

        if not (String.IsNullOrEmpty reference) && reference.StartsWith prefix then
            reference.Substring prefix.Length
        else
            reference

module WebhookSubscription =
    /// Default grace window for a rotated signing secret. While the
    /// window is open the dispatcher dual-signs every delivery (new +
    /// previous secret) so a receiver still holding the old secret keeps
    /// verifying without missed deliveries; at window end the previous
    /// secret is dropped and only the new signature is emitted. 24h
    /// gives receivers a full business day to update their stored secret.
    /// This is the only rotation knob — rotation is otherwise immediate.
    let secretRotationGracePeriod = TimeSpan.FromHours 24.0

    /// Mask any residual inline secret material for outbound list/get
    /// responses. Post-migration both `Secret` and `PreviousSecret` are
    /// `None` (the values live in `ISecretStore`), so masking is a no-op;
    /// on a not-yet-migrated blob it masks the inline value, preserving
    /// its length so admin UIs can hint at "secret is set" without leaking
    /// it. The reference fields (`SecretRef` / `PreviousSecretRef`) are
    /// store keys, not secrets, and cross the wire unmasked.
    let maskSecret (sub: WebhookSubscription) : WebhookSubscription = {
        sub with
            Secret = sub.Secret |> Option.map (fun s -> String.replicate (max 1 s.Length) "*")
            PreviousSecret =
                sub.PreviousSecret
                |> Option.map (fun s -> String.replicate (max 1 s.Length) "*")
    }

    /// Does this subscription's filter accept this event type?
    /// `EventTypes = []` matches everything.
    let acceptsEventType (eventType: string) (sub: WebhookSubscription) : bool =
        List.isEmpty sub.EventTypes || List.contains eventType sub.EventTypes

    /// Apply a secret rotation as a pure transition on the REFERENCE
    /// fields (Phase 6d.A). `SecretRef` is set to `currentSecretRef` (a
    /// no-op for an already-migrated subscription; on a legacy blob being
    /// rotated for the first time it adopts the canonical ref) and the
    /// previous secret's store key becomes the grace-window
    /// `PreviousSecretRef` (valid until `graceExpiresAt`). Both inline
    /// legacy secrets are cleared — after a rotation every signing secret
    /// is resolved from `ISecretStore`. The subscription id is unchanged.
    /// The secret VALUES are moved in `ISecretStore` by the handler; this
    /// transition only tracks the refs + expiry.
    let withRotatedSecret
        (currentSecretRef: string)
        (previousSecretRef: string)
        (graceExpiresAt: DateTime)
        (sub: WebhookSubscription)
        : WebhookSubscription =
        {
            sub with
                SecretRef = currentSecretRef
                Secret = None
                PreviousSecretRef = Some previousSecretRef
                PreviousSecret = None
                PreviousSecretExpiresAt = Some graceExpiresAt
        }

    /// The `ISecretStore` refs a delivery at instant `now` should be
    /// signed with: always the current `SecretRef`, plus the grace-window
    /// `PreviousSecretRef` while unexpired. Once `PreviousSecretExpiresAt`
    /// has passed, only the current ref is returned. The dispatcher
    /// resolves these refs to values (with an inline-secret fallback for
    /// not-yet-migrated blobs) immediately before signing. Current-first,
    /// mirroring the prior `acceptedSecrets` order.
    let acceptedSecretRefs (now: DateTime) (sub: WebhookSubscription) : string list =
        match sub.PreviousSecretRef, sub.PreviousSecretExpiresAt with
        | Some prevRef, Some expiresAt when now < expiresAt -> [ sub.SecretRef; prevRef ]
        | _ -> [ sub.SecretRef ]

// ─── Retry policy ────────────────────────────────────────────────

/// Retry / dead-letter policy applied by `WebhookDispatcher`. Expressed
/// as data (portability rule 3) — no callbacks, no supervision objects,
/// so a future distributed dispatcher can read the same record without
/// inheriting framework semantics.
///
/// Backoff is exponential with `min(InitialBackoff * 2^(attempt-1), MaxBackoff)`.
/// `MaxAttempts` is inclusive — a value of 5 means up to 5 HTTP
/// requests per delivery before dead-lettering.
type WebhookRetryPolicy = {
    MaxAttempts: int
    InitialBackoff: TimeSpan
    MaxBackoff: TimeSpan
    DisableAfterConsecutiveFailures: int
}

module WebhookRetryPolicy =
    /// SDK defaults: 5 attempts, 30s initial backoff, 30min cap, auto-
    /// disable after 5 consecutive dead-letters. Tuned for typical
    /// SaaS webhook receivers (Slack, Zapier) which generally recover
    /// inside the 30min window.
    let defaults = {
        MaxAttempts = 5
        InitialBackoff = TimeSpan.FromSeconds 30.0
        MaxBackoff = TimeSpan.FromMinutes 30.0
        DisableAfterConsecutiveFailures = 5
    }

    /// Compute the delay before attempt `attempt` (1-indexed). Attempt
    /// 1 is the initial delivery and runs immediately — `delayFor 1`
    /// returns `TimeSpan.Zero`. Attempt 2 onwards uses exponential
    /// backoff capped at `MaxBackoff`.
    let delayFor (policy: WebhookRetryPolicy) (attempt: int) : TimeSpan =
        if attempt <= 1 then
            TimeSpan.Zero
        else
            let exponent = float (attempt - 1)
            let raw = policy.InitialBackoff.TotalMilliseconds * (2.0 ** exponent)
            let capped = min raw policy.MaxBackoff.TotalMilliseconds
            TimeSpan.FromMilliseconds capped

// ─── Delivery log ────────────────────────────────────────────────

/// Outcome of a single delivery attempt. Recorded once per HTTP
/// request the dispatcher makes (every retry produces a fresh
/// `WebhookDelivery` row — they share the same `EventId` but have
/// distinct `DeliveryId`s).
[<RequireQualifiedAccess>]
type WebhookDeliveryOutcome =
    /// HTTP 2xx response within the configured timeout.
    | Success of statusCode: int * latencyMs: int64
    /// Non-2xx response or transient transport failure. Eligible for
    /// retry per `WebhookRetryPolicy` until `MaxAttempts` is reached.
    | Failure of statusCode: int option * error: string * latencyMs: int64
    /// Terminal failure after the policy's `MaxAttempts` is exhausted.
    /// Recorded once per dead-lettered delivery and triggers a
    /// `WebhookDeliveryFailed` audit event.
    | DeadLettered of finalError: string

/// One row in the per-subscription delivery log. Persisted as one
/// blob under `_platform/webhooks/{scopeId}/deliveries/`. Date prefix
/// on the blob name enables cheap retention pruning by `olderThan`.
///
/// `EventId = None` for test-fires — they're surfaced in the log so
/// admins can verify wiring, but they don't reference a real event
/// in the audit store.
type WebhookDelivery = {
    DeliveryId: Guid
    SubscriptionId: Guid
    EventId: Guid option
    Attempt: int
    AttemptedAt: DateTime
    Outcome: WebhookDeliveryOutcome
}

// ─── Outbound HTTP body envelope ─────────────────────────────────

/// Body envelope POSTed to the subscription's `TargetUrl`. The
/// receiving service unwraps `event` to get the underlying
/// `ModuleEvent` payload. Serialised as plain camelCase JSON via
/// `System.Text.Json` — third-party endpoints aren't Fable clients,
/// so the DU-aware `FableConverters` shape is not appropriate
/// here. The persisted log uses `FableConverters` separately so
/// the admin UI sees the round-tripped DU.
type WebhookDeliveryPayload = {
    DeliveryId: Guid
    SubscriptionId: Guid
    Event: ModuleEvent
    DeliveredAt: DateTime
    Attempt: int
}

// ─── Event-type constants for SDK-emitted audit events ───────────

/// Event types written by the webhook subsystem itself. Exposed as
/// `[<Literal>]` so SDK consumers can subscribe to them, filter on
/// them in the audit log, and target them as `EventTypes` on a
/// webhook subscription (e.g. an admin alerting hook that listens
/// for `WebhookSubscriptionAutoDisabled`).
module WebhookEventTypes =
    [<Literal>]
    let SubscriptionCreated = "WebhookSubscriptionCreated"

    [<Literal>]
    let SubscriptionStatusChanged = "WebhookSubscriptionStatusChanged"

    [<Literal>]
    let SubscriptionDeleted = "WebhookSubscriptionDeleted"

    [<Literal>]
    let SubscriptionSecretRotated = "WebhookSubscriptionSecretRotated"

    /// Phase 6d.A — emitted once per dispatcher secret-resolve (the
    /// dispatcher reads the signing secret from `ISecretStore` before
    /// HMAC signing a delivery). Forensic completeness: the audit trail
    /// records every access to a subscription's signing material. Never
    /// carries the secret value — only the subscription id + ref.
    [<Literal>]
    let SecretAccessed = "WebhookSecretAccessed"

    [<Literal>]
    let DeliveryFailed = "WebhookDeliveryFailed"

    [<Literal>]
    let SubscriptionAutoDisabled = "WebhookSubscriptionAutoDisabled"

// ─── Phase 464 — cross-instance signing-secret rotation fanout ────
//
// `IWebhookRegistry.RotateSecret` moves the signing-secret VALUES in
// `ISecretStore` and rewrites the subscription's reference bookkeeping.
// Neither reaches a sibling instance. The dispatcher itself holds no
// subscription cache — it re-lists the scope per event and re-reads the
// secret per delivery — so the stale read is one layer down, in a
// CACHING `ISecretStore`: `FileSecretStore` memoises a scope's whole
// secret map on first read and evicts only on its OWN write. A rotation
// performed on instance A therefore leaves instance B signing (and any
// same-deployment receiver verifying) with the superseded secret for as
// long as B's process lives — there is no periodic refresh to wait for.
//
// Phase 464 closes that by publishing this envelope through
// `INotificationChannel` on `NotificationKind.PlatformReservedScope`
// after a successful `RotateSecret`. Every wired instance drops its
// cached secret material for the affected scope on receipt, so the next
// resolve reads the rotated value from the durable store.
//
// The propagation window is the active channel companion's fanout
// latency, NOT zero — minute-grain per the `INotificationChannel`
// precision contract. The in-process default channel reaches only the
// publishing process, which is exactly right for a single instance and
// is why the fanout is a harmless no-op there (GP 11 / GP 13).
//
// **Portability rule 5 (no cross-shard ordering) is satisfied** — cache
// invalidation is idempotent and order-insensitive. Two envelopes for
// the same scope, or envelopes for different scopes arriving in any
// order, converge on the same state: every instance has dropped its
// memoised copy and will re-read. No instance needs to observe a total
// order, so a distributed companion may fan out per-shard with no
// cross-shard sequencing promise.

/// Phase 464 — the cross-instance webhook signing-secret rotation
/// envelope. Published on `NotificationKind.PlatformReservedScope` under
/// `WebhookSecretRotatedNotification.NotificationKey` as a
/// `CustomNotification` whose payload is this record's JSON.
///
/// **Identity-by-value (portability rule 1).** Every field is a string,
/// a `Guid`, or an instant — no live handles and no secret VALUES, so an
/// instance can be a separate process, container, grain, or actor
/// without a signature change, and the envelope stays safe to log.
type WebhookSecretRotatedEnvelope = {
    /// Scope owning the rotated subscription. The receiving instance
    /// drops its cached secret material for exactly this scope.
    ScopeId: string
    /// Subscription whose signing secret was rotated.
    SubscriptionId: Guid
    /// `ISecretStore` key now holding the current signing secret. A
    /// reference, never the secret value.
    CurrentSecretRef: string
    /// `ISecretStore` key holding the grace-window previous secret, so a
    /// receiving instance can tell a rotation apart from a first-time
    /// secret assignment without re-reading the subscription blob.
    PreviousSecretRef: string
    /// When the grace window closes and the previous secret stops
    /// verifying. Carried so an instance can reason about the
    /// dual-signing window without a store round-trip.
    GraceExpiresAt: DateTime
    /// When the rotation was performed on the originating instance.
    /// Subtracting this from the observation time is the measured
    /// fanout window the timing contract promises at minute grain.
    RotatedAt: DateTimeOffset
    /// Instance the rotation originated on. Load-bearing: an instance
    /// that receives its OWN publish (which the in-process channel
    /// always does) must not act on it — the rotating instance already
    /// invalidated locally, and treating the echo as a sibling signal
    /// would make a single-instance deployment read as a working fanout.
    OriginReplicaId: string
}

/// Phase 464 — wire constants for the cross-instance signing-secret
/// rotation broadcast. Public so a distributed `INotificationChannel`
/// companion, or a deployment auditing its own fanout, can recognise the
/// topic without re-deriving the string.
module WebhookSecretRotatedNotification =
    /// `CustomNotification` key the rotation envelope travels under.
    /// Published on the cross-scope reserved bus
    /// (`NotificationKind.PlatformReservedScope`), the same convention
    /// `MembershipChanged` and the Phase 22b key-destruction broadcast
    /// use.
    [<Literal>]
    let NotificationKey = "_platform.webhooks.secret-rotated"