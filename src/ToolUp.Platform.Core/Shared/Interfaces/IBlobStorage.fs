// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.BlobStorage

open System

/// Summary metadata for a blob. Returned by `GetMetadata` so callers can
/// make cache / freshness / size decisions without downloading the
/// content. Providers populate all fields where the backing store
/// exposes them; `ContentType` is `None` when the store does not track
/// it (e.g. local filesystem, which infers from extension at download
/// time only).
type BlobMetadata = {
    /// Size of the blob in bytes.
    Size: int64
    /// Last modification time in UTC. For append-only stores, the time
    /// the blob was written.
    LastModified: DateTime
    /// MIME type when the backing store tracks it; `None` otherwise.
    ContentType: string option
}

/// Abstraction for blob/object storage.
/// Implementations may target Azure Blob Storage, AWS S3, GCS, or local filesystem.
///
/// **Scope discipline (non-negotiable).** Container names MUST be derived from a
/// resolved `StorageScope`, not from user input. Known scope prefixes:
///   - `user-{userId}` — individual user scope
///   - `team-{teamId}` — team scope
///   - `session-{sessionId}` — anonymous/ephemeral scope
///   - `_platform` — reserved SDK-level container (teams, memberships, permissions)
///
/// Callers that construct container names from any other source are bypassing team
/// isolation and will be rejected in code review. Implementations
/// SHOULD log or audit container names that do not match these prefixes so accidental
/// violations surface early.
///
/// **`ValidatedContainer` newtype — decision: not adopted (resolved 2026-05-15).**
/// We deferred, to the cloud-storage review, whether to make
/// `container` a newtype constructible only from a resolved `StorageScope` so the
/// invariant above is type-enforced rather than docstring-enforced. The
/// cloud-storage providers have shipped (Azure / S3 / GCS) and the call is: keep
/// `container: string`. Rationale:
///   1. The invariant is already enforced at its single source — the server-side
///      `IStorageScopeResolver` is the only place a scope container is minted;
///      user input never reaches it. A newtype would re-assert at ~70 call sites
///      a property that one chokepoint already guarantees.
///   2. Legitimate non-scope containers exist (`_platform`, audit-sink target
///      containers, health-probe containers). A "only from `StorageScope`"
///      constructor would need escape hatches for these, diluting the guarantee
///      it exists to provide.
///   3. The cloud-storage providers all treat `container` as an opaque
///      scope-derived prefix; none needed container-validation logic — the
///      runtime check buys little over the resolver-level invariant.
///   4. The contract tests already assert the scope-parameter property
///      across every implementation.
/// Net: the newtype is a breaking ripple through every caller and implementation
/// for a docstring-grade invariant already covered upstream and by contract tests
/// (added weight must justify itself). Revisit only if a future provider
/// cannot enforce isolation at the resolver boundary.
type IBlobStorage =
    /// Upload content to a blob. `container` must be a scope-derived name (see type docs).
    abstract Upload: container: string * blobName: string * content: byte[] -> Async<Result<string, string>>
    /// Download a blob's content. `container` must be a scope-derived name (see type docs).
    abstract Download: container: string * blobName: string -> Async<Result<byte[], string>>
    /// Delete a blob. `container` must be a scope-derived name (see type docs).
    /// Idempotent — deleting a non-existent blob returns `Ok`.
    abstract Delete: container: string * blobName: string -> Async<Result<unit, string>>
    /// List blobs in a container with optional prefix. `container` must be a scope-derived name (see type docs).
    abstract List: container: string * prefix: string -> Async<string list>
    /// Return whether a blob exists at the given path. Cheaper than
    /// `Download` when callers only need existence (cache-probe, idempotent
    /// upload checks, file-manager reload). `container` must be a
    /// scope-derived name (see type docs).
    abstract Exists: container: string * blobName: string -> Async<bool>
    /// Return summary metadata for a blob without downloading its content.
    /// `Error` when the blob does not exist or the backing store failed.
    /// `container` must be a scope-derived name (see type docs).
    abstract GetMetadata: container: string * blobName: string -> Async<Result<BlobMetadata, string>>

    /// Phase 455 — bounded ranged read. Download at most `length` bytes
    /// starting at byte `offset`, without materialising the rest of the
    /// blob (cloud implementations issue a native HTTP range request;
    /// the local implementation seeks). `container` must be a
    /// scope-derived name (see type docs).
    ///
    /// **Semantics (contract-tested across every implementation):**
    ///   - `offset < 0` or `length <= 0` → `Error` (invalid arguments).
    ///   - Missing blob → `Error` (parity with `Download`).
    ///   - `offset >= size` → `Ok [||]` (past-EOF clamp; providers'
    ///     416 responses map here, distinguished from not-found).
    ///   - Otherwise the bytes `[offset, min(offset + length, size))` —
    ///     the result may be SHORTER than `length` when the range runs
    ///     off the end; concatenating consecutive ranges byte-equals
    ///     the full `Download`.
    ///
    /// **No open-ended range.** "Offset to EOF" is deliberately not
    /// expressible — callers combine `GetMetadata` (`Size`) with a
    /// capped-chunk loop, so no implementation is ever forced to
    /// materialise a whole object to satisfy a range. Portability
    /// audit (GP 12): identity by value (`byte[]`, not a stream —
    /// also what keeps retry decorators safe: a returned buffer can
    /// be re-fetched, a partially-consumed stream cannot), async at
    /// boundary, failure as `Result` data, stateless.
    ///
    /// Custom implementations without a native range primitive can
    /// delegate to `BlobStorage.downloadRangeViaDownload` (download-
    /// then-slice — correct, not cheap).
    ///
    /// **Encryption caveat:** the `EncryptedBlobStorage` decorator
    /// refuses ranged reads (whole-blob AES-GCM — a mid-blob
    /// ciphertext range is undecryptable); callers reading encrypted
    /// content use `Download`.
    abstract DownloadRange:
        container: string * blobName: string * offset: int64 * length: int -> Async<Result<byte[], string>>

    /// Phase 9h — GDPR Article 17 erasure surface. Erase (or redact)
    /// every blob in `container` whose name starts with `prefix`,
    /// per `policy`:
    ///
    ///  - `HardDelete` — delete every matching blob.
    ///  - `Tombstone` / `RetainPerCompliance` — overwrite each
    ///    matching blob's content with `Erasure.TombstoneMarker`
    ///    while keeping its name. Blob bytes are opaque, so the
    ///    store cannot structurally redact individual fields — it
    ///    replaces the whole payload and keeps the key so the
    ///    erasure is discoverable. Both policies behave identically
    ///    here (raw blob storage is not the audit/event fabric, so
    ///    neither refuses).
    ///
    /// **Prefix-scoped.** The caller supplies the subject prefix;
    /// deployments that store per-subject blobs under a name prefixed
    /// by the subject id get them erased. A blank `prefix` is a
    /// zero-count no-op (it would otherwise match every blob in the
    /// container — a catastrophic over-erasure).
    ///
    /// **Scope isolation (GP 4).** `container` is scope-derived (see
    /// the type-level scope-discipline note); another scope's
    /// container is structurally unreachable.
    ///
    /// `dryRun = true` counts matching blobs without mutating.
    /// Default implementations delegate to `BlobStorage.eraseByPrefix`
    /// (List + Delete / Upload over this same interface). Portability
    /// audit (GP 12): identity by value, async at boundary, failure
    /// as `ErasureError`, stateless, single-container, precision
    /// declared above.
    abstract Erase:
        container: string * prefix: string * policy: ErasurePolicy * dryRun: bool ->
            Async<Result<ErasureSummary, ErasureError>>

