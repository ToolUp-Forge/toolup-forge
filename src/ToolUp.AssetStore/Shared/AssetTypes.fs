// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AssetStore

open System

// ─── Phase 39 — Asset substrate shared types ────────────────────
//
// Pure shared-tier types: no IO, no DI, no framework references.
// Server and (future) client code reference these via the
// `ToolUp.AssetStore` namespace. Smart constructors enforce the
// alt-text invariant at the boundary; downstream code receives
// pre-validated `UploadRequest` values it can trust.

/// Identity of an asset record. Opaque value-type id (GP 12.1 —
/// identity by value, never a live handle). Issued by
/// `DefaultAssetStore.Upload`; persisted on the record blob; used
/// as the lookup key for `Get` / `GetDerivative` / `Delete`.
type AssetId =
    | AssetId of string

    member this.Value =
        let (AssetId v) = this
        v

module AssetId =
    let value (AssetId v) = v

    /// Fresh asset id — GUID-derived, URL-safe. New uploads call
    /// this; do not generate ids client-side.
    let create () : AssetId = AssetId(Guid.NewGuid().ToString("N"))

/// Encoded image format for a derivative. Only the four formats
/// the SkiaSharp default renderer ships with — JPEG / PNG /
/// WebP / AVIF. Operators wanting wider format coverage swap
/// in the optional `ImageMagickDerivativeRenderer` companion.
type ImageFormat =
    | Jpeg
    | Png
    | Webp
    | Avif

module ImageFormat =
    let mimeType =
        function
        | Jpeg -> "image/jpeg"
        | Png -> "image/png"
        | Webp -> "image/webp"
        | Avif -> "image/avif"

    /// File-extension suffix used in the derivative-cache path.
    /// Stable — operators read the cache layout directly when
    /// debugging or warming the cache out-of-band.
    let fileExtension =
        function
        | Jpeg -> "jpg"
        | Png -> "png"
        | Webp -> "webp"
        | Avif -> "avif"

/// A single derivative shape — name, target dimensions, output
/// format, encoder quality. `MaxWidth` / `MaxHeight` constrain
/// the output bounding box; the renderer preserves aspect ratio
/// and never upscales (smaller originals pass through at their
/// native resolution). `Quality` is the encoder's 0–100 lossy
/// slider — ignored for `Png` (lossless).
type DerivativeSpec = {
    /// Stable name — used as the cache-blob suffix and the
    /// `GetDerivative` lookup key. Operators see this in the
    /// blob path. Convention: lowercase-hyphen
    /// (`"thumbnail"`, `"medium"`, `"og"`, `"webp-medium"`).
    Name: string
    /// Bounding-box width in pixels. `None` = no width
    /// constraint (height-only fit).
    MaxWidth: int option
    /// Bounding-box height in pixels. `None` = no height
    /// constraint (width-only fit).
    MaxHeight: int option
    /// Output format the renderer encodes to. `Jpeg` for
    /// content photos, `Webp` / `Avif` for modern-browser
    /// fallback chains, `Png` for vector-shaped graphics that
    /// must stay lossless.
    Format: ImageFormat
    /// 0–100. 80 is the SDK default for `Jpeg` / `Webp` /
    /// `Avif`; ignored for `Png`.
    Quality: int
}

/// Named bundle of derivatives — `"web-default"`,
/// `"podcast-card"`, etc. Pinned per `AssetRecord` so a record
/// upgraded later (operator edits the profile config to add an
/// `"avif-medium"` derivative) gets the new derivative
/// generated on its next request. Profile id is just a string;
/// the resolution table lives in `DerivativeProfiles`.
type DerivativeProfileId =
    | DerivativeProfileId of string

    member this.Value =
        let (DerivativeProfileId v) = this
        v

module DerivativeProfileId =
    let value (DerivativeProfileId v) = v
    let webDefault = DerivativeProfileId "web-default"

