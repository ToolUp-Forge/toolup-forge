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
// Phase 471 adds a fourth, `GET /api/media/hls-key/{mediaId}` — see
// `HlsKeyDelivery`, which owns both the endpoint and the manifest
// rewrite this module applies on the way out.
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
//
// ─── Phase 473 — every body is counted where it is written ──────────
//
// Each serving path resolves ONE `PlaybackTelemetry.EgressAccount`
// before it writes, and the account is counted from the write sites
// rather than from `Content-Length`: a `206` the client abandons
// mid-window costs the origin what it actually sent, and that is the
// number a deployment bills or budgets against. `copyToBody` flushes in
// its `finally`, so an aborted scrub is metered rather than lost.
//
// **The count is ORIGIN egress.** With an edge in front (Phase 472) a
// cache hit never reaches this process, so these numbers are what left
// here — not what a viewer received. See the `PlaybackTelemetry` module
// header.
//
// With neither `IMetricsSink` nor `IUsageLog` composed, `accountFor`
// answers `EgressUnmetered` and the write sites do a tag test and
// nothing else — no allocation on the serve path at all (GP 13).

let private library (ctx: HttpContext) : IMediaLibrary =
    ctx.RequestServices.GetService(typeof<IMediaLibrary>) :?> IMediaLibrary

let private urlSigner (ctx: HttpContext) : SignedUrl.MediaUrlSigner =
    ctx.RequestServices.GetService(typeof<SignedUrl.MediaUrlSigner>) :?> SignedUrl.MediaUrlSigner

let private mediaScope (ctx: HttpContext) : StorageScope option =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> Some s
    | _ -> None

let private headerValue (ctx: HttpContext) (name: string) : string =
    match ctx.Request.Headers.TryGetValue name with
    | true, v -> v.ToString()
    | _ -> ""

let private setHeader (ctx: HttpContext) (name: string) (value: string) =
    ctx.Response.Headers[name] <- StringValues value

// ─── Phase 472 — declared edge cacheability ───────────────────────────
//
// These routes emitted no `Cache-Control` at all before this phase, so a
// CDN in front of them applied whatever heuristic it liked to a response
// carrying no directive — which for a segment might be "cache forever"
// and for a gated original might be the same. Declaring the posture per
// response class makes edge behaviour a decision.
//
// The declaration is read from the composed `MediaLibraryOptions`, whose
// default declares NOTHING, so an upgrading deployment emits exactly the
// headers it emitted before (GP 11). A deployment that never composed
// the media library never reaches this code at all.
//
// **The key route is not reachable from here and has no knob.**
// `/api/media/hls-key/{id}` is served by `HlsKeyDelivery.keyHandler`,
// which hard-wires `no-store` + `Pragma: no-cache`. A cached decryption
// key is the encryption scheme defeated, so the one class where a wrong
// declaration would be catastrophic is the one class this record cannot
// express.

/// The composed options, or the defaults when none is registered (which
/// is what a hand-built test host or a pre-472 composition looks like).
/// Never throws: a serving path must not 500 because a knob is absent.
let private mediaOptions (ctx: HttpContext) : MediaLibraryOptions =
    match ctx.RequestServices.GetService(typeof<MediaLibraryOptions>) with
    | :? MediaLibraryOptions as o -> o
    | _ -> MediaLibraryOptions.defaults

/// Emit the declared `Cache-Control`, or no header at all for
/// `EdgeCacheUnset`.
let private declareCacheability (ctx: HttpContext) (cacheability: EdgeCacheability) =
    match EdgeCacheHeader.render cacheability with
    | Some value -> setHeader ctx "Cache-Control" value
    | None -> ()

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
///   1. **One seam for what was actually served.** Every streamed
///      `200` / `206` body in this module leaves through here, so the
///      served byte count is observable at a single point rather than
///      inferred from `Content-Length` (which is what the server
///      INTENDED to send).
///   2. **Cancellation reaches the store.** The served stream is lazy —
///      it pulls the next window from blob storage on each read — so
///      honouring `ctx.RequestAborted` here is what stops an abandoned
///      scrub from paying for the rest of the range. `CopyToAsync`
///      with no token would drain it.
///
/// Phase 473 attaches egress accounting to reason 1: `account` is
/// counted per chunk and flushed in the `finally`, so an aborted scrub
/// meters what it actually cost rather than what it promised. The
/// account is `EgressUnmetered` — a singleton — when neither telemetry
/// sink is composed, and both calls are then a tag test with no
/// allocation (GP 13).
///
/// Fully async: no sync-over-async anywhere on the serve path (GP 7).
let private copyToBody (ctx: HttpContext) (account: PlaybackTelemetry.EgressAccount) (stream: Stream) : Task<int64> = task {
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
                PlaybackTelemetry.count account read

        return written
    finally
        ArrayPool<byte>.Shared.Return buffer
        PlaybackTelemetry.flush account
}