/// Phase 455 — shared download-then-slice fallback for
/// `IBlobStorage.DownloadRange`. Correct against the contract semantics
/// but NOT cheap: it materialises the whole blob and slices. In-tree
/// test doubles and custom implementations without a native range
/// primitive delegate here; production implementations override with a
/// native seek / HTTP range request so bounded reads stay bounded.
let downloadRangeViaDownload
    (store: IBlobStorage)
    (container: string)
    (blobName: string)
    (offset: int64)
    (length: int)
    : Async<Result<byte[], string>> =
    async {
        if offset < 0L then
            return Error "DownloadRange: offset must be non-negative"
        elif length <= 0 then
            return Error "DownloadRange: length must be positive"
        else
            let! result = store.Download(container, blobName)

            match result with
            | Error e -> return Error e
            | Ok bytes ->
                if offset >= int64 bytes.Length then
                    return Ok Array.empty
                else
                    let start = int offset
                    let count = min length (bytes.Length - start)
                    return Ok(Array.sub bytes start count)
    }

/// Shared default for `IBlobStorage.Erase`. Every concrete
/// implementation delegates to this — the algorithm is the same
/// across Azure / S3 / GCS / local / in-memory because it is
/// expressed purely in terms of `List` + `Delete` / `Upload`.
/// Kept out of the interface so implementations stay free to
/// override with a backend-native bulk delete later.
let eraseByPrefix
    (store: IBlobStorage)
    (container: string)
    (prefix: string)
    (policy: ErasurePolicy)
    (dryRun: bool)
    : Async<Result<ErasureSummary, ErasureError>> =
    async {
        if Erasure.isBlankSubject prefix then
            return
                Result.Ok {
                    HandlerName = "blobs"
                    RecordsAffected = 0
                    Note = Some "blank prefix — no-op (would otherwise match every blob)"
                }
        else
            let! names = store.List(container, prefix)

            if dryRun || List.isEmpty names then
                let verb =
                    match policy with
                    | ErasurePolicy.HardDelete -> "deleted"
                    | _ -> "tombstoned"

                return
                    Result.Ok {
                        HandlerName = "blobs"
                        RecordsAffected = names.Length
                        Note = Some(sprintf "%d blob(s) would be %s under %s/%s" names.Length verb container prefix)
                    }
            else
                let marker = System.Text.Encoding.UTF8.GetBytes Erasure.TombstoneMarker

                let! outcomes =
                    names
                    |> List.map (fun name -> async {
                        match policy with
                        | ErasurePolicy.HardDelete ->
                            let! r = store.Delete(container, name)

                            return
                                match r with
                                | Ok() -> true
                                | Error _ -> false
                        | ErasurePolicy.Tombstone
                        | ErasurePolicy.RetainPerCompliance ->
                            let! r = store.Upload(container, name, marker)

                            return
                                match r with
                                | Ok _ -> true
                                | Error _ -> false
                    })
                    |> Async.Parallel

                let succeeded = outcomes |> Array.filter id |> Array.length
                let failed = outcomes.Length - succeeded

                let verb =
                    match policy with
                    | ErasurePolicy.HardDelete -> "deleted"
                    | _ -> "tombstoned"

                let summary = {
                    HandlerName = "blobs"
                    RecordsAffected = succeeded
                    Note = Some(sprintf "%d blob(s) %s under %s/%s" succeeded verb container prefix)
                }

                if failed = 0 then
                    return Result.Ok summary
                else
                    return
                        Result.Error(
                            HandlerPartialFailure(
                                "blobs",
                                summary,
                                sprintf "%d of %d blob op(s) failed" failed outcomes.Length
                            )
                        )
    }
