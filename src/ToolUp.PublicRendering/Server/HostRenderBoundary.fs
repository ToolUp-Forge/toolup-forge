// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open ToolUp.Platform

// ─── Phase 273 — SSR hosted-tree error-boundary + degraded fallback ────
//
// A node that throws while a hosted tree renders SERVER-side (the Phase 111
// server-rendered-fragment path) risks failing the whole request — one bad
// subtree 500s an entire SSR page (and voids its SEO first-paint). Phase 268
// gives client render-fault *telemetry* and Phase 203 checks hydration
// *parity*, but there was no server-side graceful degradation between them.
//
// `HostRenderBoundary` is the server-side peer of a client React error
// boundary: it wraps an opaque subtree render so a thrown node renders a
// **structured fallback fragment** instead of propagating — the surrounding
// page completes (no 500) — and the fault reports through the Phase 268
// `IHostRenderTelemetrySink` (so a degraded SSR render is observable, not
// silent). A healthy tree renders byte-identically to pre-273; a pipeline
// that never wraps a render pays nothing (GP 11 / GP 13).
//
// **Renderer-neutral by construction (GP 1).** The boundary takes a neutral
// fallback-fragment factory and an OPAQUE subtree renderer (`unit -> 'F`);
// no tree-language type appears. The SSR fragment representation is an HTML
// fragment STRING (the same shape the Phase 111 path emits and the Phase 203
// `HydrationParity` harness consumes), so `guard` specialises the generic
// core to `string`.
//
// **Parity-clean fallback (Phase 203).** `defaultFallback` is deterministic
// and purely structural — a stable `<div>` carrying the opaque node id as a
// data attribute and a fixed degraded-state message, with NO exception text
// (which would leak internals AND diverge run-to-run). A matching CSR
// fallback mount of the same structure therefore hydrates parity-clean; the
// exception detail rides the telemetry sink, not the served HTML.

[<RequireQualifiedAccess>]
module HostRenderBoundary =

    /// CSS class stamped on the default fallback fragment — the stable
    /// grep/style handle a deployment targets to style the degraded state.
    [<Literal>]
    let FallbackClass = "toolup-host-render-fallback"

    /// The fixed, deterministic degraded-state message the default fallback
    /// renders. No exception text (that rides the telemetry sink) so the
    /// fragment is byte-stable across renders and parity-clean vs a matching
    /// CSR fallback mount.
    [<Literal>]
    let FallbackMessage = "This content is temporarily unavailable."

    /// Minimal HTML-attribute/text escaping for the opaque node id (which
    /// the host owns and forge never interprets) so it is safe to embed in
    /// the fallback fragment's `data-node-id` attribute.
    let private escape (s: string) : string =
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")

    /// The neutral default fallback fragment for a faulted subtree: a stable,
    /// purely-structural `<div>` carrying the opaque node id and a fixed
    /// degraded-state message. Deterministic (no exception text), so the SSR
    /// fallback and a matching CSR fallback mount hydrate parity-clean
    /// (Phase 203). A consumer wanting a richer fallback passes its own
    /// factory to `guardWith`.
    let defaultFallback (fault: HostRenderFault) : string =
        "<div class=\""
        + FallbackClass
        + "\" data-node-id=\""
        + escape fault.NodeId
        + "\" role=\"note\">"
        + FallbackMessage
        + "</div>"

    /// Generic boundary: run `render`; on ANY thrown node, capture a
    /// `RenderFault` (keyed on `nodeId`, carrying the exception message) to
    /// `sink` and return `fallback fault` INSTEAD of propagating — so the
    /// surrounding page completes. A healthy render returns its result
    /// unchanged (the boundary is transparent on the success path, GP 11).
    /// Generic over the fragment type so the same core serves any renderer
    /// (the SSR string path via `guard`; a `XmlNode` / Feliz path via a
    /// consumer-supplied fallback).
    let guardWith
        (sink: IHostRenderTelemetrySink)
        (fallback: HostRenderFault -> 'Fragment)
        (nodeId: string)
        (render: unit -> 'Fragment)
        : 'Fragment =
        try
            render ()
        with ex ->
            let fault = HostRenderFault.render nodeId ex.Message
            sink.Capture fault
            fallback fault

    /// SSR string-fragment boundary with the neutral default fallback: wrap
    /// a hosted subtree render (`unit -> string`, the Phase 111 fragment
    /// shape) so a thrown node yields the structured `defaultFallback`
    /// fragment + a Phase 268 sink fault, and the page completes. The common
    /// case; use `guardWith` for a custom fallback fragment.
    let guard (sink: IHostRenderTelemetrySink) (nodeId: string) (render: unit -> string) : string =
        guardWith sink defaultFallback nodeId render