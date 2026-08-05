// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AssetStore

// ─── Phase 186 — running the upload-validation seam ──────────────
//
// `IUploadValidator` / `UploadValidation` / `UploadRejection` are
// declared in the shared tier (they are pure data + one interface,
// and `AssetStoreOptions` has to be able to name the mode). What
// lives here is the *server-side* half: the runner that the upload
// handler calls, and the in-tree reference validator that gives the
// seam a vendor-free default worth composing.

/// Tuning for `SniffingUploadValidator`.
type MimeSniffOptions = {
    /// What to do when `MagicBytes.sniff` recognises nothing.
    /// `false` (default) refuses — the validator exists to
    /// corroborate a declared type, and "I cannot corroborate it"
    /// is not corroboration. `true` admits unrecognised payloads,
    /// for a deployment whose accept-list carries types the table
    /// does not cover (SVG, plain text) and that has other reasons
    /// to trust them.
    AllowUnrecognisedBytes: bool
    /// Reject a raster image that also carries executable markup in
    /// its leading window (a polyglot). `true` by default — the
    /// header check alone passes these.
    RejectMarkupPolyglots: bool
    /// How far into the payload the polyglot scan looks. 1 KiB
    /// covers the shapes that matter (a polyglot has to put its
    /// markup early enough for a sniffing browser to find it) and
    /// keeps the check O(1) in the upload size.
    MarkupScanBytes: int
    /// Phase 639 — corroborate a declared **spreadsheet package**
    /// type (`.xlsx` / `.xlsm`) against the container's own
    /// `[Content_Types].xml` rather than against the zip header.
    ///
    /// `false` (default, and the pre-639 behaviour exactly — GP 11)
    /// leaves every OOXML upload sniffing as `application/zip`, so a
    /// deployment declaring `…spreadsheetml.sheet` is refused. `true`
    /// is the opt-in a deployment whose accept-list carries workbooks
    /// takes; it only ever ADMITS payloads the header check would
    /// have refused, and never newly refuses one.
    ///
    /// Admitting `.xlsm` admits a workbook whose macros the platform
    /// ignores — the tabular ingestion leg extracts the sheet grid and
    /// never opens, extracts or executes `vbaProject.bin`. That is a
    /// posture a consumer can and should state to its uploaders.
    RecogniseSpreadsheetPackages: bool
}

[<RequireQualifiedAccess>]
module MimeSniffOptions =

    let defaults: MimeSniffOptions = {
        AllowUnrecognisedBytes = false
        RejectMarkupPolyglots = true
        MarkupScanBytes = 1024
        RecogniseSpreadsheetPackages = false
    }

    /// `defaults` plus the spreadsheet-package opt-in — the shape a
    /// deployment that ingests workbooks wants.
    let withSpreadsheetPackages: MimeSniffOptions = {
        defaults with
            RecogniseSpreadsheetPackages = true
    }

/// In-tree reference `IUploadValidator`: cross-checks the declared
/// `Content-Type` against what the bytes actually are, and refuses a
/// raster image that smuggles executable markup.
///
/// Vendor-free by construction (GP 1) — it is a byte-prefix table
/// and a substring scan, nothing more — so it ships in this package
/// rather than a companion. It is still **opt-in**: a deployment
/// composes it deliberately via
/// `AssetStoreOptions.withUploadValidator`, and one that does not is
/// unchanged (GP 11 / GP 13).
///
/// It is deliberately NOT a malware scanner and makes no claim to
/// be one. A scanner is a network call to a vendor backend and
/// belongs in a companion behind this same seam; a deployment
/// wanting both composes a validator that calls both.
type SniffingUploadValidator(options: MimeSniffOptions) =

    /// Defaults: fail-closed on unrecognised bytes, polyglot scan on.
    /// An explicit overload rather than an optional parameter — F#
    /// folds optional constructor arguments into one widened
    /// constructor, which reads as a removal against the public-API
    /// baseline the moment a second one is added.
    new() = SniffingUploadValidator(MimeSniffOptions.defaults)

    member _.Options = options

    interface IUploadValidator with
        member _.Name = "sniffing"

        member _.Validate(bytes, declaredMime) = async {
            let declared =
                if isNull declaredMime then
                    ""
                else
                    declaredMime.Trim().ToLowerInvariant()

            match MagicBytes.sniff bytes with
            | None when options.AllowUnrecognisedBytes -> return Ok()
            | None -> return Error(MimeMismatch(declared, MagicBytes.unrecognised))
            | Some sniffed when sniffed <> declared ->
                // Phase 639 — an Office Open XML workbook IS a zip, so
                // the header check reports `application/zip` and the
                // comparison above disagrees with a perfectly honest
                // `…spreadsheetml.sheet` declaration. When the
                // deployment has opted in, look at the container's own
                // content-type manifest before refusing.
                //
                // Deliberately reached only on the mismatch arm: a
                // payload the header check already corroborated is
                // never re-judged, so turning the option on can only
                // widen what is admitted (GP 11).
                let packaged =
                    if options.RecogniseSpreadsheetPackages then
                        MagicBytes.openXmlPackage bytes
                    else
                        None

                match packaged with
                // Case-INSENSITIVE, and load-bearing: the registered
                // macro-enabled type is spelled
                // `…sheet.macroEnabled.12`, with a capital E, while
                // `declared` was lower-cased above. Every type in the
                // magic-byte table happens to be all-lowercase, so an
                // ordinal compare has been correct until now and is
                // silently wrong for this one. MIME types are
                // case-insensitive by RFC 2045 either way.
                | Some packageType when packageType.Equals(declared, System.StringComparison.OrdinalIgnoreCase) ->
                    return Ok()
                // The container disagreed too — report what it actually
                // declares, which is far more useful to an operator
                // than "application/zip".
                | Some packageType -> return Error(MimeMismatch(declared, packageType))
                | None -> return Error(MimeMismatch(declared, sniffed))
            | Some sniffed ->
                if
                    options.RejectMarkupPolyglots
                    && MagicBytes.isRasterImage sniffed
                    && MagicBytes.containsMarkup options.MarkupScanBytes bytes
                then
                    return Error(MimeMismatch(declared, MagicBytes.markup))
                else
                    return Ok()
        }

/// The single call site every upload path uses to consult the seam.
[<RequireQualifiedAccess>]
module UploadValidator =

    /// Run the composed validation mode over an upload's bytes.
    ///
    /// Two behaviours are load-bearing and both are here rather than
    /// in each caller, so no caller can get them wrong:
    ///
    ///   * `NoUploadValidator` short-circuits without touching the
    ///     bytes and without an `async` state machine — the zero-cost
    ///     default (GP 13).
    ///   * A configured validator that RAISES is mapped to
    ///     `ValidationUnavailable`, i.e. a refusal. This is the
    ///     fail-closed rule stated once: a scan backend that throws a
    ///     socket exception must not be indistinguishable from one
    ///     that returned "clean", which is exactly what letting the
    ///     exception escape (and be caught by some outer handler
    ///     that logs and continues) would produce.
    let run
        (validation: UploadValidation)
        (bytes: byte[])
        (declaredMime: string)
        : Async<Result<unit, UploadRejection>> =
        match validation with
        | NoUploadValidator -> async.Return(Ok())
        | EnabledUploadValidator validator ->
            let name = validator.Name

            async {
                try
                    return! validator.Validate(bytes, declaredMime)
                with ex ->
                    return Error(ValidationUnavailable $"validator '%s{name}' raised: %s{ex.Message}")
            }

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