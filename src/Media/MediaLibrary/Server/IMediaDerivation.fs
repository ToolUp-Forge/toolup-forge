// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.MediaLibrary

// ─── Phase 88 — media derivation + transcode hooks (GP 1) ─────────────
//
// The default media library stores originals untransformed and serves
// them as single-file progressive downloads with ZERO transcode
// dependency. Poster-frame extraction and HLS rendition ladders are
// heavy, FFmpeg/cloud-backed concerns, so they sit behind these hooks
// and ship as opt-in sub-companions (`ToolUp.Media.FFmpeg`,
// `ToolUp.Media.CloudTranscode`). The default `Noop*` implementations
// declare no capabilities, keeping the core dependency graph minimal
// (GP 1) and the unused path free (GP 13).

/// What a derivation / transcode provider can do. The default library
/// branches on these flags so it never calls a capability a provider
/// doesn't advertise.
type MediaDerivationCapabilities = {
    CanExtractPoster: bool
    CanTranscodeHls: bool
}

/// Lightweight probe of a media item's intrinsic properties, populated
/// into `MediaRecord` at upload time when a provider can read them.
type MediaProbe = {
    DurationSeconds: float option
    Width: int option
    Height: int option
}

/// A still poster frame extracted from a video.
type PosterResult = {
    Bytes: byte[]
    MimeType: string
    Width: int option
    Height: int option
}

/// One file produced by an HLS transcode pass — the master / variant
/// `.m3u8` manifests and the `.ts` / `.m4s` segments. `BlobSuffix` is
/// appended to the item's rendition prefix when persisted; the library
/// owns blob persistence so the transcoder stays a pure bytes-in /
/// files-out transform (GP 12 rule 4 — stateless between calls).
type TranscodedFile = {
    BlobSuffix: string
    Bytes: byte[]
    MimeType: string
    /// Rendition label this file belongs to (`"hls"`, `"720p"`); files
    /// sharing a label collapse into one `MediaRendition` entry keyed by
    /// the master manifest.
    RenditionName: string
    /// `true` for the master manifest a player should load first.
    IsMasterManifest: bool
}

/// Pluggable poster / probe hook. Implemented by the FFmpeg sub-companion
/// for real frame extraction; the default declares no capability so the
/// core library never depends on a media binary.
type IMediaDerivation =
    abstract Capabilities: MediaDerivationCapabilities
    abstract Probe: originalBytes: byte[] * mimeType: string -> Async<MediaProbe>
    abstract ExtractPoster: originalBytes: byte[] * mimeType: string -> Async<Result<PosterResult, string>>

/// Pluggable HLS / adaptive-bitrate transcode hook. Implemented by the
/// FFmpeg + cloud-transcode sub-companions; absent by default (the item
/// is served as a single-file progressive download).
type IMediaTranscoder =
    abstract Capabilities: MediaDerivationCapabilities
    abstract TranscodeToHls: originalBytes: byte[] * mimeType: string -> Async<Result<TranscodedFile list, string>>

module MediaDerivationCapabilities =
    let none: MediaDerivationCapabilities = {
        CanExtractPoster = false
        CanTranscodeHls = false
    }

/// Default poster/probe hook — declares no capability and refuses to
/// extract. Keeps the default media library free of any media-binary
/// dependency (GP 1). Install `ToolUp.Media.FFmpeg` for real extraction.
module NoopMediaDerivation =
    let create () : IMediaDerivation =
        { new IMediaDerivation with
            member _.Capabilities = MediaDerivationCapabilities.none

            member _.Probe(_, _) = async {
                return {
                    DurationSeconds = None
                    Width = None
                    Height = None
                }
            }

            member _.ExtractPoster(_, _) = async {
                return Error "no media derivation configured (install the ToolUp.Media.FFmpeg sub-companion)"
            }
        }

/// Default transcode hook — declares no capability and refuses to
/// transcode. The default library never calls it (it checks
/// `Capabilities.CanTranscodeHls` first); present only so the DI slot is
/// always satisfiable.
module NoopMediaTranscoder =
    let create () : IMediaTranscoder =
        { new IMediaTranscoder with
            member _.Capabilities = MediaDerivationCapabilities.none

            member _.TranscodeToHls(_, _) = async {
                return Error "no media transcoder configured (install the ToolUp.Media.FFmpeg sub-companion)"
            }
        }