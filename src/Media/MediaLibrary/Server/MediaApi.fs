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
}

module MediaApi =
    [<Literal>]
    let routeBuilderPrefix = "/api/media"

    let routeBuilder (_typeName: string) (methodName: string) =
        sprintf "%s/%s" routeBuilderPrefix methodName