/// Per-asset record. Stored as one JSON blob under
/// `assets/records/{assetId}.json`; loaded by `Get` and
/// `List`. The original bytes themselves live at
/// `assets/originals/{contentHash}` (content-hash-keyed so two
/// records that ingest the same image share storage).
///
/// `AltText` is required at upload time and persisted here
/// (not on the derivative). The audit payload excludes it
/// deliberately (treated as user content per Phase 39 spec).
type AssetRecord = {
    Id: AssetId
    /// SHA-256 of the original bytes (hex-encoded, lowercase).
    /// The derivative cache lives under
    /// `assets/derivatives/{ContentHash}/{derivative-name}.{ext}`,
    /// so two records ingesting the same image share derivatives
    /// automatically.
    ContentHash: string
    /// File name the user uploaded — surfaced in admin UIs;
    /// never used to derive the blob path. Validation strips
    /// path separators upstream.
    OriginalFilename: string
    /// MIME type sniffed from the upload (`image/jpeg`,
    /// `image/png`, …). Reused on `Get` responses so HTTP
    /// callers don't need to re-sniff.
    MimeType: string
    /// Size of the original bytes.
    SizeBytes: int64
    /// REQUIRED at upload time. Empty string is rejected by
    /// `UploadRequest.create`; > 1024 chars is rejected as too
    /// long (an accessibility hint, not a paragraph).
    AltText: string
    /// Optional rich-text caption. Distinct from `AltText` —
    /// captions are displayed visually, alt text is for
    /// assistive tech only.
    Caption: string option
    /// The user who performed the upload. Resolved server-side
    /// from `AccessContext.UserId`; client never names the
    /// actor.
    UploadedBy: string
    /// Wall-clock at the moment the record was persisted.
    UploadedAt: DateTimeOffset
    /// Image pixel dimensions (decoded once at upload time and
    /// cached on the record so list views don't need to round-
    /// trip the original). `None` when the SkiaSharp decode
    /// failed to surface them (corrupt header).
    Width: int option
    Height: int option
    /// Profile this asset was uploaded against. Resolved
    /// against the deployment's profile registry at request
    /// time — adding a new derivative to a profile causes
    /// existing records to lazily produce the new derivative.
    DerivativeProfile: DerivativeProfileId
}

/// Why an upload failed. Discriminated DU (GP 12.3 — retry /
/// failure expressed as data, not exception messages). All
/// cases are deterministic in the input — callers re-validating
/// after the error see the same outcome.
type AssetUploadError =
    /// `UploadRequest.AltText` was empty or whitespace.
    | AltTextRequired
    /// `UploadRequest.AltText` exceeded 1024 chars.
    | AltTextTooLong of length: int
    /// `UploadRequest.OriginalFilename` was empty or contained
    /// path separators after trimming.
    | InvalidFilename
    /// MIME type not in the SDK's accept-list. The default
    /// list is `image/jpeg`, `image/png`, `image/webp`,
    /// `image/avif`, `image/gif`. Operators override via
    /// `AssetStoreOptions.AcceptedMimeTypes`.
    | UnsupportedMimeType of mime: string
    /// Upload exceeded `AssetStoreOptions.MaxBytes` (default
    /// 25 MiB).
    | FileTooLarge of size: int64 * cap: int64
    /// `IBlobStorage.Upload` returned `Error`. Carries the
    /// provider's diagnostic so the caller can surface it.
    | StorageError of message: string

/// Why fetching a derivative failed.
type AssetDerivativeError =
    /// Asset id doesn't resolve to a record. Either never
    /// uploaded or deleted.
    | AssetNotFound
    /// Profile didn't declare this derivative name. Operators
    /// add the spec to the profile and re-request.
    | UnknownDerivative of name: string
    /// Renderer raised. Original is intact; the cache slot
    /// stays empty so the next request retries.
    | RenderFailed of message: string
    /// Reading the original from blob storage failed.
    | StorageError of message: string
    /// Phase 127 — the derivative is an async-mode profile entry
    /// whose derivation job is queued / running. Carries the job
    /// correlation id; completion surfaces over the notification
    /// channel (`DerivativeJobs.DerivativeReadyNotificationKey`)
    /// and the content-hash cache serves instantly thereafter.
    | DerivationPending of correlationId: string

/// Why a delete failed.
type AssetDeleteError =
    | AssetNotFound
    | StorageError of message: string

