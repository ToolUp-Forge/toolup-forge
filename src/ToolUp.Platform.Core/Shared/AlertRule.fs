// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── AlertRule — Phase 178 threshold-rule data ───────────────────────
//
// A neutral, sector-agnostic threshold rule: "when this signal crosses
// this bound for this long, deliver a notification". The rule is pure
// data — identity by value, no framework handles, no domain vocabulary
// (it names metrics and health probes, never business concepts, GP 1).
// It lives in the Fable-safe Shared layer (GP 10) so a future admin UI
// can author / render the same shape the server evaluates.
//
// The *evaluation* engine (`AlertRuleEngine`) is server-tier; this file
// carries only the declarative rule. The engine reads the metric /
// health signals the SDK already emits (`IMetricsSink` accumulator via
// Phase 9e, `IHealthCheck` state via Phase 9k) and delivers through the
// existing notification family (`INotificationChannel` for in-app /
// SSE, the transactional dispatcher for out-of-band email / SMS / push).
//
// Six-rule portability audit (GP 12):
//   1. Identity by value      — every field is a value type (string,
//                                 float, TimeSpan, Map, DU). No handles.
//   2. Async                  — N/A: this is data, not a boundary. The
//                                 async surface is the engine's delivery
//                                 (`INotificationChannel.Publish`).
//   3. Retry as data          — delivery retry/back-off lives in the
//                                 notification impls the rule targets,
//                                 not on the rule.
//   4. Stateless              — the rule carries no runtime state; the
//                                 engine keeps the per-rule breach window
//                                 (documented mutable, server-tier).
//   5. No cross-shard ordering — rules are independent; evaluation order
//                                 is not observable.
//   6. Precision at the floor — `ForDuration` is honoured at the engine's
//                                 `JobPrecision.Minute` tick floor (the
//                                 `INotificationChannel` precision
//                                 contract); sub-minute alerting needs a
//                                 different transport.

/// What signal an `AlertRule` watches. Both variants name the signal by
/// its stable string key — the metric name (+ its tag set) or the health
/// probe name — never a domain concept.
type AlertSource =
    /// A metric emitted through `IMetricsSink`, identified by its
    /// registered `name` and the `tags` selecting the specific series.
    /// The engine reads the current accumulated value (counter total /
    /// gauge value) for `(name, tags)`.
    | Metric of name: string * tags: Map<string, string>
    /// A health probe contributed via `IHealthCheck`, identified by its
    /// `IHealthCheck.Name`. The engine runs the probe on each tick and
    /// evaluates the resulting `HealthResult`.
    | HealthProbe of probeName: string

/// The condition a reading must satisfy to count as a breach. Numeric
/// comparisons (`GreaterThan` / `LessThan` / `Equals`) apply to a
/// `Metric` source's scalar value; `ProbeUnhealthy` / `ProbeDegraded`
/// apply to a `HealthProbe` source's state. A condition paired with an
/// incompatible source (e.g. `ProbeUnhealthy` on a `Metric`) is inert —
/// it never breaches (documented; a compose-time validator is a
/// follow-up).
type ThresholdCondition =
    /// Metric value strictly greater than the bound.
    | GreaterThan of float
    /// Metric value strictly less than the bound.
    | LessThan of float
    /// Metric value exactly equal to the bound (exact float equality —
    /// intended for discrete gauges, e.g. a "0 = down / 1 = up" signal).
    | Equals of float
    /// Health probe reporting `Unhealthy`.
    | ProbeUnhealthy
    /// Health probe reporting `Degraded` *or worse* (`Degraded` or
    /// `Unhealthy`) — the "at least degraded" ladder rung.
    | ProbeDegraded

/// Where a fired alert is delivered. A rule may declare several.
type AlertDelivery =
    /// In-app / real-time delivery. Publishes a `SystemMessage`
    /// notification (at the rule's `Severity`) under `scopeId` via
    /// `INotificationChannel` — every subscriber on that scope (SSE
    /// clients, in-process consumers) sees it.
    | ViaChannel of scopeId: string
    /// Out-of-band delivery. Publishes the transactional notification
    /// matching `SinkKind` (`TransactionalEmail` / `TransactionalSms` /
    /// `MobilePush`); the `DispatchingNotificationChannel` decorator
    /// routes it to the registered `INotificationSink` of that kind.
    | ViaSink of NotificationKind.SinkKind

/// A declarative alert rule. `Name` is the rule's identity (must be
/// unique within a deployment — the engine keys its per-rule breach
/// window on it). `ForDuration` is the debounce window: the breach must
/// persist across it before the rule fires, absorbing transient blips.
/// `Severity` styles the `SystemMessage` for `ViaChannel` delivery.
type AlertRule = {
    Name: string
    Source: AlertSource
    Condition: ThresholdCondition
    ForDuration: TimeSpan
    Severity: SystemMessageLevel
    DeliverVia: AlertDelivery list
}

module AlertRule =
    /// The empty rule set — the `ServerConfig.AlertRules` default. An
    /// empty set means the engine's `BackgroundService` is never hosted
    /// (GP 13: no rules ⇒ zero runtime cost).
    let none: AlertRule list = []