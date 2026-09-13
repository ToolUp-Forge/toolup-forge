// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module internal ToolUp.AI.AIDiagnosticsWindow

open System

// ─── The `/dev/*` rolling-window arithmetic, in one place ────────────
//
// Four AI-tier diagnostics endpoints roll the same 60-minute window over
// an audit stream and report the same percentiles:
//
//   * `/dev/ai-fastpath`    (Phase 6j.A) — window + p50/p95/p99
//   * `/dev/ai-latency`     (Phase 6i.A) — window + p50/p95/p99 + optional
//   * `/dev/ai-allowlist`   (Phase 47)   — window only
//   * `/dev/ai-cross-module`(Phase 36.E) — window + p50/p95/p99
//
// Phase 47's outcome flagged the duplication as a rule-of-three candidate
// "once a third denial rollup appears". The third rollup is here, and the
// count had in fact already been reached on the arithmetic: `percentile`
// was byte-identical private code in two of those files and the window
// constant was a private triplicate across three. This module is that
// extraction, and the three existing sites now call it.
//
// **`internal`, deliberately.** Nothing outside this assembly reads it —
// the rollups are `/dev/*` handlers, not a consumer surface — and an
// exported helper would be public API to keep compatible for a shared
// four-line function.
//
// **Not shared with `/dev/auth-denials` (Phase 120), and that is not an
// oversight.** That rollup lives in `ToolUp.Platform.Server`, reads typed
// `AuditEvent`s through `IAuditLog` rather than raw stream rows, spells
// its window as an `int` minute count, and computes no percentiles at
// all. Hoisting four lines into `Platform.Core` to reach it would add
// public surface to the tier every consumer takes, to unify a constant
// with a different type and a function the other site does not call.

/// The rolling window every AI-tier `/dev/*` rollup reports over. One
/// declaration so an operator comparing two endpoints is comparing the
/// same slice of time — which was the reason each copy cited for picking
/// 60 minutes, and is the reason they must not drift apart.
let rollingWindow = TimeSpan.FromMinutes 60.0

/// Nearest-rank percentile over an unsorted sample. `0.0` on an empty
/// sample: these are operator dashboards, and a zero beside a zero count
/// reads correctly where a `None` or a raise would need handling at every
/// call site.
///
/// Nearest-rank (rather than an interpolating definition) is what every
/// copy of this function already computed, and the shape is load-bearing
/// for small samples: on three data points p99 is the largest one, which
/// is what an operator reading a tail-latency column expects to see.
let percentile (values: float[]) (p: float) : float =
    if values.Length = 0 then
        0.0
    else
        let sorted = values |> Array.sort
        let rank = int (Math.Ceiling(p * float sorted.Length)) - 1
        let clamped = max 0 (min (sorted.Length - 1) rank)
        sorted[clamped]

/// `percentile` over a partially-populated sample — `None` when no value
/// is present at all, rather than a `0.0` that would read as "fast".
let percentileOpt (values: float option[]) (p: float) : float option =
    let populated = values |> Array.choose id

    if populated.Length = 0 then
        None
    else
        Some(percentile populated p)