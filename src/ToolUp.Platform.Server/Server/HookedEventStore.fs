module ToolUp.Platform.HookedEventStore

open ToolUp.Platform
open ToolUp.Platform.WebhookDispatcher

/// `IEventStore` decorator that fires `IWebhookDispatcher.Dispatch`
/// after every successful `Write` to the inner store. This is the
/// bridge between the audit log and outbound webhook delivery —
/// modules emit events through their normal `IEventStore` dependency,
/// the wrapper persists them, and the dispatcher fans them out to
/// any matching subscriptions.
///
/// Read paths pass through unchanged. The wrapper is registered as
/// `IEventStore` in DI; `compose` wires the inner (un-wrapped) store
/// into the dispatcher itself so dispatcher-emitted audit events
/// (`WebhookDeliveryFailed`, `WebhookSubscriptionAutoDisabled`) are
/// persisted but not re-dispatched. That avoids a feedback loop where
/// a failing alerting webhook would compound its own audit traffic.
/// Subscription create / update / delete audits originate in
/// `WebhookApiHandler` (which writes through the wrapper) so admins
/// *can* subscribe a webhook to those event types — the documented
/// alerting use case.
///
/// `Dispatch` is non-blocking — the underlying queue is bounded with
/// `BoundedChannelFullMode.DropWrite`, so a saturated dispatcher
/// drops the event and logs a Warn rather than back-pressuring the
/// caller. The hook never blocks `Write`.
type HookedEventStore(inner: IEventStore, dispatcher: IWebhookDispatcher) =
    interface IEventStore with
        member _.Write(evt) = async {
            do! inner.Write evt
            dispatcher.Dispatch evt
        }

        member _.ReadAll(scopeId) = inner.ReadAll(scopeId)

        member _.ReadByType(scopeId, eventType) = inner.ReadByType(scopeId, eventType)

        member _.ReadBySource(scopeId, sourceModule) =
            inner.ReadBySource(scopeId, sourceModule)

        member _.ListScopes() = inner.ListScopes()

        member _.Erase(scopeId, subjectUserId, policy, dryRun) =
            inner.Erase(scopeId, subjectUserId, policy, dryRun)