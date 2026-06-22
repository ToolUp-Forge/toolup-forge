namespace ToolUp.Webhooks.Server

open System.Collections.Concurrent
open ToolUp.Webhooks

/// Dev / single-instance `IWebhookDedupStore`. Holds claimed
/// `(kind, eventId)` pairs in process memory — correct for a single
/// node, NOT durable across restarts or shared across replicas. A
/// multi-instance deployment supplies a durable implementation
/// (e.g. over `IBlobIdempotencyStore` once that substrate ships) with
/// no change to the route or handler code.
type InMemoryWebhookDedupStore() =
    let seen = ConcurrentDictionary<string, byte>()

    interface IWebhookDedupStore with
        member _.TryClaim(kind, eventId) = async { return seen.TryAdd(kind + ":" + eventId, 0uy) }