/// Serve a stored original through the library's `ContentLength` +
/// `OpenRange` (blob layout stays encapsulated). Emits `200` / `206` /
/// `416` per the `Range` header.
let private serveOriginal
    (ctx: HttpContext)
    (lib: IMediaLibrary)
    (scopeId: string)
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
                // Phase 472 — the original is scope- or signature-gated,
                // so its declaration is the `Original` class.
                declareCacheability ctx (mediaOptions ctx).EdgeCache.Original
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

                        let! _ =
                            copyToBody
                                ctx
                                (PlaybackTelemetry.accountFor ctx id scopeId PlaybackTelemetry.ClassOriginal)
                                body

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

                        let! _ =
                            copyToBody
                                ctx
                                (PlaybackTelemetry.accountFor ctx id scopeId PlaybackTelemetry.ClassOriginal)
                                body

                        return Some ctx
                    | Error _ ->
                        ctx.Response.StatusCode <- 416
                        setHeader ctx "Content-Range" (sprintf "bytes */%d" total)
                        return Some ctx
    }

/// Serve an in-memory byte buffer (HLS manifest / segment) with `Range`
/// support. Manifests are small, but players range-request segments.
/// The whole-blob path — taken when the composed `IMediaLibrary` does
/// not declare `IMediaRangeReader`, when it does but the store will
/// not report the blob's size, and (Phase 471) for any manifest, whose
/// body the serve path may rewrite.
///
/// Phase 473 — this path does NOT go through `copyToBody` (there is no
/// stream to pace: the bytes are already in hand), so it meters its own
/// write. The count is the window's own length rather than the buffer's,
/// which is what a `206` from here actually puts on the wire.
let private serveBytes
    (ctx: HttpContext)
    (account: PlaybackTelemetry.EgressAccount)
    (mime: string)
    (bytes: byte[])
    : Task<HttpContext option> =
    task {
        let total = int64 bytes.Length
        setHeader ctx "Accept-Ranges" "bytes"
        ctx.Response.ContentType <- mime

        match ByteRange.parse (headerValue ctx "Range") total with
        | NoRange ->
            ctx.Response.StatusCode <- 200
            ctx.Response.ContentLength <- Nullable total
            do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length)
            PlaybackTelemetry.count account bytes.Length
            PlaybackTelemetry.flush account
            return Some ctx
        | Satisfiable r ->
            ctx.Response.StatusCode <- 206
            setHeader ctx "Content-Range" (sprintf "bytes %d-%d/%d" r.Start r.End total)
            ctx.Response.ContentLength <- Nullable r.Length
            do! ctx.Response.Body.WriteAsync(bytes, int r.Start, int r.Length)
            PlaybackTelemetry.count account (int r.Length)
            PlaybackTelemetry.flush account
            return Some ctx
        | RangeRequest.Unsatisfiable ->
            ctx.Response.StatusCode <- 416
            setHeader ctx "Content-Range" (sprintf "bytes */%d" total)
            return Some ctx
    }

/// Phase 471 — read a derived manifest whole and rewrite its
/// `#EXT-X-KEY` URIs to origin-absolute form before serving it.
///
/// Manifests are the one derived blob whose CONTENT the origin must
/// still own after the bytes leave. The key URI is written at transcode
/// time as a root-relative path, which resolves against whatever host
/// serves the manifest — and once the rendition is statically exported
/// or sitting on a CDN edge, that host is not this one. Rewriting here
/// is what keeps the key fetch pointed at the gate no matter where the
/// segments physically landed.
///
/// It leaves through `serveBytes` rather than the streaming path
/// because the rewrite changes the body's length, so a `Content-Range`
/// computed from the STORED size would be a lie. The two paths are
/// otherwise header-for-header identical — same `Accept-Ranges`, same
/// `200` / `206` / `416` decision over the same parsed `Range` — and a
/// manifest is a few hundred bytes, so nothing is bounded differently
/// in practice. It is also what `hlsHandler`'s whole-blob fallback has
/// always done for manifests.
///
/// **A manifest carrying no key tag comes back byte-for-byte**:
/// `rewriteKeyUris` short-circuits to the same string, and the ORIGINAL
/// bytes are then served rather than a re-encoding of them, so an
/// unencrypted deployment sees exactly the bytes and headers it saw
/// before this phase.
let private serveManifestRewritten
    (ctx: HttpContext)
    (account: PlaybackTelemetry.EgressAccount)
    (ranged: IMediaRangeReader)
    (container: string)
    (id: MediaId)
    (file: string)
    (total: int64)
    : Task<HttpContext option> =
    task {
        match!
            ranged.OpenDerivedRange(container, id, file, { Start = 0L; End = total - 1L })
            |> Async.StartAsTask
        with
        | Error _ ->
            ctx.SetStatusCode 404
            return Some ctx
        | Ok(stream, mime) ->
            use body = stream
            use buffer = new MemoryStream()
            do! body.CopyToAsync(buffer, ctx.RequestAborted)
            let original = buffer.ToArray()
            let text = Text.Encoding.UTF8.GetString original

            let rewritten =
                HlsKeyDelivery.rewriteKeyUris (HlsKeyDelivery.absoluteKeyUri ctx id) text

            let served =
                if obj.ReferenceEquals(rewritten, text) then
                    original
                else
                    Text.Encoding.UTF8.GetBytes rewritten

            return! serveBytes ctx account mime served
    }

