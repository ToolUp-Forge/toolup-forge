// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ResilientBlobStorage

open ToolUp.Platform.BlobStorage
open ToolUp.Platform.TransientFault

// ─── Phase 176 — IBlobStorage transient-fault decorator ──────────────
//
// Wraps any `IBlobStorage` and routes every method through a shared
// `TransientFaultRunner`. Pure decoration — no domain knowledge, no state
// of its own beyond the runner's breaker bookkeeping. With
// `TransientFaultPolicy.identity` the runner's fast path returns the
// inner `Async` unchanged, so the wrapped store is observably identical
// to `inner` (same results, same call count, no added latency — GP 11/13;
// proven by re-running `IBlobStorageContract` through this wrapper).
//
// Transient faults (classifier-positive thrown exceptions) retry per the
// policy's `BackoffSchedule`; the breaker opens after the threshold; a
// per-call timeout cancels a hung call. `Result.Error` domain outcomes
// (a missing-blob `Download`, an idempotent `Delete` of an absent blob)
// are *values*, not exceptions, so they flow straight through untouched —
// the decorator never reinterprets a domain error as a retryable fault.

/// `IBlobStorage` decorator applying a `TransientFaultPolicy` to every
/// method. Correlation/context rides the wrapped async chain unchanged
/// (GP 7) — the decorator only schedules `inner`'s calls, it does not
/// touch their payloads.
type ResilientBlobStorage(inner: IBlobStorage, policy: TransientFaultPolicy) =
    let runner = TransientFaultRunner policy

    // Phase 600 — conditional-write forwarding. Reads route through the
    // runner like every other method. Conditional UPLOADS deliberately
    // do NOT retry: after an ambiguous failure (timeout with the write
    // actually applied), a retry would observe its own write and report
    // a false `ETagMismatch` — the caller's read-retry loop is the
    // correct recovery, not a transport-level replay.
    interface IConditionalBlobStorage with
        member _.DownloadWithETag(container, blobName) =
            match inner with
            | :? IConditionalBlobStorage as cas -> runner.Run(fun () -> cas.DownloadWithETag(container, blobName))
            | _ -> async { return Error "underlying blob storage does not support conditional writes" }

        member _.UploadWithETag(container, blobName, content, condition) =
            match inner with
            | :? IConditionalBlobStorage as cas -> cas.UploadWithETag(container, blobName, content, condition)
            | _ -> async {
                return Error(ConditionalWriteFailure "underlying blob storage does not support conditional writes")
              }

    interface IBlobStorage with
        member _.Upload(container, blobName, content) =
            runner.Run(fun () -> inner.Upload(container, blobName, content))

        member _.Download(container, blobName) =
            runner.Run(fun () -> inner.Download(container, blobName))

        member _.DownloadRange(container, blobName, offset, length) =
            runner.Run(fun () -> inner.DownloadRange(container, blobName, offset, length))

        // Phase 741 — forward the inner store's declaration verbatim.
        // The decorator adds retries, never capability: answering
        // `true` over a store that cannot compose would put the media
        // library on a path guaranteed to refuse.
        member _.CanComposeFrom = inner.CanComposeFrom

        // Safe to retry precisely because the seam hands across NAMES
        // and a byte count, not a stream — a re-run re-reads the same
        // parts and re-commits the same target (the GP 12 note on the
        // member). A partially-committed multi-part upload is abandoned
        // by the implementation, not resumed by the decorator.
        member _.ComposeFrom(container, targetBlobName, sourceBlobNames) =
            runner.Run(fun () -> inner.ComposeFrom(container, targetBlobName, sourceBlobNames))

        member _.Delete(container, blobName) =
            runner.Run(fun () -> inner.Delete(container, blobName))

        member _.List(container, prefix) =
            runner.Run(fun () -> inner.List(container, prefix))

        member _.Exists(container, blobName) =
            runner.Run(fun () -> inner.Exists(container, blobName))

        member _.GetMetadata(container, blobName) =
            runner.Run(fun () -> inner.GetMetadata(container, blobName))

        member _.Erase(container, prefix, policy', dryRun) =
            runner.Run(fun () -> inner.Erase(container, prefix, policy', dryRun))

/// Apply the resilience decorator when the deployment opted in.
/// `NoResilience` returns `inner` un-wrapped — no decorator object in the
/// hot path at all, byte-for-byte identical to the bare store (GP 13).
/// Mirrors `ComposeEncryption.applyEncryptionDecorator`.
let applyStorageResilience (mode: ResilienceMode) (inner: IBlobStorage) : IBlobStorage =
    match mode with
    | NoResilience -> inner
    | WithResiliencePolicy policy -> ResilientBlobStorage(inner, policy) :> IBlobStorage