/// Compose-time options for the AssetStore companion.
/// `MaxBytes`, `AcceptedMimeTypes`, `AltTextMaxChars` are the
/// three operator-facing levers; everything else uses
/// recipe-grade defaults.
type AssetStoreOptions = {
    /// Hard cap on a single upload's byte count. Defaults to
    /// 25 MiB — enough for photographs at typical phone
    /// resolution, small enough to surface mistakes before
    /// they pin the upload bandwidth.
    MaxBytes: int64
    /// MIME types the handler accepts. The default list
    /// covers the four SkiaSharp encodes plus `gif`. Operators
    /// shipping AVIF-only or PNG-only deployments trim this.
    AcceptedMimeTypes: Set<string>
    /// Max length of the alt-text string. Defaults to 1024 —
    /// enough for a sentence, short of a paragraph.
    AltTextMaxChars: int
    /// `true` (default) wraps every upload in a
    /// `IAuditLog.Record AssetUploaded`. `false` suppresses
    /// audit emission entirely — only for deployments with
    /// strict audit-volume budgets.
    EmitAudit: bool
}

module AssetStoreOptions =
    let private defaultMimeTypes =
        Set.ofList [ "image/jpeg"; "image/png"; "image/webp"; "image/avif"; "image/gif" ]

    let defaults: AssetStoreOptions = {
        MaxBytes = 25L * 1024L * 1024L
        AcceptedMimeTypes = defaultMimeTypes
        AltTextMaxChars = 1024
        EmitAudit = true
    }

/// Asset-store substrate opt-in (GP 13 — default off, strip-
/// imports byte-for-byte).
///
///   * `NoAssetStore` (default) — no DI registration, no
///     `/api/assets/*` handlers, no audit emission. Strip-
///     imports byte-for-byte: a deployment that doesn't opt in
///     pays zero runtime cost.
///   * `EnabledAssetStore options` — `DefaultAssetStore` is
///     registered as `IAssetStore`, the Fable.Remoting handler
///     mounts at `/api/assets/`, and the multipart upload
///     endpoint mounts at `/api/assets/upload`.
type AssetStoreMode =
    | NoAssetStore
    | EnabledAssetStore of options: AssetStoreOptions

/// Upload request — what the handler hands to
/// `IAssetStore.Upload`. Smart constructor enforces the alt-
/// text invariant + filename validity + MIME accept-list. The
/// uploaded bytes are passed by-value (the SDK's other upload
/// surfaces — `IDataIngestor`, `IFileManagementApi` — do the
/// same).
type UploadRequest = private {
    bytes: byte[]
    originalFilename: string
    mimeType: string
    altText: string
    caption: string option
    uploadedBy: string
    profile: DerivativeProfileId
} with

    member this.Bytes = this.bytes
    member this.OriginalFilename = this.originalFilename
    member this.MimeType = this.mimeType
    member this.AltText = this.altText
    member this.Caption = this.caption
    member this.UploadedBy = this.uploadedBy
    member this.Profile = this.profile

module UploadRequest =

    /// Smart constructor — the only path through which an
    /// `UploadRequest` value is created. Validates alt-text +
    /// filename + MIME + size against the supplied options.
    /// Returns `Result<UploadRequest, AssetUploadError>` so
    /// the handler surfaces a typed error to the client
    /// before the bytes ever reach storage.
    let create
        (options: AssetStoreOptions)
        (bytes: byte[])
        (originalFilename: string)
        (mimeType: string)
        (altText: string)
        (caption: string option)
        (uploadedBy: string)
        (profile: DerivativeProfileId)
        : Result<UploadRequest, AssetUploadError> =

        let trimmedFilename =
            if isNull originalFilename then
                ""
            else
                originalFilename.Trim()

        let trimmedAlt = if isNull altText then "" else altText.Trim()

        let trimmedMime =
            if isNull mimeType then
                ""
            else
                mimeType.Trim().ToLowerInvariant()

        let size = if isNull bytes then 0L else int64 bytes.Length

        if String.IsNullOrWhiteSpace trimmedAlt then
            Error AltTextRequired
        elif trimmedAlt.Length > options.AltTextMaxChars then
            Error(AltTextTooLong trimmedAlt.Length)
        elif
            String.IsNullOrWhiteSpace trimmedFilename
            || trimmedFilename.Contains '/'
            || trimmedFilename.Contains '\\'
        then
            Error InvalidFilename
        elif not (options.AcceptedMimeTypes.Contains trimmedMime) then
            Error(UnsupportedMimeType trimmedMime)
        elif size > options.MaxBytes then
            Error(FileTooLarge(size, options.MaxBytes))
        else
            Ok {
                bytes = bytes
                originalFilename = trimmedFilename
                mimeType = trimmedMime
                altText = trimmedAlt
                caption = caption |> Option.map _.Trim()
                uploadedBy = uploadedBy
                profile = profile
            }
