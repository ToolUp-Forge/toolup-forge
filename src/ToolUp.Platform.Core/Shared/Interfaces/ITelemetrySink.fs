// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 163 — ITelemetrySink ─────────────────────────────────────────
//
// A clean seam for **end-user product telemetry** (page views, funnel
// events, feature usage) — deliberately distinct from the two existing
// event seams it is often confused with:
//   * `IEventStore` / `IAuditLog` — the system-of-record for audit /
//     domain events (durable, queryable, compliance-bearing).
//   * `IMetricsSink` — operational metrics (counters / gauges / timings).
// Product analytics is none of those: it is fire-and-forget behavioural
// data shipped to an external analytics product (GA4, Mixpanel, Segment).
// Without this seam consumers hand-roll it and leak a third-party script
// into the SDK client bundle by default.
//
// **No-op default** — `NoOpTelemetrySink` is a true no-op (no allocation
// beyond the returned `Async`, no network), so a deployment that composes
// no analytics pays nothing (GP 13). Opt-in companions
// (`ToolUp.TelemetrySinks.Ga4`, …) ship the events out (GP 1 — the vendor
// transport never reaches `ToolUp.Platform.*`; GP 2 — the reference GA4
// sink is BCL `HttpClient`, no paid SDK).
//
// **PII by construction = none.** `TelemetryEvent.Properties` are
// operator-declared string keys — the SDK never auto-populates a user
// identifier, email, or any subject PII into a telemetry event. Consent
// gating (suppress analytics when the end user has not consented) rides the
// **client-side** `Telemetry.track` helper against the client-tier
// `IConsentProvider`, before any event leaves the browser — analytics that
// never ships can never breach consent.
//
// **Six portability rules (GP 12).** Identity by value (`scopeId` string,
// an event record with a `Map<string,string>` property bag); async at the
// boundary; no retry/callback semantics leaked (a sink that batches/retries
// owns that internally); stateless between `Track` calls; ordering promised
// only within a scope; no timing-precision contract (telemetry is not the
// system-of-record — GP 6).

/// A single product-telemetry event: a named event plus operator-declared
/// string properties (wire-safe primitives; never SDK-populated PII).
type TelemetryEvent = {
    /// Event name (e.g. `"page_view"`, `"report_exported"`).
    Event: string
    /// Operator-declared properties — primitive, wire-safe, no PII.
    Properties: Map<string, string>
}

/// End-user behavioural-analytics sink. `Track` is fire-and-forget-shaped
/// but async at the boundary so a sink can await its transport. Scope-tagged
/// (per-tenant) by `scopeId`. Default: `NoOpTelemetrySink` (a true no-op).
type ITelemetrySink =
    /// Stable sink identifier (diagnostics / compose-time duplicate guard).
    abstract Name: string

    /// Record `event` under `scopeId`. Implementations MUST NOT throw across
    /// the boundary for a delivery failure — telemetry is best-effort and
    /// never breaks the request that emitted it; surface failures via the
    /// sink's own logging.
    abstract Track: scopeId: string * event: TelemetryEvent -> Async<unit>

/// The default no-op telemetry sink — a true no-op (no allocation beyond the
/// returned `Async`, no network). Composed when
/// `ServerConfig.TelemetrySink = NoTelemetrySink` (the default), so emission
/// sites are free at runtime (GP 13).
type NoOpTelemetrySink() =
    interface ITelemetrySink with
        member _.Name = "noop"
        member _.Track(_scopeId: string, _event: TelemetryEvent) = async { return () }