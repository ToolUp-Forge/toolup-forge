// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

/// Real-time notification channel — the transport abstraction that
/// every SDK subsystem uses to push scope-scoped events to subscribers.
/// Currently consumed by: SSE delivery to clients, AI tool completion
/// notices, RAG ingestion status, background jobs, AI-driven module
/// actions.
///
/// ## Scope-gated delivery
///
/// Every `Publish` takes a `scopeId`; handlers registered under a
/// different `scopeId` never see the notification. Scope matching is
/// structural (dictionary lookup), not a defensive filter — a bug in
/// the implementation that crossed scopes would be a team-isolation
/// breach, not a feature gap.
///
/// The `scopeId` is always resolved from the caller's authenticated
/// request — typically via `ScopeResolutionMiddleware` populating
/// `ctx.Items["ToolUp.StorageScope"]`. Callers must not accept a
/// `scopeId` parameter from an untrusted source (query string, client
/// body) without validating it against the authenticated context
/// first.
///
/// ## Handler semantics
///
/// Handlers are fire-and-forget from the publisher's perspective.
/// `Publish` returns once the notification has been accepted by the
/// channel transport — not necessarily once every subscriber has seen
/// it. In-process implementations may deliver synchronously (the
/// default `InMemoryNotificationChannel` does so inside `Publish`);
/// cross-process implementations (Redis pub/sub, NATS, Service Bus)
/// publish-and-forget and subscribers run in their own scheduler
/// context. Publisher-side errors cover transport acceptance only,
/// not delivery success — if a subscriber must confirm receipt, it
/// does so by emitting a follow-up notification.
///
/// Handler exceptions are caught, logged, and never propagated back
/// to the publisher. A misbehaving subscriber cannot prevent the
/// next subscriber (or the next publish) from running. Portability
/// rule 3: no callback-based supervision; retry and failure policy
/// live in the implementation, not in the interface.
///
/// ## Ordering
///
/// Delivery within a single `scopeId` is best-effort sequential (in
/// the default in-memory implementation, strict FIFO). No cross-scope
/// ordering is promised; distributed implementations (future Orleans,
/// Akka, Redis pub/sub) may deliver concurrent scopes in any order.
/// Callers that need causal ordering must encode it in the payload.
///
/// ## Precision
///
/// Delivery is near-real-time but not sub-second guaranteed. SSE
/// transport, network buffering, and scheduler behaviour can delay a
/// notification by multiple seconds under load. The lower bound on
/// any work that consumes this channel is `JobPrecision.Minute` —
/// code paths that must dispatch sub-minute (e.g. live-market ticks)
/// cannot use this transport. Interfaces that need
/// audit-grade or billing-grade timing must use a persistent store,
/// not this channel.
///
/// ## Lifecycle
///
/// Subscriptions are transient. The default implementation keeps them
/// in-process; a silo restart, process crash, or explicit `Unsubscribe`
/// call discards them. Callers that need durable subscriptions must
/// layer a persistent bookkeeping store on top.
type INotificationChannel =
    /// Hand `notification` to the channel for delivery to every
    /// subscriber currently registered for `scopeId`. Returns after
    /// the transport has accepted the publish — in-process channels
    /// may complete delivery before returning; cross-process channels
    /// typically return once the message is on the wire. Handler
    /// exceptions are swallowed after being logged and never surface
    /// to the publisher.
    abstract Publish: scopeId: string * notification: Notification -> Async<unit>

    /// Register a handler to receive every notification published
    /// under `scopeId`. Returns an opaque `NotificationSubscriptionId`
    /// that the caller retains to cancel via `Unsubscribe`. The
    /// handler is invoked synchronously from `Publish`; long-running
    /// work must be dispatched off-thread by the handler itself.
    ///
    /// The synchronous `(NotificationEnvelope -> unit)` callback is a
    /// documented exemption to portability rule 2 — the method itself
    /// is `Async<_>`, and per-item hot-path dispatchers stay
    /// synchronous by design.
    abstract Subscribe: scopeId: string * handler: (NotificationEnvelope -> unit) -> Async<NotificationSubscriptionId>

    /// Cancel a subscription. Idempotent — a second call for the same
    /// id (or a call for an id that never existed) is a no-op. After
    /// this returns, the handler will not be invoked again by the
    /// same channel instance.
    abstract Unsubscribe: subscriptionId: NotificationSubscriptionId -> Async<unit>