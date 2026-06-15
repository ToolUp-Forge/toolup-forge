// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open System
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open ToolUp.Platform.Metrics

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
// The combinator was extracted byte-for-byte in [Phase 155]; [Phase 147]
// hardens this single seam for crawl-budget correctness — and because the
// page handler routes through it, the fixes flow to both callers:
//   * **weak ETag** (`W/"…"`) — required under the pipeline's
//     `UseResponseCompression`, which rewrites the body across content
//     codings (RFC 7232 §2.1; a strong ETag asserts byte equality).
//   * the **`If-Modified-Since` union** — Googlebot predominantly
//     revalidates with `If-Modified-Since`, not `If-None-Match`.
//   * **second-granularity** `Last-Modified` truncation so the emitted
//     value round-trips equal against the `If-Modified-Since` echoed back.
// The caller supplies a *content-stable* `lastModified` (a publish date or
// the `deployStamp` below) — never a wall-clock render moment — or a
// deterministic resource can never `304`.
module ConditionalGet =

    /// Wrap a bare content hash as a **weak** ETag wire value (`W/"…"`).
    /// Weak rather than strong because the SDK pipeline runs
    /// `UseResponseCompression`: content-coding negotiation (gzip / br /
    /// identity) rewrites the body, so RFC 7232 requires the weak form
    /// under transfer/content-coding variance.
    let etagOf (hash: string) : string = "W/\"" + hash + "\""

    /// Normalise an `If-None-Match` candidate (or our own ETag) to its
    /// opaque tag for weak comparison (RFC 7232 §2.3.2 — `If-None-Match`
    /// uses the weak comparison function): strip an optional `W/` weakness
    /// indicator and the surrounding quotes, leaving the bare hash.
    let private opaqueTag (candidate: string) : string =
        let t = candidate.Trim()
        let t = if t.StartsWith "W/" then t.Substring 2 else t
        t.Trim('"')

    /// True when the request's `If-None-Match` matches the current ETag
    /// (weak comparison) or is the wildcard `*`. Honours a comma-separated
    /// list and an unquoted hash for tolerance.
    let private ifNoneMatchMatches (ctx: HttpContext) (hash: string) : bool =
        match ctx.Request.Headers.TryGetValue "If-None-Match" with
        | true, values ->
            values
            |> Seq.collect (fun v -> (v: string).Split(',') |> Array.map _.Trim())
            |> Seq.exists (fun candidate -> candidate.Trim() = "*" || opaqueTag candidate = hash)
        | _ -> false

    /// Truncate a timestamp to whole-second precision in UTC. HTTP dates
    /// (`Last-Modified` / `If-Modified-Since`) carry no sub-second
    /// component, so a resource's `Last-Modified` must be truncated to
    /// round-trip equal against the second-granularity `If-Modified-Since`
    /// a client echoes back.
    let toHttpSeconds (dt: DateTimeOffset) : DateTimeOffset =
        let utc = dt.UtcDateTime
        DateTimeOffset(utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond)), TimeSpan.Zero)

    /// True when a valid `If-Modified-Since` says the resource is unchanged
    /// — its `lastModified` (truncated to seconds) is at or before the
    /// client's `If-Modified-Since` instant (RFC 7232 §3.3, second-
    /// granularity comparison). A malformed / unparseable header is ignored
    /// (not-modified = false → full response), never an error.
    let private ifModifiedSinceSatisfied (ctx: HttpContext) (lastModified: DateTimeOffset) : bool =
        match ctx.Request.Headers.TryGetValue "If-Modified-Since" with
        | true, values when values.Count > 0 ->
            match
                DateTimeOffset.TryParse(
                    values[0],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                )
            with
            | true, since -> toHttpSeconds lastModified <= since
            | _ -> false
        | _ -> false

    /// True when the request's conditional headers permit a `304`: the
    /// **union** of the gates (RFC 7232) — `If-None-Match` matches the ETag
    /// (weak comparison) OR `If-Modified-Since` is at/after the resource's
    /// content-stable `lastModified`.
    let isNotModified (ctx: HttpContext) (hash: string) (lastModified: DateTimeOffset) : bool =
        ifNoneMatchMatches ctx hash || ifModifiedSinceSatisfied ctx lastModified

    /// Emit the conditional-GET validators + `Cache-Control` on the
    /// response: a weak `ETag` over `hash`, a second-granularity
    /// `Last-Modified` from `lastModified`, and the supplied
    /// `cacheControl`.
    let setValidators (ctx: HttpContext) (hash: string) (lastModified: DateTimeOffset) (cacheControl: string) : unit =
        ctx.Response.Headers["ETag"] <- StringValues(etagOf hash)
        ctx.Response.Headers["Last-Modified"] <- StringValues((toHttpSeconds lastModified).UtcDateTime.ToString("R"))
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

    /// `cacheable` plus the Phase 153 crawler-observability emits, so a
    /// programmatic-SSR consumer that adopts this combinator (rather than
    /// composing `PublicPageHandler`) still gets the SEO metrics for free:
    /// `publicrendering.conditional_get{outcome=304|200}` (the Googlebot
    /// 304-rate large indexed-site runbooks chase) and
    /// `publicrendering.request_by_agent{agent=…}` (bounded crawler-class
    /// buckets via a cheap UA substring scan; the raw UA is never a tag).
    ///
    /// Wire behaviour is byte-for-byte identical to `cacheable` — the emits
    /// are side-effects on the supplied `IMetricsSink`. Pass a real sink to
    /// opt in; a `NoOpMetricsSink` makes every emit an empty method body, so
    /// this is the same zero-cost-when-unused shape `cacheable` has
    /// (GP 13) — there is no behavioural reason to keep using the bare
    /// `cacheable` once a sink is available.
    let cacheableWithMetrics
        (metrics: IMetricsSink)
        (etag: string)
        (lastModified: DateTimeOffset)
        (cacheControl: string)
        : HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) ->
            setValidators ctx etag lastModified cacheControl

            let userAgent =
                match ctx.Request.Headers.TryGetValue "User-Agent" with
                | true, v -> v.ToString()
                | _ -> ""

            RenderMetrics.emitAgent metrics (RenderMetrics.classifyAgent userAgent)

            if isNotModified ctx etag lastModified then
                ctx.Response.StatusCode <- 304
                RenderMetrics.emitConditionalGet metrics RenderMetrics.CondGet304
                earlyReturn ctx
            else
                RenderMetrics.emitConditionalGet metrics RenderMetrics.CondGet200
                next ctx

    // ─── Content-version stamp (Phase 147) ───────────────────────────

    /// A process-stable deploy-generation timestamp: the last-write time of
    /// the entry assembly (truncated to whole seconds). Stable across
    /// restarts of the same deployed binary and identical across every
    /// render, so it is a sound content-stable `Last-Modified` fallback for
    /// a resource that carries no intrinsic publish date — the single
    /// content-version stamp model. Changes only on redeploy (which
    /// cold-caches anyway). Falls back to the Unix epoch when the entry
    /// assembly can't be located (e.g. some test hosts) — still stable
    /// within the process.
    let deployStamp: DateTimeOffset =
        let stamp =
            try
                match System.Reflection.Assembly.GetEntryAssembly() with
                | null -> DateTimeOffset.UnixEpoch
                | asm ->
                    let path = asm.Location

                    if String.IsNullOrEmpty path || not (IO.File.Exists path) then
                        DateTimeOffset.UnixEpoch
                    else
                        DateTimeOffset(IO.File.GetLastWriteTimeUtc path, TimeSpan.Zero)
            with _ ->
                DateTimeOffset.UnixEpoch

        toHttpSeconds stamp