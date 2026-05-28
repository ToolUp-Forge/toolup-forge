// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AssetStore

/// Fable.Remoting contract for the asset-store API. The
/// multipart upload endpoint (`/api/assets/upload`) is the
/// canonical path for browser file uploads — see
/// `AssetUploadHandler.uploadEndpoint` — because raw
/// `byte[]`-in-Remoting is awkward for the React/Feliz side
/// (one chunked-encoded request beats serialising the payload
/// twice through Newtonsoft). The Remoting API below covers
/// the metadata + derivative-read paths.
///
/// Mount at `/api/assets/`. Authentication is the SDK's
/// default — `AuthEnforcementMiddleware` covers every non-
/// anonymous route.
type IAssetApi = {
    /// Resolve an asset id to its record. `None` on unknown
    /// id; same shape as `IAssetStore.Get`.
    GetAsset: string -> Async<AssetRecord option>
    /// List the scope's assets (newest first), optionally
    /// filtered by id-prefix, paginated by `page` (0-indexed,
    /// 50 records per page).
    ListAssets: string * int -> Async<AssetRecord list>
    /// Fetch (or generate-on-demand) a derivative. Returns
    /// the bytes + mime type so a Feliz component can render
    /// `<img src={data:URL}>` without a second round-trip.
    /// For high-volume reads, deployments serve derivatives
    /// directly from CDN-fronted blob storage; this Remoting
    /// path is the fallback.
    GetDerivative: string * string -> Async<Result<byte[] * string, AssetDerivativeError>>
    /// Delete an asset by id. Idempotent — unknown ids
    /// resolve to `Ok ()`.
    DeleteAsset: string -> Async<Result<unit, AssetDeleteError>>
}

module AssetApi =
    /// Standard route prefix. Matches the convention used by
    /// every other Fable.Remoting handler in the SDK.
    [<Literal>]
    let routeBuilderPrefix = "/api/assets"

    let routeBuilder (_typeName: string) (methodName: string) =
        sprintf "%s/%s" routeBuilderPrefix methodName