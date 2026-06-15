namespace ToolUp.Stripe.Server

open System.Text
open System.Threading
open ToolUp.Platform.BlobStorage

/// **Production-ready** durable webhook idempotency store backed by
/// `IBlobStorage`. Unlike the in-memory default, dedup state persists
/// across process restarts and — given a shared backing store — spans
/// instances, closing the multi-instance gap Stripe's at-least-once
/// delivery exposes.
///
/// **Stateless between calls (GP 12, rule 4).** A claim is derived
/// purely from the event id + the backing store; the only in-memory
/// state is a process-local mutex used for atomicity, which a restarted
/// instance reconstructs harmlessly. An Orleans grain / Akka actor
/// implementing the same seam would behave identically.
///
/// **Atomicity.** `TryClaim` is serialised within the process by an
/// async gate and persisted via an `Exists` → `Upload` pair on the
/// backing store. For STRICT cross-instance exactly-once the backing
/// `IBlobStorage` must provide read-after-write consistency — the
/// shipped cloud providers (S3 / Azure / GCS) do. The residual
/// cross-instance window (two replicas both passing `Exists` before
/// either `Upload`s) is bounded by Stripe's redelivery interval and is
/// closed entirely by a backing store with conditional-create
/// semantics. A billing handler is expected to be idempotent regardless
/// (the store is defence-in-depth, not the sole guard).
///
/// **Fail-open to processing.** If the claim write fails, `TryClaim`
/// returns `true` (process the event) rather than `false` (drop it) —
/// at-least-once + an idempotent handler tolerates a reprocess, but a
/// dropped billing event is unrecoverable.
///
/// **Scope.** `container` is an `IBlobStorage` scope-derived name; the
/// reserved `_platform` container is the natural home for
/// deployment-level webhook dedup (it is not tenant-scoped state). Keys
/// are namespaced under `stripe-webhook-idem/` so they never collide
/// with other `_platform` blobs.
type DurableIdempotencyStore(blobStorage: IBlobStorage, container: string) =
    // Async-friendly mutual exclusion for the Exists→Upload critical
    // section. Within one process this guarantees a single winner; see
    // the type doc for the cross-instance consistency contract.
    let gate = new SemaphoreSlim(1, 1)

    /// Blob key for an event id. Stripe ids are `evt_…` (alphanumeric +
    /// underscore); any out-of-range char is folded to `_` so a
    /// malformed id can never escape the key namespace.
    let keyFor (eventId: string) : string =
        let safe =
            eventId
            |> Seq.map (fun c ->
                if System.Char.IsLetterOrDigit c || c = '_' || c = '-' then
                    c
                else
                    '_')
            |> Seq.toArray
            |> System.String

        sprintf "stripe-webhook-idem/%s" safe

    /// Construct with the reserved `_platform` container — the common
    /// case for deployment-level webhook dedup.
    new(blobStorage: IBlobStorage) = DurableIdempotencyStore(blobStorage, "_platform")

    interface IWebhookIdempotencyStore with
        member _.TryClaim(eventId: string) : Async<bool> = async {
            do! gate.WaitAsync() |> Async.AwaitTask

            try
                let key = keyFor eventId
                let! exists = blobStorage.Exists(container, key)

                if exists then
                    return false
                else
                    let! written = blobStorage.Upload(container, key, Encoding.UTF8.GetBytes "1")

                    match written with
                    | Ok _ -> return true
                    // Write failure → fail open: process the event
                    // rather than drop it. A redelivery reprocesses,
                    // which an idempotent handler tolerates.
                    | Error _ -> return true
            finally
                gate.Release() |> ignore
        }