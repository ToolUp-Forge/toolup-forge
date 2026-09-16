module KnowledgeBase.ServerIngestionStatusStream

open System.Collections.Generic
open SharedTypes
open KnowledgeBase.ServerIndexStorage

// ─── Phase 69c.tail D — ingestion status as a typed stream ────────
//
// The KB client learns that a document finished ingesting by asking:
// `KnowledgeApi.GetStatus` every 2 s, per non-terminal document, until
// every one of them is terminal. The terminal transition is ALSO pushed
// through `INotificationChannel` (the published
// `"KnowledgeBase.IngestionStatus"` contract, which the AI side panel
// subscribes to), but everything between `Queued` and `Complete` — the
// `ExtractingText` flip and each `Embedding(processed, total)` step — is
// visible only to a poll.
//
// This is the same progress as a Phase 69c stream: a subscription to the
// status cache's change feed (`IngestionStatusFeed`), seeded with the
// status the cache holds at subscribe time, ending on the first terminal
// status. The ELEMENT TYPE is `IngestionStatus` itself — not a new
// envelope — deliberately: `IngestionStatus` is a published wire contract
// with eight domain cases a third party already decodes, and a stream
// that re-renders it into anything else would be a second contract to
// keep in step for no gain. A consumer of this stream parses each chunk
// with the parser it already has for `GetStatus`.
//
// **Why this is not the `JobHandle<'T>` shape (Phase 69i.I).** The forge
// TIDY-UP carries a decision bundle asking whether `UploadDocument` /
// `GetStatus` should move onto the typed job handle. It should not, and
// this is not a step towards it: a `JobStatus<'T>` has five cases where
// `IngestionStatus` has eight, so the port is information-destroying; the
// handle would duplicate the document id that `GetDocuments`,
// `DeleteDocument` and the blob layout all key on; and the notification
// key is published. A handle is also still POLLED — it replaces one poll
// with another — where this replaces the poll with a push. The two are
// not alternatives at the same seam, and only this one is in 69c.tail's
// scope.
//
// **What this is NOT: a mounted endpoint.** As with Phase 53's replay
// stream, this is a library surface. `GetStatus` is `[<AllowAnonymous>]`
// and scope-gated through its request deps, and a streaming method cannot
// carry a pre-flight attribute at all (the adapter refuses to start on
// one that does), so a composer that wants this on the wire declares a
// server-only record over its own per-request closure — the shape
// `AIStreamingApi` uses — and mounts it beside `knowledgeApi`:
//
// ```fsharp skip=fragment
// type KnowledgeStreamingApi = {
//     [<AllowAnonymous>]
//     StreamIngestionStatus: string -> IAsyncEnumerable<IngestionStatus>
// }
//
// let knowledgeStreamingApi (_ctx: HttpContext) : KnowledgeStreamingApi = {
//     StreamIngestionStatus = ingestionStatusStream
// }
// ```
//
// The client keeps polling until it opts in; the poll is not retired by
// this and is still the restart path (a cache miss falls back to the
// persisted index, which a subscription cannot).

/// True for an ingestion status that will not change again. The same set
/// the KB client's poll loop treats as terminal — a stream that ended on
/// a narrower set would hang on `UploadRejected` / `UnsupportedFormat` /
/// `OcrUnavailable`, each of which is an end state with nothing further
/// to report.
let isTerminalStatus (status: IngestionStatus) =
    match status with
    | IngestionStatus.Complete _
    | IngestionStatus.Failed _
    | IngestionStatus.UploadRejected _
    | IngestionStatus.UnsupportedFormat _
    | IngestionStatus.OcrUnavailable _ -> true
    | _ -> false

/// One document's ingestion status, as it changes.
///
/// Cold: nothing is subscribed until the stream is enumerated, and each
/// enumeration is an independent subscription. The first element is the
/// status the cache holds at that moment (so a consumer that arrives
/// mid-ingest is told where it is rather than waiting for the next
/// transition); every later element is a transition. The stream ends on
/// the first terminal status — including one delivered as the seed, so
/// subscribing to an already-finished document yields exactly one element
/// and completes.
///
/// A document the cache has never heard of (a bad id, or one whose status
/// was swept) produces no seed and no elements until something writes
/// one. That is deliberate: the cache is not the store, and answering
/// "unknown" out of a stream would require inventing a status the wire
/// contract does not have. A caller that needs the persisted fallback
/// asks `GetStatus` — which is exactly what it is for — before, or
/// instead of, subscribing.
///
/// The subscription is disposed when the stream is disposed, which the
/// dispatcher does when the connection ends, so an aborted consumer
/// leaves nothing behind.
let ingestionStatusStream (docId: string) : IAsyncEnumerable<IngestionStatus> =
    ToolUp.Remoting.Server.AsyncStream.fromSubscription isTerminalStatus (fun emit ->
        // Subscribe BEFORE reading the seed: the other order can drop a
        // transition that lands between the read and the subscribe, and a
        // dropped transition is unrecoverable. This order can instead
        // deliver the same status twice — harmless, since a status is a
        // state rather than an increment.
        let subscription = IngestionStatusFeed.subscribe docId emit

        match statusCache.TryGetValue docId with
        | true, current -> emit current
        | _ -> ()

        subscription)