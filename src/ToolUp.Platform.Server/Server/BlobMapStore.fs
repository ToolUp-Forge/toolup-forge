// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Infra

open ToolUp.Platform

open System
open System.Security.Cryptography
open ToolUp.Platform.BlobStorage

// ─── The durable document store ─────────────────────────────────────
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
// `BlobDocumentStore<'T>` is the one place that gets all three right, over
// the `IConditionalBlobStorage` seam (GP 12: interface-shaped over
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

    /// Three retries at 100 / 200 / 400 ms.
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

/// Raised by a read whose signature has no error channel when the store
/// could not establish the document (`Unreadable` or `Quarantined`): the
/// operation is refused rather than answered as though the document were
/// absent or empty. Carries the store's verdict.
exception DocumentUnavailableException of error: BlobMapStoreError with
    override this.Message = BlobMapStoreError.describe this.error

/// Companion functions for `DocumentUnavailableException`.
module DocumentUnavailable =

    /// The value of a store read, or the refusal raised as
    /// `DocumentUnavailableException` for a caller with no `Result` to carry it.
    let orRaise (result: Result<'T, BlobMapStoreError>) : 'T =
        match result with
        | Ok value -> value
        | Error error -> raise (DocumentUnavailableException error)

    /// `work`'s outcome, with a raised `DocumentUnavailableException` folded
    /// into its `Error` channel as the store's description.
    let asError (work: Async<Result<'T, string>>) : Async<Result<'T, string>> = async {
        try
            return! work
        with DocumentUnavailableException error ->
            return Error(BlobMapStoreError.describe error)
    }

/// The name of one document inside its scope's container, rendered by the
/// owning store's layout function (`DocPath.ofString`). Its representation
/// is private, so a path is built in one place per store and never spliced
/// from a container string at a call site.
type DocPath = private DocPath of string

/// Companion functions for `DocPath`.
module DocPath =

    /// A document path: non-empty, `/`-delimited (the `IBlobStorage` blob-name
    /// rule), never rooted and never carrying `..` or a backslash. Raises
    /// `ArgumentException` otherwise: a path is a store's layout, so a bad one
    /// is a programming error its first test catches.
    let ofString (path: string) : DocPath =
        if String.IsNullOrWhiteSpace path then
            invalidArg (nameof path) "a document path is not empty"
        elif
            path.Contains '\\'
            || path.StartsWith '/'
            || path.Split('/') |> Array.contains ".."
        then
            invalidArg (nameof path) $"document path '{path}' is not a relative, '/'-delimited blob name"
        else
            DocPath path

    /// A blob name a backend's `List` returned, taken as it is.
    let internal ofBlobName (name: string) : DocPath = DocPath name

    /// The blob name.
    let value (DocPath path) : string = path

/// The bound on `BlobDocumentStore.List`'s concurrent reads.
module BlobDocumentStoreLimits =

    /// The degree a store passes when it has no reason to choose another.
    [<Literal>]
    let DefaultListDegree = 16

    /// The ceiling every requested degree is clamped to.
    [<Literal>]
    let MaxListDegree = 32

    /// `degree` clamped to 1..`MaxListDegree`.
    let clamp (degree: int) : int = degree |> max 1 |> min MaxListDegree

/// Codecs over the platform's one JSON options instance.
module BlobCodec =

    /// `'T` as UTF-8 JSON through `PlatformJson`; bytes that do not decode are
    /// `Error`, which the store quarantines.
    let json<'T> : BlobCodec<'T> = {
        Encode = ToolUp.Remoting.Json.SystemTextJson.PlatformJson.serialize<'T>
        Decode = ToolUp.Remoting.Json.SystemTextJson.PlatformJson.deserialize<'T>
    }

/// The platform's one durable document store: one document per blob, keyed
/// by the scope that owns it and a path inside that scope's container, read
/// and written under the three invariants in the file header (absence
/// established by `Exists` BEFORE the read, undecodable bytes quarantined and
/// never overwritten, the ETag capability confirmed by probe) with a bounded
/// retry on a lost precondition. A store that keeps documents in blobs is a
/// codec and a layout function over this type; it decides none of the three
/// for itself.
type BlobDocumentStore<'T>(storage: IBlobStorage, codec: BlobCodec<'T>, logger: ILogger, options: BlobMapStoreOptions) =

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
                match! storage.Delete(container, probe) with
                | Ok() -> ()
                | Error e ->
                    logger.Warn(
                        $"[BlobDocumentStore] {container}/{probe}: the capability-probe blob could not be deleted ({e}); a non-conditional answer is not cached, so the next Update re-probes and re-deletes it."
                    )

                return false
    }

    let warnFallback (container: string) (blobName: string) =
        if not fallbackWarned then
            fallbackWarned <- true

            logger.Warn(
                $"[BlobDocumentStore] {container}/{blobName}: the blob storage does not support conditional writes, so read-modify-write falls back to unconditional writes — concurrent writers on other nodes can lose an update. Use a backend implementing IConditionalBlobStorage for multi-node deployments."
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
                $"[BlobDocumentStore] {container}/{blobName} did not decode ({reason}); its bytes were copied to {target}. The blob was NOT overwritten — repair or remove it to restore writes.",
                None
            )
        | Error m ->
            logger.Error(
                $"[BlobDocumentStore] {container}/{blobName} did not decode ({reason}) and the quarantine copy to {target} failed ({m}). The blob was NOT overwritten.",
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
                                $"[BlobDocumentStore] {container}/{blobName} lost the write precondition {retry + 1} time(s) in a row; surfacing Contended."
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

    let update (container: string) (blobName: string) (transform: 'T option -> BlobUpdate<'T, 'R>) = async {
        match cas with
        | Some c ->
            let! conditional = confirmConditional c container blobName

            if conditional then
                return! updateConditional c container blobName transform
            else
                return! updatePlain container blobName transform
        | None -> return! updatePlain container blobName transform
    }

    let supportsConditionalWrites (container: string) (blobName: string) =
        match cas with
        | Some c -> confirmConditional c container blobName
        | None -> async.Return false

    /// Store over `storage` with the default retry budget.
    new(storage: IBlobStorage, codec: BlobCodec<'T>, logger: ILogger) =
        BlobDocumentStore<'T>(storage, codec, logger, BlobMapStoreOptions.defaults)

    // The container-string primitives `BlobMapStore` keeps for its existing
    // callers. New code names the scope by value through the typed members.
    member internal _.ReadAt(container: string, blobName: string) = readPlain container blobName

    member internal _.UpdateAt(container: string, blobName: string, transform: 'T option -> BlobUpdate<'T, 'R>) =
        update container blobName transform

    member internal _.SupportsConditionalWritesAt(container: string, blobName: string) =
        supportsConditionalWrites container blobName

    /// Read and decode one document. `Ok None` = absent. A present blob that
    /// cannot be read is `Unreadable`; one that does not decode is copied
    /// aside and reported as `Quarantined`. Never an empty value for either.
    member _.Read(scope: ScopeKey, path: DocPath) : Async<Result<'T option, BlobMapStoreError>> =
        readPlain (ScopeKey.container scope) (DocPath.value path)

    /// Guarded read-modify-write. `transform` is pure over the decoded value
    /// (`None` = absent) and may run more than once: on a lost precondition it
    /// is replayed over the fresh read. Nothing is written unless the read
    /// succeeded and the transform returned `Write`.
    member _.Update
        (scope: ScopeKey, path: DocPath, transform: 'T option -> BlobUpdate<'T, 'R>)
        : Async<Result<'R, BlobMapStoreError>> =
        update (ScopeKey.container scope) (DocPath.value path) transform

    /// Remove one document. Deleting an absent document is `Ok` (the
    /// `IBlobStorage.Delete` contract); a refused delete is `WriteFailed`.
    member _.Delete(scope: ScopeKey, path: DocPath) : Async<Result<unit, BlobMapStoreError>> = async {
        let blobName = DocPath.value path

        let! deleted = async {
            try
                return! storage.Delete(ScopeKey.container scope, blobName)
            with ex ->
                return Error ex.Message
        }

        return
            deleted
            |> Result.mapError (fun reason -> BlobMapStoreError.WriteFailed(blobName, reason))
    }

    /// Every document under `prefix` in the scope's container, each with its
    /// own verdict: a blob that cannot be read or decoded is an `Error` row
    /// naming it, never a dropped row, so a caller that needs every document
    /// refuses the operation and one that can show partial results says which
    /// are missing. A blob that disappears between the listing and its read is
    /// absent and is not reported, and so are the store's own quarantine and
    /// probe siblings. At most `degree` reads run at once, clamped
    /// to 1..`BlobDocumentStoreLimits.MaxListDegree`.
    member _.List
        (scope: ScopeKey, prefix: string, degree: int)
        : Async<(DocPath * Result<'T, BlobMapStoreError>) list> =
        async {
            let container = ScopeKey.container scope
            let! listed = storage.List(container, prefix)

            // The store's own siblings (a quarantine copy, a capability probe
            // left by a backend that ignored the precondition) are not
            // documents.
            let names =
                listed
                |> List.filter (fun name -> not (name.Contains ".corrupt-" || name.EndsWith ".etag-probe"))

            let! rows =
                names
                |> List.map (fun name -> async {
                    match! readPlain container name with
                    | Ok(Some value) -> return Some(DocPath.ofBlobName name, Ok value)
                    | Ok None -> return None
                    | Error e -> return Some(DocPath.ofBlobName name, Error e)
                })
                |> fun reads -> Async.Parallel(reads, maxDegreeOfParallelism = BlobDocumentStoreLimits.clamp degree)

            return rows |> Array.choose id |> Array.toList
        }

    /// Whether `Update` on this document runs as a conditional write: the
    /// backend implements `IConditionalBlobStorage` AND answers the
    /// side-effect-free capability probe the way a conditional backend must.
    /// `false` means `Update` falls back to an unconditional write.
    member _.SupportsConditionalWrites(scope: ScopeKey, path: DocPath) : Async<bool> =
        supportsConditionalWrites (ScopeKey.container scope) (DocPath.value path)

/// Guarded read-modify-write of one document per blob, addressed by a
/// container string: the shape the first adopters were written against, kept
/// over `BlobDocumentStore<'T>` with the same members. New code uses the
/// document store and names its scope by value.
type BlobMapStore<'T>(storage: IBlobStorage, codec: BlobCodec<'T>, logger: ILogger, options: BlobMapStoreOptions) =

    let docs = BlobDocumentStore<'T>(storage, codec, logger, options)

    /// Store over `storage` with the default retry budget.
    new(storage: IBlobStorage, codec: BlobCodec<'T>, logger: ILogger) =
        BlobMapStore<'T>(storage, codec, logger, BlobMapStoreOptions.defaults)

    /// Read and decode one blob. `Ok None` = absent. A present blob that
    /// cannot be read is `Unreadable`; one that does not decode is
    /// quarantined and reported as `Quarantined`.
    member _.Read(container: string, blobName: string) : Async<Result<'T option, BlobMapStoreError>> =
        docs.ReadAt(container, blobName)

    /// Whether `Update` on this blob runs as a conditional write (see
    /// `BlobDocumentStore.SupportsConditionalWrites`).
    member _.SupportsConditionalWrites(container: string, blobName: string) : Async<bool> =
        docs.SupportsConditionalWritesAt(container, blobName)

    /// `Read` of one blob in a scope's container (`ScopeKey.container`).
    member this.Read(scope: ScopeKey, blobName: string) : Async<Result<'T option, BlobMapStoreError>> =
        this.Read(ScopeKey.container scope, blobName)

    /// `Update` of one blob in a scope's container (`ScopeKey.container`).
    member this.Update
        (scope: ScopeKey, blobName: string, transform: 'T option -> BlobUpdate<'T, 'R>)
        : Async<Result<'R, BlobMapStoreError>> =
        this.Update(ScopeKey.container scope, blobName, transform)

    /// Guarded read-modify-write. `transform` is pure over the decoded
    /// value (`None` = absent) and may run more than once: on a lost
    /// precondition it is replayed over the fresh read. Nothing is written
    /// unless the read succeeded and the transform returned `Write`.
    member _.Update
        (container: string, blobName: string, transform: 'T option -> BlobUpdate<'T, 'R>)
        : Async<Result<'R, BlobMapStoreError>> =
        docs.UpdateAt(container, blobName, transform)