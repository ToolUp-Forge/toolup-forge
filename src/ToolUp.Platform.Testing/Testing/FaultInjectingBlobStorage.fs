// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Testing.FaultInjectingBlobStorage

open System.Collections.Generic
open ToolUp.Platform.BlobStorage

// ─── Fault-injecting IBlobStorage decorator ──────────────────────────
//
// A test-only decorator over any `IBlobStorage` that can corrupt reads,
// tear or drop writes, widen the read→write window, and interleave a
// racing writer — the adversarial conditions the fail-closed blob
// read-modify-write paths (fail-closed decode + quarantine, the
// single-instance `MarkUsed` / KB `index.json` locks) are supposed to
// survive.
//
// It is inert by construction: with no faults registered it is a pure
// pass-through to the inner store (so a store re-run through the wrapper
// behaves identically). Faults are opt-in per (container, blobName), so a
// test can seed valid data with faults disarmed, then arm exactly the
// blob under test — leaving sibling blobs pristine, which is how the
// "existing valid entries survive" property is demonstrated.
//
// Determinism: byte-corruption is a fixed XOR pattern keyed by the seed,
// so a given (seed, input) always produces the same corrupt bytes. The
// suite constructs the decorator with a fixed seed — reproducible, not
// flaky. Timing knobs (`WidenReadWriteGap`) only make an *unlocked* RMW
// more likely to lose a write; the correctness assertions never depend on
// a race landing a particular way, so a widened gap cannot flake a
// passing store.

/// A single fault the decorator can apply to a matched blob operation.
/// Download-side faults corrupt what a reader sees; upload-side faults
/// corrupt (or drop) what reaches the backing store.
type BlobFault =
    /// Return only the first `keepBytes` of a downloaded blob — a
    /// truncated read (short read / partial fetch).
    | DownloadTruncate of keepBytes: int
    /// Return the downloaded blob with a deterministic subset of its
    /// bytes flipped — a garbled read.
    | DownloadCorrupt
    /// Replace the downloaded payload wholesale with `payload` — arbitrary
    /// garbage at rest.
    | DownloadGarbage of payload: byte[]
    /// Persist only the first `keepBytes` of an uploaded blob — a torn
    /// write. The write returns `Ok` (the store believes it succeeded);
    /// the corruption surfaces on the *next* read.
    | UploadPartial of keepBytes: int
    /// Return `Ok` from an upload without persisting anything — a silently
    /// lost write (the shape an ETag conditional-write would reject, but
    /// the interim single-instance store cannot detect).
    | UploadDrop
    /// Fail an upload with `message` — a hard write error surfaced to the
    /// caller.
    | UploadFail of message: string

/// When a registered fault fires, relative to how many times its
/// (container, blobName) predicate has matched.
type FaultTrigger =
    /// Fire on every matching operation.
    | Always
    /// Fire only on the `n`-th (1-based) matching operation — lets a test
    /// let the first read/write through and fault a later one.
    | OnlyCall of n: int

type private FaultRule = {
    Match: string -> string -> bool
    Fault: BlobFault
    Trigger: FaultTrigger
    Hits: int ref
}

