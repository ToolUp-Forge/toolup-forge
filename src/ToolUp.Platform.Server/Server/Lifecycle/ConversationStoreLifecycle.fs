module ToolUp.Platform.ConversationStoreLifecycle

open System
open ToolUp.Platform

// ─── Phase 54d — first-party conversation-store lifecycle hook ───────
//
// On `OnDeprovisioned`, hard-erases every conversation in the
// offboarded scope so the AI conversation history a tenant accumulated
// doesn't outlive the tenant. Resolves the active `IConversationStore`
// from DI per call (stateless between invocations, GP 12 rule 4):
//   * `IConversationStore` present (`ConversationStore =
//     EnabledConversationStore _`) — enumerate the scope's conversation
//     headers (`ListByScope`), then drive `Erase(scopeId, subject,
//     HardDelete, dryRun = false)` once per distinct `CreatedBy`. The
//     eraser is subject-keyed (Phase 53's per-DSR contract), so covering
//     every author in the scope hard-deletes every conversation —
//     including any whose author is not a current team member, which the
//     subject-resolved `data-erasure` hook would miss.
//   * no store registered — `Skipped` (conversation persistence not
//     enabled).
//
// This is core-tier (conversation substrate lives in
// `ToolUp.Platform.Server`, not a companion), so it registers alongside
// the other four first-party hooks in `ComposeTenantLifecycle`. It is
// deliberately *complementary* to `data-erasure`: that hook erases a
// resolved subject set's records across every `IErasureHandler`; this
// one guarantees a scope-wide conversation purge regardless of authorship.
//
// **Idempotency.** Re-running after a successful purge finds an empty
// `ListByScope`, so it is a clean `Completed` no-op. `Erase` under
// `HardDelete` is itself idempotent (a second run matches nothing).
//
// `OnProvisioned` is a no-op `Skipped`: conversations are created on
// demand by the AI handler, so provisioning has nothing to do.

type ConversationStoreLifecycle(services: IServiceProvider) =
    interface ITenantLifecycle with
        member _.Name = "conversation-store"

        member _.OnProvisioned(_scopeId, _actorUserId) = async {
            return
                LifecycleHookResult.Skipped
                    "no provisioning action — conversations are created on demand by the AI handler"
        }

        member _.OnDeprovisioned(scopeId, _actorUserId) = async {
            match services.GetService(typeof<IConversationStore>) with
            | :? IConversationStore as store ->
                let! conversations = store.ListByScope scopeId

                // Distinct authors in the scope. The eraser is
                // subject-keyed, so erasing every author hard-deletes
                // every conversation in scope. Blank authors are skipped
                // (the substrate treats a blank subject as a zero-count
                // no-op anyway — the same over-erasure guard the other
                // `IErasureHandler` participants apply).
                let subjects =
                    conversations
                    |> List.map _.CreatedBy
                    |> List.filter (String.IsNullOrWhiteSpace >> not)
                    |> List.distinct

                let errors = ResizeArray<string>()

                for subject in subjects do
                    let! result = store.Erase(scopeId, subject, ErasurePolicy.HardDelete, false)

                    match result with
                    | Ok _ -> ()
                    | Error err -> errors.Add(ErasureError.toMessage err)

                if errors.Count > 0 then
                    return LifecycleHookResult.Failed(String.Join("; ", errors))
                else
                    return LifecycleHookResult.Completed
            | _ ->
                return
                    LifecycleHookResult.Skipped
                        "no IConversationStore registered (conversation persistence not enabled)"
        }

/// Construct the first-party conversation-store lifecycle hook. Resolves
/// the active `IConversationStore` from `services` on every call.
let create (services: IServiceProvider) : ITenantLifecycle =
    ConversationStoreLifecycle(services) :> ITenantLifecycle