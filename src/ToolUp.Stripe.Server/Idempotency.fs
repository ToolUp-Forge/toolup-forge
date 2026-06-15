namespace ToolUp.Stripe.Server

open System.Collections.Generic

/// Seam for webhook event-id deduplication.
///
/// Stripe delivers webhooks **at least once** — a network blip on the
/// `200` acknowledgement causes redelivery, so the same event id can
/// arrive more than once. The handler claims each event id through this
/// seam; a replay that loses the claim short-circuits to `200` without
/// re-invoking the consumer handler.
///
/// **Single fused operation (`TryClaim`).** A separate
/// `HasSeen` + `Record` pair leaves a check-then-record race a
/// distributed store must close atomically; `TryClaim` returns whether
/// THIS caller won the claim, so the atomicity lives in one method the
/// implementation owns.
///
/// Six-rule portability audit (GP 12) — fully expanded in
/// [Phase 142](../../../ToolUp-Diametrical/roadmap/phases/142-stripe-webhook-idempotency-store.md):
///   1. Identity by value      — the event id is a `string`.
///   2. Async at every boundary — `TryClaim` returns `Async<bool>`.
///   3. Retry/supervision as data — no callbacks; the bool is the result.
///   4. Stateless between calls — a claim is derived only from the
///                                event id + backing store; nothing is
///                                assumed to survive in memory between
///                                calls (the durable impl honours this).
///   5. No cross-shard ordering — dedup is per-event-id; no ordering
///                                across event ids is promised.
///   6. Precision at lower bound — n/a (no time semantics).
type IWebhookIdempotencyStore =
    /// Atomically claim `eventId`. Returns `true` when THIS caller won
    /// the claim (first time the id is seen); `false` when the id was
    /// already claimed (a replay).
    abstract TryClaim: eventId: string -> Async<bool>

/// **Dev-only** in-memory LRU idempotency store — the registered
/// default. Dedup state lives in this process only: it is lost on
/// restart and does not span instances. A multi-instance deployment
/// composes a durable store (see
/// [Phase 142](../../../ToolUp-Diametrical/roadmap/phases/142-stripe-webhook-idempotency-store.md)'s
/// `DurableIdempotencyStore`) instead.
///
/// Bounded to `capacity` entries (default 10,000); the oldest claim is
/// evicted when the bound is exceeded, so a long-running process does
/// not grow without limit. Eviction means an event id older than the
/// last `capacity` distinct ids could be reprocessed — acceptable for
/// the dev default; the durable store has no such bound.
type InMemoryIdempotencyStore(?capacity: int) =
    let cap = defaultArg capacity 10_000
    let gate = obj ()
    let seen = Dictionary<string, LinkedListNode<string>>()
    let order = LinkedList<string>()

    interface IWebhookIdempotencyStore with
        member _.TryClaim(eventId: string) : Async<bool> = async {
            return
                lock gate (fun () ->
                    if seen.ContainsKey eventId then
                        false
                    else
                        let node = order.AddLast eventId
                        seen.[eventId] <- node

                        if seen.Count > cap then
                            let oldest = order.First

                            if not (isNull oldest) then
                                order.RemoveFirst()
                                seen.Remove oldest.Value |> ignore

                        true)
        }