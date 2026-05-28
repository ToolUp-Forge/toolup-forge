// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Minimal logging interface for SDK-level diagnostics.
///
/// Implementations must be thread-safe — callers may invoke from any context
/// (request threads, background timers, DI construction, SSE streams). The
/// default server implementation is `ConsoleLogger`; swap via DI registration
/// to route logs to structured aggregators, rolling files, or cloud
/// observability services (Datadog, Splunk, CloudWatch, Stackdriver).
///
/// This interface is intentionally narrow. Extended interfaces (scoped
/// loggers, structured properties, log levels as data) can be added as
/// separate interfaces if and when needed without breaking existing callers.
type ILogger =
    /// Debug-level detail. Verbose; may be filtered out in production.
    abstract Debug: message: string -> unit

    /// Informational event. Normal operational signal — startup, shutdown,
    /// request flow, state transitions.
    abstract Info: message: string -> unit

    /// Warning. Something unexpected but recoverable — fallback activated,
    /// degraded behaviour, transient failure that was retried successfully.
    abstract Warn: message: string -> unit

    /// Error. A failure callers should be aware of. Pass the originating
    /// exception when one is available so the implementation can record the
    /// stack trace and any nested causes.
    abstract Error: message: string * ex: exn option -> unit

/// Optional capability for category-gated trace output. Loggers that opt
/// in implement BOTH `ILogger` and
/// `ITraceLogger`; callers reach the trace surface through the
/// `Logger.trace` helper below, which no-ops when the underlying logger
/// doesn't support it. Adding a separate capability interface (rather than a
/// new method on `ILogger`) keeps every existing implementation — including
/// the dozen or so test-double `{ new ILogger with ... }` object expressions
/// across the codebase — compiling unchanged.
///
/// Use a dotted-namespace category convention: `ai.sse`, `ai.agent`,
/// `platform.sse`, `auth`, etc. The default `ConsoleLogger` does straight-
/// string membership against `ServerConfig.TraceCategories` — no prefix
/// matching, no globbing — so spell categories consistently.
type ITraceLogger =
    abstract Trace: category: string * message: string -> unit

// `Logger.trace` helper lives in `Server/Logger.fs` (server-only) — Fable
// cannot type-test F# interfaces reliably, and the helper is only ever
// invoked from server-side F#. Keeping the runtime type test out of the
// shared compile unit silences a Fable warning without changing
// behaviour.