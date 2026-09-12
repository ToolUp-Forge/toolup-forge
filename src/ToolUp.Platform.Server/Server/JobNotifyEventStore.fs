module ToolUp.Platform.JobNotifyEventStore

open ToolUp.Platform

// ─── JobNotifyEventStore ──────────────────────────────────────────
//
// `IEventStore` decorator that fires `IJobScheduler.NotifyEventWritten`
// after every successful `Write` to the inner store. This is what
// makes `Trigger.OnEvent` jobs auto-fire — the scheduler doesn't
// poll the event store, modules emit events through their normal
// `IEventStore` dependency, and the wrapper bridges them across.
//
// Same idiom as `HookedEventStore` (Phase 6d webhooks). Stacking the
// two decorators is intentional: a write goes through both hooks
// (notify scheduler + dispatch webhooks) before returning.
//
// **Scheduler reference is optional and may resolve to `None`
// transiently during compose-time construction.** The cell pattern
// avoids a chicken-and-egg cycle — the scheduler depends on the
// event store for its own emissions, and registering the scheduler
// in DI happens in the same compose pass that builds the wrapper.
// Once compose populates the cell, every subsequent `Write`
// dispatches OnEvent triggers normally.
//
// Scheduler emission self-feedback IS supported: a `JobCompleted`
// event emitted by the scheduler can fire an `OnEvent("JobCompleted")`
// job in the same scope. Callers who don't want this register their
// follow-up jobs with a more specific `EventType`.
//
// **Phase 598 — trigger-cursor advance.** When the deployment opts
// into `ServerConfig.EventTriggerCatchUp`, compose hands this
// decorator the shared `JobTriggerWatermark`; after the scheduler
// notify returns, the wrapper advances the scope's in-memory cursor
// past `evt` (the decorator holds the full `ModuleEvent`, so the
// cursor records the event's own `OccurredAt` — no wall-clock skew).
// The `None`-scheduler compose window deliberately does NOT advance:
// those events were never notified, and leaving them past the cursor
// is exactly what lets the scheduler's startup catch-up scan recover
// them.
//
// **Phase 9u — position 300**, the outermost link of the canonical
// chain: an `OnEvent` trigger fires only after the audit replicator has
// enqueued the event and the webhook dispatch hook has been handed it.
// See `EventStoreChain`.
type JobNotifyEventStore
    (
        inner: IEventStore,
        schedulerLookup: unit -> IJobScheduler option,
        ?watermark: JobTriggerWatermark.JobTriggerWatermark
    ) =
    interface IEventStoreDecorator with
        member _.DecoratorName = "JobNotifyEventStore"
        member _.DecoratorPosition = EventStoreChain.JobNotifyPosition

        member _.DecoratorPurpose =
            "Calls IJobScheduler.NotifyEventWritten after every successful write, which is what makes Trigger.OnEvent jobs auto-fire, and advances the shared trigger watermark when EventTriggerCatchUp is on."

        member _.InnerStore = inner

    interface IEventStore with
        member _.Write(evt) = async {
            do! inner.Write evt

            match schedulerLookup () with
            | Some scheduler ->
                do! scheduler.NotifyEventWritten(evt.ScopeId, evt.EventType, evt.Id)
                watermark |> Option.iter _.Advance(evt)
            | None -> ()
        }

        member _.ReadAll(scopeId) = inner.ReadAll(scopeId)

        member _.ReadByType(scopeId, eventType) = inner.ReadByType(scopeId, eventType)

        member _.ReadBySource(scopeId, sourceModule) =
            inner.ReadBySource(scopeId, sourceModule)

        member _.ListScopes() = inner.ListScopes()

        member _.Erase(scopeId, subjectUserId, policy, dryRun) =
            inner.Erase(scopeId, subjectUserId, policy, dryRun)