// ─── Phase 600 — conditional writes (the ETag seam) ──────────────────
//
// Optional capability interface for backends that support optimistic
// concurrency on blob writes. Deliberately NOT members on
// `IBlobStorage`: thirteen in-tree implementers (plus consumer-side
// custom stores) implement that interface, so new abstract members
// would be a breaking consumer-facing sweep. Backends that support
// conditional writes implement THIS interface alongside
// `IBlobStorage`; consumers probe with a type-test —
//
//     match blobStorage with
//     | :? IConditionalBlobStorage as cas -> // ETag-guarded path
//     | _ -> // fallback: per-key serialisation (the standing interim guard)
//
// ETags are OPAQUE per-provider tokens — a content hash locally, the
// native ETag / generation on cloud backends. Callers never parse
// them; the only valid operations are "carry the token from a
// `DownloadWithETag`" and "hand it back via `IfMatch`".

/// Precondition for a conditional upload.
type ETagCondition =
    /// Write only if the blob's current etag equals this token —
    /// the read-modify-write guard.
    | IfMatch of etag: string
    /// Write only if the blob does not exist — create-only semantics
    /// (lease/lock acquisition, first-writer-wins).
    | IfAbsent

/// Why a conditional upload was refused.
type ConditionalWriteError =
    /// The precondition failed. `currentETag = Some t` — the blob
    /// exists at `t` (stale `IfMatch`, or `IfAbsent` losing the
    /// create race); `None` — the blob is absent but `IfMatch`
    /// expected it.
    | ETagMismatch of currentETag: string option
    /// Infrastructure failure unrelated to the precondition.
    | ConditionalWriteFailure of message: string

/// Optimistic-concurrency capability over blob storage. Implementations
/// MUST make `UploadWithETag` atomic with respect to concurrent
/// conditional uploads on the same `(container, blobName)` — that
/// atomicity is the entire point of the seam. Scope discipline and
/// path rules match `IBlobStorage`.
type IConditionalBlobStorage =
    /// Download content together with the etag token to hand back via
    /// `IfMatch`. `Error` on absent blob or storage failure.
    abstract DownloadWithETag: container: string * blobName: string -> Async<Result<byte[] * string, string>>

    /// Conditionally upload. Returns the NEW etag on success, a typed
    /// `ConditionalWriteError` otherwise. A refused precondition
    /// (`ETagMismatch`) leaves the stored blob untouched.
    abstract UploadWithETag:
        container: string * blobName: string * content: byte[] * condition: ETagCondition ->
            Async<Result<string, ConditionalWriteError>>