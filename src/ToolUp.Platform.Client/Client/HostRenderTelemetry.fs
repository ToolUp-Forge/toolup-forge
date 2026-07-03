// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.HostRenderTelemetry

open ToolUp.Platform

// ─── Phase 268 — client-tier host-render telemetry (forwarding default) ─
//
// The neutral CONTRACT — `HostRenderFault` + `IHostRenderTelemetrySink` +
// `NoOpHostRenderTelemetrySink` — lives in Core (`HostRenderFault.fs`) so
// both the client render path and the SSR boundary (Phase 273) report
// through one sink. This module ships the CLIENT-tier forwarding default:
// each captured fault is written to the client structured logger (the
// console-backed `ILogger`), so a render fault becomes a devtools warning
// with a grep-handle instead of a swallowed exception. It also bridges the
// Phase 270 capability-negotiation mismatch onto the sink, and exposes a
// counting decorator the client boot-degradation / health surface can read
// to make a render-fault spike visible (Phase 121 precedent).
//
// Nothing here is composed by default: a host that wires no sink keeps the
// Core `NoOpHostRenderTelemetrySink` and pays nothing (GP 13). A host that
// wires `defaultSink` opts into observability explicitly.

/// A forwarding sink: each captured fault is written to `logger.Warn`
/// (via `HostRenderFault.describe`, the same stable one-line form the SSR
/// fallback renders). Tier-neutral over `ILogger`; the client passes its
/// console logger, a server host could pass its structured logger.
let forwardingToLogger (logger: ILogger) : IHostRenderTelemetrySink =
    { new IHostRenderTelemetrySink with
        member _.Capture(fault: HostRenderFault) =
            logger.Warn(HostRenderFault.describe fault)
    }

/// The client forwarding default: forwards every fault to the console
/// logger under the `client.host-render` category, so a hosted-tree fault
/// surfaces in devtools with a stable grep-handle. Wire this (or a custom
/// sink) into a hosted view; a host that wants the old silence keeps the
/// Core `NoOpHostRenderTelemetrySink` instead.
let defaultSink: IHostRenderTelemetrySink =
    forwardingToLogger (Logger.forCategory "client.host-render")

/// Bridge a Phase 270 capability-negotiation mismatch onto a Phase 268
/// sink: report the mismatch as a `RenderFault` against `nodeId`. This is
/// the concrete wiring the `ClientHostNegotiatedView.withNegotiatedElementView`
/// `onMismatch` hook documents — pass `HostRenderTelemetry.onMismatch sink
/// nodeId` where it previously took `ignore`, and a mount-time capability
/// gap is captured through the same sink as a render fault.
let onMismatch (sink: IHostRenderTelemetrySink) (nodeId: string) : HostCapabilityMismatch -> unit =
    fun mismatch -> sink.Capture(HostRenderFault.render nodeId (HostCapabilityMismatch.describe mismatch))

/// A counting decorator over an inner sink: every `Capture` increments a
/// running fault count (readable via `.Count`) and forwards to `inner`, so
/// the client boot-degradation / health surface can surface a render-fault
/// spike rather than only logging each one (Phase 121 precedent). The
/// counter is a hot-path `mutable` — the SDK's documented exception for
/// write-only metric accumulation — justified here identically to
/// `IMetricsSink` counters. A host that never constructs this pays nothing
/// (GP 13); the inner sink's own semantics are unchanged.
type CountingHostRenderTelemetrySink(inner: IHostRenderTelemetrySink) =
    let mutable count = 0

    /// The number of faults captured since construction.
    member _.Count = count

    interface IHostRenderTelemetrySink with
        member _.Capture(fault: HostRenderFault) =
            count <- count + 1
            inner.Capture fault