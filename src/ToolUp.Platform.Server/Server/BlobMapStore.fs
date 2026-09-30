// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Security.Cryptography
open ToolUp.Platform.BlobStorage

// ─── Phase 864 — the guarded read-modify-write helper ───────────────
//
// Every blob-backed store that keeps one document per blob (a user's
// membership list, a share-token claim, a knowledge-base index) mutates it
// the same way: read the blob, decode it, change it, write it back. Done
// naively that loop has three failure modes, and each has shipped:
//
//   * A read FAILURE read as EMPTY. `IBlobStorage.Download` reports a
//     missing blob and an infrastructure fault as the same `Error string`,
//     so a store that maps any error to "empty" writes empty-plus-one on a
//     transient fault — erasing every other entry the blob held.
//   * A decode failure read as empty — the same erasure, from bytes that
//     are present but unreadable.
//   * A lost update. Two writers read the same version and the second
//     write silently drops the first's change. An in-process lock fixes
//     this on one node and on no other.
//
// `BlobMapStore<'T>` is the one place that gets all three right, over the
// Phase 600 `IConditionalBlobStorage` seam (GP 12: interface-shaped over
// `IBlobStorage`, identity by value, retry budget as data):
//
//   read-with-ETag -> decode -> transform -> conditional write
//     -> bounded retry on a lost precondition (the transform is REPLAYED
//        over the fresh read, never merged)
//
//   * ABSENT is `None` — the empty state — and the first write carries
//     `IfAbsent`, so two creators also resolve by precondition. Absence is
//     established by `Exists`, probed BEFORE the read: a read that fails on
//     a blob that existed is `Unreadable`, a typed error, and NOTHING is
//     written (GP 9); a read that fails on a blob that did not exist is
//     absence. (Probed after the read instead, a blob a concurrent writer
//     created in between would read as unreadable.) If `Exists` itself is
//     wrong (it has no error channel), the `IfAbsent` guard still refuses to
//     overwrite a blob that is there.
//   * UNDECODABLE bytes are copied aside to a content-addressed sibling
//     (`<blob>.corrupt-<sha256 prefix>`, create-only, so re-reading the same
//     bad bytes quarantines once) and the operation fails with
//     `Quarantined`. The canonical blob is NEVER overwritten: its bytes stay
//     where they are for an operator to repair, and the store keeps failing
//     closed until they do.
//   * A backend WITHOUT conditional writes falls back to an unconditional
//     write, with one logged warning per store instance. The absent /
//     unreadable / corrupt rules still hold there; only the lost-update
//     guarantee is gone, which is what the warning says.
//
// The capability is CONFIRMED, not assumed. A type test on
// `IConditionalBlobStorage` is necessary but not sufficient: a forwarding
// decorator can implement the interface over an inner store that does not,
// and answer every conditional call with `ConditionalWriteFailure`. So the
// first operation of an instance asks one side-effect-free question the
// seam's contract answers exactly — `IfMatch` against a blob that does not
// exist, which a conditional backend refuses with `ETagMismatch None`
// without writing anything — and caches only a positive answer.
//
// The ETag seam's own precedent is `BlobPendingInviteStore` (Phase 5h),
// which runs this loop by hand over one map blob; it is unchanged.

/// How a value is carried as blob bytes. `Decode` returns `Error` rather
/// than throwing for bytes it cannot read; a throwing decoder is treated
/// the same way.
type BlobCodec<'T> = {
    Encode: 'T -> byte[]
    Decode: byte[] -> Result<'T, string>
}

/// Retry budget for `BlobMapStore`'s read-modify-write loop. A lost
/// precondition is retried up to `MaxRetries` further times, each after a
/// pause that starts at `InitialBackoff` and doubles.
type BlobMapStoreOptions = {
    /// Retries after the first attempt before the operation surfaces
    /// `BlobMapStoreError.Contended`. `0` means a single attempt.
    MaxRetries: int
    /// Pause before the first retry; each later retry doubles it.
    /// `TimeSpan.Zero` makes the loop retry without pausing.
    InitialBackoff: TimeSpan
}