/// Wraps `inner`, applying registered faults. `seed` controls the
/// deterministic byte-corruption pattern (default fixed) so the suite is
/// reproducible.
type FaultInjectingBlobStorage(inner: IBlobStorage, ?seed: int) =
    let seedValue = defaultArg seed 20260619
    let gate = obj ()
    let rules = List<FaultRule>()
    let mutable readWriteGapMs = 0

    let mutable onBeforeUpload: string -> string -> Async<unit> =
        fun _ _ -> async { return () }

    /// Deterministic garble: flip one byte in three under a seed-keyed
    /// XOR. Enough structural damage to make JSON undecodable while
    /// staying a pure function of (seed, input).
    let corruptBytes (bytes: byte[]) : byte[] =
        if isNull bytes || bytes.Length = 0 then
            [| 0xFFuy; 0x00uy; 0xFFuy |]
        else
            let copy = Array.copy bytes

            for i in 0 .. copy.Length - 1 do
                if (i + seedValue) % 3 = 0 then
                    copy[i] <- copy[i] ^^^ 0xA5uy

            copy

    let clamp n (bytes: byte[]) =
        Array.sub bytes 0 (max 0 (min n bytes.Length))

    /// Pick the first registered fault whose side matches (download vs
    /// upload), whose predicate matches, and whose trigger fires on this
    /// hit. Increments the rule's hit counter for every side+predicate
    /// match so `OnlyCall` counts attempts, not just fires.
    let selectFault (isUpload: bool) (container: string) (blobName: string) : BlobFault option =
        lock gate (fun () ->
            rules
            |> Seq.tryPick (fun rule ->
                let sideMatches =
                    match rule.Fault with
                    | DownloadTruncate _
                    | DownloadCorrupt
                    | DownloadGarbage _ -> not isUpload
                    | UploadPartial _
                    | UploadDrop
                    | UploadFail _ -> isUpload

                if sideMatches && rule.Match container blobName then
                    rule.Hits.Value <- rule.Hits.Value + 1

                    let fires =
                        match rule.Trigger with
                        | Always -> true
                        | OnlyCall n -> rule.Hits.Value = n

                    if fires then Some rule.Fault else None
                else
                    None))

    // ─── Fault configuration (test-side, mutation-based like the fakes) ──

    /// Arm a fault on an exact (container, blobName). `trigger` defaults to
    /// `Always`.
    member _.FaultBlob(container: string, blobName: string, fault: BlobFault, ?trigger: FaultTrigger) =
        lock gate (fun () ->
            rules.Add {
                Match = (fun c n -> c = container && n = blobName)
                Fault = fault
                Trigger = defaultArg trigger Always
                Hits = ref 0
            })

    /// Arm a fault on any operation whose (container, blobName) satisfies
    /// `predicate`. `trigger` defaults to `Always`.
    member _.FaultWhere(predicate: string -> string -> bool, fault: BlobFault, ?trigger: FaultTrigger) =
        lock gate (fun () ->
            rules.Add {
                Match = predicate
                Fault = fault
                Trigger = defaultArg trigger Always
                Hits = ref 0
            })

    /// Disarm every registered fault. The decorator becomes a pure
    /// pass-through again (timing knobs are left untouched).
    member _.ClearFaults() = lock gate (fun () -> rules.Clear())

    /// Sleep `ms` inside `Download` after fetching, widening the
    /// read→(process)→write window so an *unlocked* read-modify-write is
    /// far more likely to lose a concurrent write. A correctly-locked
    /// store is unaffected.
    member _.WidenReadWriteGap(ms: int) = readWriteGapMs <- ms

    /// Run `hook container blobName` immediately before each upload — a
    /// seam for interleaving a racing writer / modelling a
    /// write-after-change.
    member _.InjectBeforeUpload(hook: string -> string -> Async<unit>) = onBeforeUpload <- hook

    interface IBlobStorage with
        // Phase 741 — no compose faults are modelled yet, so the call
        // passes through to the inner store and reports its capability
        // rather than masking it.
        member _.CanComposeFrom = inner.CanComposeFrom

        member _.ComposeFrom(container, targetBlobName, sourceBlobNames) =
            inner.ComposeFrom(container, targetBlobName, sourceBlobNames)

        member this.Erase(container, prefix, policy, dryRun) =
            // Erase composes over List + Delete/Upload; route it through the
            // inner store so the erasure algorithm is exercised fault-free.
            inner.Erase(container, prefix, policy, dryRun)

        member _.Upload(container, blobName, content) = async {
            do! onBeforeUpload container blobName

            match selectFault true container blobName with
            | Some(UploadPartial n) -> return! inner.Upload(container, blobName, clamp n content)
            | Some UploadDrop -> return Ok blobName
            | Some(UploadFail msg) -> return Error msg
            | _ -> return! inner.Upload(container, blobName, content)
        }

        member _.Download(container, blobName) = async {
            let! result = inner.Download(container, blobName)

            if readWriteGapMs > 0 then
                do! Async.Sleep readWriteGapMs

            match result with
            | Error e -> return Error e
            | Ok bytes ->
                match selectFault false container blobName with
                | Some(DownloadTruncate n) -> return Ok(clamp n bytes)
                | Some DownloadCorrupt -> return Ok(corruptBytes bytes)
                | Some(DownloadGarbage payload) -> return Ok payload
                | _ -> return Ok bytes
        }

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        // Ranged reads pass through fault-free: the registered fault
        // shapes (truncate / corrupt / garbage) model whole-blob
        // Download outcomes. Extend with range-specific faults if a
        // test ever needs them.
        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)