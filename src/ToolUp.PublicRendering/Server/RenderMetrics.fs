// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Metrics

// ─── Phase 93 — SSR render observability ─────────────────────────────
//
// Per-request render metrics emitted through the existing `IMetricsSink`
// (the sanctioned sync hot-path interface — GP 12 rule 2). Three signals:
//   * `publicrendering.render` (counter) + `publicrendering.render_ms`
//     (histogram) — per-request render count + duration, tagged by
//     `outcome` (rendered / not_modified / unauthorized / forbidden /
//     not_found / no_layout). The outcome tag yields the 304 rate and the
//     audience-denial counts directly.
//   * `publicrendering.render_cache` (counter) — cache hit / miss / stale,
//     already emitted by the Phase 84 cached path (the name is owned here
//     for coherence).
//   * `publicrendering.source_resolve_ms` (histogram) — per-content-source
//     resolve latency, via the opt-in `instrumentSource` decorator (a
//     deployment wraps the named sources it wants timed).
//
// **Zero cost when no sink (GP 13).** The SDK registers `NoOpMetricsSink`
// when metrics are disabled, and the page handler falls back to it when
// none is in DI; every emit is then an empty method body. The render-path
// `Stopwatch` is the only always-on work and is negligible (the same
// trade-off the SDK's `RequestTimingMiddleware` already makes).
module RenderMetrics =

    /// Counter — one increment per render attempt, tagged `outcome`.
    [<Literal>]
    let RenderCount = "publicrendering.render"

    /// Histogram (ms) — render duration, tagged `outcome`.
    [<Literal>]
    let RenderMs = "publicrendering.render_ms"

    /// Counter — render-cache outcome (hit / miss / stale). Emitted by the
    /// Phase 84 cached path.
    [<Literal>]
    let RenderCache = "publicrendering.render_cache"

    /// Histogram (ms) — per-content-source resolve latency, tagged
    /// `source`.
    [<Literal>]
    let SourceResolveMs = "publicrendering.source_resolve_ms"

    // ─── Phase 153 — crawler / SEO observability ─────────────────────

    /// Counter — conditional-GET outcome, tagged `outcome` (`"304"` /
    /// `"200"`). The `304`-rate (the metric large indexed-site runbooks
    /// chase) is the ratio of the two.
    [<Literal>]
    let ConditionalGet = "publicrendering.conditional_get"

    /// Counter — request count bucketed by a bounded crawler class, tagged
    /// `agent` (`"googlebot"` / `"bingbot"` / `"other-bot"` / `"human"`).
    /// Bounded cardinality — the raw User-Agent is never a tag value.
    [<Literal>]
    let RequestByAgent = "publicrendering.request_by_agent"

    /// Counter — `PageNotFound` fall-throughs (the `None` return the
    /// `RedirectMap` / terminal-404 chain then owns). A spike in
    /// crawler-hit not-found slugs is a soft-404 / stale-link signal.
    [<Literal>]
    let PageNotFound = "publicrendering.page_not_found"

    // ─── conditional-GET `outcome` tag values ────────────────────────
    [<Literal>]
    let CondGet304 = "304"

    [<Literal>]
    let CondGet200 = "200"

    // ─── bounded `agent` tag values ──────────────────────────────────
    [<Literal>]
    let AgentGooglebot = "googlebot"

    [<Literal>]
    let AgentBingbot = "bingbot"

    [<Literal>]
    let AgentOtherBot = "other-bot"

    [<Literal>]
    let AgentHuman = "human"

    // ─── `outcome` tag values ────────────────────────────────────────
    [<Literal>]
    let OutcomeRendered = "rendered"

    [<Literal>]
    let OutcomeNotModified = "not_modified"

    [<Literal>]
    let OutcomeUnauthorized = "unauthorized"

    [<Literal>]
    let OutcomeForbidden = "forbidden"

    [<Literal>]
    let OutcomeNotFound = "not_found"

    [<Literal>]
    let OutcomeNoLayout = "no_layout"

    /// Classify a completed render from the response status + handler
    /// result. `None` (the handler fell through) is a not-found; otherwise
    /// the written status code distinguishes the outcomes.
    let classifyOutcome (ctx: HttpContext) (result: HttpContext option) : string =
        match result with
        | None -> OutcomeNotFound
        | Some _ ->
            match ctx.Response.StatusCode with
            | 304 -> OutcomeNotModified
            | 401 -> OutcomeUnauthorized
            | 403 -> OutcomeForbidden
            | 500 -> OutcomeNoLayout
            | _ -> OutcomeRendered

    /// Emit the per-request render count + duration, tagged by outcome.
    let emitRender (metrics: IMetricsSink) (outcome: string) (elapsedMs: float) : unit =
        let tags = Map["outcome", outcome]
        metrics.Increment(RenderCount, tags)
        metrics.Record(RenderMs, elapsedMs, tags)

    /// Emit a render-cache outcome (hit / miss / stale).
    let emitCache (metrics: IMetricsSink) (outcome: string) : unit =
        metrics.Increment(RenderCache, Map["outcome", outcome])

    // ─── Phase 153 — crawler / SEO observability emitters ────────────

    /// Classify a request User-Agent into the bounded `agent` set via a
    /// cheap case-insensitive substring scan. `googlebot` / `bingbot`
    /// before the generic `bot` / `spider` / `crawl` / `slurp` catch (those
    /// UAs also contain `"bot"`); an empty / human UA falls to `human`.
    /// Bounded cardinality by construction — never returns the raw UA.
    let classifyAgent (userAgent: string) : string =
        if String.IsNullOrEmpty userAgent then
            AgentHuman
        else
            let ua = userAgent.ToLowerInvariant()

            if ua.Contains "googlebot" then
                AgentGooglebot
            elif ua.Contains "bingbot" then
                AgentBingbot
            elif
                ua.Contains "bot"
                || ua.Contains "spider"
                || ua.Contains "crawl"
                || ua.Contains "slurp"
            then
                AgentOtherBot
            else
                AgentHuman

    /// Emit a conditional-GET outcome (`"304"` / `"200"`).
    let emitConditionalGet (metrics: IMetricsSink) (outcome: string) : unit =
        metrics.Increment(ConditionalGet, Map["outcome", outcome])

    /// Emit a request bucketed by bounded crawler class.
    let emitAgent (metrics: IMetricsSink) (agentClass: string) : unit =
        metrics.Increment(RequestByAgent, Map["agent", agentClass])

    /// Emit a `PageNotFound` fall-through (soft-404 / stale-link signal).
    let emitNotFound (metrics: IMetricsSink) : unit =
        metrics.Increment(PageNotFound, Map.empty)

    /// Wrap a content source so each `Resolve` emits its latency under
    /// `source_resolve_ms` tagged `source=label`. Opt-in: a deployment
    /// wraps the sources it wants timed before `withContentSource`. The
    /// wrapper preserves `IEnumerableContentSource` — it delegates
    /// enumeration to the inner source when the inner implements it, and
    /// returns `[]` otherwise (byte-for-byte the pre-wrap discovery
    /// behaviour, since a non-enumerable source contributes nothing).
    let instrumentSource (metrics: IMetricsSink) (label: string) (source: IContentSource) : IContentSource =
        { new IContentSource with
            member _.Resolve slug ctx = async {
                let sw = System.Diagnostics.Stopwatch.StartNew()
                let! result = source.Resolve slug ctx
                sw.Stop()
                metrics.Record(SourceResolveMs, sw.Elapsed.TotalMilliseconds, Map["source", label])
                return result
            }

          interface IEnumerableContentSource with
              member _.EnumerateRoutes() =
                  match source with
                  | :? IEnumerableContentSource as e -> e.EnumerateRoutes()
                  | _ -> async { return [] }
        }