/// Companion functions for `BlobMapStoreOptions`.
module BlobMapStoreOptions =

    /// Three retries at 100 / 200 / 400 ms — the Phase 5h budget.
    let defaults: BlobMapStoreOptions = {
        MaxRetries = 3
        InitialBackoff = TimeSpan.FromMilliseconds 100.0
    }

/// What a transform decided about the value it was handed.
[<RequireQualifiedAccess>]
type BlobUpdate<'T, 'R> =
    /// Persist `value`, then return `result`.
    | Write of value: 'T * result: 'R
    /// Persist nothing; return `result`. The operation is complete after
    /// the read (a no-op, or a domain refusal such as "already a member").
    | Keep of result: 'R

/// Why a guarded read or read-modify-write did not complete. Every case
/// guarantees the canonical blob was not written by this operation.
[<RequireQualifiedAccess>]
type BlobMapStoreError =
    /// The blob exists and could not be read (or its absence could not be
    /// established). Never read as empty.
    | Unreadable of blobName: string * reason: string
    /// The blob's bytes did not decode. They were copied to
    /// `quarantinedTo`; the canonical blob is untouched.
    | Quarantined of blobName: string * quarantinedTo: string * reason: string
    /// The write itself failed for a reason other than a lost precondition.
    | WriteFailed of blobName: string * reason: string
    /// The precondition was lost on every attempt of the retry budget.
    | Contended of blobName: string * attempts: int

/// Companion functions for `BlobMapStoreError`.
module BlobMapStoreError =

    /// One sentence naming the blob and the failure, for a store that
    /// surfaces errors as strings.
    let describe (error: BlobMapStoreError) : string =
        match error with
        | BlobMapStoreError.Unreadable(blob, reason) ->
            $"blob '{blob}' could not be read ({reason}); nothing was written"
        | BlobMapStoreError.Quarantined(blob, quarantine, reason) ->
            $"blob '{blob}' did not decode ({reason}); its bytes were copied to '{quarantine}' and it was not overwritten"
        | BlobMapStoreError.WriteFailed(blob, reason) -> $"write of blob '{blob}' failed: {reason}"
        | BlobMapStoreError.Contended(blob, attempts) ->
            $"blob '{blob}' changed under every one of {attempts} attempts; nothing was written"