/// Phase 468 — serve a derived blob (HLS manifest / segment, poster)
/// from bounded ranged reads, the same way `serveOriginal` serves the
/// original. `total` has already been read from
/// `IMediaRangeReader.DerivedContentLength`, so a `416` needs no body
/// read at all and a `206` reads only its window.
///
/// Phase 471 diverts manifests to `serveManifestRewritten` first; every
/// other derived blob (segments, posters) takes the bounded path
/// unchanged.
let private serveDerivedRanged
    (ctx: HttpContext)
    (account: PlaybackTelemetry.EgressAccount)
    (ranged: IMediaRangeReader)
    (container: string)
    (id: MediaId)
    (file: string)
    (total: int64)
    : Task<HttpContext option> =
    task {
        if HlsKeyDelivery.isManifest file then
            return! serveManifestRewritten ctx account ranged container id file total
        else
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
                    let! _ = copyToBody ctx account body
                    return Some ctx
    }

/// Scoped (authenticated) stream — `GET /api/media/stream/{mediaId}`.
let streamHandler: HttpHandler =
    GET
    >=> routef "/api/media/stream/%s" (fun raw ->
        fun (_: HttpFunc) (ctx: HttpContext) -> task {
            match mediaScope ctx with
            | None ->
                ctx.SetStatusCode 401
                return Some ctx
            | Some scope -> return! serveOriginal ctx (library ctx) scope.ScopeId scope.Container (MediaId raw)
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
                | Ok payload ->
                    // Phase 473 — the signature's OWN payload carries the
                    // attribution, so a signed serve meters against the
                    // scope that minted the token rather than against
                    // whatever ambient scope (if any) the request has.
                    return! serveOriginal ctx (library ctx) payload.ScopeId payload.Container (MediaId raw)
        })

/// Scoped HLS manifest / segment serving — `GET
/// /api/media/hls/{mediaId}/{file}`. Serves the produced rendition blobs
/// when a transcode sub-companion has run; absent otherwise (the item is
/// streamed as a single-file progressive download via `streamHandler`).
let hlsHandler: HttpHandler =
    GET
    >=> routef "/api/media/hls/%s/%s" (fun (raw, file) ->
        fun (_: HttpFunc) (ctx: HttpContext) -> task {
            match mediaScope ctx with
            | None ->
                ctx.SetStatusCode 401
                return Some ctx
            | Some scope ->
                let container = scope.Container
                let lib = library ctx
                let id = MediaId raw

                // Phase 473 — one account per response, resolved before
                // either serving path runs, for the same reason the edge
                // declaration below is decided once: both paths write the
                // same derived file, so classifying it twice is two
                // chances to disagree.
                let account =
                    PlaybackTelemetry.accountFor ctx id scope.ScopeId (PlaybackTelemetry.responseClassForDerived file)

                // Phase 472 — declare the edge posture ONCE, here,
                // before either serving path runs. Both paths write the
                // same derived file, so deciding the class twice is two
                // chances to disagree; `edgeCacheabilityForDerived` is
                // pure and keyed on the same extension test the rewrite
                // uses.
                declareCacheability ctx (MediaLibraryOptions.edgeCacheabilityForDerived (mediaOptions ctx) file)

                let serveWhole () = task {
                    match! lib.OpenDerived(container, id, file) |> Async.StartAsTask with
                    | Ok(bytes, mime) ->
                        // Phase 471 — the rewrite belongs to SERVING, not
                        // to the bounded path, so the whole-blob fallback
                        // applies it too. A library that declares no
                        // `IMediaRangeReader` must not hand out manifests
                        // whose key URI resolves against the CDN.
                        if HlsKeyDelivery.isManifest file then
                            let text = Text.Encoding.UTF8.GetString bytes

                            let rewritten =
                                HlsKeyDelivery.rewriteKeyUris (HlsKeyDelivery.absoluteKeyUri ctx id) text

                            let served =
                                if obj.ReferenceEquals(rewritten, text) then
                                    bytes
                                else
                                    Text.Encoding.UTF8.GetBytes rewritten

                            return! serveBytes ctx account mime served
                        else
                            return! serveBytes ctx account mime bytes
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
                    | Ok total when total > 0L -> return! serveDerivedRanged ctx account ranged container id file total
                    | _ -> return! serveWhole ()
                | _ -> return! serveWhole ()
        })

/// All media-serving handlers, in match order.
///
/// Phase 471 appends the HLS key endpoint. Its literal prefix
/// (`/api/media/hls-key/`) cannot be confused with `hlsHandler`'s
/// (`/api/media/hls/`), so order is not load-bearing here — it is
/// listed beside the route it serves keys for.
let handlers: HttpHandler list = [ streamHandler; signedHandler; hlsHandler; HlsKeyDelivery.keyHandler ]