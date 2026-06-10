// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Media.CloudTranscode.CloudTranscodeProvider

open ToolUp.MediaLibrary

// ─── Phase 88 — cloud-transcode sub-companion ─────────────────────────
//
// An `IMediaTranscoder` adapter for cloud-managed transcode services
// (AWS MediaConvert, Mux, Coconut, …). The SDK deliberately ships NO
// cloud-vendor SDK here (GP 1 / GP 2): the deployment supplies a `submit`
// callback that performs the actual provider call (start the job, poll
// to completion, download the rendered HLS package) and returns the
// produced files. This keeps the vendor dependency — and the credentials
// — entirely in the deployment, while the SDK owns the `IMediaTranscoder`
// seam the media library composes against.
//
// A deployment that installs the package but hasn't wired a callback yet
// uses `notConfigured`, which declares no capability so the media library
// serves single-file progressive downloads (the default) until the cloud
// path is connected.

/// Wrap a deployment-supplied transcode callback into an
/// `IMediaTranscoder`. `submit originalBytes mimeType` runs the provider
/// job and returns the produced HLS `TranscodedFile`s (master manifest +
/// segments) or an error message. The media library persists the
/// returned files under the item's derived directory and records the
/// rendition.
let create (submit: byte[] -> string -> Async<Result<TranscodedFile list, string>>) : IMediaTranscoder =
    { new IMediaTranscoder with
        member _.Capabilities = {
            CanExtractPoster = false
            CanTranscodeHls = true
        }

        member _.TranscodeToHls(bytes, mimeType) = submit bytes mimeType
    }

/// A not-yet-configured cloud transcoder — declares no capability and
/// refuses. Use as a placeholder until a `submit` callback is wired.
let notConfigured () : IMediaTranscoder =
    { new IMediaTranscoder with
        member _.Capabilities = {
            CanExtractPoster = false
            CanTranscodeHls = false
        }

        member _.TranscodeToHls(_, _) = async {
            return Error "cloud transcode not configured — supply a submit callback via CloudTranscodeProvider.create"
        }
    }