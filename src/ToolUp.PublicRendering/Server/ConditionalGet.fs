// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open System
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives

// ─── Phase 155 — handler-agnostic HTTP conditional-GET primitive ─────
//
// A reusable Giraffe combinator (`cacheable` / `immutableAsset`) plus the
// lower-level helpers (`setValidators` / `isNotModified`) it is built from.
// Lifted out of `PublicPageHandler` so any Giraffe route — not just the
// content-file page handler — can emit `ETag` / `Last-Modified` /
// `Cache-Control` validators and short-circuit a conditional re-request to
// `304`. A programmatic-SSR consumer (one rendering from domain data rather
// than the content-file pipeline) wraps an expensive deterministic render
// with `cacheable contentHash lastModified cacheControl >=> body`.
//
// `PublicPageHandler` is the first caller, not the owner — it calls
// `setValidators` / `isNotModified` inline on its cached + uncached paths
// so the 304 logic lives in one place.
//
// **Phase 155 is a pure extraction**: the helpers reproduce the
// pre-extraction `PublicPageHandler` behaviour exactly — a strong ETag and
// an `If-None-Match`-only gate — so the page handler stays byte-for-byte.
// [Phase 147] then hardens this single seam (weak ETag, the
// `If-Modified-Since` union, content-stable `Last-Modified`), and because
// the page handler routes through it the correctness fixes flow to both
// callers. The `lastModified` parameter is accepted now (though the 155
// gate only consults `If-None-Match`) so the 147 hardening is a body-only
// change with no call-site churn.
module ConditionalGet =

    /// Wrap a bare content hash as an ETag wire value. Phase 155 emits a
    /// strong ETag (`"…"`) — the pre-extraction behaviour. [Phase 147]
    /// switches this to the weak form (`W/"…"`) required under the
    /// pipeline's `UseResponseCompression` content-coding negotiation.
    let etagOf (hash: string) : string = "\"" + hash + "\""

    /// True when the request's `If-None-Match` matches the current ETag
    /// (or is the wildcard `*`). Honours a comma-separated list and an
    /// unquoted hash for tolerance.
    let private ifNoneMatchMatches (ctx: HttpContext) (hash: string) : bool =
        match ctx.Request.Headers.TryGetValue "If-None-Match" with
        | true, values ->
            let etag = etagOf hash

            values
            |> Seq.collect (fun v -> (v: string).Split(',') |> Array.map _.Trim())
            |> Seq.exists (fun candidate -> candidate = etag || candidate = hash || candidate = "*")
        | _ -> false

    /// True when the request's conditional headers permit a `304`. Phase
    /// 155: `If-None-Match` only (the pre-extraction gate). [Phase 147]
    /// adds the `If-Modified-Since` union over `lastModified`.
    let isNotModified (ctx: HttpContext) (hash: string) (_lastModified: DateTimeOffset) : bool =
        ifNoneMatchMatches ctx hash

    /// Emit the conditional-GET validators + `Cache-Control` on the
    /// response: an `ETag` over `hash`, a `Last-Modified` from
    /// `lastModified`, and the supplied `cacheControl`.
    let setValidators (ctx: HttpContext) (hash: string) (lastModified: DateTimeOffset) (cacheControl: string) : unit =
        ctx.Response.Headers["ETag"] <- StringValues(etagOf hash)
        ctx.Response.Headers["Last-Modified"] <- StringValues(lastModified.UtcDateTime.ToString("R"))
        ctx.Response.Headers["Cache-Control"] <- StringValues cacheControl

    /// Handler-agnostic conditional-GET combinator. Emits the validators
    /// for `(etag, lastModified, cacheControl)`, then either short-circuits
    /// to `304 Not Modified` (when the request's conditional headers are
    /// satisfied) or continues to the wrapped body handler:
    ///
    ///     route "/report"
    ///     >=> ConditionalGet.cacheable contentHash deployStamp "public, max-age=300"
    ///     >=> renderReport
    ///
    /// `etag` is the bare content hash (the combinator wraps it as an ETag
    /// for the wire and compares against the request's `If-None-Match`).
    let cacheable (etag: string) (lastModified: DateTimeOffset) (cacheControl: string) : HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) ->
            setValidators ctx etag lastModified cacheControl

            if isNotModified ctx etag lastModified then
                ctx.Response.StatusCode <- 304
                earlyReturn ctx
            else
                next ctx

    /// Convenience for immutable, content-addressed assets (fingerprinted
    /// filenames whose URL changes when their bytes do): a one-year
    /// `max-age` + `immutable`, plus the conditional-GET validators. The
    /// fingerprint doubles as the ETag content hash.
    let immutableAsset (fingerprint: string) (lastModified: DateTimeOffset) : HttpHandler =
        cacheable fingerprint lastModified "public, max-age=31536000, immutable"