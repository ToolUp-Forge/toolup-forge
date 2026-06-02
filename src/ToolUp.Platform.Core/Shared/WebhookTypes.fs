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
/// `Secret` is sensitive — server-side responses for `List` /
/// `GetSubscription` mask it (`***`). Only `CreateSubscription` returns
/// it so the admin can copy it into the receiving service. Rotation is
/// not currently supported — admins delete and recreate to rotate
/// (deferred follow-up).
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
    Secret: string
    EventTypes: string list
    Status: WebhookStatus
    CreatedBy: string
    CreatedAt: DateTime
    ConsecutiveFailures: int
}

module WebhookSubscription =
    /// Mask the secret for outbound list/get responses. The masked
    /// representation preserves the secret's length so admin UIs can
    /// hint at "secret is set" without leaking the value.
    let maskSecret (sub: WebhookSubscription) : WebhookSubscription = {
        sub with
            Secret = String.replicate (max 1 sub.Secret.Length) "*"
    }

    /// Does this subscription's filter accept this event type?
    /// `EventTypes = []` matches everything.
    let acceptsEventType (eventType: string) (sub: WebhookSubscription) : bool =
        List.isEmpty sub.EventTypes || List.contains eventType sub.EventTypes

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
    let DeliveryFailed = "WebhookDeliveryFailed"

    [<Literal>]
    let SubscriptionAutoDisabled = "WebhookSubscriptionAutoDisabled"