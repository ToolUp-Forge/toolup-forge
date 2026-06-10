// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.MediaLibrary

// ─── Phase 88 — IMediaApi (Fable.Remoting contract) ───────────────────
//
// The metadata + management surface a client uses alongside the raw
// range-serving endpoints. Identifiers transit as `string` over the wire
// (ToolUp.Remoting serialises the `MediaId` DU as its wrapped string);
// `GetSignedUrl` takes a TTL in seconds and returns a ready-to-use URL
// path bound to the caller's active scope.

type IMediaApi = {
    GetMedia: string -> Async<MediaRecord option>
    ListMedia: string * int -> Async<MediaRecord list>
    DeleteMedia: string -> Async<Result<unit, MediaDeleteError>>
    GetSignedUrl: string * int -> Async<Result<string, SignedUrlError>>
}

module MediaApi =
    [<Literal>]
    let routeBuilderPrefix = "/api/media"

    let routeBuilder (_typeName: string) (methodName: string) =
        sprintf "%s/%s" routeBuilderPrefix methodName