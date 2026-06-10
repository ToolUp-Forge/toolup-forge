// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.MediaLibrary.RangeHandler

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Giraffe
open ToolUp.Platform

// ─── Phase 88 — HTTP range-serving endpoints ──────────────────────────
//
// Three routes the SDK mounts when `ServerConfig.MediaLibrary =
// EnabledMediaLibrary`:
//
//   GET /api/media/stream/{mediaId}        scoped (authenticated) stream
//   GET /media/signed/{mediaId}?token=...  scope-signed public stream
//   GET /api/media/hls/{mediaId}/{file}    HLS manifest / segment
//
// All honour `Range` (→ `206 Partial Content` + `Content-Range` +
// `Accept-Ranges: bytes`) so `<video>`/`<audio>` seeking works; an
// out-of-bounds range yields `416 Range Not Satisfiable` with a
// `bytes */total` `Content-Range`. `If-Range` is honoured against the
// content-hash `ETag` (a stale validator falls back to a full `200`).
// The signed route verifies the HMAC signature + expiry + scope binding
// before serving a byte (GP 4) — a leaked URL stops working at its TTL.

let private library (ctx: HttpContext) : IMediaLibrary =
    ctx.RequestServices.GetService(typeof<IMediaLibrary>) :?> IMediaLibrary

let private urlSigner (ctx: HttpContext) : SignedUrl.MediaUrlSigner =
    ctx.RequestServices.GetService(typeof<SignedUrl.MediaUrlSigner>) :?> SignedUrl.MediaUrlSigner

let private scopeContainer (ctx: HttpContext) : string option =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> Some s.Container
    | _ -> None

let private headerValue (ctx: HttpContext) (name: string) : string =
    match ctx.Request.Headers.TryGetValue name with
    | true, v -> v.ToString()
    | _ -> ""

let private setHeader (ctx: HttpContext) (name: string) (value: string) =
    ctx.Response.Headers[name] <- StringValues value

/// Decide whether to honour the `Range` header given an `If-Range`
/// validator. An empty `If-Range` always honours; a present `If-Range`
/// honours only when it equals our strong `ETag` (otherwise the client's
/// cached validator is stale and we serve the whole current body).
let private shouldHonourRange (ifRange: string) (etag: string) : bool =
    String.IsNullOrEmpty ifRange || ifRange = etag

/// Serve a stored original through the library's `ContentLength` +
/// `OpenRange` (blob layout stays encapsulated). Emits `200` / `206` /
/// `416` per the `Range` header.
let private serveOriginal
    (ctx: HttpContext)
    (lib: IMediaLibrary)
    (container: string)
    (id: MediaId)
    : Task<HttpContext option> =
    task {
        match! lib.Get(container, id) |> Async.StartAsTask with
        | None ->
            ctx.SetStatusCode 404
            return Some ctx
        | Some record ->
            match! lib.ContentLength(container, id) |> Async.StartAsTask with
            | Error _ ->
                ctx.SetStatusCode 404
                return Some ctx
            | Ok total ->
                let etag = sprintf "\"%s\"" record.ContentHash
                setHeader ctx "Accept-Ranges" "bytes"
                setHeader ctx "ETag" etag
                ctx.Response.ContentType <- record.MimeType

                let honour = shouldHonourRange (headerValue ctx "If-Range") etag

                let parsed =
                    if honour then
                        ByteRange.parse (headerValue ctx "Range") total
                    else
                        NoRange

                match parsed with
                | RangeRequest.Unsatisfiable ->
                    ctx.Response.StatusCode <- 416
                    setHeader ctx "Content-Range" (sprintf "bytes */%d" total)
                    return Some ctx
                | NoRange ->
                    match!
                        lib.OpenRange(container, id, { Start = 0L; End = total - 1L })
                        |> Async.StartAsTask
                    with
                    | Ok stream ->
                        ctx.Response.StatusCode <- 200
                        ctx.Response.ContentLength <- Nullable total
                        do! stream.CopyToAsync(ctx.Response.Body)
                        stream.Dispose()
                        return Some ctx
                    | Error _ ->
                        ctx.SetStatusCode 404
                        return Some ctx
                | Satisfiable r ->
                    match! lib.OpenRange(container, id, r) |> Async.StartAsTask with
                    | Ok stream ->
                        ctx.Response.StatusCode <- 206
                        setHeader ctx "Content-Range" (sprintf "bytes %d-%d/%d" r.Start r.End total)
                        ctx.Response.ContentLength <- Nullable r.Length
                        do! stream.CopyToAsync(ctx.Response.Body)
                        stream.Dispose()
                        return Some ctx
                    | Error _ ->
                        ctx.Response.StatusCode <- 416
                        setHeader ctx "Content-Range" (sprintf "bytes */%d" total)
                        return Some ctx
    }