// ─── Phase 127 — generalised derivative profiles ─────────────────
//
// The original profile vocabulary is image-typed (`DerivativeSpec`
// — bounding box + `ImageFormat` + quality). The generalisation
// adds a parallel general entry kind for arbitrary MIME →
// derivative mappings (document → preview image, video → poster
// frame, model → compressed variant) without touching the image
// entries: a profile is now a list of `ProfileEntry`, and every
// pre-existing registration maps onto `ImageDerivative` unchanged
// (GP 11 — same registry API, same resolution, same cache paths).

/// When the derivative is produced relative to the request.
type DerivationMode =
    /// Rendered inline on the requesting call (the cache-miss
    /// behaviour image profiles have always had). For renders in
    /// the tens-to-hundreds-of-milliseconds class.
    | Sync
    /// Rendered on `IJobScheduler` off the request path; the
    /// request returns `DerivationPending` until the job completes
    /// and notifies. For seconds-to-minutes-class derivations.
    /// Requires the compose-time async-derivation opt-in (GP 13).
    | AsyncJob

/// A general (arbitrary-MIME) derivative shape. Identity by value
/// throughout (GP 12 rule 1): names, MIME strings and a string
/// renderer key — the renderer registry resolves the key to an
/// implementation at render time.
type GeneralDerivativeSpec = {
    /// Stable name — cache-blob suffix and `GetDerivative` lookup
    /// key, exactly like `DerivativeSpec.Name`.
    Name: string
    /// Input MIME types this entry accepts. Exact matches
    /// (`"video/mp4"`), prefix wildcards (`"image/*"`), or the
    /// universal `"*"`. A request whose record MIME matches none
    /// fails typed rather than handing the renderer bytes it
    /// never claimed to accept.
    AcceptedInputMimes: string list
    /// MIME type of the produced derivative.
    OutputMime: string
    /// File-extension suffix for the cache path
    /// (`assets/derivatives/{hash}/{name}.{ext}`).
    FileExtension: string
    /// Key into the deployment's MIME-renderer registry
    /// (`AssetCompose.withMimeRenderer`). Renderer implementations
    /// carrying vendor dependencies stay in companions (GP 1).
    RendererKey: string
    Mode: DerivationMode
    /// Opaque per-spec parameters handed to the renderer (target
    /// dimensions, codec hints, compression level — whatever the
    /// renderer's README documents).
    Parameters: Map<string, string>
}

module GeneralDerivativeSpec =
    /// Does the record's MIME satisfy the entry's accept list?
    let acceptsMime (inputMime: string) (spec: GeneralDerivativeSpec) : bool =
        spec.AcceptedInputMimes
        |> List.exists (fun accepted ->
            accepted = "*"
            || String.Equals(accepted, inputMime, StringComparison.OrdinalIgnoreCase)
            || (accepted.EndsWith "*"
                && inputMime.StartsWith(accepted.TrimEnd '*', StringComparison.OrdinalIgnoreCase)))

/// One derivative a profile declares — the image shape every
/// existing profile uses, or the generalised arbitrary-MIME shape.
type ProfileEntry =
    | ImageDerivative of DerivativeSpec
    | GeneralDerivative of GeneralDerivativeSpec

module ProfileEntry =
    /// The entry's stable lookup name.
    let name =
        function
        | ImageDerivative spec -> spec.Name
        | GeneralDerivative spec -> spec.Name