// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AssetStore

/// Server-side substrate for uploading and retrieving image
/// assets — what differentiates this from `IBlobStorage` is
/// per-asset records, mandatory alt-text-at-upload validation,
/// and on-demand derivative generation cached by content-hash.
/// The default implementation (`DefaultAssetStore`) wraps
/// `IBlobStorage` for originals + derivative cache and emits
/// `AssetUploaded` / `AssetDeleted` audit events through
/// `IAuditLog`.
///
/// **Six portability rules** (per `CLAUDE.md`):
///
///   1. **Identity by value.** Every method takes / returns
///      `AssetId` (string-backed value type), `string` slugs,
///      `AssetRecord` (record of primitives + `DateTimeOffset`).
///      No `IBlobReference` / `Stream` handles persisted across
///      calls. ✓
///   2. **Async at every boundary.** Every method is `Async<_>`. ✓
///   3. **Retry + supervision as data.** Errors surface as
///      `AssetUploadError` / `AssetDerivativeError` /
///      `AssetDeleteError` discriminated unions. No
///      `exn` callback parameters, no fire-and-forget
///      sinks. ✓
///   4. **Stateless between invocations.** Implementations
///      MUST NOT hold per-upload state between calls. The
///      default impl writes the record blob synchronously
///      before returning so a process restart after `Upload`
///      cannot lose the record. ✓
///   5. **No cross-shard ordering promises.** `List` returns
///      records in `UploadedAt` descending — a deterministic
///      shape derived from the input set, not a per-shard
///      arrival order. ✓
///   6. **Precision at the lower bound.** Wall-clock at
///      seconds granularity (`DateTimeOffset` from
///      `DateTimeOffset.UtcNow`). Sub-second ordering of two
///      uploads from the same scope is not promised. ✓
type IAssetStore =
    /// Persist a validated `UploadRequest`. The implementation
    /// computes the SHA-256 content hash, writes the original
    /// bytes to blob storage under
    /// `assets/originals/{ContentHash}`, writes the
    /// `AssetRecord` JSON under `assets/records/{AssetId}.json`,
    /// optionally probes pixel dimensions, and emits the audit
    /// event. Two uploads of identical bytes share the original
    /// blob (content-hash dedup) but produce two distinct
    /// records — alt-text / caption / uploader differ per upload.
    abstract Upload: scopeContainer: string * request: UploadRequest -> Async<Result<AssetRecord, AssetUploadError>>

    /// Resolve an asset id to its record. `None` means the
    /// record does not exist (never uploaded or already
    /// deleted). Original-bytes retrieval is intentionally not
    /// exposed on this surface — callers want the derivative,
    /// not the original. Internal SDK call sites that need the
    /// original (e.g. re-render) read it via `IBlobStorage`
    /// directly.
    abstract Get: scopeContainer: string * id: AssetId -> Async<AssetRecord option>

    /// Fetch (or generate-on-demand and cache) a derivative.
    /// Cache hit: served from `assets/derivatives/{ContentHash}/
    /// {derivativeName}.{ext}`. Cache miss: the implementation
    /// resolves the spec from the record's `DerivativeProfile`,
    /// renders via `IDerivativeRenderer`, writes the cache slot,
    /// and serves. Returns `(bytes, mimeType)`.
    abstract GetDerivative:
        scopeContainer: string * id: AssetId * derivativeName: string ->
            Async<Result<byte[] * string, AssetDerivativeError>>

    /// Delete a record + cascade. Removes the record blob, the
    /// derivative-cache subtree, and the original blob (subject
    /// to content-hash sharing — see implementation note: the
    /// default impl reference-counts originals via the record-
    /// listing for the prefix). Idempotent — deleting an unknown
    /// id returns `Ok` after a no-op (audit event is suppressed
    /// in that case).
    abstract Delete: scopeContainer: string * id: AssetId -> Async<Result<unit, AssetDeleteError>>

    /// List records under the scope's `assets/records/`
    /// subtree, optionally filtered by id-prefix. `prefix`
    /// matches `AssetId.Value` from the start; empty `prefix`
    /// returns every record. Order: `UploadedAt` descending
    /// (newest first), ties broken by `AssetId` ascending.
    /// `page` is 0-indexed; the default impl returns 50 records
    /// per page.
    abstract List: scopeContainer: string * prefix: string * page: int -> Async<AssetRecord list>