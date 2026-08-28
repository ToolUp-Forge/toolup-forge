// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.MediaLibrary

open ToolUp.Platform // 0.5.0 — forge-native auth + audit attributes

// ─── Phase 88 — IMediaApi (Fable.Remoting contract) ───────────────────
//
// The metadata + management surface a client uses alongside the raw
// range-serving endpoints. Identifiers transit as `string` over the wire
// (ToolUp.Remoting serialises the `MediaId` DU as its wrapped string);
// `GetSignedUrl` takes a TTL in seconds and returns a ready-to-use URL
// path bound to the caller's active scope.

type IMediaApi = {
    // Handler (`MediaCompose.mediaApi`) requires a resolved
    // `StorageScope` (fails closed without one) but applies no
    // role/claim gate beyond it — anonymous-mode session scopes
    // qualify, so `AllowAnonymous` is the honest classification;
    // scope isolation keeps gating.
    [<AllowAnonymous>]
    GetMedia: string -> Async<MediaRecord option>
    [<AllowAnonymous>]
    ListMedia: string * int -> Async<MediaRecord list>
    [<AllowAnonymous>]
    [<Audit "Custom:MediaDeleted">]
    DeleteMedia: string -> Async<Result<unit, MediaDeleteError>>
    [<AllowAnonymous>]
    GetSignedUrl: string * int -> Async<Result<string, SignedUrlError>>

    // ─── Phase 469 — resumable chunked uploads ───────────────────
    //
    // The chunk endpoints ride the SAME scoped route family
    // (`/api/media/*`) and the same scope resolution as the four
    // above, so a session is bound to the caller's scope by the
    // handler that builds this record, not by anything on the wire
    // (GP 4). Arguments stay primitives — filename / MIME / declared
    // size / caption rather than a `MediaUploadDeclaration` — because
    // the declaration type is smart-constructed and its validation
    // must run SERVER-side, where the deployment's own
    // `MediaLibraryOptions` are; a client cannot be trusted to have
    // constructed it against the right cap or MIME allowlist.
    //
    // `uploadedBy` is likewise not a parameter: the handler takes it
    // from the resolved scope, so a caller cannot attribute an upload
    // to somebody else.

    /// Open a resumable upload session. Returns the session id.
    /// Validates filename / MIME / declared size up front, so a
    /// doomed upload fails before its first chunk.
    [<AllowAnonymous>]
    BeginUpload: string * string * int64 * string option -> Async<Result<string, UploadSessionError>>

    /// Append one chunk at an absolute byte offset. Idempotent at an
    /// already-accepted offset; any other offset is refused with the
    /// expected cursor, which is what a resuming client re-sends
    /// from. The per-chunk body cap is
    /// `MediaLibraryOptions.MaxChunkBytes`.
    [<AllowAnonymous>]
    AppendChunk: string * int64 * byte[] -> Async<Result<UploadProgress, UploadSessionError>>

    /// Assemble and ingest the session. Produces a `MediaRecord`
    /// indistinguishable from a single-shot upload of the same bytes.
    [<AllowAnonymous>]
    [<Audit "Custom:MediaUploadCommitted">]
    CommitUpload: string -> Async<Result<MediaRecord, UploadSessionError>>

    /// Abandon a session and delete its chunks.
    [<AllowAnonymous>]
    [<Audit "Custom:MediaUploadAborted">]
    AbortUpload: string -> Async<Result<unit, UploadSessionError>>
}

module MediaApi =
    [<Literal>]
    let routeBuilderPrefix = "/api/media"

    let routeBuilder (_typeName: string) (methodName: string) =
        sprintf "%s/%s" routeBuilderPrefix methodName

    /// Phase 473 — the playback beacon route (`PlaybackTelemetry
    /// .beaconHandler`).
    ///
    /// Declared HERE, beside the remoting route builder, because it
    /// shares that builder's `/api/media/` prefix: the builder maps
    /// `IMediaApi`'s member names onto `/api/media/{methodName}`, so
    /// this literal is the one thing that says a future contract member
    /// may not be called `Beacon`. It is deliberately NOT a remoting
    /// member — the endpoint answers `204` to everything (see the
    /// handler), and the remoting dispatcher's job is precisely to turn
    /// a failure into a status a caller can read.
    [<Literal>]
    let beaconRoute = "/api/media/beacon"