/// Serve an in-memory byte buffer (HLS manifest / segment) with `Range`
/// support. Manifests are small, but players range-request segments.
let private serveBytes (ctx: HttpContext) (mime: string) (bytes: byte[]) : Task<HttpContext option> = task {
    let total = int64 bytes.Length
    setHeader ctx "Accept-Ranges" "bytes"
    ctx.Response.ContentType <- mime

    match ByteRange.parse (headerValue ctx "Range") total with
    | NoRange ->
        ctx.Response.StatusCode <- 200
        ctx.Response.ContentLength <- Nullable total
        do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length)
        return Some ctx
    | Satisfiable r ->
        ctx.Response.StatusCode <- 206
        setHeader ctx "Content-Range" (sprintf "bytes %d-%d/%d" r.Start r.End total)
        ctx.Response.ContentLength <- Nullable r.Length
        do! ctx.Response.Body.WriteAsync(bytes, int r.Start, int r.Length)
        return Some ctx
    | RangeRequest.Unsatisfiable ->
        ctx.Response.StatusCode <- 416
        setHeader ctx "Content-Range" (sprintf "bytes */%d" total)
        return Some ctx
}

/// Scoped (authenticated) stream — `GET /api/media/stream/{mediaId}`.
let streamHandler: HttpHandler =
    GET
    >=> routef "/api/media/stream/%s" (fun raw ->
        fun (_: HttpFunc) (ctx: HttpContext) -> task {
            match scopeContainer ctx with
            | None ->
                ctx.SetStatusCode 401
                return Some ctx
            | Some container -> return! serveOriginal ctx (library ctx) container (MediaId raw)
        })

/// Scope-signed public stream — `GET /media/signed/{mediaId}?token=...`.
/// Verifies the HMAC signature + expiry + that the signed `MediaId`
/// matches the route before serving from the signed scope's container.
let signedHandler: HttpHandler =
    GET
    >=> routef "/media/signed/%s" (fun raw ->
        fun (_: HttpFunc) (ctx: HttpContext) -> task {
            let token =
                match ctx.Request.Query.TryGetValue "token" with
                | true, v -> v.ToString()
                | _ -> ""

            if String.IsNullOrEmpty token then
                ctx.SetStatusCode 401
                return Some ctx
            else
                match! (urlSigner ctx).VerifyAsync(token, DateTimeOffset.UtcNow) |> Async.StartAsTask with
                | Error _ ->
                    ctx.SetStatusCode 403
                    return Some ctx
                | Ok payload when payload.MediaId <> raw ->
                    ctx.SetStatusCode 403
                    return Some ctx
                | Ok payload -> return! serveOriginal ctx (library ctx) payload.Container (MediaId raw)
        })

/// Scoped HLS manifest / segment serving — `GET
/// /api/media/hls/{mediaId}/{file}`. Serves the produced rendition blobs
/// when a transcode sub-companion has run; absent otherwise (the item is
/// streamed as a single-file progressive download via `streamHandler`).
let hlsHandler: HttpHandler =
    GET
    >=> routef "/api/media/hls/%s/%s" (fun (raw, file) ->
        fun (_: HttpFunc) (ctx: HttpContext) -> task {
            match scopeContainer ctx with
            | None ->
                ctx.SetStatusCode 401
                return Some ctx
            | Some container ->
                match! (library ctx).OpenDerived(container, MediaId raw, file) |> Async.StartAsTask with
                | Ok(bytes, mime) -> return! serveBytes ctx mime bytes
                | Error _ ->
                    ctx.SetStatusCode 404
                    return Some ctx
        })

/// All three media-serving handlers, in match order.
let handlers: HttpHandler list = [ streamHandler; signedHandler; hlsHandler ]