/// Guarded read-modify-write of one document per blob over `IBlobStorage`,
/// conditional (ETag CAS) where the backend supports it. See the file
/// header for the absent / unreadable / corrupt / fallback rules.
type BlobMapStore<'T>(storage: IBlobStorage, codec: BlobCodec<'T>, logger: ILogger, options: BlobMapStoreOptions) =

    let maxRetries = max 0 options.MaxRetries

    let cas =
        match box storage with
        | :? IConditionalBlobStorage as c -> Some c
        | _ -> None

    // Benign races on both flags: the worst case is one extra probe or one
    // extra warning, never a wrong write.
    let mutable conditionalConfirmed = false
    let mutable fallbackWarned = false

    let backoffFor (retry: int) : TimeSpan =
        TimeSpan.FromTicks(options.InitialBackoff.Ticks * (1L <<< (retry - 1)))

    let decode (content: byte[]) : Result<'T, string> =
        try
            codec.Decode content
        with ex ->
            Error ex.Message

    /// The side-effect-free capability probe (see the file header).
    let confirmConditional (c: IConditionalBlobStorage) (container: string) (blobName: string) = async {
        if conditionalConfirmed then
            return true
        else
            let probe = blobName + ".etag-probe"

            let! answer = async {
                try
                    return! c.UploadWithETag(container, probe, [||], IfMatch "toolup-etag-probe")
                with ex ->
                    return Error(ConditionalWriteFailure ex.Message)
            }

            match answer with
            | Error(ETagMismatch _) ->
                conditionalConfirmed <- true
                return true
            | Error(ConditionalWriteFailure _) -> return false
            | Ok _ ->
                // A backend that ignored the precondition wrote the probe:
                // it is not conditional, and the probe must not linger.
                let! _ = storage.Delete(container, probe)
                return false
    }

    let warnFallback (container: string) (blobName: string) =
        if not fallbackWarned then
            fallbackWarned <- true

            logger.Warn(
                $"[BlobMapStore] {container}/{blobName}: the blob storage does not support conditional writes, so read-modify-write falls back to unconditional writes — concurrent writers on other nodes can lose an update. Use a backend implementing IConditionalBlobStorage for multi-node deployments."
            )

    let quarantineName (blobName: string) (content: byte[]) =
        let digest = Convert.ToHexString(SHA256.HashData content).ToLowerInvariant()
        $"{blobName}.corrupt-{digest.Substring(0, 16)}"

    /// Copy undecodable bytes aside — create-only where the backend can —
    /// and report `Quarantined`. The canonical blob is not touched.
    let quarantine (container: string) (blobName: string) (content: byte[]) (reason: string) = async {
        let target = quarantineName blobName content

        let! copied = async {
            try
                match cas with
                | Some c when conditionalConfirmed ->
                    match! c.UploadWithETag(container, target, content, IfAbsent) with
                    | Ok _
                    | Error(ETagMismatch _) -> return Ok()
                    | Error(ConditionalWriteFailure m) -> return Error m
                | _ ->
                    let! present = storage.Exists(container, target)

                    if present then
                        return Ok()
                    else
                        match! storage.Upload(container, target, content) with
                        | Ok _ -> return Ok()
                        | Error m -> return Error m
            with ex ->
                return Error ex.Message
        }

        match copied with
        | Ok() ->
            logger.Error(
                $"[BlobMapStore] {container}/{blobName} did not decode ({reason}); its bytes were copied to {target}. The blob was NOT overwritten — repair or remove it to restore writes.",
                None
            )
        | Error m ->
            logger.Error(
                $"[BlobMapStore] {container}/{blobName} did not decode ({reason}) and the quarantine copy to {target} failed ({m}). The blob was NOT overwritten.",
                None
            )

        return BlobMapStoreError.Quarantined(blobName, target, reason)
    }

    /// Whether the blob exists, asked BEFORE it is read. The order is the
    /// point: a read that fails on a blob that existed before the read is
    /// `Unreadable`; one that fails on a blob that did not is absence. Asked
    /// after a failed read instead, a blob a concurrent writer created in
    /// between would read as unreadable, and a concurrent create would fail
    /// rather than resolve by precondition.
    let probeExists (container: string) (blobName: string) = async {
        try
            let! exists = storage.Exists(container, blobName)
            return Ok exists
        with ex ->
            return Error(BlobMapStoreError.Unreadable(blobName, $"existence probe failed: {ex.Message}"))
    }

    let decoded (container: string) (blobName: string) (content: byte[]) = async {
        match decode content with
        | Ok value -> return Ok(Some value)
        | Error reason ->
            let! error = quarantine container blobName content reason
            return Error error
    }

    /// Read with the etag to CAS against (`None` etag = absent).
    /// `knownPresent` skips the existence probe on a retry that already
    /// learned the blob exists (a lost `IfAbsent` / stale `IfMatch`).
    let readConditional (c: IConditionalBlobStorage) (container: string) (blobName: string) (knownPresent: bool) = async {
        let! existed =
            if knownPresent then
                async { return Ok true }
            else
                probeExists container blobName

        match existed with
        | Error e -> return Error e
        | Ok existedBefore ->
            let! result = async {
                try
                    return! c.DownloadWithETag(container, blobName)
                with ex ->
                    return Error ex.Message
            }

            match result with
            | Ok(content, etag) ->
                match! decoded container blobName content with
                | Ok value -> return Ok(value, Some etag)
                | Error e -> return Error e
            | Error reason when existedBefore -> return Error(BlobMapStoreError.Unreadable(blobName, reason))
            | Error _ -> return Ok(None, None)
    }

    let readPlain (container: string) (blobName: string) = async {
        match! probeExists container blobName with
        | Error e -> return Error e
        | Ok existedBefore ->
            let! result = async {
                try
                    return! storage.Download(container, blobName)
                with ex ->
                    return Error ex.Message
            }

            match result with
            | Ok content -> return! decoded container blobName content
            | Error reason when existedBefore -> return Error(BlobMapStoreError.Unreadable(blobName, reason))
            | Error _ -> return Ok None
    }

    let updateConditional
        (c: IConditionalBlobStorage)
        (container: string)
        (blobName: string)
        (transform: 'T option -> BlobUpdate<'T, 'R>)
        =
        let rec attempt (retry: int) (knownPresent: bool) = async {
            match! readConditional c container blobName knownPresent with
            | Error e -> return Error e
            | Ok(current, etag) ->
                match transform current with
                | BlobUpdate.Keep result -> return Ok result
                | BlobUpdate.Write(value, result) ->
                    let condition =
                        match etag with
                        | Some tag -> IfMatch tag
                        | None -> IfAbsent

                    let! written = async {
                        try
                            return! c.UploadWithETag(container, blobName, codec.Encode value, condition)
                        with ex ->
                            return Error(ConditionalWriteFailure ex.Message)
                    }

                    match written with
                    | Ok _ -> return Ok result
                    | Error(ConditionalWriteFailure message) ->
                        return Error(BlobMapStoreError.WriteFailed(blobName, message))
                    | Error(ETagMismatch current) ->
                        if retry >= maxRetries then
                            logger.Warn(
                                $"[BlobMapStore] {container}/{blobName} lost the write precondition {retry + 1} time(s) in a row; surfacing Contended."
                            )

                            return Error(BlobMapStoreError.Contended(blobName, retry + 1))
                        else
                            let pause = backoffFor (retry + 1)

                            if pause > TimeSpan.Zero then
                                do! Async.Sleep pause

                            // `Some _` — the blob is there now (a peer wrote or
                            // created it), so a failed re-read is Unreadable.
                            return! attempt (retry + 1) current.IsSome
        }

        attempt 0 false

    let updatePlain (container: string) (blobName: string) (transform: 'T option -> BlobUpdate<'T, 'R>) = async {
        match! readPlain container blobName with
        | Error e -> return Error e
        | Ok current ->
            match transform current with
            | BlobUpdate.Keep result -> return Ok result
            | BlobUpdate.Write(value, result) ->
                warnFallback container blobName

                let! written = async {
                    try
                        return! storage.Upload(container, blobName, codec.Encode value)
                    with ex ->
                        return Error ex.Message
                }

                match written with
                | Ok _ -> return Ok result
                | Error message -> return Error(BlobMapStoreError.WriteFailed(blobName, message))
    }

    /// Store over `storage` with the default retry budget.
    new(storage: IBlobStorage, codec: BlobCodec<'T>, logger: ILogger) =
        BlobMapStore<'T>(storage, codec, logger, BlobMapStoreOptions.defaults)

    /// Read and decode one blob. `Ok None` = absent. A present blob that
    /// cannot be read is `Unreadable`; one that does not decode is
    /// quarantined and reported as `Quarantined`.
    member _.Read(container: string, blobName: string) : Async<Result<'T option, BlobMapStoreError>> =
        readPlain container blobName

    /// Guarded read-modify-write. `transform` is pure over the decoded
    /// value (`None` = absent) and may run more than once: on a lost
    /// precondition it is replayed over the fresh read. Nothing is written
    /// unless the read succeeded and the transform returned `Write`.
    member _.Update
        (container: string, blobName: string, transform: 'T option -> BlobUpdate<'T, 'R>)
        : Async<Result<'R, BlobMapStoreError>> =
        async {
            match cas with
            | Some c ->
                let! conditional = confirmConditional c container blobName

                if conditional then
                    return! updateConditional c container blobName transform
                else
                    return! updatePlain container blobName transform
            | None -> return! updatePlain container blobName transform
        }