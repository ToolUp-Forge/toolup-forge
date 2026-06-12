// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Admin-UI payloads ───────────────────────────────────────────

/// Payload for `CreateSubscription`. The handler resolves `ScopeId`,
/// `CreatedBy`, `CreatedAt`, `Status`, and `ConsecutiveFailures` from
/// the request context — the admin only chooses target, secret, and
/// event-type filter.
///
/// `Secret` is expected to be a high-entropy string (≥ 32 chars). The
/// admin UI offers a "generate" button that emits a base64 32-byte
/// random string; nothing on the server enforces a minimum length yet
/// — that's a follow-up tied to a future secret-rotation API.
type CreateWebhookRequest = {
    [<PiiSafe>]
    TargetUrl: string
    Secret: string
    [<PiiSafe>]
    EventTypes: string list
}

/// Outcome of a manual test-fire from the admin UI. The dispatcher
/// posts a synthetic `WebhookTest` event to the chosen subscription
/// and reports the immediate first-attempt result — retries are not
/// run on test-fires (an admin who wants to see the retry path can
/// stop their receiving service and trigger a real event).
type WebhookTestResult = {
    DeliveryId: Guid
    StatusCode: int option
    LatencyMs: int64
    Error: string option
}

// ─── Fable.Remoting API surface ─────────────────────────────────

/// Admin-facing Fable.Remoting API for managing webhook subscriptions
/// and inspecting delivery history. Auto-injected by `compose` in all
/// non-Anonymous modes — Anonymous deployments have no persistent
/// scope to attach subscriptions to, so the entire surface short-
/// circuits to an error there.
///
/// Scope isolation: the handler resolves the caller's scope from
/// `AccessContext` and never reads or writes another scope. A Team A
/// admin cannot see, modify, or delete Team B subscriptions or
/// deliveries — the registry is keyed on `ScopeId`, derived server-
/// side.
///
/// Write gating in Team / MultiTeam modes: Owner / Admin only
/// (`TeamRoles.canWriteTeamConfig`). Member is read-only. Individual
/// and AuthenticatedEphemeral users own their user-scope subscriptions
/// outright.
///
/// Returned `WebhookSubscription` records are masked via
/// `WebhookSubscription.maskSecret` before crossing the wire — the
/// secret value is only ever returned by `CreateSubscription` so the
/// admin can copy it into the receiving service. Once created, the
/// secret is write-only (rotation requires delete + recreate).
type IWebhookApi = {
    /// Create a new subscription in the caller's scope. Returns the
    /// full `WebhookSubscription` *with the unmasked secret* — the
    /// only response in the API that does so. The admin UI displays
    /// the secret once, behind a "I have copied this" confirmation,
    /// then refreshes the list (which re-fetches with masking).
    [<RequiresClaim "scope">]
    [<Audit "Custom:WebhookSubscriptionCreated">]
    CreateSubscription: CreateWebhookRequest -> Async<Result<WebhookSubscription, string>>

    /// Enumerate the caller's scope's subscriptions, secrets masked.
    /// Order is undefined; the admin UI sorts by `CreatedAt` desc.
    [<RequiresClaim "scope">]
    ListSubscriptions: unit -> Async<Result<WebhookSubscription list, string>>

    /// Single subscription by id, secret masked. Returns `Error` when
    /// the subscription does not exist in the caller's scope (cross-
    /// scope reads are impossible — the handler scopes the lookup).
    [<RequiresClaim "scope">]
    GetSubscription: Guid -> Async<Result<WebhookSubscription, string>>

    /// Flip subscription status. The admin UI uses this to pause
    /// (during incident response) or to re-enable an auto-disabled
    /// subscription after fixing the receiver. Setting `Active` from
    /// `Disabled` resets `ConsecutiveFailures` to 0.
    [<RequiresClaim "scope">]
    [<Audit "PolicyChanged">]
    UpdateStatus: Guid * WebhookStatus -> Async<Result<unit, string>>

    /// Delete the subscription and its delivery log. Idempotent —
    /// succeeds when the subscription does not exist. Emits a
    /// `WebhookSubscriptionDeleted` audit event.
    [<RequiresClaim "scope">]
    [<Audit "Custom:WebhookSubscriptionDeleted">]
    DeleteSubscription: Guid -> Async<Result<unit, string>>

    /// Post a synthetic `WebhookTest` event to the subscription's
    /// target *now*, bypassing the dispatcher's queue. Returns the
    /// first-attempt outcome — no retries on test-fires.
    [<RequiresClaim "scope">]
    TestFire: Guid -> Async<Result<WebhookTestResult, string>>

    /// Recent delivery log rows for the subscription, newest first,
    /// capped at 100 entries. Older entries are pruned by the
    /// dispatcher per `WebhookRetention` (server-side knob, not
    /// admin-tunable).
    [<RequiresClaim "scope">]
    ListDeliveries: Guid -> Async<Result<WebhookDelivery list, string>>
}

module WebhookApi =
    /// Fable.Remoting endpoint prefix. Matches the pattern used by
    /// `PlatformApi`, `IConfigApi`, etc.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"