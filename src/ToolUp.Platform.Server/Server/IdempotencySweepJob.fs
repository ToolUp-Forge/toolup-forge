// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Server

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 164 — BlobIdempotencyStore TTL sweep job ─────────────────────
//
// Phase 69f shipped `BlobIdempotencyStore` with TTL enforced **lazily on
// read** — correct for replay semantics, but dead `_platform`-container
// entries accumulate on write-heavy idempotent endpoints (a key written
// once and never read again is never reclaimed). This `IJobScheduler`-backed
// sweep enumerates the idempotency container and deletes entries whose
// envelope TTL has elapsed, closing the documented Phase 69f.C deferral.
//
// **Opt-in, default off (GP 11 / GP 13).** A deployment that doesn't add
// `IdempotencySweep.declaration` keeps lazy-TTL-on-read and pays nothing —
// no handler registered, no enumeration cost. Only meaningful with a
// `BlobIdempotencyStore` composed (the in-process default evicts on its own
// FIFO cap).
//
// **Stateless between runs (GP 12 rule 4).** Every tick re-reads the full
// blob set from the store; nothing is cached across runs. **Concurrency-
// safe:** the sweep only deletes entries it has just read as past-expiry, and
// the lazy read path re-checks expiry on every `TryGet`, so a sweep racing a
// live read/replay can never strand a still-valid entry — last-write-wins on
// `Delete` is benign (deleting an already-deleted blob is `Ok`).
//
// **Forward-reference note.** The sweep is a separate file from `Idempotency.fs`
// (the phase's stated key file) because `IJobHandler` / `ScheduledJobDeclaration`
// compile *after* `Idempotency.fs` in the Server tier; an `IJobHandler` impl
// can't live there. The shared `BlobIdempotencyLayout` (container / prefix /
// envelope-expiry parse) stays in `Idempotency.fs` so the two never drift.

/// Phase 164 — sweeps TTL-expired entries from a `BlobIdempotencyStore`'s
/// container. `container` defaults to the store's `_platform`. A
/// parameter-less `IJobHandler` (no payload); all state comes from the
/// store each run.
type IdempotencySweepJob(blobStorage: IBlobStorage, ?container: string) =
    let container = defaultArg container BlobIdempotencyLayout.DefaultContainer

    interface IJobHandler with
        member _.Execute(_ctx: JobContext) : Async<JobResult> = async {
            try
                let! names = blobStorage.List(container, BlobIdempotencyLayout.BlobPrefix)
                let nowTicks = DateTimeOffset.UtcNow.UtcTicks

                for name in names do
                    match! blobStorage.Download(container, name) with
                    | Ok bytes ->
                        match BlobIdempotencyLayout.tryReadExpiryTicks bytes with
                        | Some expiry when nowTicks >= expiry ->
                            // Best-effort delete. A racing live read/replay
                            // re-checks expiry itself, so removing an
                            // already-expired entry never strands a valid one;
                            // deleting an already-deleted blob is `Ok`.
                            do! blobStorage.Delete(container, name) |> Async.Ignore
                        | _ ->
                            // Live entry, or a corrupt / non-envelope blob —
                            // leave it (the lazy path owns corrupt-on-read).
                            ()
                    | Error _ -> ()

                return JobResult.Success
            with ex ->
                // Transient (storage hiccup) — retried per the job's RetryPolicy;
                // a partial sweep is safe (the rest is reclaimed next run).
                return JobResult.TransientFailure ex.Message
        }

/// Compose helpers for the Phase 164 idempotency TTL sweep.
module IdempotencySweep =
    /// Reserved `IJobScheduler` handler name for the sweep.
    [<Literal>]
    let HandlerName = "_platform.idempotency.sweep"

    /// Construct the sweep handler over the deployment's idempotency blob
    /// storage (the same `IBlobStorage` the `BlobIdempotencyStore` writes to).
    let handler (blobStorage: IBlobStorage) : IJobHandler =
        IdempotencySweepJob(blobStorage) :> IJobHandler

    /// Opt-in scheduled-job declaration for proactive idempotency-entry
    /// reclamation. Add it to the deployment's scheduled-job declarations to
    /// enable; **default off** — lazy-TTL-on-read remains the correctness
    /// path, so a deployment that doesn't add this is byte-for-byte unchanged
    /// (GP 11 / GP 13). `cron` is a 5-field expression (e.g. `"0 * * * *"` —
    /// hourly). Only meaningful with a `BlobIdempotencyStore` composed.
    let declaration (cron: string) (blobStorage: IBlobStorage) : ScheduledJobDeclaration =
        ScheduledJobDeclaration.create HandlerName (handler blobStorage) (Trigger.CronTrigger cron)