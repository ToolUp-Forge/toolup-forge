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

    interface IBlobStorage with
        member _.Upload(container, blobName, content) =
            runner.Run(fun () -> inner.Upload(container, blobName, content))

        member _.Download(container, blobName) =
            runner.Run(fun () -> inner.Download(container, blobName))

        member _.DownloadRange(container, blobName, offset, length) =
            runner.Run(fun () -> inner.DownloadRange(container, blobName, offset, length))

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