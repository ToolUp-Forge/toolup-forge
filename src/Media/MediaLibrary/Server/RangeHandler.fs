// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.MediaLibrary.RangeHandler

open System
open System.Buffers
open System.IO
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
//
// ─── Phase 468 — every body leaves through one chunked copy loop ─────
//
// The three routes now share `copyToBody`: an explicit bounded
// read/write loop rather than `Stream.CopyToAsync`. The served stream
// itself is lazy (`DefaultMediaLibrary` pulls `RangeChunkBytes` windows
// from the blob store on demand), so this loop is what actually paces
// the store reads — and cancelling it on `ctx.RequestAborted` is what
// stops an abandoned scrub from pulling the rest of the window.
//
// The HLS / poster route takes the same bounded path when the composed
// `IMediaLibrary` declares the optional `IMediaRangeReader` capability,
// and degrades to the whole-blob `OpenDerived` read otherwise, so a
// custom or CDN-direct library keeps working byte-for-byte.

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

/// Phase 468 — response-body copy buffer. Deliberately NOT
/// `MediaLibraryOptions.RangeChunkBytes`: that one sizes each read
/// against the blob store, this one only sizes each hop from the served
/// stream into the response, and 64 KiB is the BCL's own stream-copy
/// scale.
[<Literal>]
let private bodyBufferBytes = 65536

/// Copy a served stream into the response body in bounded chunks,
/// returning the bytes written.
///
/// An explicit loop rather than `Stream.CopyToAsync`, for two reasons
/// that are load-bearing rather than stylistic:
///
///   1. **One seam for what was actually served.** Every `200` / `206`
///      body in this module leaves through here, so the served byte
///      count is observable at a single point rather than inferred from
///      `Content-Length` (which is what the server INTENDED to send).
///   2. **Cancellation reaches the store.** The served stream is lazy —
///      it pulls the next window from blob storage on each read — so
///      honouring `ctx.RequestAborted` here is what stops an abandoned
///      scrub from paying for the rest of the range. `CopyToAsync`
///      with no token would drain it.
///
/// Fully async: no sync-over-async anywhere on the serve path (GP 7).
let private copyToBody (ctx: HttpContext) (stream: Stream) : Task<int64> = task {
    let buffer = ArrayPool<byte>.Shared.Rent bodyBufferBytes

    try
        let mutable written = 0L
        let mutable eof = false

        while not eof do
            let! read = stream.ReadAsync(Memory<byte>(buffer, 0, buffer.Length), ctx.RequestAborted)

            if read <= 0 then
                eof <- true
            else
                do! ctx.Response.Body.WriteAsync(ReadOnlyMemory<byte>(buffer, 0, read), ctx.RequestAborted)
                written <- written + int64 read

        return written
    finally
        ArrayPool<byte>.Shared.Return buffer
}

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
                        use body = stream
                        ctx.Response.StatusCode <- 200
                        ctx.Response.ContentLength <- Nullable total
                        let! _ = copyToBody ctx body
                        return Some ctx
                    | Error _ ->
                        ctx.SetStatusCode 404
                        return Some ctx
                | Satisfiable r ->
                    match! lib.OpenRange(container, id, r) |> Async.StartAsTask with
                    | Ok stream ->
                        use body = stream
                        ctx.Response.StatusCode <- 206
                        setHeader ctx "Content-Range" (sprintf "bytes %d-%d/%d" r.Start r.End total)
                        ctx.Response.ContentLength <- Nullable r.Length
                        let! _ = copyToBody ctx body
                        return Some ctx
                    | Error _ ->
                        ctx.Response.StatusCode <- 416
                        setHeader ctx "Content-Range" (sprintf "bytes */%d" total)
                        return Some ctx
    }

/// Phase 468 — serve a derived blob (HLS manifest / segment, poster)
/// from bounded ranged reads, the same way `serveOriginal` serves the
/// original. `total` has already been read from
/// `IMediaRangeReader.DerivedContentLength`, so a `416` needs no body
/// read at all and a `206` reads only its window.
let private serveDerivedRanged
    (ctx: HttpContext)
    (ranged: IMediaRangeReader)
    (container: string)
    (id: MediaId)
    (file: string)
    (total: int64)
    : Task<HttpContext option> =
    task {
        setHeader ctx "Accept-Ranges" "bytes"
        let parsed = ByteRange.parse (headerValue ctx "Range") total

        match parsed with
        | RangeRequest.Unsatisfiable ->
            ctx.Response.StatusCode <- 416
            setHeader ctx "Content-Range" (sprintf "bytes */%d" total)
            return Some ctx
        | NoRange
        | Satisfiable _ ->
            let window, status =
                match parsed with
                | Satisfiable r -> r, 206
                | _ -> { Start = 0L; End = total - 1L }, 200

            match! ranged.OpenDerivedRange(container, id, file, window) |> Async.StartAsTask with
            | Error _ ->
                ctx.SetStatusCode 404
                return Some ctx
            | Ok(stream, mime) ->
                use body = stream
                ctx.Response.ContentType <- mime
                ctx.Response.StatusCode <- status

                if status = 206 then
                    setHeader ctx "Content-Range" (sprintf "bytes %d-%d/%d" window.Start window.End total)

                ctx.Response.ContentLength <- Nullable window.Length
                let! _ = copyToBody ctx body
                return Some ctx
    }

/// Serve an in-memory byte buffer (HLS manifest / segment) with `Range`
/// support. Manifests are small, but players range-request segments.
/// The whole-blob path — taken when the composed `IMediaLibrary` does
/// not declare `IMediaRangeReader`, and when it does but the store will
/// not report the blob's size.
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
                let lib = library ctx
                let id = MediaId raw

                let serveWhole () = task {
                    match! lib.OpenDerived(container, id, file) |> Async.StartAsTask with
                    | Ok(bytes, mime) -> return! serveBytes ctx mime bytes
                    | Error _ ->
                        ctx.SetStatusCode 404
                        return Some ctx
                }

                // Phase 468 — bounded path when the library declares the
                // optional capability AND the store reports the blob's
                // size; whole-blob otherwise (which is also how an
                // absent blob reaches its 404).
                match box lib with
                | :? IMediaRangeReader as ranged ->
                    match! ranged.DerivedContentLength(container, id, file) |> Async.StartAsTask with
                    | Ok total when total > 0L -> return! serveDerivedRanged ctx ranged container id file total
                    | _ -> return! serveWhole ()
                | _ -> return! serveWhole ()
        })

/// All three media-serving handlers, in match order.
let handlers: HttpHandler list = [ streamHandler; signedHandler; hlsHandler ]